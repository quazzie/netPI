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
        if (!InProject(IdeaOps.Str(args, "cwd"), run, project)) return ValueTask.CompletedTask;

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
    /// The commit is in the session's project: the working directory the command ran in, or the run's own when the call
    /// named none. A commit in a scratch directory or another repository is not this project's work.
    /// </summary>
    public static bool InProject(string? cwd, AgentRunContext run, ProjectInfo project)
    {
        var dir = string.IsNullOrWhiteSpace(cwd) ? run.Cwd : cwd;
        if (string.IsNullOrWhiteSpace(dir)) return true;   // nothing to compare against: the run's project is the best answer
        if (!Path.IsPathRooted(dir)) return true;          // relative: resolved against the session cwd, which is the project
        try
        {
            var root = Path.GetFullPath(project.Path);
            var full = Path.GetFullPath(dir);
            var inside = full.Equals(root, StringComparison.OrdinalIgnoreCase) ||
                         full.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            return inside;
        }
        catch { return false; }
    }

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
