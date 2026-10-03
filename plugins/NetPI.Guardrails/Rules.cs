using System.Text.Json;
using System.Text.RegularExpressions;

namespace NetPI.Guardrails;

internal enum GuardAction { Block, Ask }

/// <summary>A rule that matched: what it does, its text as written, and the command part or path it matched.</summary>
/// <param name="Detail">What the typed sleep rule read off the command (how long it waits); null for the other kinds.</param>
internal sealed record Verdict(GuardAction Action, string Rule, string Kind, string Subject, string? Detail = null);

/// <summary>
/// The rules of <c>guardrails.commands</c> and <c>guardrails.paths</c>, one per line: a line starting with <c>ask:</c>
/// asks the user first, otherwise the rule blocks (<c>block:</c> may be written); <c>#</c> starts a comment line.
/// <para>Commands (bash, pwsh, ssh run) are split into parts at new lines, <c>;</c>, <c>&amp;&amp;</c>, <c>||</c>,
/// <c>|</c> and <c>&amp;</c> (not the <c>&amp;</c> of a redirection such as <c>2&gt;&amp;1</c>), and each regular
/// expression is tried on each part, ignoring case. A part is tried as written and with the decoration a shell ignores
/// taken off (see <see cref="Forms"/>), so a trailing comment, a redirection, quotes or <c>sudo</c> do not hide it.</para>
/// <para>A path rule protects a file or folder: <c>write</c> and <c>edit</c> may not change it or anything in it, nor may
/// an ssh download write into it, and a shell command (bash, pwsh) may not name it, in any of the spellings a shell takes
/// (<c>~/.netpi</c>, <c>$HOME/.netpi</c>, <c>%USERPROFILE%\.netpi</c>, <c>C:\Users\me\.netpi</c>,
/// <c>/c/Users/me/.netpi</c>…).</para>
/// <para>The guard has to judge the call that runs, not another one: tool names are matched ignoring case (as the runner
/// finds the tool) and every argument is read as the tool reads it — names ignoring case, <c>_</c>, <c>-</c> and spaces,
/// the first name the tool tries wins, an array is its lines, a string-encoded arguments object is unwrapped.</para>
/// </summary>
internal sealed partial class RuleSet
{
    /// <summary>A rule that takes longer than this on one part counts as matching it (see <see cref="IsMatch"/>).</summary>
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(250);
    /// <summary>How path spellings are compared: case-insensitive on Windows and macOS (their default file systems), like the rest of the path checks.</summary>
    private static readonly StringComparison PathComparison = WorkspacePaths.Comparison;

    /// <summary>Catastrophic commands only: none of these has a place in normal development work.</summary>
    public static readonly IReadOnlyList<string> DefaultCommands =
    [
        @"^(sudo\s+)?rm\s+(-\S+\s+)*(/|/\*|~/?\*?|\$HOME/?\*?)(\s+-\S+)*$",
        @"^(sudo\s+)?(rm|rmdir|rd|del|erase|Remove-Item)\s+(\S+\s+)*['""]?([a-z]:[\\/]?|/[a-z]/?)\*?['""]?(\s+-\S+)*$",
        @"^(sudo\s+)?mkfs(\.\S+)?(\s|$)",
        @"^(sudo\s+)?dd\s.*\bof=/dev/(?!null\b)",
        @"^format(\.com)?\s+[a-z]:",
        @"^(sudo\s+)?(shutdown|reboot|poweroff|halt)(\s|$)",
        @"^(Stop|Restart)-Computer(\s|$)",
        @"^:\(\)\s*\{",
    ];

    /// <summary>NetPI's own home asks first; SSH keys and config stay untouched.</summary>
    public static readonly IReadOnlyList<string> DefaultPaths = ["ask: ~/.netpi", "~/.ssh"];

    // Ignoring case: the runner finds a tool by its name ignoring case, so "Bash" runs bash.
    public static readonly HashSet<string> CommandTools = new(StringComparer.OrdinalIgnoreCase) { "bash", "pwsh", "ssh" };
    public static readonly HashSet<string> LocalShellTools = new(StringComparer.OrdinalIgnoreCase) { "bash", "pwsh" };
    public static readonly HashSet<string> WriteTools = new(StringComparer.OrdinalIgnoreCase) { "write", "edit" };

    // The names each tool reads its argument by, in the order it tries them (Tools.Shell ShellService, Tools.Ssh run,
    // Tools.Files FileToolBase.PathNames, Tools.Ssh copy): the first one present wins, so the guard takes the same one.
    private static readonly string[] ShellCommandNames = ["command", "cmd", "script", "code", "commands", "input"];
    private static readonly string[] SshCommandNames = ["script", "command", "cmd", "code"];
    private static readonly string[] WritePathNames = ["path", "file_path", "filePath", "file", "filename", "fileName", "target"];
    private static readonly string[] SshDownloadTargetNames = ["to", "destination", "dest", "target"];

    private static string[] CommandNames(string tool) => tool.Equals("ssh", StringComparison.OrdinalIgnoreCase) ? SshCommandNames : ShellCommandNames;

    private sealed record CommandRule(string Text, Regex Pattern, GuardAction Action);
    private sealed record PathRule(string Text, string Root, string[] Spellings, GuardAction Action);

    private readonly List<CommandRule> _commands = [];
    private readonly List<PathRule> _paths = [];

    /// <summary>Rules that could not be used (an invalid regular expression), with why.</summary>
    public List<string> Problems { get; } = [];

    public int Count => _commands.Count + _paths.Count;

    public static RuleSet Parse(IEnumerable<string> commands, IEnumerable<string> paths, string home)
    {
        var set = new RuleSet();
        foreach (var line in commands)
        {
            if (!Line(line, out var action, out var text)) continue;
            try
            {
                set._commands.Add(new CommandRule(line.Trim(), new Regex(text, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, MatchTimeout), action));
            }
            catch (ArgumentException ex)
            {
                set.Problems.Add($"guardrails.commands: {line.Trim()}: {ex.Message}");
            }
        }
        foreach (var line in paths)
        {
            if (!Line(line, out var action, out var text)) continue;
            var root = Expand(text, home);
            if (root is null)
            {
                set.Problems.Add($"guardrails.paths: {line.Trim()}: not an absolute path or one under ~");
                continue;
            }
            set._paths.Add(new PathRule(line.Trim(), root, Spellings(root, home), action));
        }
        return set;
    }

    private static bool Line(string? line, out GuardAction action, out string text)
    {
        action = GuardAction.Block;
        text = line?.Trim() ?? "";
        if (text.Length == 0 || text.StartsWith('#')) return false;
        if (text.StartsWith("ask:", StringComparison.OrdinalIgnoreCase))
        {
            action = GuardAction.Ask;
            text = text[4..].Trim();
        }
        else if (text.StartsWith("block:", StringComparison.OrdinalIgnoreCase)) text = text[6..].Trim();
        return text.Length > 0;
    }

    /// <summary>The first rule the call breaks, a blocking rule before an asking one; null when it may run.</summary>
    public Verdict? Check(string tool, ToolArgs args, Func<string, string> resolve)
    {
        Verdict? ask = null;
        bool Note(Verdict v)
        {
            if (v.Action == GuardAction.Block) return true;
            ask ??= v;
            return false;
        }

        if (CommandTools.Contains(tool) && args.Str(CommandNames(tool)) is { Length: > 0 } command)
        {
            foreach (var part in Parts(command))
            {
                var forms = Forms(part);
                foreach (var r in _commands)
                    if (forms.Any(f => IsMatch(r.Pattern, f)) && Note(new Verdict(r.Action, r.Text, "command", part)))
                        return new Verdict(r.Action, r.Text, "command", part);
            }
            if (LocalShellTools.Contains(tool))
                foreach (var r in _paths)
                    if (Names(command, r) && Note(new Verdict(r.Action, r.Text, "path", r.Root)))
                        return new Verdict(r.Action, r.Text, "path", r.Root);
        }

        // Independent of the command above: an ssh call can carry a script and still be a download.
        if (LocalWriteTarget(tool, args) is { Length: > 0 } path)
        {
            string full;
            try { full = resolve(path); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return ask; }
            foreach (var r in _paths)
                if (Under(full, r.Root) && Note(new Verdict(r.Action, r.Text, "path", full)))
                    return new Verdict(r.Action, r.Text, "path", full);
        }
        return ask;
    }

    /// <summary>The command a bash, pwsh or ssh (run) call runs (null for other tools or without one).</summary>
    internal static string? CommandOf(string tool, ToolArgs args) =>
        CommandTools.Contains(tool) ? args.Str(CommandNames(tool)) : null;

    /// <summary>The host an ssh call runs on, when it names one.</summary>
    internal static string? HostOf(ToolArgs args) => args.Str("host", "server", "alias");

    /// <summary>The local path a call writes: the file of write and edit, the destination of an ssh download (scp writes it).</summary>
    internal static string? LocalWriteTarget(string tool, ToolArgs args)
    {
        if (WriteTools.Contains(tool)) return args.Str(WritePathNames);
        return IsSshDownload(tool, args) ? args.Str(SshDownloadTargetNames)?.Trim() : null;
    }

    /// <summary>
    /// Whether an ssh call is a download, routed as the ssh tool routes it: the action is <c>copy</c> (or scp, upload,
    /// download), and the direction is the <c>direction</c> argument, or the action itself when that says upload/download.
    /// </summary>
    internal static bool IsSshDownload(string tool, ToolArgs args)
    {
        if (!tool.Equals("ssh", StringComparison.OrdinalIgnoreCase)) return false;
        var action = args.Str("action", "verb", "command")?.Trim().ToLowerInvariant();
        if (action is null && args.Has("script")) action = "run";
        if (action is not ("copy" or "scp" or "upload" or "download")) return false;
        var direction = args.Str("direction", "mode")?.Trim().ToLowerInvariant();
        if (direction is null && args.Str("action")?.Trim().ToLowerInvariant() is "upload" or "download")
            direction = args.Str("action")!.Trim().ToLowerInvariant();
        return direction == "download";
    }

    /// <summary>
    /// The parts of a command line, split where a shell starts another command (quotes are not understood). A backslash at
    /// the end of a line continues it, and the <c>&amp;</c> of a redirection (<c>2&gt;&amp;1</c>, <c>&amp;&gt;file</c>) is not a separator.
    /// </summary>
    internal static IEnumerable<string> Parts(string command) =>
        Regex.Split(LineContinuation().Replace(command, " "), @"\r?\n|&&|\|\||;|\||(?<![<>])&(?!>)").Select(p => p.Trim()).Where(p => p.Length > 0);

    /// <summary>
    /// A part with what a shell ignores taken off, quotes kept: a trailing comment, redirections and the wrappers
    /// (<see cref="Wrapper"/>). A rule that reads the words of a part (<see cref="Sleeps"/>) starts here, where the command
    /// position is.
    /// </summary>
    internal static string Undecorated(string part)
    {
        try
        {
            var s = TrailingComment().Replace(part, "");
            s = Redirection().Replace(s, "");
            return Wrapper().Replace(s, "").Trim();
        }
        catch (RegexMatchTimeoutException) { return part; }
    }

    /// <summary>
    /// The ways to read a part: as written, and with what a shell ignores taken off — a trailing comment, redirections,
    /// quotes, and wrappers such as <c>sudo -n</c>, <c>env</c>, <c>command</c>, <c>time</c>, <c>then</c> and <c>do</c>. A rule that
    /// matches any form applies. The extra form only widens what is caught: it can never hide the part as written.
    /// </summary>
    internal static IReadOnlyList<string> Forms(string part)
    {
        try
        {
            var s = TrailingComment().Replace(part, "");
            s = Redirection().Replace(s, "");
            s = s.Replace("\"", "").Replace("'", "").Trim();
            s = Wrapper().Replace(s, "").Trim();
            return s.Length == 0 || s == part ? [part] : [part, s];
        }
        catch (RegexMatchTimeoutException) { return [part]; }   // the helper patterns are linear; the part as written is still checked
    }

    [GeneratedRegex(@"\\\r?\n", RegexOptions.CultureInvariant, 250)]
    private static partial Regex LineContinuation();

    // One whitespace character before the marker, not a run: each start position is O(1), so a long run of spaces cannot make it quadratic.
    [GeneratedRegex(@"\s#[^\r\n]*$", RegexOptions.CultureInvariant, 250)]
    private static partial Regex TrailingComment();

    [GeneratedRegex(@"\s(?:\d*|&)(?:>>?|<)\s*&?\S*", RegexOptions.CultureInvariant, 250)]
    private static partial Regex Redirection();

    [GeneratedRegex(@"^(?:(?:sudo(?:\s+(?:-[ugCDhpRrT]\s+\S+|-\S+))*|command|env(?:\s+(?:-\S+|\w+=\S*))*|time|nohup|exec|if|elif|while|until|then|do|else|!)\s+)+",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, 250)]
    private static partial Regex Wrapper();

    /// <summary>A rule that times out on a part counts as matching it: not knowing is not a pass.</summary>
    private static bool IsMatch(Regex r, string s)
    {
        try { return r.IsMatch(s); }
        catch (RegexMatchTimeoutException) { return true; }
    }

    private static bool Under(string full, string root)
    {
        // One canonical function for both sides: a long-path prefix, an admin share of this machine or a junction
        // anywhere along the way is the same place, not a different spelling that slips past the rule.
        full = WorkspacePaths.Canonical(full);
        root = WorkspacePaths.Canonical(root);
        return full.Equals(root, PathComparison)
            || (full.Length > root.Length && full.StartsWith(root, PathComparison) && full[root.Length] is '\\' or '/');
    }

    /// <summary>Whether a command names the path: a spelling of it, followed by the end, a separator or punctuation.</summary>
    private static bool Names(string command, PathRule rule)
    {
        foreach (var s in rule.Spellings)
        {
            for (var i = command.IndexOf(s, PathComparison); i >= 0; i = command.IndexOf(s, i + 1, PathComparison))
            {
                var end = i + s.Length;
                if (end == command.Length || command[end] is '/' or '\\' or '"' or '\'' or ' ' or '\t' or '\r' or '\n' or ';' or ')' or '|' or '&' or '>' or '<' or '`')
                    return true;
            }
        }
        return false;
    }

    /// <summary>~ and environment variables expanded; null when the result is not an absolute path.</summary>
    internal static string? Expand(string text, string home)
    {
        text = text.Trim().Trim('"', '\'');
        if (text == "~") text = home;
        else if (text.StartsWith("~/", StringComparison.Ordinal) || text.StartsWith("~\\", StringComparison.Ordinal)) text = Path.Combine(home, text[2..]);
        text = Environment.ExpandEnvironmentVariables(text);
        if (!Path.IsPathRooted(text) || (OperatingSystem.IsWindows() && !Path.IsPathFullyQualified(text))) return null;
        // The same canonical form the write side gets: a rule root and a write target are compared as places, not spellings.
        try { return WorkspacePaths.Canonical(text); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    /// <summary>How shells spell a path: as it is, with forward slashes, the Git Bash drive form, and from the home folder.</summary>
    internal static string[] Spellings(string root, string home)
    {
        var list = new List<string> { root };
        if (OperatingSystem.IsWindows())
        {
            list.Add(root.Replace('\\', '/'));
            if (root.Length >= 2 && root[1] == ':') list.Add("/" + char.ToLowerInvariant(root[0]) + root[2..].Replace('\\', '/'));
        }
        home = Path.TrimEndingDirectorySeparator(home);
        if (Under(root, home) && root.Length > home.Length)
        {
            var rest = root[(home.Length + 1)..];
            var fwd = rest.Replace('\\', '/');
            list.AddRange(["~/" + fwd, "$HOME/" + fwd, "${HOME}/" + fwd]);
            if (OperatingSystem.IsWindows())
            {
                var back = rest.Replace('/', '\\');
                list.AddRange(["~\\" + back, "$HOME\\" + back, "%USERPROFILE%\\" + back, "$env:USERPROFILE\\" + back, "$env:USERPROFILE/" + fwd, "${env:USERPROFILE}\\" + back]);
            }
        }
        return [.. list.Distinct(StringComparer.OrdinalIgnoreCase)];
    }
}

