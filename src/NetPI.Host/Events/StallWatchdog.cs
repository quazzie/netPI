using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace NetPI.Host.Events;

/// <summary>
/// Says so, in the log, when the process stops making progress: a subscriber's handler running too long (its own
/// queue — and only it — falls behind), the bus not delivering with events queued, the thread pool not starting work
/// that was queued to it (every blocked call and every RPC then waits too), or the database gate that nothing can take
/// (every statement of the process goes through it: a deadlock on it freezes all of them).
/// <para>
/// It runs on a thread of its own, never on the pool: a watchdog that needs the thing it watches goes quiet exactly when it
/// is needed. It only reads counters, logs on the edge of a stall (and now and then while it lasts) and again when it ends,
/// and does nothing else, so a stalled server leaves its own explanation behind instead of an empty log.
/// </para>
/// </summary>
internal sealed class StallWatchdog : IDisposable
{
    public static readonly TimeSpan DefaultThreshold = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Reminder = TimeSpan.FromSeconds(30);

    private readonly EventBus _bus;
    private readonly ILogger _log;
    private readonly TimeSpan _threshold;
    private readonly TimeSpan _tick;
    private readonly Action<Action> _queue;
    private readonly ManualResetEventSlim _stop = new(false);
    private readonly Thread _thread;

    // thread pool probe: a work item queued each tick; while the last one has not started, the pool is not running work
    private int _probePending;
    private long _probeQueuedAt;

    // the episodes in progress: when each began (Stopwatch timestamp) and when it was last reported
    private Episode _handler, _dispatcher, _pool, _gateEpisode;
    private readonly object? _gate;
    private int _gateMisses;

    /// <param name="queue">Where the pool probe is queued; the thread pool unless a test stands in for a pool that does not run it.</param>
    /// <param name="gate">A lock every statement of the process runs under (the database's): tried without waiting each tick, and a stretch where it
    /// cannot be taken for the threshold is reported. Null: not watched.</param>
    public StallWatchdog(EventBus bus, ILogger log, TimeSpan? threshold = null, TimeSpan? tick = null, Action<Action>? queue = null, object? gate = null)
    {
        _bus = bus;
        _log = log;
        _gate = gate;
        _queue = queue ?? (work => ThreadPool.UnsafeQueueUserWorkItem(static w => ((Action)w!)(), work));
        _threshold = threshold ?? DefaultThreshold;
        _tick = tick ?? TimeSpan.FromSeconds(1);
        _thread = new Thread(Loop) { IsBackground = true, Name = "netpi-watchdog" };
        _thread.Start();
    }

    private void Loop()
    {
        long delivered = -1;
        var deliveredAt = Stopwatch.GetTimestamp();
        while (!_stop.Wait(_tick))
        {
            try
            {
                var a = _bus.Activity();
                // one handler holding its subscriber's line for too long
                Report(ref _handler, a.Pattern is not null && a.Elapsed >= _threshold,
                    () => $"Event handler '{a.Pattern}' has been running for {a.Elapsed.TotalSeconds:0.#}s on '{a.Type}'; {a.Backlog} event(s) wait in its queue (only it is delayed, the other subscribers keep receiving)",
                    "The handler it was stuck in came back");

                // a flush that should be back by now: who is holding its marker
                var stale = _bus.StaleFlush();
                if (stale is not null) _log.LogWarning("{Stale}", stale);

                // events queued and none delivered, with no handler to blame: the dispatcher itself is not running
                if (a.Delivered != delivered || a.Backlog == 0) { delivered = a.Delivered; deliveredAt = Stopwatch.GetTimestamp(); }
                var still = Stopwatch.GetElapsedTime(deliveredAt);
                Report(ref _dispatcher, a.Pattern is null && a.Backlog > 0 && still >= _threshold,
                    () => $"The event bus has not delivered anything for {still.TotalSeconds:0.#}s with {a.Backlog} event(s) queued and no handler running (the dispatcher is not being scheduled)",
                    "The event bus is delivering again");

                // the thread pool: has the work item queued a tick (or more) ago started?
                if (Interlocked.CompareExchange(ref _probePending, 1, 0) == 0)
                {
                    Volatile.Write(ref _probeQueuedAt, Stopwatch.GetTimestamp());
                    _queue(ProbeRan);
                }
                var waited = Volatile.Read(ref _probePending) == 1 ? Stopwatch.GetElapsedTime(Volatile.Read(ref _probeQueuedAt)) : TimeSpan.Zero;
                Report(ref _pool, waited >= _threshold,
                    () => $"The thread pool has not started a queued work item for {waited.TotalSeconds:0.#}s " +
                          $"(threads {ThreadPool.ThreadCount}, pending items {ThreadPool.PendingWorkItemCount}, completed {ThreadPool.CompletedWorkItemCount}): " +
                          "its threads are blocked, so no RPC, event or continuation can run",
                    "The thread pool is running work again");

                // the database gate: held by a thread that is not coming back (a deadlock, or a statement that never returns)?
                if (_gate is not null)
                {
                    var free = Monitor.TryEnter(_gate, TimeSpan.FromMilliseconds(Math.Min(100, _tick.TotalMilliseconds * 2)));
                    if (free) Monitor.Exit(_gate);
                    _gateMisses = free ? 0 : _gateMisses + 1;
                    var heldFor = TimeSpan.FromTicks(_tick.Ticks * _gateMisses);
                    Report(ref _gateEpisode, _gateMisses > 0 && heldFor >= _threshold,
                        () => $"The database gate has not been free for {heldFor.TotalSeconds:0.#}s: a thread holds it and is not coming back (a deadlock, or a statement that never returns). " +
                              "Every statement of the process waits for it (every RPC that reads or writes a session, a message, a setting stored in the database), " +
                              "while calls that touch no database (/api/health, app.info) still answer",
                        "The database gate is free again");
                }
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "The stall watchdog's check failed");
            }
        }
    }

    private void ProbeRan()
    {
        Volatile.Write(ref _probePending, 0);
    }

    /// <summary>One episode's log lines: on its start, every <see cref="Reminder"/> while it lasts, and when it ends.</summary>
    private void Report(ref Episode e, bool stalled, Func<string> what, string ended)
    {
        var now = Stopwatch.GetTimestamp();
        if (stalled)
        {
            if (e.Since == 0)
            {
                e.Since = now;
                e.Reported = now;
                _log.LogWarning("{What}", what());
            }
            else if (Stopwatch.GetElapsedTime(e.Reported, now) >= Reminder)
            {
                e.Reported = now;
                _log.LogWarning("Still stalled: {What}", what());
            }
        }
        else if (e.Since != 0)
        {
            _log.LogWarning("{Ended} after {Seconds:0.#}s", ended, Stopwatch.GetElapsedTime(e.Since, now).TotalSeconds);
            e = default;
        }
    }

    public void Dispose()
    {
        _stop.Set();
        if (_thread.IsAlive && Thread.CurrentThread != _thread) _thread.Join(TimeSpan.FromSeconds(2));
    }

    private struct Episode
    {
        public long Since;
        public long Reported;
    }
}
