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
        };
    }

    /// <summary>0 or negative disables the timeout.</summary>
    private static TimeSpan Seconds(double v) => v <= 0 ? Timeout.InfiniteTimeSpan : TimeSpan.FromSeconds(Math.Min(v, 86_400));

    private static T Get<T>(ISettings s, string path, T fallback)
    {
        try { return s.Get(path, fallback) ?? fallback; } catch { return fallback; }
    }
}
