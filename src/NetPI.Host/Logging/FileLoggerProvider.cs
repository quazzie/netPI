using System.Text;
using Microsoft.Extensions.Logging;

namespace NetPI.Host.Logging;

/// <summary>
/// Minimal append-only file logger for ~/.netpi/logs/netpi.log
/// (PLAN §47: simple structured logs, no serilog). One file, one line per
/// entry: <c>ISO-timestamp | level | plugin | event | message</c>.
/// </summary>
public sealed class FileLoggerProvider(string logDirectory) : ILoggerProvider
{
    private readonly object _gate = new();
    private readonly List<LoggerEntry> _entries = [];
    private string _currentFile = Path.Combine(logDirectory, $"netpi-{DateTime.Now:yyyy-MM-dd}.log");
    private StreamWriter? _writer;

    public ILogger CreateLogger(string categoryName) => new FileLogger(categoryName, this);

    public void Dispose()
    {
        lock (_gate)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }


    public void Write(LoggerEntry entry)
    {
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(logDirectory);
                if (_writer is null)
                {
                    _writer = new StreamWriter(_currentFile, append: true, new UTF8Encoding(false))
                    {
                        AutoFlush = true
                    };
                }
                _entries.Add(entry);
                _writer.WriteLine(string.Join(" | ", entry.Timestamp, entry.Level, entry.Plugin, entry.Event ?? "-", entry.Message, entry.Exception ?? ""));
            }
            catch
            {
                // Logging must never crash the host.
            }
        }
    }

    public IReadOnlyList<LoggerEntry> Snapshot()
    {
        lock (_gate) return _entries.ToList();
    }

    public record LoggerEntry(DateTimeOffset Timestamp, LogLevel Level, string Plugin, string? Event, string Message, string? Exception);

    private sealed class FileLogger(string category, FileLoggerProvider provider) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            provider.Write(new LoggerEntry(
                DateTimeOffset.Now,
                logLevel,
                category,
                eventId.Id == 0 ? null : eventId.Id.ToString(),
                formatter(state, exception),
                exception?.ToString()));
        }
    }
}
