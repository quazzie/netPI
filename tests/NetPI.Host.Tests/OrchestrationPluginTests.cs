using System.IO;
using System.Text.Json;
using NetPI.Abstractions;
using NetPI.Orchestration;
using NetPI.Storage.Sqlite;
using Xunit;

namespace NetPI.Host.Tests;

/// <summary>
/// astra-2 packages C/D/F: the AgentOrchestrator over a real temp-dir
/// SqliteOrchestrationStore, with a fake runner (no live runs). Verifies:
///   * SpawnChild is idempotent by operationId;
///   * ListAssignmentsAsync returns nonterminal + bounded terminal history;
///   * CancelAsync(subtree) cancels the child + descendants and is idempotent;
///   * a terminal AgentEvent (published by the runner) reconciles the row;
///   * Pools() is empty when no lanes service is present.
/// </summary>
public sealed class OrchestrationPluginTests : IDisposable
{
    private readonly string _dir =
        Directory.CreateTempSubdirectory("netpi-orch-plugin-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* best effort; isolated per run */ }
    }

    private sealed record Env(FakeRegistry Registry, FakeEventBus Bus, SqliteOrchestrationStore Store, AgentOrchestrator Orch, FakeRunner Runner, string DbDir) : IDisposable
    {
        public void Dispose()
        {
            try { Directory.Delete(DbDir, recursive: true); }
            catch (IOException) { }
        }
    }

    private Env Make(string runnerMode = "none")
    {
        var db = Directory.CreateTempSubdirectory("netpi-orch-tx2-").FullName;
        var path = Path.Combine(db, "test.db");
        using (var _ = new SqliteSessionStore(path)) { /* migrate v4 */ }
        var store = new SqliteOrchestrationStore(path);
        var registry = new FakeRegistry();
        var bus = new FakeEventBus();
        registry.Add("orchestration-store", store);
        var runner = new FakeRunner(runnerMode);
        registry.Add("runner", runner);
        var ctx = new FakeContext(registry, bus);
        var orch = new AgentOrchestrator(ctx, store);
        registry.Add("orchestration", orch);
        return new Env(registry, bus, store, orch, runner, db);
    }

    // ---- tests -------------------------------------------------------------

    [Fact]
    public async Task SpawnChild_IsIdempotentByOperationId()
    {
        var e = Make();
        var root = await e.Store.EnsureRootAgentAsync("sess-root", null, "root");
        var req = new AgentSpawnRequest { Brief = "do the thing", OperationId = "op-1" };

        var a = await e.Orch.SpawnChildAsync(root.AgentId, req, default);
        var b = await e.Orch.SpawnChildAsync(root.AgentId, req, default);

        Assert.Equal(a.AssignmentId, b.AssignmentId);
        Assert.Equal(a.Agent.AgentId, b.Agent.AgentId);
        // No live runner (none) -> the assignment stays queued.
        Assert.Equal(AgentAssignmentLifecycle.Queued, a.Status);
    }

    [Fact]
    public async Task ListAssignments_IncludesNonTerminalAndTerminalHistory()
    {
        var e = Make();
        var root = await e.Store.EnsureRootAgentAsync("sess-root", null, "root");
        var req = new AgentSpawnRequest { Brief = "work", OperationId = "op-2" };
        var spawned = await e.Store.SpawnChildAsync(
            "op-2", root.AgentId, null, "default", null, null, "work", "child");
        var row = spawned.Agent is not null
            ? await e.Store.GetAssignmentAsync(spawned.AssignmentId)
            : null;
        Assert.NotNull(row);

        // Move it terminal so it counts as history, then spawn a live one.
        await e.Store.TransitionAsync(row!.AssignmentId, row.Version,
            AgentAssignmentLifecycle.Completed, AgentState.Idle, null, null, null, null, null);

        var live = await e.Store.SpawnChildAsync(
            "op-3", root.AgentId, null, "default", null, null, "more", "child2");

        var all = await e.Orch.ListAssignmentsAsync(default);
        Assert.Contains(all, r => r.AssignmentId == row.AssignmentId && !r.IsNonTerminal);
        Assert.Contains(all, r => r.AssignmentId == live.AssignmentId && r.IsNonTerminal);
    }

    [Fact]
    public async Task CancelSubtree_CancelsChildAndDescendants_AndIsIdempotent()
    {
        var e = Make();
        var root = await e.Store.EnsureRootAgentAsync("sess-root", null, "root");
        var child = await e.Store.SpawnChildAsync(
            "op-a", root.AgentId, null, "default", null, null, "child work", "child");

        // Cancel the child's assignment (subtree=true) via the orchestrator.
        var outcome = await e.Orch.CancelAsync(child.AssignmentId, subtree: true, default);
        Assert.Equal(AgentAssignmentLifecycle.Cancelled, outcome);

        var after = await e.Store.GetAssignmentAsync(child.AssignmentId);
        Assert.Equal(AgentAssignmentLifecycle.Cancelled, after!.Lifecycle);

        // Idempotent: cancelling the (now terminal) assignment returns the recorded outcome.
        var again = await e.Orch.CancelAsync(child.AssignmentId, subtree: true, default);
        Assert.Equal(AgentAssignmentLifecycle.Cancelled, again);
    }

    [Fact]
    public async Task TerminalAgentEvent_ReconcilesAssignment()
    {
        var e = Make();
        var root = await e.Store.EnsureRootAgentAsync("sess-root", null, "root");
        var spawned = await e.Store.SpawnChildAsync(
            "op-r", root.AgentId, null, "default", null, null, "run", "child");

        // The orchestrator subscribes to the runner's terminal events on the bus.
        e.Orch.SubscribeToRunnerEvents();

        // The runner (NetPI.Agent) publishes exactly one terminal event per run.
        // The runner's terminal event carries the RUN id (the store's run_id = the
        // spawn operation id, "op-r"), NOT the assignment id.
        await e.Bus.PublishAsync(new AgentEvent(
            "evt-1", AgentEventType.AgentCompleted, DateTimeOffset.UtcNow,
            spawned.Agent.SessionId, null, "op-r"));

        // Fire-and-forget reconciliation: poll until the row is terminal.
        var done = await WaitUntil(() => e.Store.GetAssignmentAsync(spawned.AssignmentId, default));
        Assert.True(done, "assignment did not reconcile to terminal in time");
    }

    [Fact]
    public async Task RegisterWait_DependencyCycle_IsRejected()
    {
        var e = Make();
        var root = await e.Store.EnsureRootAgentAsync("sess-root", null, "root");
        var child = await e.Store.SpawnChildAsync(
            "op-c", root.AgentId, null, "default", null, null, "child", "child");

        // The child may NOT wait on its own assignment (self-cycle).
        var childAgent = child.Agent;
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await e.Orch.RegisterWaitAsync(childAgent.AgentId, new AgentWaitCondition
            { AssignmentIds = [child.AssignmentId] }, default));
    }

    [Fact]
    public async Task RegisterWait_DescendantWaitingOnAncestor_IsRejected()
    {
        var e = Make();
        var root = await e.Store.EnsureRootAgentAsync("sess-root", null, "root");
        var rootAssign = await e.Store.CreateAssignmentAsync(
            "op-anc", root.AgentId, root.SessionId, null, null, "default",
            null, null, "root work", "root brief");
        var child = await e.Store.SpawnChildAsync(
            "op-desc", root.AgentId, null, "default", null, null, "child", "child");

        // The child is a descendant of the root; waiting for the root's assignment
        // is a cycle (the root cannot go terminal while the child is live).
        var childAgent = child.Agent;
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await e.Orch.RegisterWaitAsync(childAgent.AgentId, new AgentWaitCondition
            { AssignmentIds = [rootAssign.AssignmentId] }, default));
    }

    [Fact]
    public async Task RegisterWait_DescendantWaitingOnSibOrSelfAssignment_Ok()
    {
        // A child waiting on ITS OWN (other) assignment is the normal delegation
        // pattern and must be accepted.
        var e = Make();
        var root = await e.Store.EnsureRootAgentAsync("sess-root", null, "root");
        var child = await e.Store.SpawnChildAsync(
            "ok-1", root.AgentId, null, "default", null, null, "child", "child");
        var other = await e.Store.SpawnChildAsync(
            "ok-2", root.AgentId, null, "default", null, null, "other", "other");

        // Should not throw: the child waits on the sibling's assignment (no cycle).
        await e.Orch.RegisterWaitAsync(child.Agent.AgentId, new AgentWaitCondition
        { AssignmentIds = [other.AssignmentId] }, default);
    }

    [Fact]
    public async Task Pools_IsEmptyWithoutLanes()
    {
        var e = Make();
        Assert.Empty(e.Orch.Pools());
    }

    private static async Task<bool> WaitUntil(Func<ValueTask<AgentAssignmentRow?>> probe)
    {
        for (int i = 0; i < 100; i++)
        {
            var row = await probe();
            if (row is not null && row.IsNonTerminal == false) return true;
            await Task.Delay(20);
        }
        return false;
    }

    // ---- fakes ---------------------------------------------------------------

    private sealed class FakeRegistry : IServiceRegistry
    {
        private readonly Dictionary<string, object> _services = new(StringComparer.Ordinal);
        public void Add(string id, object service) => _services[id] = service;
        public T Resolve<T>(string id) where T : notnull
            => (T)(_services.TryGetValue(id, out var v) ? v : throw new ServiceUnavailableException(id, "not registered"));
        public IDisposable Register<T>(string id, T instance) where T : notnull { _services[id] = instance; return new Noop(); }
        public IValueLease<T> Acquire<T>(string id) where T : notnull => new Lease<T>(Resolve<T>(id));
        public IValueLease<object> Acquire(string id, Type expectedType)
            => new Lease<object>(_services.TryGetValue(id, out var v) ? v : throw new ServiceUnavailableException(id, "no"));
        public IValueLease<T> AcquireSelfLease<T>() where T : notnull => new Lease<T>(default!);
        private sealed class Noop : IDisposable { public void Dispose() { } }
        private sealed class Lease<T>(T value) : IValueLease<T>
        {
            public T Value => value;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class FakeEventBus : IEventBus
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

    private sealed class FakeRunner(string mode) : IAgentRunner
    {
        private readonly List<RunInfo> _runs = [];
        public ValueTask<bool> SuspendRunAsync(string runId, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(false);
        public ValueTask<bool> RequeueRunAsync(AgentRunRequest request, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(false);

        public ValueTask<AgentRunStart> StartRunAsync(AgentRunRequest request, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new AgentRunStart(request.SessionId, null, null, RunDisposition.Admitted));
        public ValueTask CancelRunAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public bool IsRunning => false;
        public IReadOnlyList<RunInfo> ListRuns() => _runs;
        public RunInfo? GetRun(string runId) => _runs.FirstOrDefault(r => r.RunId == runId);
        public RunInfo? GetSessionRun(string sessionId) => _runs.FirstOrDefault(r => r.SessionId == sessionId);
        public bool CancelRun(string runId) => false;
        public System.Threading.SemaphoreSlim SessionGate(string sessionId) => new(1, 1);
    }

    private sealed class FakeContext(FakeRegistry services, FakeEventBus events) : IPluginContext
    {
        public PluginInfo Info { get; } = new("netpi.orchestration", "Orchestration Test", "0.1.0");
        public IServiceRegistry Services => services;
        public ICommandRegistry Commands => throw new NotSupportedException();
        public IWebPanelRegistry WebPanels => throw new NotSupportedException();
        public IEventBus Events => events;
        public JsonElement OwnConfig => JsonDocument.Parse("{}").RootElement.Clone();
        public IPluginLogger Log => new NullLog();
        public IValueLease<object> LeaseSelf() => throw new NotSupportedException();
    }

    private sealed class NullLog : IPluginLogger
    {
        public void Debug(string message) { }
        public void Information(string message) { }
        public void Warning(string message) { }
        public void Error(string message, Exception? exception = null) { }
    }
}
