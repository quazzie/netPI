using System.Text;
using System.Text.Json.Nodes;

namespace NetPI.Tools.Ssh;

/// <summary>
/// A <c>bash</c> or <c>pwsh</c> command that starts <c>ssh</c>, <c>scp</c> or <c>sftp</c> to a host of the ssh config is
/// refused, with the <c>ssh</c> tool call that does the same (idea-xh3pvw): the tool reuses the connection, saves the output,
/// gives the host's note, and the guardrails and the Autonomy journal see the host and the remote command instead of a
/// local shell line. A host the config does not name is left alone (the tool cannot reach it either), and so is anything
/// that only mentions ssh (<c>git push</c>, <c>echo ssh</c>, a quoted string). Setting <c>ssh.blockShellSsh</c>.
/// </summary>
internal sealed class ShellSshHook(IPluginContext ctx) : IAgentHook
{
    /// <summary>Before the guardrails (10 000): the agent hears "use the ssh tool" rather than a question about the shell line.</summary>
    public int Order => 9_000;

    public ValueTask<ToolCallDecision?> OnBeforeToolCallAsync(AgentTurnContext turn, ToolCallPart call) => ValueTask.FromResult(Decide(call));

    /// <summary>The refusal for a shell call that starts ssh to a configured host; null for every other call.</summary>
    internal ToolCallDecision? Decide(ToolCallPart call)
    {
        try
        {
            var (name, raw) = ToolCalls.Effective(call);
            if (!name.Equals("bash", StringComparison.OrdinalIgnoreCase) && !name.Equals("pwsh", StringComparison.OrdinalIgnoreCase)) return null;
            if (!ctx.Settings.Get("ssh.blockShellSsh", true)) return null;
            if (ToolPathArgs.CommandOf(name, ToolArgs.Parse(raw)) is not { Length: > 0 } command) return null;
            if (command.IndexOf("ssh", StringComparison.OrdinalIgnoreCase) < 0 && command.IndexOf("scp", StringComparison.OrdinalIgnoreCase) < 0) return null;
            var o = SshOptions.Read(ctx.Settings, ctx.Paths.Home);
            if (ShellSsh.Find(command, SshConfig.Read(o.Config)) is not { } hit) return null;
            return new ToolCallDecision { Block = true, Reason = hit.Advice() };
        }
        catch (Exception) { return null; } // a rule that only redirects never blocks a call it cannot read
    }
}

/// <summary>A shell command's start of ssh/scp/sftp to a configured host: which program, which host alias, the remote command.</summary>
internal sealed record ShellSshHit(string Program, string Host, string? Remote, string Part)
{
    public string Advice()
    {
        var call = Program == "ssh"
            ? new JsonObject { ["action"] = "run", ["host"] = Host, ["script"] = Remote ?? "…" }
            : new JsonObject { ["action"] = "copy", ["host"] = Host, ["direction"] = "upload | download", ["from"] = "…", ["to"] = "…" };
        var what = Program == "ssh" && Remote is null ? "an interactive login, which the ssh tool does not do: run the commands you need with" : "use";
        return $"`{Part}` starts {Program} through the shell, but {Host} is a host of the ssh tool: {what} " +
               $"ssh {call.ToJsonString()} instead (it reuses the connection, saves the output and gives the host's notes). " +
               "Nothing ran.";
    }
}

internal static class ShellSsh
{
    private static readonly string[] Programs = ["ssh", "scp", "sftp"];
    /// <summary>The options of each program that take a value (the next word, when not attached).</summary>
    private static readonly Dictionary<string, string> ValueFlags = new()
    {
        ["ssh"] = "BbcDEeFIiJLlmOoPpQRSWw",
        ["scp"] = "cDFiJlOoPSX",
        ["sftp"] = "BbcDFiJlOoPRSsX",
    };
    private static readonly HashSet<string> Wrappers = new(StringComparer.OrdinalIgnoreCase) { "sudo", "env", "command", "exec", "time", "nohup", "&" };

    /// <summary>The first part of <paramref name="command"/> that starts ssh, scp or sftp to one of <paramref name="hosts"/>, or null.</summary>
    public static ShellSshHit? Find(string command, IReadOnlyList<SshHost> hosts)
    {
        if (hosts.Count == 0) return null;
        foreach (var part in Parts(command))
        {
            var words = Words(part);
            var i = 0;
            // what a shell starts first: past variable assignments and wrappers (sudo and its options, env, exec, …)
            while (i < words.Count && (Wrappers.Contains(words[i]) || IsAssignment(words[i]) || (i > 0 && Wrappers.Contains(words[i - 1]) && words[i].StartsWith('-'))))
                i++;
            if (i >= words.Count) continue;
            var program = Program(words[i]);
            if (program is null) continue;
            var hit = program == "ssh" ? Ssh(words, i + 1, hosts) : Copy(program, words, i + 1, hosts);
            if (hit is not null) return hit with { Program = program, Part = part.Trim() };
        }
        return null;
    }

    private static ShellSshHit? Ssh(List<string> words, int start, IReadOnlyList<SshHost> hosts)
    {
        for (var i = start; i < words.Count; i++)
        {
            var w = words[i];
            if (w == "--") { i++; if (i >= words.Count) return null; w = words[i]; }
            else if (w.StartsWith('-') && w.Length > 1)
            {
                if (w.Length == 2 && ValueFlags["ssh"].Contains(w[1])) i++;
                continue;
            }
            if (Known(HostOf(w), hosts) is not { } alias) return null;
            var remote = i + 1 < words.Count ? string.Join(' ', words.Skip(i + 1)) : null;
            return new ShellSshHit("ssh", alias, remote, "");
        }
        return null;
    }

    private static ShellSshHit? Copy(string program, List<string> words, int start, IReadOnlyList<SshHost> hosts)
    {
        var flags = ValueFlags[program];
        var first = true;
        for (var i = start; i < words.Count; i++)
        {
            var w = words[i];
            if (w.StartsWith('-') && w.Length > 1)
            {
                if (w.Length == 2 && flags.Contains(w[1])) i++;
                continue;
            }
            // scp: [user@]host:path (or scp://); sftp: its first word is the destination, with or without :path
            var colon = w.IndexOf(':');
            var target = w.StartsWith("scp://", StringComparison.OrdinalIgnoreCase) || w.StartsWith("sftp://", StringComparison.OrdinalIgnoreCase) ? w
                : colon > 0 && !IsDrive(w, colon) ? w[..colon]
                : program == "sftp" && first ? w : null;
            first = false;
            if (target is not null && Known(HostOf(target), hosts) is { } alias) return new ShellSshHit(program, alias, null, "");
        }
        return null;
    }

    /// <summary><c>C:\x</c> / <c>C:/x</c> is a local Windows path, not host <c>C</c>.</summary>
    private static bool IsDrive(string w, int colon) => colon == 1 && char.IsLetter(w[0]) && (w.Length == 2 || w[2] is '\\' or '/');

    /// <summary>The host of <c>[user@]host[:port]</c> or <c>ssh://[user@]host[:port][/path]</c>.</summary>
    internal static string HostOf(string target)
    {
        var t = target;
        var scheme = t.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0)
        {
            t = t[(scheme + 3)..];
            var slash = t.IndexOf('/');
            if (slash >= 0) t = t[..slash];
            var port = t.LastIndexOf(':');
            if (port >= 0 && !t.EndsWith(']')) t = t[..port];
        }
        var at = t.LastIndexOf('@');
        if (at >= 0) t = t[(at + 1)..];
        return t.Trim('[', ']');
    }

    /// <summary>The alias a host is known by: an alias itself, or the HostName of one.</summary>
    private static string? Known(string host, IReadOnlyList<SshHost> hosts)
    {
        if (host.Length == 0) return null;
        return hosts.FirstOrDefault(h => h.Alias.Equals(host, StringComparison.OrdinalIgnoreCase))?.Alias
               ?? hosts.FirstOrDefault(h => h.HostName is { } n && n.Equals(host, StringComparison.OrdinalIgnoreCase))?.Alias;
    }

    private static string? Program(string word)
    {
        var w = word.Trim('"', '\'');
        var slash = Math.Max(w.LastIndexOf('/'), w.LastIndexOf('\\'));
        if (slash >= 0) w = w[(slash + 1)..];
        if (w.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) w = w[..^4];
        return Programs.FirstOrDefault(p => p.Equals(w, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsAssignment(string w)
    {
        var eq = w.IndexOf('=');
        return eq > 0 && w[..eq].All(c => char.IsLetterOrDigit(c) || c == '_');
    }

    /// <summary>The command split where a shell starts another one: new lines, <c>;</c>, <c>&amp;&amp;</c>, <c>||</c>, <c>|</c>, <c>&amp;</c> (not inside quotes, not <c>2&gt;&amp;1</c>).</summary>
    internal static List<string> Parts(string command)
    {
        var parts = new List<string>();
        var sb = new StringBuilder();
        char quote = '\0';
        for (var i = 0; i < command.Length; i++)
        {
            var c = command[i];
            if (quote != '\0')
            {
                sb.Append(c);
                if (c == quote) quote = '\0';
                continue;
            }
            if (c is '"' or '\'') { quote = c; sb.Append(c); continue; }
            var redirect = c == '&' && i > 0 && command[i - 1] == '>';
            if (c is '\n' or '\r' or ';' or '|' || (c == '&' && !redirect))
            {
                parts.Add(sb.ToString());
                sb.Clear();
                continue;
            }
            sb.Append(c);
        }
        parts.Add(sb.ToString());
        return [.. parts.Where(p => !string.IsNullOrWhiteSpace(p))];
    }

    /// <summary>A part's words, quotes taken off (a quoted string is one word).</summary>
    internal static List<string> Words(string part)
    {
        var words = new List<string>();
        var sb = new StringBuilder();
        char quote = '\0';
        var any = false;
        foreach (var c in part)
        {
            if (quote != '\0')
            {
                if (c == quote) quote = '\0';
                else sb.Append(c);
                continue;
            }
            if (c is '"' or '\'') { quote = c; any = true; continue; }
            if (char.IsWhiteSpace(c))
            {
                if (sb.Length > 0 || any) words.Add(sb.ToString());
                sb.Clear();
                any = false;
                continue;
            }
            sb.Append(c);
        }
        if (sb.Length > 0 || any) words.Add(sb.ToString());
        return words;
    }
}
