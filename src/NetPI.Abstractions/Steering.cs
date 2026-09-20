using System.Text.Json;

namespace NetPI.Abstractions;

/// <summary>
/// A user steering message waiting to be injected into an agent run (PLAN §12).
/// </summary>
public sealed record QueuedUserMessage(string Text);

/// <summary>
/// Per-session steering queues (PLAN §12): each active session owns its own
/// queue, and a steering message never cancels the running tool batch — it is
/// appended after the batch completes and seen by the next model call.
/// </summary>
public interface ISteeringQueue
{
    /// <summary>
    /// Add a steering message for <paramref name="sessionId"/> (or the active
    /// run when null). If no run is active it is held until the next run begins.
    /// </summary>
    ValueTask EnqueueAsync(string text, string? sessionId = null, CancellationToken cancellationToken = default);

    /// <summary>Number of pending steering messages for the session (0 when null).</summary>
    int PendingCount(string? sessionId = null);
}

/// <summary>
/// Resolves a shell command string to a concrete executable + arguments
/// without owning the process (PLAN §26). Owned by shell plugins.
/// </summary>
public interface IShellCommandResolver
{
    /// <summary>Shell identity, e.g. "bash", "powershell", "sh".</summary>
    string ShellId { get; }

    ValueTask<ResolvedCommand> ResolveAsync(string command, string workingDirectory, CancellationToken ct);
}

/// <summary>Plain-data result of shell command resolution. No process types cross the boundary.</summary>
public sealed record ResolvedCommand(
    string FileName,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string>? Environment)
{
    public override string ToString() => $"{FileName} {string.Join(' ', Arguments)}";
}
