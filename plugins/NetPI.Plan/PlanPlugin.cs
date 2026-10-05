using System.Text.Json.Nodes;

namespace NetPI.Plan;

/// <summary>
/// Plan mode (<c>/plan</c>): a chat that may only read. The agent explores (read, grep, find, ls, the web, MCP tools that read,
/// read-only research subagents on any agent), asks with <c>ask_user</c>, and submits a structured plan with
/// <c>plan_submit</c>; the run waits, with its instance given back, while the user approves it (saved as an idea, optionally in
/// a new chat that takes the chat's place), asks for changes, saves it as an idea or a file, or cancels. The model can offer
/// the mode itself with <c>plan_enter</c> (the user confirms on a card).
/// <para>
/// State: <c>meta.planMode</c> on the chat (<see cref="PlanMode"/>), the plans in this plugin's data (<see cref="PlanStore"/>).
/// RPC: plan.enter, plan.exit, plan.get, plan.list, plan.answer, plan.offers, plan.enterAnswer, plan.command (the slash command).
/// Events (unscoped): plan.changed, plan.enter.asked, plan.enter.closed. The gate is <see cref="PlanHook"/> over <see cref="PlanPolicy"/>.
/// </para>
/// </summary>
[NetPiPlugin("netpi.plan", Name = "Plan mode", Description = "/plan: explore read-only, submit a plan, the user approves it, asks for changes or saves it", Order = 67)]
public sealed class PlanPlugin : INetPiPlugin
{
    private PlanService? _service;

    public Task StartAsync(IPluginContext context, CancellationToken ct)
    {
        // Run state of this plugin: a fork starts without it (the session service remembers the keys even when this plugin is not loaded at a fork).
        context.Sessions.DeclareForkReset(PlanMode.MetaKey, PlanMode.FromKey);
        var store = new PlanStore(context);
        var service = _service = new PlanService(context, store);

        context.Tools.Register(new PlanSubmitTool(service));
        context.Tools.Register(new PlanEnterTool(service));
        context.Services.Register<IAgentHook>(new PlanHook(context));
        context.Services.Register(new SettingsSection
        {
            Id = "plan", Title = "Plan mode", Group = "Agents", Order = 34,
            Settings =
            [
                SettingInfo.List("plan.mcpAllow", "MCP tools allowed in plan mode", [],
                    "MCP tools that read, beyond the ones their server declares read-only. Names with * as wildcard, matched against the tool's own name or server/name: ha_get_*, ha_search, ha_list_*. A tool that changes things stays blocked.",
                    "ha_get_*"),
                SettingInfo.Bool("plan.subagentsReadOnly", "Research subagents are read-only", true,
                    "Subagents a plan-mode chat starts get only tools that read, no checkout of their own, and are bound by plan mode too."),
                SettingInfo.Bool("plan.seedTodos", "Approved plan fills the todo list", true,
                    "Approving a plan in the same chat puts its steps in the chat's todo list (unless the list has open items)."),
            ],
        });

        context.Rpc.Register("plan.enter", (req, _) => Task.FromResult<object?>(new JsonObject { ["mode"] = PlanMode.State(service.Enter(req.Required("sessionId"))) }),
            "Put a chat in plan mode: { sessionId } → { mode }");
        context.Rpc.Register("plan.exit", async (req, _) => await service.ExitAsync(req.Required("sessionId")).ConfigureAwait(false),
            "Leave plan mode (a plan being decided is cancelled): { sessionId } → bool (false: it was not in plan mode)");
        context.Rpc.RegisterReadOnly("plan.get", (req, _) => Task.FromResult<object?>(Get(context, store, req)),
            "A plan with its revisions and markdown: { planId } or { sessionId } (the chat's current, else latest plan) → plan | null");
        context.Rpc.RegisterReadOnly("plan.list", (req, _) =>
        {
            var plans = req.Str("sessionId") is { } sid ? store.ForSession(sid, req.Str("status")) : store.WithStatus(req.Str("status") is { } s ? [s] : [PlanStore.Awaiting, PlanStore.Revising]);
            return Task.FromResult<object?>(new JsonArray([.. plans.Select(p =>
            {
                var view = PlanStore.View(p, revisions: false);
                view["waiting"] = service.Waits(p["id"]!.GetValue<string>());
                return (JsonNode)view;
            })]));
        }, "Plans: { sessionId?, status? } → plan[] (without revision bodies; waiting: a run waits for the decision). Without a chat: the ones awaiting or being revised");
        context.Rpc.Register("plan.answer", async (req, token) => await service.AnswerAsync(req.Required("planId"), req.Required("decision"), req.Str("feedback"), req.Bool("newChat") == true, token).ConfigureAwait(false),
            "Decide a plan: { planId, decision: approve | revise | save | file | cancel, feedback? (required to revise, a note on approve), newChat? (approve: carry it out in a new chat that replaces this one) } → { planId, status, ideaId?, newSessionId?, path?, todos? }");
        context.Rpc.RegisterReadOnly("plan.offers", (req, _) => Task.FromResult<object?>(service.Offers(req.Str("sessionId"))),
            "Plan-mode offers waiting for the user (plan_enter): { sessionId? } → { id, sessionId, callId, reason, askedAt }[]");
        context.Rpc.Register("plan.enterAnswer", (req, _) => Task.FromResult<object?>(service.AnswerEnter(req.Required("id"), req.Bool("enter") == true)),
            "Answer a plan-mode offer: { id, enter } → true");
        context.Rpc.Register("plan.command", async (req, token) => await Command(context, service, req.Required("sessionId"), req.Str("args")?.Trim() ?? "", token).ConfigureAwait(false),
            "/plan command: { sessionId, args } → status text");
        context.Ui.AddCommand(new SlashCommandInfo
        {
            Name = "plan", Description = "Plan mode: explore read-only and submit a plan to approve (/plan off leaves it)",
            Rpc = "plan.command", ArgsHint = "[task] | off | show",
        });

        // the host publishes { id } as the payload (no session on the envelope), like sessions.delete does
        context.Events.Subscribe(EventTypes.SessionDeleted, e =>
        {
            if ((e.As<JsonObject>()?["id"]?.GetValue<string>() ?? e.SessionId) is { Length: > 0 } id) store.DeleteSession(id);
        });
        return Task.CompletedTask;
    }

    /// <summary>A waiting plan or offer ends as withdrawn, so its run goes on (with its instance back) instead of hanging.</summary>
    public Task StopAsync(CancellationToken ct)
    {
        _service?.WithdrawAll();
        return Task.CompletedTask;
    }

    private static JsonNode? Get(IPluginContext context, PlanStore store, RpcRequest req)
    {
        JsonObject? doc = null;
        if (req.Str("planId") is { } id) doc = store.Get(id);
        else if (req.Str("sessionId") is { } sid)
        {
            doc = PlanMode.PlanId(context.Sessions.GetSession(sid)) is { } current ? store.Get(current) : null;
            doc ??= store.ForSession(sid).FirstOrDefault();
        }
        else throw new RpcException("bad_request", "Pass planId or sessionId.");
        return doc is null ? null : PlanStore.View(doc, revisions: true);
    }

    private static async Task<object?> Command(IPluginContext context, PlanService service, string sessionId, string args, CancellationToken ct)
    {
        switch (args.ToLowerInvariant())
        {
            case "off" or "exit" or "cancel":
                return await service.ExitAsync(sessionId).ConfigureAwait(false) ? "Plan mode is off." : "This chat is not in plan mode.";
            case "show" or "status":
                return service.Status(sessionId);
            case "":
                if (PlanMode.Active(context.Sessions.GetSession(sessionId))) return service.Status(sessionId);
                service.Enter(sessionId);
                return "Plan mode is on: describe the task, the agent explores and submits a plan.";
        }
        service.Enter(sessionId);
        var runtime = context.Services.Get<IAgentRuntime>() ?? throw new RpcException("unavailable", "The agent runtime is not running.");
        await runtime.SendAsync(sessionId, new UserInput { Text = args }, DeliveryMode.Auto, ct).ConfigureAwait(false);
        return "Plan mode is on.";
    }
}
