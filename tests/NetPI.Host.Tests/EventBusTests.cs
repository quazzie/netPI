using Microsoft.Extensions.Logging.Abstractions;
using NetPI.Host.Events;

namespace NetPI.Host.Tests;

public static class EventBusTests
{
    private static async Task WaitFor(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new AssertException("timed out waiting for: " + what);
            await Task.Delay(20);
        }
    }

    public static void Register(TestRunner r)
    {
        r.Add("bus: delivers in publish order with increasing Seq", async () =>
        {
            await using var bus = new EventBus(NullLogger.Instance);
            var seen = new List<(int N, long Seq)>();
            using var _ = bus.Subscribe("n.value", e => seen.Add(((int)e.Data!, e.Seq)));
            for (var i = 0; i < 2000; i++) bus.Publish("n.value", i);
            await bus.FlushAsync();
            Check.Equal(2000, seen.Count);
            for (var i = 0; i < seen.Count; i++) Check.Equal(i, seen[i].N);
            Check.True(seen.Zip(seen.Skip(1)).All(p => p.Second.Seq == p.First.Seq + 1), "consecutive seq");
        });

        r.Add("bus: exact, prefix and * patterns; async handlers; failing handler isolation", async () =>
        {
            await using var bus = new EventBus(NullLogger.Instance);
            var log = new List<string>();
            using var a = bus.Subscribe("agent.status", e => log.Add("exact:" + e.Type));
            using var b = bus.Subscribe("agent.*", e => log.Add("prefix:" + e.Type));
            using var c = bus.Subscribe("*", e => log.Add("all:" + e.Type));
            using var d = bus.Subscribe("agent.status", _ => throw new InvalidOperationException("handler bug"));
            using var e1 = bus.SubscribeAsync("agent.status", async e => { await Task.Delay(5); log.Add("async:" + e.Type); });
            bus.Publish("agent.status");
            bus.Publish("agentx");
            bus.Publish("agent");
            bus.Publish("agent.queue.more");
            await bus.FlushAsync();
            Check.Equal("exact:agent.status,prefix:agent.status,all:agent.status,async:agent.status,all:agentx,all:agent,prefix:agent.queue.more,all:agent.queue.more",
                string.Join(",", log));
        });

        r.Add("bus: disposing a subscription is idempotent and stops delivery", async () =>
        {
            await using var bus = new EventBus(NullLogger.Instance);
            var count = 0;
            var sub = bus.Subscribe("x", _ => count++);
            bus.Publish("x");
            await bus.FlushAsync();
            sub.Dispose();
            sub.Dispose();
            bus.Publish("x");
            await bus.FlushAsync();
            Check.Equal(1, count);
            Check.Equal(0, bus.SubscriberCount);
        });

        r.Add("bus: Activity names the handler the dispatcher is inside, and says nothing between handlers", async () =>
        {
            await using var bus = new EventBus(NullLogger.Instance);
            var inside = new ManualResetEventSlim();
            var release = new ManualResetEventSlim();
            using var _ = bus.Subscribe("slow.*", e => { inside.Set(); release.Wait(5000); });
            Check.Equal(null, bus.Activity().Pattern, "idle before anything is published");
            bus.Publish("slow.one");
            Check.True(inside.Wait(5000), "the handler started");
            var a = bus.Activity();
            Check.Equal("slow.*", a.Pattern, "the subscription it is inside");
            Check.Equal("slow.one", a.Type, "the event it is delivering");
            await Task.Delay(60);
            Check.True(bus.Activity().Elapsed >= TimeSpan.FromMilliseconds(50), "it measures how long");
            release.Set();
            await bus.FlushAsync();
            var after = bus.Activity();
            Check.Equal(null, after.Pattern, "nothing in flight once delivered");
            Check.Equal(1L, after.Delivered, "one event delivered");
        });

        r.Add("watchdog: a handler that holds the bus is named in the log, and its release is logged", async () =>
        {
            var log = new CapturingLogger();
            await using var bus = new EventBus(NullLogger.Instance);
            using var dog = new StallWatchdog(bus, log, TimeSpan.FromMilliseconds(150), TimeSpan.FromMilliseconds(30));
            var inside = new ManualResetEventSlim();
            var release = new ManualResetEventSlim();
            using var _ = bus.Subscribe("agent.status", e => { inside.Set(); release.Wait(10_000); });
            bus.Publish("agent.status");
            bus.Publish("agent.status");
            Check.True(inside.Wait(5000), "the handler started");
            await WaitFor(() => log.Has("inside the handler 'agent.status' on 'agent.status'"), "a stall report naming the handler");
            Check.True(log.Has("1 event(s) wait behind it"), "it says how many events wait: " + log.Dump());
            release.Set();
            await WaitFor(() => log.Has("left the handler it was stuck in"), "the release is logged");
            await bus.FlushAsync();
            var reports = log.Lines.Count(l => l.Contains("inside the handler"));
            Check.Equal(1, reports, "one report for one episode, not one per tick: " + log.Dump());
        });

        r.Add("watchdog: a bus that keeps delivering logs nothing, however long its queue", async () =>
        {
            var log = new CapturingLogger();
            await using var bus = new EventBus(NullLogger.Instance);
            // a pool that runs its work at once, so only the bus is under test
            using var dog = new StallWatchdog(bus, log, TimeSpan.FromMilliseconds(400), TimeSpan.FromMilliseconds(20), work => work());
            using var _ = bus.Subscribe("x", e => { });
            for (var i = 0; i < 20_000; i++) bus.Publish("x");
            await bus.FlushAsync();
            await Task.Delay(100);
            Check.Equal(0, log.Lines.Count, "no stall to report: " + log.Dump());
        });

        r.Add("watchdog: a pool that does not start its queued work is reported while it lasts, and its recovery is", async () =>
        {
            var log = new CapturingLogger();
            await using var bus = new EventBus(NullLogger.Instance);
            // a stand-in pool that keeps what it is given: nothing starts until the test lets it
            var held = new System.Collections.Concurrent.ConcurrentQueue<Action>();
            using var dog = new StallWatchdog(bus, log, TimeSpan.FromMilliseconds(150), TimeSpan.FromMilliseconds(30), held.Enqueue);
            await WaitFor(() => log.Has("thread pool has not started a queued work item"), "the starved pool is reported: " + log.Dump());
            Check.True(log.Has("its threads are blocked"), "it says what that means: " + log.Dump());
            while (held.TryDequeue(out var work)) work();
            await WaitFor(() => log.Has("thread pool is running work again"), "its recovery is reported: " + log.Dump());
        });

        r.Add("watchdog: a gate that a thread holds and never releases is reported, and so is its release", async () =>
        {
            var log = new CapturingLogger();
            await using var bus = new EventBus(NullLogger.Instance);
            var gate = new object();
            using var dog = new StallWatchdog(bus, log, TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(30), work => work(), gate);
            // a thread that holds the gate: a deadlocked statement looks like this from outside
            var release = new ManualResetEventSlim();
            var holding = new ManualResetEventSlim();
            var holder = new Thread(() => { lock (gate) { holding.Set(); release.Wait(20_000); } }) { IsBackground = true };
            holder.Start();
            Check.True(holding.Wait(5000), "the thread holds the gate");
            await WaitFor(() => log.Has("The database gate has not been free for"), "the held gate is reported: " + log.Dump());
            Check.True(log.Has("a thread holds it and is not coming back"), "with what it means: " + log.Dump());
            release.Set();
            holder.Join(5000);
            await WaitFor(() => log.Has("The database gate is free again"), "its release is reported: " + log.Dump());
            Check.Equal(1, log.Lines.Count(l => l.Contains("has not been free for")), "one report for the episode: " + log.Dump());
        });

        r.Add("watchdog: a gate that is taken and released all the time is not reported", async () =>
        {
            var log = new CapturingLogger();
            await using var bus = new EventBus(NullLogger.Instance);
            var gate = new object();
            using var dog = new StallWatchdog(bus, log, TimeSpan.FromMilliseconds(400), TimeSpan.FromMilliseconds(20), work => work(), gate);
            var stop = new CancellationTokenSource();
            // busy, never held for long: statements of a loaded server
            var worker = Task.Run(() => { while (!stop.IsCancellationRequested) { lock (gate) Thread.SpinWait(2000); Thread.Yield(); } });
            await Task.Delay(700);
            stop.Cancel();
            await worker;
            Check.Equal(0, log.Lines.Count, "no stall to report: " + log.Dump());
        });

        r.Add("bus: ring buffer keeps the last 500 events", async () =>
        {
            await using var bus = new EventBus(NullLogger.Instance);
            for (var i = 0; i < 700; i++) bus.Publish(new BusEvent { Type = "r", Data = i, Ui = false });
            await bus.FlushAsync();
            var recent = bus.Recent(1000);
            Check.Equal(500, recent.Count);
            Check.Equal(200, (int)recent[0].Data!);
            Check.Equal(699, (int)recent[^1].Data!);
            var last3 = bus.Recent(3);
            Check.Equal("697,698,699", string.Join(",", last3.Select(e => e.Data)));
        });
    }
}

/// <summary>An ILogger that keeps what it was told, for the tests that look at a log line.</summary>
internal sealed class CapturingLogger : Microsoft.Extensions.Logging.ILogger
{
    private readonly List<string> _lines = [];

    public IReadOnlyList<string> Lines { get { lock (_lines) return [.. _lines]; } }
    public bool Has(string text) => Lines.Any(l => l.Contains(text, StringComparison.Ordinal));
    public string Dump() => string.Join(" | ", Lines);

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
    public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (_lines) _lines.Add(formatter(state, exception));
    }
}

