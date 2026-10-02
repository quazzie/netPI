using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace NetPI.Plan;

/// <summary>How a waiting <c>plan_submit</c> (or <c>plan_enter</c>) ended: approve | approve-new | revise | cancel | enter | decline | withdrawn | replaced.</summary>
internal sealed record PlanDecision(string Kind, string? Feedback = null, string? NewSessionId = null, string? IdeaId = null, bool Todos = false)
{
    public static readonly PlanDecision Withdrawn = new("withdrawn");
    public static readonly PlanDecision Replaced = new("replaced");
}

/// <summary>A tool call that waits for the user: its own id (never the model's call id, which two chats can share), the chat, the call.</summary>
internal sealed class Waiter
{
    public required string Id { get; init; }
    public required string SessionId { get; init; }
    public required string CallId { get; init; }
    public required string AgentId { get; init; }
    public string? Reason { get; init; }
    public DateTimeOffset AskedAt { get; } = DateTimeOffset.UtcNow;
    public TaskCompletionSource<PlanDecision> Decision { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary>
/// Plan mode's behaviour: entering and leaving, the plan a chat submits and what the user decides about it. The tools,
/// the RPC methods and the slash command are thin over this.
/// <para>
/// A decision made while the run still waits in <c>plan_submit</c> is handed to it (its tool result carries it on, with the
/// run's instance given back meanwhile). One made when the run is gone (NetPI restarted, the run was stopped) does the same
/// work and tells the chat with a message instead, so a plan never gets stuck on a card nobody waits on.
/// </para>
/// </summary>
internal sealed class PlanService(IPluginContext ctx, PlanStore store)
{
    public const string NoticeOn = "plan";
    public const string NoticeOff = "plan-off";

    private readonly ConcurrentDictionary<string, Waiter> _plans = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Waiter> _enters = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(1, 1);

    public const string OnText =
        "Plan mode is on: the user wants a plan before anything changes. You are read-only. write, edit, the shell (bash, pwsh, ssh, process) and the browser are " +
        "blocked, and so are MCP tools that change things. Explore with read, grep, find and ls, web_search and web_fetch, and MCP tools that only read. " +
        "Ask with ask_user when a decision is the user's. For broad research start subagents with agent_spawn on whichever agent suits the job " +
        "(agent_choices shows what each is for); they are read-only too. When you understand the task, submit the plan with plan_submit " +
        "(title, summary, steps, files, risks, tests, open questions) and wait: the user approves it, asks for changes (revise it and submit again) or cancels. " +
        "Do not start the work before it is approved. A good plan has concrete steps in order, the files each touches, and how it is verified.";

    public const string OffText =
        "Plan mode is off: you may change things again (write, edit, shell). If a plan was approved, carry it out step by step and keep the todo list current.";

    // ---------------------------------------------------------------- mode

    public SessionInfo Enter(string sessionId)
    {
        var session = ctx.Sessions.GetSession(sessionId) ?? throw new RpcException("not_found", $"Session {sessionId} not found");
        if (session.Kind == "subagent") throw new RpcException("bad_request", "A subagent's chat has no plan mode: start it in the chat that spawned it.");
        if (PlanMode.Active(session)) return session;
        session = PlanMode.Set(ctx.Sessions, sessionId, PlanMode.Planning);
        Changed(sessionId, mode: PlanMode.Planning);
        return session;
    }

    /// <summary>Leaves plan mode (the user's /plan off or the card's Cancel): the plan being decided, if any, is cancelled.</summary>
    public async Task<bool> ExitAsync(string sessionId)
    {
        var session = ctx.Sessions.GetSession(sessionId) ?? throw new RpcException("not_found", $"Session {sessionId} not found");
        if (!PlanMode.Active(session)) return false;
        await _gate.WaitAsync().ConfigureAwait(false);
        JsonObject? doc = null;
        try
        {
            if (PlanMode.PlanId(session) is { } id && store.Get(id) is { } d && d["status"]?.GetValue<string>() is PlanStore.Awaiting or PlanStore.Revising)
            {
                d["status"] = PlanStore.Cancelled;
                store.Put(d);
                doc = d;
            }
            PlanMode.Set(ctx.Sessions, sessionId, null);
            if (doc is not null && _plans.TryGetValue(doc["id"]!.GetValue<string>(), out var w)) w.Decision.TrySetResult(new PlanDecision("cancel"));
        }
        finally { _gate.Release(); }
        Changed(sessionId, doc, "off");
        return true;
    }

    public string Status(string sessionId)
    {
        var session = ctx.Sessions.GetSession(sessionId);
        if (session is null) return "No such chat.";
        var state = PlanMode.State(session);
        var doc = PlanMode.PlanId(session) is { } id ? store.Get(id) : null;
        var title = doc?["title"]?.GetValue<string>();
        return state switch
        {
            PlanMode.Planning => doc is { } d && d["revision"]!.GetValue<int>() > 0
                ? $"Plan mode: revising “{title}” (after revision {d["revision"]}). /plan off leaves it."
                : "Plan mode: exploring, no plan yet. /plan off leaves it.",
            PlanMode.Awaiting => $"Plan mode: “{title}” (revision {doc?["revision"]}) waits for your decision.",
            PlanMode.Approved => $"The plan “{title}” was approved; plan mode is over. /plan starts a new one.",
            _ => "Not in plan mode. /plan <task> starts one.",
        };
    }

    // ---------------------------------------------------------------- the model's tools

    /// <summary>
    /// <c>plan_submit</c>: stores the plan as the chat's next revision (the same plan again is not a new revision), then waits
    /// for the user's decision with the run's instance given back.
    /// </summary>
    public async Task<ToolResult> SubmitAsync(ToolContext context, PlanBody body, CancellationToken ct)
    {
        var runtime = context.Services.Get<IAgentRuntime>();
        if (runtime?.Get(context.AgentId)?.IsSubagent == true)
            return ToolResult.Error("Only the main agent submits a plan: put what you found in your report instead.");
        var session = ctx.Sessions.GetSession(context.SessionId);
        if (!PlanMode.Active(session))
            return ToolResult.Error("This chat is not in plan mode, so there is no plan to submit. The user starts one with /plan; you can offer it with plan_enter.");

        JsonObject doc;
        Waiter waiter;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            doc = PlanMode.PlanId(session) is { } id && store.Get(id) is { } open && open["status"]?.GetValue<string>() is PlanStore.Awaiting or PlanStore.Revising
                ? open
                : store.Create(session!);
            if (doc["status"]?.GetValue<string>() == PlanStore.Awaiting && doc["plan"] is not null && PlanStore.Body(doc).SameAs(body)) doc["callId"] = context.CallId;
            else store.Revise(doc, body, context.CallId);
            store.Put(doc);
            PlanMode.Set(ctx.Sessions, context.SessionId, PlanMode.Awaiting, doc["id"]!.GetValue<string>(), body.Title);
            var planId = doc["id"]!.GetValue<string>();
            waiter = new Waiter { Id = planId, SessionId = context.SessionId, CallId = context.CallId, AgentId = context.AgentId };
            if (_plans.TryGetValue(planId, out var older)) older.Decision.TrySetResult(PlanDecision.Replaced);
            _plans[planId] = waiter;
        }
        finally { _gate.Release(); }
        Changed(context.SessionId, doc, PlanMode.Awaiting, submitted: true);

        var status = "cancelled";
        try
        {
            var decided = runtime is null
                ? await WaitAsync(waiter.Decision.Task, ct).ConfigureAwait(false)
                : await runtime.WaitYieldedAsync(context.AgentId, waiter.Decision.Task, "waiting for your decision on the plan", null, ct).ConfigureAwait(false);
            if (!decided)
            {
                status = "steered";
                await ReopenAsync(doc["id"]!.GetValue<string>(), ct).ConfigureAwait(false);
                return Done("No decision: the user wrote a new message instead; it follows. Plan mode stays on: answer it, and call plan_submit again when the plan is ready (or changed).",
                    doc, status);
            }
            var decision = await waiter.Decision.Task.ConfigureAwait(false);
            status = decision.Kind switch { "approve" or "approve-new" => "approved", "revise" => "revised", "cancel" => "cancelled", var other => other };
            return Done(Render(doc, decision), doc, status, decision);
        }
        finally
        {
            _plans.TryRemove(new KeyValuePair<string, Waiter>(waiter.Id, waiter));
        }
    }

    /// <summary>The user wrote instead of deciding: the plan is being talked about, not decided, until the next plan_submit.</summary>
    private async Task ReopenAsync(string planId, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        JsonObject? doc;
        try
        {
            doc = store.Get(planId);
            if (doc is null || doc["status"]?.GetValue<string>() != PlanStore.Awaiting) return;
            doc["status"] = PlanStore.Revising;
            store.Put(doc);
            PlanMode.Set(ctx.Sessions, doc["sessionId"]!.GetValue<string>(), PlanMode.Planning);
        }
        finally { _gate.Release(); }
        Changed(doc["sessionId"]!.GetValue<string>(), doc, PlanMode.Planning);
    }

    private static string Render(JsonObject doc, PlanDecision d)
    {
        var title = doc["title"]?.GetValue<string>();
        var n = doc["revision"]!.GetValue<int>();
        return d.Kind switch
        {
            "approve" => $"The user approved your plan “{title}” (revision {n}). Plan mode is off: you can write, edit and run commands now. Carry the plan out step by step" +
                         (d.Todos ? "; the todo list holds its steps, keep it current with todo_write" : "") +
                         (d.IdeaId is not null ? $". It is saved as idea {d.IdeaId}." : ".") +
                         (d.Feedback is not null ? $"\nThey added: {d.Feedback}" : ""),
            "approve-new" => $"The user approved your plan “{title}” and moved it to a new chat ({d.NewSessionId}), which carries it out; this chat is archived. " +
                             "Do nothing more here: end your turn with one short sentence.",
            "revise" => $"The user asked for changes to revision {n}:\n{d.Feedback}\nRevise the plan and call plan_submit again (it becomes revision {n + 1}). Plan mode stays on.",
            "cancel" => "The user cancelled the plan: nothing was approved and plan mode is off. Stop planning; ask what they want instead if it is not clear.",
            "replaced" => "This plan was submitted again: the newer plan_submit call waits for the decision.",
            _ => "The plan tool restarted (an update), so this wait ended. Your plan still waits for the user: call plan_submit again with the same plan to keep waiting for the decision.",
        };
    }

    private static ToolResult Done(string text, JsonObject doc, string status, PlanDecision? d = null) => ToolResult.Ok(text, new JsonObject
    {
        ["kind"] = "plan",
        ["planId"] = doc["id"]?.DeepClone(),
        ["revision"] = doc["revision"]?.DeepClone(),
        ["title"] = doc["title"]?.DeepClone(),
        ["plan"] = doc["plan"]?.DeepClone(),
        ["status"] = status,
        ["feedback"] = d?.Feedback,
        ["ideaId"] = d?.IdeaId ?? doc["ideaId"]?.GetValue<string>(),
        ["newSessionId"] = d?.NewSessionId,
    });

    private static async Task<bool> WaitAsync(Task until, CancellationToken ct)
    {
        await until.WaitAsync(ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// <c>plan_enter</c>: the model proposes plan mode. The user confirms on a card (enter) or declines; entering is the
    /// same as /plan. Nothing changes without the answer.
    /// </summary>
    public async Task<ToolResult> OfferAsync(ToolContext context, string? reason, CancellationToken ct)
    {
        var runtime = context.Services.Get<IAgentRuntime>();
        if (runtime?.Get(context.AgentId)?.IsSubagent == true)
            return ToolResult.Error("Only the main agent can propose plan mode: nobody watches a subagent's chat.");
        if (PlanMode.Active(ctx.Sessions.GetSession(context.SessionId)))
            return ToolResult.Error("This chat is already in plan mode.");
        var w = new Waiter { Id = "planq_" + Ids.Short(12), SessionId = context.SessionId, CallId = context.CallId, AgentId = context.AgentId, Reason = reason };
        _enters[w.Id] = w;
        ctx.Events.Publish("plan.enter.asked", EnterJson(w));
        var status = "cancelled";
        try
        {
            var decided = runtime is null
                ? await WaitAsync(w.Decision.Task, ct).ConfigureAwait(false)
                : await runtime.WaitYieldedAsync(context.AgentId, w.Decision.Task, "waiting for your OK to plan", null, ct).ConfigureAwait(false);
            if (!decided)
            {
                status = "steered";
                return ToolResult.Ok("No answer: the user wrote a new message instead; it follows.", EnterDetails(w, status));
            }
            var d = await w.Decision.Task.ConfigureAwait(false);
            if (d.Kind == "enter")
            {
                status = "entered";
                return ToolResult.Ok(OnText, EnterDetails(w, status));
            }
            status = d.Kind == "decline" ? "declined" : "withdrawn";
            return ToolResult.Ok(d.Kind == "decline"
                ? "The user declined plan mode: go ahead without a plan (or ask what they prefer)."
                : "No answer: the plan plugin restarted. Ask again if you still want plan mode.", EnterDetails(w, status));
        }
        finally
        {
            if (_enters.TryRemove(new KeyValuePair<string, Waiter>(w.Id, w)))
                ctx.Events.Publish("plan.enter.closed", new JsonObject { ["id"] = w.Id, ["sessionId"] = w.SessionId, ["callId"] = w.CallId, ["status"] = status });
        }
    }

    public bool AnswerEnter(string requestId, bool enter)
    {
        if (!_enters.TryGetValue(requestId, out var w)) throw new RpcException("not_found", "No plan-mode offer waits with that id (it was answered, or its run ended).");
        // plan mode is on before the run goes on, so its next tool call is already checked
        if (enter) Enter(w.SessionId);
        if (!w.Decision.TrySetResult(new PlanDecision(enter ? "enter" : "decline"))) throw new RpcException("not_found", "The offer was already answered.");
        return true;
    }

    public JsonArray Offers(string? sessionId) =>
        new([.. _enters.Values.Where(w => sessionId is null || w.SessionId == sessionId).OrderBy(w => w.AskedAt).Select(w => (JsonNode)EnterJson(w))]);

    private static JsonObject EnterJson(Waiter w) => new()
    {
        ["id"] = w.Id, ["sessionId"] = w.SessionId, ["callId"] = w.CallId, ["agentId"] = w.AgentId, ["reason"] = w.Reason, ["askedAt"] = w.AskedAt.ToString("O"),
    };

    private static JsonObject EnterDetails(Waiter w, string status) => new() { ["kind"] = "plan-enter", ["reason"] = w.Reason, ["status"] = status };

    // ---------------------------------------------------------------- the user's decision

    /// <summary>
    /// <c>plan.answer</c>: approve (optionally in a new chat, optionally with a note), revise (feedback), save (as an idea, the plan
    /// goes on waiting), file (to docs/plans, the same) or cancel.
    /// </summary>
    public async Task<JsonObject> AnswerAsync(string planId, string decision, string? feedback, bool newChat, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var doc = store.Get(planId) ?? throw new RpcException("not_found", $"No plan {planId}.");
            var sessionId = doc["sessionId"]!.GetValue<string>();
            var status = doc["status"]!.GetValue<string>();
            var result = new JsonObject { ["planId"] = planId };
            switch (decision)
            {
                case "save":
                    {
                        var idea = await SaveIdeaAsync(doc, "open", ct).ConfigureAwait(false);
                        store.Put(doc);
                        result["ideaId"] = idea;
                        break;
                    }
                case "file":
                    {
                        if (doc["plan"] is null) throw new RpcException("bad_request", "This plan has no content yet.");
                        result["path"] = SaveFile(doc);
                        store.Put(doc);
                        break;
                    }
                case "approve":
                    {
                        Require(status, PlanStore.Awaiting, "approved");
                        var session = ctx.Sessions.GetSession(sessionId) ?? throw new RpcException("not_found", $"Session {sessionId} not found");
                        var body = PlanStore.Body(doc);
                        var ideaId = await SaveIdeaAsync(doc, "planned", ct).ConfigureAwait(false);
                        store.Put(doc); // the idea is remembered even if what follows fails: a retry follows it instead of saving another
                        string? newId = null;
                        var todos = false;
                        if (newChat) newId = await CreateReplacementAsync(session, doc, body, ideaId, ct).ConfigureAwait(false);
                        else todos = ctx.Settings.Get("plan.seedTodos", true) && SeedTodos(sessionId, body);
                        doc["status"] = PlanStore.Approved;
                        if (newId is not null) doc["newSessionId"] = newId;
                        store.Put(doc);
                        PlanMode.Set(ctx.Sessions, sessionId, PlanMode.Approved, planId, body.Title, newId);
                        if (newId is not null) ctx.Sessions.UpdateSession(sessionId, s => s.Archived = true);
                        var d = new PlanDecision(newChat ? "approve-new" : "approve", feedback?.Trim() is { Length: > 0 } f ? f : null, newId, ideaId, todos);
                        await DeliverAsync(doc, d, ct).ConfigureAwait(false);
                        result["ideaId"] = ideaId;
                        result["newSessionId"] = newId;
                        result["todos"] = todos;
                        break;
                    }
                case "revise":
                    {
                        Require(status, PlanStore.Awaiting, "revised");
                        var text = feedback?.Trim();
                        if (string.IsNullOrEmpty(text)) throw new RpcException("bad_request", "Say what to change: a revision needs feedback.");
                        PlanStore.NoteFeedback(doc, text);
                        doc["status"] = PlanStore.Revising;
                        store.Put(doc);
                        PlanMode.Set(ctx.Sessions, sessionId, PlanMode.Planning);
                        await DeliverAsync(doc, new PlanDecision("revise", text), ct).ConfigureAwait(false);
                        break;
                    }
                case "cancel":
                    {
                        if (status is not (PlanStore.Awaiting or PlanStore.Revising)) throw new RpcException("bad_request", $"This plan is {status}: nothing to cancel.");
                        doc["status"] = PlanStore.Cancelled;
                        store.Put(doc);
                        PlanMode.Set(ctx.Sessions, sessionId, null);
                        await DeliverAsync(doc, new PlanDecision("cancel"), ct).ConfigureAwait(false);
                        break;
                    }
                default:
                    throw new RpcException("bad_request", "decision is approve, revise, save, file or cancel.");
            }
            result["status"] = doc["status"]!.GetValue<string>();
            Changed(sessionId, doc, ctx.Sessions.GetSession(sessionId) is { } s2 ? PlanMode.State(s2) ?? "off" : "off");
            return result;
        }
        finally { _gate.Release(); }
    }

    private static void Require(string status, string wanted, string verb)
    {
        if (status != wanted) throw new RpcException("bad_request", $"This plan is {status}, so it cannot be {verb} now" + (status == PlanStore.Revising ? " (a new revision is being written)." : "."));
    }

    /// <summary>Hands the decision to the run that waits for it, or, when none does, tells the chat with a message.</summary>
    private async Task DeliverAsync(JsonObject doc, PlanDecision d, CancellationToken ct)
    {
        var planId = doc["id"]!.GetValue<string>();
        if (_plans.TryGetValue(planId, out var w) && w.Decision.TrySetResult(d)) return;
        var runtime = ctx.Services.Get<IAgentRuntime>();
        if (runtime is null) return;
        var text = d.Kind switch
        {
            "approve" => Render(doc, d),
            "revise" => $"Please revise the plan:\n{d.Feedback}\nThen submit it again with plan_submit.",
            "cancel" => "I cancelled the plan: plan mode is off.",
            _ => null, // approved in a new chat: that chat carries it out, this one is archived
        };
        if (text is null) return;
        await runtime.SendAsync(doc["sessionId"]!.GetValue<string>(), new UserInput { Text = text }, DeliveryMode.Auto, ct).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------- what an approval does

    private async Task<string?> SaveIdeaAsync(JsonObject doc, string ideaStatus, CancellationToken ct)
    {
        if (!ctx.Rpc.Exists("ideas.add")) return doc["ideaId"]?.GetValue<string>(); // no backlog to save to: the plan still stands
        if (doc["plan"] is null) throw new RpcException("bad_request", "This plan has no content yet.");
        var body = PlanStore.Body(doc);
        var revision = doc["revision"]!.GetValue<int>();
        var markdown = body.Markdown();
        var summary = body.Summary.Length > 0 ? body.Summary : body.Steps[0].Text;
        var ideaId = doc["ideaId"]?.GetValue<string>();
        try
        {
            if (ideaId is not null)
            {
                // saved before (an earlier revision, or as a draft): the same idea follows the plan instead of a second one
                if (doc["ideaRevision"]?.GetValue<int>() != revision || ideaStatus == "planned")
                {
                    var patch = new JsonObject { ["title"] = body.Title, ["summary"] = Clip(summary, 600) };
                    if (ideaStatus == "planned") patch["status"] = "planned";
                    if (doc["ideaSectionId"]?.GetValue<string>() is { } sectionId)
                        patch["updateSections"] = new JsonArray(new JsonObject { ["id"] = sectionId, ["content"] = markdown });
                    await ctx.Rpc.InvokeAsync("ideas.update", new JsonObject { ["id"] = ideaId, ["patch"] = patch }, ct).ConfigureAwait(false);
                    doc["ideaRevision"] = revision;
                }
                return ideaId;
            }
            var added = NetPiJson.ToNode(await ctx.Rpc.InvokeAsync("ideas.add", new JsonObject
            {
                ["sessionId"] = doc["sessionId"]!.GetValue<string>(),
                ["idea"] = new JsonObject
                {
                    ["title"] = body.Title,
                    ["summary"] = Clip(summary, 600),
                    ["status"] = ideaStatus,
                    ["tags"] = new JsonArray("plan"),
                    ["sections"] = new JsonArray(new JsonObject { ["kind"] = "plan", ["title"] = "Plan", ["content"] = markdown }),
                },
            }, ct).ConfigureAwait(false));
            ideaId = added?["id"]?.GetValue<string>() ?? throw new RpcException("internal", "The ideas plugin saved the plan but returned no id.");
            doc["ideaId"] = ideaId;
            doc["ideaSectionId"] = (added["sections"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault()?["id"]?.GetValue<string>();
            doc["ideaRevision"] = revision;
            return ideaId;
        }
        catch (RpcException) { throw; }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new RpcException("internal", $"The plan could not be saved as an idea: {ex.Message}");
        }
    }

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    /// <summary>The plan's steps as the chat's checklist, unless it has open items of its own (those are not replaced).</summary>
    private bool SeedTodos(string sessionId, PlanBody body)
    {
        var seeded = false;
        ctx.Sessions.UpdateSession(sessionId, s =>
        {
            s.Meta ??= new JsonObject();
            if (s.Meta["todo"] is JsonArray existing && existing.OfType<JsonObject>().Any(i => i["status"]?.GetValue<string>() != "done")) return;
            s.Meta["todo"] = new JsonArray([.. body.Steps.Take(50).Select(st => (JsonNode)new JsonObject
            {
                ["text"] = Clip(st.Text, 300), ["status"] = "pending",
            })]);
            seeded = true;
        });
        return seeded;
    }

    /// <summary>The plan as <c>docs/plans/&lt;date&gt;-&lt;slug&gt;.md</c> in the chat's workspace (written only when the user asks); the path, relative to it.</summary>
    private string SaveFile(JsonObject doc)
    {
        var session = ctx.Sessions.GetSession(doc["sessionId"]!.GetValue<string>()) ?? throw new RpcException("not_found", "The plan's chat is gone.");
        string cwd;
        try { cwd = ctx.Services.Get<IWorkspaceResolver>()?.CwdOf(session) ?? ctx.Sessions.GetCwd(session); }
        catch (Exception ex) when (ex is not OperationCanceledException) { throw new RpcException("bad_request", $"The chat's workspace cannot be used: {ex.Message}"); }
        var body = PlanStore.Body(doc);
        var slug = Slug(body.Title);
        var dir = Path.Combine(cwd, "docs", "plans");
        Directory.CreateDirectory(dir);
        var date = DateTime.Now.ToString("yyyy-MM-dd");
        // saved before: the same file is written again (the plan may have been revised); otherwise a name that is free
        var path = doc["filePath"]?.GetValue<string>() is { Length: > 0 } saved ? Path.Combine(cwd, saved) : Path.Combine(dir, $"{date}-{slug}.md");
        for (var i = 2; doc["filePath"] is null && File.Exists(path); i++) path = Path.Combine(dir, $"{date}-{slug}-{i}.md");
        File.WriteAllText(path, $"# {body.Title}\n\n{body.Markdown()}", new UTF8Encoding(false));
        var relative = Path.GetRelativePath(cwd, path).Replace('\\', '/');
        doc["filePath"] = relative;
        return relative;
    }

    internal static string Slug(string title)
    {
        var slug = Regex.Replace(title.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        if (slug.Length > 48) slug = slug[..48].TrimEnd('-');
        return slug.Length == 0 ? "plan" : slug;
    }

    /// <summary>
    /// A new chat of the same project, workspace and agent that carries the plan out: the plan (and its idea) as its first message,
    /// before the plan chat is archived. A failure here leaves the plan chat as it was.
    /// </summary>
    private async Task<string> CreateReplacementAsync(SessionInfo from, JsonObject doc, PlanBody body, string? ideaId, CancellationToken ct)
    {
        var created = ctx.Sessions.CreateSession(new SessionInfo
        {
            Title = Clip("Plan: " + body.Title, 80),
            ProjectId = from.ProjectId,
            Model = from.Model,
            Reasoning = from.Reasoning,
            Meta = new JsonObject { [PlanMode.FromKey] = from.Id },
        });
        try
        {
            if (SessionWorkspace.Of(from) is { Length: > 0 } workspace && ctx.Rpc.Exists("sessions.setWorkspace"))
                await ctx.Rpc.InvokeAsync("sessions.setWorkspace", new JsonObject { ["id"] = created.Id, ["workspaceId"] = workspace }, ct).ConfigureAwait(false);
            if (SessionAgent.Of(from) is { Length: > 0 } agent && ctx.Rpc.Exists("agents.use"))
                await ctx.Rpc.InvokeAsync("agents.use", new JsonObject { ["sessionId"] = created.Id, ["agent"] = agent }, ct).ConfigureAwait(false);
            if (ideaId is not null && ctx.Rpc.Exists("ideas.attach"))
                await ctx.Rpc.InvokeAsync("ideas.attach", new JsonObject { ["sessionId"] = created.Id, ["id"] = ideaId }, ct).ConfigureAwait(false);
            var runtime = ctx.Services.Get<IAgentRuntime>() ?? throw new RpcException("unavailable", "The agent runtime is not running.");
            var text = $"Carry out this approved plan.\n\n# {body.Title}\n\n{body.Markdown()}\n" +
                       (ideaId is not null ? $"It is saved as idea {ideaId}. " : "") +
                       "Work through the steps in order and keep a todo list; if the plan turns out wrong, say so before you deviate from it.";
            await runtime.SendAsync(created.Id, new UserInput { Text = text }, DeliveryMode.Auto, ct).ConfigureAwait(false);
            return created.Id;
        }
        catch
        {
            try { ctx.Sessions.DeleteSession(created.Id); } catch (Exception ex) { ctx.Logger.LogWarning(ex, "Plan: the new chat {Session} could not be removed after a failure", created.Id); }
            throw;
        }
    }

    // ---------------------------------------------------------------- events, reads

    /// <summary>plan.changed (unscoped, so a window without the chat open hears of it too): the chat's mode and, when there is one, its plan.</summary>
    public void Changed(string sessionId, JsonObject? doc = null, string? mode = null, bool submitted = false)
    {
        ctx.Events.Publish("plan.changed", new JsonObject
        {
            ["sessionId"] = sessionId,
            ["mode"] = mode,
            ["planId"] = doc?["id"]?.GetValue<string>(),
            ["callId"] = doc?["callId"]?.GetValue<string>(),
            ["status"] = doc?["status"]?.GetValue<string>(),
            ["revision"] = doc?["revision"]?.GetValue<int>(),
            ["title"] = doc?["title"]?.GetValue<string>(),
            ["submitted"] = submitted, // a plan_submit just stored it: the user is wanted
        });
    }

    /// <summary>A waiting wait ends as withdrawn when this plugin stops, so its run goes on (with its slot back) instead of hanging.</summary>
    public void WithdrawAll()
    {
        foreach (var w in _plans.Values) w.Decision.TrySetResult(PlanDecision.Withdrawn);
        foreach (var w in _enters.Values) w.Decision.TrySetResult(PlanDecision.Withdrawn);
    }

    public bool Waits(string planId) => _plans.ContainsKey(planId);
}
