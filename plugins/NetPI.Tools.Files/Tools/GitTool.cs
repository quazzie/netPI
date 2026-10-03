namespace NetPI.Tools.Files;

/// <summary>
/// The git a plan may run: one read-only tool with a fixed set of actions (log, show, diff, status, blame) over the
/// session's workspace, so a plan can ask "what changed in this area lately", "who touched this" and "what does that
/// commit do" without the shell plan mode refuses. The actions run through the same <see cref="GitRunner"/> the Files
/// and Workspaces plugins use. There is never a free command line: each action is an allow-list of subcommand and
/// flags, commit names pass through git's own parser (a hash, HEAD, a tag or a branch), and paths are resolved and
/// checked like the file tools' reads. Nothing writes — no commits, pushes or fetches.
/// </summary>
public sealed class GitTool(ISettings? settings = null) : FileToolBase(settings)
{
    /// <summary>How much of one answer is kept before it is cut with a note (the runner would cut a longer result
    /// in the middle, while a cut answer ends with what to narrow). Kept under the tool result limit with room.</summary>
    public const int MaxOutputChars = 30 * 1024;

    /// <summary>log: the most commits one call returns.</summary>
    public const int MaxCommits = 100;

    public override ToolDefinition Definition { get; } = new()
    {
        Name = "git",
        Label = "Git",
        Category = "files",
        ReadOnly = true,
        SummaryArg = "action",
        Description =
            "Read git in the session's workspace: log (recent commits, optionally for a path), show (what a commit " +
            "changed), diff (the changes, worktree or between commits), status, blame (who last changed each line). " +
            "Read-only: it never writes, so it is allowed in plan mode.",
        Help =
            "Actions, all run in the session's workspace (paths are resolved like the file tools' paths, relative or " +
            "absolute):\n" +
            "- log { n? (default 20, max 100), path? } — the recent commits, newest first: short hash, date, author, " +
            "subject. path limits them to the commits that touched a file or directory.\n" +
            "- show { commit, path?, patch? } — what one commit did: its header and the files it changed. patch: true " +
            "adds the full diff. commit is a hash, HEAD, HEAD~2, a tag or a branch.\n" +
            "- diff { path?, a?, b?, staged? } — a change: no args = worktree vs the index (unstaged); staged: true = " +
            "the index vs HEAD; a = that commit vs the worktree; a b = between two commits.\n" +
            "- status { path? } — the branch and the uncommitted changes, one line per file.\n" +
            "- blame { path, start?, end? } — who last changed each line of a file, with a line range (both or neither).\n" +
            $"An answer is cut at about {MaxOutputChars / 1024} KB: narrow it with a path, fewer commits (n) or a line " +
            "range. Nothing is ever written — it is the git a plan can run, because plan mode has no shell.",
        Parameters = ToolSchema.Object(
            ("action", ToolSchema.Str("What to do", "log", "show", "diff", "status", "blame"), true),
            ("n", ToolSchema.Int("log: how many commits (default 20, max 100)"), false),
            ("path", ToolSchema.Str("A file or directory to limit to (resolved like the file tools' paths)"), false),
            ("commit", ToolSchema.Str("show: the commit (a hash, HEAD, a tag or a branch)"), false),
            ("patch", ToolSchema.Bool("show: the full patch (default: the stat only)"), false),
            ("a", ToolSchema.Str("diff: first commit/branch (with b: between them; alone: vs the worktree)"), false),
            ("b", ToolSchema.Str("diff: second commit/branch"), false),
            ("staged", ToolSchema.Bool("diff: the staged changes against HEAD"), false),
            ("start", ToolSchema.Int("blame: first line (1-based)"), false),
            ("end", ToolSchema.Int("blame: last line"), false)),
        PromptGuidelines =
        [
            "For git questions (what changed lately, who touched this, what a commit does) use the git tool instead of " +
            "git in the shell: it is read-only, so it is allowed in plan mode.",
        ],
    };

    protected override async Task<ToolResult> RunAsync(ToolContext ctx, ToolArgs args, CancellationToken ct)
    {
        var action = (args.Str("action") ?? "").Trim().ToLowerInvariant();
        return action switch
        {
            "" => MissingArg("action", """{"action": "log", "n": 20}"""),
            "log" => await LogAsync(ctx, args, ct).ConfigureAwait(false),
            "show" => await ShowAsync(ctx, args, ct).ConfigureAwait(false),
            "diff" => await DiffAsync(ctx, args, ct).ConfigureAwait(false),
            "status" => await StatusAsync(ctx, args, ct).ConfigureAwait(false),
            "blame" => await BlameAsync(ctx, args, ct).ConfigureAwait(false),
            _ => ToolResult.Error($"Unknown action \"{action}\". The actions are log, show, diff, status and blame."),
        };
    }

    // ------------------------------------------------ actions

    private Task<ToolResult> LogAsync(ToolContext ctx, ToolArgs args, CancellationToken ct)
    {
        var n = Math.Clamp(args.Int("n", "limit", "count") ?? 20, 1, MaxCommits);
        string? path = null;
        if (args.Str(PathNames) is { Length: > 0 } p) { path = ctx.ResolvePath(p); if (!File.Exists(path) && !Directory.Exists(path)) return Task.FromResult(NotFound(ctx, path)); }
        var list = new List<string> { "log", "--no-color", "-n", n.ToString(), "--pretty=format:%h %ad %an %s", "--date=short" };
        if (path is not null) list.AddRange(["--", path]);
        return Run(ctx, "log", ct, list, new { n, path });
    }

    private async Task<ToolResult> ShowAsync(ToolContext ctx, ToolArgs args, CancellationToken ct)
    {
        var commit = args.Str("commit", "rev", "hash", "id");
        if (string.IsNullOrWhiteSpace(commit)) return MissingArg("commit", """{"action": "show", "commit": "HEAD"}""");
        if (Ref(commit) is { } bad) return bad;
        string? path = null;
        if (args.Str(PathNames) is { Length: > 0 } p) { path = ctx.ResolvePath(p); if (!File.Exists(path) && !Directory.Exists(path)) return NotFound(ctx, path); }
        var patch = args.Bool("patch", "full") ?? false;
        var list = new List<string> { "show", "--no-color", "--no-ext-diff" };
        if (patch) list.AddRange(["--stat", "--patch"]);
        else list.Add("--stat");
        list.Add(commit);
        if (path is not null) list.AddRange(["--", path]);
        return await Run(ctx, "show", ct, list, new { commit, path, patch }).ConfigureAwait(false);
    }

    private async Task<ToolResult> DiffAsync(ToolContext ctx, ToolArgs args, CancellationToken ct)
    {
        var staged = args.Bool("staged", "cached", "index") ?? false;
        string? a = null, b = null;
        foreach (var (value, name) in new[] { (args.Str("a", "from", "old"), "a"), (args.Str("b", "to", "new"), "b") })
        {
            if (string.IsNullOrWhiteSpace(value)) continue;
            if (Ref(value) is { } bad) return bad;
            if (staged) return ToolResult.Error($"diff: {name} and staged cannot be combined: staged is the index against HEAD, the others name commits.");
            if (name == "a") a = value; else b = value;
        }
        string? path = null;
        if (args.Str(PathNames) is { Length: > 0 } p) { path = ctx.ResolvePath(p); if (!File.Exists(path) && !Directory.Exists(path)) return NotFound(ctx, path); }
        var list = new List<string> { "diff", "--no-color", "--no-ext-diff" };
        if (staged) list.Add("--cached");
        if (a is not null) list.Add(a);
        if (a is not null && b is not null) list.Add(b);
        if (path is not null) list.AddRange(["--", path]);
        return await Run(ctx, "diff", ct, list, new { a, b, staged, path }).ConfigureAwait(false);
    }

    private async Task<ToolResult> StatusAsync(ToolContext ctx, ToolArgs args, CancellationToken ct)
    {
        string? path = null;
        if (args.Str(PathNames) is { Length: > 0 } p) { path = ctx.ResolvePath(p); if (!File.Exists(path) && !Directory.Exists(path)) return NotFound(ctx, path); }
        var list = new List<string> { "status", "--porcelain=v1", "-b" };
        if (path is not null) list.AddRange(["--", path]);
        return await Run(ctx, "status", ct, list, new { path }).ConfigureAwait(false);
    }

    private async Task<ToolResult> BlameAsync(ToolContext ctx, ToolArgs args, CancellationToken ct)
    {
        var p = args.Str(PathNames);
        if (string.IsNullOrWhiteSpace(p)) return MissingArg("path", """{"action": "blame", "path": "src/Upload.cs"}""");
        var full = ctx.ResolvePath(p);
        if (!File.Exists(full))
            return Directory.Exists(full)
                ? ToolResult.Error($"{full} is a directory, not a file. blame blames one file.")
                : NotFound(ctx, full);
        int? start = null, end = null;
        var s = args.Int("start", "from", "line", "from_line", "fromLine");
        var e = args.Int("end", "to", "until", "to_line", "toLine");
        if (s is null && e is null) { }
        else if (s is null || e is null) return ToolResult.Error("blame: start and end are given together (the line range to blame).");
        else
        {
            if (s < 1 || e < 1) return ToolResult.Error("blame: start and end are line numbers, counting from 1.");
            if (e < s) return ToolResult.Error($"blame: end ({e}) is before start ({s}).");
            start = s; end = e;
        }
        var list = new List<string> { "blame" };
        if (start is not null) list.AddRange(["-L", $"{start},{end}"]);
        list.AddRange(["--", full]);
        return await Run(ctx, "blame", ct, list, new { path = full, start, end }).ConfigureAwait(false);
    }

    // ------------------------------------------------ plumbing

    /// <summary>A commit name this tool will run: a hash, HEAD, HEAD~2, a tag or a branch. Git's own parser takes the
    /// rest — the set here only keeps a value from naming anything but a revision (no colon, no space, nothing a
    /// shell would read as a command, and never a leading dash: a "commit" of <c>--ext-diff</c> would be an option that runs the
    /// repository's configured diff program, after the <c>--no-ext-diff</c> this tool passes).</summary>
    private static ToolResult? Ref(string value) =>
        value.Length is >= 1 and <= 120 && value[0] != '-' && value.All(c => char.IsLetterOrDigit(c) || c is '_' or '.' or '/' or '+' or '~' or '-' or '^')
            ? null
            : ToolResult.Error($"git: \"{value}\" is not a commit name: this tool runs a hash, HEAD, a tag or a branch (letters, digits and _ . / + ~ - ^ only).");

    /// <summary>One git call: bounded like the file tools' reads (under the tool result limit, so the runner never
    /// cuts a second time), with the endings the file tools already know and the note when an answer stopped at its
    /// bound.</summary>
    private async Task<ToolResult> Run(ToolContext ctx, string action, CancellationToken ct, IReadOnlyList<string> list, object details)
    {
        var cap = ToolResultLimit.Fit(Settings, MaxOutputChars, 400);
        var r = await GitRunner.RunAsync(ctx.Cwd, ct, cap, [.. list]).ConfigureAwait(false);
        if (r.Aborted) throw new OperationCanceledException(ct);
        if (r.StartError is not null)
            return ToolResult.Error($"git is not available ({r.StartError}). Install git or add it to PATH.");
        if (r.TimedOut)
            return ToolResult.Error($"git timed out after {GitRunner.Timeout.TotalSeconds:0} seconds.");
        if (r.ExitCode != 0)
        {
            var err = r.Stderr.Trim();
            if (err.Length > 2000) err = err[..2000] + "…";
            return ToolResult.Error($"git {action} failed (exit {r.ExitCode}){(string.IsNullOrWhiteSpace(err) ? "" : ": " + err)}");
        }
        var content = r.Stdout;
        if (r.Cut)
            content += $"\n\n[… the answer stopped at {cap / 1024} KB. Narrow it: a path, fewer commits (n) or a line range (start/end).]";
        return ToolResult.Ok(content, new { action, exitCode = 0, chars = r.Stdout.Length, truncated = r.Cut, details });
    }
}
