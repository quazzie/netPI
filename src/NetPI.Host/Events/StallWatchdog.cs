using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace NetPI.Host.Events;

/// <summary>
/// Says so, in the log, when the process stops making progress: the event bus inside one handler for too long (delivery is
/// one task, so everything behind it waits, the UI fan-out included), the bus not delivering with events queued, or the
/// thread pool not starting work that was queued to it (every blocked call and every RPC then waits too).
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
    private Episode _handler, _dispatcher, _pool;

    /// <param name="queue">Where the pool probe is queued; the thread pool unless a test stands in for a pool that does not run it.</param>
    public StallWatchdog(EventBus bus, ILogger log, TimeSpan? threshold = null, TimeSpan? tick = null, Action<Action>? queue = null)
    {
        _bus = bus;
        _log = log;
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
                // one handler holding the dispatcher
                Report(ref _handler, a.Pattern is not null && a.Elapsed >= _threshold,
                    () => $"The event bus has been inside the handler '{a.Pattern}' on '{a.Type}' for {a.Elapsed.TotalSeconds:0.#}s; " +
                          $"{Math.Max(0, a.Backlog - 1)} event(s) wait behind it, and so does everything that listens to them (the UI included)",
                    "The event bus left the handler it was stuck in");

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
