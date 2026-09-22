using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NetPI.Abstractions;
using NetPI.Agent;
using Xunit;

namespace NetPI.Host.Tests;

/// <summary>
/// astra-2 §3/§4: the runner admits a Pooled deployment through the lane
/// scheduler and queues (never rejects) it when the pool is full; a queued
/// record executes NOTHING until its lane is admitted (the AdmittedFromQueue
/// event is the only trigger that starts it); a Direct deployment bypasses
/// lanes entirely. Hermetic: a gated provider, a policy source, a real
/// LaneScheduler wrapped by a counting facade, and a no-op session store.
/// </summary>
public class RunnerLaneAdmissionTests
{
    // ---- a model provider whose RunAsync blocks on a per-run gate -----------
    private sealed class GateProvider : IModelProvider
    {
        private readonly object _lock = new();
        private readonly Dictionary<string, TaskCompletionSource> _gates = new();
        private readonly List<string> _started = new();
        public IReadOnlyList<string> StartedRunIds { get { lock (_lock) return _started.ToList(); } }

        public async IAsyncEnumerable<ModelEvent> RunAsync(ModelRequest request, CancellationToken cancellationToken)
        {
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var key = request.RunId ?? "ad-hoc";
            lock (_lock) { _gates[key] = tcs; _started.Add(key); }
            yield return new ModelStarted("model");
            using var reg = cancellationToken.Register(() => tcs.TrySetResult());
            await tcs.Task;
            yield return new ModelCompleted(new AgentMessage("a", MessageRole.Assistant,
                [new TextPart("done")], DateTimeOffset.UtcNow));
        }

        public bool OpenRun(string runId)
        {
            lock (_lock) return _gates.TryGetValue(runId, out var t) ? t.TrySetResult() : false;
        }
    }

    // ---- deployment policy source: only the given models are pooled ---------
    private sealed class FakePolicy : IDeploymentPolicySource
    {
        private readonly Dictionary<string, DeploymentPolicy> _map;
        public FakePolicy(params DeploymentPolicy[] policies)
        {
            _map = policies.ToDictionary(p => p.ModelId, StringComparer.Ordinal);
        }
        public DeploymentPolicy? PolicyFor(string modelId) => _map.TryGetValue(modelId, out var p) ? p : null;
    }

    // ---- a counting facade so the test can assert direct runs never call it -
    private sealed class CountingLanes : ILaneScheduler, ILaneAdmissionSink
    {
        private readonly ILaneScheduler _inner;
        public CountingLanes(ILaneScheduler inner) => _inner = inner;
        public int AcquireCalls;
        public ValueTask<LaneAcquireResult> AcquireAsync(LaneQueueEntry q)
        { Interlocked.Increment(ref AcquireCalls); return _inner.AcquireAsync(q); }
        public ValueTask ReleaseAsync(LaneOwnershipToken t) => _inner.ReleaseAsync(t);
        public ValueTask<LaneOwnershipToken?> HandoffAsync(LaneOwnershipToken from, LaneQueueEntry to)
            => _inner.HandoffAsync(from, to);
        public bool TryValidatePermit(LaneOwnershipToken? t, string pool, string assignment)
            => _inner.TryValidatePermit(t, pool, assignment);
        public ValueTask<bool> CancelQueuedAsync(string a) => _inner.CancelQueuedAsync(a);
        public IReadOnlyList<LanePoolSnapshot> Snapshots() => _inner.Snapshots();
        public ValueTask SetPoolEnabledAsync(string pool, bool enabled) => _inner.SetPoolEnabledAsync(pool, enabled);
        public void OnAdmittedFromQueue(Action<LaneOwnershipToken> handler)
        { if (_inner is ILaneAdmissionSink s) s.OnAdmittedFromQueue(handler); }
    }

    // ---- no-op session store (records appends) --------------------------------
    private sealed class NoopStore : ISessionStore
    {
        public int AppendCount { get; private set; }
        public ValueTask<SessionInfo> CreateAsync(string? workspacePath, CancellationToken ct = default)
            => ValueTask.FromResult(new SessionInfo(Guid.NewGuid().ToString("n"), workspacePath,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 0, null));
        public ValueTask<SessionInfo?> GetAsync(string id, CancellationToken ct = default)
            => ValueTask.FromResult<SessionInfo?>(null);
        public ValueTask AppendAsync(SessionEntry entry, CancellationToken ct = default)
        { AppendCount++; return ValueTask.CompletedTask; }
        public ValueTask<IReadOnlyList<SessionInfo>> ListAsync(int count = 50, int offset = 0, CancellationToken ct = default)
            => ValueTask.FromResult<IReadOnlyList<SessionInfo>>(Array.Empty<SessionInfo>());
        public ValueTask<IReadOnlyList<SessionInfo>> SearchAsync(string query, int count = 100, int offset = 0, CancellationToken ct = default)
            => ListAsync(count, offset, ct);
        public ValueTask<int> SearchCountAsync(string query, CancellationToken ct = default)
            => ValueTask.FromResult(0);
        public ValueTask<int> CountAsync(CancellationToken ct = default) => ValueTask.FromResult(0);
        public ValueTask RenameAsync(string id, string title, CancellationToken ct = default)
            => ValueTask.CompletedTask;
        public ValueTask DeleteAsync(string id, CancellationToken ct = default)
            => ValueTask.CompletedTask;
        public ValueTask SetModelAsync(string sessionId, string? modelId, string? reasoningLevel, CancellationToken ct = default)
            => ValueTask.CompletedTask;
        public ValueTask SetWorkspaceAsync(string sessionId, string? workspacePath, CancellationToken ct = default)
            => ValueTask.CompletedTask;
        public ValueTask<IReadOnlyList<SessionEntry>> ReadAsync(string sessionId, int offset, int count, CancellationToken ct = default)
            => ValueTask.FromResult<IReadOnlyList<SessionEntry>>(Array.Empty<SessionEntry>());
        public ValueTask<IReadOnlyList<SessionEntry>> ReadRecentAsync(string sessionId, int count, CancellationToken ct = default)
            => ValueTask.FromResult<IReadOnlyList<SessionEntry>>(Array.Empty<SessionEntry>());
        public ValueTask<IReadOnlyList<SessionEntry>> ReadBeforeAsync(string sessionId, int beforeSequence, int count, CancellationToken ct = default)
            => ValueTask.FromResult<IReadOnlyList<SessionEntry>>(Array.Empty<SessionEntry>());
        public ValueTask<ProjectChangeResult> SetProjectAsync(ProjectChangeRequest change, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    // ---- a fully in-memory plugin context (services + no-op bus/leases) ------
    private sealed class AdmCtx : IPluginContext
    {
        private sealed class Reg : IServiceRegistry
        {
            private readonly Dictionary<string, object> _map = new(StringComparer.Ordinal);
            private sealed class Noop : IDisposable { public void Dispose() { } }
            private sealed class Lease<T>(T v) : IValueLease<T> { public T Value => v; public void Dispose() { } public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; } }
            public void Put(string id, object o) => _map[id] = o;
            public IDisposable Register<T>(string id, T instance) where T : notnull { _map[id] = instance; return new Noop(); }
            public IValueLease<T> Acquire<T>(string id) where T : notnull => new Lease<T>(Resolve<T>(id));
            public IValueLease<object> Acquire(string id, Type t)
            { var v = _map.TryGetValue(id, out var o) ? o : throw new ServiceUnavailableException(id, "missing"); return new Lease<object>(v); }
            public IValueLease<T> AcquireSelfLease<T>() where T : notnull => new Lease<T>(default!);
            public T Resolve<T>(string id) where T : notnull
            { var v = _map.TryGetValue(id, out var o) ? o : throw new ServiceUnavailableException(id, "missing"); return (T)v; }
        }
        public PluginInfo Info => new("test", "test", "1.0.0");
        public IServiceRegistry Services { get; } = new Reg();
        public ICommandRegistry Commands => throw new NotSupportedException();
        public IWebPanelRegistry WebPanels => throw new NotSupportedException();
        public IEventBus Events { get; } = new Bus();
        public JsonElement OwnConfig => JsonDocument.Parse("{}").RootElement;
        public IPluginLogger Log => new NoopLog();
        public IValueLease<object> LeaseSelf() => new NoopLease();
        private sealed class Bus : IEventBus
        {
            private sealed class Noop : IDisposable { public void Dispose() { } }
            public IDisposable Subscribe<TEvent>(NetPI.Abstractions.EventHandler<TEvent> h, EventSubscriptionOptions? o = null) => new Noop();
            public ValueTask PublishAsync<TEvent>(TEvent e, CancellationToken c = default) where TEvent : notnull => ValueTask.CompletedTask;
        }
        private sealed class NoopLog : IPluginLogger { public void Debug(string m) { } public void Information(string m) { } public void Warning(string m) { } public void Error(string m, Exception? e = null) { } }
        private sealed class NoopLease : IValueLease<object> { public object Value => this; public void Dispose() { } public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; } }
        public void Add(string id, object service) => ((Reg)Services).Put(id, service);
    }

    private sealed class NullLogger : IPluginLogger
    {
        public void Debug(string m) { }
        public void Information(string m) { }
        public void Warning(string m) { }
        public void Error(string m, Exception? e = null) { }
    }

    private static async Task WaitUntil(Func<bool> cond, int ms = 20, int max = 300)
    {
        for (var i = 0; i < max && !cond(); i++) await Task.Delay(ms);
    }

    [Fact]
    public async Task Pooled_A_B_C_ThirdIsQueuedThenStartsOnRelease()
    {
        var scheduler = new NetPI.Lanes.LaneScheduler("gen-1", new NullLogger());
        // capacity 2 (manual) so A and B take both lanes and C must queue.
        scheduler.RegisterPool("pool-1", "dep-1", "m-1", LaneCapacityMode.Manual, 2, true);

        var policy = new FakePolicy(new DeploymentPolicy("m-1", DeploymentExecutionMode.Pooled, "pool-1", "dep-1"));
        var provider = new GateProvider();
        var store = new NoopStore();
        var ctx = new AdmCtx();
        ctx.Add("provider", provider);
        ctx.Add("sessions", store);
        ctx.Add("deployments", policy);
        var lanes = new CountingLanes(scheduler);
        ctx.Add("lanes", lanes);

        var runner = new AgentRunner(new AgentRuntime(ctx), ctx, maxConcurrentRuns: 8);

        var a = await runner.StartRunAsync(new AgentRunRequest("s-a", null, "m-1", "work"));
        var b = await runner.StartRunAsync(new AgentRunRequest("s-b", null, "m-1", "work"));
        Assert.Equal(RunDisposition.Admitted, a.Disposition);
        Assert.Equal(RunDisposition.Admitted, b.Disposition);
        Assert.Equal(2, lanes.Snapshots().Single(p => p.PoolId == "pool-1").OwnedCount);
        Assert.Equal(2, lanes.AcquireCalls); // A, B admitted

        // C: pool full → ACCEPTED + QUEUED (never rejected), and it must not
        // have started (no model call for its run id yet).
        var c = await runner.StartRunAsync(new AgentRunRequest("s-c", null, "m-1", "work"));
        Assert.Equal(RunDisposition.Queued, c.Disposition);
        Assert.NotNull(c.RunId);
        Assert.Equal(3, lanes.AcquireCalls); // C's AcquireAsync also counted
        Assert.False(provider.StartedRunIds.Contains(c.RunId!), "queued run must not execute");
        Assert.Equal(1, lanes.Snapshots().Single(p => p.PoolId == "pool-1").QueueCount);
        // All three accepted runs persist their user entry (durable, not fake).
        Assert.True(store.AppendCount >= 3, "all three accepted runs persist their user entry");

        // Release A's segment → the scheduler admits C → the queued record starts.
        // A is admitted but its ExecuteAsync may still be setting up; wait until its
        // provider gate is live before opening it (deterministic, no timing sleeps).
        await WaitUntil(() => provider.StartedRunIds.Contains(a.RunId!));
        Assert.True(provider.OpenRun(a.RunId!));
        await WaitUntil(() => provider.StartedRunIds.Contains(c.RunId!));
        Assert.True(provider.StartedRunIds.Contains(c.RunId!));
        // C holds a lane (B is still gated) → owned back to 2, queue drained.
        Assert.Equal(2, lanes.Snapshots().Single(p => p.PoolId == "pool-1").OwnedCount);
        Assert.Equal(0, lanes.Snapshots().Single(p => p.PoolId == "pool-1").QueueCount);
    }

    [Fact]
    public async Task Direct_BypassesLanesEvenWhenPoolFull()
    {
        var scheduler = new NetPI.Lanes.LaneScheduler("gen-1", new NullLogger());
        scheduler.RegisterPool("pool-1", "dep-1", "m-1", LaneCapacityMode.Manual, 1, true);
        var policy = new FakePolicy(new DeploymentPolicy("m-1", DeploymentExecutionMode.Pooled, "pool-1", "dep-1"));
        var provider = new GateProvider();
        var ctx = new AdmCtx();
        ctx.Add("provider", provider);
        ctx.Add("sessions", new NoopStore());
        ctx.Add("deployments", policy);
        var lanes = new CountingLanes(scheduler);
        ctx.Add("lanes", lanes);
        var runner = new AgentRunner(new AgentRuntime(ctx), ctx, maxConcurrentRuns: 8);

        // Fill the (capacity-1) pool with a pooled run.
        var a = await runner.StartRunAsync(new AgentRunRequest("s-a", null, "m-1", "work"));
        Assert.Equal(RunDisposition.Admitted, a.Disposition);
        var acquiresAfterA = lanes.AcquireCalls;

        // A Direct model is NOT in the policy table → the runner never touches
        // the scheduler, even though the pool is full.
        var d = await runner.StartRunAsync(new AgentRunRequest("s-d", null, "m-direct", "work"));
        Assert.Equal(RunDisposition.Admitted, d.Disposition);
        Assert.True(acquiresAfterA == lanes.AcquireCalls, "a direct run must not call AcquireAsync");
        await WaitUntil(() => provider.StartedRunIds.Contains(d.RunId!));
        Assert.Contains(d.RunId, provider.StartedRunIds);
    }
}
