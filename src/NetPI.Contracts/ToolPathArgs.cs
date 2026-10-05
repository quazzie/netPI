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

    /// <summary>The path argument of a file tool call, trimmed; null when absent or blank.</summary>
    public static string? PathOf(ToolArgs args) => NonBlank(args.Str(PathNames));

    /// <summary>The working directory a shell call names, trimmed; null when absent or blank (the tool then runs in the session's cwd).</summary>
    public static string? CwdOf(ToolArgs args) => NonBlank(args.Str(CwdNames));

    /// <summary>The command a shell or ssh call runs, as the tool reads it (null when it names none).</summary>
    public static string? CommandOf(string tool, ToolArgs args) =>
        args.Str(tool.Equals("ssh", StringComparison.OrdinalIgnoreCase) ? SshCommandNames : ShellCommandNames);

    /// <summary>
    /// The local paths a file-writing call changes, in the order the tool takes them: for an <c>edit</c> of several files
    /// every <c>files[].path</c>, otherwise the one path argument. An edit of several files is all or nothing, so a
    /// guard judges every one of them.
    /// </summary>
    public static List<string> WriteTargets(string tool, ToolArgs args)
    {
        if (tool.Equals("edit", StringComparison.OrdinalIgnoreCase) && args.List(EditFilesNames) is { Count: > 0 } files)
        {
            var paths = new List<string>();
            foreach (var f in files)
                if (f.ValueKind == System.Text.Json.JsonValueKind.Object && PathOf(new ToolArgs(f)) is { } p) paths.Add(p);
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
