using Microsoft.Extensions.Logging;

namespace NetPI.Lanes;

/// <summary>
/// Lane scheduler (<see cref="ILaneScheduler"/>), daily usage and budgets.
/// <para>Settings: <c>lanes.pools</c> <c>{ "&lt;key&gt;": { "capacity": 2, "models": ["aiproxy/qwen3.8-27b", "anthropic/*"] } }</c>,
/// <c>lanes.cloudDefaultCapacity</c> (4), <c>lanes.localDefaultCapacity</c> (1), <c>lanes.budgets.&lt;provider&gt;.dailyTokens</c>.</para>
/// <para>RPC: <c>lanes.list</c>, <c>usage.summary</c>. Events: <c>lanes.changed { pools }</c>.</para>
/// </summary>
[NetPiPlugin("netpi.lanes", Name = "Lanes", Description = "Parallel lanes per model pool, priority queueing, usage and budgets", Order = 30)]
public sealed class LanesPlugin : INetPiPlugin
{
    private LaneScheduler? _scheduler;

    internal LaneScheduler? Scheduler => _scheduler;

    public Task StartAsync(IPluginContext context, CancellationToken ct)
    {
        var usage = new UsageTracker(context);
        usage.Initialize();
        var scheduler = new LaneScheduler(context, usage);
        _scheduler = scheduler;
        scheduler.Refresh();

        context.Services.Register<ILaneScheduler>(scheduler);

        context.Rpc.Register("lanes.list", (_, _) => Task.FromResult<object?>(scheduler.Snapshot()),
            "Lane pools with capacity, owners and waiters → LanePoolInfo[]");
        context.Rpc.Register("usage.summary", (_, _) => Task.FromResult<object?>(usage.Summary()),
            "Today's token usage per provider → { day, providers: [...] }");

        context.Events.Subscribe(EventTypes.SettingsChanged, _ => scheduler.Refresh());
        context.Events.Subscribe(EventTypes.ModelsChanged, _ => scheduler.Refresh());
        context.Events.Subscribe(EventTypes.UsageRecorded, usage.Record);

        context.Logger.LogInformation("Lanes: {Pools}", string.Join(", ", scheduler.Snapshot().Select(p => $"{p.Key} {p.Busy}/{p.Capacity}")));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        _scheduler?.Stop();
        return Task.CompletedTask;
    }
}
