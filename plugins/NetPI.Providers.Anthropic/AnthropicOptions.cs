using System.Text.Json.Nodes;

namespace NetPI.Providers.Anthropic;

/// <summary>auto: by the model's generation (see <see cref="ClaudeCapabilities.ThinkingApiOf"/>); the others as asked.</summary>
public enum ThinkingMode { Auto, Budget, Adaptive, Off }

/// <summary>
/// Snapshot of <c>providers.anthropic</c>: apiKey (fallback env ANTHROPIC_API_KEY; "env:NAME"/"$NAME" supported),
/// baseUrl, thinking (auto|budget|adaptive|off), thinkingDisplay (summarized|omitted, adaptive only), promptCaching,
/// defaultMaxOutputTokens, enabled, betas [..] (anthropic-beta header), headers {k:v}, adaptiveEffort (send
/// output_config.effort in adaptive mode), thinkingBudgets {low,medium,high,max}, dumpFailedRequests,
/// fallbackModels [ids] (used when the model list call fails), modelsCacheSeconds.
/// </summary>
internal sealed class AnthropicOptions
{
    public const string DefaultBaseUrl = "https://api.anthropic.com";

    public string BaseUrl { get; init; } = DefaultBaseUrl;
    public string? ApiKey { get; init; }
    public ThinkingMode Thinking { get; init; } = ThinkingMode.Auto;
    /// <summary><c>thinking.display</c> with adaptive thinking: "summarized" (readable thinking) or "omitted" (empty
    /// thinking blocks, the API's own default from Claude 4.7 on).</summary>
    public string ThinkingDisplay { get; init; } = "summarized";
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
            ApiKey = SettingsExtensions.ResolveSecret(o.Str("apiKey")) ?? SettingsExtensions.ResolveSecret("env:ANTHROPIC_API_KEY"),
            Thinking = o.Str("thinking")?.Trim().ToLowerInvariant() switch
            {
                "budget" => ThinkingMode.Budget,
                "adaptive" => ThinkingMode.Adaptive,
                "off" or "none" or "disabled" or "false" => ThinkingMode.Off,
                _ => ThinkingMode.Auto,
            },
            ThinkingDisplay = o.Str("thinkingDisplay")?.Trim().ToLowerInvariant() == "omitted" ? "omitted" : "summarized",
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
}

/// <summary>How a Claude generation takes its thinking configuration (the Messages API reference, 2026).</summary>
internal enum ThinkingApi
{
    /// <summary>Claude 3.7 to 4.5: <c>thinking: {type: enabled, budget_tokens}</c>.</summary>
    Budget,
    /// <summary>Opus and Sonnet 4.6: adaptive thinking (budget_tokens deprecated but accepted); efforts low to max, no xhigh.</summary>
    Adaptive,
    /// <summary>Opus 4.7 and later, Sonnet 5 and later, and every id this table does not know: adaptive thinking only
    /// (budget_tokens is refused with HTTP 400); the efforts include xhigh.</summary>
    AdaptiveXhigh,
}

/// <summary>Static capability table (the models API only returns ids and display names).</summary>
internal static class ClaudeCapabilities
{
    public const int ContextWindow = 200_000;

    /// <summary>Used when the models endpoint fails but an API key exists (override with fallbackModels).</summary>
    public static readonly string[] FallbackModels =
    [
        "claude-opus-5-5", "claude-sonnet-5-5", "claude-opus-4-6", "claude-sonnet-4-6", "claude-haiku-4-5",
    ];

    /// <summary>The efforts of a budget-thinking model and of 4.6 (whose output_config.effort has no xhigh).</summary>
    public static readonly string[] Efforts = ["none", "low", "medium", "high", "max"];
    /// <summary>The efforts from Opus 4.7 on: xhigh sits between high and max.</summary>
    public static readonly string[] EffortsXhigh = ["none", "low", "medium", "high", "xhigh", "max"];

    private static readonly string[] BudgetFamilies =
    [
        "claude-3-7-sonnet", "claude-sonnet-4-0", "claude-sonnet-4-2025", "claude-opus-4-0", "claude-opus-4-1", "claude-opus-4-2025",
        "claude-sonnet-4-5", "claude-opus-4-5", "claude-haiku-4-5",
    ];

    /// <summary>Which thinking configuration <paramref name="id"/> takes; an id this table does not know is a newer model.</summary>
    public static ThinkingApi ThinkingApiOf(string id)
    {
        var m = id.ToLowerInvariant();
        if (BudgetFamilies.Any(f => m.StartsWith(f, StringComparison.Ordinal))) return ThinkingApi.Budget;
        if (m.StartsWith("claude-opus-4-6", StringComparison.Ordinal) || m.StartsWith("claude-sonnet-4-6", StringComparison.Ordinal)) return ThinkingApi.Adaptive;
        return ThinkingApi.AdaptiveXhigh;
    }

    public static string[] EffortsFor(ThinkingApi api) => api == ThinkingApi.AdaptiveXhigh ? EffortsXhigh : Efforts;

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
            Reasoning = caps.Thinking ? new ReasoningInfo { Supported = true, Efforts = [.. EffortsFor(ThinkingApiOf(id))], Default = "medium" } : null,
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
