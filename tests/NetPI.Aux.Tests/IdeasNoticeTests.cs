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
            Check.Equal(1, d.Text!.Split("A commit just landed").Length - 1);
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

        // The attribution checks below run against real temporary git repositories (the repository rule: never a fake
        // .git): the project's own checkout, a linked worktree of it, and an unrelated repository. Select them all with
        // -Only "notice: attribution".

        r.Add("notice: attribution: a relative cwd counts as the session's own directory", () =>
        {
            using var env = GitRepo();
            if (!env.GitAvailable) { Console.WriteLine("    (no git on PATH: skipped)"); return; }
            var run = env.Run();
            Check.True(IdeaCommitNoticeHook.InProject("git commit -m x", "plugins", run, env.Project), "relative: resolved against the run's cwd, which is the project");
            Check.True(IdeaCommitNoticeHook.InProject("git commit -m x", null, run, env.Project), "no cwd: the run's own directory");
        });

        r.Add("notice: attribution: git -C a worktree of the same repository is the project's commit", async () =>
        {
            using var env = GitRepo(probed: true);
            if (!env.GitAvailable) { Console.WriteLine("    (no git on PATH: skipped)"); return; }
            var run = env.Run();
            Check.True(IdeaCommitNoticeHook.InProject($"git -C {env.Worktree} commit -m x", null, run, env.Project),
                "the -C directory is another checkout of the session's repository");
            // End to end through the hook, with the cwd argument still the project: the notice has to land.
            var hook = env.Hook();
            var turn = TurnWithIdeas(run);
            await hook.OnAfterToolCallAsync(turn, Commit($"git -C {env.Worktree} commit -m x", cwd: env.Project.Path), Ok());
            Check.True(await After(hook, turn) is { Action: TurnAction.Inject }, "the notice still lands");
        });

        r.Add("notice: attribution: git -C an unrelated repository is not the project's", async () =>
        {
            using var env = GitRepo(probed: true);
            if (!env.GitAvailable) { Console.WriteLine("    (no git on PATH: skipped)"); return; }
            var run = env.Run();
            Check.False(IdeaCommitNoticeHook.InProject($"git -C {env.Unrelated} commit -m x", null, run, env.Project),
                "a different repository is another project's work");
            var hook = env.Hook();
            var turn = TurnWithIdeas(run);
            await hook.OnAfterToolCallAsync(turn, Commit($"git -C {env.Unrelated} commit -m x"), Ok());
            Check.True(await After(hook, turn) is null, "no notice for another repository");
        });

        r.Add("notice: attribution: an absolute cwd in another worktree of the same repository is attributed", () =>
        {
            using var env = GitRepo(probed: true);
            if (!env.GitAvailable) { Console.WriteLine("    (no git on PATH: skipped)"); return; }
            var run = env.Run();
            Check.True(IdeaCommitNoticeHook.InProject("git commit -m x", env.Worktree, run, env.Project), "the worktree itself");
            Check.True(IdeaCommitNoticeHook.InProject("git commit -m x", Path.Combine(env.Worktree, "sub"), run, env.Project), "a subdirectory of the worktree");
            Check.False(IdeaCommitNoticeHook.InProject("git commit -m x", env.Unrelated, run, env.Project), "an unrelated repository is not");
        });

        r.Add("notice: attribution: the same path in different case is the same path", () =>
        {
            using var env = GitRepo(probed: true);
            if (!env.GitAvailable) { Console.WriteLine("    (no git on PATH: skipped)"); return; }
            var run = env.Run();
            var flipped = MixedCase(env.Worktree);
            if (OperatingSystem.IsWindows())
                Check.True(IdeaCommitNoticeHook.InProject("git commit -m x", flipped, run, env.Project), "case-insensitive: the same path, different spelling");
            else
                Check.False(IdeaCommitNoticeHook.InProject("git commit -m x", flipped, run, env.Project), "on Linux a different case is a different, absent path");
        });

        r.Add("notice: attribution: a session bound to a worktree attributes commits of its repository", () =>
        {
            using var env = GitRepo(probed: true);
            if (!env.GitAvailable) { Console.WriteLine("    (no git on PATH: skipped)"); return; }
            var run = env.Run();
            run.Cwd = env.Worktree;                                   // a bound session works in its workspace
            run.SetWorkspace(new WorkspaceBinding("ws1", env.Worktree, RepoCommonDir: env.CommonDir, Kind: "worktree"));
            Check.True(IdeaCommitNoticeHook.InProject("git commit -m x", null, run, env.Project), "a commit in the session's own worktree");
            Check.True(IdeaCommitNoticeHook.InProject("git commit -m x", env.Main, run, env.Project), "a commit in the main checkout of the same repository");
            Check.False(IdeaCommitNoticeHook.InProject("git commit -m x", env.Unrelated, run, env.Project), "a commit in another repository is not");
            // The binding recorded no common dir: the probe fills the gap.
            run.SetWorkspace(new WorkspaceBinding("ws2", env.Worktree, Kind: "worktree"));
            Check.True(IdeaCommitNoticeHook.InProject("git commit -m x", env.Main, run, env.Project), "the probe finds the worktree's repository");
        });

        r.Add("notice: attribution: without a probe the path-containment fallback decides", () =>
        {
            using var env = GitRepo(probed: false);
            if (!env.GitAvailable) { Console.WriteLine("    (no git on PATH: skipped)"); return; }
            var run = env.Run();
            Check.True(IdeaCommitNoticeHook.InProject("git commit -m x", null, run, env.Project), "no cwd: the run's own directory");
            Check.True(IdeaCommitNoticeHook.InProject("git commit -m x", "plugins", run, env.Project), "a relative cwd: the session's own directory");
            Check.True(IdeaCommitNoticeHook.InProject("git commit -m x", Path.Combine(env.Main, "plugins"), run, env.Project), "an absolute cwd inside the checkout");
            Check.False(IdeaCommitNoticeHook.InProject("git commit -m x", env.Unrelated, run, env.Project), "an absolute cwd outside the checkout");
            Check.False(IdeaCommitNoticeHook.InProject("git commit -m x", env.Worktree, run, env.Project),
                "a worktree of the same repository: the old containment check cannot see through it without a probe");
        });

        r.Add("notice: attribution: the -C redirection a commit obeys", () =>
        {
            Check.Equal("/x/y", IdeaCommitNoticeHook.GitCdir("git -C /x/y commit -m x"), "space after -C");
            Check.Equal("/x/y", IdeaCommitNoticeHook.GitCdir("git -C/x/y commit -m x"), "no space after -C");
            Check.Equal("/x/y", IdeaCommitNoticeHook.GitCdir("git -C \"/x/y\" commit -m x"), "a double-quoted path");
            Check.Equal("/x/y", IdeaCommitNoticeHook.GitCdir("git -C'/x/y' commit -m x"), "a single-quoted path");
            Check.Equal("/x/y", IdeaCommitNoticeHook.GitCdir("git -C\"/x/y\" commit -m x"), "a quoted path glued to -C");
            Check.Equal("/b", IdeaCommitNoticeHook.GitCdir("git -C /a -C /b commit -m x"), "the last -C wins");
            Check.Equal("/b", IdeaCommitNoticeHook.GitCdir("git -C /a status; git -C /b commit -m x"), "only the commit command's -C counts");
            Check.Equal("/a", IdeaCommitNoticeHook.GitCdir("git -C /a commit -m x; git push"), "the commit's own -C, nothing after it");
            Check.True(IdeaCommitNoticeHook.GitCdir("git commit -m x") is null, "no -C: no redirection");
            Check.True(IdeaCommitNoticeHook.GitCdir("git -c user.name=t commit") is null, "-c is a config option, not a directory");
            Check.True(IdeaCommitNoticeHook.GitCdir("git -C \"\" commit") is null, "an empty -C value is \"stay put\"");
            Check.Equal("/b", IdeaCommitNoticeHook.GitCdir("git --git-dir=/g -C /b commit"), "the values of other global options are skipped");
            Check.Equal("/x/y", IdeaCommitNoticeHook.GitCdir("sudo git -C /x/y commit"), "a git word behind sudo");
        });
    }

    /// <summary>An open idea as the repository hands it over.</summary>
    private static JsonObject Idea(string title) => new() { ["id"] = "idea-" + title.GetHashCode().ToString("N")[..6], ["title"] = title, ["status"] = "open" };

    // ------------------------------------------------------------------ real repositories for the attribution checks

    /// <summary>
    /// The fixture the attribution checks run in: a temporary git repository (the project's checkout, one commit in
    /// it), a linked worktree of it (a sibling directory, the same repository), and an unrelated repository in its own
    /// right — built with real git, never a fake <c>.git</c>.
    /// </summary>
    private sealed class GitEnv : IDisposable
    {
        public FakePluginContext Ctx { get; }
        public bool GitAvailable { get; }
        /// <summary>The project's own checkout (a real repository with one commit).</summary>
        public string Main { get; }
        /// <summary>A linked worktree of the same repository: a sibling directory git made of it.</summary>
        public string Worktree { get; } = "";
        /// <summary>An unrelated repository, on purpose.</summary>
        public string Unrelated { get; } = "";
        public ProjectInfo Project { get; }
        public SessionInfo Session { get; }
        public List<JsonObject> Open { get; } = [Idea("Something")];

        public GitEnv(bool probed)
        {
            Ctx = new FakePluginContext();
            Main = T.TempDir("notice-main");
            GitAvailable = Git(Main, "init", "-q", "-b", "main");
            if (GitAvailable)
            {
                File.WriteAllText(Path.Combine(Main, "a.txt"), "a");
                Git(Main, "add", "-A");
                Git(Main, "-c", "user.name=t", "-c", "user.email=t@t", "commit", "-q", "-m", "init");
                // A linked worktree: a sibling checkout of the same repository (HEAD, detached — the branch state is
                // irrelevant to the identity the checks care about).
                Worktree = T.TempDir("notice-wt");
                Git(Main, "worktree", "add", "-q", Worktree, "HEAD");
                Directory.CreateDirectory(Path.Combine(Worktree, "sub"));
                Unrelated = T.TempDir("notice-other");
                Git(Unrelated, "init", "-q", "-b", "main");
            }
            Project = Ctx.SessionsFake.CreateProject("Repo", Main);
            Session = Ctx.SessionsFake.CreateSession(new SessionInfo { Title = "s", ProjectId = Project.Id });
            if (probed && GitAvailable)
                Ctx.Services.Register<IWorkspaceRepoProbe>(new RealGitProbe());
        }

        public IdeaCommitNoticeHook Hook() => new(() => Ctx.Settings, _ => Open);

        public AgentRunContext Run()
        {
            var run = T.Run(Ctx, T.Model(), Session);
            run.Project = Project;
            run.Cwd = Main;
            return run;
        }

        /// <summary>The repository's common dir as git reports it: the identity the worktree shares with Main.</summary>
        public string CommonDir => GitOut(Main, "rev-parse", "--path-format=absolute", "--git-common-dir")!;

        public void Dispose()
        {
            Ctx.Unload();
            foreach (var dir in new[] { Main, Worktree, Unrelated })
            {
                try { Directory.Delete(dir, true); } catch { }   // a skipped fixture leaves nothing behind
            }
        }
    }

    private static GitEnv GitRepo(bool probed = false) => new(probed);

    /// <summary>The probe the workspaces plugin registers, here answered by real git in the temporary repositories.</summary>
    private sealed class RealGitProbe : IWorkspaceRepoProbe
    {
        public string? CommonDirOf(string path) => GitOut(path, "rev-parse", "--path-format=absolute", "--git-common-dir");
        public string? BranchOf(string path) => null;
        public string? HeadOf(string path) => null;
    }

    private static bool Git(string cwd, params string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("git")
        { WorkingDirectory = cwd, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        try
        {
            using var p = System.Diagnostics.Process.Start(psi)!;
            p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            p.WaitForExit();
            return p.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception) { return false; }
    }

    private static string? GitOut(string cwd, params string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("git")
        { WorkingDirectory = cwd, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        try
        {
            using var p = System.Diagnostics.Process.Start(psi)!;
            var stdout = p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            p.WaitForExit();
            return p.ExitCode == 0 ? stdout.Trim() : null;
        }
        catch (System.ComponentModel.Win32Exception) { return null; }
    }

    /// <summary>The same path with one letter's case flipped: on Windows the same path, on Linux a different, absent one.</summary>
    private static string MixedCase(string path)
    {
        var dir = Path.GetDirectoryName(path) ?? string.Empty;
        var name = Path.GetFileName(path);
        for (var i = 0; i < name.Length; i++)
        {
            if (!char.IsLetter(name[i])) continue;
            var flipped = char.IsUpper(name[i]) ? char.ToLowerInvariant(name[i]) : char.ToUpperInvariant(name[i]);
            return Path.Combine(dir, name[..i] + flipped + name[(i + 1)..]);
        }
        return path;
    }
}
