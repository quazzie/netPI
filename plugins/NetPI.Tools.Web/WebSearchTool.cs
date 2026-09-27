using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace NetPI.Tools.Web;

internal sealed record SearchHit(string Title, string Url, string Snippet, string? Age);

/// <summary>
/// <c>web_search</c> through SearXNG (<c>/search?format=json</c>) or the Brave Search API. Provider "auto" tries SearXNG
/// first when a URL is configured and falls back to Brave when it fails or finds nothing.
/// </summary>
internal sealed partial class WebSearchTool(IPluginContext ctx, HttpClient http) : IAgentTool
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    public ToolDefinition Definition { get; } = new()
    {
        Name = "web_search",
        Label = "Search",
        Category = "web",
        ReadOnly = true,
        SummaryArg = "query",
        Description = "Search the web: titles, URLs and snippets (read the promising ones with web_fetch).",
        Help = "Search operators such as site:example.com or \"exact phrase\" work. count: default 8, max 20. recency limits results to the last day, week, month or year.",
        Parameters = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["query"] = new JsonObject { ["type"] = "string" },
                ["count"] = new JsonObject { ["type"] = "integer" },
                ["recency"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("day", "week", "month", "year") },
            },
            ["required"] = new JsonArray("query"),
        },
        PromptGuidelines = ["Use web_search to find current information or documentation."],
    };

    public async Task<ToolResult> ExecuteAsync(ToolContext context, JsonElement args, CancellationToken ct)
    {
        args = Args.Unwrap(args);
        var query = Args.Str(args, "query", "q", "search", "text")?.Trim();
        if (string.IsNullOrEmpty(query)) return ToolResult.Error("web_search needs a query.");
        var o = WebOptions.Read(ctx.Settings);
        var count = Math.Clamp(Args.Int(args, "count", "limit", "n", "max_results") ?? o.SearchCount, 1, 20);
        var recency = (Args.Str(args, "recency", "freshness", "time_range") ?? "").Trim().ToLowerInvariant() switch
        {
            "day" or "d" or "24h" or "pd" => "day",
            "week" or "w" or "pw" => "week",
            "month" or "m" or "pm" => "month",
            "year" or "y" or "py" => "year",
            _ => null,
        };

        var order = o.Provider switch
        {
            "searxng" => ["searxng"],
            "brave" => ["brave"],
            _ => new[] { "searxng", "brave" },
        };
        var tried = new List<string>();
        foreach (var provider in order)
        {
            if (provider == "searxng" && o.SearxngUrl is null) continue;
            if (provider == "brave" && o.BraveApiKey is null) continue;
            try
            {
                var hits = provider == "searxng"
                    ? await SearxngAsync(o.SearxngUrl!, query, count, recency, ct).ConfigureAwait(false)
                    : await BraveAsync(o.BraveUrl, o.BraveApiKey!, query, count, recency, ct).ConfigureAwait(false);
                if (hits.Count == 0 && provider != order[^1] && Configured(o, order[^1]))
                {
                    tried.Add($"{provider}: no results");
                    continue;
                }
                return Result(query, provider, hits, tried);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or InvalidOperationException)
            {
                tried.Add($"{provider}: {(ex is OperationCanceledException ? "timed out" : ex.Message)}");
            }
        }
        if (tried.Count == 0)
            return ToolResult.Error("Web search is not configured: set web.search.searxngUrl or web.search.braveApiKey in the settings (docs/SETTINGS.md).");
        return ToolResult.Error("Web search failed: " + string.Join("; ", tried), new { query, errors = tried });
    }

    private static bool Configured(WebOptions o, string provider) => provider == "searxng" ? o.SearxngUrl is not null : o.BraveApiKey is not null;

    private static ToolResult Result(string query, string provider, List<SearchHit> hits, List<string> tried)
    {
        var sb = new StringBuilder($"Search results for \"{query}\" ({provider}, {hits.Count}):");
        if (tried.Count > 0) sb.Append($"\n(also tried: {string.Join("; ", tried)})");
        var i = 0;
        foreach (var h in hits)
        {
            sb.Append($"\n\n{++i}. {h.Title}\n   {h.Url}");
            if (h.Age is { Length: > 0 }) sb.Append($"  ({h.Age})");
            if (h.Snippet.Length > 0) sb.Append("\n   ").Append(h.Snippet);
        }
        if (hits.Count == 0) sb.Append("\n\nNo results.");
        return ToolResult.Ok(sb.ToString(), new
        {
            query,
            provider,
            results = hits.Select(h => new { title = h.Title, url = h.Url, snippet = h.Snippet, age = h.Age }).ToArray(),
            fallback = tried.Count > 0 ? tried : null,
        });
    }

    private async Task<List<SearchHit>> SearxngAsync(string baseUrl, string query, int count, string? recency, CancellationToken ct)
    {
        var url = $"{baseUrl}/search?format=json&q={Uri.EscapeDataString(query)}" + (recency is null ? "" : $"&time_range={recency}");
        var root = await GetJsonAsync(url, null, ct).ConfigureAwait(false);
        var hits = new List<SearchHit>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in root["results"] as JsonArray ?? [])
        {
            var u = S(r?["url"]);
            if (u.Length == 0 || !seen.Add(u)) continue;
            var date = S(r?["publishedDate"]);
            hits.Add(new SearchHit(Clean(S(r?["title"])), u, Clean(S(r?["content"])), date.Length >= 10 ? date[..10] : null));
            if (hits.Count == count) break;
        }
        return hits;
    }

    private async Task<List<SearchHit>> BraveAsync(string endpoint, string key, string query, int count, string? recency, CancellationToken ct)
    {
        var freshness = recency switch { "day" => "pd", "week" => "pw", "month" => "pm", "year" => "py", _ => null };
        var url = $"{endpoint}?q={Uri.EscapeDataString(query)}&count={count}" + (freshness is null ? "" : $"&freshness={freshness}");
        var root = await GetJsonAsync(url, key, ct).ConfigureAwait(false);
        var hits = new List<SearchHit>();
        foreach (var r in root["web"]?["results"] as JsonArray ?? [])
        {
            var u = S(r?["url"]);
            if (u.Length == 0) continue;
            var snippet = Clean(S(r?["description"]));
            if (r?["extra_snippets"] is JsonArray extra && extra.Count > 0 && snippet.Length < 120)
                snippet = (snippet + " " + Clean(S(extra[0]))).Trim();
            hits.Add(new SearchHit(Clean(S(r?["title"])), u, snippet, S(r?["age"]) is { Length: > 0 } age ? age : null));
            if (hits.Count == count) break;
        }
        return hits;
    }

    private async Task<JsonObject> GetJsonAsync(string url, string? braveKey, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Accept.ParseAdd("application/json");
        if (braveKey is not null) req.Headers.TryAddWithoutValidation("X-Subscription-Token", braveKey);
        using var res = await http.SendAsync(req, timeout.Token).ConfigureAwait(false);
        var body = await res.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
        if (!res.IsSuccessStatusCode)
        {
            var snippet = Clean(body.Length > 200 ? body[..200] : body);
            throw new HttpRequestException($"HTTP {(int)res.StatusCode}{(snippet.Length > 0 ? ": " + snippet : "")}");
        }
        return JsonNode.Parse(body) as JsonObject ?? throw new InvalidOperationException("the response is not a JSON object");
    }

    private static string S(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";

    /// <summary>Snippets may carry markup (Brave wraps matches in &lt;strong&gt;) and entities.</summary>
    private static string Clean(string s) => Spaces().Replace(WebUtility.HtmlDecode(Tags().Replace(s, "")), " ").Trim();

    [GeneratedRegex("<[^>]{1,200}>")]
    private static partial Regex Tags();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
