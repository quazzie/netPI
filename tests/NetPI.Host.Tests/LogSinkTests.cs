using Microsoft.Extensions.Logging;
using NetPI.Host.Logging;

namespace NetPI.Host.Tests;

public static class LogSinkTests
{
    private static LogEntry Line(int n) => new(DateTimeOffset.Now, LogLevel.Information, "test", $"line {n}", null);

    /// <summary>Today log file, read the way the sink holds it: it shares for write, so a reader must offer that too.</summary>
    private static string TodayPath(string dir) => Path.Combine(dir, $"netpi-{DateTime.Now:yyyyMMdd}.log");

    private static string ReadFile(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(fs);
        return reader.ReadToEnd();
    }

    public static void Register(TestRunner r)
    {
        r.Add("log: writes reach today's file, and the ring buffer keeps them newest last", async () =>
        {
            var dir = T.TempDir("logs");
            using var sink = new LogSink(dir, console: false);
            sink.Write(Line(1));
            sink.Write(Line(2));
            await Wait.UntilAsync(() => File.Exists(TodayPath(dir)) && ReadFile(TodayPath(dir)).Contains("line 2"),
                "both lines in today's log file");
            var recent = sink.Recent(10);
            Check.Equal("line 1,line 2", string.Join(",", recent.Select(e => e.Message)), "the ring buffer has them, oldest first");
        });

        // The file is written synchronously by the one writer task, so a log folder on a share that stopped answering used
        // to let the queue grow without limit, with nothing anywhere saying so. It is bounded now: past the bound the
        // oldest waiting line goes, it is counted, and the ring buffer (which keeps every line) says how many never
        // reached the file.
        r.Add("log: a log folder that stops answering drops the oldest waiting lines, counts them and says so", async () =>
        {
            var dir = T.TempDir("logs");
            const int bound = 8;
            using var sink = new LogSink(dir, console: false, queueCapacity: bound);
            // Hold the writer inside its first line: a share that stopped answering looks exactly like this. Waiting for it
            // to get there first is what makes the count exact - the queue is empty and holds nothing when the burst starts.
            var held = new TaskCompletionSource();
            var holding = new TaskCompletionSource();
            sink.BeforeWriteAsync = () => { holding.TrySetResult(); return held.Task; };

            sink.Write(Line(1));
            await holding.Task.WaitAsync(TimeSpan.FromSeconds(10));
            for (var i = 2; i <= 100; i++) sink.Write(Line(i));   // far past the bound: writing still neither blocks nor throws

            Check.Equal(99 - bound, sink.Dropped, "every write past the bound cost the oldest waiting line");
            var said = sink.Recent(200).First();
            Check.Contains(said.Message, $"{99 - bound} log line(s) were dropped", "the ring buffer says how many never reached the file: " + said.Message);
            Check.Equal(LogLevel.Warning, said.Level, "as a warning, so it is not lost among the info lines");
            Check.Equal(101, sink.Recent(200).Count, "every line is still in the ring: the 100 written plus the line that says so");

            held.TrySetResult();
            await Wait.UntilAsync(() => File.Exists(TodayPath(dir)) && ReadFile(TodayPath(dir)).Contains("line 100"),
                "the writer catches up once the file answers");
            var written = ReadFile(TodayPath(dir)).Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToList();
            Check.True(written.Any(l => l.EndsWith("[test] line 100")), "the newest lines are the ones that survive");
            Check.True(written.Any(l => l.EndsWith("[test] line 1")), "the one the writer had already taken is written too");
            Check.False(written.Any(l => l.EndsWith("[test] line 2")), "the oldest waiting ones are the ones that went");
        });

        r.Add("log: nothing is said about drops while the queue keeps up", () =>
        {
            using var sink = new LogSink(T.TempDir("logs"), console: false);
            for (var i = 1; i <= 50; i++) sink.Write(Line(i));
            Check.Equal(0L, sink.Dropped, "no line was dropped");
            Check.Equal(50, sink.Recent(200).Count, "and the ring buffer holds exactly what was written");
        });
    }
}