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

        r.Add("bus: exact, prefix and * patterns; async handlers; a failing handler only stops its own line", async () =>
        {
            await using var bus = new EventBus(NullLogger.Instance);
            var exact = new List<string>(); var prefix = new List<string>(); var all = new List<string>();
            var asyncSeen = new List<string>(); var failures = 0;
            using var a = bus.Subscribe("agent.status", e => exact.Add(e.Type));
            using var b = bus.Subscribe("agent.*", e => prefix.Add(e.Type));
            using var c = bus.Subscribe("*", e => all.Add(e.Type));
            using var d = bus.Subscribe("agent.status", _ => { failures++; throw new InvalidOperationException("handler bug"); });
            using var e1 = bus.SubscribeAsync("agent.status", async e => { await Task.Delay(5); asyncSeen.Add(e.Type); });
            bus.Publish("agent.status");
            bus.Publish("agentx");
            bus.Publish("agent");
            bus.Publish("agent.queue.more");
            await bus.FlushAsync();
            // Each subscriber sees exactly its own events, in publish order. The relative order across
            // subscribers is no longer a contract: one stuck handler must not hold the others back.
            Check.Equal("agent.status", string.Join(",", exact));
            Check.Equal("agent.status,agent.queue.more", string.Join(",", prefix));
            Check.Equal("agent.status,agentx,agent,agent.queue.more", string.Join(",", all));
            Check.Equal("agent.status", string.Join(",", asyncSeen));
            Check.Equal(1, failures);
        });

        r.Add("bus: a wedged subscriber only holds its own queue; the others keep flowing", async () =>
        {
            await using var bus = new EventBus(NullLogger.Instance);
            var started = new ManualResetEventSlim();
            var release = new ManualResetEventSlim();
            var fastSeen = new List<int>();
            using var _ = bus.Subscribe("t", e => { started.Set(); release.Wait(5000); });   // wedges on its worker
            using var fast = bus.Subscribe("t", e => fastSeen.Add((int)e.Data!));
            for (var i = 1; i <= 10; i++) bus.Publish("t", i);
            Check.True(started.Wait(5000), "the wedged subscriber started its handler");
            await WaitFor(() => fastSeen.Count == 10, "the fast subscriber saw every event while the other is wedged");
            release.Set();
            await bus.FlushAsync();
            Check.Equal(10, fastSeen.Count);
        });

        r.Add("bus: a subscriber that falls behind drops for itself only, and says so", async () =>
        {
            var log = new CapturingLogger();
            await using var bus = new EventBus(log, queueCapacity: 32);
            var started = new ManualResetEventSlim();
            var release = new ManualResetEventSlim();
            var slowSeen = new List<int>(); var fastSeen = new List<int>();
            using var _ = bus.Subscribe("t", e => { if (slowSeen.Count == 0) { started.Set(); release.Wait(5000); } slowSeen.Add((int)e.Data!); });
            using var fast = bus.Subscribe("t", e => fastSeen.Add((int)e.Data!));
            for (var i = 1; i <= 50; i++)
            {
                bus.Publish("t", i);
                await Task.Delay(2);   // at a pace the fast line keeps up with; the wedged one cannot
            }
            Check.True(started.Wait(5000), "the slow subscriber started");
            release.Set();
            await bus.FlushAsync();
            Check.Equal(50, fastSeen.Count, "the fast subscriber saw everything: it is not stopped by the other");
            Check.True(slowSeen.Count < 50, "the slow one missed what it could not hold while wedged: " + slowSeen.Count);
            Check.True(bus.Dropped >= 1, "the drops are counted");
            Check.True(log.Lines.Any(l => l.Contains("dropping") && l.Contains("'t'")), "and the drop is logged, naming the subscription: " + log.Dump());
        });

        r.Add("bus: FlushAsync is a barrier: it completes when every subscriber has processed what came before it", async () =>
        {
            await using var bus = new EventBus(NullLogger.Instance);
            var slow = new List<int>();
            var started = new ManualResetEventSlim();
            var release = new ManualResetEventSlim();
            using var _ = bus.Subscribe("t", e =>
            {
                slow.Add((int)e.Data!);
                if (slow.Count == 1) { started.Set(); release.Wait(5000); }   // hold the first event
            });
            bus.Publish("t", 1);
            bus.Publish("t", 2);
            Check.True(started.Wait(5000), "the subscriber started on the first event");
            var flushed = bus.FlushAsync();
            await Task.Delay(100);
            Check.False(flushed.IsCompleted, "the flush waits for the subscriber still inside the first event");
            release.Set();
            await flushed;
            Check.Equal("1,2", string.Join(",", slow), "and then it saw everything, in order");
        });

        r.Add("bus: a fast line does not complete the flush before the marker reaches a line that cannot hold it yet", async () =>
        {
            await using var bus = new EventBus(NullLogger.Instance, queueCapacity: 4);
            var bWoke = new ManualResetEventSlim();
            var released = new ManualResetEventSlim();
            var bRan = 0;
            using var b = bus.Subscribe("t", e =>
            {
                Interlocked.Increment(ref bRan);
                if (!bWoke.IsSet) bWoke.Set();
                released.Wait(5000);   // wedge: this line stops, and its queue of 4 fills behind it
            });
            using var a = bus.Subscribe("t", e => { });   // a fast line: it passes the marker early and must not end the flush for b
            for (var i = 1; i <= 10; i++) bus.Publish("t", i);   // b wedges; its queue fills while the marker cannot fit
            Check.True(bWoke.Wait(5000), "b is wedged with a full queue");
            var flush = bus.FlushAsync();
            await Task.Delay(300);   // plenty for a fast line to pass the marker
            Check.False(flush.IsCompleted, "the flush waits for b to receive its marker, even though a has long passed it");
            released.Set();
            await flush.WaitAsync(TimeSpan.FromSeconds(5));   // it does come back once b can take the marker
            Check.True(bRan >= 1, "b ran (it is the line the flush waited for): " + bRan);
        });

        r.Add("bus: a flush waits for its marker to be queued on a saturated input (full is not closed)", async () =>
        {
            const int input = 4;
            await using var bus = new EventBus(NullLogger.Instance, queueCapacity: 2, inputCapacity: input);
            var started = new ManualResetEventSlim();
            var release = new ManualResetEventSlim();
            var received = new List<string>();
            using var _ = bus.Subscribe("t.*", e =>
            {
                lock (received) received.Add(e.Type);
                if (e.Type == "t.one") { started.Set(); release.Wait(5000); }   // hold the line on the first event
            });
            bus.Publish("t.one");
            Check.True(started.Wait(5000), "the subscriber is inside the first event");
            bus.Publish("t.two");   // queued behind it: the line is full

            // A competing producer keeps the input channel full, so the flush's marker cannot be queued at once.
            var stop = new ManualResetEventSlim();
            // the stop check inside the inner loop too: once the bus is gone every publish drops, the backlog never
            // refills, and the flood would spin on drops forever otherwise
            var flood = Task.Run(() => { while (!stop.IsSet) while (!stop.IsSet && bus.Backlog < input) bus.Publish("t.flood"); });
            await WaitFor(() => bus.Backlog == input, "the input saturated");

            var flush = bus.FlushAsync();
            await Task.Delay(200);   // plenty for a give-up-on-full bug to report the bus as drained
            Check.False(flush.IsCompleted, "a full input is not a closed bus: the flush waits for its marker to be queued");

            release.Set();
            await flush.WaitAsync(TimeSpan.FromSeconds(10));
            stop.Set();
            await flood;
            Check.Equal("t.one,t.two", string.Join(",", received.Take(2)), "the events published before the flush were delivered, in order");
        });

        r.Add("bus: disposing the bus unblocks a flush that is still waiting for its marker", async () =>
        {
            const int input = 2;
            await using var bus = new EventBus(NullLogger.Instance, queueCapacity: 1, inputCapacity: input);
            var inside = new ManualResetEventSlim();
            var release = new ManualResetEventSlim();
            using var _ = bus.Subscribe("x", e => { inside.Set(); release.Wait(5000); });
            bus.Publish("x");
            Check.True(inside.Wait(5000), "the subscriber wedged on its event");
            var stop = new ManualResetEventSlim();
            var flood = Task.Run(() => { while (!stop.IsSet) while (!stop.IsSet && bus.Backlog < input) bus.Publish("x"); });
            await WaitFor(() => bus.Backlog == input, "the input saturated");
            var flush = bus.FlushAsync();
            await Task.Delay(100);
            Check.False(flush.IsCompleted, "the flush waits: its marker cannot pass the wedged line");
            await bus.DisposeAsync();   // the writer completes: the pending write sees the closed channel, the wedged worker is given up on
            release.Set();
            stop.Set();
            await flood;
            await flush.WaitAsync(TimeSpan.FromSeconds(10));   // it comes back: a dispose must never leave a flush waiting
        });

        r.Add("bus: disposing a subscription lets its worker finish what was already queued", async () =>
        {
            await using var bus = new EventBus(NullLogger.Instance);
            var seen = new List<int>();
            var inside = new ManualResetEventSlim();
            var release = new ManualResetEventSlim();
            var sub = bus.Subscribe("t", e =>
            {
                seen.Add((int)e.Data!);
                if (seen.Count == 1) { inside.Set(); release.Wait(5000); }   // hold the first event: 2 and 3 queue behind it
            });
            for (var i = 1; i <= 3; i++) bus.Publish("t", i);
            Check.True(inside.Wait(5000), "the worker is inside the first event, with two queued behind it");
            sub.Dispose();
            release.Set();
            await bus.FlushAsync();
            await WaitFor(() => seen.Count == 3, "the worker drained what was queued: " + string.Join(",", seen));
        });

        r.Add("bus: a flush whose marker is owed to a disposed subscriber completes when it goes away", async () =>
        {
            await using var bus = new EventBus(NullLogger.Instance, queueCapacity: 4);
            var wedged = new ManualResetEventSlim();
            var release = new ManualResetEventSlim();
            var ticked = 0;
            var slow = bus.Subscribe("slow", _ => { wedged.Set(); release.Wait(TimeSpan.FromSeconds(30)); });
            using var fast = bus.Subscribe("tick", _ => Interlocked.Increment(ref ticked));
            try
            {
                // One event first, and wait for the worker to be inside it: its queue is empty and, wedged as it is, the
                // worker will take nothing more. Only then is the queue full *and staying full* when the marker is offered
                // (fill it before the worker takes its first event and it drains a slot again, and the marker would fit).
                bus.Publish("slow", 1);
                Check.True(wedged.Wait(5000), "the subscriber wedged on its first event");
                for (var i = 2; i <= 11; i++) bus.Publish("slow", i);
                await WaitFor(() => bus.Dropped >= 6, "the wedged line's queue of 4 to fill (and the rest to be dropped for it alone)");
                await WaitFor(() => bus.Backlog == 0, "the dispatcher to fan every event out");

                var flush = bus.FlushAsync();   // the marker is written to the input before this call returns
                bus.Publish("tick");            // so the dispatcher reaches the marker before this event
                await WaitFor(() => Volatile.Read(ref ticked) == 1, "the fast line to see the event published after the flush");
                Check.False(flush.IsCompleted, "the flush waits for the wedged line, which cannot hold the marker");

                slow.Dispose();   // the plugin is unloaded while its line still owes the marker

                await flush.WaitAsync(TimeSpan.FromSeconds(5));   // it comes back: the marker is acked for the line that went away
            }
            finally { release.Set(); }
        });

        r.Add("bus: a completed flush is never reported as stale, no matter how long ago it started", async () =>
        {
            await using var bus = new EventBus(NullLogger.Instance, staleFlushAfter: TimeSpan.FromMilliseconds(200));
            var started = new ManualResetEventSlim();
            var release = new ManualResetEventSlim();
            using var _ = bus.Subscribe("t", e => { started.Set(); release.Wait(5000); });   // wedged inside its handler
            bus.Publish("t");
            Check.True(started.Wait(5000), "the subscriber is inside its handler");
            var flush = bus.FlushAsync();   // its marker waits on the wedged line
            await Task.Delay(400);         // well past the stale threshold: while it lasts, it is named
            Check.True(bus.StaleFlush() is not null, "the wedged flush is reported while it lasts: " + bus.StaleFlush());
            release.Set();
            await flush.WaitAsync(TimeSpan.FromSeconds(5));
            for (var i = 0; i < 5; i++)
            {
                Check.Equal(null, bus.StaleFlush(), "a completed flush is never reported, however long ago it started");
                await Task.Delay(50);
            }
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

        r.Add("bus: Activity names the handler a subscriber's worker is inside, and says nothing between handlers", async () =>
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

        r.Add("watchdog: a wedged handler is named in the log with its own queue depth, and its release is logged", async () =>
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
            await WaitFor(() => log.Has("handler 'agent.status' has been running"), "a stall report naming the handler: " + log.Dump());
            Check.True(log.Has("1 event(s) wait in its queue"), "it says how deep that subscription's queue is: " + log.Dump());
            release.Set();
            await WaitFor(() => log.Has("handler it was stuck in came back"), "the release is logged");
            await bus.FlushAsync();
            var reports = log.Lines.Count(l => l.Contains("has been running"));
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
            await WaitFor(() => log.Has("The database gate is free again"), "its release is logged: " + log.Dump());
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

        r.Add("bus: a slow handler is named in the log once per report interval, with how often and how slow", async () =>
        {
            var log = new CapturingLogger();
            await using var bus = new EventBus(log, slowHandler: TimeSpan.FromMilliseconds(40), slowReportEvery: TimeSpan.FromHours(1));
            using var slow = bus.Subscribe("laggy.*", _ => Thread.Sleep(80));
            using var fast = bus.Subscribe("quick.*", _ => { });
            for (var i = 0; i < 3; i++) bus.Publish("laggy.one");
            for (var i = 0; i < 20; i++) bus.Publish("quick.one");
            await bus.FlushAsync();
            var reports = log.Lines.Where(l => l.Contains("is slow")).ToList();
            Check.Equal(1, reports.Count, "one report, not one per slow call: " + log.Dump());
            Check.Contains(reports[0], "'laggy.*'", "it names the subscription");
            Check.Contains(reports[0], "'laggy.one'", "and the event it was slow on");
            Check.False(log.Lines.Any(l => l.Contains("'quick.*'")), "a fast handler is never named");
        });

        r.Add("bus: a handler that is slow again after the interval is reported again, with the calls since", async () =>
        {
            var log = new CapturingLogger();
            await using var bus = new EventBus(log, slowHandler: TimeSpan.FromMilliseconds(30), slowReportEvery: TimeSpan.FromMilliseconds(150));
            using var slow = bus.Subscribe("laggy", _ => Thread.Sleep(50));
            bus.Publish("laggy");
            await bus.FlushAsync();
            Check.Equal(1, log.Lines.Count(l => l.Contains("is slow")), "the first call is reported at once: " + log.Dump());
            bus.Publish("laggy");   // inside the interval: counted, not reported
            await bus.FlushAsync();
            Check.Equal(1, log.Lines.Count(l => l.Contains("is slow")), "inside the interval: still one: " + log.Dump());
            await Task.Delay(200);
            bus.Publish("laggy");
            await bus.FlushAsync();
            var reports = log.Lines.Where(l => l.Contains("is slow")).ToList();
            Check.Equal(2, reports.Count, "after the interval it is reported again: " + log.Dump());
            Check.Contains(reports[1], "2 call(s)", "with the calls since the last report");
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
