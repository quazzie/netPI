using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NetPI.Tools.Ssh;

/// <summary>
/// Remote work over the system OpenSSH client (category "ssh"): <c>ssh_hosts</c>, <c>ssh_run</c>, <c>ssh_read</c>,
/// <c>ssh_write</c>, <c>ssh_edit</c>, <c>ssh_copy</c>. Scripts and file contents go through ssh's stdin, never through
/// arguments, so nothing the agent sends needs quoting. Hosts are the aliases in the user's ssh config (read on every
/// call); unknown host keys are rejected and nothing prompts (BatchMode).
/// </summary>
[NetPiPlugin("netpi.tools.ssh", Name = "SSH tools", Description = "ssh_run, ssh_read, ssh_write, ssh_edit, ssh_copy on the hosts in ~/.ssh/config", Order = 22)]
public sealed class SshPlugin : INetPiPlugin
{
    public Task StartAsync(IPluginContext context, CancellationToken ct)
    {
        foreach (var tool in SshToolSet.Create(context, new ProcessLauncher())) context.Tools.Register(tool);
        return Task.CompletedTask;
    }
}

internal static class SshToolSet
{
    public static IAgentTool[] Create(IPluginContext ctx, ISshLauncher launcher) =>
    [
        new SshHostsTool(ctx),
        new SshRunTool(ctx, launcher),
        new SshReadTool(ctx, launcher),
        new SshWriteTool(ctx, launcher),
        new SshEditTool(ctx, launcher),
        new SshCopyTool(ctx, launcher),
    ];
}

internal abstract class SshToolBase(IPluginContext ctx, ISshLauncher launcher) : IAgentTool
{
    internal const int MaxReadBytes = 8 * 1024 * 1024;
    protected IPluginContext Ctx => ctx;
    protected ISshLauncher Launcher => launcher;
    public abstract ToolDefinition Definition { get; }

    public async Task<ToolResult> ExecuteAsync(ToolContext context, JsonElement args, CancellationToken ct)
    {
        args = A.Unwrap(args);
        var o = SshOptions.Read(ctx.Settings);
        SshHost? host = null;
        if (this is not SshHostsTool)
        {
            var name = A.Str(args, "host", "server", "alias")?.Trim();
            var hosts = SshConfig.Read(o.Config);
            if (string.IsNullOrEmpty(name))
                return ToolResult.Error($"{Definition.Name} needs a host: one of {Names(hosts)}.");
            host = hosts.FirstOrDefault(h => h.Alias.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (host is null)
                return ToolResult.Error(hosts.Count == 0
                    ? $"No hosts: {o.Config} has no Host entries. Add the host there first."
                    : $"Unknown host \"{name}\". The hosts are the aliases in {o.Config}: {Names(hosts)}.");
        }
        try { return await RunAsync(context, args, o, host!, ct).ConfigureAwait(false); }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return ToolResult.Error($"Could not start {o.Ssh}: {ex.Message}. Install OpenSSH or set ssh.path in the settings.");
        }
    }

    protected abstract Task<ToolResult> RunAsync(ToolContext context, JsonElement args, SshOptions o, SshHost host, CancellationToken ct);

    private static string Names(List<SshHost> hosts) => hosts.Count == 0 ? "(none)" : string.Join(", ", hosts.Select(h => h.Alias));

    /// <summary>ssh [common options] -T -- host remoteCommand</summary>
    protected Task<SshExec> Ssh(SshOptions o, SshHost host, string remoteCommand, byte[]? stdin, TimeSpan timeout, CancellationToken ct,
        Action<string>? onStdout = null, Action<string>? onStderr = null)
    {
        var args = o.CommonArgs();
        args.AddRange(["-T", "--", host.Alias, remoteCommand]);
        return launcher.RunAsync(o.Ssh, args, stdin, null, onStdout, onStderr, timeout, ct);
    }

    /// <summary>ssh's own failures (exit 255) with a hint for the common ones.</summary>
    protected static string Failure(SshHost host, SshExec r)
    {
        var err = r.Stderr.Trim().TrimEnd('.');
        var hint = err.Contains("Host key verification failed", StringComparison.OrdinalIgnoreCase) || err.Contains("No ED25519 host key is known", StringComparison.OrdinalIgnoreCase) || err.Contains("host key is known", StringComparison.OrdinalIgnoreCase)
            ? $" The host key of {host.Alias} is not in known_hosts: the user has to connect once (ssh {host.Alias}) to accept it."
            : err.Contains("Permission denied", StringComparison.OrdinalIgnoreCase)
                ? $" The key for {host.Alias} was not accepted."
                : "";
        return $"ssh to {host.Alias} failed: {(err.Length > 0 ? err : "exit code 255")}.{hint}";
    }

    protected static string Where(SshHost host, string path) => $"{host.Alias}:{path}";
}

// ---------------------------------------------------------------------------------------------------------------- hosts

internal sealed class SshHostsTool(IPluginContext ctx) : SshToolBase(ctx, new ProcessLauncher())
{
    public override ToolDefinition Definition { get; } = new()
    {
        Name = "ssh_hosts",
        Label = "SSH hosts",
        Category = "ssh",
        ReadOnly = true,
        Description = "List the remote hosts the ssh_* tools can use: the aliases in the user's ~/.ssh/config, with user and address.",
        PromptGuidelines = ["For work on other machines use the ssh_* tools rather than ssh in bash: scripts and file contents go through as they are, without quoting."],
    };

    protected override Task<ToolResult> RunAsync(ToolContext context, JsonElement args, SshOptions o, SshHost host, CancellationToken ct)
    {
        var hosts = SshConfig.Read(o.Config);
        if (hosts.Count == 0) return Task.FromResult(ToolResult.Ok($"No hosts: {o.Config} has no Host entries.", new { hosts = Array.Empty<object>() }));
        var lines = hosts.Select(h => $"{h.Alias}: {(h.User is null ? "" : h.User + "@")}{h.HostName ?? h.Alias}{(h.Port is { } p ? $":{p}" : "")}");
        return Task.FromResult(ToolResult.Ok($"Hosts ({o.Config}):\n" + string.Join('\n', lines),
            new { hosts = hosts.Select(h => new { alias = h.Alias, hostName = h.HostName, user = h.User, port = h.Port }).ToArray() }));
    }
}

// ---------------------------------------------------------------------------------------------------------------- run

internal sealed class SshRunTool(IPluginContext ctx, ISshLauncher launcher) : SshToolBase(ctx, launcher)
{
    private const string PgidMarker = "__netpi_pgid=";
    private const int TailLines = 2000; // like bash
    private const int TailChars = 30 * 1024;

    public override ToolDefinition Definition { get; } = new()
    {
        Name = "ssh_run",
        Label = "SSH",
        Category = "ssh",
        SummaryArg = "script",
        Description =
            "Run a bash script on a remote host (an alias from ssh_hosts). The script is sent as it is: quotes, $, " +
            "backslashes and heredocs need no escaping, and it can be long. cwd sets the remote working directory (default " +
            "the home folder). stdout and stderr are merged; the exit code is reported when it is not 0. timeout (default " +
            "120 s, max 1800) ends the whole remote process tree, and so does stopping the run.",
        Parameters = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["host"] = new JsonObject { ["type"] = "string", ["description"] = "Host alias" },
                ["script"] = new JsonObject { ["type"] = "string", ["description"] = "Bash script, any length" },
                ["cwd"] = new JsonObject { ["type"] = "string", ["description"] = "Remote working directory" },
                ["timeout"] = new JsonObject { ["type"] = "integer", ["description"] = "Seconds (default 120, max 1800)" },
            },
            ["required"] = new JsonArray("host", "script"),
        },
    };

    protected override async Task<ToolResult> RunAsync(ToolContext context, JsonElement args, SshOptions o, SshHost host, CancellationToken ct)
    {
        var script = A.Str(args, "script", "command", "cmd", "code");
        if (string.IsNullOrWhiteSpace(script)) return ToolResult.Error("ssh_run needs a script.");
        var cwd = A.Str(args, "cwd", "dir", "directory", "workdir");
        var timeout = Math.Clamp(A.Int(args, "timeout", "timeout_seconds") ?? o.Timeout, 1, 1800);

        // The remote login shell only sees this fixed line: it saves stdin (the script) to a temp file and runs it in its
        // own session (setsid) under timeout, which ends the whole process group; the first output line is its PGID.
        var remote =
            "t=$(mktemp) && cat >\"$t\" && setsid --wait bash -c 'echo " + PgidMarker + "$$; exec timeout -k 5 " + timeout +
            " bash \"$0\" </dev/null 2>&1' \"$t\"; c=$?; rm -f \"$t\"; exit $c";
        var body = Sh.Cd(cwd) + "\n" + Sh.Lf(script).TrimStart('\uFEFF');
        if (!body.EndsWith('\n')) body += "\n";

        var pgid = (string?)null;
        var head = new StringBuilder();
        var passed = false;
        void Live(string chunk)
        {
            // hide the PGID line from the live output
            if (passed) { context.Output?.Invoke(chunk); return; }
            head.Append(chunk);
            var text = head.ToString();
            var nl = text.IndexOf('\n');
            if (nl < 0 && text.Length < 64) return;
            passed = true;
            if (text.StartsWith(PgidMarker, StringComparison.Ordinal) && nl > 0)
            {
                pgid = text[PgidMarker.Length..nl].Trim();
                text = text[(nl + 1)..];
            }
            if (text.Length > 0) context.Output?.Invoke(text);
        }
        var started = DateTime.UtcNow;
        var r = await Ssh(o, host, remote, Encoding.UTF8.GetBytes(body), TimeSpan.FromSeconds(timeout + 20), ct, Live, s => context.Output?.Invoke(s))
            .ConfigureAwait(false);
        var durationMs = (long)(DateTime.UtcNow - started).TotalMilliseconds;

        var stdout = r.Stdout;
        if (stdout.StartsWith(PgidMarker, StringComparison.Ordinal))
        {
            var nl = stdout.IndexOf('\n');
            pgid ??= nl > 0 ? stdout[PgidMarker.Length..nl].Trim() : null;
            stdout = nl >= 0 ? stdout[(nl + 1)..] : "";
        }
        if ((r.Aborted || r.TimedOut) && pgid is not null && int.TryParse(pgid, out var group) && group > 1)
            _ = KillGroupAsync(o, host, group);

        var ssh = r.ExitCode == 255 && !r.Aborted && !r.TimedOut && pgid is null;
        var output = Resolve(stdout + (r.Stderr.Length > 0 && !ssh ? (stdout.Length > 0 && !stdout.EndsWith('\n') ? "\n" : "") + r.Stderr : ""));
        var (tail, truncated, total, shown) = Tail(output);
        truncated |= r.Cut;
        var fullOutputPath = truncated && !ssh ? Save(host, output) : null;
        var timedOut = r.TimedOut || r.ExitCode == 124;
        var details = new
        {
            host = host.Alias,
            command = script,
            shell = "ssh",
            cwd,
            exitCode = r.Aborted ? (int?)null : r.ExitCode,
            durationMs,
            truncated,
            fullOutputPath,
            timedOut = timedOut ? true : (bool?)null,
            aborted = r.Aborted ? true : (bool?)null,
        };
        if (ssh) return ToolResult.Error(Failure(host, r), details);

        var sb = new StringBuilder();
        if (truncated)
        {
            sb.Append(r.Cut
                ? $"[Output truncated: showing the last {shown} lines; only the last {ProcessLauncher.MaxChars / 1024 / 1024} MB of the output were kept."
                : $"[Output truncated: showing the last {shown} lines of {total}.");
            sb.Append(fullOutputPath is not null ? $" {(r.Cut ? "Those are" : "Full output")} saved to {fullOutputPath} (use read or grep on it).]\n" : "]\n");
        }
        sb.Append(tail.Length > 0 ? tail : "(no output)");
        if (timedOut) sb.Append($"\n[timed out after {timeout}s; the remote process group was ended. Use a longer timeout for long-running work.]");
        else if (r.Aborted) sb.Append("\n[aborted; the remote process group is being ended]");
        else if (r.ExitCode == 125 && output.Contains("cd:", StringComparison.Ordinal)) sb.Append($"\n[the working directory {cwd} does not exist on {host.Alias}]");
        else if (r.ExitCode != 0) sb.Append($"\n[exit code {r.ExitCode}]");
        return new ToolResult { Content = sb.ToString(), IsError = timedOut || r.Aborted, Details = details };
    }

    private async Task KillGroupAsync(SshOptions o, SshHost host, int group)
    {
        try
        {
            await Ssh(o, host, $"kill -TERM -- -{group} 2>/dev/null; sleep 2; kill -KILL -- -{group} 2>/dev/null; true", null,
                TimeSpan.FromSeconds(20), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception) { }
    }

    /// <summary>The whole output, for read/grep, when the result shows only its tail (in the temp folder, like bash's logs).</summary>
    private static string? Save(SshHost host, string output)
    {
        try
        {
            var dir = Path.Combine(Path.GetTempPath(), "netpi");
            Directory.CreateDirectory(dir);
            var name = string.Concat(host.Alias.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_'));
            var path = Path.Combine(dir, $"ssh-{name}-{DateTime.Now:yyyyMMdd-HHmmss-fff}.log");
            File.WriteAllText(path, output);
            return path;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>Progress lines: keep what follows the last carriage return of each line.</summary>
    private static string Resolve(string s)
    {
        if (!s.Contains('\r')) return s;
        var lines = s.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var cr = lines[i].LastIndexOf('\r');
            if (cr >= 0) lines[i] = lines[i][(cr + 1)..];
        }
        return string.Join('\n', lines);
    }

    private static (string Tail, bool Truncated, int Total, int Shown) Tail(string s)
    {
        s = s.TrimEnd('\n');
        var lines = s.Split('\n');
        if (lines.Length <= TailLines && s.Length <= TailChars) return (s, false, lines.Length, lines.Length);
        var keep = new List<string>();
        var size = 0;
        for (var i = lines.Length - 1; i >= 0 && keep.Count < TailLines && size + lines[i].Length + 1 <= TailChars; i--)
        {
            keep.Add(lines[i]);
            size += lines[i].Length + 1;
        }
        keep.Reverse();
        return (string.Join('\n', keep), true, lines.Length, keep.Count);
    }
}

// ---------------------------------------------------------------------------------------------------------------- read

internal sealed class SshReadTool(IPluginContext ctx, ISshLauncher launcher) : SshToolBase(ctx, launcher)
{
    public const int MaxLines = 2000;
    public const int MaxChars = 50 * 1024;
    private const string StatMarker = "__netpi_stat=";

    public override ToolDefinition Definition { get; } = new()
    {
        Name = "ssh_read",
        Label = "SSH read",
        Category = "ssh",
        ReadOnly = true,
        SummaryArg = "path",
        Description =
            $"Read a text file on a remote host (an alias from ssh_hosts), like read: at most {MaxLines} lines / {MaxChars / 1024}KB per " +
            "call; page with offset (1-based; negative counts from the end) and limit. Relative paths start at cwd or the home folder.",
        Parameters = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["host"] = new JsonObject { ["type"] = "string" },
                ["path"] = new JsonObject { ["type"] = "string" },
                ["offset"] = new JsonObject { ["type"] = "integer" },
                ["limit"] = new JsonObject { ["type"] = "integer" },
                ["cwd"] = new JsonObject { ["type"] = "string" },
            },
            ["required"] = new JsonArray("host", "path"),
        },
    };

    protected override async Task<ToolResult> RunAsync(ToolContext context, JsonElement args, SshOptions o, SshHost host, CancellationToken ct)
    {
        var path = A.Str(args, "path", "file", "file_path")?.Trim();
        if (string.IsNullOrEmpty(path)) return ToolResult.Error("ssh_read needs a path.");
        var file = await Fetch(o, host, path, A.Str(args, "cwd"), ct).ConfigureAwait(false);
        if (file.Error is not null) return ToolResult.Error(file.Error);
        var text = file.Text!;
        if (text.Contains('\0'))
            return ToolResult.Error($"{Where(host, path)} appears to be a binary file ({file.Size} bytes); ssh_read only shows text. Use ssh_copy to download it.");

        var lines = text.Replace("\r\n", "\n").Split('\n');
        var total = text.EndsWith('\n') ? lines.Length - 1 : lines.Length;
        if (total == 0) return ToolResult.Ok("(empty file)", Details(host, path, 0, 0, 0, false, file.Size));
        var offset = A.Int(args, "offset", "start_line", "line") ?? 1;
        if (offset < 0) offset = Math.Max(1, total + offset + 1);
        if (offset == 0) offset = 1;
        if (offset > total) return ToolResult.Error($"offset {offset} is past the end of the file: {Where(host, path)} has {total} lines.");
        var limit = Math.Clamp(A.Int(args, "limit", "lines", "count") ?? MaxLines, 1, MaxLines);
        var sb = new StringBuilder();
        var taken = 0;
        for (var i = offset - 1; i < total && taken < limit; i++)
        {
            if (sb.Length + lines[i].Length + 1 > MaxChars && taken > 0) break;
            if (taken > 0) sb.Append('\n');
            sb.Append(lines[i]);
            taken++;
        }
        var end = offset + taken - 1;
        var more = end < total || file.Cut;
        if (more) sb.Append($"\n\n[Showing lines {offset}-{end} of {total}{(file.Cut ? "+ (the file is larger than " + MaxReadBytes / 1024 / 1024 + " MB)" : "")}. Use offset={end + 1} to continue.]");
        return ToolResult.Ok(sb.ToString(), Details(host, path, offset, end, total, more, file.Size));
    }

    private static object Details(SshHost host, string path, int start, int end, int total, bool truncated, long size) =>
        new { host = host.Alias, path = Where(host, path), startLine = start, endLine = end, totalLines = total, truncated, bytes = size };

    internal sealed record Fetched(string? Text, long Size, string Stat, bool Cut, string? Error);

    /// <summary>The file's text (up to <see cref="SshToolBase.MaxReadBytes"/>) and its <c>size mtime</c> stat.</summary>
    internal async Task<Fetched> Fetch(SshOptions o, SshHost host, string path, string? cwd, CancellationToken ct)
    {
        var remote = Sh.Cd(cwd) + $"p={Sh.Path(path)}; if [ ! -e \"$p\" ]; then echo \"No such file: $p\" >&2; exit 2; fi; " +
                     "if [ -d \"$p\" ]; then echo \"$p is a directory\" >&2; exit 3; fi; " +
                     $"stat -c '{StatMarker}%s %Y' -- \"$p\" && head -c {MaxReadBytes} -- \"$p\"";
        var r = await Ssh(o, host, remote, null, TimeSpan.FromSeconds(Math.Max(60, o.Timeout)), ct).ConfigureAwait(false);
        if (r.ExitCode == 255) return new Fetched(null, 0, "", false, Failure(host, r));
        if (r.ExitCode == 125) return new Fetched(null, 0, "", false, $"The working directory {cwd} does not exist on {host.Alias}.");
        if (r.ExitCode != 0) return new Fetched(null, 0, "", false, r.Stderr.Trim() is { Length: > 0 } e ? $"{host.Alias}: {e}" : $"Reading {Where(host, path)} failed (exit {r.ExitCode}).");
        var nl = r.Stdout.IndexOf('\n');
        if (!r.Stdout.StartsWith(StatMarker, StringComparison.Ordinal) || nl < 0) return new Fetched(null, 0, "", false, $"Unexpected reply reading {Where(host, path)}.");
        var stat = r.Stdout[StatMarker.Length..nl].Trim();
        long.TryParse(stat.Split(' ')[0], out var size);
        return new Fetched(r.Stdout[(nl + 1)..], size, stat, size > MaxReadBytes, null);
    }
}

// ---------------------------------------------------------------------------------------------------------------- write

internal sealed class SshWriteTool(IPluginContext ctx, ISshLauncher launcher) : SshToolBase(ctx, launcher)
{
    public override ToolDefinition Definition { get; } = new()
    {
        Name = "ssh_write",
        Label = "SSH write",
        Category = "ssh",
        SummaryArg = "path",
        Description =
            "Write a file on a remote host (an alias from ssh_hosts). The content is sent as it is, any size, with no escaping. " +
            "Parent folders are created; the file keeps its permissions; append adds to the end instead of replacing.",
        Parameters = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["host"] = new JsonObject { ["type"] = "string" },
                ["path"] = new JsonObject { ["type"] = "string" },
                ["content"] = new JsonObject { ["type"] = "string" },
                ["append"] = new JsonObject { ["type"] = "boolean" },
                ["cwd"] = new JsonObject { ["type"] = "string" },
            },
            ["required"] = new JsonArray("host", "path", "content"),
        },
    };

    protected override async Task<ToolResult> RunAsync(ToolContext context, JsonElement args, SshOptions o, SshHost host, CancellationToken ct)
    {
        var path = A.Str(args, "path", "file", "file_path")?.Trim();
        if (string.IsNullOrEmpty(path)) return ToolResult.Error("ssh_write needs a path.");
        var content = A.Str(args, "content", "text", "data");
        if (content is null) return ToolResult.Error("ssh_write needs content (an empty string empties the file).");
        var append = A.Bool(args, "append") ?? false;
        var remote = Sh.Cd(A.Str(args, "cwd")) +
                     $"p={Sh.Path(path)}; if [ -e \"$p\" ]; then echo existed; fi; mkdir -p -- \"$(dirname -- \"$p\")\" && cat {(append ? ">>" : ">")}\"$p\"";
        var bytes = Encoding.UTF8.GetBytes(content);
        var r = await Ssh(o, host, remote, bytes, TimeSpan.FromSeconds(Math.Max(60, o.Timeout)), ct).ConfigureAwait(false);
        if (r.ExitCode == 255) return ToolResult.Error(Failure(host, r));
        if (r.ExitCode == 125) return ToolResult.Error($"The working directory does not exist on {host.Alias}.");
        if (r.ExitCode != 0) return ToolResult.Error($"Writing {Where(host, path)} failed: {r.Stderr.Trim()}");
        var created = !r.Stdout.Contains("existed", StringComparison.Ordinal);
        var lines = content.Length == 0 ? 0 : content.Count(c => c == '\n') + (content.EndsWith('\n') ? 0 : 1);
        var verb = append ? "Appended" : created ? "Created" : "Wrote";
        return ToolResult.Ok($"{verb} {Where(host, path)} ({bytes.Length} bytes, {lines} lines).",
            new { host = host.Alias, path = Where(host, path), created, append, bytes = bytes.Length, lines });
    }
}

// ---------------------------------------------------------------------------------------------------------------- edit

internal sealed class SshEditTool(IPluginContext ctx, ISshLauncher launcher) : SshToolBase(ctx, launcher)
{
    public override ToolDefinition Definition { get; } = new()
    {
        Name = "ssh_edit",
        Label = "SSH edit",
        Category = "ssh",
        SummaryArg = "path",
        Description =
            "Edit a file on a remote host (an alias from ssh_hosts) with exact replacements, like edit: each oldText must match " +
            "exactly once (replace_all replaces every match). Line endings (CRLF/LF) are kept. Fails without writing if the file " +
            "changed on the host while it was being edited.",
        Parameters = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["host"] = new JsonObject { ["type"] = "string" },
                ["path"] = new JsonObject { ["type"] = "string" },
                ["edits"] = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["oldText"] = new JsonObject { ["type"] = "string" },
                            ["newText"] = new JsonObject { ["type"] = "string" },
                            ["replace_all"] = new JsonObject { ["type"] = "boolean" },
                        },
                        ["required"] = new JsonArray("oldText", "newText"),
                    },
                },
                ["cwd"] = new JsonObject { ["type"] = "string" },
            },
            ["required"] = new JsonArray("host", "path", "edits"),
        },
    };

    protected override async Task<ToolResult> RunAsync(ToolContext context, JsonElement args, SshOptions o, SshHost host, CancellationToken ct)
    {
        var path = A.Str(args, "path", "file", "file_path")?.Trim();
        if (string.IsNullOrEmpty(path)) return ToolResult.Error("ssh_edit needs a path.");
        var edits = Edits(args);
        if (edits.Count == 0) return ToolResult.Error("ssh_edit needs edits: [{ \"oldText\", \"newText\" }].");
        var cwd = A.Str(args, "cwd");

        var reader = new SshReadTool(Ctx, Launcher);
        var file = await reader.Fetch(o, host, path, cwd, ct).ConfigureAwait(false);
        if (file.Error is not null) return ToolResult.Error(file.Error);
        if (file.Cut) return ToolResult.Error($"{Where(host, path)} is larger than {MaxReadBytes / 1024 / 1024} MB; edit it with ssh_run (sed, python) instead.");
        if (file.Text!.Contains('\0')) return ToolResult.Error($"{Where(host, path)} is a binary file.");
        if (file.Text.Contains('�'))
            return ToolResult.Error($"{Where(host, path)} is not valid UTF-8; writing it back would change its bytes. Edit it with ssh_run (sed, python) instead.");

        EditOutcome outcome;
        try { outcome = TextEdits.Apply(file.Text, edits, path); }
        catch (EditException ex) { return ToolResult.Error(ex.Message); }

        // write back only if the file is still what was read (size and mtime)
        var remote = Sh.Cd(cwd) + $"p={Sh.Path(path)}; s=$(stat -c '%s %Y' -- \"$p\") || exit 2; " +
                     $"[ \"$s\" = {Sh.Quote(file.Stat)} ] || {{ echo \"changed: $s\" >&2; exit 3; }}; cat >\"$p\"";
        var r = await Ssh(o, host, remote, Encoding.UTF8.GetBytes(outcome.Text), TimeSpan.FromSeconds(Math.Max(60, o.Timeout)), ct).ConfigureAwait(false);
        if (r.ExitCode == 255) return ToolResult.Error(Failure(host, r));
        if (r.ExitCode == 3) return ToolResult.Error($"{Where(host, path)} changed on the host while it was being edited; nothing was written. Read it again and redo the edit.");
        if (r.ExitCode != 0) return ToolResult.Error($"Writing {Where(host, path)} failed: {r.Stderr.Trim()}");

        return ToolResult.Ok($"Applied {outcome.Replacements} edit{(outcome.Replacements == 1 ? "" : "s")} to {Where(host, path)} (+{outcome.Added} −{outcome.Removed})",
            new
            {
                host = host.Alias,
                path = Where(host, path),
                diff = outcome.Diff,
                added = outcome.Added,
                removed = outcome.Removed,
                edits = outcome.Replacements,
                firstChangedLine = outcome.FirstChangedLine,
                eol = file.Text.Contains("\r\n", StringComparison.Ordinal) ? "crlf" : "lf",
            });
    }

    private static List<TextEdit> Edits(JsonElement args)
    {
        var list = new List<TextEdit>();
        var replaceAllTop = A.Bool(args, "replace_all", "replaceAll") ?? false;
        if (A.Get(args, "edits", "changes") is { ValueKind: JsonValueKind.Array } arr)
        {
            foreach (var e in arr.EnumerateArray())
                if (A.Str(e, "oldText", "old_text", "old", "search") is { } old && A.Str(e, "newText", "new_text", "new", "replace") is { } neu)
                    list.Add(new TextEdit(old, neu, A.Bool(e, "replace_all", "replaceAll") ?? replaceAllTop));
        }
        else if (A.Str(args, "oldText", "old_text", "old") is { } old && A.Str(args, "newText", "new_text", "new") is { } neu)
            list.Add(new TextEdit(old, neu, replaceAllTop));
        return list;
    }
}

// ---------------------------------------------------------------------------------------------------------------- copy

internal sealed class SshCopyTool(IPluginContext ctx, ISshLauncher launcher) : SshToolBase(ctx, launcher)
{
    public override ToolDefinition Definition { get; } = new()
    {
        Name = "ssh_copy",
        Label = "SCP",
        Category = "ssh",
        SummaryArg = "from",
        Description =
            "Copy files or folders between this machine and a remote host (an alias from ssh_hosts) with scp: direction " +
            "upload (from = local path, to = remote path) or download (from = remote, to = local). recursive for folders. " +
            "Use it for large or binary files.",
        Parameters = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["host"] = new JsonObject { ["type"] = "string" },
                ["direction"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("upload", "download") },
                ["from"] = new JsonObject { ["type"] = "string" },
                ["to"] = new JsonObject { ["type"] = "string" },
                ["recursive"] = new JsonObject { ["type"] = "boolean" },
            },
            ["required"] = new JsonArray("host", "direction", "from", "to"),
        },
    };

    protected override async Task<ToolResult> RunAsync(ToolContext context, JsonElement args, SshOptions o, SshHost host, CancellationToken ct)
    {
        var direction = (A.Str(args, "direction", "mode") ?? "").Trim().ToLowerInvariant();
        var from = A.Str(args, "from", "source", "src")?.Trim();
        var to = A.Str(args, "to", "destination", "dest", "target")?.Trim();
        if (direction is not ("upload" or "download")) return ToolResult.Error("ssh_copy needs direction: upload or download.");
        if (string.IsNullOrEmpty(from) || string.IsNullOrEmpty(to)) return ToolResult.Error("ssh_copy needs from and to.");
        var recursive = A.Bool(args, "recursive", "r") ?? false;

        // scp reads "C:\x" as host "C": run it in the local folder and pass a relative name
        string local, workDir, localArg;
        if (direction == "upload")
        {
            local = context.ResolvePath(from);
            if (!File.Exists(local) && !Directory.Exists(local)) return ToolResult.Error($"No such local file or folder: {local}");
            if (Directory.Exists(local) && !recursive) return ToolResult.Error($"{local} is a folder: set recursive to copy it.");
            workDir = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(local)) ?? local;
            localArg = "./" + Path.GetFileName(Path.TrimEndingDirectorySeparator(local));
        }
        else
        {
            local = context.ResolvePath(to);
            var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(local)) ?? local;
            Directory.CreateDirectory(parent);
            workDir = parent;
            localArg = "./" + Path.GetFileName(Path.TrimEndingDirectorySeparator(local));
        }
        var remote = $"{host.Alias}:{(direction == "upload" ? to : from)}";
        var scpArgs = new List<string> { "-q", "-p" };
        if (recursive) scpArgs.Add("-r");
        scpArgs.AddRange(o.CommonArgs());
        scpArgs.Add("--");
        if (direction == "upload") scpArgs.AddRange([localArg, remote]);
        else scpArgs.AddRange([remote, localArg]);

        var r = await Launcher.RunAsync(o.Scp, scpArgs, null, workDir, null, null, TimeSpan.FromSeconds(Math.Max(600, o.Timeout)), ct).ConfigureAwait(false);
        if (r.Aborted) return ToolResult.Error("[aborted]");
        if (r.TimedOut) return ToolResult.Error($"scp timed out after {Math.Max(600, o.Timeout)}s.");
        if (r.ExitCode != 0) return ToolResult.Error($"scp {(direction == "upload" ? "to" : "from")} {host.Alias} failed: {(r.Stderr.Trim() is { Length: > 0 } e ? e : $"exit code {r.ExitCode}")}");
        var bytes = File.Exists(local) ? new FileInfo(local).Length : Directory.Exists(local) ? Size(local) : 0;
        var text = direction == "upload" ? $"Uploaded {local} to {remote}" : $"Downloaded {remote} to {local}";
        return ToolResult.Ok($"{text} ({bytes} bytes).", new { host = host.Alias, direction, from, to, local, remote, bytes, recursive });
    }

    private static long Size(string dir)
    {
        try { return new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length); }
        catch (Exception) { return 0; }
    }
}

// ---------------------------------------------------------------------------------------------------------------- args

/// <summary>Lenient argument access: names match ignoring case, '_' and '-'; numbers/bools may be strings.</summary>
internal static class A
{
    private static string Norm(string s) => s.Replace("_", "").Replace("-", "").ToLowerInvariant();

    public static JsonElement Unwrap(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.String) return args;
        try
        {
            using var doc = JsonDocument.Parse(args.GetString() ?? "{}");
            return doc.RootElement.Clone();
        }
        catch (JsonException) { return args; }
    }

    public static JsonElement? Get(JsonElement args, params string[] names)
    {
        if (args.ValueKind != JsonValueKind.Object) return null;
        foreach (var name in names)
            foreach (var p in args.EnumerateObject())
                if (Norm(p.Name) == Norm(name) && p.Value.ValueKind != JsonValueKind.Null) return p.Value;
        return null;
    }

    public static string? Str(JsonElement args, params string[] names) => Get(args, names) switch
    {
        { ValueKind: JsonValueKind.String } v => v.GetString(),
        { ValueKind: JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False } v => v.GetRawText(),
        _ => null,
    };

    public static int? Int(JsonElement args, params string[] names) => Get(args, names) switch
    {
        { ValueKind: JsonValueKind.Number } v when v.TryGetDouble(out var d) => (int)Math.Clamp(d, int.MinValue, int.MaxValue),
        { ValueKind: JsonValueKind.String } v when int.TryParse(v.GetString(), out var i) => i,
        _ => null,
    };

    public static bool? Bool(JsonElement args, params string[] names) => Get(args, names) switch
    {
        { ValueKind: JsonValueKind.True } => true,
        { ValueKind: JsonValueKind.False } => false,
        { ValueKind: JsonValueKind.String } v when bool.TryParse(v.GetString(), out var b) => b,
        { ValueKind: JsonValueKind.Number } v => v.GetDouble() != 0,
        _ => null,
    };
}
