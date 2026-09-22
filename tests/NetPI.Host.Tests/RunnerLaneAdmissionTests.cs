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

    private sealed class NonCooperativeProvider : IModelProvider
    {
        private readonly object _lock = new();
        private readonly List<TaskCompletionSource> _gates = new();
        public int EnteredCount { get { lock (_lock) return _gates.Count; } }

        public async IAsyncEnumerable<ModelEvent> RunAsync(ModelRequest request, CancellationToken cancellationToken)
        {
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_lock) _gates.Add(tcs);
            yield return new ModelStarted("model");
            await tcs.Task; // ignores cancellation
            yield return new ModelCompleted(new AgentMessage("a", MessageRole.Assistant,
                [new TextPart("done")], DateTimeOffset.UtcNow));
        }

        public void ReleaseAll()
        {
            List<TaskCompletionSource> all;
            lock (_lock) { all = _gates.ToList(); _gates.Clear(); }
            foreach (var t in all) t.TrySetResult();
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

    // ---- a bus that records the AgentEvents it is published ------------------
    private sealed class CapturingBus : IEventBus
    {
        private readonly object _lock = new();
        private readonly List<AgentEvent> _events = new();
        public IReadOnlyList<AgentEvent> Events
        { get { lock (_lock) return _events.ToList(); } }
        public IDisposable Subscribe<TEvent>(NetPI.Abstractions.EventHandler<TEvent> h, EventSubscriptionOptions? o = null) => new NoopSub();
        public ValueTask PublishAsync<TEvent>(TEvent e, CancellationToken c = default) where TEvent : notnull
        {
            if (e is AgentEvent aev) { lock (_lock) _events.Add(aev); }
            return ValueTask.CompletedTask;
        }
        private sealed class NoopSub : IDisposable { public void Dispose() { } }
    }

    // ---- a fully in-memory plugin context (services + no-op bus/leases) ------

    private sealed class AdmCtx(IEventBus? bus = null) : IPluginContext
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
        public IEventBus Events { get; } = bus ?? new Bus();
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

    // ---- §13: a QUEUED run (full pool) is cancellable before it ever starts.
    //      CancelQueuedRun removes the queued record, signals its token, and
    //      purges the lane scheduler's queue entry — the run executes nothing and
    //      a later lane admission can no longer fire it (queue drained).
    [Fact]
    public async Task QueuedRun_CancelPurgesQueueEntryAndNeverStarts()
    {
        var scheduler = new NetPI.Lanes.LaneScheduler("gen-1", new NullLogger());
        // capacity 1 so B must queue behind A.
        scheduler.RegisterPool("pool-1", "dep-1", "m-1", LaneCapacityMode.Manual, 1, true);
        var policy = new FakePolicy(new DeploymentPolicy("m-1", DeploymentExecutionMode.Pooled, "pool-1", "dep-1"));
        var provider = new GateProvider();
        var ctx = new AdmCtx();
        ctx.Add("provider", provider);
        ctx.Add("sessions", new NoopStore());
        ctx.Add("deployments", policy);
        ctx.Add("lanes", new CountingLanes(scheduler));
        var runner = new AgentRunner(new AgentRuntime(ctx), ctx, maxConcurrentRuns: 8);

        var a = await runner.StartRunAsync(new AgentRunRequest("s-a", null, "m-1", "A work"));
        Assert.Equal(RunDisposition.Admitted, a.Disposition);

        // B: pool full → accepted + queued (NOT rejected), and not started yet.
        var b = await runner.StartRunAsync(new AgentRunRequest("s-b", null, "m-1", "B work"));
        Assert.Equal(RunDisposition.Queued, b.Disposition);
        Assert.NotNull(b.RunId);
        Assert.False(provider.StartedRunIds.Contains(b.RunId!));
        Assert.Equal(1, scheduler.Snapshots().Single(p => p.PoolId == "pool-1").QueueCount);

        // Cancel the queued run: it must not start, and the queue entry purges.
        Assert.True(await runner.CancelQueuedRun(b.RunId!));
        Assert.Equal(0, scheduler.Snapshots().Single(p => p.PoolId == "pool-1").QueueCount);
        // The queued record is gone (no live/queued outcome for B's run id).
        Assert.False(runner.ListRuns().Any(r => r.RunId == b.RunId && r.Outcome == RunState.Running));
        // B never executed: the provider never saw its run id.
        Assert.False(provider.StartedRunIds.Contains(b.RunId!), "a cancelled queued run must never execute");
        // A is unaffected and still owns its lane; wait for its segment to start.
        await WaitUntil(() => provider.StartedRunIds.Contains(a.RunId!));
        Assert.Equal(1, scheduler.Snapshots().Single(p => p.PoolId == "pool-1").OwnedCount);
        Assert.Contains(a.RunId, provider.StartedRunIds);
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

    // ---- §16: an ordinary user-created cloud session runs while BOTH local
    //      lanes are occupied — it needs no lane, no agent setup flow, and never
    //      changes the local owners' ownership.
    [Fact]
    public async Task CloudSession_RunsWhileBothLocalLanesOccupied_OwnershipUnchanged()
    {
        var scheduler = new NetPI.Lanes.LaneScheduler("gen-1", new NullLogger());
        scheduler.RegisterPool("pool-1", "dep-1", "m-1", LaneCapacityMode.Manual, 2, true);
        var policy = new FakePolicy(
            new DeploymentPolicy("m-1", DeploymentExecutionMode.Pooled, "pool-1", "dep-1"),
            new DeploymentPolicy("m-cloud", DeploymentExecutionMode.DirectCloud, null, "dep-cloud"));
        var provider = new GateProvider();
        var ctx = new AdmCtx();
        ctx.Add("provider", provider);
        ctx.Add("sessions", new NoopStore());
        ctx.Add("deployments", policy);
        var lanes = new CountingLanes(scheduler);
        ctx.Add("lanes", lanes);
        var runner = new AgentRunner(new AgentRuntime(ctx), ctx, maxConcurrentRuns: 8);

        // Occupy BOTH local lanes.
        var a = await runner.StartRunAsync(new AgentRunRequest("s-a", null, "m-1", "A work"));
        var b = await runner.StartRunAsync(new AgentRunRequest("s-b", null, "m-1", "B work"));
        Assert.Equal(RunDisposition.Admitted, a.Disposition);
        Assert.Equal(RunDisposition.Admitted, b.Disposition);
        await WaitUntil(() => provider.StartedRunIds.Count >= 2);
        int acquiresWithBoth = lanes.AcquireCalls;

        // A user-created cloud session: ordinary send, no setup flow. It must
        // run while both local lanes are held and touch the scheduler never.
        var c = await runner.StartRunAsync(new AgentRunRequest("s-cloud", null, "m-cloud", "cloud work"));
        Assert.Equal(RunDisposition.Admitted, c.Disposition);
        Assert.True(acquiresWithBoth == lanes.AcquireCalls, "the cloud session must not call AcquireAsync");
        await WaitUntil(() => provider.StartedRunIds.Contains(c.RunId!));

        // All three sessions visible; A/B retain their lanes throughout.
        Assert.Equal(3, runner.ListRuns().Count(r => r.Outcome == RunState.Running));
        Assert.Contains(a.RunId, provider.StartedRunIds);
        Assert.Contains(b.RunId, provider.StartedRunIds);
        Assert.Contains(c.RunId, provider.StartedRunIds);
        var snaps = scheduler.Snapshots();
        Assert.Equal(2, snaps[0].OwnedCount); // both local lanes still owned by A/B
        Assert.Equal(0, snaps[0].QueueCount);
    }

    // ---- §16: reload/shutdown with TWO live segments — both are owned by the
    //      stop path and drained (or visibly deferred); no orphan task.
    [Fact]
    public async Task TwoLiveSegments_StopDrainsBoth_NoOrphan()
    {
        var scheduler = new NetPI.Lanes.LaneScheduler("gen-1", new NullLogger());
        scheduler.RegisterPool("pool-1", "dep-1", "m-1", LaneCapacityMode.Manual, 2, true);
        var policy = new FakePolicy(new DeploymentPolicy("m-1", DeploymentExecutionMode.Pooled, "pool-1", "dep-1"));
        var provider = new GateProvider();
        var ctx = new AdmCtx();
        ctx.Add("provider", provider);
        ctx.Add("sessions", new NoopStore());
        ctx.Add("deployments", policy);
        ctx.Add("lanes", new CountingLanes(scheduler));
        var runner = new AgentRunner(new AgentRuntime(ctx), ctx, maxConcurrentRuns: 8);

        var a = await runner.StartRunAsync(new AgentRunRequest("s-a", null, "m-1", "A work"));
        var b = await runner.StartRunAsync(new AgentRunRequest("s-b", null, "m-1", "B work"));
        Assert.Equal(RunDisposition.Admitted, a.Disposition);
        Assert.Equal(RunDisposition.Admitted, b.Disposition);
        await WaitUntil(() => provider.StartedRunIds.Count >= 2);
        Assert.Equal(2, runner.ListRuns().Count(r => r.Outcome == RunState.Running));

        // Cooperative: both segments quiesce when cancelled → the stop drains both.
        var drained = await runner.WaitForRunAsync(TimeSpan.FromSeconds(10));
        Assert.True(drained, "both live segments must quiesce within the bound");
        // Each segment reached a terminal outcome (Completed when the stream
        // honored the cancel and finished; Cancelled when it unwound mid-call).
        Assert.True(runner.GetRun(a.RunId)!.Outcome is RunState.Completed or RunState.Cancelled, $"A={runner.GetRun(a.RunId)!.Outcome}");
        Assert.True(runner.GetRun(b.RunId)!.Outcome is RunState.Completed or RunState.Cancelled, $"B={runner.GetRun(b.RunId)!.Outcome}");
        await Task.Delay(50);
        Assert.Equal(0, runner.ListRuns().Count(r => r.Outcome == RunState.Running));
    }

    [Fact]
    public async Task TwoLiveSegments_NonCooperativeStop_IsVisiblyDeferred_NotOrphaned()
    {
        var scheduler = new NetPI.Lanes.LaneScheduler("gen-1", new NullLogger());
        scheduler.RegisterPool("pool-1", "dep-1", "m-1", LaneCapacityMode.Manual, 2, true);
        var policy = new FakePolicy(new DeploymentPolicy("m-1", DeploymentExecutionMode.Pooled, "pool-1", "dep-1"));
        var ctx = new AdmCtx();
        // A provider whose stream ignores cancellation: the stop must REPORT
        // the deferral, not hang forever and not strand the segment silently.
        var sticky = new NonCooperativeProvider();
        ctx.Add("provider", sticky);
        ctx.Add("sessions", new NoopStore());
        ctx.Add("deployments", policy);
        ctx.Add("lanes", new CountingLanes(scheduler));
        var runner = new AgentRunner(new AgentRuntime(ctx), ctx, maxConcurrentRuns: 8);

        var a = await runner.StartRunAsync(new AgentRunRequest("s-a", null, "m-1", "A work"));
        var b = await runner.StartRunAsync(new AgentRunRequest("s-b", null, "m-1", "B work"));
        await WaitUntil(() => sticky.EnteredCount >= 2);

        // Bounded stop against non-cooperative streams → deferred, not orphaned.
        var drained = await runner.WaitForRunAsync(TimeSpan.FromMilliseconds(500));
        Assert.False(drained, "a non-cooperative stop must be reported as deferred");
        // ...and the segments are still tracked (not dropped into the void).
        Assert.True(runner.GetRun(a.RunId!) is { } ra && ra.Outcome != RunState.Completed, "A must still be tracked");
        sticky.ReleaseAll();
        await WaitUntil(() => runner.GetRun(a.RunId)!.Outcome is RunState.Completed or RunState.Cancelled
                          && runner.GetRun(b.RunId)!.Outcome is RunState.Completed or RunState.Cancelled);
    }

    [Fact]
    public async Task Disabled_DirectCloud_IsRejectedBeforeInference()
    {
        // astra-2 11.2/17: a disabled direct-cloud deployment refuses the request
        // BEFORE any provider call - no paid call despite a busy local queue.
        var scheduler = new NetPI.Lanes.LaneScheduler("gen-1", new NullLogger());
        var policy = new FakePolicy(new DeploymentPolicy("m-cloud", DeploymentExecutionMode.DirectCloud, null, "dep-cloud", false));
        var provider = new GateProvider();
        var ctx = new AdmCtx();
        ctx.Add("provider", provider);
        ctx.Add("sessions", new NoopStore());
        ctx.Add("deployments", policy);
        ctx.Add("lanes", new CountingLanes(scheduler));
        var runner = new AgentRunner(new AgentRuntime(ctx), ctx, maxConcurrentRuns: 8);

        var start = await runner.StartRunAsync(new AgentRunRequest("s-cloud", null, "m-cloud", "work"));

        Assert.NotNull(start.Note);
        Assert.True(start.Note!.Contains("disabled"), $"note was: {start.Note}");
        Assert.Equal(0, provider.StartedRunIds.Count); // no provider call for a disabled deployment
    }

    // ---- astra-2 §16 "Local alias requested as direct-cloud" (F5) ---------------
    //      The execution mode of a model is a TRUSTED fact: it comes only from the
    //      deployment policy resolver (IDeploymentPolicySource), never from the
    //      request (AgentRunRequest has no mode field). A model configured as a
    //      LOCAL (Pooled) deployment is therefore always dispatched on the local
    //      lane path and can never be executed on the direct-cloud path — a
    //      "local alias requested as direct-cloud" has no code path to take, so
    //      the request is governed by the lane pool (admitted or queued), and the
    //      direct-cloud reject branch is unreachable for it.
    [Fact]
    public async Task PooledModel_IsNeverExecutedOnDirectCloudPath()
    {
        // The model is a LOCAL (Pooled) deployment — its execution mode is fixed
        // by the trusted config, not by the (mode-less) request.
        var policy = new FakePolicy(new DeploymentPolicy("m-alias", DeploymentExecutionMode.Pooled, "pool-1", "dep-1"));
        var scheduler = new NetPI.Lanes.LaneScheduler("gen-1", new NullLogger());
        scheduler.RegisterPool("pool-1", "dep-1", "m-alias", LaneCapacityMode.Manual, 0, enabled: true); // capacity 0 → cannot admit
        var provider = new GateProvider();
        var ctx = new AdmCtx();
        ctx.Add("provider", provider);
        ctx.Add("sessions", new NoopStore());
        ctx.Add("deployments", policy);
        var lanes = new CountingLanes(scheduler);
        ctx.Add("lanes", lanes);
        var runner = new AgentRunner(new AgentRuntime(ctx), ctx, maxConcurrentRuns: 8);

        var start = await runner.StartRunAsync(new AgentRunRequest("s-alias", null, "m-alias", "work"));

        // It is treated as a LOCAL lane work item: accepted + QUEUED on the pool
        // (never a rejection), and it never reaches the DirectCloud reject branch.
        Assert.Equal(RunDisposition.Queued, start.Disposition);
        Assert.NotNull(start.RunId);
        Assert.True(lanes.AcquireCalls >= 1, "a Pooled model must go through the lane scheduler");
        Assert.True(start.Note is null || !start.Note.Contains("disabled"),
            $"a Pooled model must not hit the DirectCloud reject branch; note was: {start.Note}");
        Assert.Equal(0, provider.StartedRunIds.Count); // nothing executed, nothing paid
    }

    [Fact]
    public async Task CallerSuppliedRunId_IsReusedAndReconciles()
    {
        // astra-2 7: a caller-supplied run identity (the store's run_id = operation id)
        // is reused for the run record AND the terminal event, so the orchestrator
        // reconciles the event back to its assignment via GetByRunIdAsync.
        var scheduler = new NetPI.Lanes.LaneScheduler("gen-1", new NullLogger());
        var policy = new FakePolicy(new DeploymentPolicy("m-cloud", DeploymentExecutionMode.DirectCloud, null, "dep-cloud", true));
        var provider = new GateProvider();
        var bus = new CapturingBus();
        var ctx = new AdmCtx(bus);
        ctx.Add("provider", provider);
        ctx.Add("sessions", new NoopStore());
        ctx.Add("deployments", policy);
        ctx.Add("lanes", new CountingLanes(scheduler));
        var runner = new AgentRunner(new AgentRuntime(ctx), ctx, maxConcurrentRuns: 8);

        const string opId = "op-abc-123";
        var start = await runner.StartRunAsync(new AgentRunRequest("s-r", null, "m-cloud", "work", RunId: opId));

        Assert.Equal(RunDisposition.Admitted, start.Disposition);
        Assert.Equal(opId, start.RunId);
        await WaitUntil(() => provider.StartedRunIds.Count == 1);
        Assert.Equal(opId, provider.StartedRunIds.Single());

        // Release the gate so the run reaches a terminal outcome.
        Assert.True(provider.OpenRun(opId));
        // Terminal event carries the operation id (reconciliation key).
        await WaitUntil(() => bus.Events.Any(e => e.Type is AgentEventType.AgentCompleted or AgentEventType.AgentFailed or AgentEventType.AgentCancelled), ms: 20, max: 300);
        var terminal = bus.Events.Where(e => e.Type is AgentEventType.AgentCompleted or AgentEventType.AgentFailed or AgentEventType.AgentCancelled).ToList();
        Assert.Single(terminal);
        Assert.Equal(opId, terminal[0].RunId);
    }

    [Fact]
    public async Task Requeue_AcceptsDurableRunExactlyOnce()
    {
        // astra-2 7/16: a durable Queued record is re-entered into the admission
        // pipeline exactly once after a restart (re-adoption). A pooled run
        // re-acquires a lane and starts; a second adopt of the same id is a no-op.
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

        var first = await runner.RequeueRunAsync(new AgentRunRequest("s-adopt", null, "m-1", "work", RunId: "op-adopt"));
        Assert.True(first, "the first adoption enters the pipeline");
        await WaitUntil(() => provider.StartedRunIds.Contains("op-adopt"));
        Assert.True(provider.StartedRunIds.Contains("op-adopt"), "the adopted pooled run starts on its lane");

        var second = await runner.RequeueRunAsync(new AgentRunRequest("s-adopt", null, "m-1", "work", RunId: "op-adopt"));
        Assert.False(second, "a second adopt of the same id is a no-op (adopt once)");
        Assert.Single(provider.StartedRunIds.Where(id => id == "op-adopt"));
    }

    [Fact]
    public async Task DuplicateOperationId_SameSession_IsIdempotent()
    {
        // astra-2 7: a repeated operation id with the SAME session is a duplicate of
        // an accepted run, not a new one (idempotent retry).
        var scheduler = new NetPI.Lanes.LaneScheduler("gen-1", new NullLogger());
        var policy = new FakePolicy(new DeploymentPolicy("m-cloud", DeploymentExecutionMode.DirectCloud, null, "dep-cloud", true));
        var provider = new GateProvider();
        var ctx = new AdmCtx();
        ctx.Add("provider", provider);
        ctx.Add("sessions", new NoopStore());
        ctx.Add("deployments", policy);
        ctx.Add("lanes", new CountingLanes(scheduler));
        var runner = new AgentRunner(new AgentRuntime(ctx), ctx, maxConcurrentRuns: 8);

        // Keep the first run admitted but alive (gated) so the second call sees it live.
        var first = await runner.StartRunAsync(new AgentRunRequest("s-d", null, "m-cloud", "work", RunId: "op-dup"));
        Assert.Equal(RunDisposition.Admitted, first.Disposition);
        await WaitUntil(() => provider.StartedRunIds.Contains("op-dup"));

        var second = await runner.StartRunAsync(new AgentRunRequest("s-d", null, "m-cloud", "work", RunId: "op-dup"));

        Assert.Equal("op-dup", second.RunId);
        Assert.NotNull(second.Note);
        Assert.True(second.Note!.Contains("Duplicate"), $"note was: {second.Note}");
        // A different session with the same id is a conflict, not a duplicate.
        var conflict = await runner.StartRunAsync(new AgentRunRequest("s-other", null, "m-cloud", "work", RunId: "op-dup"));
        Assert.NotNull(conflict.Note);
        Assert.True(conflict.Note!.Contains("conflict"), $"note was: {conflict.Note}");
        Assert.Single(provider.StartedRunIds.Where(id => id == "op-dup")); // a duplicate must not start a second run
    }

    // ---- §16: "Chat Completions / different fake engine — identical ownership
    // guarantees without response IDs/cache support" --------------------------
    // The wire is a property of the PROVIDER, below the lane layer: the scheduler
    // and runner only ever call IModelProvider.RunAsync, so ownership (who is
    // admitted, who queues, who executes) cannot depend on which wire the
    // provider speaks. Two fake engines prove it: the Responses engine emits a
    // usage event (response IDs/usage), the Chat Completions engine emits plain
    // completion only (no response IDs, no cache). The observable OWNERSHIP
    // transitions must be identical; only the provider's emitted events differ.
    private sealed class WireProvider : IModelProvider
    {
        private readonly string _engine;
        private readonly object _lock = new();
        private readonly Dictionary<string, TaskCompletionSource> _gates = new();
        private readonly List<string> _started = new();
        public WireProvider(string engine) => _engine = engine;
        public IReadOnlyList<string> StartedRunIds { get { lock (_lock) return _started.ToList(); } }
        /// <summary>Whether this engine's stream carried a usage event (the wire difference).</summary>
        public bool EmitsUsage { get; private set; }

        public async IAsyncEnumerable<ModelEvent> RunAsync(ModelRequest request, CancellationToken cancellationToken)
        {
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var key = request.RunId ?? "ad-hoc";
            lock (_lock) { _gates[key] = tcs; _started.Add(key); }
            yield return new ModelStarted("model");
            using var reg = cancellationToken.Register(() => tcs.TrySetResult());
            await tcs.Task;
            if (_engine == "responses") { EmitsUsage = true; yield return new UsageUpdated(1, 2, 3); }
            yield return new ModelCompleted(new AgentMessage("a", MessageRole.Assistant,
                [new TextPart("done")], DateTimeOffset.UtcNow));
        }

        public bool OpenRun(string runId)
        { lock (_lock) return _gates.TryGetValue(runId, out var t) ? t.TrySetResult() : false; }
    }

    /// <summary>Runs the A/B/C ownership scenario on the given fake engine and returns
    /// every observable OWNERSHIP fact (wire-agnostic) + the engine's wire signature.</summary>
    private static async Task<(
        string[] disp, int acquire, int owned, int queue, bool cQueuedNotStarted, bool cStartedAfterRelease,
        bool emitsUsage)> WireScenario(string engine)
    {
        var scheduler = new NetPI.Lanes.LaneScheduler("gen-1", new NullLogger());
        scheduler.RegisterPool("pool-1", "dep-1", "m-1", LaneCapacityMode.Manual, 2, true);
        var provider = new WireProvider(engine);
        var ctx = new AdmCtx();
        ctx.Add("provider", provider);
        ctx.Add("sessions", new NoopStore());
        ctx.Add("deployments", new FakePolicy(new DeploymentPolicy("m-1", DeploymentExecutionMode.Pooled, "pool-1", "dep-1")));
        var lanes = new CountingLanes(scheduler);
        ctx.Add("lanes", lanes);

        var runner = new AgentRunner(new AgentRuntime(ctx), ctx, maxConcurrentRuns: 8);
        var a = await runner.StartRunAsync(new AgentRunRequest("s-a", null, "m-1", "work"));
        var b = await runner.StartRunAsync(new AgentRunRequest("s-b", null, "m-1", "work"));
        var c = await runner.StartRunAsync(new AgentRunRequest("s-c", null, "m-1", "work"));
        var owned = lanes.Snapshots().Single(p => p.PoolId == "pool-1").OwnedCount;
        var queue = lanes.Snapshots().Single(p => p.PoolId == "pool-1").QueueCount;
        var cQueuedNotStarted = !provider.StartedRunIds.Contains(c.RunId!);

        // Release A → the scheduler admits C → the queued record starts.
        await WaitUntil(() => provider.StartedRunIds.Contains(a.RunId!));
        Assert.True(provider.OpenRun(a.RunId!), "engine " + engine + ": could not open A's gate");
        await WaitUntil(() => provider.StartedRunIds.Contains(c.RunId!));
        var cStartedAfterRelease = provider.StartedRunIds.Contains(c.RunId!);
        provider.OpenRun(b.RunId!); provider.OpenRun(c.RunId!);
        await Task.Delay(100); // let the two segments drain

        return (
            new[] { a.Disposition.ToString(), b.Disposition.ToString(), c.Disposition.ToString() },
            lanes.AcquireCalls, owned, queue, cQueuedNotStarted, cStartedAfterRelease,
            provider.EmitsUsage);
    }

    [Fact]
    public async Task ChatCompletionsEngine_SameOwnershipGuarantees_AsResponsesEngine()
    {
        var resp = await WireScenario("responses");
        var chat = await WireScenario("chat");

        // The engines are observably DIFFERENT: only the Responses engine carried
        // a usage event (response IDs/usage); the Chat engine has none.
        Assert.True(resp.emitsUsage, "the Responses fake must carry response usage");
        Assert.False(chat.emitsUsage, "the Chat fake must carry no response usage (no IDs, no cache)");

        // IDENTICAL ownership guarantees across wires: the scheduler never sees
        // the wire, so who is admitted/queued/executing is wire-independent.
        Assert.Equal(resp.disp, chat.disp);
        Assert.Equal(new[] { nameof(RunDisposition.Admitted), nameof(RunDisposition.Admitted), nameof(RunDisposition.Queued) }, resp.disp);
        Assert.Equal(resp.acquire, chat.acquire);
        Assert.Equal(resp.owned, chat.owned);
        Assert.Equal(resp.queue, chat.queue);
        Assert.True(resp.cQueuedNotStarted && chat.cQueuedNotStarted, "the queued run must not execute before its lane, on either wire");
        Assert.True(resp.cStartedAfterRelease && chat.cStartedAfterRelease, "the queued run must start after a release, on either wire");
    }

    // ---- §15.B: the runtime re-validates the lane permit before EVERY model
    //      call. A valid (still-owned) permit passes through; a stale permit
    //      (lane released, generation changed) fails closed BEFORE any network
    //      I/O — the provider never sees the run.
    [Fact]
    public async Task LanePermit_Stale_RefusesInference_BeforeNetwork()
    {
        var scheduler = new NetPI.Lanes.LaneScheduler("gen-1", new NullLogger());
        scheduler.RegisterPool("pool-1", "dep-1", "m-1", LaneCapacityMode.Manual, 1, true);
        var provider = new GateProvider();
        var ctx = new AdmCtx();
        ctx.Add("provider", provider);
        ctx.Add("sessions", new NoopStore());
        ctx.Add("lanes", scheduler);
        var runtime = new AgentRuntime(ctx);

        // Admit a pooled run to get a valid token.
        var token = await AcquireToken(scheduler);

        // Release the lane → the token is no longer owned → TryValidatePermit fails.
        await scheduler.ReleaseAsync(token);

        // Now drive the runtime with the stale token: it must refuse inference.
        var result = await runtime.RunAsync(new AgentRunOptions
        {
            SessionId = "s1",
            ModelId  = "m-1",
            RunId    = "run-stale",
            LanePermit = token,
        }, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("lane permit invalid", result.Note, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("run-stale", provider.StartedRunIds);
    }


    private static async Task<LaneOwnershipToken> AcquireToken(NetPI.Lanes.LaneScheduler scheduler)
    {
        var entry = new LaneQueueEntry("a-1", "pool-1", "dep-1", 0, "agent-1", "run-1", "s1", "test", DateTimeOffset.UtcNow);
        var result = await scheduler.AcquireAsync(entry);
        Assert.NotNull(result.Token);
        return result.Token!;
    }
}