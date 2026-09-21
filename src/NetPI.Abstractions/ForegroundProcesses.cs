namespace NetPI.Abstractions;

/// <summary>
/// astra-1 H: a lightweight snapshot of a FOREGROUND shell process run by the
/// Tools plugin (bash/powershell tool call). This is the only process-lifecycle
/// information the Tools plugin exposes — it tracks only processes IT started
/// (no enumeration of unrelated operating-system processes). The Activity
/// plugin consumes <see cref="IForegroundProcessTracker"/> to build its
/// "Processes" section (command + workdir + shell/PID + state + elapsed).
/// </summary>
public sealed record ForegroundProcessInfo(
    /// <summary>The shell that ran the command, e.g. "bash" / "powershell".</summary>
    string ToolShellId,
    /// <summary>The command line the shell ran.</summary>
    string Command,
    /// <summary>The working directory the shell ran in (the session workspace, or an override).</summary>
    string? WorkingDirectory,
    /// <summary>The session that ran the command (null when unknown).</summary>
    string? SessionId,
    /// <summary>The operating-system process id, once the process has started.</summary>
    int ProcessId,
    /// <summary>When the process started (clock at <c>Started</c>).</summary>
    DateTimeOffset StartedAt,
    /// <summary>Exit code once the process has finished; <c>null</c> while running.</summary>
    int? ExitCode,
    /// <summary>When the process finished; <c>null</c> while running.</summary>
    DateTimeOffset? ExitedAt)
{
    /// <summary>True while the process has not yet reported an exit.</summary>
    public bool IsRunning => ExitedAt is null;
}

/// <summary>
/// astra-1 H: lightweight lifecycle information for the foreground shell
/// processes the Tools plugin is currently running (or just finished). Registered
/// by the Tools plugin under id <c>"foreground-processes"</c>; consumed by the
/// Activity plugin. Only processes the Tools plugin itself started appear — the
/// view never enumerates unrelated OS processes.
/// </summary>
public interface IForegroundProcessTracker
{
    /// <summary>
    /// Record a foreground process as started. Called by the shell tool right
    /// after <c>Process.Start</c>. A matching <see cref="Finished"/> (or the
    /// process ending) is required to leave it as running.
    /// </summary>
    void Started(string toolShellId, string command, string? workingDirectory, string? sessionId, int processId);

    /// <summary>
    /// Record a started process as finished. <paramref name="exitCode"/> is
    /// <c>null</c> when the code is unavailable (e.g. the process was killed and
    /// never reported a code). A no-op for an unknown / already-finished id.
    /// </summary>
    void Finished(int processId, int? exitCode);

    /// <summary>All foreground processes currently running, oldest first.</summary>
    IReadOnlyList<ForegroundProcessInfo> Running();

    /// <summary>The <paramref name="max"/> most recently finished processes, newest first.</summary>
    IReadOnlyList<ForegroundProcessInfo> Recent(int max = 50);
}
