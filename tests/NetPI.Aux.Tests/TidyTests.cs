using System.Text.Json.Nodes;
using NetPI.Tidy;

namespace NetPI.Aux.Tests;

/// <summary>
/// The end-of-turn tidy reminder (plugins/NetPI.Tidy): what counts as left behind in an agent's own worktree and in a
/// shared checkout, the scratch it created, and when the agent is left alone (a question to the user, the same
/// leftovers again, a turn that is not an end). Real git repositories; no decision model registered, so the plain
/// "ends with a question" rule decides waiting.
/// </summary>
public static class TidyTests
{
    private sealed class Env
    {
        public FakePluginContext Ctx { get; } = new(T.TempDir("tidy-home"));
        public string Repo { get; }
        public bool Git { get; }
        public SessionInfo Session { get; }

        public Env()
        {
            Repo = Path.Combine(T.TempDir("tidy"), "repo");
            Directory.CreateDirectory(Repo);
            Git = TestGit.Available() && TestGit.Run(Repo, "init", "-q", "-b", "main");
            if (Git)
            {
                File.WriteAllText(Path.Combine(Repo, "README.md"), "x\n");
                File.WriteAllText(Path.Combine(Repo, ".gitignore"), "/scratch/\n/.worktrees/\n");
                Commit(Repo, "init");
            }
            Session = Ctx.SessionsFake.CreateSession(new SessionInfo { Title = "s" });
        }

        public static void Commit(string dir, string message)
        {
            TestGit.Run(dir, "add", "-A");
            TestGit.Run(dir, "-c", "user.name=t", "-c", "user.email=t@t", "commit", "-q", "-m", message);
        }

        /// <summary>A linked worktree on branch <paramref name="branch"/>, as an agent's own checkout.</summary>
        public string Worktree(string branch)
        {
            var path = Path.Combine(Repo, ".worktrees", branch);
            TestGit.Run(Repo, "worktree", "add", "-q", path, "-b", branch);
            return path;
        }

        public TidyHook Hook() => new(() => Ctx.Settings, Ctx.Services);

        public async Task<AgentRunContext> StartAsync(TidyHook hook, string cwd)
        {
            var run = T.Run(Ctx, T.Model(), Session);
            run.Cwd = cwd;
            await hook.OnRunStartAsync(run);
            return run;
        }
    }

    private static async Task<TurnDecision?> End(TidyHook hook, AgentRunContext run, string text = "Done: the fix is in.") =>
        await hook.OnAfterModelCallAsync(T.Turn(run), T.Assistant(text));

    private static void NeedGit(Env env) { if (!env.Git) Check.Skip("no git on PATH: tidy facts not checked"); }

    public static void Register(TestRunner r)
    {
        r.Add("tidy: uncommitted change in the agent's own worktree -> one notice that lists it", async () =>
        {
            var env = new Env(); NeedGit(env);
            var wt = env.Worktree("agent-a");
            var hook = env.Hook();
            var run = await env.StartAsync(hook, wt);
            File.WriteAllText(Path.Combine(wt, "new.txt"), "work\n");
            var d = await End(hook, run);
            Check.Equal(TurnAction.Inject, d!.Action);
            Check.Equal(TidyHook.NoticeKind, d.NoticeKind);
            Check.Contains(d.Text, "1 uncommitted change in your worktree");
            Check.Contains(d.Text, "new.txt");
            Check.Contains(d.Text, "say so in one line");
            Check.Equal(600, hook.Order);
            // once per run
            Check.True(await End(hook, run) is null, "a second end in the same run is not asked again");
        });

        r.Add("tidy: commits not merged into the base branch, then merged but the worktree kept", async () =>
        {
            var env = new Env(); NeedGit(env);
            var wt = env.Worktree("agent-b");
            File.WriteAllText(Path.Combine(wt, "f.txt"), "1\n");
            Env.Commit(wt, "work");
            var hook = env.Hook();
            var d = await End(hook, await env.StartAsync(hook, wt));
            Check.Contains(d!.Text, "branch agent-b has 1 commit not merged into main");

            TestGit.Run(env.Repo, "merge", "-q", "--ff-only", "agent-b");
            var d2 = await End(hook, await env.StartAsync(hook, wt));
            Check.Contains(d2!.Text, "branch agent-b is merged into main but its worktree");
            Check.Contains(d2.Text, "workspace retire");
        });

        r.Add("tidy: a clean worktree on a fresh branch and a clean checkout -> nothing", async () =>
        {
            var env = new Env(); NeedGit(env);
            var hook = env.Hook();
            Check.True(await End(hook, await env.StartAsync(hook, env.Repo)) is null, "clean main checkout");
        });

        r.Add("tidy: shared checkout -> only the files this run wrote count, never someone else's", async () =>
        {
            var env = new Env(); NeedGit(env);
            var hook = env.Hook();
            var run = await env.StartAsync(hook, env.Repo);
            File.WriteAllText(Path.Combine(env.Repo, "theirs.txt"), "owner's work\n");
            Check.True(await End(hook, run) is null, "another writer's uncommitted file is not this agent's");

            var run2 = await env.StartAsync(hook, env.Repo);
            File.WriteAllText(Path.Combine(env.Repo, "mine.txt"), "agent's work\n");
            var call = T.Call("c1", "write", new JsonObject { ["path"] = "mine.txt", ["content"] = "x" }.ToJsonString());
            await hook.OnAfterToolCallAsync(T.Turn(run2), call, new ToolResultPart { CallId = "c1", Name = "write", Content = "ok" });
            var d = await End(hook, run2);
            Check.Contains(d!.Text, "1 file you changed");
            Check.Contains(d.Text, "mine.txt");
            Check.NotContains(d.Text, "theirs.txt");
        });

        r.Add("tidy: scratch created during the run counts, scratch that was there before does not", async () =>
        {
            var env = new Env(); NeedGit(env);
            Directory.CreateDirectory(Path.Combine(env.Repo, "scratch", "2026-10-01-old"));
            var hook = env.Hook();
            var run = await env.StartAsync(hook, env.Repo);
            Directory.CreateDirectory(Path.Combine(env.Repo, "scratch", "2026-10-26-new"));
            var d = await End(hook, run);
            Check.Contains(d!.Text, "scratch you created this run is still there: 2026-10-26-new");
            Check.NotContains(d.Text, "2026-10-01-old");
        });

        r.Add("tidy: waiting for the user (a final question) and the same leftovers in the next run -> left alone", async () =>
        {
            var env = new Env(); NeedGit(env);
            var wt = env.Worktree("agent-c");
            File.WriteAllText(Path.Combine(wt, "x.txt"), "1\n");
            var hook = env.Hook();
            Check.True(await End(hook, await env.StartAsync(hook, wt), "Ready to merge. Should I merge it into main?") is null,
                "a question to the user is a reason to stop");
            Check.True(await End(hook, await env.StartAsync(hook, wt)) is not null, "the next run that just ends is told");
            Check.True(await End(hook, await env.StartAsync(hook, wt)) is null, "and not told the same leftovers again");
            File.WriteAllText(Path.Combine(wt, "y.txt"), "2\n");
            Check.True(await End(hook, await env.StartAsync(hook, wt)) is not null, "new leftovers are told");
        });

        r.Add("tidy: only a real end counts (tool calls, cut-offs, empty answers are other hooks' business); setting off", async () =>
        {
            var env = new Env(); NeedGit(env);
            var wt = env.Worktree("agent-d");
            File.WriteAllText(Path.Combine(wt, "x.txt"), "1\n");
            var hook = env.Hook();
            var run = await env.StartAsync(hook, wt);
            Check.True(await hook.OnAfterModelCallAsync(T.Turn(run), T.Assistant("next", T.Call("c", "bash"))) is null, "a tool call");
            var cut = T.Assistant("partial"); cut.StopReason = "length";
            Check.True(await hook.OnAfterModelCallAsync(T.Turn(run), cut) is null, "cut off");
            Check.True(await hook.OnAfterModelCallAsync(T.Turn(run), T.Assistant("")) is null, "empty");
            env.Ctx.Settings.Set("tidy.enabled", false);
            Check.True(await End(hook, await env.StartAsync(hook, wt)) is null, "switched off");
        });

        r.Add("tidy: the decision sees the task, the final message and the leftovers; a yes to waiting or keep-as-asked leaves the agent alone", async () =>
        {
            var env = new Env(); NeedGit(env);
            var wt = env.Worktree("agent-e");
            File.WriteAllText(Path.Combine(wt, "x.txt"), "1\n");
            var decide = new FakeDecision();
            env.Ctx.ServicesFake.Register<IDecisionService>(decide);
            var hook = env.Hook();

            decide.Answers = new() { ["waiting"] = 0.1, ["asks_user"] = 0.1, ["keep_asked"] = 0.9 };
            var run = await env.StartAsync(hook, wt);
            var turn = T.Turn(run, messages: [T.User("Fix it but leave the branch unmerged for my review."), T.Assistant("Done.")]);
            Check.True(await hook.OnAfterModelCallAsync(turn, T.Assistant("Done.")) is null, "the task asked to keep it");
            var state = decide.Last!.Body["state"]!.AsObject();
            Check.Contains(state["task_given"]!.GetValue<string>(), "leave the branch unmerged");
            Check.Contains(state["final_message"]!.GetValue<string>(), "Done.");
            Check.Contains(state["left_behind"]!.ToJsonString(), "uncommitted change");
            Check.True(decide.Last.Lane, "a single short check on the decision lane");

            decide.Answers = new() { ["waiting"] = 0.05, ["asks_user"] = 0.05, ["keep_asked"] = 0.05 };
            File.WriteAllText(Path.Combine(wt, "y.txt"), "2\n");
            Check.True(await End(hook, await env.StartAsync(hook, wt)) is not null, "not waiting -> the notice");
        });

        r.Add("tidy sweep: merged worktree and branch are [merged], unmerged work, stash, old dirty file and scratch are reported, excluded folders skipped", async () =>
        {
            var env = new Env(); NeedGit(env);
            var done = env.Worktree("done-wt");
            File.WriteAllText(Path.Combine(done, "d.txt"), "1\n"); Env.Commit(done, "done work");
            TestGit.Run(env.Repo, "merge", "-q", "--ff-only", "done-wt");
            var open = env.Worktree("open-wt");
            File.WriteAllText(Path.Combine(open, "o.txt"), "1\n"); Env.Commit(open, "open work");
            TestGit.Run(env.Repo, "branch", "merged-branch");                       // at main: merged, no worktree
            File.WriteAllText(Path.Combine(env.Repo, "README.md"), "stashed\n");
            TestGit.Run(env.Repo, "-c", "user.name=t", "-c", "user.email=t@t", "stash", "-q");
            File.WriteAllText(Path.Combine(env.Repo, "left.txt"), "x\n");
            Directory.CreateDirectory(Path.Combine(env.Repo, "scratch", "2026-10-01-probe"));

            var items = await TidySweep.SweepAsync([env.Repo], [], 0, DateTimeOffset.UtcNow.AddMinutes(1), CancellationToken.None);
            var text = TidySweep.Render(items, 0);
            Check.True(items.Any(i => i.Kind == "merged" && i.Branch == "done-wt" && i.Path is not null), text);
            Check.True(items.Any(i => i.Kind == "merged" && i.Branch == "merged-branch" && i.Path is null), text);
            Check.True(items.Any(i => i.Kind == "unmerged" && i.Branch == "open-wt"), text);
            Check.True(items.Any(i => i.Kind == "stash"), text);
            Check.True(items.Any(i => i.Kind == "dirty" && i.Text.Contains("left.txt")), text);
            Check.True(items.Any(i => i.Kind == "scratch" && i.Text.Contains("2026-10-01-probe")), text);
            Check.Contains(text, "safe to remove");

            var skipped = await TidySweep.SweepAsync([env.Repo], ["repo"], 0, DateTimeOffset.UtcNow.AddMinutes(1), CancellationToken.None);
            Check.Equal(0, skipped.Count);
            var fresh = await TidySweep.SweepAsync([env.Repo], [], 3, DateTimeOffset.UtcNow, CancellationToken.None);
            Check.False(fresh.Any(i => i.Kind is "merged" or "unmerged" or "dirty" or "scratch"), "work from today is not stale");
        });

        r.Add("tidy: written paths from write/edit arguments, the several-files form included", () =>
        {
            var cwd = Path.Combine(Path.GetTempPath(), "proj");
            var one = TidyHook.WrittenPaths(new JsonObject { ["file_path"] = "a/b.cs" }.ToJsonString(), cwd);
            Check.Equal(1, one.Count);
            Check.True(TidyFacts.Same(one[0], Path.Combine(cwd, "a", "b.cs")));
            var many = TidyHook.WrittenPaths(new JsonObject
            {
                ["files"] = new JsonArray(new JsonObject { ["path"] = "x.md" }, new JsonObject { ["path"] = Path.Combine(cwd, "y.md") }),
            }.ToJsonString(), cwd);
            Check.Equal(2, many.Count);
            Check.Equal(0, TidyHook.WrittenPaths("not json", cwd).Count);
            Check.True(TidyHook.EndsWithQuestion("Merged.\n\nShould I publish it?**"));
            Check.False(TidyHook.EndsWithQuestion("What changed? Everything is merged."));
            return Task.CompletedTask;
        });

        r.Add("tidy: status lines of git -> repository paths (renames to their target)", () =>
        {
            var p = TidyFacts.StatusPaths(" M src/a.cs\n?? new dir/b.txt\nR  old.cs -> new.cs\n");
            Check.Equal(3, p.Count);
            Check.Equal("new dir/b.txt", p[1]);
            Check.Equal("new.cs", p[2]);
            return Task.CompletedTask;
        });
    }

    /// <summary>A decision service that answers every question with a fixed p(yes), as the Decide plugin's TypeSafe shape.</summary>
    private sealed class FakeDecision : IDecisionService
    {
        public Dictionary<string, double> Answers { get; set; } = [];
        public DecisionRequest? Last { get; private set; }

        public Task<JsonObject> EvaluateAsync(DecisionRequest request, CancellationToken ct)
        {
            Last = request;
            var o = new JsonObject();
            foreach (var (k, p) in Answers) o[k] = new JsonObject { ["type"] = "noul", ["noul"] = p };
            return Task.FromResult(o);
        }
    }
}
