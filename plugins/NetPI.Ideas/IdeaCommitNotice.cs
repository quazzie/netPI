using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace NetPI.Ideas;

/// <summary>
/// The commit check above (<see cref="IdeaCommitCheck"/>) watches every repository and offers a card, because a commit made
/// in a terminal belongs to no conversation. A commit made <em>by the agent</em> is the opposite case: the run that just
/// wrote it knows what it was for, and it is still running. So after a <c>git commit</c> or <c>git merge</c> that
/// succeeded inside the session's project, this hook injects one notice (kind <c>git-commit</c>) before the next model
/// call: update the idea this commit finished or advanced, or say in one line that none of them is about it.
/// <para>
/// It is an instruction, not an action: the agent decides with the <c>ideas</c> tool and the user sees what it did, the
/// same way a nudge is advice rather than a decision. Two bounds keep it cheap: nothing happens when the project has no
/// open idea (so a project without a backlog never pays for it), and at most <c>ideas.commitNoticesPerRun</c> notices
/// are injected into one run. The card flow stays: it is what catches a commit nobody made in a chat, and a commit the
/// agent closed is no longer open, so it is not offered twice.
/// </para>
/// </summary>
public sealed partial class IdeaCommitNoticeHook(Func<ISettings?> settings, Func<string?, List<JsonObject>> openIdeas) : IAgentHook
{
    public const string NoticeKind = "git-commit";
    /// <summary>The run flag set when a commit landed and not yet turned into a notice.</summary>
    public const string PendingKey = "netpi.ideas.commit-pending";
    /// <summary>How many notices this run already got (the cap is per run, not per commit).</summary>
    public const string CountKey = "netpi.ideas.commit-notices";
    public const int DefaultMaxPerRun = 2;
    /// <summary>Open ideas named in the notice. More than this and the notice costs more than the tool call it saves.</summary>
    public const int MaxTitles = 8;
    public const int MaxTitleLength = 90;

    /// <summary>After the nudge (200), so a stalled run's nudge is the decision that lands and this one waits for the call after it.</summary>
    public int Order => 260;

    // ------------------------------------------------------------------ the commit

    public ValueTask OnAfterToolCallAsync(AgentTurnContext turn, ToolCallPart call, ToolResultPart result)
    {
        var run = turn.Run;
        if (run.CancellationToken.IsCancellationRequested) return ValueTask.CompletedTask;
        if (!Get("ideas.tellAgentOnCommit", true)) return ValueTask.CompletedTask;
        // Nothing to update without the tool, and nothing to say without a project: ideas are stamped with one.
        if (!turn.Tools.Any(t => t.Name == "ideas")) return ValueTask.CompletedTask;
        if (run.Project is not { } project) return ValueTask.CompletedTask;

        var args = Arguments(call.Arguments);
        if (args is null || !IsCommitCommand(IdeaOps.Str(args, "command"))) return ValueTask.CompletedTask;
        if (!Succeeded(result)) return ValueTask.CompletedTask;
        if (!InProject(IdeaOps.Str(args, "command"), IdeaOps.Str(args, "cwd"), run, project)) return ValueTask.CompletedTask;

        // The open set is read once, at the commit: it cannot change under us in the next few milliseconds, and the
        // notice is the only consumer.
        var open = openIdeas(project.Id);
        if (open.Count == 0) return ValueTask.CompletedTask;

        // A commit while another one is still waiting to be announced: one notice, naming both, beats two.
        run.Items[PendingKey] = Combine(AsPending(run), new Pending(project.Name, Titles(open)));
        return ValueTask.CompletedTask;
    }

    // ------------------------------------------------------------------ the notice

    public ValueTask<TurnDecision?> OnAfterModelCallAsync(AgentTurnContext turn, ChatMessage assistant)
    {
        var run = turn.Run;
        if (!TakePending(run, out var pending)) return ValueTask.FromResult<TurnDecision?>(null);
        if (run.CancellationToken.IsCancellationRequested) return ValueTask.FromResult<TurnDecision?>(null);
        if (!Get("ideas.tellAgentOnCommit", true)) return ValueTask.FromResult<TurnDecision?>(null);

        var count = run.Items.TryGetValue(CountKey, out var v) && v is int n ? n : 0;
        if (count >= Math.Clamp(Get("ideas.commitNoticesPerRun", DefaultMaxPerRun), 0, 10)) return ValueTask.FromResult<TurnDecision?>(null);
        run.Items[CountKey] = count + 1;
        return ValueTask.FromResult<TurnDecision?>(TurnDecision.Inject(Text(pending), NoticeKind));
    }

    /// <summary>What the notice says. Kept short: the model has the commit and its result right above it in the transcript.</summary>
    public static string Text(Pending pending)
    {
        var titles = string.Join("; ", pending.Titles);
        return $"A commit just landed in {pending.Project} (the git command you ran succeeded). " +
            (titles.Length > 0 ? $"Open ideas of that project: {titles}. " : "") +
            "If this commit finishes one of them, update that idea now with the ideas tool: mark it done, or leave it open and add a short section saying what landed. " +
            "If none of them is about this commit, say so in one line and do not create an idea for it.";
    }

    // ------------------------------------------------------------------ the checks

    /// <summary>
    /// The command runs a commit or a merge, and the command that runs it actually lands something. <c>--dry-run</c>,
    /// <c>--abort</c>, <c>--quit</c> and <c>--no-commit</c> are the same words without the effect.
    /// <para>
    /// Read as tokens rather than as one pattern, so the global options git takes before a subcommand (<c>-C dir</c>,
    /// <c>--git-dir</c>) do not hide it, and a compound command (<c>git add -A; git commit -m x</c>) is seen for what it
    /// is: the first subcommand after a <c>git</c> word decides, and a command that never gets there (status, log,
    /// push) is not a commit.
    /// </para>
    /// </summary>
    public static bool IsCommitCommand(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return false;
        var tokens = command.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < tokens.Length; i++)
        {
            if (!IsGitWord(tokens, i)) continue;
            for (var j = i + 1; j < tokens.Length; j++)
            {
                var token = tokens[j];
                if (token.StartsWith('-'))
                {
                    if (GlobalValueOptions.Contains(token)) j++;   // the option's value is the next token
                    continue;
                }
                if (token.Equals("commit", StringComparison.OrdinalIgnoreCase) || token.Equals("merge", StringComparison.OrdinalIgnoreCase))
                {
                    // The flags right after the subcommand decide whether anything is written.
                    var rest = string.Join(' ', tokens.Skip(j + 1).Take(FlagsRead));
                    return !DryRunFlag().IsMatch(rest);
                }
                break;   // another subcommand (status, log, push, add...): not this hook's business
            }
        }
        return false;
    }

    /// <summary>How many tokens after the subcommand are read for the flags that change its effect.</summary>
    private const int FlagsRead = 12;

    /// <summary>A token that starts a git command: the bare word (or its path) after a separator, or the first one.</summary>
    private static bool IsGitWord(string[] tokens, int i)
    {
        var token = tokens[i];
        var isGit = token.Equals("git", StringComparison.OrdinalIgnoreCase) ||
                    token.EndsWith("/git", StringComparison.OrdinalIgnoreCase) ||
                    token.EndsWith("\\git", StringComparison.OrdinalIgnoreCase);
        if (!isGit) return false;
        if (i == 0) return true;
        var before = tokens[i - 1];
        return before.EndsWith(';') || before.EndsWith('&') || before.EndsWith('|') || before.EndsWith('(') || before.EndsWith('{') ||
               before.Equals("sudo", StringComparison.OrdinalIgnoreCase) || before.Equals("env", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The tool call succeeded: not an error, and an exit code of 0 when the tool reports one. A JSON null exit code is
    /// a command still running (the shell tools report that for a background process), so nothing has landed yet.
    /// </summary>
    public static bool Succeeded(ToolResultPart result)
    {
        if (result.IsError) return false;
        if (result.Details is not JsonObject details || !details.TryGetPropertyValue("exitCode", out var code)) return true;
        if (code is null) return false;                               // JSON null: the command is still running
        if (code is not JsonValue value) return true;                  // not a number: nothing to read
        return value.TryGetValue<int>(out var exit) && exit == 0;      // non-zero: it failed
    }

    /// <summary>
    /// The commit is this session's project's work: the directory the command effectively ran in belongs to the
    /// repository the session works in.
    /// <para>
    /// The effective directory is the call's <c>cwd</c> argument (resolved against the run's cwd when relative), the
    /// run's own directory when the call named none, and where <c>git -C &lt;dir&gt;</c> pointed when the command used
    /// one — <see cref="GitCdir"/> reads it out of the command. The comparison is of <em>repositories</em>, not path
    /// prefixes: a repository's identity is its common git directory (<c>git rev-parse --git-common-dir</c>), which a
    /// linked worktree reports as the main checkout's. So a commit in a sibling worktree of the session's repository —
    /// the same project's backlog on a different branch — is attributed, while a commit in another repository is not,
    /// whatever the paths are spelled.
    /// </para>
    /// <para>
    /// Evidence, in preference: the session's workspace binding (the checkout its <c>RepoCommonDir</c> was recorded
    /// for), the <see cref="IWorkspaceRepoProbe"/> the workspaces plugin registers with the run's services, and
    /// <see cref="WorkspacePaths"/> for canonicalizing and containing paths. When there is no repository evidence
    /// (older host, plugin not loaded, or git does not recognize the directories), the pre-repository behavior decides:
    /// a relative or unnamed cwd counts as the session's own directory, and an absolute one must be inside the
    /// session's checkout.
    /// </para>
    /// </summary>
    public static bool InProject(string? command, string? rawCwd, AgentRunContext run, ProjectInfo project) =>
        InProject(command, rawCwd, run, project, run.Services.Get<IWorkspaceRepoProbe>());

    /// <summary>As <see cref="InProject(string?, string?, AgentRunContext, ProjectInfo)"/> with the probe handed over,
    /// for a test that does not want to register one.</summary>
    public static bool InProject(string? command, string? rawCwd, AgentRunContext run, ProjectInfo project, IWorkspaceRepoProbe? probe)
    {
        // 1. The directory the command effectively ran in: the call's cwd (resolved against the run's cwd when
        //    relative), or the run's own directory when the call named none.
        var dir = string.IsNullOrWhiteSpace(rawCwd) ? run.Cwd
            : Path.IsPathRooted(rawCwd) ? rawCwd
            : Path.Combine(run.Cwd, rawCwd);

        // 2. git's own redirection of the directory: `git -C <dir>` makes git operate in <dir> instead, and the last
        //    one wins.
        var redirected = GitCdir(command);
        if (redirected is not null)
            dir = redirected;

        // 3. The session's home checkout: the root of its workspace when the session is bound to one, else the
        //    project's path.
        var workspace = run.Workspace();
        var home = workspace?.Root ?? project.Path;

        // 4. Repository evidence, when there is any: compare the repository of the home and the repository of the
        //    directory the commit ran in, by their common git directories — the identity a worktree shares with the
        //    main checkout it was made from.
        var dirCanon = WorkspacePaths.Canonical(dir);
        var homeRepo = HomeRepo(workspace, home, probe);
        var dirRepo = probe?.CommonDirOf(dirCanon);
        if (homeRepo is not null && dirRepo is not null)
            return SameRepo(homeRepo, dirRepo);

        // 5. No repository evidence (the workspaces plugin is not loaded, an older host, or git does not recognize the
        //    directories): the pre-repository behavior. A relative or unnamed cwd counts as the session's own
        //    directory, which is the project; an absolute directory (or one `git -C` pointed at) must be inside the
        //    session's checkout.
        var explicitDir = (!string.IsNullOrWhiteSpace(rawCwd) && Path.IsPathRooted(rawCwd)) || redirected is not null;
        if (!explicitDir) return true;
        return WorkspacePaths.IsInside(home, dirCanon);
    }

    /// <summary>The repository of the session's home checkout: what its workspace binding recorded, else what the
    /// probe finds there.</summary>
    private static string? HomeRepo(WorkspaceBinding? workspace, string home, IWorkspaceRepoProbe? probe)
    {
        if (workspace?.RepoCommonDir is { } recorded) return recorded;
        return probe?.CommonDirOf(home);
    }

    /// <summary>Whether two common directories are one repository (paths canonicalized, compared per platform).</summary>
    private static bool SameRepo(string a, string b) =>
        string.Equals(WorkspacePaths.Canonical(a), WorkspacePaths.Canonical(b), WorkspacePaths.Comparison);

    /// <summary>
    /// The directory the commit's <c>git</c> command redirected git to: <c>-C &lt;dir&gt;</c> (also <c>-C&lt;dir&gt;</c>
    /// without the space, and a quoted path), read only from the command <see cref="IsCommitCommand"/> recognizes as the
    /// commit — a <c>-C</c> belonging to another <c>git</c> in a compound command does not move this one. The last
    /// <c>-C</c> of that command wins, git's own rule. Null when the commit did not redirect git's directory.
    /// </summary>
    public static string? GitCdir(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        var tokens = ShellTokens(command).ToArray();
        for (var i = 0; i < tokens.Length; i++)
        {
            if (!IsGitWord(tokens, i)) continue;
            string? dir = null;
            for (var j = i + 1; j < tokens.Length; j++)
            {
                var token = tokens[j];
                if (IsCommandEnd(token)) break;                 // a separator word ends this git command
                if (token.StartsWith('-'))
                {
                    if (token == "-C")
                    {
                        // An empty -C value is git's way of saying "stay put", so it is no redirection.
                        if (j + 1 < tokens.Length && tokens[j + 1] is { Length: > 0 } value && !IsCommandEnd(value))
                            dir = tokens[++j];
                        continue;
                    }
                    if (token.StartsWith("-C", StringComparison.Ordinal) && token.Length > 2) { dir = Unquote(token[2..]); continue; }
                    if (GlobalValueOptions.Contains(token)) j++;   // skip the value of the option
                    continue;
                }
                if (token.Equals("commit", StringComparison.OrdinalIgnoreCase) || token.Equals("merge", StringComparison.OrdinalIgnoreCase))
                {
                    // IsCommitCommand's effect check: a --dry-run commit lands nothing, so it is not "the commit".
                    var rest = string.Join(' ', tokens.Skip(j + 1).Take(FlagsRead));
                    if (!DryRunFlag().IsMatch(rest)) return dir;
                }
                break;   // another subcommand: this git word is not the commit
            }
        }
        return null;
    }

    /// <summary>
    /// The command's words the way a shell hands them to git: whitespace separates, a quoted stretch is one word
    /// (quotes removed), so a quoted <c>-C</c> value with spaces survives. For unquoted input this is identical to a
    /// plain whitespace split, so <see cref="IsGitWord"/> sees the words it always did.
    /// </summary>
    private static List<string> ShellTokens(string command)
    {
        var tokens = new List<string>();
        var i = 0;
        while (true)
        {
            while (i < command.Length && char.IsWhiteSpace(command[i])) i++;
            if (i >= command.Length) break;
            var start = i;
            if (command[i] is '"' or '\'')
            {
                var q = command[i];
                var content = i + 1;
                i++;
                while (i < command.Length && command[i] != q) i++;
                tokens.Add(command[content..i]);                // the quoted stretch, quotes removed
                i = Math.Min(i + 1, command.Length);            // past the closing quote (or the end)
            }
            else
            {
                while (i < command.Length && !char.IsWhiteSpace(command[i])) i++;
                tokens.Add(command[start..i]);
            }
        }
        return tokens;
    }

    /// <summary>A shell separator word: it ends the command it appears in, so a <c>-C</c> on the other side of it
    /// belongs to another command.</summary>
    private static bool IsCommandEnd(string token) =>
        token is ";" or "&" or "|" or "&&" or "||" ||
        token.StartsWith("&", StringComparison.Ordinal) ||
        token.StartsWith("|", StringComparison.Ordinal) ||
        token.EndsWith("&", StringComparison.Ordinal) ||
        token.EndsWith("|", StringComparison.Ordinal) ||
        token.EndsWith(";", StringComparison.Ordinal);

    /// <summary>A pair of surrounding quotes (as in <c>-C"/x/y"</c>), if any.</summary>
    private static string Unquote(string s) =>
        s.Length >= 2 && ((s[0] == '"' && s[^1] == '"') || (s[0] == '\'' && s[^1] == '\'')) ? s[1..^1] : s;

    private static Pending? AsPending(AgentRunContext run) => run.Items.TryGetValue(PendingKey, out var v) ? v as Pending : null;

    /// <summary>A pending commit is consumed once: the notice is asked for once, and a lost run does not repeat it.</summary>
    private static bool TakePending(AgentRunContext run, out Pending pending)
    {
        pending = null!;
        if (!run.Items.TryGetValue(PendingKey, out var v) || v is not Pending found) return false;
        run.Items.Remove(PendingKey);
        pending = found;
        return true;
    }

    /// <summary>Two commits inside one model call are one notice, naming both projects once.</summary>
    private static Pending Combine(Pending? first, Pending next) =>
        first is null ? next
        : new Pending(first.Project == next.Project ? first.Project : $"{first.Project}, {next.Project}", [.. first.Titles, .. next.Titles]);

    /// <summary>The open idea titles the notice names: bounded, and each one clipped so one long title cannot flood it.</summary>
    public static List<string> Titles(List<JsonObject> open)
    {
        var titles = new List<string>();
        foreach (var idea in open)
        {
            var title = IdeaOps.Str(idea["title"]);
            if (string.IsNullOrWhiteSpace(title)) continue;
            titles.Add($"\"{(title.Length > MaxTitleLength ? title[..MaxTitleLength] + "…" : title)}\"");
            if (titles.Count >= MaxTitles) break;
        }
        return titles;
    }

    private static JsonObject? Arguments(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonNode.Parse(json) as JsonObject; } catch (JsonException) { return null; }
    }

    private T Get<T>(string path, T fallback)
    {
        var s = settings();
        if (s is null) return fallback;
        try { return s.Get(path, fallback) ?? fallback; } catch { return fallback; }
    }

    /// <summary>The project(s) a pending notice is about, with the open ideas it names.</summary>
    public sealed record Pending(string Project, List<string> Titles);

    /// <summary>The global options that take a value, so the value is not mistaken for the subcommand.</summary>
    private static readonly HashSet<string> GlobalValueOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "-C", "-c", "--git-dir", "--work-tree", "--namespace", "--exec-path", "--config-env",
    };

    [GeneratedRegex(@"--dry-run|--abort\b|--quit\b|--no-commit\b", RegexOptions.IgnoreCase)]
    private static partial Regex DryRunFlag();
}
