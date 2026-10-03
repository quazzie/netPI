using System.Globalization;
using System.Text;
using System.Text.Json;

namespace NetPI.Tools.Shell;

public abstract class ShellToolBase : IAgentTool
{
    // Guidelines the shell tools share: every tool a line concerns carries it, and the prompt lists it once.
    internal const string FreshShell =
        "Every shell call is fresh and non-interactive: cd and variables don't persist (use cwd or `cd dir && …`), pass -y style flags, and never start editors, pagers, REPLs or prompts.";
    internal const string BackgroundProcesses =
        "Check background processes with process (action output) instead of sleeping, and block on one you already " +
        "backgrounded with process (action wait) — for a command of a known duration a single bash/pwsh call with a " +
        "matching timeout is the faster path; kill the ones you no longer need; don't append `&` to a command.";

    public abstract ToolDefinition Definition { get; }

    internal abstract Task<ToolResult> RunAsync(ToolContext ctx, ToolArgs args, CancellationToken ct);

    // No catch of its own: the run turns whatever a tool throws into that tool's error result ("Tool 'bash' failed: …",
    // logged), so a wrapper here would only say it a second time, in another wording. The file tools keep one because
    // their I/O errors get their own lines.
    public Task<ToolResult> ExecuteAsync(ToolContext context, JsonElement args, CancellationToken ct) =>
        RunAsync(context, new ToolArgs(args), ct);

    internal static string FormatDuration(TimeSpan t) =>
        t.TotalSeconds < 1 ? $"{t.TotalMilliseconds:0}ms"
        : t.TotalMinutes < 1 ? string.Create(CultureInfo.InvariantCulture, $"{t.TotalSeconds:0.#}s")
        : t.TotalHours < 1 ? $"{(int)t.TotalMinutes}m{t.Seconds:00}s"
        : $"{(int)t.TotalHours}h{t.Minutes:00}m";

    internal static string Describe(ManagedProcess p)
    {
        var state = p.Status switch
        {
            "running" => "running",
            "exited" => $"exited {p.ExitCode?.ToString() ?? "?"}",
            _ => p.Status,
        };
        return $"{p.Id}  {state}  pid {p.Pid}  {FormatDuration(p.Elapsed)}  {(p.Background ? "bg" : "fg")}  {p.Shell}: {OneLine(p.Command, 120)}";
    }

    internal static string OneLine(string s, int max)
    {
        var line = s.Replace("\r", "").Replace('\n', ' ').Trim();
        return line.Length > max ? line[..max] + "…" : line;
    }
}

/// <summary>bash (Git Bash on Windows) and pwsh tools.</summary>
public sealed class ShellTool : ShellToolBase
{
    private readonly ShellService _service;
    private readonly string _shell;

    public ShellTool(string shell, ShellService service)
    {
        _shell = shell;
        _service = service;
        Definition = shell == "pwsh" ? PwshDefinition() : BashDefinition();
    }

    public override ToolDefinition Definition { get; }

    internal override Task<ToolResult> RunAsync(ToolContext ctx, ToolArgs args, CancellationToken ct) =>
        _service.RunAsync(_shell, ctx, args, ct);

    private static System.Text.Json.Nodes.JsonObject Parameters(string commandHelp) => Schema.Object(
        ("command", Schema.Str(commandHelp), true),
        ("timeout", Schema.Int($"Seconds (default {ShellService.DefaultTimeoutSeconds}, max {ShellService.MaxTimeoutSeconds}; timeout_ms takes milliseconds)"), false),
        ("background", Schema.Bool("Returns a process id at once"), false),
        ("cwd", Schema.Str(""), false));

    private static readonly string ShellHelp =
        $"stdout and stderr are merged; the output's tail is returned, with a note of the exit code when it is not 0. timeout: " +
        $"seconds (default {ShellService.DefaultTimeoutSeconds} s, max {ShellService.MaxTimeoutSeconds}, and a value over the max is clamped to it — " +
        "timeout_ms is the entry point for milliseconds); the whole process tree is killed " +
        "on timeout. background: starts the command and returns a process id at once (servers, watchers, long builds); see " +
        "process (list, output, wait, kill). cwd: default the session working directory.";

    private static ToolDefinition BashDefinition()
    {
        var win = OperatingSystem.IsWindows();
        var guidelines = new List<string> { FreshShell, BackgroundProcesses };
        if (win)
            guidelines.Insert(0, "bash is Git Bash: use forward slashes (C:/x or /c/x); cmd built-ins (dir, copy) don't work, Windows programs (dotnet, npm, git) do.");
        return new ToolDefinition
        {
            Name = "bash",
            Label = "Bash",
            Category = "shell",
            ReadOnly = false,
            SummaryArg = "command",
            Description = (win ? "Run a command in Git Bash (bash -c)." : "Run a bash command (bash -c).") + " Use background=true for long-running processes.",
            Help = ShellHelp,
            Parameters = Parameters(""),
            PromptGuidelines = guidelines,
        };
    }

    private static ToolDefinition PwshDefinition() => new()
    {
        Name = "pwsh",
        Label = "PowerShell",
        Category = "shell",
        ReadOnly = false,
        SummaryArg = "command",
        Description = "Run a PowerShell 7 (pwsh) script, non-interactive, without profile. Use background=true for long-running processes.",
        Help = ShellHelp,
        Parameters = Parameters(""),
        PromptGuidelines =
        [
            "Use pwsh for Windows-specific work (registry, services, cmdlets, .NET APIs); prefer bash otherwise.",
            FreshShell,
            BackgroundProcesses,
        ],
    };
}

/// <summary><c>process</c>: the background processes and recent shell commands: list, output (a tail), wait and kill.</summary>
public sealed class ProcessTool(ProcessRegistry registry) : ShellToolBase, IReadOnlyCalls
{
    public const int DefaultTail = 200;
    /// <summary>How long <c>wait</c> blocks before it reports the job as still running: the shell tools' own default.</summary>
    public const int DefaultWaitSeconds = ShellService.DefaultTimeoutSeconds;
    public const int MaxWaitSeconds = ShellService.MaxTimeoutSeconds;

    public override ToolDefinition Definition { get; } = new()
    {
        Name = "process",
        Label = "Processes",
        Category = "shell",
        SummaryArg = "action",
        Description = "Background processes and recent shell commands of your session (and its subagents): list, output {id, tail?} (the latest output), " +
            "wait {id, timeout?} (blocks until the process exits, then returns its exit code and output) or kill {id}. " +
            "list all:true shows every session's processes (read-only).",
        Help =
            "list: every background process and recent command with its status (running / exited N / killed / timeout) — " +
            "this session's and its subagents', not other chats'; all:true shows every session's, read-only, with the session on each line. " +
            $"output: the last lines of a process's output (tail, default {DefaultTail}, max {ToolOutput.ModelMaxLines}) and " +
            "whether it still runs. " +
            $"wait: blocks until the process exits, then returns its exit code, how long it ran and the last {DefaultTail} lines of " +
            "output — one call, no follow-up output. It returns the moment the process exits, so a second wait costs nothing. " +
            $"On timeout (default {DefaultWaitSeconds} s, max {MaxWaitSeconds}) it reports the process as still running, for how " +
            "long, and its last lines, which is what tells slow from hung. Use it for a job you backgrounded and now need the " +
            "result of; for a command of a known duration one blocking bash/pwsh call with a matching timeout is faster. " +
            "kill: ends the process and all of its child processes. ids (proc_…) come from bash/pwsh with background=true, or from list.",
        Parameters = Schema.Object(
            ("action", Schema.Str("", "list", "output", "wait", "kill"), true),
            ("id", Schema.Str(""), false),
            ("all", Schema.Bool("list only: every session's processes, not just this session's (read-only)"), false),
            ("timeout", Schema.Int($"Seconds to block (default {DefaultWaitSeconds}, max {MaxWaitSeconds}); wait only"), false),
            ("tail", Schema.Int(""), false)),
        PromptGuidelines = [BackgroundProcesses],
    };

    public bool IsReadOnly(JsonElement args) => Action(new ToolArgs(args)) is "list" or "output" or "wait";

    private static string Action(ToolArgs args) => (args.Str("action", "verb", "command") ?? "list").Trim().ToLowerInvariant() switch
    {
        "ls" or "all" => "list",
        "log" or "logs" or "tail" or "read" => "output",
        "await" or "join" or "block" => "wait",
        "stop" or "terminate" => "kill",
        var a => a,
    };

    internal override Task<ToolResult> RunAsync(ToolContext ctx, ToolArgs args, CancellationToken ct) => Action(args) switch
    {
        "list" => Task.FromResult(List(ctx, args)),
        "output" => Task.FromResult(Output(ctx, args)),
        "wait" => WaitAsync(ctx, args, ct),
        "kill" => KillAsync(ctx, args, ct),
        var a => Task.FromResult(ToolResult.Error($"Unknown action \"{a}\": use list, output, wait or kill.")),
    };

    private ToolResult List(ToolContext ctx, ToolArgs args)
    {
        var all = args.Bool("all") ?? false;
        var own = all ? null : VisibleSessions(ctx);
        var list = registry.List().Where(p => own is null || own.Contains(p.SessionId ?? "")).ToList();
        if (list.Count == 0) return ToolResult.Ok(all
            ? "No processes."
            : "No processes in your session (all:true lists every session's).", new { processes = Array.Empty<ProcessInfo>() });
        var sb = new StringBuilder();
        foreach (var p in list)
        {
            sb.Append(Describe(p));
            if (all) sb.Append("  [").Append(p.SessionId ?? "?").Append("]");
            sb.Append('\n');
        }
        return ToolResult.Ok(sb.ToString().TrimEnd(), new { processes = list.Select(p => p.ToInfo()).ToList() });
    }

    /// <summary>
    /// The sessions a process call may reach: the caller's own and its subagent sessions (and their descendants), so a
    /// cleanup in one chat never sees, reads or kills what another chat started. The session store carries the parent
    /// links; without it a call reaches only its own session.
    /// </summary>
    private static HashSet<string> VisibleSessions(ToolContext ctx)
    {
        var visible = new HashSet<string>(StringComparer.Ordinal) { ctx.SessionId };
        var store = ctx.Services.Get<ISessionStore>();
        if (store is null) return visible;
        var queue = new Queue<string>([ctx.SessionId]);
        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            foreach (var child in store.ListSessions(new SessionQuery { ParentSessionId = id, IncludeUnmaterialized = true }))
            {
                if (visible.Add(child.Id)) queue.Enqueue(child.Id);
            }
        }
        return visible;
    }

    private ToolResult Output(ToolContext ctx, ToolArgs args)
    {
        if (!TryGet(ctx, args, out var p, out var error)) return ToolResult.Error(error!);
        var tail = Tail(args);
        var (shown, truncated, total, shownLines) = TailOf(p, tail);
        var sb = new StringBuilder();
        sb.Append('[').Append(Describe(p)).Append(']').Append('\n');
        if (truncated) sb.Append($"[showing the last {shownLines} of {total} buffered lines]\n");
        sb.Append(shown.Length > 0 ? shown : "(no output yet)");
        return ToolResult.Ok(sb.ToString(), new { process = p.ToInfo(), tail, truncated });
    }

    /// <summary>
    /// Block until the process exits. This awaits the exit, it never polls: <see cref="ManagedProcess.Completion"/>
    /// completes when the process has exited and its output was drained, so a job that is already done returns at once and
    /// one that finishes mid-wait returns the moment it does. Only a timeout ends the wait early, and then nothing is lost:
    /// the job keeps running, and the answer says so, with how long it has been going and its last lines.
    /// </summary>
    private async Task<ToolResult> WaitAsync(ToolContext ctx, ToolArgs args, CancellationToken ct)
    {
        if (!TryGet(ctx, args, out var p, out var error)) return ToolResult.Error(error!);
        var timeout = Math.Clamp(args.Int("timeout", "timeoutSeconds", "seconds", "wait") ?? DefaultWaitSeconds, 1, MaxWaitSeconds);
        var tail = Tail(args);
        var done = await Task.WhenAny(p!.Completion, Task.Delay(TimeSpan.FromSeconds(timeout), ct)).ConfigureAwait(false) == p.Completion;
        if (done) return Exited(p, tail);
        // The wait ended because the call was cancelled, not because the job hit its timeout: say that, and leave the job running.
        if (ct.IsCancellationRequested) return Aborted(p, tail);
        return StillRunning(p, tail, timeout);
    }

    /// <summary>The job is done: the process line says its status, exit code and duration, then the tail of its output.</summary>
    private static ToolResult Exited(ManagedProcess p, int tail)
    {
        var (shown, truncated, total, shownLines) = TailOf(p, tail);
        var sb = new StringBuilder();
        sb.Append('[').Append(Describe(p)).Append(']').Append('\n');
        if (truncated) sb.Append($"[showing the last {shownLines} of {total} buffered lines]\n");
        sb.Append(shown.Length > 0 ? shown : "(no output)");
        return ToolResult.Ok(sb.ToString(), new
        {
            process = p.ToInfo(), status = p.Status, exitCode = p.ExitCode, durationMs = (int)p.Elapsed.TotalMilliseconds,
            output = shown, tail, truncated,
        });
    }

    /// <summary>The wait ran out and the job is still going: that is an answer, not a lost job. It carries how long the
    /// job has been going and its last lines, which is what tells slow from hung.</summary>
    private static ToolResult StillRunning(ManagedProcess p, int tail, int timeout)
    {
        var (last, truncated, total, shownLines) = TailOf(p, tail);
        var sb = new StringBuilder();
        sb.Append('[').Append(Describe(p)).Append(']').Append('\n');
        sb.Append($"Still running after {timeout}s — it has been going {FormatDuration(p.Elapsed)}. Nothing was lost: ")
            .Append("wait again with a longer timeout, or kill it.\n");
        if (truncated) sb.Append($"[showing the last {shownLines} of {total} buffered lines]\n");
        sb.Append(last.Length > 0 ? last : "(no output yet)");
        return ToolResult.Ok(sb.ToString(), new
        {
            process = p.ToInfo(), status = "running", elapsedMs = (int)p.Elapsed.TotalMilliseconds, waitedMs = timeout * 1000,
            lastLines = last.Split('\n', StringSplitOptions.RemoveEmptyEntries), tail, truncated,
        });
    }

    /// <summary>The wait was cancelled (the call was aborted), not the job timing out: the process was left running.
    /// Same shape as <see cref="StillRunning"/>, but it says the wait was cancelled, not that the timeout ran out.</summary>
    private static ToolResult Aborted(ManagedProcess p, int tail)
    {
        var (last, truncated, total, shownLines) = TailOf(p, tail);
        var sb = new StringBuilder();
        sb.Append('[').Append(Describe(p)).Append(']').Append('\n');
        sb.Append("The wait was cancelled — the process was left running. ")
            .Append("Wait again with a longer timeout, or kill it.\n");
        if (truncated) sb.Append($"[showing the last {shownLines} of {total} buffered lines]\n");
        sb.Append(last.Length > 0 ? last : "(no output yet)");
        return ToolResult.Ok(sb.ToString(), new
        {
            process = p.ToInfo(), status = p.Status, cancelled = true, elapsedMs = (int)p.Elapsed.TotalMilliseconds,
            lastLines = last.Split('\n', StringSplitOptions.RemoveEmptyEntries), tail, truncated,
        });
    }

    private static int Tail(ToolArgs args) =>
        Math.Clamp(args.Int("tail", "lines", "n", "limit") ?? DefaultTail, 1, ToolOutput.ModelMaxLines);

    private static (string Shown, bool Truncated, int Total, int ShownLines) TailOf(ManagedProcess p, int tail)
    {
        var text = ToolOutput.ResolveCarriageReturns(p.Output.Snapshot());
        var (shown, truncated, total, shownLines) = ToolOutput.TailLines(text, tail, ToolOutput.ModelMaxBytes);
        return (shown, truncated, total, shownLines);
    }

    /// <summary>The process an id argument names — within the caller's own session and subagents — or the house-style error naming what is known.</summary>
    private bool TryGet(ToolContext ctx, ToolArgs args, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ManagedProcess? process, [System.Diagnostics.CodeAnalysis.NotNullWhen(false)] out string? error)
    {
        var id = args.Str("id", "process_id", "processId", "pid", "proc");
        if (string.IsNullOrWhiteSpace(id))
        {
            process = null;
            error = "Missing required argument 'id' (e.g. \"proc_…\"). Use action list to see ids.";
            return false;
        }
        process = registry.Get(id);
        if (process is null)
        {
            error = $"No process with id {id}. Use action list to see known processes.";
            return false;
        }
        if (!VisibleSessions(ctx).Contains(process.SessionId ?? ""))
        {
            var foreign = process.SessionId ?? "unknown";
            process = null;
            error = $"Process {id} belongs to another session ({foreign}). process reaches only your own session " +
                    "and its subagents — list all:true shows every session's. ";
            return false;
        }
        error = null;
        return true;
    }

    private async Task<ToolResult> KillAsync(ToolContext ctx, ToolArgs args, CancellationToken ct)
    {
        if (!TryGet(ctx, args, out var p, out var error)) return ToolResult.Error(error!);
        if (!p!.Kill())
            return ToolResult.Ok($"{p.Id} is not running ({p.Status}{(p.ExitCode is { } c ? $", exit code {c}" : "")}).", new { process = p.ToInfo(), killed = false });
        try { await p.Completion.WaitAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false); } catch (TimeoutException) { }
        return ToolResult.Ok($"Killed {p.Id} (pid {p.Pid}) and its child processes.", new { process = p.ToInfo(), killed = true });
    }
}
