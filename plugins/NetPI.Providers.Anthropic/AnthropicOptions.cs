using System.Text.Json.Nodes;

namespace NetPI.Providers.Anthropic;

public enum ThinkingMode { Budget, Adaptive, Off }

/// <summary>
/// Snapshot of <c>providers.anthropic</c>: apiKey (fallback env ANTHROPIC_API_KEY; "env:NAME"/"$NAME" supported),
/// baseUrl, thinking (budget|adaptive|off), promptCaching, defaultMaxOutputTokens, enabled, betas [..] (anthropic-beta
/// header), headers {k:v}, adaptiveEffort (send output_config.effort in adaptive mode), thinkingBudgets {low,medium,high,max},
/// dumpFailedRequests, fallbackModels [ids] (used when the model list call fails), modelsCacheSeconds.
/// </summary>
internal sealed class AnthropicOptions
{
    public const string DefaultBaseUrl = "https://api.anthropic.com";

    public string BaseUrl { get; init; } = DefaultBaseUrl;
    public string? ApiKey { get; init; }
    public ThinkingMode Thinking { get; init; } = ThinkingMode.Budget;
    public bool PromptCaching { get; init; } = true;
    public int DefaultMaxOutputTokens { get; init; } = 32000;
    public bool DumpFailedRequests { get; init; } = true;
    public bool Enabled { get; init; } = true;
    public bool AdaptiveEffort { get; init; } = true;
    public List<string> Betas { get; init; } = [];
    public List<KeyValuePair<string, string>> Headers { get; init; } = [];
    public Dictionary<string, int> Budgets { get; init; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["low"] = 2048, ["medium"] = 8192, ["high"] = 16384, ["max"] = 32000,
    };
    public List<string> FallbackModels { get; init; } = [];
    public int ModelsCacheSeconds { get; init; } = 600;

    public string Root
    {
        get
        {
            var b = BaseUrl.Trim().TrimEnd('/');
            if (b.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)) b = b[..^3];
            return b;
        }
    }

    public string Fingerprint => $"{Root}|{ApiKey?.GetHashCode()}|{string.Join(",", Betas)}";

    public static AnthropicOptions Parse(JsonObject? o)
    {
        var headers = new List<KeyValuePair<string, string>>();
        if (o.Obj("headers") is { } h)
            foreach (var (k, v) in h)
                if (v is JsonValue jv && jv.TryGetValue<string>(out var s)) headers.Add(new(k, s));

        var budgets = new AnthropicOptions().Budgets;
        if (o.Obj("thinkingBudgets") is { } tb)
            foreach (var key in budgets.Keys.ToList())
                if (tb.Int(key) is > 0 and var v) budgets[key] = v;

        return new AnthropicOptions
        {
            BaseUrl = o.Str("baseUrl") is { Length: > 0 } url ? url : DefaultBaseUrl,
            ApiKey = ResolveSecret(o.Str("apiKey")) ?? ResolveSecret("env:ANTHROPIC_API_KEY"),
            Thinking = o.Str("thinking")?.Trim().ToLowerInvariant() switch
            {
                "adaptive" => ThinkingMode.Adaptive,
                "off" or "none" or "disabled" or "false" => ThinkingMode.Off,
                _ => ThinkingMode.Budget,
            },
            PromptCaching = o.Bool("promptCaching", true),
            DefaultMaxOutputTokens = o.Int("defaultMaxOutputTokens") is > 0 and var m ? m : 32000,
            DumpFailedRequests = o.Bool("dumpFailedRequests", true),
            Enabled = o.Bool("enabled", true),
            AdaptiveEffort = o.Bool("adaptiveEffort", true),
            Betas = Strings(o.Arr("betas")),
            Headers = headers,
            Budgets = budgets,
            FallbackModels = Strings(o.Arr("fallbackModels")),
            ModelsCacheSeconds = o.Int("modelsCacheSeconds") is >= 0 and var c ? c : 600,
        };
    }

    private static List<string> Strings(JsonArray? arr)
    {
        var list = new List<string>();
        if (arr is null) return list;
        foreach (var n in arr)
            if (n is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s)) list.Add(s.Trim());
        return list;
    }

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

/// <summary>Static capability table (the models API only returns ids and display names).</summary>
internal static class ClaudeCapabilities
{
    public const int ContextWindow = 200_000;

    /// <summary>Used when the models endpoint fails but an API key exists (override with fallbackModels).</summary>
    public static readonly string[] FallbackModels =
    [
        "claude-opus-4-6", "claude-sonnet-4-6", "claude-opus-4-5", "claude-sonnet-4-5", "claude-haiku-4-5", "claude-opus-4-1",
    ];

    public static readonly string[] Efforts = ["none", "low", "medium", "high", "max"];

    public static (int ContextWindow, int MaxOutput, bool Thinking) For(string id)
    {
        var m = id.ToLowerInvariant();
        if (m.StartsWith("claude-3-5-haiku")) return (ContextWindow, 8192, false);
        if (m.StartsWith("claude-3-5-sonnet")) return (ContextWindow, 8192, false);
        if (m.StartsWith("claude-3-haiku") || m.StartsWith("claude-3-opus") || m.StartsWith("claude-3-sonnet")) return (ContextWindow, 4096, false);
        if (m.StartsWith("claude-3-7-sonnet")) return (ContextWindow, 64000, true);
        // Opus 4.0 / 4.1 are capped at 32k output.
        if (m.StartsWith("claude-opus-4-0") || m.StartsWith("claude-opus-4-1") || m.StartsWith("claude-opus-4-2025")) return (ContextWindow, 32000, true);
        if (m.StartsWith("claude-sonnet-4") || m.StartsWith("claude-opus-4")) return (ContextWindow, 64000, true);
        return (ContextWindow, 32000, true); // haiku 4.x and unknown/future models
    }

    public static ModelInfo ToModelInfo(string provider, string id, string? displayName, int? contextWindow = null, int? maxOutput = null)
    {
        var caps = For(id);
        return new ModelInfo
        {
            Provider = provider,
            Id = id,
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? Prettify(id) : displayName,
            ContextWindow = contextWindow ?? caps.ContextWindow,
            MaxOutputTokens = maxOutput ?? caps.MaxOutput,
            InputModalities = ["text", "image"],
            Reasoning = caps.Thinking ? new ReasoningInfo { Supported = true, Efforts = [.. Efforts], Default = "medium" } : null,
            Status = "available",
            IsLocal = false,
        };
    }

    /// <summary>"claude-sonnet-4-5" → "Claude Sonnet 4.5".</summary>
    public static string Prettify(string id)
    {
        var parts = id.Split('-', StringSplitOptions.RemoveEmptyEntries);
        var words = new List<string>();
        var nums = new List<string>();
        foreach (var p in parts)
        {
            if (p.Length >= 8 && p.All(char.IsDigit)) continue; // date suffix
            if (p.All(char.IsDigit)) nums.Add(p);
            else
            {
                if (nums.Count > 0) { words.Add(string.Join('.', nums)); nums.Clear(); }
                words.Add(char.ToUpperInvariant(p[0]) + p[1..]);
            }
        }
        if (nums.Count > 0) words.Add(string.Join('.', nums));
        return string.Join(' ', words);
    }
}
