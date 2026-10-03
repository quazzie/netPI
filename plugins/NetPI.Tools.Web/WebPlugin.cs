using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace NetPI.Tools.Web;

/// <summary>
/// Web tools (category "web"): <c>web_fetch</c> (a page as Markdown/text, paged with offset), <c>web_search</c> (SearXNG
/// or the Brave Search API), <c>screenshot</c> (a URL through a headless Edge/Chrome, or the NetPI window through the
/// desktop shell's <c>desktop.capture</c>) and <c>browser</c> (the agents' own browser: a tab per chat, driven over the
/// DevTools protocol). Light limits only (http/https, timeouts, size caps): agents also have curl.
/// </summary>
[NetPiPlugin("netpi.tools.web", Name = "Web tools", Description = "web_fetch, web_search (SearXNG / Brave), screenshot and browser", Order = 25)]
public sealed class WebPlugin : INetPiPlugin
{
    public Task StartAsync(IPluginContext context, CancellationToken ct)
    {
        context.Services.Register(new SettingsSection
        {
            Id = "web", Title = "Web", Group = "Tools", Order = 30,
            Settings =
            [
                SettingInfo.Choice("web.search.provider", "Search with", "auto", ["auto", "searxng", "brave"], "auto: SearXNG when a URL is set, falling back to Brave."),
                SettingInfo.Str("web.search.searxngUrl", "SearXNG URL", null, "The instance must allow format=json.", "e.g. http://192.168.1.3:8888"),
                SettingInfo.Secret("web.search.braveApiKey", "Brave Search API key", "env:NAME reads an environment variable.", "env BRAVE_API_KEY"),
                SettingInfo.Int("web.search.count", "Results per search", 8, null, 1, 20),
                SettingInfo.Int("web.fetch.maxChars", "Characters per page part", 20000, "Longer pages continue with offset.", 1000, 200000),
                SettingInfo.Int("web.fetch.timeoutSeconds", "Fetch timeout", 30, null, 1, 600, "s"),
                SettingInfo.Int("web.fetch.maxBytes", "Download limit", 5000000, null, 10000, null, "bytes"),
                SettingInfo.FilePath("web.browserPath", "Browser for screenshots", "Empty: the Edge or Chrome found.", ChromiumProcess.Find(null) ?? "none found (install Edge or Chrome)"),
                SettingInfo.Str("web.userAgent", "User agent", null, null, WebHttp.UserAgent),
                SettingInfo.Str("web.search.braveUrl", "Brave API URL", "https://api.search.brave.com/res/v1/web/search"),
                SettingInfo.Choice("browser.target", "Browser tabs open in", "chrome", ["chrome", "own"], "chrome: the user's running Chrome (allow remote debugging at chrome://inspect/#remote-debugging). own: the agents' hidden browser. A call can ask for the other."),
                SettingInfo.Str("browser.chromeUserData", "Chrome user data folder", null, "Where the user's Chrome keeps DevToolsActivePort. Empty: Chrome's default folder.", "default"),
                SettingInfo.Bool("browser.headless", "Browser without a window", true, "Off: the agents' browser opens a window (to log in to a site by hand, or to watch). Applies when the browser next starts."),
                SettingInfo.Str("browser.profile", "Browser profile", "default", "A name: logins and cookies are kept in <home>/browser/<name>. temp: a fresh profile each time the browser starts.", "default"),
                SettingInfo.Int("browser.idleMinutes", "Close the browser after", 10, "Minutes without a browser call.", 1, 1440, "min"),
                SettingInfo.Int("browser.maxControls", "Controls listed per page", 200, "The ones nearest the visible part; find reaches the rest.", 50, 1000),
            ],
        });
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var http = context.Track(WebHttp.Create());
        var cache = new FetchCache();
        context.Tools.Register(new WebFetchTool(context, http, cache));
        context.Tools.Register(new WebSearchTool(context, http));
        context.Tools.Register(new ScreenshotTool(context));
        var browser = new BrowserHost(context);
        context.Tools.Register(new BrowserTool(context, browser));
        context.Events.Subscribe(EventTypes.SessionDeleted, e =>
        {
            if (e.As<JsonObject>()?["id"]?.GetValue<string>() is { Length: > 0 } id) _ = browser.CloseTabAsync(id);
        });
        // closes the browser when the plugin unloads (Stopping is cancelled first)
        context.Stopping.Register(() =>
        {
            try { browser.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(10)); }
            catch (Exception ex) { context.Logger.LogDebug(ex, "closing the browser failed"); }
        });
        return Task.CompletedTask;
    }
}

internal static class WebHttp
{
    public const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0 Safari/537.36 NetPI/0.1";

    public static HttpClient Create()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 10,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };
        var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
        http.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
        return http;
    }
}

/// <summary><c>web.*</c> settings, read on every call (only NetPI's own settings and the <c>BRAVE_API_KEY</c> variable).</summary>
internal sealed record WebOptions(
    int FetchMaxChars, int FetchTimeoutSeconds, long FetchMaxBytes, string? UserAgent,
    string Provider, string? SearxngUrl, string? BraveApiKey, string BraveUrl, int SearchCount, string? BrowserPath,
    bool BrowserHeadless, string BrowserProfile, int BrowserIdleMinutes, int BrowserMaxControls, string BrowserTarget, string? BrowserChromeUserData)
{
    public static WebOptions Read(ISettings s)
    {
        var searx = Blank(s.Get<string>("web.search.searxngUrl"));
        var brave = s.GetSecret("web.search.braveApiKey") ?? Blank(Environment.GetEnvironmentVariable("BRAVE_API_KEY"));
        return new WebOptions(
            FetchMaxChars: Math.Clamp(s.Get("web.fetch.maxChars", 20_000), 1_000, 200_000),
            FetchTimeoutSeconds: Math.Clamp(s.Get("web.fetch.timeoutSeconds", 30), 3, 300),
            FetchMaxBytes: Math.Clamp(s.Get("web.fetch.maxBytes", 5_000_000L), 100_000L, 100_000_000L),
            UserAgent: Blank(s.Get<string>("web.userAgent")),
            Provider: (Blank(s.Get<string>("web.search.provider")) ?? "auto").ToLowerInvariant(),
            SearxngUrl: searx?.TrimEnd('/'),
            BraveApiKey: brave,
            BraveUrl: (Blank(s.Get<string>("web.search.braveUrl")) ?? "https://api.search.brave.com/res/v1/web/search"),
            SearchCount: Math.Clamp(s.Get("web.search.count", 8), 1, 20),
            BrowserPath: Blank(s.Get<string>("web.browserPath")),
            BrowserHeadless: s.Get("browser.headless", true),
            BrowserProfile: ProfileName(s.Get<string>("browser.profile")),
            BrowserIdleMinutes: Math.Clamp(s.Get("browser.idleMinutes", 10), 1, 1440),
            BrowserMaxControls: Math.Clamp(s.Get("browser.maxControls", 200), 50, 1000),
            BrowserTarget: Blank(s.Get<string>("browser.target"))?.ToLowerInvariant() == "own" ? "own" : "chrome",
            BrowserChromeUserData: Blank(s.Get<string>("browser.chromeUserData")));
    }

    private static string? Blank(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();

    /// <summary>A profile folder name (letters, digits, '-', '_'), or "temp"; anything else is "default".</summary>
    private static string ProfileName(string? v) => Blank(v) is { } n && Regex.IsMatch(n, "^[A-Za-z0-9_-]{1,40}$") ? n.ToLowerInvariant() : "default";

}
