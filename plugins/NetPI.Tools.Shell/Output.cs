using System.Text;

namespace NetPI.Tools.Shell;

/// <summary>
/// Streaming ANSI/VT escape-sequence remover. Keeps state across chunks (a sequence may be split between reads).
/// Handles CSI (ESC [ … final), OSC (ESC ] … BEL/ST), DCS/SOS/PM/APC strings, two/three-byte escapes and 8-bit C1 forms;
/// drops other C0 controls except \t, \n, \r.
/// </summary>
public sealed class AnsiStripper
{
    private enum State { Normal, Esc, EscIntermediate, Csi, Str, StrEsc }

    private State _state;

    public string Process(ReadOnlySpan<char> input)
    {
        StringBuilder? sb = null;
        // Fast path: nothing to strip.
        if (_state == State.Normal)
        {
            var clean = true;
            foreach (var c in input)
                if (c < 0x20 && c != '\n' && c != '\r' && c != '\t' || c is >= '\u0080' and <= '\u009F') { clean = false; break; }
            if (clean) return input.ToString();
        }
        sb = new StringBuilder(input.Length);
        foreach (var c in input)
        {
            switch (_state)
            {
                case State.Normal:
                    if (c == '\x1b') _state = State.Esc;
                    else if (c == '\u009B') _state = State.Csi;
                    else if (c is '\u009D' or '\u0090' or '\u0098' or '\u009E' or '\u009F') _state = State.Str;
                    else if (c is '\n' or '\r' or '\t') sb.Append(c);
                    else if (c < 0x20 || c == 0x7F || c is >= '\u0080' and <= '\u009F') { /* drop control */ }
                    else sb.Append(c);
                    break;
                case State.Esc:
                    if (c == '[') _state = State.Csi;
                    else if (c is ']' or 'P' or 'X' or '^' or '_') _state = State.Str;
                    else if (c is >= ' ' and <= '/') _state = State.EscIntermediate;
                    else if (c == '\x1b') _state = State.Esc;
                    else _state = State.Normal; // single-character escape (ESC 7, ESC =, ESC c …)
                    break;
                case State.EscIntermediate:
                    if (c is >= '0' and <= '~') _state = State.Normal;
                    else if (c is not (>= ' ' and <= '/')) _state = State.Normal;
                    break;
                case State.Csi:
                    if (c is >= '@' and <= '~') _state = State.Normal;
                    else if (c == '\x1b') _state = State.Esc;
                    else if (c is '\n' or '\r') { sb.Append(c); _state = State.Normal; } // malformed: do not swallow lines
                    break;
                case State.Str:
                    if (c == '\a' || c == '\u009C') _state = State.Normal;
                    else if (c == '\x1b') _state = State.StrEsc;
                    break;
                case State.StrEsc:
                    _state = c == '\\' ? State.Normal : c == '\x1b' ? State.StrEsc : State.Str;
                    break;
            }
        }
        return sb.ToString();
    }

    public static string Strip(string s) => new AnsiStripper().Process(s);
}

/// <summary>
/// Captured process output: a tail buffer (~1MB, for process output and the model tail) plus an optional spill file that
/// receives the complete output once it outgrows the in-memory buffer. Thread-safe.
/// </summary>
public sealed class OutputCapture : IDisposable
{
    private readonly object _lock = new();
    private readonly StringBuilder _buf = new();
    private readonly int _capacity;
    private readonly string? _spillPath;
    private readonly long _spillThreshold;
    private StreamWriter? _spill;
    private long _dropped;

    public long TotalChars { get; private set; }
    public long TotalBytes { get; private set; }
    /// <summary>When the last output arrived (the capture's creation before any): how long a process has been silent.</summary>
    public DateTimeOffset LastOutputAt { get; private set; } = DateTimeOffset.UtcNow;
    /// <summary>Path of the spill file once created.</summary>
    public string? SpillFile { get; private set; }

    /// <param name="capacityChars">Chars kept in memory (the most recent ones).</param>
    /// <param name="spillPath">Where to store the full output once it exceeds <paramref name="spillThreshold"/> (null: never spill).</param>
    public OutputCapture(int capacityChars = 1024 * 1024, string? spillPath = null, long spillThreshold = 1024 * 1024)
    {
        _capacity = capacityChars;
        _spillPath = spillPath;
        _spillThreshold = Math.Min(spillThreshold, capacityChars);
    }

    public void Append(string chunk)
    {
        if (chunk.Length == 0) return;
        lock (_lock)
        {
            LastOutputAt = DateTimeOffset.UtcNow;
            TotalChars += chunk.Length;
            TotalBytes += Encoding.UTF8.GetByteCount(chunk);
            _buf.Append(chunk);
            if (_spill is not null)
            {
                try { _spill.Write(chunk); } catch { /* disk full etc.: keep the tail */ }
            }
            else if (_spillPath is not null && TotalChars > _spillThreshold && _dropped == 0)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_spillPath)!);
                    _spill = new StreamWriter(_spillPath, append: false, new UTF8Encoding(false), 1 << 16);
                    _spill.Write(_buf.ToString()); // complete so far: nothing dropped yet
                    SpillFile = _spillPath;
                }
                catch
                {
                    _spill = null;
                }
            }
            if (_buf.Length > _capacity * 2)
            {
                var remove = _buf.Length - _capacity;
                // Do not split a surrogate pair.
                if (remove < _buf.Length && char.IsLowSurrogate(_buf[remove])) remove++;
                _buf.Remove(0, remove);
                _dropped += remove;
            }
        }
    }

    /// <summary>The whole output is still in memory.</summary>
    public bool Complete { get { lock (_lock) return _dropped == 0 && _buf.Length <= _capacity * 2; } }

    /// <summary>The in-memory tail (the whole output when <see cref="Complete"/>).</summary>
    public string Snapshot()
    {
        lock (_lock)
        {
            return _buf.Length > _capacity && _dropped > 0 ? _buf.ToString(_buf.Length - _capacity, _capacity) : _buf.ToString();
        }
    }

    /// <summary>Last <paramref name="lines"/> lines of the in-memory tail.</summary>
    public string Tail(int lines, int maxChars = 64 * 1024)
    {
        var s = Snapshot();
        return ToolOutput.TailLines(s, lines, maxChars).Text;
    }

    /// <summary>Make sure the complete output exists on disk and return its path (spill file, or the in-memory text saved to <paramref name="path"/>).</summary>
    public string? SaveFull(string path)
    {
        lock (_lock)
        {
            if (_spill is not null)
            {
                try { _spill.Flush(); } catch { }
                return SpillFile;
            }
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, _buf.ToString(), new UTF8Encoding(false));
                return path;
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>Shrink the in-memory buffer of a finished process.</summary>
    public void TrimTo(int chars)
    {
        lock (_lock)
        {
            if (_buf.Length <= chars) return;
            var remove = _buf.Length - chars;
            _buf.Remove(0, remove);
            _dropped += remove;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            try { _spill?.Dispose(); } catch { }
            _spill = null;
        }
    }
}

/// <summary>Batches output chunks and delivers them at most every <c>interval</c> (in order).</summary>
public sealed class OutputThrottle : IDisposable
{
    private readonly Action<string> _sink;
    private readonly TimeSpan _interval;
    private readonly object _lock = new();
    private readonly object _sendLock = new();
    private readonly StringBuilder _pending = new();
    private readonly Timer _timer;
    private bool _scheduled;
    private bool _disposed;

    public OutputThrottle(Action<string> sink, TimeSpan interval)
    {
        _sink = sink;
        _interval = interval;
        _timer = new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Add(string chunk)
    {
        if (chunk.Length == 0) return;
        lock (_lock)
        {
            if (_disposed) return;
            _pending.Append(chunk);
            if (!_scheduled)
            {
                _scheduled = true;
                _timer.Change(_interval, Timeout.InfiniteTimeSpan);
            }
        }
    }

    public void Flush()
    {
        lock (_sendLock)
        {
            string text;
            lock (_lock)
            {
                text = _pending.ToString();
                _pending.Clear();
                _scheduled = false;
            }
            if (text.Length == 0) return;
            try { _sink(text); } catch { /* UI sink failures must not break the command */ }
        }
    }

    public void Dispose()
    {
        lock (_lock) { if (_disposed) return; _disposed = true; }
        _timer.Dispose();
        Flush();
    }
}


