using Microsoft.Extensions.Logging;

namespace NetPI.Lanes;

/// <summary>
/// Lanes (<see cref="ILaneScheduler"/>), the ledger of every model call with its cost, and the budget.
/// <para>Settings: <c>lanes.&lt;id&gt;</c> <c>{ model, capacity, use, budget: { limitUsd }, cost: { input, output } }</c>,
/// <c>lanes.cloudDefaultCapacity</c> (4), <c>lanes.localDefaultCapacity</c> (1), <c>budget.*</c>; still read:
/// <c>lanes.pools</c> (model globs) and <c>lanes.budgets.&lt;provider&gt;.dailyTokens</c>.</para>
/// <para>RPC: <c>lanes.list</c>, <c>usage.summary</c>, <c>usage.session</c>, <c>budget.status</c>, <c>budget.allow</c>. Events:
/// <c>lanes.changed { pools }</c>, <c>usage.changed</c> (the budget status). For agents: the <c>lanes_list</c> tool and a
/// "Lanes" system prompt section (only for agents that can spawn subagents).</para>
/// </summary>
[NetPiPlugin("netpi.lanes", Name = "Lanes", Description = "Lanes per model, priority queueing, the cost of every model call and the budget", Order = 30)]
public sealed class LanesPlugin : INetPiPlugin
{
    private LaneScheduler? _scheduler;

    internal LaneScheduler? Scheduler => _scheduler;

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
                SettingInfo.Int("budget.warnPercent", "Warn at", 80, "From here agents use paid lanes only when you asked.", 1, 100, "%"),
                SettingInfo.Choice("budget.onLimit", "When the budget is spent", "stop", ["stop", "ask"], "stop: paid calls stop. ask: your chats stop with \"let this chat go over\"; subagents stop."),
            ],
        });
        context.Services.Register(new SettingsSection
        {
            Id = "lanes", Title = "Lanes", Group = "Models", Order = 10,
            Help = "A lane is a model with parallel slots. The lanes you set up (the table above) are the ones agents choose from; every other model gets an automatic lane.",
            Settings =
            [
                SettingInfo.Int("lanes.localDefaultCapacity", "Automatic lanes: slots per local model", 1, "When the catalog doesn't say how many requests the model serves at once.", 1, 64),
                SettingInfo.Int("lanes.cloudDefaultCapacity", "Automatic lanes: slots per cloud provider", 4, null, 1, 64),
            ],
        });
        var usage = new Ledger(context);
        usage.Initialize();
        var scheduler = new LaneScheduler(context, usage);
        _scheduler = scheduler;
        scheduler.Refresh();

        context.Services.Register<ILaneScheduler>(scheduler);
        context.Services.Register<IModelMiddleware>(new LedgerMiddleware(usage, scheduler));
        context.Tools.Register(new LanesListTool(scheduler, usage));
        context.Services.Register<IPromptSection>(new LanesPromptSection());

        context.Rpc.Register("lanes.list", (_, _) => Task.FromResult<object?>(scheduler.Snapshot()),
            "Lanes with capacity, owners, waiters, price and today's spend → LanePoolInfo[]");
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
        context.Events.Subscribe(EventTypes.ModelsChanged, _ => scheduler.Refresh());

        // Pools come from the model catalog: make sure it gets listed soon after startup so lanes.list isn't empty.
        _ = Task.Run(async () =>
        {
            try
            {
                await context.Models.ListAsync(false, context.Stopping).ConfigureAwait(false);
                scheduler.Refresh();
                var pools = scheduler.Snapshot();
                if (pools.Count > 0)
                    context.Logger.LogInformation("Lanes: {Pools}", string.Join(", ", pools.Select(p => $"{p.Key} {p.Busy}/{p.Capacity}")));
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { context.Logger.LogDebug(ex, "Initial model listing for lanes failed"); }
        }, CancellationToken.None);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        _scheduler?.Stop();
        return Task.CompletedTask;
    }
}
