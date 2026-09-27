using System.Text.Json;
using System.Text.RegularExpressions;

namespace NetPI.Guardrails;

internal enum GuardAction { Block, Ask }

/// <summary>A rule that matched: what it does, its text as written, and the command part or path it matched.</summary>
internal sealed record Verdict(GuardAction Action, string Rule, string Kind, string Subject);

/// <summary>
/// The rules of <c>guardrails.commands</c> and <c>guardrails.paths</c>, one per line: a line starting with <c>ask:</c>
/// asks the user first, otherwise the rule blocks (<c>block:</c> may be written); <c>#</c> starts a comment line.
/// <para>Commands (bash, pwsh, ssh run) are split into parts at new lines, <c>;</c>, <c>&amp;&amp;</c>, <c>||</c>,
/// <c>|</c> and <c>&amp;</c>, and each regular expression is tried on each part, ignoring case.</para>
/// <para>A path rule protects a file or folder: <c>write</c> and <c>edit</c> may not change it or anything in it, and a
/// shell command (bash, pwsh) may not name it, in any of the spellings a shell takes (<c>~/.netpi</c>,
/// <c>$HOME/.netpi</c>, <c>%USERPROFILE%\.netpi</c>, <c>C:\Users\me\.netpi</c>, <c>/c/Users/me/.netpi</c>…).</para>
/// </summary>
internal sealed class RuleSet
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(50);
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

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

    public static readonly HashSet<string> CommandTools = new(StringComparer.Ordinal) { "bash", "pwsh", "ssh" };
    public static readonly HashSet<string> LocalShellTools = new(StringComparer.Ordinal) { "bash", "pwsh" };
    public static readonly HashSet<string> WriteTools = new(StringComparer.Ordinal) { "write", "edit" };

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
    public Verdict? Check(string tool, JsonElement args, Func<string, string> resolve)
    {
        Verdict? ask = null;
        bool Note(Verdict v)
        {
            if (v.Action == GuardAction.Block) return true;
            ask ??= v;
            return false;
        }

        if (CommandTools.Contains(tool) && Arg(args, "command", "script", "cmd") is { Length: > 0 } command)
        {
            foreach (var part in Parts(command))
                foreach (var r in _commands)
                    if (IsMatch(r.Pattern, part) && Note(new Verdict(r.Action, r.Text, "command", part)))
                        return new Verdict(r.Action, r.Text, "command", part);
            if (LocalShellTools.Contains(tool))
                foreach (var r in _paths)
                    if (Names(command, r) && Note(new Verdict(r.Action, r.Text, "path", r.Root)))
                        return new Verdict(r.Action, r.Text, "path", r.Root);
        }
        else if (WriteTools.Contains(tool) && Arg(args, "path", "filepath", "file", "filename", "target") is { Length: > 0 } path)
        {
            string full;
            try { full = resolve(path); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
            foreach (var r in _paths)
                if (Under(full, r.Root) && Note(new Verdict(r.Action, r.Text, "path", full)))
                    return new Verdict(r.Action, r.Text, "path", full);
        }
        return ask;
    }

    /// <summary>The command a bash, pwsh or ssh (run) call runs (null for other tools or without one).</summary>
    internal static string? CommandOf(string tool, JsonElement args) =>
        CommandTools.Contains(tool) ? Arg(args, "command", "script", "cmd") : null;

    /// <summary>The host an ssh call runs on, when it names one.</summary>
    internal static string? HostOf(JsonElement args) => Arg(args, "host", "target", "server");

    /// <summary>The parts of a command line, split where a shell starts another command (quotes are not understood).</summary>
    internal static IEnumerable<string> Parts(string command) =>
        Regex.Split(command, @"\r?\n|&&|\|\||[;|&]").Select(p => p.Trim()).Where(p => p.Length > 0);

    private static bool IsMatch(Regex r, string s)
    {
        try { return r.IsMatch(s); }
        catch (RegexMatchTimeoutException) { return false; }
    }

    private static bool Under(string full, string root) =>
        full.Equals(root, PathComparison)
        || (full.Length > root.Length && full.StartsWith(root, PathComparison) && full[root.Length] is '\\' or '/');

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
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(text)); }
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

    /// <summary>A string argument by any of its names, ignoring case, '_' and '-' (as the tools read them).</summary>
    private static string? Arg(JsonElement args, params string[] names)
    {
        if (args.ValueKind == JsonValueKind.String)
        {
            try
            {
                using var doc = JsonDocument.Parse(args.GetString() ?? "{}");
                return Arg(doc.RootElement.Clone(), names);
            }
            catch (JsonException) { return null; }
        }
        if (args.ValueKind != JsonValueKind.Object) return null;
        foreach (var p in args.EnumerateObject())
        {
            var key = p.Name.Replace("_", "").Replace("-", "").ToLowerInvariant();
            if (names.Contains(key) && p.Value.ValueKind == JsonValueKind.String) return p.Value.GetString();
        }
        return null;
    }
}
