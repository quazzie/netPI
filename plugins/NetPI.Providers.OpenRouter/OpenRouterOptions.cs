using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace NetPI.Providers.OpenRouter;

/// <summary>
/// Snapshot of <c>providers.openrouter</c>, read on every call so edits apply live: apiKey (fallback env
/// OPENROUTER_API_KEY; "env:NAME" / "$NAME" read an environment variable), baseUrl, enabled, include [model ids or
/// globs; default: every model that supports tool calls], models.&lt;id&gt; { displayName, contextWindow,
/// maxOutputTokens, hidden }, maxOutputTokens (cap per request), provider { routing preferences, sent as is },
/// promptCaching, replayReasoning, parseThinkTags, headers {k:v}, dumpFailedRequests, modelsCacheSeconds.
/// </summary>
internal sealed class OpenRouterOptions
{
    public const string DefaultBaseUrl = "https://openrouter.ai/api";
    public const string KeyEnvironmentVariable = "OPENROUTER_API_KEY";

    public string BaseUrl { get; init; } = DefaultBaseUrl;
    public string? ApiKey { get; init; }
    public bool Enabled { get; init; } = true;
    /// <summary>Model ids or globs (<c>*</c>) to offer. Empty = every model that supports tool calls.</summary>
    public List<string> Include { get; init; } = [];
    public JsonObject? Models { get; init; }
    /// <summary>Upper bound for <c>max_tokens</c> (catalogs report up to 500k+; reasoning counts against it).</summary>
    public int MaxOutputTokens { get; init; } = 32768;
    public JsonObject? Provider { get; init; }
    public bool PromptCaching { get; init; } = true;
    public bool ReplayReasoning { get; init; } = true;
    public bool ParseThinkTags { get; init; } = true;
    public bool DumpFailedRequests { get; init; } = true;
    public List<KeyValuePair<string, string>> Headers { get; init; } = [];
    public int ModelsCacheSeconds { get; init; } = 600;

    /// <summary>Base URL without a trailing "/" or "/v1".</summary>
    public string Root
    {
        get
        {
            var b = BaseUrl.Trim().TrimEnd('/');
            if (b.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)) b = b[..^3];
            return b;
        }
    }

    /// <summary>What the model list depends on (cache key).</summary>
    public string Fingerprint => $"{Root}|{ApiKey?.GetHashCode()}|{string.Join(",", Include)}|{Models?.ToJsonString()}";

    public static OpenRouterOptions Parse(JsonObject? o)
    {
        var headers = new List<KeyValuePair<string, string>>();
        if (o.Obj("headers") is { } h)
            foreach (var (k, v) in h)
                if (v is JsonValue jv && jv.TryGetValue<string>(out var s)) headers.Add(new(k, s));
        var include = new List<string>();
        if (o.Arr("include") is { } inc)
            foreach (var x in inc)
                if (x is JsonValue jv && jv.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s)) include.Add(s.Trim());

        return new OpenRouterOptions
        {
            BaseUrl = o.Str("baseUrl") is { Length: > 0 } url ? url : DefaultBaseUrl,
            ApiKey = SettingsExtensions.ResolveSecret(o.Str("apiKey")) ?? SettingsExtensions.ResolveSecret("env:" + KeyEnvironmentVariable),
            Enabled = o.Bool("enabled", true),
            Include = include,
            Models = o.Obj("models"),
            MaxOutputTokens = o.Int("maxOutputTokens") is > 0 and var m ? m : 32768,
            Provider = o.Obj("provider"),
            PromptCaching = o.Bool("promptCaching", true),
            ReplayReasoning = o.Bool("replayReasoning", true),
            ParseThinkTags = o.Bool("parseThinkTags", true),
            DumpFailedRequests = o.Bool("dumpFailedRequests", true),
            Headers = headers,
            ModelsCacheSeconds = o.Int("modelsCacheSeconds") is >= 0 and var c ? c : 600,
        };
    }

    /// <summary>Per-model override object (model ids contain slashes and dots, so they are looked up directly).</summary>
    public JsonObject? ModelOverride(string modelId) => Models.Obj(modelId);

    /// <summary>True when <see cref="Include"/> is empty or one of its ids/globs matches.</summary>
    public bool IsIncluded(string modelId) => Include.Count == 0 || Include.Any(p => Glob(p).IsMatch(modelId));

    /// <summary>Listed by its exact id (then it is offered even without tool support).</summary>
    public bool IsListedExactly(string modelId) => Include.Any(p => string.Equals(p, modelId, StringComparison.OrdinalIgnoreCase));

    private static Regex Glob(string pattern) =>
        new("^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}
