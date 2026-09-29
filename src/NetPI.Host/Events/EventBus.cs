using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace NetPI.Host.Events;

/// <summary>
/// In-process event bus. <see cref="Publish(BusEvent)"/> never blocks: events are queued on an unbounded channel and
/// a single dispatcher task delivers them, in publish order, to every matching subscriber (exact type, <c>prefix.*</c>
/// or <c>*</c>). The dispatcher assigns <see cref="BusEvent.Seq"/> and keeps a ring buffer of recent events.
/// </summary>
internal sealed class EventBus : IEventBus, IAsyncDisposable
{
    public const int RingSize = 500;
    /// <summary>
    /// How long one subscriber may hold the dispatcher before it is called stuck. Delivery is one task, one event at a
    /// time, so a handler that never returns would otherwise freeze every later event — including the UI fan-out, which
    /// is itself just a subscriber. A handler that outlives this keeps running; it is only let go of, and it is logged.
    /// </summary>
    public static readonly TimeSpan HandlerTimeout = TimeSpan.FromSeconds(30);
    /// <summary>Backlog past which the bus says so. It never drops events; the log is the signal, not a lossy queue.</summary>
    public const int BacklogWarnAt = 1000;

    private readonly Channel<object> _queue = Channel.CreateUnbounded<object>(new UnboundedChannelOptions { SingleReader = true });
    private readonly ILogger _log;
    private readonly Lock _subsLock = new();
    private readonly Lock _ringLock = new();
    private readonly BusEvent?[] _ring = new BusEvent?[RingSize];
    private readonly Task _dispatcher;
    private Subscription[] _subs = [];
    private int _ringNext, _ringCount;
    private long _seq;
    private long _backlog;
    private int _warnedBacklog;

    public EventBus(ILogger log)
    {
        _log = log;
        _dispatcher = Task.Run(DispatchLoopAsync);
    }

    public int SubscriberCount => Volatile.Read(ref _subs).Length;

    /// <summary>Events published but not yet delivered. Delivery is serial, so this is how far behind the bus is.</summary>
    public int Backlog => (int)Math.Min(int.MaxValue, Interlocked.Read(ref _backlog));

    public void Publish(BusEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        if (string.IsNullOrEmpty(evt.Type)) throw new ArgumentException("Event type is required", nameof(evt));
        var depth = Interlocked.Increment(ref _backlog);
        if (depth >= BacklogWarnAt && depth >= (long)Volatile.Read(ref _warnedBacklog) * 2)
        {
            Volatile.Write(ref _warnedBacklog, (int)Math.Min(int.MaxValue, depth));
            _log.LogWarning("The event bus is {Depth} events behind: a subscriber is slow, or events arrive faster than they are delivered", depth);
        }
        if (!_queue.Writer.TryWrite(evt)) Interlocked.Decrement(ref _backlog);
    }

    public void Publish(string type, object? data = null, string? sessionId = null, bool ui = true) =>
        Publish(new BusEvent { Type = type, Data = data, SessionId = sessionId, Ui = ui });

    public IDisposable Subscribe(string pattern, Action<BusEvent> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return Add(new Subscription(pattern, handler, null));
    }

    public IDisposable SubscribeAsync(string pattern, Func<BusEvent, ValueTask> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return Add(new Subscription(pattern, null, handler));
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

    /// <summary>Completes once every event published before the call has been delivered.</summary>
    public Task FlushAsync()
    {
        var marker = new FlushMarker();
        if (!_queue.Writer.TryWrite(marker)) marker.Done.TrySetResult();
        return marker.Done.Task;
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
        lock (_subsLock) _subs = [.. _subs, sub];
        return new Registration(() =>
        {
            sub.Disposed = true;
            lock (_subsLock) _subs = Array.FindAll(_subs, s => !ReferenceEquals(s, sub));
        });
    }

    private async Task DispatchLoopAsync()
    {
        var reader = _queue.Reader;
        while (await reader.WaitToReadAsync().ConfigureAwait(false))
        {
            while (reader.TryRead(out var item))
            {
                if (item is FlushMarker marker)
                {
                    marker.Done.TrySetResult();
                    continue;
                }
                var pending = DispatchAsync((BusEvent)item);
                // Don't keep the event (possibly a plugin payload) alive in this long-lived state machine.
                item = null!;
                if (!pending.IsCompletedSuccessfully) await pending.ConfigureAwait(false);
                pending = default;
                Interlocked.Decrement(ref _backlog);
            }
        }
    }

    private async ValueTask DispatchAsync(BusEvent evt)
    {
        evt.Seq = ++_seq;
        AddToRing(evt);
        foreach (var s in Volatile.Read(ref _subs))
        {
            if (s.Disposed || !s.Matches(evt.Type)) continue;
            try
            {
                if (s.Sync is not null)
                {
                    s.Sync(evt);
                }
                else
                {
                    var vt = s.Async!(evt);
                    // A sync handler is given no rope (it cannot yield); an async one is let go of after HandlerTimeout,
                    // so one wedged subscriber delays the bus instead of stopping it.
                    if (!vt.IsCompletedSuccessfully)
                    {
                        var task = vt.AsTask();
                        try { await task.WaitAsync(HandlerTimeout).ConfigureAwait(false); }
                        catch (TimeoutException)
                        {
                            _log.LogError("Event handler '{Pattern}' is stuck on '{Type}' (over {Timeout}s); the bus moved on without it",
                                s.Pattern, evt.Type, HandlerTimeout.TotalSeconds);
                            // Nobody awaits it any more, so watch it: a fault after the timeout must not surface as an
                            // unobserved task exception (it is already logged as the handler's own failure).
                            _ = task.ContinueWith(static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Event handler '{Pattern}' failed on '{Type}'", s.Pattern, evt.Type);
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
        catch { /* a handler hung: give up */ }
        lock (_subsLock) _subs = [];
    }

    private sealed class FlushMarker
    {
        public readonly TaskCompletionSource Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class Subscription
    {
        private readonly int _kind; // 0 = all, 1 = exact, 2 = prefix
        private readonly string _match;

        public Subscription(string pattern, Action<BusEvent>? sync, Func<BusEvent, ValueTask>? async)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(pattern);
            Pattern = pattern.Trim();
            Sync = sync;
            Async = async;
            if (Pattern == "*") { _kind = 0; _match = ""; }
            else if (Pattern.EndsWith(".*", StringComparison.Ordinal)) { _kind = 2; _match = Pattern[..^1]; } // keep the dot
            else { _kind = 1; _match = Pattern; }
        }

        public string Pattern { get; }
        public Action<BusEvent>? Sync { get; }
        public Func<BusEvent, ValueTask>? Async { get; }
        public volatile bool Disposed;

        public bool Matches(string type) => _kind switch
        {
            0 => true,
            1 => string.Equals(type, _match, StringComparison.Ordinal),
            _ => type.StartsWith(_match, StringComparison.Ordinal),
        };
    }
}
