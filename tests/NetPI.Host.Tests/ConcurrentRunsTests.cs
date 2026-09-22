using NetPI.Abstractions;
using NetPI.Agent;
using Xunit;

namespace NetPI.Host.Tests;

/// <summary>
/// astra-1 Package E §7 acceptance: "With limit two, sessions A and B run
/// independently. Cancelling or steering A does not affect B. Two starts for
/// the same session cannot both succeed." Drives the REAL AgentRunner with a
/// session-aware provider (one session stalls, the other completes), so the
/// live isolation window is deterministic — no sleeps beyond 1 ms poll gates.
/// </summary>
public class ConcurrentRunsTests
{
    private static (AgentRunner runner, TestContext ctx, RecordingBus bus) New(
        int maxConcurrentRuns, IModelProvider provider)
    {
        var ctx = new TestContext();
        var bus = (RecordingBus)ctx.Events;
        ctx.Add("provider", provider);
        ctx.Add("sessions", new NoopStore());
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

    /// <summary>Deterministic gate: wait until the run's terminal OUTCOME is recorded
    /// (task completion alone is racy — the outcome lands in the runner's finally).</summary>
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
    public async Task LimitTwo_DistinctSessions_ActiveSimultaneously()
    {
        // astra-1 E §7: "With limit two, sessions A and B run independently."
        var (runner, _, _) = New(maxConcurrentRuns: 2, new StalledProvider());
        var a = await runner.StartRunAsync(Req("A"));
        var b = await runner.StartRunAsync(Req("B"));

        Assert.Null(a.Note);
        Assert.Null(b.Note);
        Assert.NotEqual(a.RunId, b.RunId);

        // Both are live at once: the aggregate IsRunning is true, and each
        // session reports its OWN active run with the right session id.
        Assert.True(runner.IsRunning);
        Assert.Equal(2, runner.ListRuns().Count(r => r.Outcome == RunState.Running));
        Assert.NotNull(runner.GetSessionRun("A"));
        Assert.NotNull(runner.GetSessionRun("B"));
        Assert.Equal("A", runner.GetSessionRun("A")!.SessionId);
        Assert.Equal("B", runner.GetSessionRun("B")!.SessionId);

        // Both ListRuns entries carry their correct sessions.
        Assert.Contains(runner.ListRuns(), r => r.SessionId == "A" && r.Outcome == RunState.Running);
        Assert.Contains(runner.ListRuns(), r => r.SessionId == "B" && r.Outcome == RunState.Running);

        Assert.True(runner.CancelRun(a.RunId!));
        Assert.True(runner.CancelRun(b.RunId!));
        await WaitAllRuns(runner);
        Assert.False(runner.IsRunning);
    }

    [Fact]
    public async Task CancelA_BContinues_ToCompletion()
    {
        // astra-1 E §7: "Cancelling ... A does not affect B." Session A stalls;
        // cancel only A — B must keep running and complete on its own.
        var (runner, _, _) = New(maxConcurrentRuns: 2, new PerSessionStallProvider("A"));
        var a = await runner.StartRunAsync(Req("A"));
        var b = await runner.StartRunAsync(Req("B"));
        Assert.Null(a.Note);
        Assert.Null(b.Note);

        // Cancel A only. B is still live underneath.
        Assert.True(runner.CancelRun(a.RunId!));
        await WaitTerminal(runner, a.RunId!);

        Assert.Equal(RunState.Cancelled, runner.GetRun(a.RunId!)!.Outcome);
        Assert.Null(runner.GetSessionRun("A")); // A unwound

        // B is UNTOUCHED by A's cancellation: still active, then completes.
        Assert.NotNull(runner.GetSessionRun("B"));
        Assert.Equal(b.RunId!, runner.GetSessionRun("B")!.RunId);
        Assert.True(runner.IsRunning);
        Assert.Equal(RunState.Running, runner.GetRun(b.RunId!)!.Outcome);

        await WaitTerminal(runner, b.RunId!);
        Assert.Equal(RunState.Completed, runner.GetRun(b.RunId!)!.Outcome);
        Assert.NotNull(runner.GetRun(b.RunId!)!.EndTime);
        Assert.False(runner.IsRunning);
    }

    [Fact]
    public async Task SecondStart_SameSession_Rejected_WithClearMessage()
    {
        // astra-1 E §7: "Two starts for the same session cannot both succeed."
        var (runner, _, _) = New(maxConcurrentRuns: 2, new StalledProvider());
        var first = await runner.StartRunAsync(Req("A"));
        Assert.Null(first.Note);
        Assert.NotNull(runner.GetSessionRun("A"));

        var second = await runner.StartRunAsync(Req("A"));
        Assert.NotNull(second.Note);
        Assert.Contains("already has an active run", second.Note);
        Assert.Null(second.RunId); // nothing was started

        // The first run is the only active one — the rejected start added nothing.
        Assert.Single(runner.ListRuns());
        Assert.Equal(first.RunId, runner.GetSessionRun("A")!.RunId);

        Assert.True(runner.CancelRun(first.RunId!));
        await WaitRun(runner);
    }

    // ---- fakes (self-contained) ----

    private sealed class TestContext : IPluginContext
    {
        private readonly FixedServiceRegistry _services = new();
        private readonly RecordingBus _bus = new();
        public PluginInfo Info => new("test", "test", "1.0.0");
        public IServiceRegistry Services => _services;
        public IEventBus Events => _bus;
        public ICommandRegistry Commands => new NetPI.Host.Services.CommandRegistry();
        public IWebPanelRegistry WebPanels => new NetPI.Host.Services.WebPanelRegistry();
        public System.Text.Json.JsonElement OwnConfig =>
            System.Text.Json.JsonDocument.Parse("{}").RootElement;
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
        private readonly System.Collections.Concurrent.ConcurrentQueue<AgentEvent> _all = new();
        public IEnumerable<AgentEvent> OfType() => _all;
        public IDisposable Subscribe<TEvent>(NetPI.Abstractions.EventHandler<TEvent> handler,
            EventSubscriptionOptions? options = null) => new Noop();
        public ValueTask PublishAsync<TEvent>(TEvent evt, CancellationToken cancellationToken = default)
        {
            if (evt is AgentEvent ae) _all.Enqueue(ae);
            return ValueTask.CompletedTask;
        }
        private sealed class Noop : IDisposable { public void Dispose() { } }
    }

    /// <summary>Emits ModelStarted then blocks until cancelled — keeps a run "active" so
    /// the tests have a deterministic live window.</summary>
    private sealed class StalledProvider : IModelProvider
    {
        public async IAsyncEnumerable<ModelEvent> RunAsync(ModelRequest request, CancellationToken cancellationToken)
        {
            yield return new ModelStarted("model");
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
    }

    /// <summary>Stalls the given session (waits for cancellation) and completes every
    /// other session — so one run can be cancelled while the other proceeds to
    /// a real terminal Completed outcome.</summary>
    private sealed class PerSessionStallProvider(string stallSession) : IModelProvider
    {
        public async IAsyncEnumerable<ModelEvent> RunAsync(ModelRequest request, CancellationToken cancellationToken)
        {
            yield return new ModelStarted("model");
            if (request.SessionId == stallSession)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken); // stays "active" until cancelled
                yield break;
            }
            // Give the non-stalled run a short bounded latency so the test
            // can reliably observe it still live (Running) right after A's
            // cancel — a model turn is never instant. Then it completes.
            await Task.Delay(200, cancellationToken);
            yield return new ModelCompleted(new AgentMessage("asst", MessageRole.Assistant,
                [new TextPart("done " + (request.SessionId ?? ""))], DateTimeOffset.UtcNow));
        }
    }

    private sealed class NoopStore : ISessionStore
    {
        public ValueTask<SessionInfo> CreateAsync(string? workspacePath, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new SessionInfo("s", null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 0, null));
        public ValueTask<SessionInfo?> GetAsync(string id, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<SessionInfo?>(null);
        public ValueTask AppendAsync(SessionEntry entry, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask<IReadOnlyList<SessionInfo>> ListAsync(int count = 50, int offset = 0, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlyList<SessionInfo>>([]);
        public ValueTask<int> CountAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(0);
        public ValueTask RenameAsync(string id, string title, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask DeleteAsync(string id, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask SetModelAsync(string sessionId, string? modelId, string? reasoningLevel, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask SetWorkspaceAsync(string sessionId, string? workspacePath, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
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
            => ValueTask.FromException<ProjectChangeResult>(new System.NotSupportedException());
    }
}
