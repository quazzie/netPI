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

/// <summary>One slice of a background job's captured output (PLAN §28).</summary>
public sealed record BackgroundJobOutput(
    string JobId,
    BackgroundJobState State,
    int? ExitCode,
    string Text,
    int NextOffset,
    bool Truncated);


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

    /// <summary>
    /// Read captured output from <paramref name="offset"/> (a cursor in
    /// characters, 0 = from the start). Returns the new text and the cursor
    /// position to pass next time (PLAN §28: cursor-based so the model does not
    /// re-fetch the full output).
    /// </summary>
    ValueTask<BackgroundJobOutput> GetOutputAsync(
        string jobId, int offset = 0, CancellationToken cancellationToken = default);

}
