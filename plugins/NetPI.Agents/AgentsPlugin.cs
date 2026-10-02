using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace NetPI.Agents;

/// <summary>
/// Agents (<see cref="IAgentScheduler"/>), the ledger of every model call with its cost, and the budget.
/// <para>Settings: <c>agents.&lt;id&gt;</c> <c>{ model, instances, use, disabled, budget: { limitUsd }, cost: { input, output } }</c>,
/// <c>budget.*</c>; <c>models.localSlots</c> (1) and <c>models.cloudSlots</c> (4) for model calls
/// without an agent; still read: <c>budget.providers.&lt;provider&gt;.dailyTokens</c>. The lanes of earlier versions become agents on the first
/// start (<see cref="AgentUpgrade"/>).</para>
/// <para>RPC: <c>agents.list</c>, <c>agents.use</c>, <c>agents.setEnabled</c>, <c>usage.summary</c>, <c>usage.session</c>,
/// <c>budget.status</c>, <c>budget.allow</c>. Events: <c>agents.changed { agents }</c>, <c>usage.changed</c> (the budget
/// status). For agents that delegate: the <c>agent_choices</c> tool and an "Agents" system prompt section.</para>
/// </summary>
[NetPiPlugin("netpi.agents", Name = "Agents", Description = "The agents chats and subagents run on (a model with instances, active while its model is loaded), the cost of every model call and the budget", Order = 30)]
public sealed class AgentsPlugin : INetPiPlugin
{
    private AgentScheduler? _scheduler;
    private Ledger? _usage;

    internal AgentScheduler? Scheduler => _scheduler;

    public Task StartAsync(IPluginContext context, CancellationToken ct)
    {
        context.Services.Register(new SettingsSection
        {
            Id = "modelSlots", Title = "Model slots", Group = "Models", Order = 10,
            Settings =
            [
                SettingInfo.Int(ModelCapacity.LocalSetting, "Local model slots", ModelCapacity.DefaultLocal,
                    "Concurrent runs of one local model without a catalog concurrency; agents on the model share them."),
                SettingInfo.Int(ModelCapacity.CloudSetting, "Cloud provider slots", ModelCapacity.DefaultCloud,
                    "Concurrent runs across one cloud provider's models."),
            ],
        });
        context.Services.Register(new SettingsSection
        {
            Id = "budget", Title = "Budget", Group = "Models", Order = 20,
            Settings =
            [
                SettingInfo.Number("budget.monthlyUsd", "Monthly budget", null, "Spend plus reservations for paid model calls per month; empty = no limit. Capped cloud models need known prices. Free and local models don't count.", 0, null, "$"),
                SettingInfo.Number("budget.dailyUsd", "Daily budget", null, "Empty = no daily limit.", 0, null, "$"),
                SettingInfo.Int("budget.resetDay", "The month starts on day", 1, null, 1, 28),
                SettingInfo.Int("budget.warnPercent", "Warn at", 80, "From here agents use paid agents only when you asked.", 1, 100, "%"),
                SettingInfo.Choice("budget.onLimit", "When the budget is spent", "stop", ["stop", "ask"], "stop: paid calls stop. ask: your chats stop with \"let this chat go over\"; subagents stop."),
            ],
        });
        context.Services.Register(new SettingsSection
        {
            Id = "agentQueue", Title = "Agent queue", Group = "Agents", Order = 15,
            Settings =
            [
                SettingInfo.Int("agents.queueMax", "Waiting runs per agent", 20,
                    "The longest an agent's queue of waiting runs grows: beyond it a new run is refused with a clear error instead of waiting forever. 0: no waiting at all."),
                SettingInfo.Int("agents.queueTimeoutSeconds", "Longest wait for a free slot", 600,
                    "A run that has waited this long for a slot fails with a clear error instead of waiting forever. 0: no time limit.", 0, null, "s"),
            ],
        });
        // the agents themselves (agents.<id>) have their own editor in the settings (Agents & budget)
        try
        {
            var upgraded = AgentUpgrade.Run(context.Settings);
            if (upgraded.Count > 0) context.Logger.LogInformation("Lanes became agents: {Agents}", string.Join(", ", upgraded));
        }
        catch (Exception ex) { context.Logger.LogWarning(ex, "Turning the lanes into agents failed"); }

        // The registry of what runs on shared model resources outlives a reload of this plugin: a new generation adopts the registered
        // instance (it is a plain class in the shared contracts) so the count carries across the swap, and points it at its own bus.
        var leases = context.Services.Get<IResourceLeases>() as ResourceLeases ?? new ResourceLeases(context.Events);
        leases.Rebind(context.Events);
        context.Services.Register<IResourceLeases>(leases);
        context.Sessions.DeclareForkReset(Ledger.AllowanceMetaKey);

        var usage = new Ledger(context);
        _usage = usage;
        usage.Initialize();
        var scheduler = new AgentScheduler(context, usage);
        _scheduler = scheduler;
        scheduler.Refresh();

        context.Services.Register<IAgentScheduler>(scheduler);
        context.Services.Register<IModelMiddleware>(new LedgerMiddleware(usage, scheduler));
        context.Tools.Register(new AgentChoicesTool(scheduler, usage));
        context.Services.Register<IPromptSection>(new AgentsPromptSection());

        context.Rpc.RegisterReadOnly("agents.list", (_, _) => Task.FromResult<object?>(scheduler.Snapshot()),
            "The agents (always) and other model calls in progress, with instances, owners, waiters, state, price and today's spend → AgentSlots[]");
        context.Rpc.RegisterReadOnly("agents.resources", (_, _) => Task.FromResult<object?>(scheduler.Resources()),
            "Shared model resources, including calls admitted before a scheduler replacement → ModelResourceSlots[]");
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
        context.Rpc.RegisterReadOnly("usage.summary", (_, _) => Task.FromResult<object?>(usage.Summary()),
            "Today's tokens per provider, the budget, this period's calls per model → { day, providers, budget, models }");
        context.Rpc.RegisterReadOnly("usage.session", (r, _) => Task.FromResult<object?>(usage.SessionCost(r.Required("sessionId"))),
            "What a chat cost: { sessionId } → { costUsd, calls, withSubagentsUsd, withSubagentsCalls }");
        context.Rpc.RegisterReadOnly("budget.status", (_, _) => Task.FromResult<object?>(usage.BudgetStatus()),
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
            var status = usage.BudgetStatus();
            status["executionAvailable"] = rt is not null;
            if (rt is null) status["continuationReason"] = "No executor capability is available; the allowance is saved. Continue explicitly when execution is available.";
            return status;
        }, "Let a chat go over the budget until the period ends and continue it: { sessionId } (budget.onLimit \"ask\")");

        context.Events.Subscribe(EventTypes.SettingsChanged, e =>
        {
            scheduler.Refresh();
            var path = (NetPiJson.ToNode(e.Data) as JsonObject)?["path"]?.GetValue<string>();
            // A file reload or settings.replace has no path: its budget may have changed too.
            if (string.IsNullOrEmpty(path) || path == "budget" || path.StartsWith("budget.", StringComparison.Ordinal))
                context.Events.Publish("usage.changed", usage.BudgetStatus());
        });
        // model states (loaded, unloaded, offline) switch local agents on and off
        context.Events.Subscribe(EventTypes.ModelsChanged, _ => scheduler.Refresh());
        context.Events.Subscribe("resources.released", _ => scheduler.Refresh());

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
        _usage?.Stop();
        return Task.CompletedTask;
    }
}
