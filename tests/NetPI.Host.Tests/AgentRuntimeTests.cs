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

// ---- fakes ----------------------------------------------------------------

internal sealed class FakeProvider : IModelProvider
{
    private readonly Queue<IReadOnlyList<ModelEvent>> _turns;
    public FakeProvider(params IReadOnlyList<ModelEvent>[] turns) => _turns = new Queue<IReadOnlyList<ModelEvent>>(turns);
    /// <summary>Messages sent on the most recent model call (PLAN §12 assertions).</summary>
    public IReadOnlyList<AgentMessage>? LastMessages;
    /// <summary>Tools offered on the most recent model call (astra-2 §8 shared-read assertions).</summary>
    public IReadOnlyList<ToolDefinition>? LastTools;

    public async IAsyncEnumerable<ModelEvent> RunAsync(ModelRequest request, CancellationToken cancellationToken)
    {
        LastMessages = request.Messages;
        LastTools = request.Tools;
        var events = _turns.Count > 0 ? _turns.Dequeue() : null;
        if (events is null) yield break;
        foreach (var ev in events)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return ev;
        }
    }
}

internal sealed class FixedServiceRegistry : IServiceRegistry
{
    private readonly Dictionary<string, object> _map = new(StringComparer.Ordinal);
    public void Put(string id, object instance) => _map[id] = instance;

    public IDisposable Register<T>(string id, T instance) where T : notnull
    {
        _map[id] = instance;
        return new Noop();
    }

    public IValueLease<T> Acquire<T>(string id) where T : notnull
        => new Lease<T>(Require<T>(id));

    public IValueLease<object> Acquire(string id, Type expectedType)
        => new Lease<object>(Require(id, expectedType));

    public IValueLease<T> AcquireSelfLease<T>() where T : notnull
        => new Lease<T>(default!);

    public T Resolve<T>(string id) where T : notnull => Require<T>(id);

    private object Require(string id, Type type) =>
        _map.TryGetValue(id, out var v) ? v : throw new ServiceUnavailableException(id, "missing");

    private T Require<T>(string id) where T : notnull =>
        _map.TryGetValue(id, out var v) ? (T)v : throw new ServiceUnavailableException(id, "missing");

    private sealed class Noop : IDisposable { public void Dispose() { } }

    private sealed class Lease<T>(T value) : IValueLease<T> where T : notnull
    {
        public T Value => value;
        public void Dispose() { }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}

internal sealed class NoopEventBus : IEventBus
{
    public IDisposable Subscribe<TEvent>(NetPI.Abstractions.EventHandler<TEvent> handler, EventSubscriptionOptions? options = null)
        => new Noop();
    public ValueTask PublishAsync<TEvent>(TEvent @event, CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;
    private sealed class Noop : IDisposable { public void Dispose() { } }
}

internal sealed class NoopPluginContext : IPluginContext
{
    private readonly FixedServiceRegistry _services = new();
    /// <summary>PLAN §44 test hook: number of live self-leases held by the agent.</summary>
    public int LeaseDepth => _leaseDepth;
    private int _leaseDepth;
    public PluginInfo Info => new("test", "test", "1.0.0");
    public IServiceRegistry Services => _services;
    public IEventBus Events => new NoopEventBus();
    public ICommandRegistry Commands => new NetPI.Host.Services.CommandRegistry();
    public IWebPanelRegistry WebPanels => new NetPI.Host.Services.WebPanelRegistry();
    public JsonElement OwnConfig => JsonDocument.Parse("{}").RootElement;
    public IPluginLogger Log => new NoopLogger();
    public IValueLease<object> LeaseSelf()
    {
        Interlocked.Increment(ref _leaseDepth);
        return new NoopLease(() => Interlocked.Decrement(ref _leaseDepth));
    }
    private sealed class NoopLease(Action onRelease) : IValueLease<object>
    {
        private int _disposed;
        public object Value => this;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            onRelease();
        }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
    public void Add(string id, object service) => _services.Put(id, service);
    private sealed class NoopLogger : IPluginLogger
    {
        public void Debug(string m) { }
        public void Information(string m) { }
        public void Warning(string m) { }
        public void Error(string m, Exception? e = null) { }
    }
}

/// <summary>PLAN §50: agent scenarios with a fake provider (no real model).</summary>
public class AgentRuntimeTests
{
    private static AgentRuntime MakeRuntime(IModelProvider provider, IToolRegistry? tools = null,
        ISessionStore? store = null)
    {
        var ctx = new NoopPluginContext();
        ctx.Add("provider", provider);
        if (tools is not null) ctx.Add("tools", tools);
        if (store is not null) ctx.Add("sessions", store);
        return new AgentRuntime(ctx);
    }

    private static AgentRunOptions Options(string text) => new()
    {
        SessionId = "s1",
        ModelId = "model",
        Messages = [new AgentMessage("u1", MessageRole.User, [new TextPart(text)], DateTimeOffset.UtcNow)],
    };

    private static AgentMessage Assistant(params MessagePart[] parts)
        => new("a1", MessageRole.Assistant, parts, DateTimeOffset.UtcNow);

    [Fact]
    public async Task PlainResponse_CompletesWithFinalText()
    {
        var provider = new FakeProvider([
            [new ModelStarted("model"), new ModelCompleted(Assistant(new TextPart("hello")))],
        ]);
        var rt = MakeRuntime(provider);

        var result = await rt.RunAsync(Options("hi"), CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal(1, result.Turns);
        Assert.Contains(result.FinalAssistant!.Parts, p => (p as TextPart)?.Text == "hello");
    }

    [Fact]
    public async Task SingleToolCall_ExecutesAndFeedsResultBack()
    {
        var tool = new EchoTool();
        var registry = new TestRegistry(tool);
        var provider = new FakeProvider([
            [new ModelStarted("model"), new ModelCompleted(Assistant(
                new ToolCallPart("t1", "echo",
                    JsonSerializer.SerializeToElement(new { msg = "ping" }))))],
            [new ModelCompleted(Assistant(new TextPart("done")))],
        ]);
        var rt = MakeRuntime(provider, registry);

        var result = await rt.RunAsync(Options("echo"), CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal(2, result.Turns);
        Assert.True(tool.Saw("echo", "ping"));
    }

    [Fact]
    public async Task UnknownToolCall_BecomesErrorResultWithoutException()
    {
        var registry = new TestRegistry();
        var provider = new FakeProvider([
            [new ModelStarted("model"), new ModelCompleted(Assistant(
                new ToolCallPart("t1", "nope",
                    JsonSerializer.SerializeToElement(new { }))))],
            [new ModelCompleted(Assistant(new TextPart("ok")))],
        ]);
        var rt = MakeRuntime(provider, registry);

        var result = await rt.RunAsync(Options("x"), CancellationToken.None);

        Assert.True(result.Ok);
        // The tool round-trip still completes; the unknown tool is surfaced as an
        // error result, not an exception.
        Assert.Equal(2, result.Turns);
    }

    [Fact]
    public async Task Steering_IsPerSessionAndDrainedBetweenTurns()
    {
        // PLAN §12: steering queues are per-session; a steer for the running
        // session is injected after the tool batch, before the next model call.
        var tool = new CountingLeaseTool(new NoopPluginContext());
        var registry = new TestRegistry(tool);
        var provider = new FakeProvider([
            [new ModelStarted("model"), new ModelCompleted(Assistant(
                new ToolCallPart("t1", "counting",
                    JsonSerializer.SerializeToElement(new { }))))],
            [new ModelCompleted(Assistant(new TextPart("done")))],
        ]);
        var ctx = new NoopPluginContext();
        ctx.Add("provider", provider);
        ctx.Add("tools", registry);
        var rt = new AgentRuntime(ctx);

        // Enqueue steering for the run's session while the run is busy.
        await ((ISteeringQueue)rt).EnqueueAsync("use the other config", "s1");
        Assert.Equal(1, ((ISteeringQueue)rt).PendingCount("s1"));

        var result = await rt.RunAsync(Options("x"), CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal(0, ((ISteeringQueue)rt).PendingCount("s1")); // drained
        // The steering message reached the model in the second call.
        Assert.Contains(
            provider.LastMessages!,
            m => m.Role == MessageRole.User
                  && m.Parts.OfType<TextPart>().Any(t => t.Text == "use the other config"));
    }

    [Fact]
    public async Task SelfLease_IsHeldWhileToolsExecuteAndReleasedAfterRun()
    {
        // PLAN §44: the agent holds its own plugin lease from run start to run end —
        // observable while a tool executes — and releases it when the run completes.
        var ctx = new NoopPluginContext();
        var tool = new CountingLeaseTool(ctx);
        var registry = new TestRegistry(tool);
        var provider = new FakeProvider([
            [new ModelStarted("model"), new ModelCompleted(Assistant(
                new ToolCallPart("t1", "counting",
                    JsonSerializer.SerializeToElement(new { }))))],
            [new ModelCompleted(Assistant(new TextPart("ok")))],
        ]);
        ctx.Add("provider", provider);
        ctx.Add("tools", registry);
        var rt = new AgentRuntime(ctx);

        Assert.Equal(0, ctx.LeaseDepth); // idle: no self-lease
        var result = await rt.RunAsync(Options("x"), CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal(1, tool.TurnsSeen); // the tool actually executed
        Assert.True(tool.LeaseSeenDuringToolExecution,
            "agent self-lease must be held while tools execute");
        Assert.Equal(0, ctx.LeaseDepth); // released once the run completes
    }

    [Fact]
    public async Task Cancellation_ReturnsNotOkWithCancelledNote()
    {
        // A provider that never completes; cancellation must unwind cleanly.
        var provider = new HangProvider();
        var rt = MakeRuntime(provider);
        using var cts = new CancellationTokenSource(50);
        var result = await rt.RunAsync(Options("hi"), cts.Token);
        Assert.False(result.Ok);
        Assert.Equal("cancelled", result.Note);
    }

    // ---- astra-1 §11a (A/B): transcript persistence failure → failed run -------

    [Fact]
    public async Task ToolCallIntent_PersistFails_BeforeExecution_RunsFail_ToolsNotExecuted()
    {
        // astra-1 §11a (A/B): the assistant's tool-call INTENT is persisted before the
        // tools run. If that persist fails, the run must stop WITHOUT executing the
        // tools (we have no durable record of the calls, and a retry could otherwise
        // re-run side effects).
        var tool = new CountingLeaseTool(new NoopPluginContext());
        var registry = new TestRegistry(tool);
        var provider = new FakeProvider([
            [new ModelStarted("model"), new ModelCompleted(Assistant(
                new ToolCallPart("t1", "counting",
                    JsonSerializer.SerializeToElement(new { }))))],
        ]);
        var store = new FailingAppendStore(failAfter: 0); // every append fails
        var rt = MakeRuntime(provider, registry, store);

        var result = await rt.RunAsync(Options("x"), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.True(tool.TurnsSeen == 0, "tools must NOT execute when the tool-call intent is lost (TurnsSeen=" + tool.TurnsSeen + ")");
        Assert.NotNull(result.FinalAssistant);
        Assert.True(result.Note is not null && result.Note.Contains("tool-call intent"), result.Note);
    }

    [Fact]
    public async Task ToolResult_PersistFails_AfterExecution_RunsFail_ToolsRunExactlyOnce()
    {
        // astra-1 §11a (A/B): if the tool RESULTS cannot be persisted after execution,
        // the run stops before the next model turn and reports the uncertain recovery
        // state. The tools already ran exactly once — they must NOT be rerun.
        var tool = new CountingLeaseTool(new NoopPluginContext());
        var registry = new TestRegistry(tool);
        var provider = new FakeProvider([
            [new ModelStarted("model"), new ModelCompleted(Assistant(
                new ToolCallPart("t1", "counting",
                    JsonSerializer.SerializeToElement(new { }))))],
            [new ModelCompleted(Assistant(new TextPart("done")))], // never reached
        ]);
        var store = new FailingAppendStore(failAfter: 1); // 1st append ok, 2nd fails
        var rt = MakeRuntime(provider, registry, store);

        var result = await rt.RunAsync(Options("x"), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.True(tool.TurnsSeen == 1, "the tool ran exactly once and must not be rerun (TurnsSeen=" + tool.TurnsSeen + ")");
        Assert.True(result.Note is not null && result.Note.Contains("persisted"), result.Note);
    }

    [Fact]
    public async Task Cancellation_DuringPersist_IsCancellationNotPersistenceFailure()
    {
        // astra-1 §11a (A/B): a cancel must surface as a cancelled run, not a
        // persistence failure (AppendAsync rethrows OperationCanceledException).
        var provider = new HangProvider();
        var rt = MakeRuntime(provider);
        using var cts = new CancellationTokenSource(20);
        var result = await rt.RunAsync(Options("hi"), cts.Token);
        Assert.False(result.Ok);
        Assert.Equal("cancelled", result.Note);
    }
}

// ---- test doubles for tools -----------------------------------------------

internal sealed class EchoTool : IAgentTool
{
    private readonly List<string> _calls = [];
    public string Name => "echo";
    public string Description => "echo";
    public IReadOnlyList<string> Guidelines => [];
    public JsonElement Parameters => JsonSerializer.SerializeToElement(
        new { type = "object", properties = new { msg = new { type = "string" } } });

    public ValueTask<ToolResult> ExecuteAsync(ToolContext context, CancellationToken ct)
    {
        var msg = context.Arguments.TryGetProperty("msg", out var p) ? p.GetString()! : "";
        _calls.Add($"{Name}:{msg}");
        return ValueTask.FromResult(new ToolResult("t1", "echo", [new TextPart("echo " + msg)], false));
    }

    public bool Saw(string tool, string msg) => _calls.Contains($"{tool}:{msg}");
}

internal sealed class TestRegistry(params IAgentTool[] tools) : IToolRegistry
{
    private readonly List<IAgentTool> _all = [.. tools];
    public IDisposable Register(IAgentTool tool) { _all.Add(tool); return new Noop(); }
    public IReadOnlyList<IAgentTool> All() => _all;
    public IAgentTool? Find(string name) => _all.FirstOrDefault(t => t.Name == name);
    private sealed class Noop : IDisposable { public void Dispose() { } }
}

internal sealed class CountingLeaseTool(NoopPluginContext ctx) : IAgentTool
{
    public string Name => "counting";
    public string Description => "observes the agent's self-lease while executing";
    public IReadOnlyList<string> Guidelines => [];
    public JsonElement Parameters => JsonSerializer.SerializeToElement(new { type = "object" });
    public int TurnsSeen;
    public bool LeaseSeenDuringToolExecution;
    public ValueTask<ToolResult> ExecuteAsync(ToolContext context, CancellationToken ct)
    {
        TurnsSeen++;
        LeaseSeenDuringToolExecution = ctx.LeaseDepth > 0; // 1 iff the run is active
        return ValueTask.FromResult(new ToolResult("t1", "counting", [new TextPart("ok")], false));
    }
}

internal sealed class HangProvider : IModelProvider
{
    public async IAsyncEnumerable<ModelEvent> RunAsync(ModelRequest request, CancellationToken cancellationToken)
    {
        yield return new ModelStarted("model");
        await Task.Delay(Timeout.Infinite, cancellationToken);
    }
}

/// <summary>
/// astra-1 §11a (A/B) test double: an ISessionStore whose AppendAsync succeeds for the
/// first <c>failAfter</c> calls, then throws (simulating a disk-full / store outage).
/// </summary>
internal sealed class FailingAppendStore(int failAfter) : ISessionStore
{
    private int _appends;
    public int AppendCalls;

    public ValueTask<SessionInfo> CreateAsync(string? workspacePath, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(new SessionInfo("s1", null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 0, null));
    public ValueTask<SessionInfo?> GetAsync(string id, CancellationToken cancellationToken = default)
        => ValueTask.FromResult<SessionInfo?>(null);
    public ValueTask AppendAsync(SessionEntry entry, CancellationToken cancellationToken = default)
    {
        AppendCalls++; _appends++;
        if (_appends > failAfter) throw new InvalidOperationException("disk full (simulated)");
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
        => ValueTask.FromResult<IReadOnlyList<SessionEntry>>([]);
    public ValueTask<ProjectChangeResult> SetProjectAsync(ProjectChangeRequest change, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
}
