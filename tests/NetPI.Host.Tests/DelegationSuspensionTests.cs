using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NetPI.Abstractions;
using NetPI.Agent;
using NetPI.Lanes;
using NetPI.Orchestration;
using NetPI.Storage.Sqlite;
using Xunit;

namespace NetPI.Host.Tests;

/// <summary>
/// astra-2 §6.2/§6.3/§16: delegation with suspension. Two harnesses:
///   * E2E — real AgentRunner + LaneScheduler + gated provider: lane capacity 1,
///     the parent delegates and fully quiesces, the child runs to terminal on
///     the released lane, the parent resumes (same run id) with a bounded result.
///   * Store-level — real SqliteOrchestrationStore + fake runner: the durable wake
///     is retained and consumed exactly once; duplicate delegate is idempotent;
///     a message to a suspended parent survives resume.
/// </summary>
public sealed class DelegationSuspensionTests : IDisposable
{
    private readonly List<string> _dirs = [];

    public void Dispose()
    {
        foreach (var d in _dirs)
            try { Directory.Delete(d, recursive: true); }
            catch (IOException) { }
    }

    private string NewDbDir()
    {
        var d = Directory.CreateTempSubdirectory("netpi-deleg-").FullName;
        _dirs.Add(d);
        return d;
    }

    private static async Task WaitUntil(Func<bool> cond, int ms = 20, int max = 300)
    {
        for (var i = 0; i < max && !cond(); i++) await Task.Delay(ms);
    }

    // ---- E2E harness (mirrors RunnerLaneAdmissionTests) ----------------------

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

    /// <summary>
    /// §16 "Provider ignores cancellation temporarily": the model call does NOT
    /// end when its token is cancelled — it only ends when the test releases it.
    /// (A real non-cooperative HTTP stream behaves this way until the socket is
    /// actually torn down.)
    /// </summary>
    private sealed class StickyGateProvider : IModelProvider
    {
        private readonly object _lock = new();
        private TaskCompletionSource? _gate;
        private bool _cancelObserved;
        public bool CancelObserved { get { lock (_lock) return _cancelObserved; } }

        public bool Entered { get { lock (_lock) return _entered; } }
        private bool _entered;
        private readonly List<string> _started = new();
        public IReadOnlyList<string> StartedRunIds { get { lock (_lock) return _started.ToList(); } }
        public async IAsyncEnumerable<ModelEvent> RunAsync(ModelRequest request, CancellationToken cancellationToken)
        {
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_lock) { _gate = tcs; _entered = true; _started.Add(request.RunId ?? "ad-hoc"); }
            yield return new ModelStarted("model");
            using var reg = cancellationToken.Register(() => { lock (_lock) _cancelObserved = true; });
            await tcs.Task; // never ends on cancellation
            yield return new ModelCompleted(new AgentMessage("a", MessageRole.Assistant,
                [new TextPart("done")], DateTimeOffset.UtcNow));
        }

        public bool Release()
        {
            lock (_lock) return _gate?.TrySetResult() ?? false;
        }
    }

    private sealed class FakePolicy : IDeploymentPolicySource
    {
        private readonly Dictionary<string, DeploymentPolicy> _map;
        public FakePolicy(params DeploymentPolicy[] policies) =>
            _map = policies.ToDictionary(p => p.ModelId, StringComparer.Ordinal);
        public DeploymentPolicy? PolicyFor(string modelId) => _map.TryGetValue(modelId, out var p) ? p : null;
    }

    private sealed class DispatchBus : IEventBus
    {
        private readonly List<(Type Type, object Handler)> _subs = [];
        public IDisposable Subscribe<TEvent>(NetPI.Abstractions.EventHandler<TEvent> handler, EventSubscriptionOptions? options = null)
        {
            _subs.Add((typeof(TEvent), handler));
            return new Noop();
        }
        public ValueTask PublishAsync<TEvent>(TEvent @event, CancellationToken cancellationToken = default)
        {
            foreach (var entry in _subs.Where(s => s.Type == typeof(TEvent)))
            {
                var cast = (NetPI.Abstractions.EventHandler<TEvent>)entry.Item2;
                cast(@event);
            }
            return ValueTask.CompletedTask;
        }
        private sealed class Noop : IDisposable { public void Dispose() { } }
    }

    private sealed class NoopStore : ISessionStore
    {
        public ValueTask<SessionInfo> CreateAsync(string? workspacePath, CancellationToken ct = default)
            => ValueTask.FromResult(new SessionInfo(Guid.NewGuid().ToString("n"), workspacePath,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 0, null));
        public ValueTask<SessionInfo?> GetAsync(string id, CancellationToken ct = default)
            => ValueTask.FromResult<SessionInfo?>(null);
        public ValueTask AppendAsync(SessionEntry entry, CancellationToken ct = default)
            => ValueTask.CompletedTask;
        public ValueTask<IReadOnlyList<SessionInfo>> ListAsync(int count = 50, int offset = 0, CancellationToken ct = default)
            => ValueTask.FromResult<IReadOnlyList<SessionInfo>>(Array.Empty<SessionInfo>());
        public ValueTask<IReadOnlyList<SessionInfo>> SearchAsync(string query, int count = 100, int offset = 0, CancellationToken ct = default)
            => ListAsync(count, offset, ct);
        public ValueTask<int> SearchCountAsync(string query, int count = 100, CancellationToken ct = default)
            => ValueTask.FromResult(0);
        public ValueTask<int> CountAsync(CancellationToken ct = default) => ValueTask.FromResult(0);
        public ValueTask RenameAsync(string id, string title, CancellationToken ct = default)
            => ValueTask.CompletedTask;
        public ValueTask DeleteAsync(string id, CancellationToken ct = default) => ValueTask.CompletedTask;
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

    private sealed class InMemCtx(IEventBus? bus = null) : IPluginContext
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
        public IEventBus Events { get; } = bus ?? new DispatchBus();
        public JsonElement OwnConfig => JsonDocument.Parse("{}").RootElement;
        public IPluginLogger Log => new NullLog();
        public IValueLease<object> LeaseSelf() => new NoopLease();
        private sealed class NoopLog : IPluginLogger { public void Debug(string m) { } public void Information(string m) { } public void Warning(string m) { } public void Error(string m, Exception? e = null) { } }
        private sealed class NoopLease : IValueLease<object> { public object Value => this; public void Dispose() { } public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; } }
        public void Add(string id, object service) => ((Reg)Services).Put(id, service);
    }

    private sealed class NullLog : IPluginLogger
    {
        public void Debug(string m) { }
        public void Information(string m) { }
        public void Warning(string m) { }
        public void Error(string m, Exception? e = null) { }
    }

    private sealed record E2E(LaneScheduler Lanes, GateProvider Provider, AgentRunner Runner,
        SqliteOrchestrationStore Store, AgentOrchestrator Orch, InMemCtx Ctx);

    private E2E MakeE2E(int capacity)
    {
        var dir = NewDbDir();
        var path = Path.Combine(dir, "test.db");
        using (var _ = new SqliteSessionStore(path)) { /* migrate */ }
        var store = new SqliteOrchestrationStore(path);
        var bus = new DispatchBus();
        var lanes = new LaneScheduler("gen-1", new NullLog());
        lanes.RegisterPool("pool-1", "dep-1", "m-1", LaneCapacityMode.Manual, capacity, true);
        var policy = new FakePolicy(new DeploymentPolicy("m-1", DeploymentExecutionMode.Pooled, "pool-1", "dep-1"));
        var provider = new GateProvider();
        var ctx = new InMemCtx(bus);
        ctx.Add("provider", provider);
        ctx.Add("sessions", new NoopStore());
        ctx.Add("deployments", policy);
        ctx.Add("lanes", lanes);
        ctx.Add("orchestration-store", store);
        var runner = new AgentRunner(new AgentRuntime(ctx), ctx, maxConcurrentRuns: 8);
        ctx.Add("runner", runner);
        var orch = new AgentOrchestrator(ctx, store);
        return new E2E(lanes, provider, runner, store, orch, ctx);
    }

    // ---- §16: capacity 1 — parent delegates, fully quiesces, child runs, parent resumes
    [Fact]
    public async Task Capacity1_Delegate_ParentQuiesces_ChildRuns_ParentResumesWithSameRunId()
    {
        var e = MakeE2E(capacity: 1);
        var root = await e.Store.EnsureRootAgentAsync("sess-parent", null, "parent");
        await e.Store.CreateAssignmentAsync("parent-op", root.AgentId, "sess-parent",
            null, null, "m-1", "pool-1", "dep-1", "parent work", "parent brief");
        e.Orch.SubscribeToRunnerEvents();

        var start = await e.Runner.StartRunAsync(new AgentRunRequest(
            "sess-parent", null, "m-1", "parent work", RunId: "parent-op"));
        Assert.Equal(RunDisposition.Admitted, start.Disposition);
        await WaitUntil(() => e.Runner.GetRun("parent-op") is { Outcome: RunState.Running });
        Assert.True(e.Runner.GetRun("parent-op")!.Outcome == RunState.Running, "parent run must be Running");

        // The parent's segment delegates a child. The pool is full (parent holds the
        // only lane), so the child queues; the parent's suspension releases the lane.
        var result = await e.Orch.DelegateAsync(root.AgentId,
            new AgentSpawnRequest { Brief = "child work", OperationId = "child-op" }, default);

        // (a) parent fully quiesces: Suspended (not Cancelled/Failed), assignment Waiting.
        Assert.Equal(RunState.Suspended, e.Runner.GetRun("parent-op")!.Outcome);
        Assert.Equal(AgentAssignmentLifecycle.Waiting,
            (await e.Store.GetAssignmentAsync(result.ParentAssignmentId))!.Lifecycle);

        // (b) the child is admitted for the released lane and runs (no deadlock).
        var childAssign = await e.Store.GetByRunIdAsync("child-op", default);
        Assert.NotNull(childAssign);
        await WaitUntil(() => e.Runner.GetRun("child-op") is { Outcome: RunState.Running });
        Assert.True(e.Runner.GetRun("child-op")!.Outcome == RunState.Running, "child run must be Running");
        Assert.Contains("child-op", e.Provider.StartedRunIds);

        // §16: with capacity 1 there is never more than one owner.
        Assert.Equal(1, e.Runner.ListRuns().Count(r => r.Outcome == RunState.Running));

        // (c) the child finishes; the durable wake resumes the parent on the SAME run id.
        Assert.True(e.Provider.OpenRun("child-op"), "child gate must be openable");
        await WaitUntil(() => e.Runner.GetRun("parent-op") is { Outcome: RunState.Running });
        Assert.True(e.Runner.GetRun("parent-op")!.Outcome == RunState.Running,
            "parent must be re-admitted (Running) after the child finished");
        Assert.Equal(AgentAssignmentLifecycle.Running,
            (await e.Store.GetAssignmentAsync(result.ParentAssignmentId))!.Lifecycle);
        Assert.Equal(AgentAssignmentLifecycle.Completed, (await e.Store.GetAssignmentAsync(result.ChildAssignmentId, default))!.Lifecycle);

        // (d) the wake was consumed exactly once — no satisfied wait remains.
        Assert.Empty(await e.Store.ListSatisfiedWaitsAsync(root.AgentId, default));
    }

    // ---- §16: two parents delegate at once — two atomic handoffs, no deadlock
    [Fact]
    public async Task Capacity2_TwoParentsDelegate_BothChildrenAdmit_BothParentsResume()
    {
        var e = MakeE2E(capacity: 2);
        var parentA = await e.Store.EnsureRootAgentAsync("sess-A", null, "parent A");
        var parentB = await e.Store.EnsureRootAgentAsync("sess-B", null, "parent B");
        await e.Store.CreateAssignmentAsync("parentA-op", parentA.AgentId, "sess-A",
            null, null, "m-1", "pool-1", "dep-1", "A work", "A brief");
        await e.Store.CreateAssignmentAsync("parentB-op", parentB.AgentId, "sess-B",
            null, null, "m-1", "pool-1", "dep-1", "B work", "B brief");
        e.Orch.SubscribeToRunnerEvents();

        var sa = await e.Runner.StartRunAsync(new AgentRunRequest("sess-A", null, "m-1", "A work", RunId: "parentA-op"));
        var sb = await e.Runner.StartRunAsync(new AgentRunRequest("sess-B", null, "m-1", "B work", RunId: "parentB-op"));
        Assert.Equal(RunDisposition.Admitted, sa.Disposition);
        Assert.Equal(RunDisposition.Admitted, sb.Disposition);
        await WaitUntil(() => e.Runner.GetRun("parentA-op") is { Outcome: RunState.Running }
                          && e.Runner.GetRun("parentB-op") is { Outcome: RunState.Running });

        // Both delegate at once: each child queues behind the full pool, each
        // parent's suspension releases one lane; both children must be admitted.
        var rA = await e.Orch.DelegateAsync(parentA.AgentId,
            new AgentSpawnRequest { Brief = "child A", OperationId = "childA-op" }, default);
        var rB = await e.Orch.DelegateAsync(parentB.AgentId,
            new AgentSpawnRequest { Brief = "child B", OperationId = "childB-op" }, default);

        Assert.Equal(RunState.Suspended, e.Runner.GetRun("parentA-op")!.Outcome);
        Assert.Equal(RunState.Suspended, e.Runner.GetRun("parentB-op")!.Outcome);
        Assert.Equal(AgentAssignmentLifecycle.Waiting, (await e.Store.GetAssignmentAsync(rA.ParentAssignmentId))!.Lifecycle);
        Assert.Equal(AgentAssignmentLifecycle.Waiting, (await e.Store.GetAssignmentAsync(rB.ParentAssignmentId))!.Lifecycle);

        await WaitUntil(() => e.Runner.GetRun("childA-op") is { Outcome: RunState.Running }
                          && e.Runner.GetRun("childB-op") is { Outcome: RunState.Running });
        Assert.True(e.Runner.GetRun("childA-op")!.Outcome == RunState.Running, "child A must run");
        Assert.True(e.Runner.GetRun("childB-op")!.Outcome == RunState.Running, "child B must run");

        // §16: never more than two owners at once (capacity 2).
        var runningCount = e.Runner.ListRuns().Count(r => r.Outcome == RunState.Running);
        Assert.True(runningCount <= 2, $"never more than two owners, saw {runningCount}");

        // Both children finish; both parents resume on their own run ids.
        e.Provider.OpenRun("childA-op");
        e.Provider.OpenRun("childB-op");
        await WaitUntil(() => e.Runner.GetRun("parentA-op") is { Outcome: RunState.Running }
                          && e.Runner.GetRun("parentB-op") is { Outcome: RunState.Running });
        Assert.True(e.Runner.GetRun("parentA-op")!.Outcome == RunState.Running, "parent A must resume");
        Assert.True(e.Runner.GetRun("parentB-op")!.Outcome == RunState.Running, "parent B must resume");
        Assert.Empty(await e.Store.ListSatisfiedWaitsAsync(parentA.AgentId, default));
        Assert.Empty(await e.Store.ListSatisfiedWaitsAsync(parentB.AgentId, default));
    }

    // ---- store-level harness (fake runner over the real store) ---------------

    private sealed class FakeRunner : IAgentRunner
    {
        public List<string> SuspendedRunIds = [];
        public List<AgentRunRequest> Requeued = [];
        public bool SuspendResult = true;
        public bool RequeueResult = true;
        private readonly List<RunInfo> _runs = [];

        public ValueTask<bool> SuspendRunAsync(string runId, CancellationToken ct = default)
        {
            SuspendedRunIds.Add(runId);
            if (SuspendResult)
            {
                var i = _runs.FindIndex(r => r.RunId == runId);
                if (i >= 0) _runs[i] = _runs[i] with { Outcome = RunState.Suspended };
            }
            return ValueTask.FromResult(SuspendResult);
        }
        public ValueTask<bool> RequeueRunAsync(AgentRunRequest request, CancellationToken ct = default)
        {
            Requeued.Add(request);
            if (RequeueResult && request.RunId is { } id)
            {
                var i = _runs.FindIndex(r => r.RunId == id);
                if (i >= 0) _runs[i] = _runs[i] with { Outcome = RunState.Running };
            }
            return ValueTask.FromResult(RequeueResult);
        }
        public ValueTask<AgentRunStart> StartRunAsync(AgentRunRequest request, CancellationToken ct = default)
            => ValueTask.FromResult(new AgentRunStart(request.SessionId, null, request.RunId, RunDisposition.Admitted));
        public ValueTask CancelRunAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
        public bool IsRunning => false;
        public IReadOnlyList<RunInfo> ListRuns() => _runs;
        public RunInfo? GetRun(string runId) => _runs.FirstOrDefault(r => r.RunId == runId);
        public RunInfo? GetSessionRun(string sessionId) => _runs.FirstOrDefault(r => r.SessionId == sessionId);
        public bool CancelRun(string runId) => false;
        public ValueTask<bool> CancelQueuedRun(string runId, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(false);
        public System.Threading.SemaphoreSlim SessionGate(string sessionId) => new(1, 1);

        public void SeedRun(string runId, string sessionId) =>
            _runs.Add(new RunInfo(runId, sessionId, "m-1", AgentState.CallingModel,
                DateTimeOffset.UtcNow, null, RunState.Running));
    }

    private sealed record StoreEnv(FakeRunner Runner, SqliteOrchestrationStore Store,
        AgentOrchestrator Orch, DispatchBus Bus);

    private StoreEnv MakeStore()
    {
        var dir = NewDbDir();
        var path = Path.Combine(dir, "test.db");
        using (var _ = new SqliteSessionStore(path)) { }
        var store = new SqliteOrchestrationStore(path);
        var bus = new DispatchBus();
        var runner = new FakeRunner();
        var ctx = new InMemCtx(bus);
        ctx.Add("orchestration-store", store);
        ctx.Add("runner", runner);
        var orch = new AgentOrchestrator(ctx, store);
        return new StoreEnv(runner, store, orch, bus);
    }

    // ---- §16: child finishes before the parent's suspension unwinds —
    //      the durable wake is retained and consumed exactly once.
    [Fact]
    public async Task DurableWake_RetainedAcrossChildTerminal_ConsumedExactlyOnce()
    {
        var e = MakeStore();
        var root = await e.Store.EnsureRootAgentAsync("sess-parent", null, "parent");
        await e.Store.CreateAssignmentAsync("parent-op", root.AgentId, "sess-parent",
            null, null, "m-1", "pool-1", "dep-1", "parent work", "parent brief");
        e.Runner.SeedRun("parent-op", "sess-parent");
        e.Orch.SubscribeToRunnerEvents();

        var result = await e.Orch.DelegateAsync(root.AgentId,
            new AgentSpawnRequest { Brief = "child work", OperationId = "child-op" }, default);
        Assert.Single(e.Runner.SuspendedRunIds);
        Assert.Contains("parent-op", e.Runner.SuspendedRunIds);

        // The child goes terminal (the runner would publish exactly one terminal
        // AgentEvent with the run id; the bus delivers it to the reconciler).
        var child = await e.Store.GetByRunIdAsync("child-op", default);
        Assert.NotNull(child);
        e.Bus.PublishAsync(new AgentEvent("evt-child", AgentEventType.AgentCompleted,
            DateTimeOffset.UtcNow, child!.SessionId, null, "child-op"));
        await WaitUntil(() => e.Runner.Requeued.Count > 0);

        // Resume happened ONCE, with the SAME run id, carrying a bounded summary.
        Assert.Single(e.Runner.Requeued);
        Assert.Equal("parent-op", e.Runner.Requeued[0].RunId);
        Assert.True(e.Runner.Requeued[0].Text.Contains("child work"),
            $"resume brief must carry the bounded child result, got: {e.Runner.Requeued[0].Text}");
        Assert.True(e.Runner.Requeued[0].Text.Contains("completed"),
            "resume brief must name the child's outcome");
        Assert.True(e.Runner.Requeued[0].Text.Length < 2000,
            "resume brief is bounded (no transcript)");

        // Consumed: the satisfied wait is gone and a second consumer pass resumes nothing.
        Assert.Empty(await e.Store.ListSatisfiedWaitsAsync(root.AgentId, default));
        Assert.Equal(0, await e.Orch.ConsumeSatisfiedWaitsAsync(root.AgentId, default));
        Assert.Single(e.Runner.Requeued); // still exactly one resume
    }

    // ---- §16: duplicate delegate operation is idempotent (no double suspend/wait)
    [Fact]
    public async Task DuplicateDelegate_SameOperationId_ReturnsOriginalChild_SuspendsOnce()
    {
        var e = MakeStore();
        var root = await e.Store.EnsureRootAgentAsync("sess-parent", null, "parent");
        await e.Store.CreateAssignmentAsync("parent-op", root.AgentId, "sess-parent",
            null, null, "m-1", "pool-1", "dep-1", "parent work", "parent brief");
        e.Runner.SeedRun("parent-op", "sess-parent");

        var first = await e.Orch.DelegateAsync(root.AgentId,
            new AgentSpawnRequest { Brief = "child work", OperationId = "child-op" }, default);
        var again = await e.Orch.DelegateAsync(root.AgentId,
            new AgentSpawnRequest { Brief = "child work", OperationId = "child-op" }, default);

        Assert.Equal(first.ChildAssignmentId, again.ChildAssignmentId);
        Assert.Equal(first.ChildAgent.AgentId, again.ChildAgent.AgentId);
        Assert.Equal("idempotent replay", again.Reason);
        Assert.Single(e.Runner.SuspendedRunIds); // the parent quiesced exactly once
        Assert.Empty(e.Runner.Requeued);
    }

    // ---- §16: a direct-cloud coordinator delegates two local workers — the
    //      coordinator holds NO local lane (direct deployment); the children
    //      pin the LOCAL pooled model and need ordinary local admission.
    [Fact]
    public async Task CloudCoordinator_DelegatesTwoLocalWorkers_ChildrenUseLocalPooledModel()
    {
        var e = MakeE2E(capacity: 2);
        var cloudModel = "cloud-m";
        var localModel = "m-1"; // the pool's model
        var policy = new FakePolicy(
            new DeploymentPolicy("m-1", DeploymentExecutionMode.Pooled, "pool-1", "dep-1"),
            new DeploymentPolicy("cloud-m", DeploymentExecutionMode.DirectCloud, null, "cloud"));
        // Re-register with both policies (the harness created one with m-1 only).
        e.Ctx.Add("deployments", policy);

        var coord = await e.Store.EnsureRootAgentAsync("sess-cloud", null, "coordinator");
        await e.Store.CreateAssignmentAsync("coord-op", coord.AgentId, "sess-cloud",
            null, null, cloudModel, null, "cloud", "coordinator work", default);
        e.Orch.SubscribeToRunnerEvents();

        // The coordinator runs DIRECT (no lane): it admits immediately, holds nothing.
        var start = await e.Runner.StartRunAsync(new AgentRunRequest(
            "sess-cloud", null, cloudModel, "coordinator work", RunId: "coord-op"));
        Assert.Equal(RunDisposition.Admitted, start.Disposition);
        await WaitUntil(() => e.Runner.GetRun("coord-op") is { Outcome: RunState.Running });
        var coordRow = await e.Store.GetByRunIdAsync("coord-op", default);
        Assert.Null(coordRow!.LaneId); // direct: never held a local lane

        // Delegate two local workers — the child model is EXPLICIT (the local
        // pooled model), never inherited from the cloud parent.
        var rA = await e.Orch.DelegateAsync(coord.AgentId, new AgentSpawnRequest
        {
            Brief = "local A", OperationId = "localA-op",
            PoolId = "pool-1", DeploymentId = "dep-1", ModelId = localModel,
        }, default);
        var rB = await e.Orch.DelegateAsync(coord.AgentId, new AgentSpawnRequest
        {
            Brief = "local B", OperationId = "localB-op",
            PoolId = "pool-1", DeploymentId = "dep-1", ModelId = localModel,
        }, default);

        // The coordinator quiesced; it STILL holds no lane (direct deployments
        // never acquire one — the children got their own via ordinary admission).
        Assert.Equal(RunState.Suspended, e.Runner.GetRun("coord-op")!.Outcome);

        var childA = await e.Store.GetByRunIdAsync("localA-op", default);
        var childB = await e.Store.GetByRunIdAsync("localB-op", default);
        Assert.NotNull(childA); Assert.NotNull(childB);
        // Children pinned to the LOCAL pooled model (not the cloud model).
        Assert.Equal(localModel, childA!.ModelId);
        Assert.Equal(localModel, childB!.ModelId);
        Assert.Equal(localModel, (await e.Store.GetAssignmentAsync(rA.ChildAssignmentId, default))!.ModelId);

        // Both children admitted (capacity 2), both running.
        await WaitUntil(() => e.Runner.GetRun("localA-op") is { Outcome: RunState.Running }
                          && e.Runner.GetRun("localB-op") is { Outcome: RunState.Running });
        Assert.True(e.Runner.GetRun("localA-op")!.Outcome == RunState.Running, "local A runs");
        Assert.True(e.Runner.GetRun("localB-op")!.Outcome == RunState.Running, "local B runs");
        Assert.Contains("localA-op", e.Provider.StartedRunIds);
        Assert.Contains("localB-op", e.Provider.StartedRunIds);
    }

    // ---- §16: duplicate send — the same idempotency key returns the same seq,
    //      never a second row.
    [Fact]
    public async Task DuplicateSend_SameIdempotencyKey_ReturnsSameSeq_NoDuplicateRow()
    {
        var e = MakeStore();
        var root = await e.Store.EnsureRootAgentAsync("sess-A", null, "A");
        var other = await e.Store.EnsureRootAgentAsync("sess-B", null, "B");

        var seq1 = await e.Orch.SendMessageAsync(root.AgentId, other.AgentId, "note", "body", "key-1", default);
        var seq2 = await e.Orch.SendMessageAsync(root.AgentId, other.AgentId, "note", "body", "key-1", default);

        Assert.Equal(seq1, seq2); // same recipient sequence, not a fresh row

        var inbox = await e.Orch.DrainMailboxAsync(other.AgentId, 10, default);
        Assert.Single(inbox); // exactly one delivered, despite two sends
    }

    // ---- §16/§6.2: delegation is depth-bounded by the configured max
    [Fact]
    public async Task Delegate_PastMaxDepth_IsRejected()
    {
        // maxDelegationDepth: 1 — a root may delegate once; the CHILD may not.
        var dir = NewDbDir();
        var path = System.IO.Path.Combine(dir, "test.db");
        using (var _ = new SqliteSessionStore(path)) { }
        var store = new SqliteOrchestrationStore(path);
        var bus = new DispatchBus();
        var runner = new FakeRunner();
        var ctx = new InMemCtx(bus);
        ctx.Add("orchestration-store", store);
        ctx.Add("runner", runner);
        var orch = new AgentOrchestrator(ctx, store, maxDelegationDepth: 1);

        var root = await store.EnsureRootAgentAsync("sess-root", null, "root");
        var child = await store.SpawnChildAsync("op-child", root.AgentId, null, "default", null, null, "child", "child");
        var childAgent = child.Agent;
        // SpawnChildAsync already created the child's nonterminal assignment
        // (one per session); the runner's live run carries the child's operation id.
        runner.SeedRun("op-child", childAgent.SessionId);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await orch.DelegateAsync(childAgent.AgentId,
                new AgentSpawnRequest { Brief = "grandchild", OperationId = "grandchild-op" }, default));

        // Nothing was spawned or suspended.
        Assert.Null(await store.GetByRunIdAsync("grandchild-op", default));
        Assert.Empty(runner.SuspendedRunIds);
    }

    // ---- §16: an ordinary message to the suspended parent is retained for resume
    [Fact]
    public async Task MessageToSuspendedParent_IsRetainedAndDeliveredOnResume()
    {
        var e = MakeStore();
        var root = await e.Store.EnsureRootAgentAsync("sess-parent", null, "parent");
        await e.Store.CreateAssignmentAsync("parent-op", root.AgentId, "sess-parent",
            null, null, "m-1", "pool-1", "dep-1", "parent work", "parent brief");
        e.Runner.SeedRun("parent-op", "sess-parent");
        e.Orch.SubscribeToRunnerEvents();

        var result = await e.Orch.DelegateAsync(root.AgentId,
            new AgentSpawnRequest { Brief = "child work", OperationId = "child-op" }, default);
        var child = await e.Store.GetByRunIdAsync("child-op", default);
        Assert.NotNull(child);
        var childAgent = await e.Store.GetAgentAsync(child!.AgentId, default);
        Assert.NotNull(childAgent);

        // The parent is suspended (assignment Waiting): an ordinary message still
        // lands in its mailbox — it is not rejected because the run is quiesced.
        var seq = await e.Orch.SendMessageAsync(childAgent!.AgentId, root.AgentId,
            "note", "hello while suspended", default);
        Assert.True(seq >= 1, "message must be accepted while the parent is suspended");

        // Resume via the child's terminal event; the message is readable afterwards.
        e.Bus.PublishAsync(new AgentEvent("evt-child", AgentEventType.AgentCompleted,
            DateTimeOffset.UtcNow, child.SessionId, null, "child-op"));
        await WaitUntil(() => e.Runner.Requeued.Count > 0);
        Assert.Single(e.Runner.Requeued);

        var inbox = await e.Orch.DrainMailboxAsync(root.AgentId, 10, default);
        Assert.Single(inbox);
        Assert.Equal("hello while suspended", inbox[0].Body);
    }

    // ---- §16: provider ignores cancellation — the lane stays occupied until the
    //      stream truly ends; queued work cannot overlap it.
    [Fact]
    public async Task ProviderIgnoresCancellation_LaneStaysOccupied_QueuedWorkCannotOverlap()
    {
        var e = MakeE2E(capacity: 1);
        var sticky = new StickyGateProvider();
        e.Ctx.Add("provider", sticky); // replace the harness gate provider

        var rootA = await e.Store.EnsureRootAgentAsync("sess-A", null, "A");
        var rootC = await e.Store.EnsureRootAgentAsync("sess-C", null, "C");
        await e.Store.CreateAssignmentAsync("A-op", rootA.AgentId, "sess-A",
            null, null, "m-1", "pool-1", "dep-1", "A work", "A brief");
        await e.Store.CreateAssignmentAsync("C-op", rootC.AgentId, "sess-C",
            null, null, "m-1", "pool-1", "dep-1", "C work", "C brief");

        var startA = await e.Runner.StartRunAsync(new AgentRunRequest(
            "sess-A", null, "m-1", "A work", RunId: "A-op"));
        Assert.Equal(RunDisposition.Admitted, startA.Disposition);
        await WaitUntil(() => e.Runner.GetRun("A-op") is { Outcome: RunState.Running });

        // C is submitted while A runs: capacity 1 → C queues, never starts.
        var startC = await e.Runner.StartRunAsync(new AgentRunRequest(
            "sess-C", null, "m-1", "C work", RunId: "C-op"));
        Assert.Equal(RunDisposition.Queued, startC.Disposition);
        Assert.DoesNotContain("C-op", sticky.StartedRunIds);

        // Cancel A while its provider ignores cancellation: the cancel request is
        // delivered to the provider (it observed it) but the stream does NOT end.
        // The run must be AT THE PROVIDER (stream in flight) when we cancel.
        await WaitUntil(() => sticky.Entered);
        Assert.True(sticky.Entered, "run must reach the provider before cancel");
        e.Runner.CancelRun("A-op");
        await WaitUntil(() => sticky.CancelObserved);
        var a = e.Runner.GetRun("A-op");
        Assert.True(sticky.CancelObserved, $"provider must observe the cancellation (state={a?.Outcome})");
        Assert.True(sticky.CancelObserved, "provider must observe the cancellation");

        // While the non-cooperative stream is still live, the lane stays occupied
        // and C cannot overlap it.
        var running = e.Runner.ListRuns().Count(r => r.Outcome == RunState.Running);
        Assert.True(running >= 1, $"A must still own the lane (running={running})");
        Assert.DoesNotContain("C-op", sticky.StartedRunIds);

        // Only when the stream actually ends does the lane release and C admit.
        sticky.Release();
        // C starts only AFTER A's segment unwinds and releases the lane — wait
        // for C's provider entry specifically (A's terminal state alone would
        // be satisfied before the release is processed).
        await WaitUntil(() => sticky.StartedRunIds.Contains("C-op"), ms: 20, max: 150);
        Assert.Contains("C-op", sticky.StartedRunIds);
    }
}
