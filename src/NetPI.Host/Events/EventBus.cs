using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace NetPI.Host.Events;

/// <summary>A moment of the bus: the handler a subscriber's worker is inside (null between handlers) and how long it has been there.</summary>
internal readonly record struct BusActivity(string? Type, string? Pattern, TimeSpan Elapsed, long Delivered, int Backlog);

/// <summary>
/// In-process event bus. <see cref="Publish(BusEvent)"/> never blocks: events go to a bounded dispatcher channel,
/// and a single dispatcher hands each event, in publish order, to every matching subscriber's own bounded queue.
/// Each subscriber is delivered by its own worker (one event at a time, in queue order), so one slow or wedged
/// subscriber holds only its own line; the others keep flowing. A subscriber whose queue fills up drops for itself
/// (counted and logged) rather than stalling the bus. <see cref="BusEvent.Seq"/> is assigned and the ring buffer
/// kept by the dispatcher. The order in which *different* subscribers' handlers run for one event is not a
/// contract: within one subscriber everything stays in order.
/// </summary>
internal sealed class EventBus : IEventBus, IAsyncDisposable
{
    public const int RingSize = 500;
    /// <summary>
    /// How long one async handler may hold its worker before it is let go of (it keeps running in the background and
    /// is logged). A sync handler is given no rope: it cannot yield, so it holds only its own line until it returns.
    /// </summary>
    public static readonly TimeSpan HandlerTimeout = TimeSpan.FromSeconds(30);
    /// <summary>Input backlog past which the bus says so. The channel is bounded, so past this it drops (counted and logged).</summary>
    public const int BacklogWarnAt = 1000;
    /// <summary>How many events one subscriber may hold before it starts dropping for itself.</summary>
    public const int DefaultQueueCapacity = 2048;
    /// <summary>How many events the dispatcher channel holds before the bus drops (last-resort memory ceiling).</summary>
    public const int DefaultInputCapacity = 65536;
    /// <summary>A handler call longer than this is slow: its own queue (and only it) waits as long.</summary>
    public static readonly TimeSpan SlowHandler = TimeSpan.FromMilliseconds(250);
    /// <summary>How often one subscriber's slowness is reported (a handler that is slow every time would otherwise log every event).</summary>
    public static readonly TimeSpan SlowReportEvery = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DropReportEvery = TimeSpan.FromSeconds(30);

    private readonly Channel<object> _queue;
    private readonly ILogger _log;
    private readonly int _queueCapacity;
    private readonly TimeSpan _slowAfter, _slowEvery;
    private readonly Lock _subsLock = new();
    private readonly Lock _ringLock = new();
    private readonly BusEvent?[] _ring = new BusEvent?[RingSize];
    private readonly Task _dispatcher;
    private Subscription[] _subs = [];
    private int _ringNext, _ringCount;
    private long _seq;
    private long _dropped;
    private long _backlog;
    private int _warnedBacklog;
    private long _delivered;
    // Flush markers that could not be queued to a full subscriber's line; retried by the dispatcher until they fit.
    private readonly List<(Subscription Sub, FlushMarker Marker)> _debt = [];
    private readonly Lock _debtLock = new();
    // Flushes still in flight: a flush that does not come back is named in the log (see StaleFlush).
    private readonly List<FlushMarker> _flushes = [];
    private readonly Lock _flushLock = new();

    public EventBus(ILogger log, TimeSpan? slowHandler = null, TimeSpan? slowReportEvery = null, int? queueCapacity = null, int? inputCapacity = null)
    {
        _log = log;
        _slowAfter = slowHandler ?? SlowHandler;
        _slowEvery = slowReportEvery ?? SlowReportEvery;
        _queueCapacity = queueCapacity ?? DefaultQueueCapacity;
        _queue = Channel.CreateBounded<object>(new BoundedChannelOptions(inputCapacity ?? DefaultInputCapacity) { SingleReader = true });
        _dispatcher = Task.Run(DispatchLoopAsync);
    }

    public int SubscriberCount => Volatile.Read(ref _subs).Length;

    /// <summary>Events dropped at the input channel or for a full subscriber line. Zero in practice: both are last-resort ceilings.</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    /// <summary>Events published and still waiting for fan-out.</summary>
    public int Backlog => (int)Math.Min(int.MaxValue, Interlocked.Read(ref _backlog));

    /// <summary>
    /// The longest handler any subscriber's worker is inside (null when none), with that subscription's queue depth:
    /// how a stalled bus names its culprit (see <see cref="StallWatchdog"/>).
    /// </summary>
    public BusActivity Activity()
    {
        var best = -1L;
        string? type = null, pattern = null;
        var depth = 0;
        foreach (var s in Volatile.Read(ref _subs))
        {
            var since = Volatile.Read(ref s.InSince);
            if (since == 0) continue;
            var ts = Stopwatch.GetTimestamp();
            if (ts - since > best)
            {
                best = ts - since;
                type = s.InType;
                pattern = s.Pattern;
                depth = s.Depth;
            }
        }
        return pattern is null
            ? new BusActivity(null, null, TimeSpan.Zero, Interlocked.Read(ref _delivered), Backlog)
            : new BusActivity(type, pattern, TimeSpan.FromTicks(best), Interlocked.Read(ref _delivered), depth);
    }

    public void Publish(BusEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        if (string.IsNullOrEmpty(evt.Type)) throw new ArgumentException("Event type is required", nameof(evt));
        var depth = (int)Interlocked.Increment(ref _backlog) + 1;
        if (depth >= BacklogWarnAt && depth >= _warnedBacklog * 2)
        {
            _warnedBacklog = Math.Min(int.MaxValue, depth);
            _log.LogWarning("The event bus is {Depth} events behind: a subscriber is slow, or events arrive faster than they are fanned out", depth);
        }
        if (!_queue.Writer.TryWrite(evt)) { Interlocked.Decrement(ref _backlog); Interlocked.Increment(ref _dropped); _log.LogError("The event bus input is full: dropped an event of type '{Type}' (the dispatcher is not keeping up)", evt.Type); }
    }

    public void Publish(string type, object? data = null, string? sessionId = null, bool ui = true) =>
        Publish(new BusEvent { Type = type, Data = data, SessionId = sessionId, Ui = ui });

    public IDisposable Subscribe(string pattern, Action<BusEvent> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return Add(new Subscription(pattern, handler, null, _queueCapacity, _log, _slowAfter, _slowEvery));
    }

    public IDisposable SubscribeAsync(string pattern, Func<BusEvent, ValueTask> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return Add(new Subscription(pattern, null, handler, _queueCapacity, _log, _slowAfter, _slowEvery));
    }

    public IReadOnlyList<BusEvent> Recent(int max = 200)
    {
        lock (_ringLock)
        {
            var n = Math.Clamp(max, 0, _ringCount);
            var list = new List<BusEvent>(n);
            var start = (_ringNext - n + RingSize) % RingSize;
            for (var i = 0; i < n; i++) list.Add(_ring[(start + i) % RingSize]!);
            return list;
        }
    }

    /// <summary>
    /// Completes once every subscriber has processed every event published before the call: the marker rides the
    /// dispatcher in order, and each subscriber's worker passes it after its own line. A wedged subscriber holds the
    /// flush for itself (callers bound it with a timeout); it no longer holds the dispatcher.
    /// </summary>
    public async Task FlushAsync()
    {
        var marker = new FlushMarker();
        lock (_flushLock) _flushes.Add(marker);
        bool wrote = false;
        try
        {
            await _queue.Writer.WaitToWriteAsync().ConfigureAwait(false);   // the input is only full while the dispatcher is behind
            wrote = _queue.Writer.TryWrite(marker);
        }
        catch (ChannelClosedException) { }
        if (!wrote) marker.Done.TrySetResult();   // the bus is going away: nothing is left to wait for
        marker.Done.Task.ContinueWith(f => { lock (_flushLock) _flushes.RemoveAll(m => ReferenceEquals(m, f)); },
            TaskContinuationOptions.ExecuteSynchronously);
        await marker.Done.Task.ConfigureAwait(false);   // done when every live line has passed the marker
    }

    /// <summary>
    /// A flush that should be back by now (5 s and counting), with who holds it: which lines are inside a handler or
    /// have work queued behind the marker — and the saying a stalled server leaves behind instead of an empty log.
    /// The watchdog logs it once per flush and then at most once per 10 s.
    /// </summary>
    public string? StaleFlush()
    {
        FlushMarker? oldest = null;
        lock (_flushLock)
        {
            foreach (var f in _flushes)
            {
                var age = Stopwatch.GetElapsedTime(f.Started);
                if (age < TimeSpan.FromSeconds(5)) continue;
                if (oldest is null || age > Stopwatch.GetElapsedTime(oldest.Started)) oldest = f;
            }
        }
        if (oldest is null) return null;
        var marker = oldest;
        if (marker.Warned && marker.WarnedFor == (int)Stopwatch.GetElapsedTime(marker.Started).TotalSeconds / 10) return null;   // re-say at most once per 10 s
        marker.Warned = true;
        marker.WarnedFor = (int)Stopwatch.GetElapsedTime(marker.Started).TotalSeconds / 10;

        var holders = new List<string>();
        foreach (var s in Volatile.Read(ref _subs))
        {
            if (s.InSince != 0) holders.Add($"'{s.Pattern}' is {Stopwatch.GetElapsedTime(s.InSince).TotalSeconds:0.#}s inside '{s.InType}'");
            else if (s.Depth > 0) holders.Add($"'{s.Pattern}' has {s.Depth} event(s) queued");
        }
        var holding = holders.Count == 0
            ? $"but no line has work queued or a handler running: its marker was not delivered to {marker.Unacked} line(s)"
            : string.Join("; ", holders);
        return $"A flush has not completed for {Stopwatch.GetElapsedTime(marker.Started).TotalSeconds:0.#}s; {marker.Unacked} line(s) have not passed its marker; {holding}";
    }

    /// <summary>
    /// Replace ring-buffer payloads whose type lives in a collectible (plugin) load context with a JSON snapshot,
    /// so the buffer does not keep an unloaded plugin alive.
    /// </summary>
    public void DetachCollectible()
    {
        lock (_ringLock)
        {
            for (var i = 0; i < RingSize; i++)
            {
                var e = _ring[i];
                if (e?.Data is null || !e.Data.GetType().IsCollectible) continue;
                JsonElement snapshot;
                try { snapshot = NetPiJson.ToElement(e.Data); }
                catch { snapshot = default; }
                _ring[i] = new BusEvent
                {
                    Type = e.Type, Data = snapshot.ValueKind == JsonValueKind.Undefined ? null : snapshot,
                    SessionId = e.SessionId, Source = e.Source, Ui = e.Ui, Time = e.Time, Seq = e.Seq,
                };
            }
        }
    }

    private Registration Add(Subscription sub)
    {
        sub.Start();
        lock (_subsLock) _subs = [.. _subs, sub];
        return new Registration(() =>
        {
            sub.Disposed = true;
            sub.Completed = true;
            sub.Queue.Writer.TryComplete();   // the worker finishes what is queued, then exits
            lock (_subsLock) _subs = Array.FindAll(_subs, s => !ReferenceEquals(s, sub));
            lock (_debtLock)
            {
                // A marker still owed to this line can never reach it now: ack it for every such marker, or the
                // flush it belongs to waits for a line that no longer exists (and says so for as long as it lives).
                foreach (var (debtSub, marker) in _debt)
                    if (ReferenceEquals(debtSub, sub)) marker.Drop(sub);
                _debt.RemoveAll(d => ReferenceEquals(d.Sub, sub));
            }
        });
    }

    private async Task DispatchLoopAsync()
    {
        var reader = _queue.Reader;
        while (true)
        {
            // A flush marker waiting on a full subscriber's line still has to get there: retry it on a slow tick,
            // and keep delivering whatever input arrives in the meantime — even when the bus is otherwise quiet.
            if (HasDebt())
            {
                RetryDebt();
                while (HasDebt())
                {
                    while (reader.TryRead(out var item)) Deliver(item);
                    RetryDebt();
                    if (HasDebt()) await Task.Delay(20).ConfigureAwait(false);
                }
            }
            if (!await reader.WaitToReadAsync().ConfigureAwait(false)) return;
            while (reader.TryRead(out var item)) Deliver(item);
        }
    }

    /// <summary>One item off the dispatcher channel: a marker is fanned to the lines, an event to its subscribers.</summary>
    private void Deliver(object item)
    {
        if (item is FlushMarker marker)
        {
            EnqueueMarker(marker);
            return;
        }
        Interlocked.Decrement(ref _backlog);
        var evt = (BusEvent)item;
        item = null!;   // don't hold the event (possibly a plugin payload) in this long-lived state machine
        Dispatch(evt);
    }

    /// <summary>Hand one event to every matching subscriber's line. Never invokes a handler, never blocks.</summary>
    private void Dispatch(BusEvent evt)
    {
        evt.Seq = ++_seq;
        AddToRing(evt);
        foreach (var s in Volatile.Read(ref _subs))
        {
            if (s.Disposed || !s.Matches(evt.Type)) continue;
            if (!s.Offer(evt) && !s.Completed)
            {
                Interlocked.Increment(ref _dropped);
                s.NoteDrop(evt.Type);
            }
        }
        Interlocked.Increment(ref _delivered);
    }

    /// <summary>Put a flush marker on every live subscriber's line; the ones that cannot hold it are remembered as debt.</summary>
    private void EnqueueMarker(FlushMarker marker)
    {
        foreach (var s in Volatile.Read(ref _subs))
        {
            if (s.Disposed) continue;
            marker.Target(s);                       // the flush waits for this line
            if (s.Offer(marker)) continue;
            if (s.Completed) { marker.Drop(s); continue; }   // went away in the meantime
            lock (_debtLock) _debt.Add((s, marker));
        }
        marker.TargetsFinalized();   // no line joins the flush from this point
    }

    private bool HasDebt() { lock (_debtLock) return _debt.Count > 0; }

    private void RetryDebt()
    {
        lock (_debtLock)
        {
            for (var i = _debt.Count - 1; i >= 0; i--)
            {
                var (sub, marker) = _debt[i];
                if (sub.Completed) { marker.Drop(sub); _debt.RemoveAt(i); continue; }   // it went away before the marker reached it
                if (sub.Offer(marker)) _debt.RemoveAt(i);   // in the queue now: the line acks when it passes it
            }
        }
    }

    private void AddToRing(BusEvent evt)
    {
        lock (_ringLock)
        {
            _ring[_ringNext] = evt;
            _ringNext = (_ringNext + 1) % RingSize;
            if (_ringCount < RingSize) _ringCount++;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();
        try { await _dispatcher.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
        catch { /* debt on a wedged subscriber: give up; the process exit reaps it */ }
        foreach (var s in Volatile.Read(ref _subs)) { s.Completed = true; s.Queue.Writer.TryComplete(); }
        var workers = Volatile.Read(ref _subs).Select(s => s.Worker).ToArray();
        try { await Task.WhenAll(workers).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
        catch { /* a wedged handler holds its worker: give up */ }
        lock (_subsLock) _subs = [];
    }

    /// <summary>
    /// One subscriber's delivery line: its own bounded queue, one worker, one event at a time in queue order.
    /// The worker is the unit of isolation: a handler that is slow or never returns holds this line and nothing else.
    /// </summary>
    private sealed class Subscription
    {
        private readonly ILogger _log;
        private readonly TimeSpan _slowAfter, _slowEvery;
        private long _dropWarnedAt;
        private int _calls;
        private TimeSpan _slowest;
        private string _slowestType = "";
        private long _slowReportedAt;

        public Subscription(string pattern, Action<BusEvent>? sync, Func<BusEvent, ValueTask>? async, int capacity, ILogger log, TimeSpan slowAfter, TimeSpan slowEvery)
        {
            Pattern = pattern.Trim();
            if (Pattern.Length == 0) throw new ArgumentException("Empty pattern");
            if (Pattern == "*") { _kind = 0; _match = ""; }
            else if (Pattern.EndsWith(".*", StringComparison.Ordinal)) { _kind = 2; _match = Pattern[..^1]; }   // keep the dot
            else { _kind = 1; _match = Pattern; }
            Sync = sync;
            Async = async;
            _log = log;
            _slowAfter = slowAfter;
            _slowEvery = slowEvery;
            _capacity = capacity;
            Queue = Channel.CreateBounded<object>(new BoundedChannelOptions(capacity) { SingleReader = true });
        }

        public string Pattern { get; }
        public Action<BusEvent>? Sync { get; }
        public Func<BusEvent, ValueTask>? Async { get; }
        public Channel<object> Queue { get; }
        public Task Worker { get; private set; } = Task.CompletedTask;
        public volatile bool Disposed;
        // Whether the queue can still take items: CanWrite has no public surface on ChannelWriter (net10), so we keep it.
        // Set where the queue is completed: dispose and bus teardown.
        public volatile bool Completed;
        private readonly int _kind;   // 0 = all, 1 = exact, 2 = prefix
        private readonly string _match;
        private readonly int _capacity;
        private int _depth;   // events in the queue: how deep this line's backlog is (for Activity and the drop warning)

        /// <summary>Events in this line's queue right now.</summary>
        public int Depth => Volatile.Read(ref _depth);

        // What this worker is inside right now (for Activity): written by the worker, read racily.
        internal volatile string? InType;
        internal volatile string? InPattern;
        internal long InSince;

        public bool Matches(string type) => _kind switch
        {
            0 => true,
            1 => string.Equals(type, _match, StringComparison.Ordinal),
            _ => type.StartsWith(_match, StringComparison.Ordinal),
        };

        public void Start() => Worker = Task.Run(RunAsync);

        /// <summary>Put an event (or marker) on this line. False when it cannot be held: queue full or going away.</summary>
        public bool Offer(object item)
        {
            if (!Queue.Writer.TryWrite(item)) return false;
            Interlocked.Increment(ref _depth);
            return true;
        }

        /// <summary>An item left the queue (the worker took it).</summary>
        public void Released() => Interlocked.Decrement(ref _depth);

        /// <summary>Says a line is dropping, at most once per interval (a full queue would otherwise log every event).</summary>
        public void NoteDrop(string type)
        {
            var now = Stopwatch.GetTimestamp();
            if (_dropWarnedAt != 0 && Stopwatch.GetElapsedTime(_dropWarnedAt, now) < DropReportEvery) return;
            _dropWarnedAt = now;
            _log.LogWarning("Event handler '{Pattern}' is falling behind: it is dropping events (its queue of {Cap} is full). " +
                            "Only it is affected; the other subscribers keep receiving", Pattern, _capacity);
        }

        private async Task RunAsync()
        {
            await foreach (var next in Queue.Reader.ReadAllAsync())
            {
                Released();
                if (next is FlushMarker marker)
                {
                    marker.Ack(this);
                    continue;
                }
                object item = next;
                var evt = (BusEvent)item;
                item = null!;   // the worker waits long between events; don't hold a (plugin) payload in the frame meanwhile
                InType = evt.Type;
                InPattern = Pattern;
                var since = Stopwatch.GetTimestamp();
                Volatile.Write(ref InSince, since);
                try
                {
                    if (Sync is not null)
                    {
                        Sync(evt);
                    }
                    else
                    {
                        var vt = Async!(evt);
                        // A sync handler is given no rope (it cannot yield); an async one is let go of after HandlerTimeout,
                        // so one wedged subscriber holds its own line instead of the bus.
                        if (!vt.IsCompletedSuccessfully)
                        {
                            var task = vt.AsTask();
                            try { await task.WaitAsync(HandlerTimeout).ConfigureAwait(false); }
                            catch (TimeoutException)
                            {
                                _log.LogError("Event handler '{Pattern}' is stuck on '{Type}' (over {Timeout}s); its line moved on without it",
                                    Pattern, evt.Type, HandlerTimeout.TotalSeconds);
                                // Nobody awaits it any more, so watch it: a fault after the timeout must not surface as an
                                // unobserved task exception (it is already logged as the handler's own failure).
                                _ = task.ContinueWith(static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Event handler '{Pattern}' failed on '{Type}'", Pattern, evt.Type);
                }
                finally
                {
                    var began = Volatile.Read(ref InSince);
                    Volatile.Write(ref InSince, 0);
                    if (began != 0) NoteDuration(evt.Type, Stopwatch.GetElapsedTime(began));
                }
            }
        }

        /// <summary>Counts a slow call and, at most once per interval, says how slow and how often.</summary>
        private void NoteDuration(string type, TimeSpan took)
        {
            if (took < _slowAfter) return;
            _calls++;
            if (took > _slowest) { _slowest = took; _slowestType = type; }
            var now = Stopwatch.GetTimestamp();
            if (_slowReportedAt != 0 && Stopwatch.GetElapsedTime(_slowReportedAt, now) < _slowEvery) return;
            _slowReportedAt = now;
            _log.LogWarning("Event handler '{Pattern}' is slow: {Calls} call(s) took over {Limit} ms since the last report, the slowest {Took} ms on '{Type}'. " +
                            "Only its own line waited for them; the other subscribers kept flowing",
                Pattern, _calls, (int)_slowAfter.TotalMilliseconds, (int)_slowest.TotalMilliseconds, _slowestType);
            _calls = 0;
            _slowest = TimeSpan.Zero;
        }
    }

    /// <summary>
    /// A flush marker: the flush completes when every line that was live at the call has passed the marker
    /// (acked), or went away before it was reached. A line whose queue was full keeps the flush open until the
    /// marker gets to it and it passes it — a fast line cannot complete the flush for lines the marker has not
    /// reached yet.
    /// </summary>
    private sealed class FlushMarker
    {
        public readonly TaskCompletionSource Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly long Started = Stopwatch.GetTimestamp();
        private readonly object _sync = new();
        private readonly HashSet<Subscription> _outstanding = new();
        private bool _finalized;
        public bool Warned;
        public int WarnedFor;

        /// <summary>Lines the marker still has to pass (for the stale-flush log).</summary>
        public int Unacked { get { lock (_sync) return _outstanding.Count; } }

        /// <summary>A line that was live at the call: the flush waits for it.</summary>
        public void Target(Subscription sub)
        {
            lock (_sync)
            {
                _outstanding.Add(sub);
                if (_finalized) CheckDone();
            }
        }

        /// <summary>The live snapshot is complete: no line joins the flush from this point.</summary>
        public void TargetsFinalized()
        {
            lock (_sync)
            {
                _finalized = true;
                CheckDone();
            }
        }

        /// <summary>A line passed the marker (its worker says so).</summary>
        public void Ack(Subscription sub)
        {
            lock (_sync)
            {
                _outstanding.Remove(sub);
                CheckDone();
            }
        }

        /// <summary>A line went away before the marker reached it: the flush no longer waits for it.</summary>
        public void Drop(Subscription sub)
        {
            lock (_sync)
            {
                _outstanding.Remove(sub);
                CheckDone();
            }
        }

        private void CheckDone()
        {
            if (_finalized && _outstanding.Count == 0) Done.TrySetResult();
        }
    }
}
