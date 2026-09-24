using Microsoft.Extensions.Logging;

namespace NetPI.Retry;

/// <summary>
/// Registers <see cref="RetryMiddleware"/> as the outermost model middleware. Settings (read per call):
/// <c>retry.enabled</c>, <c>retry.maxAttempts</c>, <c>retry.baseDelayMs</c>, <c>retry.maxDelayMs</c>,
/// <c>retry.firstEventTimeoutSeconds</c>, <c>retry.stallTimeoutSeconds</c>.
/// </summary>
[NetPiPlugin("netpi.retry", Name = "Retry", Description = "Retries model calls on lost connections and stalled streams", Order = 15)]
public sealed class RetryPlugin : INetPiPlugin
{
    public Task StartAsync(IPluginContext context, CancellationToken ct)
    {
        context.Services.Register(new SettingsSection
        {
            Id = "retry", Title = "Retries", Group = "Models", Order = 60,
            Settings =
            [
                SettingInfo.Bool("retry.enabled", "Retry lost connections and stalled streams", true),
                SettingInfo.Int("retry.maxAttempts", "Attempts", 6, null, 1, 20),
                SettingInfo.Int("retry.firstEventTimeoutSeconds", "Wait for the first token", 600, "A slow prefill of a long context can take minutes.", 10, 3600, "s"),
                SettingInfo.Int("retry.stallTimeoutSeconds", "Silence between tokens", 180, null, 10, 3600, "s"),
                SettingInfo.Int("retry.maxTotalSeconds", "Give up after", 300, null, 10, 7200, "s"),
                SettingInfo.Int("retry.baseDelayMs", "First delay", 1000, "Exponential backoff with jitter.", 100, 60000, "ms"),
                SettingInfo.Int("retry.maxDelayMs", "Longest delay", 30000, null, 100, 600000, "ms"),
            ],
        });
        var settings = context.Settings;
        context.Services.Register<IModelMiddleware>(new RetryMiddleware(() => RetryOptions.From(settings), context.Logger));
        return Task.CompletedTask;
    }
}

public sealed class RetryOptions
{
    public bool Enabled { get; init; } = true;
    /// <summary>Total attempts including the first one.</summary>
    public int MaxAttempts { get; init; } = 6;
    public TimeSpan BaseDelay { get; init; } = TimeSpan.FromMilliseconds(1000);
    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromMilliseconds(30_000);
    /// <summary>Max silence before the first stream event (local prefill of huge prompts, AiProxy outage hold).</summary>
    public TimeSpan FirstEventTimeout { get; init; } = TimeSpan.FromSeconds(600);
    /// <summary>Max silence between two stream events.</summary>
    public TimeSpan StallTimeout { get; init; } = TimeSpan.FromSeconds(180);
    /// <summary>Stop retrying once this much time has passed since the first attempt (AiProxy alone may hold a
    /// request for 2 minutes per attempt when a backend is down).</summary>
    public TimeSpan MaxTotal { get; init; } = TimeSpan.FromSeconds(300);

    public static RetryOptions From(ISettings? s)
    {
        if (s is null) return new RetryOptions();
        return new RetryOptions
        {
            Enabled = Get(s, "retry.enabled", true),
            MaxAttempts = Math.Clamp(Get(s, "retry.maxAttempts", 6), 1, 100),
            BaseDelay = TimeSpan.FromMilliseconds(Math.Max(0, Get(s, "retry.baseDelayMs", 1000.0))),
            MaxDelay = TimeSpan.FromMilliseconds(Math.Max(0, Get(s, "retry.maxDelayMs", 30_000.0))),
            FirstEventTimeout = Seconds(Get(s, "retry.firstEventTimeoutSeconds", 600.0)),
            StallTimeout = Seconds(Get(s, "retry.stallTimeoutSeconds", 180.0)),
            MaxTotal = Seconds(Get(s, "retry.maxTotalSeconds", 300.0)),
        };
    }

    /// <summary>0 or negative disables the timeout.</summary>
    private static TimeSpan Seconds(double v) => v <= 0 ? Timeout.InfiniteTimeSpan : TimeSpan.FromSeconds(Math.Min(v, 86_400));

    private static T Get<T>(ISettings s, string path, T fallback)
    {
        try { return s.Get(path, fallback) ?? fallback; } catch { return fallback; }
    }
}
