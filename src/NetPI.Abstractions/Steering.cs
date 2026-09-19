using System.Text.Json;

namespace NetPI.Abstractions;

/// <summary>
/// Queue of user steering messages injected into a running agent run (PLAN §12).
/// Owned by the agent runtime; other plugins (Web, UI) publish steering through it.
/// </summary>
public interface ISteeringQueue
{
    /// <summary>
    /// Add a steering message for the active run. If no run is active it is
    /// held until the next run begins.
    /// </summary>
    ValueTask EnqueueAsync(string text, CancellationToken cancellationToken = default);

    /// <summary>Poll and remove the next pending steering message, or null when empty.</summary>
    ValueTask<string?> TryDequeueAsync(CancellationToken cancellationToken = default);

    /// <summary>Number of pending steering messages.</summary>
    int PendingCount { get; }
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
