using System.Collections.Concurrent;
using NetPI.Abstractions;
using NetPI.Agent;
using Xunit;

namespace NetPI.Host.Tests;

/// <summary>
/// astra-1 Package E (slice 1) — session-specific run management: the runner owns
/// the RunId → RunHandle / SessionId → active-run registry, exposes the run-query
/// contract (ListRuns/GetRun/GetSessionRun/CancelRun), stamps RunId on the run's
/// events, and enforces capacity (default 1) + one-run-per-session.
/// </summary>
public class RunRegistryTests
{
    private static (AgentRunner runner, TestContext ctx, RecordingBus bus) New(
        int maxConcurrentRuns = 1, bool stall = false)
    {
        var ctx = new TestContext();
        var bus = (RecordingBus)ctx.Events;
        ctx.Add("provider", stall ? new StalledProvider() : new FakeProvider([
            [new ModelStarted("model"), new ModelCompleted(new AgentMessage("a1", MessageRole.Assistant,
                [new TextPart("ok")], DateTimeOffset.UtcNow))],
        ]));
        ctx.Add("sessions", new ThrowingStore());
        return (new AgentRunner(new AgentRuntime(ctx), ctx, maxConcurrentRuns), ctx, bus);
    }

    private static AgentRunRequest Req(string session, string text = "hi") =>
        new(session, null, "model", text);

    private static async Task WaitRun(AgentRunner r)
    {
        var t = r.RunTask;
        if (t is null) return;
        var done = await Task.WhenAny(t, Task.Delay(10_000));
        Assert.True(done == t, "run did not finish in time");
        await t;
    }

    /// <summary>Deterministic gate: waits until the run's terminal OUTCOME is recorded on the
    /// registry (task completion alone is racy — the outcome lands in the runner's finally).</summary>
    private static async Task WaitTerminal(AgentRunner r, string runId)
    {
        for (var i = 0; i < 2000; i++)
        {
            var info = r.GetRun(runId);
            if (info is not null && info.Outcome != RunState.Running) return;
            await Task.Delay(1);
        }
        Assert.Fail("run did not reach a terminal outcome in time");
    }

    /// <summary>Gate: wait until EVERY run the registry knows is in a terminal outcome.</summary>
    private static async Task WaitAllRuns(AgentRunner r)
    {
        for (var i = 0; i < 2000; i++)
        {
            if (r.ListRuns().All(x => x.Outcome != RunState.Running)) return;
            await Task.Delay(1);
        }
        Assert.Fail("not all runs reached a terminal outcome in time");
    }


    [Fact]
    public async Task StartRun_ReturnsRunId_RegistryListsIt()
    {
        var (runner, _, _) = New();
        var start = await runner.StartRunAsync(Req("s1"));

        Assert.Null(start.Note);
        Assert.NotNull(start.RunId);
        var run = runner.GetRun(start.RunId!);
        Assert.NotNull(run);
        Assert.Equal(start.RunId, run!.RunId);
        Assert.Equal("s1", run.SessionId);
        Assert.Equal("model", run.ModelId);
        Assert.Equal(RunState.Running, run.Outcome);

        await WaitRun(runner);

        Assert.Equal(RunState.Completed, runner.GetRun(start.RunId!)!.Outcome);
        Assert.NotNull(runner.GetRun(start.RunId!)!.EndTime);
        Assert.False(runner.IsRunning);
    }

    [Fact]
    public async Task TerminalEvent_CarriesRunId_OnlyForItsOwnRun()
    {
        var (runner, _, bus) = New();
        var start = await runner.StartRunAsync(Req("s1"));
        await WaitRun(runner);

        var terminals = bus.OfType().Where(e => e.Type is
            AgentEventType.AgentCompleted or AgentEventType.AgentCancelled or AgentEventType.AgentFailed).ToList();
        Assert.Single(terminals);
        Assert.Equal(start.RunId, terminals[0].RunId);
    }

    [Fact]
    public async Task SecondStart_SameSession_Rejected_WhileActive()
    {
        var (runner, ctx, _) = New(stall: true);
        var start = await runner.StartRunAsync(Req("s1"));
        Assert.Null(start.Note);
        Assert.NotNull(runner.GetSessionRun("s1"));

        var second = await runner.StartRunAsync(Req("s1"));
        Assert.NotNull(second.Note);
        Assert.Contains("already has an active run", second.Note);

        // A DIFFERENT session is also refused at capacity 1 (aggregate limit).
        var other = await runner.StartRunAsync(Req("s2"));
        Assert.NotNull(other.Note);

        Assert.True(runner.CancelRun(start.RunId!));
        await WaitRun(runner);
        Assert.Equal(0, ctx.LeaseDepth);
    }

    [Fact]
    public async Task GetSessionRun_FlipsToNull_WhenRunFinishes()
    {
        var (runner, _, _) = New(stall: true);
        var start = await runner.StartRunAsync(Req("s1"));

        // The stalled run is still active when we look — the flip-off is the assertion.
        var active = runner.GetSessionRun("s1");
        Assert.NotNull(active);
        Assert.Equal(start.RunId, active!.RunId);

        Assert.True(runner.CancelRun(start.RunId!));
        await WaitTerminal(runner, start.RunId);
        Assert.Null(runner.GetSessionRun("s1"));
    }

    [Fact]
    public async Task CancelRun_ByRunId_OnlyCancelsThatRun()
    {
        var (runner, ctx, bus) = New(stall: true);
        var start = await runner.StartRunAsync(Req("s1")); // stalled
        Assert.False(runner.CancelRun("does-not-exist"));
        Assert.True(runner.CancelRun(start.RunId!));

        await WaitRun(runner);

        Assert.Equal(RunState.Cancelled, runner.GetRun(start.RunId!)!.Outcome);
        Assert.Contains(AgentEventType.AgentCancelled, bus.OfType().Select(e => e.Type));
        Assert.Equal(0, ctx.LeaseDepth);
    }


    [Fact]
    public async Task LimitOne_AggregateIsRunning_FlipsOff_AtTerminal()
    {
        var (runner, _, _) = New(maxConcurrentRuns: 1);
        var start = await runner.StartRunAsync(Req("s1"));
        Assert.True(runner.IsRunning);

        await WaitRun(runner);
        Assert.False(runner.IsRunning);
        Assert.Single(runner.ListRuns());
    }

    [Fact]
    public async Task LimitTwo_TwoSessions_RunConcurrently_Isolated()
    {
        // astra-1 E (slice 2): capacity 2 + per-run runtime state — two DIFFERENT
        // sessions run at the same time, and each finishes in its own session's
        // context. The stale pre-slice-2 failure mode: the second run entered the
        // runtime's single shared loop and failed with "already in progress".
        var (runner, _, bus) = New(maxConcurrentRuns: 2, stall: true);
        var a = await runner.StartRunAsync(Req("A"));
        var b = await runner.StartRunAsync(Req("B"));
        Assert.Null(a.Note);
        Assert.Null(b.Note);
        Assert.NotEqual(a.RunId, b.RunId);

        // Both are live at once — capacity 2 lets them through, one-run-per-session holds.
        Assert.True(runner.IsRunning);
        Assert.Equal(2, runner.ListRuns().Count);
        Assert.NotNull(runner.GetSessionRun("A"));
        Assert.NotNull(runner.GetSessionRun("B"));

        // Wind both down; both must land in a terminal outcome with THEIR OWN run ids
        // stamped on the terminal events (no cross-run contamination).
        Assert.True(runner.CancelRun(a.RunId!));
        Assert.True(runner.CancelRun(b.RunId!));
        await WaitAllRuns(runner);
        Assert.All(runner.ListRuns(), x => Assert.Equal(RunState.Cancelled, x.Outcome));

        var terminals = bus.OfType().Where(e => e.Type is
            AgentEventType.AgentCancelled or AgentEventType.AgentCompleted or AgentEventType.AgentFailed).ToList();
        Assert.Equal(2, terminals.Count);
        // Set-equality (run ids are random hex; order of the terminal events is
        // whichever run finished first — both run ids and both session ids must be
        // present, each exactly once, each on its OWN run's terminal event).
        Assert.Equal(new[] { a.RunId!, b.RunId! }.OrderBy(x => x), terminals.Select(t => t.RunId!).OrderBy(x => x));
        Assert.Equal(new[] { "A", "B" }, terminals.Select(t => t.SessionId!).OrderBy(x => x));
        Assert.All(terminals, t => Assert.True(t.RunId! == a.RunId || t.RunId! == b.RunId));
        Assert.Contains(terminals, t => t.RunId == a.RunId && t.SessionId == "A");
        Assert.Contains(terminals, t => t.RunId == b.RunId && t.SessionId == "B");
        Assert.False(runner.IsRunning);
        Assert.Null(runner.GetSessionRun("A"));
        Assert.Null(runner.GetSessionRun("B"));
    }

    // ---- fakes (self-contained; RunCleanupTests' are private to that class) ----

    private sealed class TestContext : IPluginContext
    {
        private readonly FixedServiceRegistry _services = new();
        private readonly RecordingBus _bus = new();
        public int LeaseDepth => _depth;
        private int _depth;

        public PluginInfo Info => new("test", "test", "1.0.0");
        public IServiceRegistry Services => _services;
        public IEventBus Events => _bus;
        public ICommandRegistry Commands => new NetPI.Host.Services.CommandRegistry();
        public IWebPanelRegistry WebPanels => new NetPI.Host.Services.WebPanelRegistry();
        public System.Text.Json.JsonElement OwnConfig =>
            System.Text.Json.JsonDocument.Parse("{}").RootElement;
        public IPluginLogger Log => new NoopLogger();
        public IValueLease<object> LeaseSelf()
        {
            Interlocked.Increment(ref _depth);
            return new NoopLease(() => Interlocked.Decrement(ref _depth));
        }
        public void Add(string id, object service) => _services.Put(id, service);

        private sealed class NoopLease(Action onRelease) : IValueLease<object>
        {
            private int _disposed;
            public object Value => this;
            public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) != 0) return; onRelease(); }
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

    private sealed class ThrowingStore : ISessionStore
    {
        public ValueTask<SessionInfo> CreateAsync(string? workspacePath, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new SessionInfo("s1", null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 0, null));
        public ValueTask<SessionInfo?> GetAsync(string id, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<SessionInfo?>(null);
        public ValueTask AppendAsync(SessionEntry entry, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;
        public ValueTask<IReadOnlyList<SessionInfo>> ListAsync(int count = 50, int offset = 0, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlyList<SessionInfo>>([]);
        public ValueTask<int> CountAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(0);
        public ValueTask RenameAsync(string id, string title, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask DeleteAsync(string id, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask SetModelAsync(string sessionId, string? modelId, string? reasoningLevel, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;
        public ValueTask SetWorkspaceAsync(string sessionId, string? workspacePath, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;
        public ValueTask<IReadOnlyList<SessionEntry>> ReadAsync(string sessionId, int offset, int count, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlyList<SessionEntry>>([]);
        public ValueTask<IReadOnlyList<SessionEntry>> ReadBeforeAsync(string sessionId, int beforeSequence, int count, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlyList<SessionEntry>>([]);
        public ValueTask<SessionEntry?> LatestCompactionAsync(string sessionId, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<SessionEntry?>(null);
        public ValueTask<IReadOnlyList<SessionEntry>> ReadAfterAsync(string sessionId, int afterSequence, int count, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlyList<SessionEntry>>([]);
        public ValueTask<IReadOnlyList<SessionEntry>> ReadRecentAsync(string sessionId, int count, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlyList<SessionEntry>>([]);
    public ValueTask<ProjectChangeResult> SetProjectAsync(ProjectChangeRequest change, CancellationToken cancellationToken = default)
        => throw new System.NotSupportedException();

    }

    /// <summary>Emits ModelStarted then blocks until cancelled — keeps a run "active" so
    /// capacity/query tests have a live window to assert on.</summary>
    private sealed class StalledProvider : IModelProvider
    {
        /// <summary>Emits ModelStarted, then awaits forever (until cancelled). Keeps a run "active"
        /// so capacity/query tests have a live window to assert on.</summary>
        public async IAsyncEnumerable<ModelEvent> RunAsync(ModelRequest request, CancellationToken cancellationToken)
        {
            yield return new ModelStarted("model");
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
    }
}
