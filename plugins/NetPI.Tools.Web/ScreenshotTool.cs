using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NetPI.Tools.Web;

/// <summary>
/// <c>screenshot</c>: a URL rendered by a headless Edge/Chrome (DevTools protocol over a WebSocket, a fresh profile per
/// call), or with no URL the NetPI window as the user sees it (the desktop shell's <c>desktop.capture</c> RPC).
/// </summary>
internal sealed class ScreenshotTool(IPluginContext ctx) : IAgentTool
{
    public ToolDefinition Definition { get; } = new()
    {
        Name = "screenshot",
        Label = "Screenshot",
        Category = "web",
        ReadOnly = true,
        SummaryArg = "url",
        Description =
            "Take a screenshot to see a web page or UI: pass a url (e.g. a local dev server) to render it in a headless " +
            "browser, or no url to capture the NetPI window as the user sees it. Returns the image plus the page title and " +
            "any console errors. wait_for waits for a CSS selector; full_page captures the whole scroll height.",
        Parameters = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["url"] = new JsonObject { ["type"] = "string", ["description"] = "Page to render; omit for the NetPI window" },
                ["width"] = new JsonObject { ["type"] = "integer", ["description"] = "Viewport width (default 1280)" },
                ["height"] = new JsonObject { ["type"] = "integer", ["description"] = "Viewport height (default 800)" },
                ["full_page"] = new JsonObject { ["type"] = "boolean" },
                ["wait_for"] = new JsonObject { ["type"] = "string", ["description"] = "CSS selector to wait for (up to 10 s)" },
                ["delay_ms"] = new JsonObject { ["type"] = "integer", ["description"] = "Extra wait after loading (default 500)" },
            },
        },
        PromptGuidelines = ["Use screenshot to check UI work visually (your own dev server, or the NetPI window with no url)."],
    };

    public async Task<ToolResult> ExecuteAsync(ToolContext context, JsonElement args, CancellationToken ct)
    {
        if (context.Model?.InputModalities is { Count: > 0 } mods && !mods.Contains("image"))
            return ToolResult.Error($"The current model ({context.Model.Id}) can't see images, so a screenshot would not help it. Use a vision model for visual checks.");
        args = Args.Unwrap(args);
        var url = Args.Str(args, "url", "href", "page")?.Trim();
        return string.IsNullOrEmpty(url) ? await AppWindowAsync(ct).ConfigureAwait(false) : await PageAsync(url, args, ct).ConfigureAwait(false);
    }

    private async Task<ToolResult> AppWindowAsync(CancellationToken ct)
    {
        if (!ctx.Rpc.Exists("desktop.capture"))
            return ToolResult.Error("There is no NetPI window to capture here (that needs the desktop app, NetPI.exe). Pass a url to screenshot a page.");
        JsonElement e;
        try { e = NetPiJson.ToElement(await ctx.Rpc.InvokeAsync("desktop.capture", new { maxWidth = 1600 }, ct).ConfigureAwait(false)); }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return ToolResult.Error("Capturing the NetPI window failed: " + ex.Message);
        }
        var data = e.TryGetProperty("data", out var d) ? d.GetString() : null;
        if (string.IsNullOrEmpty(data)) return ToolResult.Error("Capturing the NetPI window returned no image.");
        var w = e.TryGetProperty("width", out var wv) ? wv.GetInt32() : 0;
        var h = e.TryGetProperty("height", out var hv) ? hv.GetInt32() : 0;
        return new ToolResult
        {
            Content = $"Screenshot of the NetPI window ({w}×{h}).",
            Images = [new ImagePart { MediaType = e.TryGetProperty("mediaType", out var mt) ? mt.GetString() ?? "image/png" : "image/png", Data = data }],
            Details = new { source = "window", width = w, height = h },
        };
    }

    private async Task<ToolResult> PageAsync(string url, JsonElement args, CancellationToken ct)
    {
        if (!url.Contains("://", StringComparison.Ordinal)) url = (url.StartsWith("localhost", StringComparison.OrdinalIgnoreCase) || url.StartsWith("127.", StringComparison.Ordinal) ? "http://" : "https://") + url;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https" or "file"))
            return ToolResult.Error($"Not a URL the screenshot tool can open: {url}");
        var o = WebOptions.Read(ctx.Settings);
        var exe = HeadlessBrowser.Find(o.BrowserPath);
        if (exe is null)
            return ToolResult.Error("No Edge, Chrome or Chromium found for screenshots. Install one or set web.browserPath in the settings.");
        var width = Math.Clamp(Args.Int(args, "width") ?? 1280, 320, 3840);
        var height = Math.Clamp(Args.Int(args, "height") ?? 800, 240, 2160);
        var fullPage = Args.Bool(args, "full_page", "fullpage", "full") ?? false;
        var waitFor = Args.Str(args, "wait_for", "selector", "waitFor")?.Trim();
        var delay = Math.Clamp(Args.Int(args, "delay_ms", "delay", "wait_ms") ?? 500, 0, 10_000);

        var notes = new List<string>();
        try
        {
            await using var browser = await HeadlessBrowser.LaunchAsync(exe, width, height, ct).ConfigureAwait(false);
            await browser.SendAsync("Page.enable", null, ct).ConfigureAwait(false);
            await browser.SendAsync("Runtime.enable", null, ct).ConfigureAwait(false);
            await browser.SendAsync("Log.enable", null, ct).ConfigureAwait(false);
            await browser.SendAsync("Emulation.setDeviceMetricsOverride", new { width, height, deviceScaleFactor = 1, mobile = false }, ct).ConfigureAwait(false);
            browser.ExpectLoad();
            var nav = await browser.SendAsync("Page.navigate", new { url = uri.ToString() }, ct).ConfigureAwait(false);
            if (nav.TryGetProperty("errorText", out var et) && et.GetString() is { Length: > 0 } navError)
                return ToolResult.Error($"Could not open {uri}: {navError}", new { url = uri.ToString(), error = navError });
            if (!await browser.WaitForLoadAsync(TimeSpan.FromSeconds(20), ct).ConfigureAwait(false)) notes.Add("the page had not finished loading after 20 s");
            if (!string.IsNullOrEmpty(waitFor))
            {
                var found = false;
                var until = DateTime.UtcNow.AddSeconds(10);
                while (!found && DateTime.UtcNow < until)
                {
                    var r = await browser.SendAsync("Runtime.evaluate", new { expression = $"!!document.querySelector({JsonSerializer.Serialize(waitFor)})", returnByValue = true }, ct).ConfigureAwait(false);
                    found = r.TryGetProperty("result", out var res) && res.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.True;
                    if (!found) await Task.Delay(250, ct).ConfigureAwait(false);
                }
                if (!found) notes.Add($"\"{waitFor}\" did not appear within 10 s");
            }
            if (delay > 0) await Task.Delay(delay, ct).ConfigureAwait(false);

            var title = await browser.EvaluateStringAsync("document.title", ct).ConfigureAwait(false) ?? "";
            var shotHeight = height;
            JsonElement shot;
            if (fullPage)
            {
                var metrics = await browser.SendAsync("Page.getLayoutMetrics", null, ct).ConfigureAwait(false);
                var size = metrics.TryGetProperty("cssContentSize", out var cs) ? cs : metrics.GetProperty("contentSize");
                shotHeight = (int)Math.Clamp(Math.Ceiling(size.GetProperty("height").GetDouble()), height, 16_384);
                shot = await browser.SendAsync("Page.captureScreenshot", new
                {
                    format = "png",
                    captureBeyondViewport = true,
                    clip = new { x = 0, y = 0, width, height = shotHeight, scale = 1 },
                }, ct).ConfigureAwait(false);
            }
            else shot = await browser.SendAsync("Page.captureScreenshot", new { format = "png" }, ct).ConfigureAwait(false);

            var errors = browser.ConsoleErrors.Take(10).ToArray();
            var sb = new StringBuilder($"Screenshot of {uri} ({width}×{shotHeight}{(fullPage ? ", full page" : "")}).");
            if (title.Length > 0) sb.Append($" Title: {title}.");
            foreach (var n in notes) sb.Append($"\nNote: {n}.");
            if (errors.Length > 0) sb.Append("\nConsole errors:").Append(string.Concat(errors.Select(x => "\n- " + x)));
            return new ToolResult
            {
                Content = sb.ToString(),
                Images = [new ImagePart { MediaType = "image/png", Data = shot.GetProperty("data").GetString() ?? "" }],
                Details = new { source = "browser", url = uri.ToString(), title, width, height = shotHeight, fullPage, consoleErrors = errors, notes },
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return ToolResult.Error($"Screenshot of {uri} failed: {ex.Message}", new { url = uri.ToString(), error = ex.Message });
        }
    }
}

/// <summary>A headless Edge/Chrome with a throw-away profile, driven through the DevTools protocol of its first page.</summary>
internal sealed class HeadlessBrowser : IAsyncDisposable
{
    private readonly Process _process;
    private readonly string _profile;
    private readonly ClientWebSocket _ws = new();
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    private volatile TaskCompletionSource _loaded = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _send = new(1, 1);
    private Task? _reader;
    private int _id;

    public ConcurrentQueue<string> ConsoleErrors { get; } = new();

    private HeadlessBrowser(Process process, string profile)
    {
        _process = process;
        _profile = profile;
    }

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

    public static async Task<HeadlessBrowser> LaunchAsync(string exe, int width, int height, CancellationToken ct)
    {
        var profile = Path.Combine(Path.GetTempPath(), "netpi-browser-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(profile);
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        foreach (var a in new[]
                 {
                     "--headless=new", "--disable-gpu", "--hide-scrollbars", "--mute-audio", "--no-first-run", "--no-default-browser-check",
                     "--disable-extensions", "--disable-background-networking", "--disable-sync", "--disable-component-update",
                     "--remote-debugging-port=0", $"--user-data-dir={profile}", $"--window-size={width},{height}", "about:blank",
                 })
            psi.ArgumentList.Add(a);
        var process = Process.Start(psi) ?? throw new InvalidOperationException("the browser did not start");
        process.ErrorDataReceived += (_, _) => { };
        process.OutputDataReceived += (_, _) => { };
        process.BeginErrorReadLine();
        process.BeginOutputReadLine();
        var browser = new HeadlessBrowser(process, profile);
        try
        {
            await browser.ConnectAsync(ct).ConfigureAwait(false);
            return browser;
        }
        catch
        {
            await browser.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task ConnectAsync(CancellationToken ct)
    {
        // the browser writes its port to DevToolsActivePort in the profile
        var portFile = Path.Combine(_profile, "DevToolsActivePort");
        var until = DateTime.UtcNow.AddSeconds(20);
        int port = 0;
        while (DateTime.UtcNow < until)
        {
            if (_process.HasExited) throw new InvalidOperationException($"the browser exited (code {_process.ExitCode})");
            try
            {
                if (File.Exists(portFile) && int.TryParse((await File.ReadAllLinesAsync(portFile, ct).ConfigureAwait(false)).FirstOrDefault(), out port) && port > 0) break;
            }
            catch (IOException) { }
            await Task.Delay(100, ct).ConfigureAwait(false);
        }
        if (port <= 0) throw new TimeoutException("the browser did not open its DevTools port");

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        string? wsUrl = null;
        for (var attempt = 0; attempt < 30 && wsUrl is null; attempt++)
        {
            try
            {
                var list = JsonNode.Parse(await http.GetStringAsync($"http://127.0.0.1:{port}/json/list", ct).ConfigureAwait(false)) as JsonArray;
                wsUrl = list?.FirstOrDefault(t => (string?)t?["type"] == "page")?["webSocketDebuggerUrl"]?.GetValue<string>();
            }
            catch (HttpRequestException) { }
            if (wsUrl is null) await Task.Delay(100, ct).ConfigureAwait(false);
        }
        if (wsUrl is null)
        {
            using var res = await http.PutAsync($"http://127.0.0.1:{port}/json/new?about:blank", null, ct).ConfigureAwait(false);
            wsUrl = JsonNode.Parse(await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false))?["webSocketDebuggerUrl"]?.GetValue<string>()
                    ?? throw new InvalidOperationException("the browser has no page to drive");
        }
        _ws.Options.KeepAliveInterval = TimeSpan.Zero;
        await _ws.ConnectAsync(new Uri(wsUrl), ct).ConfigureAwait(false);
        _reader = Task.Run(ReadLoopAsync);
    }

    public async Task<JsonElement> SendAsync(string method, object? parameters, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _id);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        var json = JsonSerializer.Serialize(new { id, method, @params = parameters ?? new { } });
        await _send.WaitAsync(ct).ConfigureAwait(false);
        try { await _ws.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, ct).ConfigureAwait(false); }
        finally { _send.Release(); }
        try { return await tcs.Task.WaitAsync(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false); }
        catch (TimeoutException) { throw new TimeoutException($"{method} got no answer within 30 s"); }
        finally { _pending.TryRemove(id, out _); }
    }

    public async Task<string?> EvaluateStringAsync(string expression, CancellationToken ct)
    {
        var r = await SendAsync("Runtime.evaluate", new { expression, returnByValue = true }, ct).ConfigureAwait(false);
        return r.TryGetProperty("result", out var res) && res.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }

    /// <summary>Arm the load signal for the next navigation.</summary>
    public void ExpectLoad() => _loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task<bool> WaitForLoadAsync(TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            await _loaded.Task.WaitAsync(timeout, ct).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException) { return false; }
    }

    private async Task ReadLoopAsync()
    {
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        try
        {
            while (_ws.State == WebSocketState.Open && !_stop.IsCancellationRequested)
            {
                var r = await _ws.ReceiveAsync(buffer, _stop.Token).ConfigureAwait(false);
                if (r.MessageType == WebSocketMessageType.Close) break;
                message.Write(buffer, 0, r.Count);
                if (!r.EndOfMessage) continue;
                Dispatch(message.GetBuffer().AsSpan(0, (int)message.Length));
                message.SetLength(0);
            }
        }
        catch (Exception) { /* closed */ }
        foreach (var p in _pending.Values) p.TrySetException(new InvalidOperationException("the browser connection closed"));
    }

    private void Dispatch(ReadOnlySpan<byte> json)
    {
        using var doc = JsonDocument.Parse(json.ToArray());
        var root = doc.RootElement;
        if (root.TryGetProperty("id", out var idEl) && idEl.TryGetInt32(out var id))
        {
            if (!_pending.TryGetValue(id, out var tcs)) return;
            if (root.TryGetProperty("error", out var err))
                tcs.TrySetException(new InvalidOperationException(err.TryGetProperty("message", out var m) ? m.GetString() : err.GetRawText()));
            else tcs.TrySetResult(root.TryGetProperty("result", out var res) ? res.Clone() : default);
            return;
        }
        var method = root.TryGetProperty("method", out var me) ? me.GetString() : null;
        var p = root.TryGetProperty("params", out var pe) ? pe : default;
        switch (method)
        {
            case "Page.loadEventFired":
                _loaded.TrySetResult();
                break;
            case "Runtime.consoleAPICalled" when p.TryGetProperty("type", out var type) && type.GetString() is "error" or "assert":
                var parts = p.TryGetProperty("args", out var a) && a.ValueKind == JsonValueKind.Array
                    ? a.EnumerateArray().Select(x => x.TryGetProperty("value", out var v) ? (v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText()) : x.TryGetProperty("description", out var d) ? d.GetString() : null)
                    : [];
                Add(string.Join(" ", parts.Where(x => !string.IsNullOrEmpty(x))));
                break;
            case "Runtime.exceptionThrown" when p.TryGetProperty("exceptionDetails", out var ex):
                Add(ex.TryGetProperty("exception", out var e) && e.TryGetProperty("description", out var desc) ? desc.GetString() : ex.TryGetProperty("text", out var t) ? t.GetString() : null);
                break;
            case "Log.entryAdded" when p.TryGetProperty("entry", out var entry) && entry.TryGetProperty("level", out var lv) && lv.GetString() == "error":
                var text = entry.TryGetProperty("text", out var tx) ? tx.GetString() : null;
                var src = entry.TryGetProperty("url", out var u) ? u.GetString() : null;
                Add(string.IsNullOrEmpty(src) ? text : $"{text} ({src})");
                break;
        }
    }

    private void Add(string? error)
    {
        if (string.IsNullOrWhiteSpace(error) || ConsoleErrors.Count >= 20) return;
        var line = error.Trim();
        var nl = line.IndexOf('\n');
        ConsoleErrors.Enqueue(nl > 0 && nl < line.Length - 1 ? line[..nl] + " …" : line);
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        try { if (_ws.State == WebSocketState.Open) await _ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); }
        catch (Exception) { }
        _ws.Dispose();
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
        }
        catch (Exception) { }
        _process.Dispose();
        if (_reader is not null) try { await _reader.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); } catch (Exception) { }
        for (var attempt = 0; attempt < 10 && Directory.Exists(_profile); attempt++)
        {
            try { Directory.Delete(_profile, recursive: true); }
            catch (Exception) { await Task.Delay(200).ConfigureAwait(false); }
        }
        _stop.Dispose();
        _send.Dispose();
    }
}
