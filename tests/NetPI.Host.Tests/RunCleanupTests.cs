using System.Collections.Concurrent;
using NetPI.Abstractions;
using NetPI.Agent;
using Xunit;

namespace NetPI.Host.Tests;

/// <summary>
/// astra-1 Package A — run cleanup. Every accepted run leaves NO active run
/// and NO leaked lease, fails loudly when it cannot start or build context
/// (never a silent empty prompt), and produces EXACTLY ONE terminal outcome
/// (completed / cancelled / failed).
/// </summary>
public class RunCleanupTests
{
    private static (AgentRunner runner, TestContext ctx, RecordingBus bus) New(
        bool withProvider, bool failAppend = false, bool failContext = false)
    {
        var ctx = new TestContext();
        var bus = (RecordingBus)ctx.Events;
        if (withProvider)
            ctx.Add("provider", new FakeProvider([
                [new ModelStarted("model"), new ModelCompleted(new AgentMessage("a1", MessageRole.Assistant,
                    [new TextPart("ok")], DateTimeOffset.UtcNow))],
            ]));
        ctx.Add("sessions", new ThrowingStore(failAppend, failContext));
        return (new AgentRunner(new AgentRuntime(ctx), ctx), ctx, bus);
    }

    private static AgentRunRequest Req(string text = "hi") =>
        new("s1", null, "model", text);

    private static async Task WaitRun(AgentRunner r)
    {
        var t = r.RunTask;
        if (t is null) return;
        var done = await Task.WhenAny(t, Task.Delay(10_000));
        Assert.True(done == t, "run did not finish in time");
        await t;
    }

    private static IEnumerable<AgentEventType> Terminal(RecordingBus b) =>
        b.OfType()
          .Where(e => e.Type is AgentEventType.AgentCompleted
                    or AgentEventType.AgentCancelled
                    or AgentEventType.AgentFailed)
          .Select(e => e.Type);

    [Fact]
    public async Task EmptyMessage_FailsSend_TakesNoGate_NoLeak()
    {
        var (runner, ctx, bus) = New(withProvider: true);
        var start = await runner.StartRunAsync(new AgentRunRequest("s1", null, "model", "   "));

        Assert.False(string.IsNullOrWhiteSpace(start.Note));
        Assert.False(runner.IsRunning);
        Assert.Null(runner.RunTask);
        Assert.Equal(0, ctx.LeaseDepth);
        Assert.Empty(bus.OfType());
    }

    [Fact]
    public async Task PersistenceFailure_FailsSend_NoRun_NoLeak()
    {
        var (runner, ctx, bus) = New(withProvider: true, failAppend: true);
        var start = await runner.StartRunAsync(Req());

        Assert.False(string.IsNullOrWhiteSpace(start.Note));
        Assert.False(runner.IsRunning);
        Assert.Null(runner.RunTask);
        Assert.Equal(0, ctx.LeaseDepth);
        Assert.DoesNotContain(AgentEventType.AgentStarting, bus.OfType().Select(e => e.Type));
    }

    [Fact]
    public async Task MissingProvider_FailsRun_NoActiveRun_NoLeak()
    {
        var (runner, ctx, bus) = New(withProvider: false);
        var start = await runner.StartRunAsync(Req());

        Assert.Null(start.Note);
        Assert.NotNull(runner.RunTask);
        await WaitRun(runner);

        Assert.False(runner.IsRunning);
        Assert.Equal(0, ctx.LeaseDepth);
        Assert.Equal([AgentEventType.AgentFailed], Terminal(bus).ToArray());
    }

    [Fact]
    public async Task ContextBuildFailure_FailsRun_NotSilentlyEmpty()
    {
        var (runner, ctx, bus) = New(withProvider: true, failContext: true);
        var start = await runner.StartRunAsync(Req());

        Assert.Null(start.Note);
        await WaitRun(runner);

        Assert.False(runner.IsRunning);
        Assert.Equal(0, ctx.LeaseDepth);
        Assert.Equal([AgentEventType.AgentFailed], Terminal(bus).ToArray());
    }

    [Fact]
    public async Task CleanRun_ProducesExactlyOneTerminal()
    {
        var (runner, ctx, bus) = New(withProvider: true);
        var start = await runner.StartRunAsync(Req());
        Assert.Null(start.Note);
        await WaitRun(runner);

        Assert.Equal(0, ctx.LeaseDepth);
        var terms = Terminal(bus).ToArray();
        Assert.Single(terms);
        Assert.Equal(AgentEventType.AgentCompleted, terms[0]);
    }

    [Fact]
    public async Task CancelledRun_ProducesExactlyOneTerminal_Cancellation()
    {
        var (runner, ctx, bus) = New(withProvider: true);
        // Swap in a provider that blocks so cancellation is what ends the run.
        ctx.Add("provider", new StalledProvider());
        var start = await runner.StartRunAsync(Req());
        Assert.Null(start.Note);

        await Task.Delay(100);
        await runner.CancelRunAsync(CancellationToken.None);
        await WaitRun(runner);

        Assert.False(runner.IsRunning);
        Assert.Equal(0, ctx.LeaseDepth);
        Assert.Equal([AgentEventType.AgentCancelled], Terminal(bus).ToArray());
    }

    // ---- fakes -----------------------------------------------------------

    /// <summary>Emits one started event, then blocks until cancelled.</summary>
    private sealed class StalledProvider : IModelProvider
    {
        public IAsyncEnumerable<ModelEvent> RunAsync(ModelRequest request, CancellationToken ct)
            => StalledAsync(request, ct);

        private async IAsyncEnumerable<ModelEvent> StalledAsync(ModelRequest request, CancellationToken ct)
        {
            yield return new ModelStarted(request.ModelId);
            await Task.Delay(Timeout.Infinite, ct);
        }
    }

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

    /// <summary>Records every published event for terminal-outcome assertions.</summary>
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

    private sealed class ThrowingStore(bool failAppend, bool failContext) : ISessionStore
    {
        public ValueTask<SessionInfo> CreateAsync(string? workspacePath, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new SessionInfo("s1", null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 0, null));
        public ValueTask<SessionInfo?> GetAsync(string id, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<SessionInfo?>(null);
        public ValueTask AppendAsync(SessionEntry entry, CancellationToken cancellationToken = default)
        {
            if (failAppend) throw new InvalidOperationException("disk full (simulated)");
            return ValueTask.CompletedTask;
        }
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
        public ValueTask<IReadOnlyList<SessionEntry>> ReadRecentAsync(string sessionId, int count, CancellationToken cancellationToken = default)
        {
            if (failContext) throw new InvalidOperationException("context read failed (simulated)");
            return ValueTask.FromResult<IReadOnlyList<SessionEntry>>([]);
        }
        public ValueTask<ProjectChangeResult> SetProjectAsync(ProjectChangeRequest change, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
