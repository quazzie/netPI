using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NetPI.Tools.Ssh;

// ---------------------------------------------------------------------------------------------------------------- background jobs

/// <summary>
/// The remote side of <c>run { background: true }</c> and <c>job</c>. A job is three files in <c>~/.cache/netpi-jobs</c> next
/// to its script: <c>&lt;id&gt;.log</c> (stdout and stderr), <c>&lt;id&gt;.pid</c> (the process group's leader, written by the job's
/// own shell) and <c>&lt;id&gt;.exit</c> (the exit code, written, then renamed into place, when the script ends). A job whose
/// process is gone without an exit file (killed from outside, the host restarted) is "lost".
/// </summary>
internal static class SshJobs
{
    /// <summary>The job folder on the host, as a shell word.</summary>
    internal const string Dir = "\"$HOME\"/.cache/netpi-jobs";
    internal const string StatusMarker = "__netpi_job=";
    internal const int DefaultTail = 200;
    internal const int DefaultWaitSeconds = 60;

    private static readonly Regex ValidId = new("^[A-Za-z0-9_-]{1,64}$", RegexOptions.Compiled);

    internal static bool IsValidId(string id) => ValidId.IsMatch(id);

    internal static string NewId() => "j" + Guid.NewGuid().ToString("N")[..10];

    /// <summary>
    /// Saves stdin (the script) as <c>&lt;id&gt;.sh</c> and starts it detached: its own session where <c>setsid</c> exists (a
    /// process-group kill then reaches everything it started), immune to the hang-up when the ssh session ends, every stream
    /// redirected so the ssh call returns at once. Prints <c>__netpi_job=&lt;id&gt;</c>.
    /// </summary>
    internal static string StartCommand(string id, string? cwd) =>
        Sh.Cd(cwd) + $"d={Dir}; mkdir -p -- \"$d\" || exit 4; id={Sh.Quote(id)}; cat >\"$d/$id.sh\" || exit 5; " +
        "if command -v setsid >/dev/null 2>&1; then s=setsid; else s=; fi; " +
        "$s nohup bash -c 'echo $$ >\"$1.pid\"; bash \"$0\" </dev/null >\"$1.log\" 2>&1; c=$?; echo $c >\"$1.exit.tmp\"; mv -f \"$1.exit.tmp\" \"$1.exit\"' " +
        "\"$d/$id.sh\" \"$d/$id\" >/dev/null 2>&1 </dev/null & " +
        $"echo \"{StatusMarker}$id\"";

    /// <summary>Prints the status line (<c>__netpi_job=running|exited N|lost|killed</c>) and sets nothing else.</summary>
    private const string Status =
        "if [ -e \"$d/$id.exit\" ]; then echo \"__netpi_job=exited $(cat \"$d/$id.exit\")\"; " +
        "elif [ -e \"$d/$id.pid\" ] && ! kill -0 \"$(cat \"$d/$id.pid\")\" 2>/dev/null; then echo \"__netpi_job=lost\"; " +
        "else echo \"__netpi_job=running\"; fi; ";

    private static string Prelude(string id) =>
        $"d={Dir}; id={Sh.Quote(id)}; [ -e \"$d/$id.sh\" ] || {{ echo \"No such job: $id\" >&2; exit 2; }}; ";

    internal static string OutputCommand(string id, int tail) =>
        Prelude(id) + Status + $"tail -n {tail} -- \"$d/$id.log\" 2>/dev/null; true";

    internal static string WaitCommand(string id, int tail, int seconds) =>
        Prelude(id) +
        $"i=0; while [ ! -e \"$d/$id.exit\" ] && [ $i -lt {seconds} ]; do " +
        "if [ -e \"$d/$id.pid\" ] && ! kill -0 \"$(cat \"$d/$id.pid\")\" 2>/dev/null; then break; fi; sleep 1; i=$((i+1)); done; " +
        Status + $"tail -n {tail} -- \"$d/$id.log\" 2>/dev/null; true";

    /// <summary>TERM, a pause, KILL, for the job's process group (or its process where it has no group of its own); records 143 when the job left no exit code.</summary>
    internal static string KillCommand(string id, int tail) =>
        Prelude(id) +
        "p=$(cat \"$d/$id.pid\" 2>/dev/null); if [ -z \"$p\" ]; then sleep 1; p=$(cat \"$d/$id.pid\" 2>/dev/null); fi; " +
        "if [ -e \"$d/$id.exit\" ]; then " + Status + $"tail -n {tail} -- \"$d/$id.log\" 2>/dev/null; exit 0; fi; " +
        "[ -n \"$p\" ] || { echo \"No process id was recorded for job $id.\" >&2; exit 3; }; " +
        "kill -TERM -- \"-$p\" 2>/dev/null || kill -TERM \"$p\" 2>/dev/null; sleep 2; " +
        "kill -KILL -- \"-$p\" 2>/dev/null || kill -KILL \"$p\" 2>/dev/null; sleep 0.2; " +
        "[ -e \"$d/$id.exit\" ] || echo 143 >\"$d/$id.exit\"; echo \"__netpi_job=killed\"; " +
        $"tail -n {tail} -- \"$d/$id.log\" 2>/dev/null; true";

    /// <summary>The status line of an answer and the log tail after it.</summary>
    internal static (string Status, string Log) Split(string stdout)
    {
        if (!stdout.StartsWith(StatusMarker, StringComparison.Ordinal)) return ("", stdout);
        var nl = stdout.IndexOf('\n');
        return nl < 0 ? (stdout[StatusMarker.Length..].Trim(), "") : (stdout[StatusMarker.Length..nl].Trim(), stdout[(nl + 1)..]);
    }

    internal static string Describe(string id, string status) => status switch
    {
        "running" => $"[job {id}: running]",
        "lost" => $"[job {id}: lost — its process is gone and left no exit code (killed from outside, or the host restarted)]",
        "killed" => $"[job {id}: killed]",
        _ when status.StartsWith("exited ", StringComparison.Ordinal) => status[7..].Trim() == "0" ? $"[job {id}: finished, exit code 0]" : $"[job {id}: finished, exit code {status[7..].Trim()}]",
        _ => $"[job {id}: {status}]",
    };
}

internal sealed class SshJobTool(IPluginContext ctx, ISshLauncher launcher) : SshToolBase(ctx, launcher)
{
    internal override string Name => "job";
    internal override string Summary =>
        "A background job started with run { background: true }: op output (default; the log's last lines and the job's state), " +
        "wait (until it ends, or timeout seconds, default 60, max 1800) or kill (the job's whole process group). Needs host and id.";

    internal static string OpOf(JsonElement args) => (new ToolArgs(args).Str("op", "operation", "do") ?? "output").Trim().ToLowerInvariant();

    /// <summary>output and wait only read; kill ends a process.</summary>
    internal override bool ReadOnlyFor(JsonElement args) => OpOf(args) != "kill";

    protected override async Task<ToolResult> RunAsync(ToolContext context, JsonElement args, SshOptions o, SshHost host, List<SshHost> hosts, CancellationToken ct)
    {
        var a = new ToolArgs(args);
        var id = a.Str("id", "job", "job_id", "jobId")?.Trim();
        if (string.IsNullOrEmpty(id)) return ToolResult.Error("ssh job needs id (the job id run { background: true } returned).");
        if (!SshJobs.IsValidId(id)) return ToolResult.Error($"\"{id}\" is not a job id (letters, digits, - and _).");
        var op = OpOf(args);
        var tail = Math.Clamp(a.Int("tail", "lines") ?? SshJobs.DefaultTail, 1, 5000);
        var wait = Math.Clamp(a.Int("timeout", "timeout_seconds") ?? SshJobs.DefaultWaitSeconds, 1, 1800);
        string command;
        TimeSpan timeout;
        switch (op)
        {
            case "output": (command, timeout) = (SshJobs.OutputCommand(id, tail), TimeSpan.FromSeconds(Math.Max(60, o.Timeout))); break;
            case "wait": (command, timeout) = (SshJobs.WaitCommand(id, tail, wait), TimeSpan.FromSeconds(wait + 30)); break;
            case "kill": (command, timeout) = (SshJobs.KillCommand(id, tail), TimeSpan.FromSeconds(60)); break;
            default: return ToolResult.Error($"Unknown op \"{op}\": output, wait or kill.");
        }
        var r = await Ssh(o, host, command, null, timeout, ct).ConfigureAwait(false);
        if (r.Aborted) return ToolResult.Error($"The job {op} was aborted (the job itself keeps running).");
        if (r.TimedOut) return ToolResult.Error($"The job {op} timed out after {timeout.TotalSeconds:0}s (the job itself keeps running).");
        if (r.ExitCode == 255) return ToolResult.Error(Failure(host, r));
        if (r.ExitCode != 0) return ToolResult.Error(r.Stderr.Trim() is { Length: > 0 } e ? $"{host.Alias}: {e}" : $"ssh job {op} failed (exit {r.ExitCode}).");

        var (status, log) = SshJobs.Split(r.Stdout);
        var (text, truncated, total, shown) = ToolOutput.TailLines(ToolOutput.ResolveCarriageReturns(log), ToolOutput.ModelMaxLines, ToolResultLimit.Fit(Ctx.Settings, ToolOutput.ModelMaxBytes));
        var sb = new StringBuilder(SshJobs.Describe(id, status));
        if (op == "wait" && status == "running") sb.Clear().Append($"[job {id}: still running after {wait}s]");
        sb.Append('\n').Append(truncated ? $"[showing the last {shown} of {total} lines]\n" : "").Append(text.Length > 0 ? text : "(no output yet)");
        return ToolResult.Ok(sb.ToString().TrimEnd(), new { host = host.Alias, id, op, status, log = $"~/.cache/netpi-jobs/{id}.log" });
    }
}

// ---------------------------------------------------------------------------------------------------------------- grep and find

internal sealed class SshGrepTool(IPluginContext ctx, ISshLauncher launcher) : SshToolBase(ctx, launcher)
{
    internal const int DefaultMax = 200;
    internal const int MaxLineChars = 300;
    private static readonly Regex Row = new(@"^(?<path>.+?):(?<line>\d+):(?<text>.*)$", RegexOptions.Compiled | RegexOptions.Singleline);

    internal override string Name => "grep";
    internal override bool ReadOnly => true;
    internal override string Summary =>
        $"Search file contents on a host with grep -rnIE: pattern (an extended regex), path (default: the home folder), glob (file names, " +
        $"several separated by spaces or commas), ignoreCase, maxResults (default {DefaultMax}). .git and node_modules are skipped; " +
        "output is path:line: text, like grep.";

    protected override async Task<ToolResult> RunAsync(ToolContext context, JsonElement args, SshOptions o, SshHost host, List<SshHost> hosts, CancellationToken ct)
    {
        var a = new ToolArgs(args);
        var pattern = a.Str("pattern", "regex", "query", "q");
        if (string.IsNullOrEmpty(pattern)) return ToolResult.Error("ssh grep needs a pattern.");
        var path = a.Str("path", "dir", "directory")?.Trim() is { Length: > 0 } p ? p : ".";
        var max = Math.Clamp(a.Int("maxResults", "max_results", "limit", "max") ?? DefaultMax, 1, 2000);
        var includes = (a.Str("glob", "include") ?? "").Split([' ', ',', '\t'], StringSplitOptions.RemoveEmptyEntries);
        var flags = a.Bool("ignoreCase", "ignore_case", "i") == true ? "-rnIHEi" : "-rnIHE";
        var remote = Sh.Cd(a.Str("cwd")) + $"p={Sh.Path(path)}; if [ ! -e \"$p\" ]; then echo \"No such file or directory: $p\" >&2; exit 2; fi; " +
                     $"grep {flags} --exclude-dir=.git --exclude-dir=node_modules {string.Concat(includes.Select(g => "--include=" + Sh.Quote(g) + " "))}" +
                     $"-e {Sh.Quote(pattern)} -- \"$p\" 2>&1 | head -n {max + 50}; true";
        var r = await Ssh(o, host, remote, null, TimeSpan.FromSeconds(Math.Max(60, o.Timeout)), ct).ConfigureAwait(false);
        if (r.Aborted) return ToolResult.Error("The search was aborted.");
        if (r.TimedOut) return ToolResult.Error($"The search timed out after {Math.Max(60, o.Timeout)}s. Narrow path or glob.");
        if (r.ExitCode == 255) return ToolResult.Error(Failure(host, r));
        if (r.ExitCode == 125) return ToolResult.Error($"The working directory does not exist on {host.Alias}.");
        if (r.ExitCode != 0) return ToolResult.Error(r.Stderr.Trim() is { Length: > 0 } e ? $"{host.Alias}: {e}" : $"ssh grep failed (exit {r.ExitCode}).");

        var rows = new List<string>();
        var notes = new List<string>();
        foreach (var line in r.Stdout.Replace("\r\n", "\n").Split('\n'))
        {
            if (line.Length == 0) continue;
            if (line.StartsWith("grep: ", StringComparison.Ordinal)) { notes.Add(line[6..]); continue; }
            var m = Row.Match(line);
            if (!m.Success) continue;
            var text = m.Groups["text"].Value.TrimEnd();
            if (text.Length > MaxLineChars) text = text[..MaxLineChars] + "…";
            var file = m.Groups["path"].Value;
            if (file.StartsWith("./", StringComparison.Ordinal)) file = file[2..];
            rows.Add($"{file}:{m.Groups["line"].Value}: {text}");
        }
        var truncated = rows.Count > max;
        if (truncated) rows.RemoveRange(max, rows.Count - max);
        var sb = new StringBuilder();
        if (rows.Count == 0)
        {
            if (notes.Count > 0 && notes.Any(n => !n.Contains("Permission denied", StringComparison.OrdinalIgnoreCase) && !n.Contains("No such file", StringComparison.OrdinalIgnoreCase)))
                return ToolResult.Error($"grep on {host.Alias} failed: {string.Join("; ", notes.Distinct().Take(3))}");
            sb.Append($"No matches for /{(pattern.Length > 80 ? pattern[..80] + "…" : pattern)}/ in {Where(host, path)}.");
        }
        else sb.Append(string.Join('\n', rows));
        if (notes.Count > 0) sb.Append("\n\n[grep said: ").Append(string.Join("; ", notes.Distinct().Take(3))).Append(']');
        if (truncated) sb.Append($"\n\n[Results truncated at {max} matches. Narrow the search with path/glob or a more specific pattern, or raise maxResults.]");
        return ToolResult.Ok(sb.ToString(), new { host = host.Alias, pattern, path = Where(host, path), matches = rows.Count, truncated });
    }
}

internal sealed class SshFindTool(IPluginContext ctx, ISshLauncher launcher) : SshToolBase(ctx, launcher)
{
    internal const int DefaultMax = 1000;

    internal override string Name => "find";
    internal override bool ReadOnly => true;
    internal override string Summary =>
        $"Find files and folders on a host by glob: pattern (\"*.cs\", \"src/*.{{ts,tsx}}\"; one without / matches names at any depth), path (default: the home folder), " +
        $"maxResults (default {DefaultMax}). .git and node_modules are skipped.";

    /// <summary>The <c>find</c> test for a glob: <c>-name</c> for a bare name, <c>-path</c> for one with folders; <c>{a,b}</c> expands to alternatives.</summary>
    internal static string Test(string pattern)
    {
        pattern = pattern.Trim().Replace('\\', '/');
        if (pattern.StartsWith("./", StringComparison.Ordinal)) pattern = pattern[2..];
        var alternatives = Expand(pattern).Select(alt =>
        {
            if (!alt.Contains('/')) return "-name " + Sh.Quote(alt);
            while (alt.StartsWith("**/", StringComparison.Ordinal)) alt = alt[3..];
            return "-path " + Sh.Quote("*/" + alt.Replace("**", "*"));
        }).ToList();
        return alternatives.Count == 1 ? alternatives[0] : "\\( " + string.Join(" -o ", alternatives) + " \\)";
    }

    /// <summary>One level of brace expansion: <c>*.{a,b}</c> → <c>*.a</c>, <c>*.b</c>.</summary>
    internal static List<string> Expand(string pattern)
    {
        var open = pattern.IndexOf('{');
        var close = open < 0 ? -1 : pattern.IndexOf('}', open);
        if (open < 0 || close < 0) return [pattern];
        return [.. pattern[(open + 1)..close].Split(',').Select(alt => pattern[..open] + alt + pattern[(close + 1)..])];
    }

    protected override async Task<ToolResult> RunAsync(ToolContext context, JsonElement args, SshOptions o, SshHost host, List<SshHost> hosts, CancellationToken ct)
    {
        var a = new ToolArgs(args);
        var pattern = a.Str("pattern", "glob", "name", "query", "q");
        if (string.IsNullOrWhiteSpace(pattern)) return ToolResult.Error("ssh find needs a pattern (a glob such as \"*.cs\").");
        var path = a.Str("path", "dir", "directory")?.Trim() is { Length: > 0 } p ? p : ".";
        var max = Math.Clamp(a.Int("maxResults", "max_results", "limit", "max") ?? DefaultMax, 1, 20_000);
        var remote = Sh.Cd(a.Str("cwd")) + $"p={Sh.Path(path)}; if [ ! -e \"$p\" ]; then echo \"No such file or directory: $p\" >&2; exit 2; fi; " +
                     $"find \"$p\" \\( -name .git -o -name node_modules \\) -prune -o {Test(pattern)} -print 2>/dev/null | head -n {max + 1}; true";
        var r = await Ssh(o, host, remote, null, TimeSpan.FromSeconds(Math.Max(60, o.Timeout)), ct).ConfigureAwait(false);
        if (r.Aborted) return ToolResult.Error("The search was aborted.");
        if (r.TimedOut) return ToolResult.Error($"The search timed out after {Math.Max(60, o.Timeout)}s. Narrow path or pattern.");
        if (r.ExitCode == 255) return ToolResult.Error(Failure(host, r));
        if (r.ExitCode == 125) return ToolResult.Error($"The working directory does not exist on {host.Alias}.");
        if (r.ExitCode != 0) return ToolResult.Error(r.Stderr.Trim() is { Length: > 0 } e ? $"{host.Alias}: {e}" : $"ssh find failed (exit {r.ExitCode}).");

        var rows = r.Stdout.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.StartsWith("./", StringComparison.Ordinal) ? l[2..] : l).ToList();
        var truncated = rows.Count > max;
        if (truncated) rows.RemoveRange(max, rows.Count - max);
        var text = rows.Count == 0
            ? $"No files matching {pattern} in {Where(host, path)}."
            : string.Join('\n', rows) + (truncated ? $"\n\n[Showing the first {max} results. Use a more specific pattern or path, or raise maxResults.]" : "");
        return ToolResult.Ok(text, new { host = host.Alias, pattern, path = Where(host, path), count = rows.Count, truncated });
    }
}
