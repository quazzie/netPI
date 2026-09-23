using System.Globalization;
using System.Text;
using System.Text.Json;

namespace NetPI.Tools.Shell;

public abstract class ShellToolBase : IAgentTool
{
    public abstract ToolDefinition Definition { get; }

    internal abstract Task<ToolResult> RunAsync(ToolContext ctx, ToolArgs args, CancellationToken ct);

    public async Task<ToolResult> ExecuteAsync(ToolContext context, JsonElement args, CancellationToken ct)
    {
        try
        {
            return await RunAsync(context, new ToolArgs(args), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ToolResult.Error($"{Definition.Name} failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

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
        ("timeout", Schema.Int($"Timeout in seconds (default {ShellService.DefaultTimeoutSeconds}, max {ShellService.MaxTimeoutSeconds}). The whole process tree is killed on timeout."), false),
        ("background", Schema.Bool("Start the command in the background and return immediately with a process id (for servers, watchers, long builds)."), false),
        ("cwd", Schema.Str("Working directory (default: the session working directory)."), false));

    private static ToolDefinition BashDefinition()
    {
        var win = OperatingSystem.IsWindows();
        var guidelines = new List<string>
        {
            "Use bash to run programs, builds, tests, git and package managers. For files use read/edit/write/grep/find/ls instead of cat/sed/echo >/grep/find.",
            "Each call starts a fresh non-interactive shell with stdin closed: cd and variables do not persist (use cwd or `cd dir && …`), pass --yes/-y style flags, and never start interactive programs (editors, pagers, `git rebase -i`, REPLs).",
            "For servers, watchers and other long-running commands use background=true, then process_output / process_kill; do not append `&`.",
            $"Output is the last {OutputFormat.ModelMaxLines} lines / {OutputFormat.ModelMaxBytes / 1024}KB; when truncated the full output is saved to a file you can read or grep.",
        };
        if (win)
            guidelines.Insert(1, "On Windows bash is Git Bash: use forward slashes (C:/Users/me or /c/Users/me), POSIX tools (ls, grep, sed) are available, cmd built-ins (dir, copy) are not; Windows programs (dotnet, npm, git, python) work normally. For Windows-specific tasks prefer the pwsh tool.");
        return new ToolDefinition
        {
            Name = "bash",
            Label = "Bash",
            Category = "shell",
            ReadOnly = false,
            SummaryArg = "command",
            Description = (win ? "Run a command in Git Bash (bash -c)." : "Run a bash command (bash -c).") +
                " stdout and stderr are merged. Returns the output (tail) and a note with the exit code when it is not 0. " +
                "Use background=true for long-running processes.",
            Parameters = Parameters("The bash command/script to run."),
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
        Description =
            "Run a PowerShell 7 (pwsh) script, non-interactive, without profile. Output streams are merged; " +
            "the exit code is reported when it is not 0. Use background=true for long-running processes.",
        Parameters = Parameters("The PowerShell script to run."),
        PromptGuidelines =
        [
            "Use pwsh for Windows-specific work (registry, services, Windows paths, .NET APIs, cmdlets); prefer bash for general commands.",
            "Each pwsh call is a fresh non-interactive process without profile: state does not persist; never use Read-Host or other prompts.",
        ],
    };
}

public sealed class ProcessListTool(ProcessRegistry registry) : ShellToolBase
{
    public override ToolDefinition Definition { get; } = new()
    {
        Name = "process_list",
        Label = "Processes",
        Category = "shell",
        ReadOnly = true,
        Description = "List background processes and recent shell commands with their status (running / exited N / killed / timeout).",
        Parameters = Schema.Object(),
        PromptGuidelines = ["Use process_list to find the id of a background process you started earlier."],
    };

    internal override Task<ToolResult> RunAsync(ToolContext ctx, ToolArgs args, CancellationToken ct)
    {
        var list = registry.List();
        if (list.Count == 0) return Task.FromResult(ToolResult.Ok("No processes.", new { processes = Array.Empty<ProcessInfo>() }));
        var sb = new StringBuilder();
        foreach (var p in list) sb.Append(Describe(p)).Append('\n');
        return Task.FromResult(ToolResult.Ok(sb.ToString().TrimEnd(), new { processes = list.Select(p => p.ToInfo()).ToList() }));
    }
}

public sealed class ProcessOutputTool(ProcessRegistry registry) : ShellToolBase
{
    public const int DefaultTail = 200;

    public override ToolDefinition Definition { get; } = new()
    {
        Name = "process_output",
        Label = "Process output",
        Category = "shell",
        ReadOnly = true,
        SummaryArg = "id",
        Description = "Show the latest output of a background process (or a recent command) and whether it is still running.",
        Parameters = Schema.Object(
            ("id", Schema.Str("Process id (proc_…) as returned by bash/pwsh with background=true."), true),
            ("tail", Schema.Int($"Number of trailing lines to return (default {DefaultTail}, max {OutputFormat.ModelMaxLines})."), false)),
        PromptGuidelines = ["Check a background process with process_output instead of sleeping in the shell; call it again later to see new output."],
    };

    internal override Task<ToolResult> RunAsync(ToolContext ctx, ToolArgs args, CancellationToken ct)
    {
        var id = args.Str("id", "process_id", "processId", "pid", "proc");
        if (string.IsNullOrWhiteSpace(id)) return Task.FromResult(ToolResult.Error("Missing required argument 'id' (e.g. \"proc_…\"). Use process_list to see ids."));
        var p = registry.Get(id);
        if (p is null) return Task.FromResult(ToolResult.Error($"No process with id {id}. Use process_list to see known processes."));
        var tail = Math.Clamp(args.Int("tail", "lines", "n", "limit") ?? DefaultTail, 1, OutputFormat.ModelMaxLines);
        var text = OutputFormat.ResolveCarriageReturns(p.Output.Snapshot());
        var (shown, truncated, total, shownLines) = OutputFormat.TailLines(text, tail, OutputFormat.ModelMaxBytes);
        var sb = new StringBuilder();
        sb.Append('[').Append(Describe(p)).Append(']').Append('\n');
        if (truncated) sb.Append($"[showing the last {shownLines} of {total} buffered lines]\n");
        sb.Append(shown.Length > 0 ? shown : "(no output yet)");
        return Task.FromResult(ToolResult.Ok(sb.ToString(), new { process = p.ToInfo(), tail, truncated }));
    }
}

public sealed class ProcessKillTool(ProcessRegistry registry) : ShellToolBase
{
    public override ToolDefinition Definition { get; } = new()
    {
        Name = "process_kill",
        Label = "Kill process",
        Category = "shell",
        ReadOnly = false,
        SummaryArg = "id",
        Description = "Kill a background process and all of its child processes.",
        Parameters = Schema.Object(("id", Schema.Str("Process id (proc_…)."), true)),
        PromptGuidelines = ["Stop background processes you started with process_kill once you no longer need them."],
    };

    internal override async Task<ToolResult> RunAsync(ToolContext ctx, ToolArgs args, CancellationToken ct)
    {
        var id = args.Str("id", "process_id", "processId", "pid", "proc");
        if (string.IsNullOrWhiteSpace(id)) return ToolResult.Error("Missing required argument 'id'. Use process_list to see ids.");
        var p = registry.Get(id);
        if (p is null) return ToolResult.Error($"No process with id {id}. Use process_list to see known processes.");
        if (!p.Kill())
            return ToolResult.Ok($"{p.Id} is not running ({p.Status}{(p.ExitCode is { } c ? $", exit code {c}" : "")}).", new { process = p.ToInfo(), killed = false });
        try { await p.Completion.WaitAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false); } catch (TimeoutException) { }
        return ToolResult.Ok($"Killed {p.Id} (pid {p.Pid}) and its child processes.", new { process = p.ToInfo(), killed = true });
    }
}
