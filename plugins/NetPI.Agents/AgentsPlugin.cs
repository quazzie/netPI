using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace NetPI.Agents;

/// <summary>
/// Agents (<see cref="IAgentScheduler"/>), the ledger of every model call with its cost, and the budget.
/// <para>Settings: <c>agents.&lt;id&gt;</c> <c>{ model, instances, use, disabled, budget: { limitUsd }, cost: { input, output } }</c>,
/// <c>budget.*</c>; still read: <c>models.localSlots</c> (1) and <c>models.cloudSlots</c> (4) for model calls
/// without an agent, <c>budget.providers.&lt;provider&gt;.dailyTokens</c>. The lanes of earlier versions become agents on the first
/// start (<see cref="AgentUpgrade"/>).</para>
/// <para>RPC: <c>agents.list</c>, <c>agents.use</c>, <c>agents.setEnabled</c>, <c>usage.summary</c>, <c>usage.session</c>,
/// <c>budget.status</c>, <c>budget.allow</c>. Events: <c>agents.changed { agents }</c>, <c>usage.changed</c> (the budget
/// status). For agents that delegate: the <c>agent_choices</c> tool and an "Agents" system prompt section.</para>
/// </summary>
[NetPiPlugin("netpi.agents", Name = "Agents", Description = "The agents chats and subagents run on (a model with instances, active while its model is loaded), the cost of every model call and the budget", Order = 30)]
public sealed class AgentsPlugin : INetPiPlugin
{
    private AgentScheduler? _scheduler;

    internal AgentScheduler? Scheduler => _scheduler;

    public Task StartAsync(IPluginContext context, CancellationToken ct)
    {
        context.Services.Register(new SettingsSection
        {
            Id = "budget", Title = "Budget", Group = "Models", Order = 20,
            Settings =
            [
                SettingInfo.Number("budget.monthlyUsd", "Monthly budget", null, "Spend on paid models per month; empty = no limit. Free and local models don't count.", 0, null, "$"),
                SettingInfo.Number("budget.dailyUsd", "Daily budget", null, "Empty = no daily limit.", 0, null, "$"),
                SettingInfo.Int("budget.resetDay", "The month starts on day", 1, null, 1, 28),
                SettingInfo.Int("budget.warnPercent", "Warn at", 80, "From here agents use paid agents only when you asked.", 1, 100, "%"),
                SettingInfo.Choice("budget.onLimit", "When the budget is spent", "stop", ["stop", "ask"], "stop: paid calls stop. ask: your chats stop with \"let this chat go over\"; subagents stop."),
            ],
        });
        // the agents themselves (agents.<id>) have their own editor in the settings (Agents & budget)
        try
        {
            var upgraded = AgentUpgrade.Run(context.Settings);
            if (upgraded.Count > 0) context.Logger.LogInformation("Lanes became agents: {Agents}", string.Join(", ", upgraded));
        }
        catch (Exception ex) { context.Logger.LogWarning(ex, "Turning the lanes into agents failed"); }

        var usage = new Ledger(context);
        usage.Initialize();
        var scheduler = new AgentScheduler(context, usage);
        _scheduler = scheduler;
        scheduler.Refresh();

        context.Services.Register<IAgentScheduler>(scheduler);
        context.Services.Register<IModelMiddleware>(new LedgerMiddleware(usage, scheduler));
        context.Tools.Register(new AgentChoicesTool(scheduler, usage));
        context.Services.Register<IPromptSection>(new AgentsPromptSection());

        context.Rpc.Register("agents.list", (_, _) => Task.FromResult<object?>(scheduler.Snapshot()),
            "The agents (always) and other model calls in progress, with instances, owners, waiters, state, price and today's spend → AgentSlots[]");
        context.Rpc.Register("agents.use", (r, _) =>
        {
            var sid = r.Required("sessionId");
            if (context.Sessions.GetSession(sid) is null) throw new RpcException("not_found", $"Session {sid} not found");
            var id = r.Str("agent");
            if (string.IsNullOrWhiteSpace(id))
                return Task.FromResult<object?>(context.Sessions.UpdateSession(sid, s => s.Meta?.Remove(SessionAgent.MetaKey)));
            var agent = scheduler.Agent(id) ?? throw new RpcException("not_found", $"There is no agent \"{id}\"");
            return Task.FromResult<object?>(context.Sessions.UpdateSession(sid, s =>
            {
                s.Meta ??= new JsonObject();
                s.Meta[SessionAgent.MetaKey] = agent.Id;
                s.Model = agent.Model;
            }));
        }, "Run a chat on an agent: { sessionId, agent } → SessionInfo (meta.agent, and the agent's model); agent null: none");
        context.Rpc.Register("agents.setEnabled", (r, _) =>
        {
            var id = r.Required("id");
            var agent = scheduler.Agent(id) ?? throw new RpcException("not_found", $"There is no agent \"{id}\"");
            var enabled = r.Bool("enabled") ?? throw new RpcException("bad_request", "enabled (true or false) is required");
            context.Settings.Set($"agents.{agent.Id}.disabled", enabled ? null : JsonValue.Create(true));
            scheduler.Refresh();
            return Task.FromResult<object?>(scheduler.Snapshot());
        }, "Switch an agent on or off: { id, enabled } → AgentSlots[] (agents.<id>.disabled; runs on it finish, new ones are refused)");
        context.Rpc.Register("usage.summary", (_, _) => Task.FromResult<object?>(usage.Summary()),
            "Today's tokens per provider, the budget, this period's calls per model → { day, providers, budget, models }");
        context.Rpc.Register("usage.session", (r, _) => Task.FromResult<object?>(usage.SessionCost(r.Required("sessionId"))),
            "What a chat cost: { sessionId } → { costUsd, calls, withSubagentsUsd, withSubagentsCalls }");
        context.Rpc.Register("budget.status", (_, _) => Task.FromResult<object?>(usage.BudgetStatus()),
            "The budget: { monthlyUsd, dailyUsd, warnPercent, resetDay, onLimit, periodStart, periodEnd, spentUsd, todayUsd, warning, exhausted }");
        context.Rpc.Register("budget.allow", async (r, token) =>
        {
            var sid = r.Required("sessionId");
            if (context.Sessions.GetSession(sid) is null) throw new RpcException("not_found", $"Session {sid} not found");
            usage.Allow(sid);
            // continue the chat that stopped at the budget
            var rt = context.Services.Get<IAgentRuntime>();
            if (rt is not null && rt.GetBySession(sid) is not { Status: AgentStatus.Running or AgentStatus.Queued or AgentStatus.Yielded })
                await rt.SendAsync(sid, new UserInput
                {
                    Text = "The user let this chat go over the budget until the budget period ends. Continue where you stopped.",
                    AsNotice = true,
                    NoticeKind = "budget",
                    Source = "system",
                }, DeliveryMode.Auto, token).ConfigureAwait(false);
            return usage.BudgetStatus();
        }, "Let a chat go over the budget until the period ends and continue it: { sessionId } (budget.onLimit \"ask\")");

        context.Events.Subscribe(EventTypes.SettingsChanged, _ => scheduler.Refresh());
        // model states (loaded, unloaded, offline) switch local agents on and off
        context.Events.Subscribe(EventTypes.ModelsChanged, _ => scheduler.Refresh());

        // Agents follow the model catalog: list it soon after startup so their states are known.
        _ = Task.Run(async () =>
        {
            try
            {
                await context.Models.ListAsync(false, context.Stopping).ConfigureAwait(false);
                scheduler.Refresh();
                var pools = scheduler.Snapshot();
                if (pools.Count > 0)
                    context.Logger.LogInformation("Agents: {Agents}", string.Join(", ", pools.Select(p => $"{p.Key} {p.Busy}/{p.Capacity}{(p.Available ? "" : " (" + (p.Disabled ? "off" : p.Unavailable) + ")")}")));
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { context.Logger.LogDebug(ex, "Initial model listing for the agents failed"); }
        }, CancellationToken.None);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        _scheduler?.Stop();
        return Task.CompletedTask;
    }
}
