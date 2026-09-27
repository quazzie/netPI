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
        "Check background processes with process_output instead of sleeping, and kill the ones you no longer need; don't append `&` to a command.";

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
        ("timeout", Schema.Int($"Seconds (default {ShellService.DefaultTimeoutSeconds}, max {ShellService.MaxTimeoutSeconds})"), false),
        ("background", Schema.Bool("Returns a process id at once"), false),
        ("cwd", Schema.Str(""), false));

    private static readonly string ShellHelp =
        $"stdout and stderr are merged; the output's tail is returned, with a note of the exit code when it is not 0. timeout: " +
        $"default {ShellService.DefaultTimeoutSeconds} s, max {ShellService.MaxTimeoutSeconds}; the whole process tree is killed " +
        "on timeout. background: starts the command and returns a process id at once (servers, watchers, long builds); see " +
        "process_list, process_output and process_kill. cwd: default the session working directory.";

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
        PromptGuidelines = [],
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
        PromptGuidelines = [BackgroundProcesses],
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
        PromptGuidelines = [BackgroundProcesses],
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
