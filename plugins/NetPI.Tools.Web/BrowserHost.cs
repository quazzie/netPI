using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace NetPI.Tools.Web;

/// <summary>
/// The browsers behind the tool, one tab per chat:
/// <list type="bullet">
/// <item>"chrome": the user's running Chrome, reached through the DevTools port it opens when the user allows remote
/// debugging (chrome://inspect/#remote-debugging; the port is in <c>DevToolsActivePort</c> in its user data folder). One
/// connection (Chrome asks the user to allow each connection), dropped after <c>browser.idleMinutes</c> without a call.
/// NetPI only ever touches the tabs it opened: it never lists the user's other tabs, never closes the browser, and closes
/// only its own tabs.</item>
/// <item>"own": the agents' own Edge/Chrome process (started on first use, closed when idle and when the plugin
/// unloads).</item>
/// </list>
/// </summary>
internal sealed class BrowserHost : IAsyncDisposable
{
    private readonly IPluginContext _ctx;
    private readonly SemaphoreSlim _launch = new(1, 1);
    private readonly ConcurrentDictionary<string, BrowserTab> _tabs = new();
    private readonly Timer _idle;
    private ChromiumProcess? _ownProcess;
    private CdpConnection? _own;
    private CdpConnection? _chrome;
    private DateTime _lastUse = DateTime.UtcNow;

    public BrowserHost(IPluginContext ctx)
    {
        _ctx = ctx;
        _idle = new Timer(_ => _ = CloseIfIdleAsync(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    public async Task<BrowserTab?> TabAsync(string sessionId, bool create, string where, CancellationToken ct)
    {
        _lastUse = DateTime.UtcNow;
        if (_tabs.TryGetValue(sessionId, out var tab))
        {
            if (tab.IsOpen && (!create || tab.Attached == (where == "chrome"))) return tab;
            _tabs.TryRemove(sessionId, out _);
            if (tab.IsOpen) await tab.CloseAsync().ConfigureAwait(false);  // moving to the other browser
        }
        if (!create) return null;
        var attached = where == "chrome";
        var cdp = attached ? await ConnectChromeAsync(ct).ConfigureAwait(false) : await EnsureOwnAsync(ct).ConfigureAwait(false);
        // a background tab: in the user's Chrome it does not take the user's current tab or the focus
        var created = await cdp.SendAsync("Target.createTarget", new { url = "about:blank", background = attached || !WebOptions.Read(_ctx.Settings).BrowserHeadless ? true : (bool?)null }, null, ct).ConfigureAwait(false);
        tab = await BrowserTab.AttachAsync(cdp, created.GetProperty("targetId").GetString()!, attached, ct).ConfigureAwait(false);
        _tabs[sessionId] = tab;
        return tab;
    }

    /// <summary>Closes the chat's tab (a tab it opened; the text says what happened), or null when it has none.</summary>
    public async Task<string?> CloseTabAsync(string sessionId)
    {
        if (!_tabs.TryRemove(sessionId, out var tab)) return null;
        await tab.CloseAsync().ConfigureAwait(false);
        return "Closed the browser tab.";
    }

    /// <summary>Hands the chat's tab in the user's Chrome back to the user: brought forward in its window, left open.</summary>
    public async Task<string?> LeaveTabAsync(string sessionId, CancellationToken ct)
    {
        if (!_tabs.TryGetValue(sessionId, out var tab) || !tab.IsOpen) return null;
        if (!tab.Attached) return "This tab is in the hidden browser, which the user can't see: tell the user the result (or its URL) instead.";
        _tabs.TryRemove(sessionId, out _);
        var (url, title) = await tab.LeaveAsync(ct).ConfigureAwait(false);
        return $"Left the tab open for the user in their Chrome: {title} — {url}. The chat has no tab now; open starts a new one.";
    }

    private static string ChromeUserData(string? configured)
    {
        if (!string.IsNullOrEmpty(configured)) return configured;
        if (OperatingSystem.IsWindows()) return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Google", "Chrome", "User Data");
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return OperatingSystem.IsMacOS() ? Path.Combine(home, "Library", "Application Support", "Google", "Chrome") : Path.Combine(home, ".config", "google-chrome");
    }

    private async Task<CdpConnection> ConnectChromeAsync(CancellationToken ct)
    {
        await _launch.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_chrome is { IsOpen: true } open) return open;
            if (_chrome is not null) await _chrome.DisposeAsync().ConfigureAwait(false);
            _chrome = null;
            var o = WebOptions.Read(_ctx.Settings);
            var folder = ChromeUserData(o.BrowserChromeUserData);
            var portFile = Path.Combine(folder, "DevToolsActivePort");
            string? endpoint = null;
            try
            {
                if (await File.ReadAllLinesAsync(portFile, ct).ConfigureAwait(false) is [var port, var wsPath, ..] && int.TryParse(port, out var p) && p > 0)
                    endpoint = $"ws://127.0.0.1:{p}{wsPath}";
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            const string how = "Start Chrome and allow remote debugging at chrome://inspect/#remote-debugging (once), or use browser: \"own\" for the hidden browser.";
            if (endpoint is null) throw new BrowserUnavailableException($"The user's Chrome is not reachable: no DevTools port in {folder}. {how}");
            CdpConnection cdp;
            try { cdp = await CdpConnection.ConnectAsync(new Uri(endpoint), ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is WebSocketException or HttpRequestException)
            {
                throw new BrowserUnavailableException($"The user's Chrome did not accept the connection ({ex.Message}). {how}");
            }
            cdp.Event += OnEvent;
            // Chrome asks the user to allow the connection: the first call waits for that answer
            try { await cdp.SendAsync("Target.setDiscoverTargets", new { discover = true }, null, ct, timeoutSeconds: 120).ConfigureAwait(false); }
            catch (Exception ex) when (ex is TimeoutException or InvalidOperationException)
            {
                await cdp.DisposeAsync().ConfigureAwait(false);
                throw new BrowserUnavailableException($"The user did not allow the connection in Chrome ({ex.Message}). Ask them to click Allow in Chrome's dialog, then try again.");
            }
            _ctx.Logger.LogInformation("browser: connected to the user's Chrome ({Folder})", folder);
            return _chrome = cdp;
        }
        finally { _launch.Release(); }
    }

    private async Task<CdpConnection> EnsureOwnAsync(CancellationToken ct)
    {
        await _launch.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_own is { IsOpen: true } open) return open;
            await StopOwnAsync().ConfigureAwait(false);
            var o = WebOptions.Read(_ctx.Settings);
            var exe = ChromiumProcess.Find(o.BrowserPath)
                      ?? throw new BrowserUnavailableException("No Edge, Chrome or Chromium found for the hidden browser. Install one or set web.browserPath in the settings.");
            var temp = o.BrowserProfile == "temp";
            var profile = temp ? Path.Combine(_ctx.Paths.TempDir, "browser-" + Guid.NewGuid().ToString("N")[..10]) : Path.Combine(_ctx.Paths.Home, "browser", o.BrowserProfile);
            // a profile used before still holds the port file of the browser that had it
            try { File.Delete(Path.Combine(profile, "DevToolsActivePort")); } catch (IOException) { }
            var flags = new List<string>
            {
                "--disable-search-engine-choice-screen", "--hide-crash-restore-bubble", "--disable-features=Translate,msWindowTabManagerPublic",
            };
            if (o.BrowserHeadless) flags.Insert(0, "--headless=new");
            _ownProcess = ChromiumProcess.Start(exe, profile, 1280, 900, flags, temp);
            Uri endpoint;
            try { endpoint = await _ownProcess.WaitForEndpointAsync(ct).ConfigureAwait(false); }
            catch (TimeoutException)
            {
                throw new BrowserUnavailableException(_ownProcess.HasExited
                    ? $"The browser profile {profile} is in use by another browser process: close it, or set browser.profile to temp."
                    : "The browser did not open its DevTools port within 20 s.");
            }
            _own = await CdpConnection.ConnectAsync(endpoint, ct).ConfigureAwait(false);
            _own.Event += OnEvent;
            await _own.SendAsync("Target.setDiscoverTargets", new { discover = true }, null, ct).ConfigureAwait(false);
            _ctx.Logger.LogInformation("browser started: {Exe} ({Mode}, profile {Profile})", exe, o.BrowserHeadless ? "headless" : "window", profile);
            return _own;
        }
        finally { _launch.Release(); }
    }

    private void OnEvent(string method, JsonElement p, string? sessionId)
    {
        if (method == "Target.targetCreated" && p.TryGetProperty("targetInfo", out var info) && info.GetProperty("type").GetString() == "page"
            && info.TryGetProperty("openerId", out var opener))
        {
            // a link that opens a new tab: the chat follows it, as a user would (only from a tab of ours)
            var openerId = opener.GetString();
            foreach (var tab in _tabs.Values)
                if (tab.TargetId == openerId) _ = tab.FollowAsync(info.GetProperty("targetId").GetString()!);
            return;
        }
        if (method == "Target.targetDestroyed" && p.TryGetProperty("targetId", out var gone))
        {
            // the user closed the tab: the chat's next call opens a new one
            foreach (var (key, tab) in _tabs)
                if (tab.TargetId == gone.GetString()) { tab.Gone(); _tabs.TryRemove(key, out _); }
            return;
        }
        if (sessionId is null) return;
        foreach (var tab in _tabs.Values)
            if (tab.SessionId == sessionId) { tab.OnEvent(method, p); return; }
    }

    private async Task CloseIfIdleAsync()
    {
        var minutes = WebOptions.Read(_ctx.Settings).BrowserIdleMinutes;
        if ((_ownProcess is null && _chrome is null) || DateTime.UtcNow - _lastUse < TimeSpan.FromMinutes(minutes)) return;
        if (!await _launch.WaitAsync(0).ConfigureAwait(false)) return;
        try
        {
            _ctx.Logger.LogInformation("browser: closed after {Minutes} idle minutes", minutes);
            await StopOwnAsync().ConfigureAwait(false);
            await DisconnectChromeAsync().ConfigureAwait(false);
        }
        finally { _launch.Release(); }
    }

    /// <summary>Drops the connection to the user's Chrome (Chrome's automation banner goes with it); its tabs stay open.</summary>
    private async Task DisconnectChromeAsync()
    {
        foreach (var (key, tab) in _tabs) if (tab.Attached) _tabs.TryRemove(key, out _);
        var cdp = _chrome; _chrome = null;
        if (cdp is not null) await cdp.DisposeAsync().ConfigureAwait(false);
    }

    private async Task StopOwnAsync()
    {
        foreach (var (key, tab) in _tabs) if (!tab.Attached) _tabs.TryRemove(key, out _);
        var cdp = _own; _own = null;
        if (cdp is not null) await cdp.DisposeAsync().ConfigureAwait(false);
        var process = _ownProcess; _ownProcess = null;
        if (process is not null) await process.DisposeAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await _idle.DisposeAsync().ConfigureAwait(false);
        await _launch.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopOwnAsync().ConfigureAwait(false);
            await DisconnectChromeAsync().ConfigureAwait(false);  // the user's tabs, and ours there, stay open
        }
        finally { _launch.Release(); }
        _launch.Dispose();
    }
}
