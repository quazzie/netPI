using System.Collections.Concurrent;
using NetPI.Abstractions;
using NetPI.Agent;
using Xunit;

namespace NetPI.Host.Tests;

/// <summary>
/// astra-1 D2 (slice 2) — the RUNNER's safe-boundary apply of a pending project
/// change, end to end through the real <see cref="AgentRunner"/> +
/// <see cref="RunnerSafeBoundary"/>: the selection enqueued while a run is
/// in flight is applied ONLY at the run's safe boundary (after the run
/// unwinds, before the session is usable again), commits with the stored
/// snapshot and the SAME operation id (idempotent), fails clean (prior
/// project stays active, row consumed), a later enqueue supersedes an
/// earlier unapplied one, and the per-session gate serializes the apply
/// with a concurrent send.
/// </summary>
public class ProjectPendingApplyTests
{
    private sealed class TestContext : IPluginContext
    {
        private readonly FixedServiceRegistry _services = new();
        private readonly RecordingBus _bus = new();
        public PluginInfo Info => new("test", "test", "1.0.0");
        public IServiceRegistry Services => _services;
        public IEventBus Events => _bus;
        public ICommandRegistry Commands => new NetPI.Host.Services.CommandRegistry();
        public IWebPanelRegistry WebPanels => new NetPI.Host.Services.WebPanelRegistry();
        public System.Text.Json.JsonElement OwnConfig => System.Text.Json.JsonDocument.Parse("{}").RootElement;
        public IPluginLogger Log => new NoopLogger();
        public IValueLease<object> LeaseSelf() => new NoopLease();
        public void Add(string id, object service) => _services.Put(id, service);
        private sealed class NoopLease : IValueLease<object>
        {
            public object Value => this;
            public void Dispose() { }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
        private sealed class NoopLogger : IPluginLogger
        {
            public void Debug(string m) { }
            public void Information(string m) { }
            public void Warning(string m) { }
            public void Error(string m, Exception? e = null) { }
        }
    }

    private sealed class RecordingBus : IEventBus
    {
        private readonly ConcurrentQueue<object> _all = new();
        public IEnumerable<AgentEvent> OfType() => _all.OfType<AgentEvent>();
        public IDisposable Subscribe<TEvent>(NetPI.Abstractions.EventHandler<TEvent> handler,
            EventSubscriptionOptions? options = null) => new Noop();
        public ValueTask PublishAsync<TEvent>(TEvent evt, CancellationToken cancellationToken = default)
        {
            _all.Enqueue(evt);
            return ValueTask.CompletedTask;
        }
        private sealed class Noop : IDisposable { public void Dispose() { } }
    }

    private sealed class FakeProvider : IModelProvider
    {
        public async IAsyncEnumerable<ModelEvent> RunAsync(ModelRequest request, CancellationToken cancellationToken)
        {
            yield return new ModelStarted("model");
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            yield return new ModelCompleted(new AgentMessage("a1", MessageRole.Assistant,
                [new TextPart("ok")], DateTimeOffset.UtcNow));
        }
    }

    private sealed class StalledProvider : IModelProvider
    {
        /// <summary>Emits ModelStarted, then awaits forever until cancelled — keeps a
        /// run live so "mid-batch" assertions have a window.</summary>
        public async IAsyncEnumerable<ModelEvent> RunAsync(ModelRequest request, CancellationToken cancellationToken)
        {
            yield return new ModelStarted("model");
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
    }

    /// <summary>Recording ISessionStore — every SetProjectAsync attempt recorded (op +
    /// resulting project/workspace); the idempotency contract (same OperationId →
    /// original entry, no second commit) is modelled by re-using the first result.</summary>
    private sealed class FakeSessionStore : ISessionStore
    {
        public string? ProjectId = "no-project";
        public string? ProjectWorkspace;
        public bool FailSetProject;
        public readonly List<ProjectChangeRequest> ProjectSets = [];
        public readonly List<SessionEntry> ProjectEntries = [];

        public ValueTask<ProjectChangeResult> SetProjectAsync(ProjectChangeRequest change, CancellationToken ct = default)
        {
            if (FailSetProject)
            {
                ProjectSets.Add(change);
                throw new InvalidOperationException("simulated apply failure (project deleted)");
            }
            ProjectSets.Add(change); // every commit ATTEMPT is recorded (incl. retries)
            // Idempotency (the sqlite UNIQUE(id) contract): the FIRST commit for an
            // operation id persists the entry + switches the session; a RETRY with
            // the same operation id must NOT append a second entry.
            var entry = ProjectEntries.FirstOrDefault(e => e.Id == change.OperationId);
            if (entry is null)
            {
                ProjectId = change.ProjectId;
                ProjectWorkspace = change.Snapshot.WorkspacePath;
                entry = new SessionEntry(change.OperationId, change.SessionId,
                    EntryKind.ProjectContext, null,
                    System.Text.Json.JsonSerializer.SerializeToElement(change),
                    DateTimeOffset.UtcNow);
                ProjectEntries.Add(entry);
            }
            // Same OperationId → the ORIGINAL session/entry (retry path).
            var info = new SessionInfo(change.SessionId, ProjectWorkspace, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 0, null)
            { ProjectId = ProjectId };
            return ValueTask.FromResult(new ProjectChangeResult(info, entry));
        }

        // ---- the rest: no-ops (the runner only appends the user entry) ----
        public ValueTask<SessionInfo> CreateAsync(string? workspacePath, CancellationToken ct = default)
            => ValueTask.FromResult(new SessionInfo("s1", null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 0, null));
        public ValueTask<SessionInfo?> GetAsync(string id, CancellationToken ct = default)
            => ValueTask.FromResult<SessionInfo?>(null);
        public ValueTask AppendAsync(SessionEntry entry, CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask<IReadOnlyList<SessionInfo>> ListAsync(int count = 50, int offset = 0, CancellationToken ct = default)
            => ValueTask.FromResult<IReadOnlyList<SessionInfo>>([]);
        public ValueTask<int> CountAsync(CancellationToken ct = default) => ValueTask.FromResult(0);
        public ValueTask RenameAsync(string id, string title, CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask DeleteAsync(string id, CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask SetModelAsync(string sessionId, string? modelId, string? reasoningLevel, CancellationToken ct = default)
            => ValueTask.CompletedTask;
        public ValueTask SetWorkspaceAsync(string sessionId, string? workspacePath, CancellationToken ct = default)
            => ValueTask.CompletedTask;
        public ValueTask<IReadOnlyList<SessionEntry>> ReadAsync(string sessionId, int offset, int count, CancellationToken ct = default)
            => ValueTask.FromResult<IReadOnlyList<SessionEntry>>([]);
        public ValueTask<IReadOnlyList<SessionEntry>> ReadBeforeAsync(string sessionId, int beforeSequence, int count, CancellationToken ct = default)
            => ValueTask.FromResult<IReadOnlyList<SessionEntry>>([]);
        public ValueTask<SessionEntry?> LatestCompactionAsync(string sessionId, CancellationToken ct = default)
            => ValueTask.FromResult<SessionEntry?>(null);
        public ValueTask<IReadOnlyList<SessionEntry>> ReadAfterAsync(string sessionId, int afterSequence, int count, CancellationToken ct = default)
            => ValueTask.FromResult<IReadOnlyList<SessionEntry>>([]);
        public ValueTask<IReadOnlyList<SessionEntry>> ReadRecentAsync(string sessionId, int count, CancellationToken ct = default)
            => ValueTask.FromResult<IReadOnlyList<SessionEntry>>([]);
    }

    /// <summary>In-memory IPendingProjectChangeStore — one row per session (UPSERT),
    /// exactly the contract the sqlite store implements.</summary>
    private sealed class FakePendingStore : IPendingProjectChangeStore
    {
        public readonly Dictionary<string, PendingProjectChangeInfo> Rows = new(StringComparer.Ordinal);
        public ValueTask<PendingProjectChangeInfo> EnqueueAsync(ProjectChangeRequest change, CancellationToken ct = default)
        {
            var info = new PendingProjectChangeInfo(change.SessionId, change.OperationId, change.ProjectId,
                change.Snapshot.ProjectName, DateTimeOffset.UtcNow, change);
            Rows[change.SessionId] = info;   // later enqueue REPLACES the unapplied one
            return ValueTask.FromResult(info);
        }
        public ValueTask<PendingProjectChangeInfo?> PendingAsync(string sessionId, CancellationToken ct = default)
            => ValueTask.FromResult(Rows.TryGetValue(sessionId, out var row) ? row : null);
        public ValueTask<bool> ClearAsync(string sessionId, string operationId, CancellationToken ct = default)
        {
            if (Rows.TryGetValue(sessionId, out var row) && row.OperationId == operationId)
            {
                Rows.Remove(sessionId);
                return ValueTask.FromResult(true);
            }
            return ValueTask.FromResult(false);
        }
    }

    private static (AgentRunner runner, RecordingBus bus, FakeSessionStore sessions, FakePendingStore pending) New(bool stall = false)
    {
        var ctx = new TestContext();
        var bus = (RecordingBus)ctx.Events;
        var sessions = new FakeSessionStore();
        var pending = new FakePendingStore();
        ctx.Add("provider", stall ? new StalledProvider() : new FakeProvider());
        ctx.Add("sessions", sessions);
        ctx.Add("pending-projects", pending);
        return (new AgentRunner(new AgentRuntime(ctx), ctx), bus, sessions, pending);
    }

    private static AgentRunRequest Req(string session, string text = "hi") =>
        new(session, null, "model", text);

    private static async Task WaitAllRuns(AgentRunner r)
    {
        for (var i = 0; i < 2000; i++)
        {
            if (r.ListRuns().All(x => x.Outcome != RunState.Running)) return;
            await Task.Delay(1);
        }
        Assert.Fail("not all runs reached a terminal outcome in time");
    }

    /// <summary>Gate: wait until the boundary apply for this operation is on the bus
    /// (the runner fires it synchronously in the run's finally, after the terminal
    /// outcome is recorded — so WaitAllRuns alone is not a sufficient gate).</summary>
    private static async Task WaitProjectApplied(RecordingBus bus, string operationId)
    {
        for (var i = 0; i < 2000; i++)
        {
            if (bus.OfType().Any(e => e.Type == AgentEventType.ProjectApplied &&
                e.Payload is not null && e.Payload.Value.TryGetProperty("operationId", out var op) &&
                op.GetString() == operationId))
                return;
            await Task.Delay(1);
        }
        Assert.Fail("no ProjectApplied event for " + operationId);
    }

    private static ProjectContextSnapshot Snap(string id, string name, string ws) =>
        new(id, name, ws, ["/AGENTS.md"], "INSTRUCTIONS-" + name, "hash-" + id, DateTimeOffset.UtcNow);

    private static ProjectChangeRequest Change(string op, string sid, string projectId, string name, string ws) =>
        new(op, sid, projectId, Snap(projectId, name, ws));

    // ---- scenarios -------------------------------------------------------------

    [Fact]
    public async Task EnqueuedWhileRunning_AppliesAtBoundary_ClearsRow_PublishesProjectApplied()
    {
        var (runner, bus, sessions, pending) = New();
        sessions.ProjectId = "old-project";
        sessions.ProjectWorkspace = "C:\\ws\\original";

        var op = Guid.NewGuid().ToString("n");
        // Enqueue the selection (the Web surface does this while a run is active).
        await pending.EnqueueAsync(Change(op, "s1", "new-project", "New", "C:\\ws\\new"));

        var start = await runner.StartRunAsync(Req("s1"));
        Assert.Null(start.Note);

        await WaitAllRuns(runner);
        await WaitProjectApplied(bus, op);

        // Applied: the session points at the NEW project (stored snapshot used —
        // never re-resolved), the row is cleared, the event carries the payload.
        Assert.Equal("new-project", sessions.ProjectId);
        Assert.Equal("C:\\ws\\new", sessions.ProjectWorkspace);
        Assert.Empty(pending.Rows);
        var evt = bus.OfType().First(e => e.Type == AgentEventType.ProjectApplied);
        Assert.Equal("s1", evt.SessionId);
        Assert.Equal(start.RunId, evt.RunId);
        Assert.NotNull(evt.Payload);
        var p = evt.Payload!.Value;
        Assert.Equal(op, p.GetProperty("operationId").GetString());
        Assert.Equal("new-project", p.GetProperty("projectId").GetString());
        Assert.Equal("New", p.GetProperty("projectName").GetString());
    }

    [Fact]
    public async Task FailedApply_PriorProjectStaysActive_RowConsumed_NoProjectApplied()
    {
        var (runner, bus, sessions, pending) = New();
        sessions.ProjectId = "old-project";
        sessions.ProjectWorkspace = "C:\\ws\\original";
        sessions.FailSetProject = true;

        var op = Guid.NewGuid().ToString("n");
        await pending.EnqueueAsync(Change(op, "s1", "new-project", "New", "C:\\ws\\new"));

        var start = await runner.StartRunAsync(Req("s1"));
        Assert.Null(start.Note);
        await WaitAllRuns(runner);

        // The boundary fires in the run's finally (after the outcome is recorded),
        // but a failed apply publishes no ProjectApplied — gate on the attempt.
        for (var i = 0; i < 1000 && sessions.ProjectSets.Count == 0; i++) await Task.Delay(1);

        Assert.Single(sessions.ProjectSets);              // the boundary TRIED
        Assert.Equal("old-project", sessions.ProjectId);  // prior project stays active
        Assert.Equal("C:\\ws\\original", sessions.ProjectWorkspace);
        Assert.Empty(pending.Rows);                       // the dead row is CONSUMED
        Assert.DoesNotContain(bus.OfType(), e => e.Type == AgentEventType.ProjectApplied);
        Assert.Contains(bus.OfType(), e => e.Type == AgentEventType.AgentCompleted);
    }

    [Fact]
    public async Task DuplicateOperationId_IsIdempotent_NoDoubleCommit()
    {
        var (runner, bus, sessions, pending) = New();
        sessions.ProjectId = "old-project";

        var op = Guid.NewGuid().ToString("n");
        var req = Change(op, "s1", "new-project", "New", "C:\\ws\\new");
        await pending.EnqueueAsync(req);

        var start = await runner.StartRunAsync(Req("s1"));
        Assert.Null(start.Note);
        await WaitAllRuns(runner);
        await WaitProjectApplied(bus, op);
        Assert.Equal("new-project", sessions.ProjectId);

        // A host restart re-surfaces the persisted row (simulated: re-enqueue the
        // SAME operation); the next boundary applies it again — SetProjectAsync
        // commits with the same OperationId and must NOT append a second entry.
        await pending.EnqueueAsync(req);
        var second = await runner.StartRunAsync(Req("s1"));
        Assert.Null(second.Note);
        await WaitAllRuns(runner);

        Assert.Equal("new-project", sessions.ProjectId);
        Assert.Empty(pending.Rows);
        // Exactly ONE persisted ProjectContext entry per operation id:
        Assert.Single(sessions.ProjectEntries, e => e.Id == op);
        Assert.Equal(2, sessions.ProjectSets.Count); // two commits, one entry
    }

    [Fact]
    public async Task LaterEnqueue_SupersedesEarlierUnapplied_OnlyLatestApplies()
    {
        var (runner, bus, sessions, pending) = New(stall: true);
        sessions.ProjectId = "old-project";

        var op1 = Guid.NewGuid().ToString("n");
        var op2 = Guid.NewGuid().ToString("n");
        await pending.EnqueueAsync(Change(op1, "s1", "project-a", "A", "C:\\ws\\a"));
        var start = await runner.StartRunAsync(Req("s1"));
        Assert.Null(start.Note);
        Assert.NotNull(runner.GetSessionRun("s1"));

        // Mid-run: a later selection lands — the pending store REPLACES the
        // earlier unapplied one.
        await pending.EnqueueAsync(Change(op2, "s1", "project-b", "B", "C:\\ws\\b"));
        var current = await pending.PendingAsync("s1");
        Assert.Equal(op2, current!.OperationId);

        Assert.True(runner.CancelRun(start.RunId!));
        await WaitAllRuns(runner);
        await WaitProjectApplied(bus, op2);

        Assert.Equal("project-b", sessions.ProjectId);    // only the LATEST applies
        Assert.Equal("C:\\ws\\b", sessions.ProjectWorkspace);
        Assert.Equal(op2, Assert.Single(sessions.ProjectSets).OperationId);
        Assert.Empty(pending.Rows);
    }

    [Fact]
    public async Task MidBatch_NeverApplies_OnlyAfterUnwind()
    {
        var (runner, bus, sessions, pending) = New(stall: true);
        sessions.ProjectId = "old-project";

        var op = Guid.NewGuid().ToString("n");
        await pending.EnqueueAsync(Change(op, "s1", "new-project", "New", "C:\\ws\\new"));

        var start = await runner.StartRunAsync(Req("s1"));
        Assert.NotNull(runner.GetSessionRun("s1"));

        // MID-BATCH: the in-flight batch must keep its original workspace — the
        // boundary is the run's finally, which has NOT run yet.
        await Task.Delay(50);
        Assert.Empty(sessions.ProjectSets);
        Assert.Equal("old-project", sessions.ProjectId);
        Assert.NotNull(await pending.PendingAsync("s1"));
        Assert.DoesNotContain(bus.OfType(), e => e.Type == AgentEventType.ProjectApplied);

        // Wind down; the apply fires as the run unwinds — and only then.
        Assert.True(runner.CancelRun(start.RunId!));
        await WaitAllRuns(runner);
        await WaitProjectApplied(bus, op);
        Assert.Equal("new-project", sessions.ProjectId);
    }

    [Fact]
    public async Task SendHoldsSessionGate_ApplyDefers_UntilGateFrees()
    {
        var (runner, bus, sessions, pending) = New();
        sessions.ProjectId = "old-project";

        var op = Guid.NewGuid().ToString("n");
        await pending.EnqueueAsync(Change(op, "s1", "new-project", "New", "C:\\ws\\new"));

        // A concurrent send owns the session's gate (its critical section in
        // StartRunAsync). The boundary apply must NOT jump the queue — it
        // defers (returns null) and the NEXT boundary applies the row.
        using var gate = runner.SessionGate("s1");
        Assert.True(gate.Wait(TimeSpan.FromSeconds(5))); // simulate the send holding it
        var deferred = await runner.ApplyPendingProjectChangeAsync("s1");
        Assert.Null(deferred);                            // deferred: the send owns the boundary
        Assert.Empty(sessions.ProjectSets);
        Assert.NotNull(await pending.PendingAsync("s1")); // the row persists

        gate.Release();                                   // the send's critical section ends
        var applied = await runner.ApplyPendingProjectChangeAsync("s1");
        Assert.Equal(op, applied);
        Assert.Equal("new-project", sessions.ProjectId);
        Assert.Empty(pending.Rows);
    }
}
