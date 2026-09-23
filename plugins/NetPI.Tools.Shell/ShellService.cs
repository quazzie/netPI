using System.Text;

namespace NetPI.Tools.Shell;

/// <summary>Runs bash/pwsh commands (foreground or background) and formats results for the model.</summary>
public sealed class ShellService(ProcessRegistry registry, ISettings? settings, string? tempDir = null)
{
    public const int DefaultTimeoutSeconds = 120;
    public const int MaxTimeoutSeconds = 1800;
    /// <summary>How long a background start waits to catch immediate failures.</summary>
    public static TimeSpan BackgroundStartupWait { get; set; } = TimeSpan.FromMilliseconds(500);

    public ProcessRegistry Registry { get; } = registry;
    public ISettings? Settings { get; } = settings;
    public string TempDir { get; } = tempDir ?? Path.Combine(Path.GetTempPath(), "netpi");

    /// <summary>Delete spill files older than two days (best effort).</summary>
    public void CleanupTempFiles()
    {
        try
        {
            if (!Directory.Exists(TempDir)) return;
            foreach (var f in Directory.EnumerateFiles(TempDir))
            {
                var name = Path.GetFileName(f);
                if (!(name.StartsWith("bash-proc_") || name.StartsWith("pwsh-proc_") || name.StartsWith("cmd-"))) continue;
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(f) > TimeSpan.FromDays(2)) File.Delete(f);
            }
        }
        catch { }
    }

    /// <summary>Timeout in seconds from args (seconds; values above 3600 are taken as milliseconds) or settings.</summary>
    internal int? ResolveTimeout(ToolArgs args, bool background)
    {
        double? secs = null;
        if (args.Double("timeout_ms", "timeoutMs", "timeout_millis") is { } ms) secs = ms / 1000.0;
        else if (args.Double("timeout", "timeout_seconds", "timeoutSeconds", "timeoutSec", "timeout_s") is { } t) secs = t > 3600 ? t / 1000.0 : t;
        if (secs is null)
        {
            if (background) return null;
            secs = Settings.SafeGet("shell.timeoutSeconds", DefaultTimeoutSeconds);
        }
        if (secs <= 0) return background ? null : Settings.SafeGet("shell.timeoutSeconds", DefaultTimeoutSeconds);
        return (int)Math.Clamp(Math.Ceiling(secs.Value), 1, MaxTimeoutSeconds);
    }

    public (LaunchSpec? Spec, string? Error) BuildSpec(string shell, string command)
    {
        if (shell == "pwsh")
        {
            var pwsh = ShellLocator.FindPwsh(Settings, out _);
            if (pwsh is null)
                return (null, "PowerShell was not found (looked for pwsh on PATH and in the default PowerShell 7 locations" +
                    (OperatingSystem.IsWindows() ? ", and Windows PowerShell" : "") +
                    "). Install PowerShell 7 or set the shell.pwshPath setting — or use the bash tool instead.");
            return (ShellLaunch.Pwsh(pwsh, command, TempDir), null);
        }
        var bash = ShellLocator.FindBash(Settings);
        if (bash is null)
            return (null, OperatingSystem.IsWindows()
                ? "Git Bash was not found (looked in Program Files\\Git, LocalAppData\\Programs\\Git and next to git.exe on PATH). Install Git for Windows or set the shell.bashPath setting to its bin\\bash.exe — or use the pwsh tool."
                : "bash was not found. Set the shell.bashPath setting.");
        return (ShellLaunch.Bash(bash, command, TempDir), null);
    }

    internal async Task<ToolResult> RunAsync(string shell, ToolContext ctx, ToolArgs args, CancellationToken ct)
    {
        var command = args.Str("command", "cmd", "script", "code", "commands", "input");
        if (string.IsNullOrWhiteSpace(command))
            return ToolResult.Error($"Missing required argument 'command'. Example: {{\"command\": \"{(shell == "pwsh" ? "Get-ChildItem" : "ls -la")}\"}}");

        var cwdArg = args.Str("cwd", "workdir", "working_directory", "workingDirectory", "directory", "dir");
        var cwd = string.IsNullOrWhiteSpace(cwdArg) ? ctx.Cwd : ctx.ResolvePath(cwdArg);
        if (!Directory.Exists(cwd))
            return ToolResult.Error($"Working directory does not exist: {cwd}");

        var background = args.Bool("background", "run_in_background", "runInBackground", "detach", "async") ?? false;
        var timeout = ResolveTimeout(args, background);

        var (spec, specError) = BuildSpec(shell, command);
        if (spec is null) return ToolResult.Error(specError!, new { command, shell, cwd, background });
        if (ct.IsCancellationRequested)
            return ToolResult.Error("[aborted before the command started; nothing was run]", new { command, shell, cwd, background, aborted = true });

        var id = Ids.New("proc");
        var capture = new OutputCapture(1024 * 1024, background ? null : Path.Combine(TempDir, $"{shell}-{id}.log"));
        Action<string>? live = background ? BackgroundPublisher(id) : ctx.Output;
        ManagedProcess mp;
        try
        {
            mp = ManagedProcess.Start(id, spec, command, cwd, capture, live,
                background ? TimeSpan.FromMilliseconds(250) : TimeSpan.FromMilliseconds(50),
                ctx.SessionId, ctx.AgentId, background, Registry.OnExited, Registry.Add);
        }
        catch (Exception ex)
        {
            capture.Dispose();
            if (spec.TempScript is not null) { try { File.Delete(spec.TempScript); } catch { } }
            return ToolResult.Error($"Failed to start {shell} ({spec.Executable}): {ex.Message}", new { command, shell, cwd, background });
        }

        if (background) return await StartedInBackgroundAsync(mp, timeout, ct).ConfigureAwait(false);

        var timedOut = false;
        var aborted = false;
        using (var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeout ?? DefaultTimeoutSeconds)))
        using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token))
        {
            try
            {
                await mp.Completion.WaitAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                timedOut = !ct.IsCancellationRequested;
                aborted = ct.IsCancellationRequested;
                mp.Kill(timedOut ? "timeout" : "killed");
                try { await mp.Completion.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None).ConfigureAwait(false); }
                catch (TimeoutException) { }
            }
        }
        return FormatForeground(mp, timeout ?? DefaultTimeoutSeconds, timedOut, aborted);
    }

    private Action<string>? BackgroundPublisher(string id)
    {
        var bus = Registry.Events;
        if (bus is null) return null;
        return chunk =>
        {
            try { bus.Publish(EventTypes.ProcessOutput, new { id, chunk }); } catch { }
        };
    }

    private async Task<ToolResult> StartedInBackgroundAsync(ManagedProcess mp, int? timeout, CancellationToken ct)
    {
        if (timeout is { } secs)
        {
            _ = Task.Delay(TimeSpan.FromSeconds(secs)).ContinueWith(_ => mp.Kill("timeout"), TaskScheduler.Default);
        }
        try { await Task.WhenAny(mp.Completion, Task.Delay(BackgroundStartupWait, ct)).ConfigureAwait(false); }
        catch (OperationCanceledException) { }

        var details = Details(mp, truncated: false, fullOutputPath: null);
        if (!mp.IsRunning)
        {
            var (tail, truncated, _, _) = OutputFormat.TailLines(OutputFormat.ResolveCarriageReturns(mp.Output.Snapshot()), 200, 16 * 1024);
            var sb = new StringBuilder();
            sb.Append($"Background process {mp.Id} exited immediately with code {mp.ExitCode?.ToString() ?? "?"}.");
            sb.Append(tail.Length > 0 ? "\n" + tail : "\n(no output)");
            if (truncated) sb.Append($"\n[output truncated; use process_output with id \"{mp.Id}\" for more]");
            return new ToolResult { Content = sb.ToString(), IsError = mp.ExitCode is not 0, Details = details };
        }
        var early = mp.Output.Tail(20, 4000).TrimEnd();
        var msg = new StringBuilder();
        msg.Append($"Started background process {mp.Id} (pid {mp.Pid}).");
        if (timeout is not null) msg.Append($" It will be killed after {timeout}s.");
        msg.Append($"\nUse process_output with id \"{mp.Id}\" to read its output and process_kill to stop it.");
        if (early.Length > 0) msg.Append("\nOutput so far:\n").Append(OutputFormat.ResolveCarriageReturns(early));
        return ToolResult.Ok(msg.ToString(), details);
    }

    private ToolResult FormatForeground(ManagedProcess mp, int timeoutSeconds, bool timedOut, bool aborted)
    {
        var full = OutputFormat.ResolveCarriageReturns(mp.Output.Snapshot());
        var (tail, truncated, totalLines, shownLines) = OutputFormat.TailLines(full, OutputFormat.ModelMaxLines, OutputFormat.ModelMaxBytes);
        var complete = mp.Output.Complete;
        truncated |= !complete;
        string? fullPath = null;
        if (truncated) fullPath = mp.Output.SaveFull(Path.Combine(TempDir, $"{mp.Shell}-{mp.Id}.log"));
        mp.Output.Dispose(); // closes the spill file; the in-memory tail stays readable
        mp.Output.TrimTo(ProcessRegistry.FinishedTailChars);

        var sb = new StringBuilder();
        if (truncated)
        {
            var total = complete ? $"{totalLines} lines" : PathDisplayBytes(mp.Output.TotalBytes);
            sb.Append($"[Output truncated: showing the last {shownLines} lines of {total}.");
            sb.Append(fullPath is not null ? $" Full output saved to {fullPath} (use read or grep on it).]" : "]");
            sb.Append('\n');
        }
        sb.Append(tail.Length > 0 ? tail : "(no output)");
        if (timedOut) sb.Append($"\n[timed out after {timeoutSeconds}s; the process tree was killed. Use a longer timeout or background=true for long-running commands.]");
        else if (aborted) sb.Append("\n[aborted; the process tree was killed]");
        else if (mp.ExitCode is { } code && code != 0) sb.Append($"\n[exit code {code}]");

        return new ToolResult
        {
            Content = sb.ToString(),
            IsError = timedOut || aborted,
            Details = Details(mp, truncated, fullPath, timedOut, aborted),
        };
    }

    private static string PathDisplayBytes(long bytes) => bytes < 1024 * 1024
        ? (bytes / 1024.0).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " KB"
        : (bytes / (1024.0 * 1024)).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " MB";

    private static object Details(ManagedProcess mp, bool truncated, string? fullOutputPath, bool timedOut = false, bool aborted = false) => new
    {
        command = mp.Command,
        shell = mp.Shell,
        cwd = mp.Cwd,
        exitCode = mp.IsRunning ? null : mp.ExitCode,
        durationMs = mp.IsRunning ? (long?)null : (long)mp.Elapsed.TotalMilliseconds,
        truncated,
        fullOutputPath,
        background = mp.Background,
        processId = mp.Id,
        pid = mp.Pid,
        status = mp.Status,
        timedOut = timedOut ? true : (bool?)null,
        aborted = aborted ? true : (bool?)null,
    };
}
