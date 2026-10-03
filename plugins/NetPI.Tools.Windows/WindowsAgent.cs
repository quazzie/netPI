using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace NetPI.Tools.Windows;

/// <summary>
/// The UI Automation helper (<c>netpi-windows-agent.exe</c>, <c>src/NetPI.WindowsAgent</c>): one process, started on
/// first use, one JSON request per line. It runs from a copy outside the plugin folder, so a rebuild can replace the
/// plugin's files while it runs; a request that gets no answer (an app that stopped responding) kills it, and the next
/// request starts a new one.
/// </summary>
internal sealed class WindowsAgent(IPluginContext ctx) : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _process;
    private long _id;

    /// <summary>The helper as built next to the plugin (agent/), or NETPI_WINDOWS_AGENT (the tests).</summary>
    private string? Source()
    {
        var env = Environment.GetEnvironmentVariable("NETPI_WINDOWS_AGENT");
        if (!string.IsNullOrEmpty(env) && File.Exists(env)) return env;
        foreach (var dir in new[] { Path.Combine(ctx.PluginDirectory, "agent"), Path.Combine(Path.GetDirectoryName(typeof(WindowsAgent).Assembly.Location) ?? "", "agent") })
        {
            var exe = Path.Combine(dir, "netpi-windows-agent.exe");
            if (File.Exists(exe)) return exe;
        }
        return null;
    }

    private Process Start()
    {
        var source = Source() ?? throw new InvalidOperationException("The UI Automation helper (netpi-windows-agent.exe) is missing from the plugin's agent folder: rebuild NetPI.Tools.Windows.");
        var from = Path.GetDirectoryName(source)!;
        // a copy per build of the helper, so the plugin folder stays replaceable while it runs
        var stamp = File.GetLastWriteTimeUtc(source).Ticks.ToString("x", System.Globalization.CultureInfo.InvariantCulture) + "-" + new FileInfo(source).Length.ToString("x", System.Globalization.CultureInfo.InvariantCulture);
        var run = Path.Combine(ctx.Paths.TempDir, "netpi-windows-agent", stamp);
        if (!File.Exists(Path.Combine(run, "netpi-windows-agent.exe")))
        {
            var staging = run + ".tmp-" + Environment.ProcessId;
            Directory.CreateDirectory(staging);
            foreach (var f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(staging, Path.GetFileName(f)), overwrite: true);
            try { Directory.Move(staging, run); }
            catch (IOException) { Directory.Delete(staging, recursive: true); }  // another start made it first
        }
        var psi = new ProcessStartInfo(Path.Combine(run, "netpi-windows-agent.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
        };
        var p = Process.Start(psi) ?? throw new InvalidOperationException("The UI Automation helper did not start.");
        p.ErrorDataReceived += (_, e) => { if (!string.IsNullOrEmpty(e.Data)) ctx.Logger.LogDebug("windows agent: {Line}", e.Data); };
        p.BeginErrorReadLine();
        ctx.Logger.LogInformation("windows: UI Automation helper started (pid {Pid})", p.Id);
        return p;
    }

    /// <summary>One request; the helper's answer, or an exception with its error.</summary>
    public async Task<JsonObject> CallAsync(JsonObject request, CancellationToken ct, int timeoutSeconds = 30)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_process is null || _process.HasExited)
            {
                _process?.Dispose();
                _process = Start();
            }
            var id = ++_id;
            request["id"] = id;
            await _process.StandardInput.WriteLineAsync(request.ToJsonString().AsMemory(), ct).ConfigureAwait(false);
            await _process.StandardInput.FlushAsync(ct).ConfigureAwait(false);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
            while (true)
            {
                string? line;
                try { line = await _process.StandardOutput.ReadLineAsync(timeout.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    Kill();
                    throw new TimeoutException($"the window did not answer within {timeoutSeconds} s (the app may have stopped responding); the helper was restarted");
                }
                catch (OperationCanceledException)
                {
                    Kill();  // the answer would come to the next request
                    throw;
                }
                if (line is null)
                {
                    Kill();
                    throw new InvalidOperationException("the UI Automation helper exited");
                }
                if (JsonNode.Parse(line) is not JsonObject answer || answer["id"]?.GetValue<long>() != id) continue;
                if (answer["error"]?.GetValue<string>() is { } error) throw new InvalidOperationException(error);
                return answer;
            }
        }
        finally { _gate.Release(); }
    }

    private void Kill()
    {
        try { _process?.Kill(entireProcessTree: true); } catch (Exception) { }
        _process?.Dispose();
        _process = null;
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_process is { HasExited: false } p)
            {
                try { p.StandardInput.Close(); } catch (Exception) { }
                if (!p.WaitForExit(2000)) Kill();
            }
            _process?.Dispose();
            _process = null;
        }
        finally { _gate.Release(); }
    }
}
