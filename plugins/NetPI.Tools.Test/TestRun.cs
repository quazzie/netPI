using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace NetPI.Tools.Test;

/// <summary>One run of a test command: its exit code, the tail of its output, whether it timed out.</summary>
internal sealed record CommandOutcome(int Exit, string Tail, bool TimedOut, bool Cancelled, string? StartError, TimeSpan Elapsed);

/// <summary>What a results file says.</summary>
internal sealed record TestResults(int Passed, int Failed, int Skipped, IReadOnlyList<TestFailure> Failures, string? Log);

internal sealed record TestFailure(string Name, string Message, string? Evidence);

internal static class TestRun
{
    private const int TailChars = 8000;

    /// <summary>Run a command line through the platform's shell in <paramref name="cwd"/>, the whole process tree ended at the deadline or on cancellation.</summary>
    public static async Task<CommandOutcome> RunAsync(string command, string cwd, IReadOnlyDictionary<string, string> env, TimeSpan timeout,
        Action<string>? output, CancellationToken ct)
    {
        var psi = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe") { Arguments = "/d /s /c \"" + command + "\"" }
            : new ProcessStartInfo("/bin/sh") { ArgumentList = { "-c", command } };
        psi.WorkingDirectory = cwd;
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.RedirectStandardInput = true;
        psi.StandardOutputEncoding = psi.StandardErrorEncoding = Encoding.UTF8;
        foreach (var (k, v) in env) psi.Environment[k] = v;
        var sw = Stopwatch.StartNew();
        Process process;
        try { process = Process.Start(psi) ?? throw new Win32Exception("the command did not start"); }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException)
        {
            return new CommandOutcome(-1, "", false, false, ex.Message, sw.Elapsed);
        }
        using (process)
        {
            try { ChildJob.Assign(process); } catch { /* a safety net only */ }
            try { process.StandardInput.Close(); } catch { }
            var tail = new StringBuilder();
            void Add(string? line)
            {
                if (line is null) return;
                lock (tail)
                {
                    tail.AppendLine(line);
                    if (tail.Length > TailChars * 2) tail.Remove(0, tail.Length - TailChars);
                }
                output?.Invoke(line + "\n");
            }
            var pumps = Task.WhenAll(Pump(process.StandardOutput, Add), Pump(process.StandardError, Add));
            using var deadline = new CancellationTokenSource(timeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
            try
            {
                await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
                await pumps.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                return new CommandOutcome(process.ExitCode, Tail(tail), false, false, null, sw.Elapsed);
            }
            catch (TimeoutException) { return new CommandOutcome(process.ExitCode, Tail(tail), false, false, null, sw.Elapsed); }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* gone */ }
                try { await pumps.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); } catch { }
                return new CommandOutcome(-1, Tail(tail), deadline.IsCancellationRequested, ct.IsCancellationRequested, null, sw.Elapsed);
            }
        }
    }

    private static string Tail(StringBuilder tail)
    {
        lock (tail)
        {
            var s = tail.ToString();
            return s.Length > TailChars ? "…" + s[^TailChars..] : s;
        }
    }

    private static async Task Pump(StreamReader reader, Action<string?> add)
    {
        try { while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line) add(line); }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
    }

    /// <summary>The results file, or why it cannot be read.</summary>
    public static (TestResults? Results, string? Error) ReadResults(string path)
    {
        if (!File.Exists(path)) return (null, $"the results file {path} was not written");
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var o = doc.RootElement;
            if (o.ValueKind != JsonValueKind.Object) return (null, $"the results file {path} is not a JSON object");
            int Count(string name) => o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : 0;
            string? Str(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            var failures = new List<TestFailure>();
            if (o.TryGetProperty("failures", out var list) && list.ValueKind == JsonValueKind.Array)
                foreach (var f in list.EnumerateArray())
                    failures.Add(new TestFailure(Str(f, "name") ?? "(unnamed)", Str(f, "message") ?? "", Str(f, "evidence")));
            var failed = Math.Max(Count("failed"), failures.Count);
            return (new TestResults(Count("passed"), failed, Count("skipped"), failures, Str(o, "log")), null);
        }
        catch (JsonException ex) { return (null, $"the results file {path} is not valid JSON: {ex.Message}"); }
        catch (IOException ex) { return (null, $"the results file {path} could not be read: {ex.Message}"); }
    }
}
