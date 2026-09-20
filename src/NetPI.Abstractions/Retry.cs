namespace NetPI.Abstractions;

/// <summary>
/// One retry-policy decision (PLAN §34). The Retry plugin owns the policy;
/// the agent consumes this decision and never hard-codes status-code logic.
/// </summary>
public sealed record RetryDecision(
    bool ShouldRetry,
    int Attempt,
    int MaxAttempts,
    int DelayMs)
{
    /// <summary>A definitive "do not retry" decision.</summary>
    public static RetryDecision Abort { get; } = new(false, 0, 0, 0);
}

/// <summary>
/// Retry policy around model requests (PLAN §34). Implemented by the Retry
/// plugin and resolved by the agent. Given the current attempt number and the
/// error string from the failed <see cref="ModelEvent"/> / provider failure,
/// returns a decision: retry (with a backoff delay) or abort.
/// </summary>
public interface IModelRetryPolicy
{
    /// <summary>True when a retry policy is configured and active.</summary>
    bool IsEnabled { get; }

    /// <summary>
    /// Classify one failed model attempt. <paramref name="attempt"/> is the
    /// 1-based count of attempts already made (so the first failure is 1).
    /// </summary>
    RetryDecision Decide(int attempt, string error);
}
