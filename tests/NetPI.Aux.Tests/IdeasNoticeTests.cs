using System.Text.Json.Nodes;
using NetPI.Ideas;

namespace NetPI.Aux.Tests;

/// <summary>
/// The notice a run gets after it commits: which tool calls count as a landed commit, when it is silent, and the bound
/// on how often one run may be asked (docs/plans/2026-09-30-ideas-commit-notice.md).
/// </summary>
public static class IdeasNoticeTests
{
    private sealed class Env
    {
        public FakePluginContext Ctx { get; } = new();
        public ProjectInfo Project { get; }
        public SessionInfo Session { get; }
        public List<JsonObject> Open { get; set; } = [];
        public int Reads { get; private set; }

        public Env()
        {
            Project = Ctx.SessionsFake.CreateProject("NetPI", Path.Combine(Ctx.Paths.Home, "repo"));
            Session = Ctx.SessionsFake.CreateSession(new SessionInfo { Title = "s", ProjectId = Project.Id });
        }

        public IdeaCommitNoticeHook Hook()
        {
            Reads = 0;
            return new IdeaCommitNoticeHook(() => Ctx.Settings, _ => { Reads++; return Open; });
        }

        public AgentRunContext Run()
        {
            var run = T.Run(Ctx, T.Model(), Session);
            run.Project = Project;
            run.Cwd = Project.Path;
            return run;
        }
    }

    private static async Task<TurnDecision?> After(IdeaCommitNoticeHook hook, AgentTurnContext turn) =>
        await hook.OnAfterModelCallAsync(turn, T.Assistant("done"));

    private static AgentTurnContext TurnWithIdeas(AgentRunContext run) => T.Turn(run, tools: [T.Tool("bash"), T.Tool("ideas")]);

    private static ToolCallPart Commit(string command, string? cwd = null)
    {
        var args = new JsonObject { ["command"] = command };
        if (cwd is not null) args["cwd"] = cwd;
        return new ToolCallPart { Id = "c1", Name = "bash", Arguments = args.ToJsonString() };
    }

    private static ToolResultPart Ok(int? exit = 0) => new()
    {
        CallId = "c1", Name = "bash", Content = "[master abc1234] Work tab: a fix",
        Details = new JsonObject { ["exitCode"] = exit is null ? null : JsonValue.Create(exit) },
    };

    public static void Register(TestRunner r)
    {
        r.Add("notice: a successful git commit in the project → one notice naming the open ideas", async () =>
        {
            var env = new Env { Open = [Idea("Collapsing a process row leaks a timer"), Idea("Ideas: admission bound")] };
            var hook = env.Hook();
            var run = env.Run();
            var turn = TurnWithIdeas(run);
            await hook.OnAfterToolCallAsync(turn, Commit("git add -A; git commit -m \"Work tab: a fix\""), Ok());
            var d = await After(hook, turn);
            Check.Equal(TurnAction.Inject, d!.Action);
            Check.Equal(IdeaCommitNoticeHook.NoticeKind, d.NoticeKind);
            Check.Contains(d.Text, "NetPI");
            Check.Contains(d.Text, "Collapsing a process row leaks a timer");
            Check.Contains(d.Text, "admission bound");
            Check.Contains(d.Text, "mark it done");
            Check.Equal(260, hook.Order);
            // One read of the open set, for the commit.
            Check.Equal(1, env.Reads);
        });

        r.Add("notice: nothing pending → no decision", async () =>
        {
            var env = new Env { Open = [Idea("Something")] };
            var hook = env.Hook();
            Check.True(await After(hook, TurnWithIdeas(env.Run())) is null, "no commit, no notice");
        });

        r.Add("notice: the pending commit is consumed once", async () =>
        {
            var env = new Env { Open = [Idea("Something")] };
            var hook = env.Hook();
            var run = env.Run();
            var turn = TurnWithIdeas(run);
            await hook.OnAfterToolCallAsync(turn, Commit("git commit -m x"), Ok());
            Check.True(await After(hook, turn) is { Action: TurnAction.Inject }, "the first call after the commit asks");
            Check.True(await After(hook, turn) is null, "and only that one does");
        });

        r.Add("notice: no open idea in the project → silent (no extra model call)", async () =>
        {
            var env = new Env { Open = [] };
            var hook = env.Hook();
            var run = env.Run();
            var turn = TurnWithIdeas(run);
            await hook.OnAfterToolCallAsync(turn, Commit("git commit -m x"), Ok());
            Check.True(await After(hook, turn) is null, "nothing tracked, nothing to ask");
        });

        r.Add("notice: off by setting → silent", async () =>
        {
            var env = new Env { Open = [Idea("Something")] };
            env.Ctx.SettingsFake.Set("ideas.tellAgentOnCommit", false);
            var hook = env.Hook();
            var run = env.Run();
            var turn = TurnWithIdeas(run);
            await hook.OnAfterToolCallAsync(turn, Commit("git commit -m x"), Ok());
            Check.True(await After(hook, turn) is null, "the setting is off");
        });

        r.Add("notice: a run is asked at most ideas.commitNoticesPerRun times", async () =>
        {
            var env = new Env { Open = [Idea("Something")] };
            env.Ctx.SettingsFake.Set("ideas.commitNoticesPerRun", 2);
            var hook = env.Hook();
            var run = env.Run();
            var turn = TurnWithIdeas(run);
            var asked = 0;
            for (var i = 0; i < 5; i++)
            {
                await hook.OnAfterToolCallAsync(turn, Commit($"git commit -m c{i}"), Ok());
                if (await After(hook, turn) is { Action: TurnAction.Inject }) asked++;
            }
            Check.Equal(2, asked);
        });

        r.Add("notice: without the ideas tool there is nothing to ask", async () =>
        {
            var env = new Env { Open = [Idea("Something")] };
            var hook = env.Hook();
            var run = env.Run();
            var turn = T.Turn(run, tools: [T.Tool("bash")]);
            await hook.OnAfterToolCallAsync(turn, Commit("git commit -m x"), Ok());
            Check.True(await After(hook, turn) is null, "no ideas tool, no notice");
        });

        r.Add("notice: a session without a project is left alone", async () =>
        {
            var env = new Env { Open = [Idea("Something")] };
            var hook = env.Hook();
            var run = env.Run();
            run.Project = null;
            var turn = TurnWithIdeas(run);
            await hook.OnAfterToolCallAsync(turn, Commit("git commit -m x"), Ok());
            Check.True(await After(hook, turn) is null, "ideas are stamped with a project; without one this is guesswork");
        });

        r.Add("notice: a commit in another repository is not this project's", async () =>
        {
            var env = new Env { Open = [Idea("Something")] };
            env.Ctx.SettingsFake.Set("ideas.commitNoticesPerRun", 5);
            var hook = env.Hook();
            var run = env.Run();
            var turn = TurnWithIdeas(run);
            await hook.OnAfterToolCallAsync(turn, Commit("git commit -m x", cwd: Path.Combine(env.Ctx.Paths.Home, "other")), Ok());
            Check.True(await After(hook, turn) is null, "outside the project");
            // A subdirectory of the project, and the run's own cwd when the call names none, are inside it.
            await hook.OnAfterToolCallAsync(turn, Commit("git commit -m x", cwd: Path.Combine(env.Project.Path, "plugins")), Ok());
            Check.True(await After(hook, turn) is { Action: TurnAction.Inject }, "a subdirectory is the same project");
            await hook.OnAfterToolCallAsync(turn, Commit("git commit -m x"), Ok());
            Check.True(await After(hook, turn) is { Action: TurnAction.Inject }, "no cwd given: the run's own");
        });

        r.Add("notice: a failed commit is not a commit", async () =>
        {
            var env = new Env { Open = [Idea("Something")] };
            env.Ctx.SettingsFake.Set("ideas.commitNoticesPerRun", 5);
            var hook = env.Hook();
            var run = env.Run();
            var turn = TurnWithIdeas(run);
            await hook.OnAfterToolCallAsync(turn, Commit("git commit -m x"), new ToolResultPart { IsError = true, Content = "nothing to commit" });
            Check.True(await After(hook, turn) is null, "an error result");
            await hook.OnAfterToolCallAsync(turn, Commit("git commit -m x"), Ok(exit: 1));
            Check.True(await After(hook, turn) is null, "a non-zero exit code");
            await hook.OnAfterToolCallAsync(turn, Commit("git commit -m x", cwd: env.Project.Path), Ok(exit: null));
            Check.True(await After(hook, turn) is null, "still running (a background command)");
        });

        r.Add("notice: commands that do not land a commit", () =>
        {
            foreach (var command in new[]
                     {
                         "git status", "git log --oneline -5", "git add -A", "git diff --cached", "git push", "git commit --dry-run",
                         "git merge --abort origin/main", "git merge --no-commit main", "ls -la", "echo commit", "",
                     })
                Check.True(!IdeaCommitNoticeHook.IsCommitCommand(command), $"not a commit: {command}");
            foreach (var command in new[]
                     {
                         "git commit -m \"fix\"", "git commit", "git commit -a --amend --no-edit", "git -C C:/x commit -m x",
                         "git merge main", "git merge --no-ff main", "git add -A; git commit -m 'x'; git push",
                     })
                Check.True(IdeaCommitNoticeHook.IsCommitCommand(command), $"a commit: {command}");
        });

        r.Add("notice: only shell tools are read", async () =>
        {
            var env = new Env { Open = [Idea("Something")] };
            var hook = env.Hook();
            var turn = TurnWithIdeas(env.Run());
            await hook.OnAfterToolCallAsync(turn, new ToolCallPart { Id = "c1", Name = "read", Arguments = "{\"path\":\"git commit -m x\"}" }, Ok());
            Check.True(await After(hook, turn) is null, "a file read is not a commit");
        });

        r.Add("notice: two commits in one model call are one notice", async () =>
        {
            var env = new Env { Open = [Idea("First"), Idea("Second"), Idea("Third")] };
            var hook = env.Hook();
            var run = env.Run();
            var turn = TurnWithIdeas(run);
            await hook.OnAfterToolCallAsync(turn, Commit("git commit -m a"), Ok());
            await hook.OnAfterToolCallAsync(turn, Commit("git commit -m b"), Ok());
            var d = await After(hook, turn);
            Check.Contains(d!.Text, "First");
            Check.Contains(d.Text, "Second");
            Check.Contains(d.Text, "Third");
            Check.Equal(1, d!.Text.Split("A commit just landed").Length - 1);
        });

        r.Add("notice: the open ideas are bounded and clipped", () =>
        {
            var open = Enumerable.Range(1, 20).Select(i => Idea($"A very long idea title that goes on and on and on, number {i}, with more words after it")).ToList();
            var titles = IdeaCommitNoticeHook.Titles(open);
            Check.Equal(IdeaCommitNoticeHook.MaxTitles, titles.Count);
            Check.True(titles.All(t => t.Length <= IdeaCommitNoticeHook.MaxTitleLength + 4), "each title is clipped");
            var text = IdeaCommitNoticeHook.Text(new IdeaCommitNoticeHook.Pending("NetPI", titles));
            Check.True(text.Length < 1400, $"the notice stays short (was {text.Length})");
        });
    }

    /// <summary>An open idea as the repository hands it over.</summary>
    private static JsonObject Idea(string title) => new() { ["id"] = "idea-" + title.GetHashCode().ToString("N")[..6], ["title"] = title, ["status"] = "open" };
}
