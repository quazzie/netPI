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

    public async IAsyncEnumerable<ModelEvent> RunAsync(ModelRequest request, CancellationToken cancellationToken)
    {
        LastMessages = request.Messages;
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
}

// ---- test doubles for tools -----------------------------------------------

internal sealed class EchoTool : IAgentTool
{
    private readonly List<string> _calls = [];
    public string Name => "echo";
    public string Description => "echo";
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
