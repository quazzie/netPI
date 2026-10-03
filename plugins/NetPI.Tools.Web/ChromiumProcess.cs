using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;

namespace NetPI.Tools.Web;

/// <summary>A browser that is not there or could not be started: what the tools answer with, so the user is told which.</summary>
internal sealed class BrowserUnavailableException(string message) : Exception(message);

/// <summary>
/// An Edge/Chrome process NetPI started, with the DevTools endpoint it opens in a profile of its own. Both browsers
/// behind the tools are this: the browser tool's hidden one (started on first use, closed when it goes idle) and the
/// screenshot tool's (one per call). Only the switches and where the pages come from differ; starting it, finding its
/// port and shutting it down are here once.
/// </summary>
internal sealed class ChromiumProcess : IAsyncDisposable
{
    private static readonly string[] CommonArgs =
        ["--no-first-run", "--no-default-browser-check", "--disable-sync", "--mute-audio", "--remote-debugging-port=0", "about:blank"];

    private readonly Process _process;
    private readonly string _profile;
    private readonly bool _temp;

    private ChromiumProcess(Process process, string profile, bool temp)
    {
        _process = process;
        _profile = profile;
        _temp = temp;
    }

    /// <summary>The browser to launch: the configured one if it is there, else the Edge or Chrome installed on this machine.</summary>
    public static string? Find(string? configured)
    {
        if (!string.IsNullOrEmpty(configured)) return File.Exists(configured) ? configured : null;
        var candidates = new List<string>();
        if (OperatingSystem.IsWindows())
        {
            foreach (var root in new[] { Environment.GetEnvironmentVariable("ProgramFiles(x86)"), Environment.GetEnvironmentVariable("ProgramFiles"), Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) })
            {
                if (string.IsNullOrEmpty(root)) continue;
                candidates.Add(Path.Combine(root, "Microsoft", "Edge", "Application", "msedge.exe"));
                candidates.Add(Path.Combine(root, "Google", "Chrome", "Application", "chrome.exe"));
                candidates.Add(Path.Combine(root, "Chromium", "Application", "chrome.exe"));
            }
        }
        else if (OperatingSystem.IsMacOS())
        {
            candidates.Add("/Applications/Google Chrome.app/Contents/MacOS/Google Chrome");
            candidates.Add("/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge");
            candidates.Add("/Applications/Chromium.app/Contents/MacOS/Chromium");
        }
        else
        {
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                foreach (var name in new[] { "google-chrome", "google-chrome-stable", "chromium", "chromium-browser", "microsoft-edge", "microsoft-edge-stable" })
                    candidates.Add(Path.Combine(dir, name));
        }
        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>
    /// Start the browser on a profile of its own. <paramref name="flags"/> are the caller's own switches (its headless
    /// flag, the features it turns off); the ones every browser here needs are added here. <paramref name="temp"/> marks
    /// a profile NetPI made, which is deleted with the process: one the user named is kept.
    /// </summary>
    public static ChromiumProcess Start(string exe, string profile, int width, int height, IEnumerable<string> flags, bool temp)
    {
        Directory.CreateDirectory(profile);
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        foreach (var a in flags.Concat(CommonArgs).Append($"--user-data-dir={profile}").Append($"--window-size={width},{height}"))
            psi.ArgumentList.Add(a);
        var process = Process.Start(psi) ?? throw new BrowserUnavailableException("The browser did not start.");
        process.ErrorDataReceived += (_, _) => { };
        process.OutputDataReceived += (_, _) => { };
        process.BeginErrorReadLine();
        process.BeginOutputReadLine();
        return new ChromiumProcess(process, profile, temp);
    }

    /// <summary>The port the browser opened for DevTools, once <see cref="WaitForEndpointAsync"/> has read it.</summary>
    public int Port { get; private set; }

    public bool HasExited => _process.HasExited;

    /// <summary>
    /// Wait for the browser to write its port (and the path of its browser endpoint) to <c>DevToolsActivePort</c> in the
    /// profile, and answer that endpoint. Throws when the browser is gone, or when 20 s pass without a port.
    /// </summary>
    public async Task<Uri> WaitForEndpointAsync(CancellationToken ct)
    {
        var portFile = Path.Combine(_profile, "DevToolsActivePort");
        var until = DateTime.UtcNow.AddSeconds(20);
        string path = "";
        for (; DateTime.UtcNow < until;)
        {
            // exit code 0: the launcher handed the browser to another process (Edge can), which still writes the port file
            if (_process.HasExited && _process.ExitCode != 0) throw new BrowserUnavailableException($"The browser exited (code {_process.ExitCode}).");
            try
            {
                if (File.Exists(portFile) && (await File.ReadAllLinesAsync(portFile, ct).ConfigureAwait(false)) is [var port, var ws, ..]
                    && int.TryParse(port, out var p) && p > 0)
                {
                    Port = p;
                    path = ws;
                    break;
                }
            }
            catch (IOException) { }
            await Task.Delay(100, ct).ConfigureAwait(false);
        }
        if (Port <= 0) throw new TimeoutException("the browser did not open its DevTools port");
        return new Uri($"ws://127.0.0.1:{Port}{path}");
    }

    /// <summary>
    /// Close the browser over DevTools (Browser.close on the browser endpoint): the launcher may have handed it to
    /// another process, which killing the launcher would leave running.
    /// </summary>
    private async Task CloseBrowserAsync()
    {
        if (Port <= 0) return;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            var url = JsonNode.Parse(await http.GetStringAsync($"http://127.0.0.1:{Port}/json/version", cts.Token).ConfigureAwait(false))?["webSocketDebuggerUrl"]?.GetValue<string>();
            if (url is null) return;
            using var ws = new ClientWebSocket();
            await ws.ConnectAsync(new Uri(url), cts.Token).ConfigureAwait(false);
            await ws.SendAsync(Encoding.UTF8.GetBytes("{\"id\":1,\"method\":\"Browser.close\"}"), WebSocketMessageType.Text, true, cts.Token).ConfigureAwait(false);
            // the browser drops the connection as it exits
            var buffer = new byte[4096];
            while (ws.State == WebSocketState.Open)
                if ((await ws.ReceiveAsync(buffer, cts.Token).ConfigureAwait(false)).MessageType == WebSocketMessageType.Close) break;
        }
        catch (Exception) { /* gone already, or it didn't answer: the process kill below is the fallback */ }
    }

    private async Task<bool> ExitedWithinAsync(TimeSpan timeout)
    {
        if (_process.HasExited) return true;
        try { await _process.WaitForExitAsync().WaitAsync(timeout).ConfigureAwait(false); return true; }
        catch (TimeoutException) { return false; }
    }

    public async ValueTask DisposeAsync()
    {
        await CloseBrowserAsync().ConfigureAwait(false);
        try
        {
            // Browser.close lets it flush its profile (cookies, storage) and exit on its own: give it a moment before the kill.
            if (!await ExitedWithinAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false))
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
        }
        catch (Exception) { }
        _process.Dispose();
        if (!_temp) return;
        for (var attempt = 0; attempt < 10 && Directory.Exists(_profile); attempt++)
        {
            try { Directory.Delete(_profile, recursive: true); }
            catch (Exception) { await Task.Delay(200).ConfigureAwait(false); }
        }
    }
}
