namespace NetPI;

/// <summary>
/// Which argument of a tool call is a path, in one place: the tools that read it (Tools.Files, Tools.Shell, Tools.Ssh)
/// and the guards that judge it before it runs (the workspace guard, the guardrails' protected paths) all take their
/// names from here, so a call cannot say one thing to a guard and another to the tool. The first name present wins, in
/// the order the tool tries it; a blank value is the tool's own refusal, never a reason to read the next name.
/// Plugins do not share code, so the vocabulary lives in the contracts.
/// </summary>
public static class ToolPathArgs
{
    /// <summary>The names the file tools (read, write, edit, git) read their path by.</summary>
    public static readonly string[] PathNames = ["path", "file_path", "filePath", "file", "filename", "fileName", "target"];

    /// <summary>The several-files form of <c>edit</c>: <c>files: [{ path, edits }]</c>.</summary>
    public static readonly string[] EditFilesNames = ["files", "fileEdits"];

    /// <summary>The names the shell tools (bash, pwsh) read their working directory by.</summary>
    public static readonly string[] CwdNames = ["cwd", "workdir", "working_directory", "workingDirectory", "directory", "dir"];

    /// <summary>The names the shell tools read their command by.</summary>
    public static readonly string[] ShellCommandNames = ["command", "cmd", "script", "code", "commands", "input"];

    /// <summary>The names <c>ssh</c> (run) reads its script by.</summary>
    public static readonly string[] SshCommandNames = ["script", "command", "cmd", "code"];

    /// <summary>The names <c>ssh</c> (copy) reads its local destination by: the file an ssh download writes on this machine.</summary>
    public static readonly string[] SshDownloadTargetNames = ["to", "destination", "dest", "target"];

    /// <summary>The names the <c>windows</c> tool reads its action, the program it opens and that program's arguments by.</summary>
    public static readonly string[] WindowsActionNames = ["action", "verb", "command"];
    public static readonly string[] WindowsAppNames = ["app", "program", "exe", "path", "uri"];
    public static readonly string[] WindowsArgsNames = ["args", "arguments"];

    /// <summary>The names <c>ssh</c> reads the host (an alias of the ssh config) by.</summary>
    public static readonly string[] SshHostNames = ["host", "server", "alias"];

    /// <summary>
    /// The action of a <c>browser</c> step as the tool runs it: its synonyms folded to the one name the tool switches on
    /// (a lowercase, trimmed word; empty when there is none).
    /// </summary>
    public static string BrowserStepAction(string? action)
    {
        action = (action ?? "").Trim().ToLowerInvariant();
        return action switch
        {
            "navigate" or "goto" or "go" => "open",
            "press" => "key",
            "fill" => "type",
            "js" or "evaluate" or "script" => "eval",
            "refresh" => "reload",
            "batch" => "steps",
            _ => action,
        };
    }

    /// <summary>The action of a <c>browser</c> call as the tool runs it: <see cref="BrowserStepAction"/> and the hand-over and show synonyms.</summary>
    public static string BrowserAction(string? action) => BrowserStepAction(action) switch
    {
        "handover" or "hand_over" or "done" => "leave",
        "view" or "watch" or "login" => "show",
        var other => other,
    };

    /// <summary>The action of a <c>windows</c> call as the tool runs it (synonyms folded; empty when there is none).</summary>
    public static string WindowsAction(string? action)
    {
        action = (action ?? "").Trim().ToLowerInvariant();
        return action switch
        {
            "attach" or "switch" => "use",
            "launch" or "start" or "run" => "open",
            "press" => "key",
            "windows" or "ls" => "list",
            _ => action,
        };
    }

    /// <summary>
    /// The action a call to <c>browser</c> or <c>windows</c> runs, as the tool reads it, or the plain lowercase action
    /// argument of any other tool (null without one). A guard that matches actions by name sees what the tool runs, not
    /// the synonym the call used.
    /// </summary>
    public static string? ActionOf(string tool, ToolArgs args)
    {
        if (args.Str(WindowsActionNames) is not { } raw) return null;
        if (tool.Equals("browser", StringComparison.OrdinalIgnoreCase)) return BrowserAction(raw);
        if (tool.Equals("windows", StringComparison.OrdinalIgnoreCase)) return WindowsAction(raw);
        return raw.Trim().ToLowerInvariant();
    }

    /// <summary>
    /// The command line a <c>windows</c> open starts (the program and its arguments, as the tool reads them), or null for any
    /// other call. Starting a program is running a command like the shell's, so the command rules judge it too: a program
    /// and its arguments went past every rule as a second way to run one (idea-03oyad).
    /// </summary>
    public static string? WindowsOpenCommand(string tool, ToolArgs args)
    {
        if (!tool.Equals("windows", StringComparison.OrdinalIgnoreCase)) return null;
        if (WindowsAction(args.Str(WindowsActionNames)) != "open") return null;
        if (NonBlank(args.Str(WindowsAppNames)) is not { } app) return null;
        return NonBlank(args.Str(WindowsArgsNames)) is { } rest ? app + " " + rest : app;
    }

    /// <summary>The path argument of a file tool call, trimmed; null when absent or blank.</summary>
    public static string? PathOf(ToolArgs args) => NonBlank(args.Str(PathNames));

    /// <summary>The working directory a shell call names, trimmed; null when absent or blank (the tool then runs in the session's cwd).</summary>
    public static string? CwdOf(ToolArgs args) => NonBlank(args.Str(CwdNames));

    /// <summary>The command a shell or ssh call runs, as the tool reads it (null when it names none).</summary>
    public static string? CommandOf(string tool, ToolArgs args) =>
        args.Str(tool.Equals("ssh", StringComparison.OrdinalIgnoreCase) ? SshCommandNames : ShellCommandNames);

    /// <summary>
    /// The local paths a file-writing call changes, in the order the tool takes them: for an <c>edit</c> of several files
    /// every <c>files[].path</c>, otherwise the one path argument. A top-level <c>path</c> beside bare-edit <c>files</c>
    /// entries (no <c>path</c> of their own) is the file the tool applies them to, so it is the target then. An edit of
    /// several files is all or nothing, so a guard judges every one of them.
    /// </summary>
    public static List<string> WriteTargets(string tool, ToolArgs args)
    {
        if (tool.Equals("edit", StringComparison.OrdinalIgnoreCase) && args.List(EditFilesNames) is { Count: > 0 } files)
        {
            var paths = new List<string>();
            foreach (var f in files)
                if (f.ValueKind == System.Text.Json.JsonValueKind.Object && PathOf(new ToolArgs(f)) is { } p) paths.Add(p);
            if (paths.Count == 0 && PathOf(args) is { } top &&
                files.All(f => f.ValueKind != System.Text.Json.JsonValueKind.Object || PathOf(new ToolArgs(f)) is null))
                paths.Add(top);
            return paths;
        }
        return PathOf(args) is { } one ? [one] : [];
    }

    /// <summary>
    /// Whether an ssh call is a download, routed as the ssh tool routes it: the action is <c>copy</c> (or scp, upload,
    /// download), and the direction is <see cref="SshDirection"/>.
    /// </summary>
    public static bool IsSshDownload(string tool, ToolArgs args)
    {
        if (!tool.Equals("ssh", StringComparison.OrdinalIgnoreCase)) return false;
        var action = args.Str("action", "verb", "command")?.Trim().ToLowerInvariant();
        if (action is null && args.Has("script")) action = "run";
        if (action is not ("copy" or "scp" or "upload" or "download")) return false;
        return SshDirection(args) == "download";
    }

    /// <summary>
    /// The direction of an ssh copy as the tool resolves it: the action itself when it says upload/download and there is
    /// no <c>direction</c> argument (the dispatcher fills it in), otherwise <c>direction</c>, then <c>mode</c> — so a call
    /// cannot name one direction and be run as the other.
    /// </summary>
    public static string? SshDirection(ToolArgs args)
    {
        var action = args.Str("action")?.Trim().ToLowerInvariant();
        var direction = args.Str("direction") is null && action is ("upload" or "download") ? action : args.Str("direction", "mode");
        return direction?.Trim().ToLowerInvariant();
    }

    /// <summary>The local path an ssh download writes (<see cref="IsSshDownload"/>), trimmed; null for any other call.</summary>
    public static string? SshDownloadTarget(string tool, ToolArgs args) =>
        IsSshDownload(tool, args) ? NonBlank(args.Str(SshDownloadTargetNames)) : null;

    private static string? NonBlank(string? value) => value?.Trim() is { Length: > 0 } s ? s : null;
}
