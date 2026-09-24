using System.Text.Json.Nodes;

namespace NetPI.Providers.AiProxy;

public enum OpenAiTransport { Responses, Chat }

/// <summary>Defaults for one OpenAI-compatible endpoint (used when the settings object omits a key).</summary>
public sealed class ProviderDefaults
{
    public string BaseUrl { get; init; } = "http://127.0.0.1:8090";
    public bool IsLocal { get; init; } = true;
    public OpenAiTransport Transport { get; init; } = OpenAiTransport.Responses;
    public int DefaultMaxOutputTokens { get; init; } = 16384;
    public int ModelsCacheSeconds { get; init; } = 10;
}

/// <summary>Effective per-request options (provider settings + per-model override <c>models.&lt;id&gt;</c>).</summary>
internal sealed record ModelOptions(
    OpenAiTransport Transport,
    bool ReplayReasoning,
    bool ParseThinkTags,
    int DefaultMaxOutputTokens,
    string? ReasoningSummary,
    bool IncludeEncryptedReasoning = false);

/// <summary>
/// A snapshot of an endpoint's settings object, parsed tolerantly. Settings are read at call time so edits
/// apply live. Keys: baseUrl, apiKey ("env:NAME" / "$NAME" read an environment variable), transport
/// (responses|chat), replayReasoning, defaultMaxOutputTokens, parseThinkTags, modelsCacheSeconds, enabled,
/// local, name, headers {k:v}, reasoningSummary, models.&lt;modelId&gt; { transport, replayReasoning, parseThinkTags,
/// maxOutputTokens, contextWindow, concurrency, displayName, hidden }.
/// </summary>
internal sealed class ProviderOptions
{
    public required string BaseUrl { get; init; }
    public string? ApiKey { get; init; }
    public OpenAiTransport Transport { get; init; }
    /// <summary>Explicit setting; null = transport default (Responses: replay reasoning items, Chat: don't).</summary>
    public bool? ReplayReasoning { get; init; }
    /// <summary>Write the request body of failed calls to logs/failed-requests (for bug reports).</summary>
    public bool DumpFailedRequests { get; init; } = true;
    public bool IncludeEncryptedReasoning { get; init; }
    public int DefaultMaxOutputTokens { get; init; }
    public bool ParseThinkTags { get; init; } = true;
    public int ModelsCacheSeconds { get; init; }
    public bool Enabled { get; init; } = true;
    public bool IsLocal { get; init; }
    public string? Name { get; init; }
    public string? ReasoningSummary { get; init; }
    public List<KeyValuePair<string, string>> Headers { get; init; } = [];
    public JsonObject? Models { get; init; }

    /// <summary>Base URL without trailing "/" or "/v1".</summary>
    public string Root
    {
        get
        {
            var b = BaseUrl.Trim().TrimEnd('/');
            if (b.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)) b = b[..^3];
            return b;
        }
    }

    /// <summary>Identity of the endpoint (cache key for the model list).</summary>
    public string Fingerprint => $"{Root}|{ApiKey?.GetHashCode()}|{string.Join(",", Headers.Select(h => h.Key + "=" + h.Value))}";

    private static bool? BoolOrNull(JsonObject? o, string key) =>
        o is not null && o.TryGetPropertyValue(key, out var v) && v is JsonValue jv
            ? (jv.TryGetValue<bool>(out var b) ? b : jv.TryGetValue<string>(out var s) && bool.TryParse(s, out var sb) ? sb : null)
            : null;

    public static ProviderOptions Parse(JsonObject? o, ProviderDefaults d)
    {
        var headers = new List<KeyValuePair<string, string>>();
        if (o.Obj("headers") is { } h)
            foreach (var (k, v) in h)
                if (v is JsonValue jv && jv.TryGetValue<string>(out var s)) headers.Add(new(k, s));

        return new ProviderOptions
        {
            BaseUrl = o.Str("baseUrl") is { Length: > 0 } url ? url : d.BaseUrl,
            ApiKey = ResolveSecret(o.Str("apiKey")),
            Transport = ParseTransport(o.Str("transport")) ?? d.Transport,
            ReplayReasoning = BoolOrNull(o, "replayReasoning"),
            DumpFailedRequests = o.Bool("dumpFailedRequests", true),
            IncludeEncryptedReasoning = o.Bool("includeEncryptedReasoning", false),
            DefaultMaxOutputTokens = o.Int("defaultMaxOutputTokens") is > 0 and var m ? m : d.DefaultMaxOutputTokens,
            ParseThinkTags = o.Bool("parseThinkTags", true),
            ModelsCacheSeconds = o.Int("modelsCacheSeconds") is >= 0 and var c ? c : d.ModelsCacheSeconds,
            Enabled = o.Bool("enabled", true),
            IsLocal = o.Bool("local", d.IsLocal),
            Name = o.Str("name"),
            ReasoningSummary = o.Str("reasoningSummary"),
            Headers = headers,
            Models = o.Obj("models"),
        };
    }

    /// <summary>Per-model override object (model ids may contain dots, so they are looked up directly).</summary>
    public JsonObject? ModelOverride(string modelId) => Models.Obj(modelId);

    public ModelOptions ForModel(string modelId)
    {
        var m = ModelOverride(modelId);
        var transport = ParseTransport(m.Str("transport")) ?? Transport;
        return new ModelOptions(
            transport,
            // Standard stateless Responses usage appends the previous response's output items (reasoning, message,
            // function calls) to the next input; chat templates differ on reasoning replay, so chat defaults to off.
            BoolOrNull(m, "replayReasoning") ?? ReplayReasoning ?? transport == OpenAiTransport.Responses,
            m.Bool("parseThinkTags", ParseThinkTags),
            m.Int("maxOutputTokens") is > 0 and var mx ? mx : DefaultMaxOutputTokens,
            m.Str("reasoningSummary") ?? ReasoningSummary,
            m.Bool("includeEncryptedReasoning", IncludeEncryptedReasoning));
    }

    public static OpenAiTransport? ParseTransport(string? s) => s?.Trim().ToLowerInvariant() switch
    {
        "responses" or "response" => OpenAiTransport.Responses,
        "chat" or "chat_completions" or "chatcompletions" or "completions" => OpenAiTransport.Chat,
        _ => null,
    };

    /// <summary>"env:NAME" or "$NAME" reads an environment variable; anything else is the literal secret.</summary>
    public static string? ResolveSecret(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        value = value.Trim();
        string? env = value.StartsWith("env:", StringComparison.OrdinalIgnoreCase) ? value[4..]
            : value.StartsWith('$') && value.Length > 1 ? value[1..] : null;
        if (env is null) return value;
        var resolved = Environment.GetEnvironmentVariable(env.Trim());
        return string.IsNullOrWhiteSpace(resolved) ? null : resolved.Trim();
    }
}

/// <summary>Maps a requested reasoning effort onto what the model supports.</summary>
internal static class EffortMap
{
    private static readonly string[] Scale = ["none", "minimal", "low", "medium", "high", "xhigh", "max"];

    /// <summary>
    /// Null when nothing should be sent (no effort requested, "default"/"auto", or reasoning explicitly unsupported).
    /// When the catalog lists efforts and the requested one is missing, the nearest supported value is used
    /// (ties prefer the higher effort).
    /// </summary>
    public static string? Resolve(string? requested, ReasoningInfo? info)
    {
        if (string.IsNullOrWhiteSpace(requested)) return null;
        var r = requested.Trim().ToLowerInvariant();
        if (r is "default" or "auto" or "null") return null;
        if (info is null) return r;
        if (!info.Supported) return null;
        if (info.Efforts.Count == 0) return r;
        foreach (var e in info.Efforts)
            if (string.Equals(e, r, StringComparison.OrdinalIgnoreCase)) return e;

        var ri = Array.IndexOf(Scale, r);
        if (ri < 0) return info.Default; // unknown value: let the backend default apply
        string? best = null; var bestDist = int.MaxValue; var bestRank = -1;
        foreach (var e in info.Efforts)
        {
            var ei = Array.IndexOf(Scale, e.ToLowerInvariant());
            if (ei < 0) continue;
            var dist = Math.Abs(ei - ri);
            if (dist < bestDist || (dist == bestDist && ei > bestRank)) { best = e; bestDist = dist; bestRank = ei; }
        }
        return best;
    }
}
