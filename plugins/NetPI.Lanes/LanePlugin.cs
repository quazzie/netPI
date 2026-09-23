using System.Text.Json;
using NetPI.Abstractions;

namespace NetPI.Lanes;

/// <summary>
/// astra-2: the NetPI.Lanes plugin — owns the <see cref="ILaneScheduler"/>
/// (service <c>lanes</c>) and polls the provider's capacity metadata
/// (service <c>provider-capacity</c>, registered by the provider plugin) at a
/// bounded interval. The scheduler itself makes NO inference requests and
/// NEVER derives capacity from anything but a fresh total-concurrency
/// observation plus the user's optional cap (astra-2 §5.3).
/// </summary>
public sealed class LanePlugin : INetPiPlugin
{
    private const string CapacityServiceId = "provider-capacity";

    private LaneScheduler? _scheduler;
    private IPluginContext? _ctx;
    private CancellationTokenSource? _pollCts;
    private Task? _pollTask;

    /// <summary>model → execution policy, built from the parsed pools/deployments (astra-2 §4).</summary>
    internal record PolicyBinding(string ModelId, string PoolId, string DeploymentId, bool Enabled);
    private readonly List<PolicyBinding> _policies = new();
    public PluginInfo Info { get; } = new("netPI.Lanes", "Lane Scheduler", "0.1.0");

    public async ValueTask LoadAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        _ctx = context;
        var gen = $"host-{Environment.ProcessId}";
        _scheduler = new LaneScheduler(gen, context.Log);

        if (context.OwnConfig.ValueKind != JsonValueKind.Object)
        {
            context.Log.Warning("netpi.lanes: config section missing — no pools configured (pooled execution will fail closed)");
            context.Services.Register<ILaneScheduler>("lanes", _scheduler);
            await ValueTask.CompletedTask;
            return;
        }

        var enabled = context.OwnConfig.TryGetProperty("enabled", out var en) &&
                      en.ValueKind == JsonValueKind.True;
        if (!enabled)
        {
            context.Log.Information("netpi.lanes disabled (legacy mode) — registering an empty scheduler (no pools)");
            context.Services.Register<ILaneScheduler>("lanes", _scheduler);
            await ValueTask.CompletedTask;
            return;
        }

        var deployments = new Dictionary<string, string>();
        var deploymentEnabled = new Dictionary<string, bool>(StringComparer.Ordinal);
        if (context.OwnConfig.TryGetProperty("deployments", out var depEl) && depEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var d in depEl.EnumerateArray())
            {
                if (d.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String)
                {
                    var depId = idEl.GetString()!;
                    var depModel = d.TryGetProperty("modelId", out var mEl) && mEl.ValueKind == JsonValueKind.String
                        ? mEl.GetString()! : depId;
                    deployments[depId] = depModel;
                    // astra-2 §11.2: a disabled deployment ("enabled": false, e.g. a
                    // direct-cloud model the user switched off) still records its
                    // execution policy so the runner REJECTS its requests before any
                    // inference — no paid call despite a busy local queue.
                    deploymentEnabled[depModel] = !d.TryGetProperty("enabled", out var enEl)
                        || enEl.ValueKind == JsonValueKind.True;
                }
            }
        }

        if (context.OwnConfig.TryGetProperty("pools", out var poolsEl) && poolsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var p in poolsEl.EnumerateArray())
            {
                var poolId = p.TryGetProperty("id", out var pidEl) && pidEl.ValueKind == JsonValueKind.String
                    ? pidEl.GetString()! : null;
                if (poolId is null)
                {
                    context.Log.Warning("lanes: pool entry without an id — skipped");
                    continue;
                }

                var poolEnabled = !p.TryGetProperty("enabled", out var peEl) || peEl.ValueKind == JsonValueKind.True;
                LaneCapacityMode mode = LaneCapacityMode.Provider;
                int? maxAgents = null;
                if (p.TryGetProperty("capacity", out var capEl) && capEl.ValueKind == JsonValueKind.Object)
                {
                    if (capEl.TryGetProperty("mode", out var modeEl) && modeEl.ValueKind == JsonValueKind.String)
                        mode = string.Equals(modeEl.GetString(), "manual", StringComparison.OrdinalIgnoreCase)
                            ? LaneCapacityMode.Manual : LaneCapacityMode.Provider;
                    if (capEl.TryGetProperty("maxAgents", out var maEl) && maEl.ValueKind == JsonValueKind.Number &&
                        maEl.TryGetInt32(out var ma) && ma > 0)
                        maxAgents = ma;
                    if (mode == LaneCapacityMode.Manual && maxAgents is null)
                    {
                        // astra-2 §5.2: a manual mode with no validated agents value is a misconfiguration —
                        // register as provider-mode with the cap as null and log loudly (never guess a number).
                        context.Log.Warning($"lanes: pool {poolId} in manual mode without a positive maxAgents — holding admission (provider mode with no cap)");
                        mode = LaneCapacityMode.Provider;
                    }
                }

                // residency: initially single-deployment per pool (astra-2 §5.4).
                string? deployment = null;
                if (p.TryGetProperty("deploymentIds", out var depIds) && depIds.ValueKind == JsonValueKind.Array)
                    foreach (var di in depIds.EnumerateArray())
                        if (di.ValueKind == JsonValueKind.String) deployment = di.GetString()!;

                if (deployment is null)
                {
                    context.Log.Warning($"lanes: pool {poolId} has no deployment binding — registered as hold (no admission)");
                    continue;
                }
                if (!deployments.ContainsKey(deployment))
                {
                    context.Log.Warning($"lanes: pool {poolId} references unknown deployment {deployment} — registered as hold");
                    continue;
                }

                _scheduler.RegisterPool(poolId, deployment, deployments[deployment], mode, maxAgents, poolEnabled);
                // astra-2 §4: record the trusted model→(pool, deployment) binding so
                // the runner can resolve a model to its admission policy.
                _policies.Add(new PolicyBinding(deployments[deployment], poolId, deployment,
                    deploymentEnabled.TryGetValue(deployments[deployment], out var de) && de));
            }
        }

        context.Services.Register<ILaneScheduler>("lanes", _scheduler);
        // astra-2 §4: the trusted model→policy resolver (runner consults it to decide
        // lane admission vs. direct execution). Built from the same parsed config.
        context.Services.Register<IDeploymentPolicySource>("deployments", new DeploymentPolicySource(_policies));
        context.Log.Information($"lanes ready: {_scheduler.Snapshots().Count} pool(s), {_policies.Count} policy binding(s)");

        StartCapacityPolling(context);
        await ValueTask.CompletedTask;
    }

    private void StartCapacityPolling(IPluginContext context)
    {
        _pollCts = new CancellationTokenSource();
        _pollTask = Task.Run(() => CapacityPollLoopAsync(context, _pollCts.Token), CancellationToken.None);
    }

    /// <summary>
    /// Bounded-interval metadata polling (astra-2 §5.3): refresh each
    /// pool's provider observation, never more often than the configured
    /// interval, and always with a bounded wait per observation.
    /// </summary>
    private async Task CapacityPollLoopAsync(IPluginContext context, CancellationToken ct)
    {
        const int intervalSeconds = 15;
        const int boundSeconds = 5;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var source = ResolveCapacitySource(context);
                if (source is not null)
                {
                    foreach (var snap in _scheduler!.Snapshots())
                    {
                        if (snap.ProviderStatus == ProviderCapacityStatus.Fresh) continue; // a fresh observation is enough
                        using var boundCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        boundCts.CancelAfter(TimeSpan.FromSeconds(boundSeconds));
                        try
                        {
                            // The capacity source is keyed by the pool's bound MODEL
                            // (a pool binds one deployment → one model).
                            var model = PoolModel(snap.PoolId);
                            if (model is null) continue;
                            var obs = await source.ObserveAsync(model, boundCts.Token);
                            var before = _scheduler.Snapshots();
                            _scheduler.UpdateProviderObservation(obs);
                            var after = _scheduler.Snapshots();
                            if (PoolProjectionChanged(before, after))
                                await PublishLanesStateAsync(context, after, ct);
                        }
                        catch (OperationCanceledException) { /* bounded wait expired — retry next tick */ }
                        catch (Exception ex)
                        {
                            context.Log.Debug($"capacity observation failed: {ex.Message}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                context.Log.Warning($"capacity poll tick failed: {ex.Message}");
            }
            try { await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>The model a pool is bound to — the key the capacity source is polled with.</summary>
    private string? PoolModel(string poolId) => _scheduler!.ModelOf(poolId);


    private IProviderCapacitySource? ResolveCapacitySource(IPluginContext context)
    {
        try { return context.Services.Resolve<IProviderCapacitySource>(CapacityServiceId); }
        catch (ServiceUnavailableException) { return null; }
    }

    private static bool PoolProjectionChanged(
        IReadOnlyList<LanePoolSnapshot> before, IReadOnlyList<LanePoolSnapshot> after)
    {
        if (before.Count != after.Count) return true;
        var oldById = before.ToDictionary(p => p.PoolId, StringComparer.Ordinal);
        foreach (var p in after)
        {
            if (!oldById.TryGetValue(p.PoolId, out var old) ||
                old.Enabled != p.Enabled || old.Draining != p.Draining ||
                old.OwnedCount != p.OwnedCount || old.EffectiveCapacity != p.EffectiveCapacity ||
                old.CapacityMode != p.CapacityMode || old.ProviderReportedConcurrency != p.ProviderReportedConcurrency ||
                old.UserCap != p.UserCap || old.ProviderStatus != p.ProviderStatus ||
                old.QueueCount != p.QueueCount || old.BlockedReason != p.BlockedReason)
                return true;
        }
        return false;
    }

    private async ValueTask PublishLanesStateAsync(
        IPluginContext context, IReadOnlyList<LanePoolSnapshot> snapshots, CancellationToken ct)
    {
        var pools = snapshots.Select(s =>
        {
            var binding = _policies.FirstOrDefault(p => string.Equals(p.PoolId, s.PoolId, StringComparison.Ordinal));
            return new AgentPoolSnapshot(s.PoolId, binding?.DeploymentId ?? string.Empty,
                binding?.ModelId ?? string.Empty, s.OwnedCount, s.QueueCount,
                s.EffectiveCapacity, s.Enabled, s.BlockedReason);
        }).ToArray();
        await context.Events.PublishAsync(new LanesStateEvent(pools, DateTimeOffset.UtcNow), ct);
    }

    public async ValueTask StartAsync(CancellationToken cancellationToken)
    {
        // One eager, bounded capacity refresh at startup so the first
        // admission decision is not blind (astra-2 §5.3: unknown holds, it
        // does not guess).
        try
        {
            if (_ctx is { } ctx)
            {
                var source = ResolveCapacitySource(ctx);
                if (source is not null && _scheduler is not null)
                {
                    foreach (var snap in _scheduler.Snapshots())
                    {
                        if (snap.ProviderStatus == ProviderCapacityStatus.Fresh) continue;
                        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                        cts.CancelAfter(TimeSpan.FromSeconds(10));
                        var model = PoolModel(snap.PoolId);
                        if (model is null) continue;
                        var obs = await source.ObserveAsync(model, cts.Token);
                        var before = _scheduler.Snapshots();
                        _scheduler.UpdateProviderObservation(obs);
                        var after = _scheduler.Snapshots();
                        if (PoolProjectionChanged(before, after))
                            await PublishLanesStateAsync(ctx, after, cancellationToken);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _ctx?.Log.Debug($"eager capacity refresh failed: {ex.Message}");
        }
        await ValueTask.CompletedTask;
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken)
    {
        _pollCts?.Cancel();
        _pollTask?.Wait(TimeSpan.FromSeconds(2));
        _pollCts?.Dispose();
        _pollCts = null;
        _pollTask = null;
        await ValueTask.CompletedTask;
    }

    public ValueTask UnloadAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
}

/// <summary>
/// astra-2 §4: the trusted model→execution-policy resolver. Built from the
/// same parsed pool/deployment config that registered the scheduler, so a
/// model resolves to exactly the pool/deployment the scheduler admits on.
/// A model with no pool binding resolves to null (legacy direct execution).
/// </summary>
internal sealed class DeploymentPolicySource : IDeploymentPolicySource
{
    private readonly IReadOnlyDictionary<string, DeploymentPolicy> _byModel;

    public DeploymentPolicySource(IReadOnlyList<LanePlugin.PolicyBinding> bindings)
    {
        var map = new Dictionary<string, DeploymentPolicy>(StringComparer.Ordinal);
        foreach (var b in bindings)
            map[b.ModelId] = new DeploymentPolicy(b.ModelId, DeploymentExecutionMode.Pooled, b.PoolId, b.DeploymentId, b.Enabled);
        _byModel = map;
    }

    public DeploymentPolicy? PolicyFor(string modelId)
    {
        if (string.IsNullOrEmpty(modelId)) return null;
        return _byModel.TryGetValue(modelId, out var p) ? p : null;
    }
}
