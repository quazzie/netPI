using Microsoft.Extensions.Logging;

namespace NetPI.Lanes;

/// <summary>
/// Lane scheduler (<see cref="ILaneScheduler"/>), daily usage and budgets.
/// <para>Settings: <c>lanes.pools</c> <c>{ "&lt;key&gt;": { "capacity": 2, "models": ["aiproxy/qwen3.8-27b", "anthropic/*"] } }</c>,
/// <c>lanes.cloudDefaultCapacity</c> (4), <c>lanes.localDefaultCapacity</c> (1), <c>lanes.budgets.&lt;provider&gt;.dailyTokens</c>.</para>
/// <para>RPC: <c>lanes.list</c>, <c>usage.summary</c>. Events: <c>lanes.changed { pools }</c>. For agents: the <c>lanes_list</c>
/// tool and a "Lanes" system prompt section (only for agents that can spawn subagents).</para>
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
        context.Tools.Register(new LanesListTool(scheduler));
        context.Services.Register<IPromptSection>(new LanesPromptSection());

        context.Rpc.Register("lanes.list", (_, _) => Task.FromResult<object?>(scheduler.Snapshot()),
            "Lane pools with capacity, owners and waiters → LanePoolInfo[]");
        context.Rpc.Register("usage.summary", (_, _) => Task.FromResult<object?>(usage.Summary()),
            "Today's token usage per provider → { day, providers: [...] }");

        context.Events.Subscribe(EventTypes.SettingsChanged, _ => scheduler.Refresh());
        context.Events.Subscribe(EventTypes.ModelsChanged, _ => scheduler.Refresh());
        context.Events.Subscribe(EventTypes.UsageRecorded, usage.Record);

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
