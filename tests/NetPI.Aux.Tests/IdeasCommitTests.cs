using System.Text.Json.Nodes;
using NetPI.Ideas;

namespace NetPI.Aux.Tests;

/// <summary>
/// The commit check's own bookkeeping: where it is in a repository's history, what it does with a burst, a worktree, a
/// decision that fails and a history that was rewritten (docs/plans/2026-09-29-ideas-flow-storage-handoff.md, assignment
/// C). The repository is a fake that behaves like <c>git log since..until</c>, so each case is exact.
/// </summary>
public static class IdeasCommitTests
{
    /// <summary>A repository with a list of commits, oldest first, and the git semantics the sweep relies on.</summary>
    private sealed class Repo(string path, string gitDir)
    {
        public string Path { get; } = path;
        public string GitDir { get; } = gitDir;
        public List<(string Hash, string Subject)> Commits { get; } = [];
        private int _next;
        public bool Reachable { get; set; } = true;
        /// <summary>Every range the plugin asked for, oldest first — that is how a gap (or its absence) is seen.</summary>
        public List<(string? Since, string? Until)> Asked { get; } = [];
        public Func<(string? Since, string? Until, int Limit), (IReadOnlyList<(string, string)> Commits, bool Reachable)>? Answer { get; set; }

        public (IReadOnlyList<(string, string)> Commits, bool Reachable) Read(string? since, string? until, int limit)
        {
            Asked.Add((since, until));
            if (Answer is not null) return Answer((since, until, limit));
            var at = since is null ? -1 : Commits.FindIndex(c => c.Hash == since);
            if (since is not null && at < 0) return ([], Reachable);
            var upper = until is null ? Commits.Count : Commits.FindIndex(c => c.Hash == until);
            if (until is not null && upper < 0) return ([], Reachable);
            // git answers a range newest first, and -n takes the newest of them.
            var slice = Commits.Skip(at + 1).Take(upper < 0 ? Commits.Count : upper).Reverse().Take(limit).ToList();
            return (slice, true);
        }

        public void Commit(string subject)
        {
            var hash = $"{(++_next):D7}" + new string('c', 33);
            Commits.Add((hash, subject));
        }
    }

    private sealed class Env
    {
        public FakePluginContext Ctx { get; }
        public Repo Repo { get; }
        public ProjectInfo Project { get; }
        public SessionInfo Session { get; }
        public string Backlog => Path.Combine(Ctx.Paths.Home, "ideas.json");
        public string PendingFile => Path.Combine(Ctx.Paths.Home, "ideas-pending.json");
        public int Decisions { get; set; }
        /// <summary>How the link question is answered (letters → probability).</summary>
        public Func<JsonArray, Dictionary<string, double>> Link { get; set; } = labels => new() { [labels[0]!.Str()!] = 0.95, [labels[^1]!.Str()!] = 0.03 };
        /// <summary>When set, the decision throws this many times before answering.</summary>
        public int FailDecisions { get; set; }

        public Env(string? home = null, Repo? repo = null)
        {
            Ctx = new FakePluginContext(home ?? T.TempDir("ideas-home"));
            var dir = repo?.Path ?? T.TempDir("ideas-repo");
            Repo = repo ?? new Repo(dir, Path.Combine(dir, ".git"));
            Project = Ctx.SessionsFake.CreateProject("Demo", dir);
            Session = Ctx.SessionsFake.CreateSession(new SessionInfo { Title = "s", ProjectId = Project.Id });
            Ctx.RpcFake.Register("files.commits", (req, _) =>
            {
                var (commits, reachable) = Repo.Read(req.Str("since"), req.Str("until"), req.Int("limit") ?? 20);
                var list = new JsonArray();
                foreach (var (hash, subject) in commits)
                    list.Add(new JsonObject { ["hash"] = hash, ["short"] = hash[..7], ["subject"] = subject, ["author"] = "t", ["at"] = "2026-09-29T10:00:00Z" });
                return Task.FromResult<object?>(new JsonObject
                {
                    ["repo"] = Repo.Path,
                    ["gitDir"] = Repo.GitDir,
                    ["commonDir"] = Repo.GitDir,
                    ["reachable"] = reachable,
                    ["commits"] = list,
                });
            });
            Ctx.RpcFake.Register("decide.decision", (req, _) =>
            {
                if (FailDecisions > 0) { FailDecisions--; throw new InvalidOperationException("the decision is down"); }
                Decisions++;
                var labels = req.Params.GetProperty("branches")[0].GetProperty("labels").EnumerateArray().Select(e => e.GetString() ?? "").ToList();
                return Task.FromResult<object?>(Answer(Link(new JsonArray(labels.Select(l => (JsonNode)JsonValue.Create(l)!).ToArray()))));
            });
        }

        public IdeaCommitCheck? Check { get; private set; }

        public async Task StartAsync()
        {
            await new IdeasPlugin().StartAsync(Ctx, CancellationToken.None);
            Check = Ctx.Owner.Owned.OfType<IdeaCommitCheck>().FirstOrDefault();
        }

        /// <summary>
        /// Close and reopen: a new process over the same home and the same repository. The project keeps its id (a
        /// restart does not renumber projects), and it is restored before the plugin starts, so the first sweep of the
        /// new process already sees the same ideas in scope.
        /// </summary>
        public async Task<Env> RestartAsync(string? projectId = null)
        {
            var home = Ctx.Paths.Home;
            projectId ??= Project.Id;
            Ctx.Unload();
            var next = new Env(home, Repo) { Project = { Id = projectId } };
            await next.StartAsync();
            return next;
        }

        public async Task<JsonObject> Rpc(string method, JsonObject p) => (JsonObject)(await Ctx.RpcFake.Call(method, p))!;

        public async Task<string> AddIdea(string title, string summary = "")
        {
            var idea = await Rpc("ideas.add", new JsonObject { ["sessionId"] = Session.Id, ["idea"] = new JsonObject { ["title"] = title, ["summary"] = summary } });
            return idea["id"].Str()!;
        }

        /// <summary>The newest commit this NetPI has read for the repository, as the database holds it.</summary>
        public string? Cursor => IdeasRepository.Open(Ctx.Db, Ctx.Log, Ctx.Paths.DatabaseFile).LastSeen(Repo.Path);

        public async Task<List<string>> CommitsOn(string ideaId)
        {
            var idea = await Rpc("ideas.get", new JsonObject { ["id"] = ideaId });
            return (idea["commits"] as JsonArray ?? []).Select(c => c!["short"]!.Str()!).ToList();
        }

        public async Task<int> Cards() => ((JsonArray)(await Rpc("ideas.suggestions", new JsonObject()))["suggestions"]!.AsArray()).Count;
    }

    public static void Register(TestRunner r)
    {
        r.Add("ideas commits: a burst of 45 commits is read whole — oldest first, no gap", async () =>
        {
            var env = new Env();
            await env.StartAsync();
            var idea = await env.AddIdea("Sweep the backlog");
            for (var i = 1; i <= 45; i++) env.Repo.Commit($"step {i} of the sweep");

            // Two sweeps: the first reads its bounded pages, the second finds nothing new.
            await env.Check!.SweepNowAsync();
            var read = await env.CommitsOn(idea);
            Check.Equal(45, read.Count, "every commit is linked, not just the newest 20");
            Check.Equal(45, new HashSet<string>(read).Count, "none twice");
            Check.Equal(env.Repo.Commits[0].Hash[..7], read[0], "oldest first");
            Check.Equal(env.Repo.Commits[44].Hash[..7], read[44], "newest last");
            Check.Equal(env.Repo.Commits[44].Hash, env.Cursor, "the cursor is at the newest commit read");
            await env.Check.SweepNowAsync();
            Check.Equal(45, (await env.CommitsOn(idea)).Count, "a second sweep adds nothing");
            env.Ctx.Unload();
        });

        r.Add("ideas commits: commits made while NetPI was closed are read, not skipped", async () =>
        {
            var env = new Env();
            await env.StartAsync();
            var idea = await env.AddIdea("Offline work");
            env.Repo.Commit("first while it ran");
            await env.Check!.SweepNowAsync();
            Check.Equal(1, (await env.CommitsOn(idea)).Count);

            // NetPI is closed, three more commits happen, it starts again.
            env.Repo.Commit("made while closed 1");
            env.Repo.Commit("made while closed 2");
            env.Repo.Commit("made while closed 3");
            var again = await env.RestartAsync(env.Project.Id);
            // The start sweeps in the background, so a commit may already be recorded here; what must not happen is the
            // cursor being re-anchored at HEAD, which would skip the three commits made while NetPI was closed.
            await again.Check!.SweepNowAsync();
            Check.Equal(4, (await again.CommitsOn(idea)).Count, "the cursor was not re-anchored at HEAD");
            Check.Equal(env.Repo.Commits[3].Hash, again.Cursor);
            again.Ctx.Unload();
        });

        r.Add("ideas commits: a worktree (.git is a file) is still watched and swept", async () =>
        {
            var env = new Env();
            // A worktree: .git is a file pointing at the real git directory elsewhere.
            var real = Path.Combine(env.Repo.Path, "..", "real-git");
            Directory.CreateDirectory(real);
            File.WriteAllText(Path.Combine(env.Repo.Path, ".git"), $"gitdir: {real}\n");
            await env.StartAsync();
            Check.True(File.Exists(Path.Combine(env.Repo.Path, ".git")), "this really is a .git file");
            Check.Contains(env.Ctx.Log.Lines.FirstOrDefault(l => l.Contains("watching")) ?? "", "watching");

            var idea = await env.AddIdea("Worktree work");
            env.Repo.Commit("a commit in the worktree");
            await env.Check!.SweepNowAsync();
            Check.Equal(1, (await env.CommitsOn(idea)).Count, "the sweep covers a repository it cannot watch");
            env.Ctx.Unload();
        });

        r.Add("ideas commits: a decision that fails leaves the cursor, and the commit is read again", async () =>
        {
            var env = new Env();
            await env.StartAsync();
            var idea = await env.AddIdea("Retry me");
            env.Repo.Commit("first");
            env.Repo.Commit("second");
            env.FailDecisions = 5; // the model is down
            await env.Check!.SweepNowAsync();
            Check.Equal(0, (await env.CommitsOn(idea)).Count, "nothing was linked");
            Check.Equal(null, env.Cursor, "and the cursor did not move: the commits are still unseen");
            Check.Contains(env.Ctx.Log.Lines.FirstOrDefault(l => l.Contains("not read yet")) ?? "", "not read yet");

            env.FailDecisions = 0;
            await env.Check.SweepNowAsync();
            Check.Equal(2, (await env.CommitsOn(idea)).Count, "both are read on the next sweep");
            Check.Equal(env.Repo.Commits[1].Hash, env.Cursor);
            env.Ctx.Unload();
        });

        r.Add("ideas commits: a rewritten history re-anchors the cursor instead of stalling", async () =>
        {
            var env = new Env();
            await env.StartAsync();
            await env.AddIdea("After a rebase");
            env.Repo.Commit("one");
            await env.Check!.SweepNowAsync();
            Check.Equal(env.Repo.Commits[0].Hash, env.Cursor);
            var asked = env.Decisions;

            // The history the cursor pointed at is gone (a rebase, a branch switch, a replaced repository).
            env.Repo.Commits.Clear();
            env.Repo.Reachable = false;
            env.Repo.Commit("one (rewritten)");
            env.Repo.Commit("two (rewritten)");
            await env.Check.SweepNowAsync();
            Check.Contains(env.Ctx.Log.Lines.FirstOrDefault(l => l.Contains("rewritten")) ?? "", "re-anchored", "and it says so");
            Check.True(env.Cursor is { Length: > 0 }, "the cursor points at something that exists: " + env.Cursor);
            Check.Equal(asked, env.Decisions, "nothing is read out of the history that is not there any more");
            env.Ctx.Unload();
        });

        r.Add("ideas commits: the check follows its setting and forgets a project that is gone", async () =>
        {
            var env = new Env();
            await env.StartAsync();
            var idea = await env.AddIdea("Setting and projects");
            env.Repo.Commit("while it is on");
            await env.Check!.SweepNowAsync();
            Check.Equal(1, (await env.CommitsOn(idea)).Count);

            env.Ctx.SettingsFake.Set("ideas.closeOnCommit", false);
            env.Repo.Commit("while it is off");
            await env.Check!.SweepNowAsync();
            Check.Equal(1, (await env.CommitsOn(idea)).Count, "off means off, even for a sweep already running");
            env.Ctx.SettingsFake.Set("ideas.closeOnCommit", true);
            await env.Check.SweepNowAsync();
            Check.Equal(2, (await env.CommitsOn(idea)).Count, "on again, and it picks up where it stopped");

            // the project (and with it the repository) is deleted
            env.Ctx.SessionsFake.DeleteProject(env.Project.Id);
            env.Repo.Commit("after the project was removed");
            await env.Check.SweepNowAsync();
            Check.Equal(2, (await env.CommitsOn(idea)).Count, "its repository is no longer watched");
            env.Ctx.Unload();
        });

        r.Add("ideas commits: a commit names two idea ids, and a pick-one links only the best", async () =>
        {
            var env = new Env();
            await env.StartAsync();
            var first = await env.AddIdea("Rewrite the transcript indexer");
            var second = await env.AddIdea("Cache the transcript indexer");
            var decided = new List<JsonArray>();
            env.Link = labels =>
            {
                if (labels.Count > 2) decided.Add((JsonArray)labels.DeepClone()); // the link question, not the done one
                // The first option and the second both clear the threshold: the distribution is mutually exclusive, so
                // only the winner is linked (an old flow linked everything above 0.7).
                var probs = new Dictionary<string, double> { [labels[0]!.Str()!] = 0.72, [labels[1]!.Str()!] = 0.71, [labels[^1]!.Str()!] = 0.02 };
                return probs;
            };

            env.Repo.Commit($"harden the indexer ({first}, {second})");
            await env.Check!.SweepNowAsync();
            Check.Equal(0, decided.Count, "a commit that names its ideas needs no decision at all");
            Check.Equal(1, (await env.CommitsOn(first)).Count, "the named idea is linked");
            Check.Equal(1, (await env.CommitsOn(second)).Count, "both, when both are named");

            env.Repo.Commit("more work on the indexer");
            await env.Check.SweepNowAsync();
            Check.Equal(1, decided.Count, "an unnamed commit is asked about");
            Check.Equal(2, (await env.CommitsOn(first)).Count, "the named commit, and this one (only the best option)");
            Check.Equal(1, (await env.CommitsOn(second)).Count, "not the idea that merely cleared the threshold");
            env.Ctx.Unload();
        });

        r.Add("ideas commits: nothing is offered for a commit that only advances an idea", async () =>
        {
            var env = new Env();
            await env.StartAsync();
            var idea = await env.AddIdea("Two parts", "Part one is done; part two is not.");
            env.Link = labels => new() { [labels[0]!.Str()!] = 0.95, [labels[^1]!.Str()!] = 0.03 };
            env.Ctx.RpcFake.Register("decide.decision", (req, _) =>
            {
                env.Decisions++;
                var labels = req.Params.GetProperty("branches")[0].GetProperty("labels").EnumerateArray().Select(e => e.GetString() ?? "").ToList();
                if (labels is ["DONE", "MORE"]) return Task.FromResult<object?>(Answer(new() { ["DONE"] = 0.2, ["MORE"] = 0.8 }));
                return Task.FromResult<object?>(Answer(new() { [labels[0]] = 0.95, [labels[^1]] = 0.03 }));
            });

            env.Repo.Commit("part one");
            await env.Check!.SweepNowAsync();
            Check.Equal(1, (await env.CommitsOn(idea)).Count, "the commit is recorded");
            Check.Equal(0, await env.Cards(), "but it is not an offer");
            env.Ctx.Unload();
        });
    }

    private static JsonObject Answer(Dictionary<string, double> probs)
    {
        var answer = new JsonObject();
        foreach (var (k, v) in probs) answer[k] = v;
        return new JsonObject { ["branches"] = new JsonArray(new JsonObject { ["id"] = "pick", ["probabilities"] = answer }) };
    }
}
