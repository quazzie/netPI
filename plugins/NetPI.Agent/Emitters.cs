using System.Text;
using System.Text.Json.Nodes;

namespace NetPI.Agent;

/// <summary>
/// Coalesces <c>stream.delta</c> events: text of the same kind is buffered and flushed every ~33ms, on a kind change,
/// on tool start and at the end of the stream. A timer flushes text that would otherwise linger during a pause.
/// </summary>
internal sealed class StreamEmitter : IDisposable
{
    public const int FlushMs = 33;

    private readonly IEventBus _bus;
    private readonly string _sessionId;
    private readonly Lock _gate = new();
    private readonly StringBuilder _buffer = new();
    private string? _kind;
    private long _lastFlush;
    private Timer? _timer;
    private bool _armed;
    private bool _disposed;

    public StreamEmitter(IEventBus bus, string sessionId)
    {
        _bus = bus;
        _sessionId = sessionId;
        _lastFlush = Environment.TickCount64;
    }

    public void Delta(string kind, string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        lock (_gate)
        {
            if (_disposed) return;
            if (_kind is not null && _kind != kind) FlushLocked();
            _kind = kind;
            _buffer.Append(text);
            if (Environment.TickCount64 - _lastFlush >= FlushMs) FlushLocked();
            else Arm();
        }
    }

    public void Flush()
    {
        lock (_gate) FlushLocked();
    }

    /// <summary>Drop buffered text (stream reset).</summary>
    public void Discard()
    {
        lock (_gate)
        {
            _buffer.Clear();
            _kind = null;
        }
    }

    private void Arm()
    {
        if (_armed) return;
        _armed = true;
        _timer ??= new Timer(_ =>
        {
            lock (_gate)
            {
                _armed = false;
                if (!_disposed) FlushLocked();
            }
        });
        _timer.Change(FlushMs, Timeout.Infinite);
    }

    private void FlushLocked()
    {
        _lastFlush = Environment.TickCount64;
        if (_buffer.Length == 0 || _kind is null) return;
        var text = _buffer.ToString();
        _buffer.Clear();
        _bus.Publish(new BusEvent
        {
            Type = EventTypes.StreamDelta,
            SessionId = _sessionId,
            Data = new JsonObject { ["sessionId"] = _sessionId, ["kind"] = _kind, ["text"] = text },
        });
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            FlushLocked();
            _disposed = true;
            _timer?.Dispose();
        }
    }
}

/// <summary>Throttled <c>tool.output</c> events for one tool call (~10 per second).</summary>
internal sealed class ToolOutputEmitter : IDisposable
{
    public const int FlushMs = 100;

    private readonly IEventBus _bus;
    private readonly string _sessionId;
    private readonly string _callId;
    private readonly Lock _gate = new();
    private readonly StringBuilder _buffer = new();
    private long _lastFlush;
    private Timer? _timer;
    private bool _armed;
    private bool _disposed;

    public ToolOutputEmitter(IEventBus bus, string sessionId, string callId)
    {
        _bus = bus;
        _sessionId = sessionId;
        _callId = callId;
        _lastFlush = Environment.TickCount64 - FlushMs;
    }

    public void Write(string chunk)
    {
        if (string.IsNullOrEmpty(chunk)) return;
        lock (_gate)
        {
            if (_disposed) return;
            _buffer.Append(chunk);
            // keep memory bounded if the UI can't keep up with a chatty tool
            if (_buffer.Length > 256 * 1024) _buffer.Remove(0, _buffer.Length - 128 * 1024);
            if (Environment.TickCount64 - _lastFlush >= FlushMs) FlushLocked();
            else if (!_armed)
            {
                _armed = true;
                _timer ??= new Timer(_ =>
                {
                    lock (_gate)
                    {
                        _armed = false;
                        if (!_disposed) FlushLocked();
                    }
                });
                _timer.Change(FlushMs, Timeout.Infinite);
            }
        }
    }

    private void FlushLocked()
    {
        _lastFlush = Environment.TickCount64;
        if (_buffer.Length == 0) return;
        var chunk = _buffer.ToString();
        _buffer.Clear();
        _bus.Publish(new BusEvent
        {
            Type = EventTypes.ToolOutput,
            SessionId = _sessionId,
            Data = new JsonObject { ["sessionId"] = _sessionId, ["callId"] = _callId, ["chunk"] = chunk },
        });
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            FlushLocked();
            _disposed = true;
            _timer?.Dispose();
        }
    }
}
