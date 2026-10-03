using System.Collections.Concurrent;
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
        Description = "Screenshot a web page (a url, rendered headless) or, without a url, the NetPI window as the user sees it.",
        Help =
            "Returns the image plus the page title and any console errors. width/height: the viewport (default 1280×800). " +
            "wait_for: a CSS selector to wait for (up to 10 s); delay_ms: an extra wait after loading (default 500); full_page " +
            "captures the whole scroll height.",
        Parameters = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["url"] = new JsonObject { ["type"] = "string" },
                ["width"] = new JsonObject { ["type"] = "integer" },
                ["height"] = new JsonObject { ["type"] = "integer" },
                ["full_page"] = new JsonObject { ["type"] = "boolean" },
                ["wait_for"] = new JsonObject { ["type"] = "string" },
                ["delay_ms"] = new JsonObject { ["type"] = "integer" },
            },
        },
        PromptGuidelines = ["Use screenshot to check UI work visually."],
    };

    public async Task<ToolResult> ExecuteAsync(ToolContext context, JsonElement args, CancellationToken ct)
    {
        if (context.Model?.InputModalities is { Count: > 0 } mods && !mods.Contains("image"))
            return ToolResult.Error($"The current model ({context.Model.Id}) can't see images, so a screenshot would not help it. Use a vision model for visual checks.");
        var a = new ToolArgs(args);
        var url = a.Str("url", "href", "page")?.Trim();
        return string.IsNullOrEmpty(url) ? await AppWindowAsync(ct).ConfigureAwait(false) : await PageAsync(url, a.Raw, ct).ConfigureAwait(false);
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
        var exe = ChromiumProcess.Find(o.BrowserPath);
        if (exe is null)
            return ToolResult.Error("No Edge, Chrome or Chromium found for screenshots. Install one or set web.browserPath in the settings.");
        var a = new ToolArgs(args);
        var width = Math.Clamp(a.Int("width") ?? 1280, 320, 3840);
        var height = Math.Clamp(a.Int("height") ?? 800, 240, 2160);
        var fullPage = a.Bool("full_page", "fullpage", "full") ?? false;
        var waitFor = a.Str("wait_for", "selector", "waitFor")?.Trim();
        var delay = Math.Clamp(a.Int("delay_ms", "delay", "wait_ms") ?? 500, 0, 10_000);

        var notes = new List<string>();
        try
        {
            await using var browser = await ScreenshotPage.LaunchAsync(exe, width, height, ct).ConfigureAwait(false);
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

/// <summary>
/// The page a screenshot is taken of: a browser of its own (a throw-away profile, one per call) and its first page,
/// over the same DevTools connection the browser tool uses.
/// </summary>
internal sealed class ScreenshotPage : IAsyncDisposable
{
    private readonly ChromiumProcess _process;
    private CdpConnection? _cdp;
    private volatile TaskCompletionSource _loaded = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ConcurrentQueue<string> ConsoleErrors { get; } = new();

    private ScreenshotPage(ChromiumProcess process) => _process = process;

    /// <summary>Start a headless browser for one call and attach to the first page it opened.</summary>
    public static async Task<ScreenshotPage> LaunchAsync(string exe, int width, int height, CancellationToken ct)
    {
        var profile = Path.Combine(Path.GetTempPath(), "netpi-browser-" + Guid.NewGuid().ToString("N")[..10]);
        var process = ChromiumProcess.Start(exe, profile, width, height,
            ["--headless=new", "--disable-features=msWindowTabManagerPublic", "--disable-gpu", "--hide-scrollbars",
             "--disable-extensions", "--disable-background-networking", "--disable-component-update"], temp: true);
        var page = new ScreenshotPage(process);
        try
        {
            await process.WaitForEndpointAsync(ct).ConfigureAwait(false);
            await page.ConnectAsync(ct).ConfigureAwait(false);
            return page;
        }
        catch
        {
            await page.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>The page target to drive: the one the browser opened, or a new one when it opened none.</summary>
    private async Task ConnectAsync(CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        string? wsUrl = null;
        for (var attempt = 0; attempt < 30 && wsUrl is null; attempt++)
        {
            try
            {
                var list = JsonNode.Parse(await http.GetStringAsync($"http://127.0.0.1:{_process.Port}/json/list", ct).ConfigureAwait(false)) as JsonArray;
                wsUrl = list?.FirstOrDefault(t => (string?)t?["type"] == "page")?["webSocketDebuggerUrl"]?.GetValue<string>();
            }
            catch (HttpRequestException) { }
            if (wsUrl is null) await Task.Delay(100, ct).ConfigureAwait(false);
        }
        if (wsUrl is null)
        {
            using var res = await http.PutAsync($"http://127.0.0.1:{_process.Port}/json/new?about:blank", null, ct).ConfigureAwait(false);
            wsUrl = JsonNode.Parse(await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false))?["webSocketDebuggerUrl"]?.GetValue<string>()
                    ?? throw new InvalidOperationException("the browser has no page to drive");
        }
        _cdp = await CdpConnection.ConnectAsync(new Uri(wsUrl), ct).ConfigureAwait(false);
        _cdp.Event += OnEvent;
    }

    public Task<JsonElement> SendAsync(string method, object? parameters, CancellationToken ct) =>
        (_cdp ?? throw new InvalidOperationException("the browser page is not connected"))
            .SendAsync(method, parameters, null, ct);

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

    /// <summary>What the page reports about itself: the load, and the console errors the model is told about.</summary>
    private void OnEvent(string method, JsonElement p, string? sessionId)
    {
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
        var cdp = _cdp;
        _cdp = null;
        if (cdp is not null) await cdp.DisposeAsync().ConfigureAwait(false);
        await _process.DisposeAsync().ConfigureAwait(false);
    }
}
