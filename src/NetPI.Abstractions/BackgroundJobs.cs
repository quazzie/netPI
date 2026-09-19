using System.Text.Json;

namespace NetPI.Abstractions;

/// <summary>State of a background job (PLAN §27).</summary>
public enum BackgroundJobState
{
    Running = 0,
    Succeeded = 1,
    Failed = 2,
    Killed = 3,
}

/// <summary>Summary of a background job. Owned by the BackgroundTasks plugin.</summary>
public sealed record BackgroundJobInfo(
    string JobId,
    string? ShellId,
    string Command,
    BackgroundJobState State,
    DateTimeOffset StartedAt,
    DateTimeOffset? ExitedAt,
    int? ExitCode,
    string? OutputPath);

/// <summary>
/// Manages long-running background processes independent of foreground shell
/// execution (PLAN §27). Owned by the BackgroundTasks plugin.
/// </summary>
public interface IBackgroundJobManager
{
    ValueTask<BackgroundJobInfo> StartAsync(
        string shellId,
        string command,
        string workingDirectory,
        JsonElement? options,
        CancellationToken cancellationToken = default);

    ValueTask<BackgroundJobInfo?> GetAsync(string jobId, CancellationToken cancellationToken = default);
    ValueTask<IReadOnlyList<BackgroundJobInfo>> ListAsync(CancellationToken cancellationToken = default);
    ValueTask<bool> KillAsync(string jobId, CancellationToken cancellationToken = default);
}
