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

/// <summary>astra-1 H: ownership captured when a background process STARTS.
/// A later project switch must not relabel an existing process — these are
/// frozen at start. All optional: a tool that doesn't know a dimension leaves
/// it null (the Activity view shows "—").</summary>
public sealed record JobOwnership(string? SessionId, string? RunId, string? ProjectId);

/// <summary>Summary of a background job. Owned by the BackgroundTasks plugin.</summary>
/// <param name="WorkingDirectory">The ORIGINAL working directory captured at start
/// (astra-1 H) — a later project switch never relabels an existing process.</param>
public sealed record BackgroundJobInfo(
    string JobId,
    string? ShellId,
    string Command,
    BackgroundJobState State,
    DateTimeOffset StartedAt,
    DateTimeOffset? ExitedAt,
    int? ExitCode,
    string? OutputPath,
    /// <summary>astra-1 H: the session that started this job (ownership frozen at start).</summary>
    string? SessionId = null,
    /// <summary>astra-1 H: the run that started this job, if known at start.</summary>
    string? RunId = null,
    /// <summary>astra-1 H: the project active when this job started, if known at start.</summary>
    string? ProjectId = null,
    /// <summary>astra-1 H: the original working directory (frozen at start).</summary>
    string? WorkingDirectory = null);

/// <summary>One slice of a background job's captured output (PLAN §28).</summary>
public sealed record BackgroundJobOutput(
    string JobId,
    BackgroundJobState State,
    int? ExitCode,
    string Text,
    int NextOffset,
    bool Truncated);

/// <summary>Published when a background job starts or reaches a terminal state.</summary>
public sealed record BackgroundJobChangedEvent(BackgroundJobInfo Job);


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
        /// <summary>
        /// astra-1 H: ownership captured when the process starts (session /
        /// run / project). Optional — a caller that doesn't know a dimension
        /// leaves it null; a later project switch never relabels the process.
        /// </summary>
        JobOwnership? ownership = null,
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
