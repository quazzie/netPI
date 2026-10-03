using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace NetPI.Tools.Web;

/// <summary>
/// The browsers behind the tool, one tab per chat:
/// <list type="bullet">
/// <item>"own" (the default): the agents' own Edge/Chrome process, headless, with a kept profile (started on first use,
/// closed when idle and when the plugin unloads). The user sees it, and logs in, through the chat's Browser view.</item>
/// <item>"chrome": the user's running Chrome — through the NetPI extension when it is connected (no debugging switch,
/// and the user can share a tab of theirs with a chat), else through the DevTools port Chrome opens when the user allows
/// remote debugging (chrome://inspect/#remote-debugging; Chrome asks the user to allow each connection). NetPI only
/// touches the tabs it opened or the user shared: it never lists the user's other tabs and never closes the browser.</item>
/// </list>
/// </summary>
internal sealed class BrowserHost : IAsyncDisposable
{
    private readonly IPluginContext _ctx;
    private readonly SemaphoreSlim _launch = new(1, 1);
    private readonly ConcurrentDictionary<string, BrowserTab> _tabs = new();
    private readonly ConcurrentDictionary<int, SharedTab> _pool = new();  // shared by the user, not given to a chat yet
    private readonly ConcurrentDictionary<string, Action<string, JsonElement>> _listeners = new();  // scratch pages by session
    private readonly ConcurrentDictionary<string, (BrowserTab Tab, string Name)> _downloads = new();
    private readonly Timer _idle;
    private ChromiumProcess? _ownProcess;
    private CdpConnection? _own;
    private CdpConnection? _chrome;
    private DateTime _lastUse = DateTime.UtcNow;
    private BrowserTab? _lastTab;

    internal sealed record SharedTab(int TabId, string Url, string Title, DateTimeOffset SharedAt);

    public BrowserHost(IPluginContext ctx)
    {
        _ctx = ctx;
        Relay = new ExtensionRelay(ctx, this);
        Relay.Event += OnEvent;
        Relay.Disconnected += OnRelayGone;
        _idle = new Timer(_ => _ = CloseIfIdleAsync(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    public ExtensionRelay Relay { get; }

    /// <summary>A chat's tab changed (opened, closed, moved to another target): the views follow it.</summary>
    public event Action<string>? TabChanged;

    public string DownloadDir => Path.Combine(_ctx.Paths.Home, "browser", "downloads");

    public BrowserTab? Peek(string sessionId) => _tabs.TryGetValue(sessionId, out var t) && t.IsOpen ? t : null;

    public IReadOnlyCollection<SharedTab> Pool => [.. _pool.Values.OrderBy(t => t.SharedAt)];

    public async Task<BrowserTab?> TabAsync(string sessionId, bool create, string where, CancellationToken ct)
    {
        _lastUse = DateTime.UtcNow;
        if (_tabs.TryGetValue(sessionId, out var tab))
        {
            if (tab.IsOpen && (!create || tab.Attached == (where == "chrome"))) return _lastTab = tab;
            _tabs.TryRemove(sessionId, out _);
            if (tab.IsOpen) await tab.CloseAsync().ConfigureAwait(false);  // moving to the other browser
        }
        if (!create) return null;
        var cdp = where == "chrome" ? await ChromeAsync(ct).ConfigureAwait(false) : await EnsureOwnAsync(ct).ConfigureAwait(false);
        // a background tab: in the user's Chrome it does not take the user's current tab or the focus
        var created = await cdp.SendAsync("Target.createTarget", new { url = "about:blank", background = where == "chrome" || !WebOptions.Read(_ctx.Settings).BrowserHeadless ? true : (bool?)null }, null, ct).ConfigureAwait(false);
        tab = await BrowserTab.AttachAsync(cdp, created.GetProperty("targetId").GetString()!, ct).ConfigureAwait(false);
        Bind(sessionId, tab);
        return _lastTab = tab;
    }

    private void Bind(string sessionId, BrowserTab tab)
    {
        _tabs[sessionId] = tab;
        tab.Changed += () => TabChanged?.Invoke(sessionId);
        TabChanged?.Invoke(sessionId);
    }

    /// <summary>Closes the chat's tab (a tab it opened; one the user shared is only let go), or null when it has none.</summary>
    public async Task<string?> CloseTabAsync(string sessionId)
    {
        if (!_tabs.TryRemove(sessionId, out var tab)) return null;
        await tab.CloseAsync().ConfigureAwait(false);
        TabChanged?.Invoke(sessionId);
        return tab.Shared ? "Let go of the tab the user shared; it stays open in their Chrome." : "Closed the browser tab.";
    }

    /// <summary>Hands the chat's tab in the user's Chrome back to the user: brought forward in its window, left open.</summary>
    public async Task<string?> LeaveTabAsync(string sessionId, CancellationToken ct)
    {
        if (!_tabs.TryGetValue(sessionId, out var tab) || !tab.IsOpen) return null;
        if (!tab.Attached) return "This tab is in the agents' own browser: use action show to put it in front of the user (the chat's Browser view), or tell them the result.";
        _tabs.TryRemove(sessionId, out _);
        var (url, title) = await tab.LeaveAsync(ct).ConfigureAwait(false);
        TabChanged?.Invoke(sessionId);
        return $"Left the tab open for the user in their Chrome: {title} — {url}. The chat has no tab now; open starts a new one.";
    }

    // ------------------------------------------------------------------ the user's Chrome

    private static string ChromeUserData(string? configured)
    {
        if (!string.IsNullOrEmpty(configured)) return configured;
        if (OperatingSystem.IsWindows()) return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Google", "Chrome", "User Data");
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return OperatingSystem.IsMacOS() ? Path.Combine(home, "Library", "Application Support", "Google", "Chrome") : Path.Combine(home, ".config", "google-chrome");
    }

    /// <summary>The user's Chrome: the extension when it is connected, else the DevTools port.</summary>
    private async Task<ICdp> ChromeAsync(CancellationToken ct)
    {
        if (Relay.IsOpen) return Relay;
        return await ConnectChromeAsync(ct).ConfigureAwait(false);
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
            var how = $"Ask the user to load the NetPI extension in Chrome (chrome://extensions → Developer mode → Load unpacked → {Relay.Folder}), " +
                      "or to allow remote debugging at chrome://inspect/#remote-debugging; or use browser: \"own\" (the agents' browser; the user sees it with action show).";
            if (endpoint is null) throw new BrowserUnavailableException($"The user's Chrome is not reachable: the NetPI extension is not connected and there is no DevTools port in {folder}. {how}");
            CdpConnection cdp;
            try { cdp = await CdpConnection.ConnectAsync(new Uri(endpoint), ct, "chrome").ConfigureAwait(false); }
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

    /// <summary>The user shared a tab of their Chrome through the extension: given to a chat, or kept for the next one that asks.</summary>
    public async Task<string?> ShareAsync(int tabId, string url, string title, string? sessionId, CancellationToken ct)
    {
        foreach (var (sid, t) in _tabs)
            if (t.Shared && t.TargetId == tabId.ToString(System.Globalization.CultureInfo.InvariantCulture))
            {
                _tabs.TryRemove(sid, out _);
                await t.CloseAsync().ConfigureAwait(false);
                TabChanged?.Invoke(sid);
            }
        _pool.TryRemove(tabId, out _);
        if (sessionId is null)
        {
            _pool[tabId] = new SharedTab(tabId, url, title, DateTimeOffset.UtcNow);
            return null;
        }
        await UseAsync(sessionId, tabId, ct).ConfigureAwait(false);
        return sessionId;
    }

    /// <summary>Gives a shared tab to a chat: the chat's tab from now on (its own tab, if it had one, is closed).</summary>
    public async Task<BrowserTab> UseAsync(string sessionId, int tabId, CancellationToken ct)
    {
        if (!Relay.IsOpen) throw new BrowserUnavailableException("The NetPI extension is not connected, so the user's shared tabs can't be reached.");
        _pool.TryRemove(tabId, out _);
        if (_tabs.TryRemove(sessionId, out var old) && old.IsOpen) await old.CloseAsync().ConfigureAwait(false);
        var tab = await BrowserTab.AttachAsync(Relay, tabId.ToString(System.Globalization.CultureInfo.InvariantCulture), ct, shared: true).ConfigureAwait(false);
        tab.Note("The user shared this tab with the chat (it is theirs: close only lets go of it).");
        Bind(sessionId, tab);
        return tab;
    }

    /// <summary>The user stopped sharing a tab: the chat that had it lets go.</summary>
    public async Task UnshareAsync(int tabId)
    {
        _pool.TryRemove(tabId, out _);
        var id = tabId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        foreach (var (sid, t) in _tabs)
            if (t.Shared && t.TargetId == id)
            {
                _tabs.TryRemove(sid, out _);
                await t.CloseAsync().ConfigureAwait(false);
                TabChanged?.Invoke(sid);
            }
    }

    /// <summary>The shared tabs and the chats that have them (for the extension's popup).</summary>
    public IEnumerable<(int TabId, string? SessionId)> SharedTabs()
    {
        foreach (var t in _pool.Values) yield return (t.TabId, null);
        foreach (var (sid, t) in _tabs)
            if (t.Shared && t.IsOpen && int.TryParse(t.TargetId, out var id)) yield return (id, sid);
    }

    private void OnRelayGone()
    {
        _pool.Clear();
        foreach (var (sid, t) in _tabs)
            if (t.Kind == "extension") { t.Gone(); _tabs.TryRemove(sid, out _); TabChanged?.Invoke(sid); }
    }

    // ------------------------------------------------------------------ the agents' own browser

    private async Task<CdpConnection> EnsureOwnAsync(CancellationToken ct)
    {
        await _launch.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_own is { IsOpen: true } open) return open;
            await StopOwnAsync().ConfigureAwait(false);
            var o = WebOptions.Read(_ctx.Settings);
            var exe = ChromiumProcess.Find(o.BrowserPath)
                      ?? throw new BrowserUnavailableException("No Edge, Chrome or Chromium found for the agents' browser. Install one or set web.browserPath in the settings.");
            var temp = o.BrowserProfile == "temp";
            var profile = temp ? Path.Combine(_ctx.Paths.TempDir, "browser-" + Guid.NewGuid().ToString("N")[..10]) : Path.Combine(_ctx.Paths.Home, "browser", o.BrowserProfile);
            // a profile used before still holds the port file of the browser that had it
            try { File.Delete(Path.Combine(profile, "DevToolsActivePort")); } catch (IOException) { }
            var flags = new List<string>
            {
                "--disable-search-engine-choice-screen", "--hide-crash-restore-bubble", "--disable-features=Translate,msWindowTabManagerPublic",
                // the agents' tabs are in the background: their timers and painting are not to be throttled
                "--disable-background-timer-throttling", "--disable-renderer-backgrounding", "--disable-backgrounding-occluded-windows",
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
            _own = await CdpConnection.ConnectAsync(endpoint, ct, "own").ConfigureAwait(false);
            _own.Event += OnEvent;
            await _own.SendAsync("Target.setDiscoverTargets", new { discover = true }, null, ct).ConfigureAwait(false);
            // downloads land in one folder, and the tab that started one is told where
            Directory.CreateDirectory(DownloadDir);
            try { await _own.SendAsync("Browser.setDownloadBehavior", new { behavior = "allow", downloadPath = DownloadDir, eventsEnabled = true }, null, ct).ConfigureAwait(false); }
            catch (InvalidOperationException ex) { _ctx.Logger.LogDebug(ex, "browser: downloads are not reported"); }
            _ctx.Logger.LogInformation("browser started: {Exe} ({Mode}, profile {Profile})", exe, o.BrowserHeadless ? "headless" : "window", profile);
            return _own;
        }
        finally { _launch.Release(); }
    }

    /// <summary>
    /// A page of its own for one screenshot: a fresh browser context (no cookies, no storage) in the agents' running
    /// browser, so a screenshot does not start a browser. Null when that browser shows windows (a context would open one).
    /// </summary>
    public async Task<ScratchPage?> ScratchAsync(int width, int height, CancellationToken ct)
    {
        if (!WebOptions.Read(_ctx.Settings).BrowserHeadless) return null;
        _lastUse = DateTime.UtcNow;
        var cdp = await EnsureOwnAsync(ct).ConfigureAwait(false);
        var context = (await cdp.SendAsync("Target.createBrowserContext", new { disposeOnDetach = true }, null, ct).ConfigureAwait(false)).GetProperty("browserContextId").GetString()!;
        var target = (await cdp.SendAsync("Target.createTarget", new { url = "about:blank", browserContextId = context, width, height }, null, ct).ConfigureAwait(false)).GetProperty("targetId").GetString()!;
        var session = (await cdp.SendAsync("Target.attachToTarget", new { targetId = target, flatten = true }, null, ct).ConfigureAwait(false)).GetProperty("sessionId").GetString()!;
        var page = new ScratchPage(cdp, context, session, () => _listeners.TryRemove(session, out _));
        _listeners[session] = page.OnEvent;
        return page;
    }

    // ------------------------------------------------------------------ events

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
                if (tab.TargetId == gone.GetString()) { tab.Gone(); _tabs.TryRemove(key, out _); TabChanged?.Invoke(key); }
            if (int.TryParse(gone.GetString(), out var pooled)) _pool.TryRemove(pooled, out _);
            return;
        }
        if (method == "Target.detachedFromTarget" && sessionId is null && p.TryGetProperty("sessionId", out var detached))
        {
            // the user cancelled the extension's debugging bar, or the browser dropped the tab
            foreach (var (key, tab) in _tabs)
                if (tab.SessionId == detached.GetString()) { tab.Gone(); _tabs.TryRemove(key, out _); TabChanged?.Invoke(key); }
            return;
        }
        if (method == "Browser.downloadWillBegin" && p.TryGetProperty("guid", out var guid))
        {
            var name = p.TryGetProperty("suggestedFilename", out var f) ? f.GetString() ?? "download" : "download";
            var owner = _lastTab;
            if (owner is null) return;
            owner.DownloadStarted();
            _downloads[guid.GetString()!] = (owner, name);
            return;
        }
        if (method == "Browser.downloadProgress" && p.TryGetProperty("guid", out var g) && p.TryGetProperty("state", out var state) && state.GetString() is "completed" or "canceled")
        {
            if (!_downloads.TryRemove(g.GetString()!, out var d)) return;
            d.Tab.DownloadEnded();
            d.Tab.Note(state.GetString() == "completed"
                ? $"Downloaded {d.Name} to {Path.Combine(DownloadDir, d.Name)}."
                : $"The download of {d.Name} was cancelled.");
            return;
        }
        if (sessionId is null) return;
        if (_listeners.TryGetValue(sessionId, out var listener)) { listener(method, p); return; }
        foreach (var tab in _tabs.Values)
            if (tab.Owns(sessionId)) { tab.OnEvent(method, p, sessionId); return; }
    }

    private async Task CloseIfIdleAsync()
    {
        var minutes = WebOptions.Read(_ctx.Settings).BrowserIdleMinutes;
        if ((_ownProcess is null && _chrome is null) || DateTime.UtcNow - _lastUse < TimeSpan.FromMinutes(minutes)) return;
        // a chat's tab someone is looking at keeps its browser
        if (_tabs.Values.Any(t => t.HasViewers)) return;
        if (!await _launch.WaitAsync(0).ConfigureAwait(false)) return;
        try
        {
            if (_ownProcess is not null)
            {
                _ctx.Logger.LogInformation("browser: closed after {Minutes} idle minutes", minutes);
                await StopOwnAsync().ConfigureAwait(false);
            }
            // the user's Chrome asks again for every new connection: it is kept while a chat has a tab there
            if (_chrome is not null && !_tabs.Values.Any(t => t.Kind == "chrome" && t.IsOpen)) await DisconnectChromeAsync().ConfigureAwait(false);
        }
        finally { _launch.Release(); }
    }

    /// <summary>Drops the connection to the user's Chrome (Chrome's automation banner goes with it); its tabs stay open.</summary>
    private async Task DisconnectChromeAsync()
    {
        foreach (var (key, tab) in _tabs) if (tab.Kind == "chrome") { _tabs.TryRemove(key, out _); TabChanged?.Invoke(key); }
        var cdp = _chrome; _chrome = null;
        if (cdp is not null) await cdp.DisposeAsync().ConfigureAwait(false);
    }

    private async Task StopOwnAsync()
    {
        foreach (var (key, tab) in _tabs) if (tab.Kind == "own") { _tabs.TryRemove(key, out _); TabChanged?.Invoke(key); }
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
            // shared tabs and the extension's own: let go, never closed
            foreach (var t in _tabs.Values) if (t.Kind == "extension" && t.IsOpen) await t.CloseAsync().ConfigureAwait(false);
            await StopOwnAsync().ConfigureAwait(false);
            await DisconnectChromeAsync().ConfigureAwait(false);  // the user's tabs, and ours there, stay open
            await Relay.DisposeAsync().ConfigureAwait(false);
        }
        finally { _launch.Release(); }
        _launch.Dispose();
    }
}

/// <summary>A page in a throw-away browser context of the agents' browser (one screenshot): its session and its events.</summary>
internal sealed class ScratchPage(ICdp cdp, string contextId, string sessionId, Action unlisten) : IAsyncDisposable
{
    public event Action<string, JsonElement>? Event;

    public void OnEvent(string method, JsonElement p) => Event?.Invoke(method, p);

    public Task<JsonElement> SendAsync(string method, object? parameters, CancellationToken ct) => cdp.SendAsync(method, parameters, sessionId, ct);

    public async ValueTask DisposeAsync()
    {
        unlisten();
        try { await cdp.SendAsync("Target.disposeBrowserContext", new { browserContextId = contextId }, null, CancellationToken.None, 5).ConfigureAwait(false); }
        catch (Exception) { }
    }
}
