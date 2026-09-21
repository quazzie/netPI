using System.Collections.Concurrent;
using System.Linq;
using System.Text.Json;
using NetPI.Abstractions;
using NetPI.Nudge;
using Xunit;

namespace NetPI.Host.Tests;

// ---- fakes ------------------------------------------------------------------

/// <summary>
/// Event bus that dispatches handlers inline and synchronously (matching the
/// host's real behavior) and records every published event, so tests can assert
/// the exact event the nudge plugin emits.
/// </summary>
internal sealed class InlineEventBus : IEventBus
{
    private readonly ConcurrentDictionary<Type, List<Delegate>> _subs = new();
    public readonly ConcurrentQueue<AgentEvent> Published = new();

    public IDisposable Subscribe<TEvent>(NetPI.Abstractions.EventHandler<TEvent> handler, EventSubscriptionOptions? options = null)
    {
        var list = _subs.GetOrAdd(typeof(TEvent), _ => []);
        lock (list) list.Add(handler);
        return new Sub(this, typeof(TEvent), handler);
    }

    public ValueTask PublishAsync<TEvent>(TEvent @event, CancellationToken cancellationToken = default)
    {
        var key = typeof(TEvent);
        List<Delegate>? list;
        lock (this)
        {
            _subs.TryGetValue(key, out list);
            if (@event is AgentEvent ae) Published.Enqueue(ae);
        }
        if (list is not null)
        {
            List<Delegate> snapshot;
            lock (list) snapshot = [.. list];
            foreach (var h in snapshot) ((NetPI.Abstractions.EventHandler<TEvent>)h)(@event);
        }
        return ValueTask.CompletedTask;
    }

    private sealed class Sub(InlineEventBus bus, Type t, Delegate d) : IDisposable
    {
        public void Dispose()
        {
            lock (bus)
            {
                if (bus._subs.TryGetValue(t, out var l)) lock (l) l.Remove(d);
            }
        }
    }
}

/// <summary>ISteeringQueue double that records enqueued steering text.</summary>
internal sealed class FakeSteering : ISteeringQueue
{
    public readonly ConcurrentQueue<string> Enqueued = new();
    public ValueTask EnqueueAsync(string text, string? sessionId = null, CancellationToken cancellationToken = default)
    {
        Enqueued.Enqueue(text);
        return ValueTask.CompletedTask;
    }
    public int PendingCount(string? sessionId = null) => Enqueued.Count;
}

/// <summary>Minimal IPluginContext wired to an inline bus + fixed registry.</summary>
internal sealed class NudgeContext : IPluginContext
{
    private readonly FixedServiceRegistry _services = new();
    public InlineEventBus Bus { get; } = new();
    public JsonElement OwnConfig { get; set; }
    public PluginInfo Info => new("netpi.nudge", "Empty-Turn Nudge", "0.1.0");
    public IServiceRegistry Services => _services;
    public ICommandRegistry Commands => new NetPI.Host.Services.CommandRegistry();
    public IWebPanelRegistry WebPanels => new NetPI.Host.Services.WebPanelRegistry();
    public IEventBus Events => Bus;
    public IPluginLogger Log => new NoopPluginLogger();
    public IValueLease<object> LeaseSelf() => new NoopLease();
    public void Add(string id, object service) => _services.Put(id, service);

    private sealed class NoopPluginLogger : IPluginLogger
    {
        public void Debug(string m) { }
        public void Information(string m) { }
        public void Warning(string m) { }
        public void Error(string m, Exception? e = null) { }
    }
    private sealed class NoopLease : IValueLease<object>
    {
        public object Value => this;
        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

// ---- tests -------------------------------------------------------------------

public class NudgePluginTests
{
    private static NudgePlugin Load(NudgeContext ctx, string? configJson = null)
    {
        ctx.OwnConfig = string.IsNullOrEmpty(configJson)
            ? JsonDocument.Parse("{}").RootElement
            : JsonDocument.Parse(configJson).RootElement;
        var plugin = new NudgePlugin();
        plugin.LoadAsync(ctx, CancellationToken.None).GetAwaiter().GetResult();
        return plugin;
    }

    private static AgentEvent TurnEmptyEvent(string sid) => new(
        Guid.NewGuid().ToString("n"), AgentEventType.TurnEmpty, DateTimeOffset.UtcNow, sid, null);

    [Fact]
    public async Task EmptyTurn_EnqueuesOneNudge_AndPublishesNudged()
    {
        var ctx = new NudgeContext();
        var steering = new FakeSteering();
        ctx.Add("steering", steering);
        Load(ctx);
        ctx.Bus.PublishAsync(AgentStarting("s1"), CancellationToken.None).GetAwaiter().GetResult();
        ctx.Bus.PublishAsync(TurnEmptyEvent("s1"), CancellationToken.None).GetAwaiter().GetResult();

        Assert.Equal(1, steering.Enqueued.Count);
        var first = steering.Enqueued.ToArray()[0];
        Assert.StartsWith("Your previous turn ended", first);

        var nudged = ctx.Bus.Published.Where(e => e.Type == AgentEventType.Nudged).ToList();
        Assert.Single(nudged);
        Assert.Equal("s1", nudged[0].SessionId);
        var root = JsonDocument.Parse(nudged[0].Payload!.Value.ToString()).RootElement;
        Assert.Equal("nudge", root.GetProperty("kind").GetString());
    }

    [Fact]
    public async Task SecondEmptyTurn_IsStillNudged_UptoMax()
    {
        var ctx = new NudgeContext();
        ctx.Add("steering", new FakeSteering());
        Load(ctx);

        var steering = (FakeSteering)ctx.Services.Resolve<ISteeringQueue>("steering");
        ctx.Bus.PublishAsync(AgentStarting("s1"), CancellationToken.None).GetAwaiter().GetResult();
        ctx.Bus.PublishAsync(TurnEmptyEvent("s1"), CancellationToken.None).GetAwaiter().GetResult();
        ctx.Bus.PublishAsync(TurnEmptyEvent("s1"), CancellationToken.None).GetAwaiter().GetResult();

        Assert.Equal(2, steering.Enqueued.Count);
    }

    [Fact]
    public async Task ThirdEmptyTurn_ExceedsDefaultMax_NudgesStop()
    {
        var ctx = new NudgeContext();
        ctx.Add("steering", new FakeSteering());
        Load(ctx);

        var steering = (FakeSteering)ctx.Services.Resolve<ISteeringQueue>("steering");
        ctx.Bus.PublishAsync(AgentStarting("s1"), CancellationToken.None).GetAwaiter().GetResult();
        ctx.Bus.PublishAsync(TurnEmptyEvent("s1"), CancellationToken.None).GetAwaiter().GetResult();
        ctx.Bus.PublishAsync(TurnEmptyEvent("s1"), CancellationToken.None).GetAwaiter().GetResult();
        ctx.Bus.PublishAsync(TurnEmptyEvent("s1"), CancellationToken.None).GetAwaiter().GetResult();

        // Default maxNudges = 2: the third empty turn must NOT nudge.
        Assert.Equal(2, steering.Enqueued.Count);
    }

    [Fact]
    public async Task NewRun_ResetsNudgeCounter()
    {
        var ctx = new NudgeContext();
        ctx.Add("steering", new FakeSteering());
        Load(ctx);

        var steering = (FakeSteering)ctx.Services.Resolve<ISteeringQueue>("steering");
        ctx.Bus.PublishAsync(AgentStarting("s1"), CancellationToken.None).GetAwaiter().GetResult();
        ctx.Bus.PublishAsync(TurnEmptyEvent("s1"), CancellationToken.None).GetAwaiter().GetResult();
        ctx.Bus.PublishAsync(TurnEmptyEvent("s1"), CancellationToken.None).GetAwaiter().GetResult();
        Assert.Equal(2, steering.Enqueued.Count);

        // A new run resets the counter, so a fresh empty turn nudges again.
        ctx.Bus.PublishAsync(AgentStarting("s1"), CancellationToken.None).GetAwaiter().GetResult();
        ctx.Bus.PublishAsync(TurnEmptyEvent("s1"), CancellationToken.None).GetAwaiter().GetResult();
        Assert.Equal(3, steering.Enqueued.Count);
    }

    [Fact]
    public async Task Disabled_SkipsNudge()
    {
        var ctx = new NudgeContext();
        var steering = new FakeSteering();
        ctx.Add("steering", steering);
        Load(ctx, configJson: """{"enabled": false}""");

        ctx.Bus.PublishAsync(AgentStarting("s1"), CancellationToken.None).GetAwaiter().GetResult();
        ctx.Bus.PublishAsync(TurnEmptyEvent("s1"), CancellationToken.None).GetAwaiter().GetResult();

        Assert.Empty(steering.Enqueued);
    }

    private static AgentEvent AgentStarting(string sid) => new(
        Guid.NewGuid().ToString("n"), AgentEventType.AgentStarting, DateTimeOffset.UtcNow, sid, null);
}
