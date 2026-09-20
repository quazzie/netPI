using System.Text.Json;
using NetPI.Abstractions;

namespace NetPI.Retry;

/// <summary>
/// Config for the Retry plugin (PLAN §34), read from the
/// <c>plugins:retry</c> (alias) or <c>netpi.retry</c> section of config.json.
/// </summary>
internal sealed record RetryConfig(
    bool Enabled,
    int MaxAttempts,
    int BaseDelayMs,
    int MaxDelayMs)
{
    public static RetryConfig FromJson(JsonElement cfg)
    {
        bool enabled = cfg.ValueKind == JsonValueKind.Object
            && cfg.TryGetProperty("enabled", out var e) && e.ValueKind == JsonValueKind.True;
        return new RetryConfig(
            Enabled: enabled,
            MaxAttempts: GetInt(cfg, "maxAttempts", 3),
            BaseDelayMs: GetInt(cfg, "baseDelayMs", 500),
            MaxDelayMs: GetInt(cfg, "maxDelayMs", 5000));
    }

    private static int GetInt(JsonElement cfg, string name, int fallback)
        => cfg.ValueKind == JsonValueKind.Object && cfg.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number
            ? p.GetInt32() : fallback;
}

/// <summary>
/// Classifies a failed model attempt and returns a retry decision (PLAN §34).
/// Retryable: connection reset, timeout, HTTP 408/429/5xx, and temporary
/// stream interruption before completion. Non-retryable: bad request,
/// authentication, unknown model, invalid tool/schema, and other 4xx.
/// </summary>
public sealed class ModelRetryPolicy : IModelRetryPolicy
{
    private readonly RetryConfig _config;
    private readonly IPluginLogger _log;

    internal ModelRetryPolicy(RetryConfig config, IPluginLogger log)
    {
        _config = config;
        _log = log;
    }


    public bool IsEnabled => _config.Enabled;

    public RetryDecision Decide(int attempt, string error)
    {
        if (!_config.Enabled) return RetryDecision.Abort;
        if (attempt >= _config.MaxAttempts)
            return new RetryDecision(false, attempt, _config.MaxAttempts, 0);

        if (!Classify(error, out var retryable, out var category))
            return new RetryDecision(false, attempt, _config.MaxAttempts, 0);

        if (!retryable)
        {
            _log.Information($"Retry: attempt {attempt}/{_config.MaxAttempts} not retryable ({category})");
            return new RetryDecision(false, attempt, _config.MaxAttempts, 0);
        }

        // Exponential backoff: base * 2^(attempt-1), capped, with a small jitter.
        int exp = attempt - 1;
        if (exp > 16) exp = 16;
        long delay = (long)_config.BaseDelayMs * (1L << exp);
        if (delay > _config.MaxDelayMs) delay = _config.MaxDelayMs;
        int jitter = (int)(Random.Shared.NextDouble() * Math.Max(1, delay / 10));
        delay = Math.Min(_config.MaxDelayMs, delay + jitter);

        _log.Information($"Retry: attempt {attempt}/{_config.MaxAttempts} retryable ({category}), waiting {delay}ms");
        return new RetryDecision(true, attempt, _config.MaxAttempts, (int)delay);
    }

    /// <summary>
    /// Classify an error string into a retryable decision + category.
    /// Heuristic matching on common provider/network failure substrings.
    /// </summary>
    private static bool Classify(string error, out bool retryable, out string category)
    {
        var e = error?.ToLowerInvariant() ?? "";
        if (e.Length == 0) { retryable = false; category = "empty"; return false; }

        // ---- retryable ------------------------------------------------------
        if (e.Contains("429") || e.Contains("too many requests") || e.Contains("rate limit"))
        {
            retryable = true; category = "429-rate-limit"; return true;
        }
        if (e.Contains("408") || e.Contains("request timeout") || e.Contains("timed out") || e.Contains("timeout"))
        {
            retryable = true; category = "408-timeout"; return true;
        }
        if (e.Contains("500") || e.Contains("502") || e.Contains("503") || e.Contains("504"))
        {
            retryable = true; category = "5xx-server"; return true;
        }
        if (e.Contains("connection reset") || e.Contains("connection refused") || e.Contains("actively refused") || e.Contains("actively refuse") ||
            e.Contains("connection closed") || e.Contains("connection abort") || e.Contains("connection failed") ||
            e.Contains("network") || e.Contains("socket") || e.Contains("econnreset") || e.Contains("econnrefused") ||
            e.Contains("dns") || e.Contains("name resolution") ||
            e.Contains("broken pipe") || e.Contains("stream interrupted") || e.Contains("stream broken") ||
            e.Contains("early eof") || e.Contains("timed out") || e.Contains("timeout") ||
            e.Contains("no connection could be made"))

        {
            retryable = true; category = "connection"; return true;
        }

        // ---- non-retryable --------------------------------------------------
        if (e.Contains("401") || e.Contains("unauthorized") || e.Contains("authentication") ||
            e.Contains("api key") || e.Contains("invalid token"))
        {
            retryable = false; category = "auth"; return false;
        }
        if (e.Contains("404") || e.Contains("not found") || e.Contains("unknown model") ||
            e.Contains("model not found"))
        {
            retryable = false; category = "unknown-model"; return false;
        }
        if (e.Contains("400") || e.Contains("bad request") || e.Contains("invalid tool") ||
            e.Contains("invalid schema") || e.Contains("invalid request") ||
            e.Contains("unprocessable") || e.Contains("422"))
        {
            retryable = false; category = "bad-request"; return false;
        }
        if (e.Contains("403") || e.Contains("forbidden"))
        {
            retryable = false; category = "forbidden"; return false;
        }

        // Default: treat unknown as non-retryable (safe default).
        retryable = false; category = "unknown";
        return false;
    }
}

/// <summary>
/// The reloadable Retry plugin (PLAN §34). Registers the
/// <see cref="IModelRetryPolicy"/> service under id "retry".
/// </summary>
public sealed class RetryPlugin : INetPiPlugin
{
    private IModelRetryPolicy? _policy;

    public PluginInfo Info { get; } = new("netPI.Retry", "Model Request Retry", "0.1.0");

    public async ValueTask LoadAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        _policy = new ModelRetryPolicy(RetryConfig.FromJson(context.OwnConfig), context.Log);
        context.Services.Register<IModelRetryPolicy>("retry", _policy);
        context.Log.Information($"Retry policy ready (enabled={_policy.IsEnabled})");
        await ValueTask.CompletedTask;
    }

    public ValueTask StartAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

    public async ValueTask StopAsync(CancellationToken cancellationToken)
    {
        _policy = null;
        await ValueTask.CompletedTask;
    }

    public ValueTask UnloadAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
}
