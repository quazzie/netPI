using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NetPI.Tools.Web;

/// <summary>
/// Web tools (category "web"): <c>web_fetch</c> (a page as Markdown/text, paged with offset), <c>web_search</c> (SearXNG
/// or the Brave Search API) and <c>screenshot</c> (a URL through a headless Edge/Chrome, or the NetPI window through the
/// desktop shell's <c>desktop.capture</c>). Light limits only (http/https, timeouts, size caps): agents also have curl.
/// </summary>
[NetPiPlugin("netpi.tools.web", Name = "Web tools", Description = "web_fetch, web_search (SearXNG / Brave) and screenshot", Order = 25)]
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
                SettingInfo.FilePath("web.browserPath", "Browser for screenshots", null, "Edge or Chrome (found automatically)"),
                SettingInfo.Str("web.userAgent", "User agent", null, null, "Chrome-like, ending in NetPI/0.1"),
                SettingInfo.Str("web.search.braveUrl", "Brave API URL", "https://api.search.brave.com/res/v1/web/search"),
            ],
        });
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var http = context.Track(WebHttp.Create());
        var cache = new FetchCache();
        context.Tools.Register(new WebFetchTool(context, http, cache));
        context.Tools.Register(new WebSearchTool(context, http));
        context.Tools.Register(new ScreenshotTool(context));
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
    string Provider, string? SearxngUrl, string? BraveApiKey, string BraveUrl, int SearchCount, string? BrowserPath)
{
    public static WebOptions Read(ISettings s)
    {
        var searx = Blank(s.Get<string>("web.search.searxngUrl"));
        var brave = Secret(s.Get<string>("web.search.braveApiKey")) ?? Blank(Environment.GetEnvironmentVariable("BRAVE_API_KEY"));
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
            BrowserPath: Blank(s.Get<string>("web.browserPath")));
    }

    private static string? Blank(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();

    /// <summary>A value or <c>env:NAME</c> / <c>$NAME</c> (an environment variable).</summary>
    private static string? Secret(string? v)
    {
        v = Blank(v);
        if (v is null) return null;
        if (v.StartsWith("env:", StringComparison.OrdinalIgnoreCase)) return Blank(Environment.GetEnvironmentVariable(v[4..]));
        if (v.StartsWith('$')) return Blank(Environment.GetEnvironmentVariable(v[1..]));
        return v;
    }

}

/// <summary>Lenient argument access: names match ignoring case, '_' and '-'; numbers/bools may be strings.</summary>
internal static class Args
{
    private static string Norm(string s) => s.Replace("_", "").Replace("-", "").ToLowerInvariant();

    public static JsonElement Unwrap(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.String) return args;
        try
        {
            using var doc = JsonDocument.Parse(args.GetString() ?? "{}");
            return doc.RootElement.Clone();
        }
        catch (JsonException) { return args; }
    }

    public static JsonElement? Get(JsonElement args, params string[] names)
    {
        if (args.ValueKind != JsonValueKind.Object) return null;
        foreach (var name in names)
            foreach (var p in args.EnumerateObject())
                if (Norm(p.Name) == Norm(name) && p.Value.ValueKind != JsonValueKind.Null) return p.Value;
        return null;
    }

    public static string? Str(JsonElement args, params string[] names) => Get(args, names) switch
    {
        { ValueKind: JsonValueKind.String } v => v.GetString(),
        { ValueKind: JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False } v => v.GetRawText(),
        _ => null,
    };

    public static int? Int(JsonElement args, params string[] names) => Get(args, names) switch
    {
        { ValueKind: JsonValueKind.Number } v when v.TryGetDouble(out var d) => (int)Math.Clamp(d, int.MinValue, int.MaxValue),
        { ValueKind: JsonValueKind.String } v when int.TryParse(v.GetString(), out var i) => i,
        _ => null,
    };

    public static bool? Bool(JsonElement args, params string[] names) => Get(args, names) switch
    {
        { ValueKind: JsonValueKind.True } => true,
        { ValueKind: JsonValueKind.False } => false,
        { ValueKind: JsonValueKind.String } v when bool.TryParse(v.GetString(), out var b) => b,
        { ValueKind: JsonValueKind.Number } v => v.GetDouble() != 0,
        _ => null,
    };
}
