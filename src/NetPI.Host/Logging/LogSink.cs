using System.Globalization;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace NetPI.Host.Logging;

/// <summary>One log line (also returned by the <c>logs.recent</c> RPC).</summary>
public sealed record LogEntry(DateTimeOffset Time, LogLevel Level, string Category, string Message, string? Exception);

/// <summary>
/// Log destination shared by every logger of a host instance: a daily rolling file
/// (<c>Home/logs/netpi-YYYYMMDD.log</c>), an in-memory ring buffer and optionally the console.
/// Writes are queued and flushed by a single background task so logging never blocks callers on IO. That queue is
/// bounded: the file is written synchronously by that one task, so a log folder on a share that stalls must not let it
/// grow without limit — past <see cref="QueueCapacity"/> the oldest waiting line is dropped, counted, and said in
/// <see cref="Recent"/> (the ring buffer keeps every line, so that is where the gap is visible).
/// </summary>
internal sealed class LogSink : IDisposable
{
    private const int RingSize = 2000;
    private const int RetentionDays = 14;
    /// <summary>How many lines may wait for the log file. Last-resort ceiling, like the bus's: a stalled share must not grow the queue forever.</summary>
    public const int QueueCapacity = 10_000;

    private readonly string? _dir;
    private readonly bool _console;
    private readonly int _capacity;
    private readonly Channel<LogEntry> _queue;
    private readonly LogEntry?[] _ring = new LogEntry?[RingSize];
    private readonly Lock _ringLock = new();
    private readonly Task _writer;
    private int _ringNext, _ringCount;
    private int _queued;      // lines in the queue, as far as this side can tell (capped at QueueCapacity: see Write)
    private long _dropped;
    private StreamWriter? _file;
    private DateOnly _fileDate;
    private volatile bool _disposed;

    public LogSink(string? logsDir, bool console, int queueCapacity = QueueCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(queueCapacity, 1);
        _dir = logsDir;
        _console = console;
        _capacity = queueCapacity;
        // DropOldest, never Wait: a logger is called on the thread that logs (a plugin's, a request's), and it must not
        // wait for a file on a share that is not answering.
        _queue = Channel.CreateBounded<LogEntry>(new BoundedChannelOptions(queueCapacity)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.DropOldest,
        });
        if (_dir is not null)
        {
            Directory.CreateDirectory(_dir);
            _ = Task.Run(DeleteOldFiles);
        }
        _writer = Task.Run(WriteLoopAsync);
    }

    /// <summary>Minimum level for non-framework categories (framework categories are filtered to Warning by the factory).</summary>
    public LogLevel MinLevel { get; set; } = LogLevel.Information;

    /// <summary>Tests only: awaited by the writer before it takes a line off the queue, so a full queue (and a drop) is deterministic.</summary>
    internal Func<Task>? BeforeWriteAsync { get; set; }

    /// <summary>Log lines dropped before they reached the file because the queue was full (a log folder that stopped answering).</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    public void Write(LogEntry entry)
    {
        lock (_ringLock)
        {
            _ring[_ringNext] = entry;
            _ringNext = (_ringNext + 1) % RingSize;
            if (_ringCount < RingSize) _ringCount++;
        }
        if (_disposed || !_queue.Writer.TryWrite(entry)) return;
        // DropOldest accepts the line and throws the oldest one away, silently — so this side keeps its own count of what
        // is waiting: over the ceiling means this write cost us a line, and the line it cost is not waiting any more.
        if (Interlocked.Increment(ref _queued) > _capacity)
        {
            Interlocked.Decrement(ref _queued);
            Interlocked.Increment(ref _dropped);
        }
    }

    public IReadOnlyList<LogEntry> Recent(int max = 200)
    {
        lock (_ringLock)
        {
            var n = Math.Clamp(max, 0, _ringCount);
            var result = new List<LogEntry>(n + 1);
            var dropped = Interlocked.Read(ref _dropped);
            // The ring keeps every line, including the ones the file queue had to drop: say so here, or a reader of the
            // log (the panel, diag) sees lines that are in no file and never learns why.
            if (dropped > 0)
                result.Add(new LogEntry(DateTimeOffset.Now, LogLevel.Warning, "NetPI.Host.Logging",
                    $"{dropped} log line(s) were dropped before they reached the log file: the log folder is not keeping up", null));
            var start = (_ringNext - n + RingSize) % RingSize;
            for (var i = 0; i < n; i++) result.Add(_ring[(start + i) % RingSize]!);
            return result;
        }
    }

    private async Task WriteLoopAsync()
    {
        var reader = _queue.Reader;
        var sb = new StringBuilder(256);
        while (await reader.WaitToReadAsync().ConfigureAwait(false))
        {
            while (reader.TryRead(out var e))
            {
                Interlocked.Decrement(ref _queued);
                if (BeforeWriteAsync is { } gate) await gate().ConfigureAwait(false);
                sb.Clear();
                Format(sb, e);
                var line = sb.ToString();
                if (_console) WriteConsole(e, line);
                if (_dir is not null) WriteFile(e, line);
            }
            try { _file?.Flush(); } catch { /* disk full, file removed... keep going */ }
        }
        try { _file?.Dispose(); } catch { }
        _file = null;
    }

    private static void Format(StringBuilder sb, LogEntry e)
    {
        sb.Append(e.Time.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)).Append(' ')
          .Append(LevelTag(e.Level)).Append(" [").Append(e.Category).Append("] ").Append(e.Message);
        if (e.Exception is not null) sb.AppendLine().Append(e.Exception);
    }

    public static string LevelTag(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRC",
        LogLevel.Debug => "DBG",
        LogLevel.Information => "INF",
        LogLevel.Warning => "WRN",
        LogLevel.Error => "ERR",
        LogLevel.Critical => "CRT",
        _ => "???",
    };

    private void WriteFile(LogEntry e, string line)
    {
        try
        {
            var date = DateOnly.FromDateTime(e.Time.LocalDateTime);
            if (_file is null || date != _fileDate)
            {
                _file?.Dispose();
                var path = Path.Combine(_dir!, $"netpi-{date:yyyyMMdd}.log");
                var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                _file = new StreamWriter(fs, new UTF8Encoding(false)) { AutoFlush = false };
                _fileDate = date;
            }
            _file.WriteLine(line);
        }
        catch
        {
            // Logging must never take the host down.
            try { _file?.Dispose(); } catch { }
            _file = null;
        }
    }

    private static void WriteConsole(LogEntry e, string line)
    {
        try
        {
            if (e.Level >= LogLevel.Warning) Console.Error.WriteLine(line);
            else Console.Out.WriteLine(line);
        }
        catch { /* no console */ }
    }

    private void DeleteOldFiles()
    {
        try
        {
            var cutoff = DateTime.Now.AddDays(-RetentionDays);
            foreach (var f in Directory.EnumerateFiles(_dir!, "netpi-*.log"))
                if (File.GetLastWriteTime(f) < cutoff) File.Delete(f);
        }
        catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _queue.Writer.TryComplete();
        try { _writer.Wait(TimeSpan.FromSeconds(3)); } catch { }
    }
}

/// <summary>Logger provider over a <see cref="LogSink"/>. Does not own the sink (it can be added to several factories).</summary>
internal sealed class NetPiLoggerProvider(LogSink sink) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new NetPiLogger(categoryName, sink);

    public void Dispose() { }

    private sealed class NetPiLogger(string category, LogSink sink) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None && logLevel >= sink.MinLevel;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            string message;
            try { message = formatter(state, exception); }
            catch (Exception ex) { message = $"<log formatting failed: {ex.Message}>"; }
            if (string.IsNullOrEmpty(message) && exception is null) return;
            // Only strings are kept: holding Exception objects could keep an unloaded plugin's types alive.
            sink.Write(new LogEntry(DateTimeOffset.Now, logLevel, category, message, exception?.ToString()));
        }
    }
}

internal static class LoggingSetup
{
    /// <summary>Framework categories are noisy: keep them at Warning.</summary>
    public static void Configure(ILoggingBuilder builder, LogSink sink)
    {
        builder.ClearProviders();
        builder.SetMinimumLevel(LogLevel.Trace);
        builder.AddFilter("Microsoft", LogLevel.Warning);
        builder.AddFilter("System", LogLevel.Warning);
        builder.AddProvider(new NetPiLoggerProvider(sink));
    }

    public static LogLevel ParseLevel(string? value, LogLevel fallback) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "trace" => LogLevel.Trace,
            "debug" => LogLevel.Debug,
            "info" or "information" => LogLevel.Information,
            "warn" or "warning" => LogLevel.Warning,
            "error" => LogLevel.Error,
            "critical" => LogLevel.Critical,
            _ => fallback,
        };
}
