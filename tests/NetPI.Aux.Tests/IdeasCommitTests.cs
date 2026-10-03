using System.Text.Json.Nodes;
using NetPI.Ideas;

namespace NetPI.Aux.Tests;

/// <summary>
/// The commit check's own bookkeeping: where it is in a repository's history, what it does with a burst, a worktree, a
/// decision that fails and a history that was rewritten (docs/archive/2026-09-29-ideas-flow-storage-handoff.md, assignment
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
        /// <summary>Every history read: what it was asked with (a null entry: the caller carried it not).</summary>
        public List<(string? Cwd, string? GitDir, string? CommonDir)> Roots { get; } = [];
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
        /// <summary>Hashes the repository cannot show a patch for: a commit from another repository, or one whose
        /// history was rewritten away.</summary>
        public HashSet<string> Unreadable { get; } = [];
        /// <summary>Hashes whose patch git cut: the commit is there, its patch is not whole.</summary>
        public HashSet<string> Truncated { get; } = [];
        /// <summary>How long the scripted patch of every commit is (a rework of many commits outgrows the bound).</summary>
        public int PatchChars { get; set; } = 0;
        /// <summary>
        /// Run when a patch is asked for, before it is answered: how a test edits the idea while its evidence is
        /// being read. It runs inside the synchronous handler on purpose — the checks start the first sweep without
        /// waiting for it, and a handler that really yielded would let a test assert before the plugin watched
        /// anything.
        /// </summary>
        public Action<string>? OnPatch { get; set; }
        /// <summary>How the link question is answered (letters → probability).</summary>
        public Func<JsonArray, Dictionary<string, double>> Link { get; set; } = labels => new() { [labels[0]!.Str()!] = 0.95, [labels[^1]!.Str()!] = 0.03 };
        /// <summary>When set, the decision throws this many times before answering.</summary>
        public int FailDecisions { get; set; }

        public Env(string? home = null, Repo? repo = null)
        {
            Ctx = new FakePluginContext(home ?? T.TempDir("ideas-home"));
            Ctx.ModelsFake.VerifierResponder = _ => new ChatMessage { Role = MessageRole.Assistant, Parts = [new TextPart { Text = "{\"verified\":false,\"confidence\":0.95,\"reason\":\"These test commits advance the idea; they do not finish the plan\"}" }] };
            // The checks decide on a model that has to exist: admission is what the sweep goes through, and a sweep with
            // no model in the catalog has nothing to ask.
            Ctx.ModelsFake.Models.Add(new ModelInfo { Provider = "aiproxy", Id = "qwen3.8-27b", IsLocal = true, MaxOutputTokens = 16384 });
            var dir = repo?.Path ?? T.TempDir("ideas-repo");
            Repo = repo ?? new Repo(dir, Path.Combine(dir, ".git"));
            Project = Ctx.SessionsFake.CreateProject("Demo", dir);
            Session = Ctx.SessionsFake.CreateSession(new SessionInfo { Title = "s", ProjectId = Project.Id });
            Ctx.RpcFake.Register("files.commits", (req, _) =>
            {
                if (req.Str("hash") is { } patchHash)
                {
                    OnPatch?.Invoke(patchHash);
                    if (Unreadable.Contains(patchHash)) return Task.FromResult<object?>(new JsonObject { ["hash"] = patchHash });
                    var patch = PatchChars > 0
                        ? "diff --git a/src/impl.cs b/src/impl.cs\n+" + new string('x', PatchChars)
                        : "diff --git a/fix b/fix\n+scripted completed implementation";
                    return Task.FromResult<object?>(new JsonObject
                    { ["hash"] = patchHash, ["patch"] = patch, ["truncated"] = Truncated.Contains(patchHash) });
                }
                Repo.Roots.Add((req.Str("cwd"), req.Str("gitDir"), req.Str("commonDir")));
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

        /// <summary>The newest commit this NetPI has read for the repository, as the store holds it.</summary>
        public string? Cursor => IdeasRepository.Open(Ctx.Data, Ctx.Access, Ctx.Log, Ctx.Paths.Home).LastSeen(Repo.Path);

        /// <summary>Age a repository's cursor past any keep window: what a repository nobody has read for months looks like.</summary>
        public void AgeCursor()
        {
            var repos = Ctx.Data.Collection("repos", new CollectionSpec().Text("at"));
            var doc = repos.Get(Repo.Path);
            if (doc is null) return;
            doc["at"] = "2020-01-01T00:00:00Z";
            repos.Put(Repo.Path, doc);
        }

        public async Task<List<string>> CommitsOn(string ideaId)
        {
            var idea = await Rpc("ideas.get", new JsonObject { ["id"] = ideaId });
            return (idea["commits"] as JsonArray ?? []).Select(c => c!["short"]!.Str()!).ToList();
        }

        public async Task<int> Cards() => ((JsonArray)(await Rpc("ideas.suggestions", new JsonObject()))["suggestions"]!.AsArray()).Count;

        /// <summary>The idea's "Needs review" state, as the ideas.review RPC answers it (null: it carries none).</summary>
        public async Task<JsonObject?> Review(string ideaId)
        {
            var answer = await Rpc("ideas.review", new JsonObject { ["id"] = ideaId });
            return answer["review"] as JsonObject;
        }

        /// <summary>The idea's "Needs review" section, as the tab reads it.</summary>
        public async Task<JsonObject?> ReviewSection(string ideaId)
        {
            var idea = await Rpc("ideas.get", new JsonObject { ["id"] = ideaId });
            return (idea["sections"] as JsonArray ?? []).OfType<JsonObject>().FirstOrDefault(s => s["title"]!.Str() == IdeaReview.Title);
        }
    }

    /// <summary>A backend with no free slot: the wait runs out and admission drops the work, exactly as in the app.</summary>
    private sealed class FullScheduler : IAgentScheduler
    {
        public bool Full { get; set; } = true;
        public int Asked;

        private sealed class Slot : IAgentSlot
        {
            public string Key => "full";
            public string AgentId => "ideas";
            public DateTimeOffset AcquiredAt => DateTimeOffset.UtcNow;
            public bool IsReleased => true;
            public void Dispose() { }
        }

        public string Resolve(ModelInfo model) => model.Ref;
        public IReadOnlyList<AgentSlots> Snapshot() => [];
        public bool TryAcquire(AgentSlotRequest request, out IAgentSlot? lease)
        {
            lease = Full ? null : new Slot();
            return lease is not null;
        }
        public async ValueTask<IAgentSlot> AcquireAsync(AgentSlotRequest request, CancellationToken ct)
        {
            Asked++;
            while (Full)
            {
                await Task.Delay(20, ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
            }
            return new Slot();
        }
    }

    public static void Register(TestRunner r)
    {
        r.Add("ideas commits: evidence past the bounds leaves a review state on the idea instead of only a skipped line", async () =>
        {
            var env = new Env();
            await env.StartAsync();
            var idea = await env.AddIdea("Rework the transcript indexer", "- parse the log without the whole file\n- keep the index warm between runs\n- document the format it reads");
            for (var i = 1; i <= IdeaCommitCheck.MaxEvidenceCommits + 1; i++) env.Repo.Commit($"step {i} of the rework ({idea})");

            await env.Check!.SweepNowAsync();

            var after = await env.Rpc("ideas.get", new JsonObject { ["id"] = idea });
            Check.Equal("open", after["status"].Str(), "completion is not claimed from evidence the check cannot read");
            Check.Equal(0, await env.Cards());

            var review = await env.Review(idea) ?? throw new AssertException("the idea carries no review state");
            Check.Contains(review["limit"]!.Str(), $"{IdeaCommitCheck.MaxEvidenceCommits + 1} linked commits", "it names the bound that was reached: " + review["limit"]!.Str());
            Check.Contains(review["limit"]!.Str(), $"at most {IdeaCommitCheck.MaxEvidenceCommits}", "and the bound itself");
            var commits = (review["commits"] as JsonArray)!.AsArray();
            Check.Equal(IdeaCommitCheck.MaxEvidenceCommits + 1, commits.Count, "every linked commit is named, not just the first bound's worth");
            Check.Equal(env.Repo.Path, commits[0]!["repo"]!.Str(), "each says where its evidence has to be read");
            Check.Equal(env.Repo.Commits[^1].Hash[..7], commits[^1]!["short"]!.Str(), "including the newest one");
            Check.Equal(null, commits[0]!["files"], "and no file list is invented: no patch was read");

            var requirements = (review["requirements"] as JsonArray)!.Select(r => r.Str()!).ToList();
            Check.Equal(3, requirements.Count, "the requirements are the idea's own list");
            Check.Equal("parse the log without the whole file", requirements[0]);

            var section = await env.ReviewSection(idea) ?? throw new AssertException("the idea carries no review section");
            var content = section["content"]!.Str();
            Check.Contains(content, "- [ ] parse the log without the whole file", "the section a reviewer reads carries the checklist");
            Check.Contains(content, env.Repo.Path, "and the repository to read the evidence from");
            Check.Contains(content, "ideas.verifyUpdate", "and how to finish the idea explicitly");
            Check.Contains(env.Ctx.Log.Lines.FirstOrDefault(l => l.Contains("not judged as finished")) ?? "", "not judged as finished");
            env.Ctx.Unload();
        });

        r.Add("ideas commits: a patch git cut is named on the idea, and the review state is refreshed, never stacked", async () =>
        {
            var env = new Env();
            await env.StartAsync();
            var idea = await env.AddIdea("Finish the migration", "- port the reader\n- keep the writer where it is");
            env.Repo.Commit($"implement {idea}");
            env.Truncated.Add(env.Repo.Commits[0].Hash);
            env.PatchChars = 400;   // the patch names a path before it is cut

            await env.Check!.SweepNowAsync();

            var review = await env.Review(idea) ?? throw new AssertException("the idea carries no review state");
            Check.Contains(review["limit"]!.Str(), "cut by git", "the limit says exactly what stopped the check: " + review["limit"]!.Str());
            Check.Contains(review["commits"]![0]!["note"]!.Str(), "cut this patch", "and the commit says why");
            Check.Equal("src/impl.cs", review["commits"]![0]!["files"]![0]!.Str(), "with the paths it did read, so a reviewer knows where to look");

            // Another commit whose patch is cut too: the state is refreshed in place.
            env.Repo.Commit($"and the rest of it ({idea})");
            env.Truncated.Add(env.Repo.Commits[1].Hash);
            await env.Check.SweepNowAsync();

            var idea2 = await env.Rpc("ideas.get", new JsonObject { ["id"] = idea });
            var sections = (idea2["sections"] as JsonArray ?? []).AsArray().Where(s => s!["title"]!.Str() == IdeaReview.Title).ToList();
            Check.Equal(1, sections.Count, "one review state on the idea, not one per sweep");
            var again = (await env.Review(idea))!;
            Check.Equal(2, (again["commits"] as JsonArray)!.Count, "and it names the evidence as it is now");
            env.Ctx.Unload();
        });

        r.Add("ideas commits: evidence the repository cannot read is a review state too, and the sweep is not pinned", async () =>
        {
            var env = new Env();
            await env.StartAsync();
            var idea = await env.AddIdea("Finished somewhere else", "- move the writer\n- keep the reader working");
            env.Repo.Commit($"first {idea}");
            await env.Check!.SweepNowAsync();
            env.Unreadable.Add(env.Repo.Commits[0].Hash);   // a commit made in another repository
            env.Repo.Commit($"second {idea}");

            await env.Check!.SweepNowAsync();

            var review = await env.Review(idea) ?? throw new AssertException("the idea carries no review state");
            Check.Contains(review["limit"]!.Str(), "is missing", "the missing evidence is the bound that was reached: " + review["limit"]!.Str());
            Check.Contains(review["commits"]![0]!["note"]!.Str(), env.Repo.Path, "and the commit says where it cannot be read");
            Check.Equal(2, (review["requirements"] as JsonArray)!.Count, "with what a reviewer has to support");
            Check.Equal(env.Repo.Commits[1].Hash, env.Cursor, "and the sweep still moves on: missing evidence is not a failure that pins it");
            Check.Equal(0, await env.Cards(), "nothing is offered on evidence that is not there");
            env.Ctx.Unload();
        });

        r.Add("ideas commits: a review state never stamps over an edit the user made while the evidence was read", async () =>
        {
            var env = new Env();
            await env.StartAsync();
            var idea = await env.AddIdea("Rework the indexer", "Everything of it has to be built before it is done.");
            env.Repo.Commit($"implement {idea}");
            env.Truncated.Add(env.Repo.Commits[0].Hash);
            // The user edits the idea while its patches are being read.
            env.OnPatch = _ => env.Rpc("ideas.update", new JsonObject
            {
                ["id"] = idea,
                ["patch"] = new JsonObject { ["summary"] = "And a requirement the user added while it was checked" },
            }).GetAwaiter().GetResult();

            await env.Check!.SweepNowAsync();

            var after = await env.Rpc("ideas.get", new JsonObject { ["id"] = idea });
            Check.Equal("And a requirement the user added while it was checked", after["summary"]!.Str(), "their edit stands");
            var review = await env.Review(idea) ?? throw new AssertException("the review state was lost");
            Check.Contains(review["limit"]!.Str(), "cut by git", "and the review state is recorded on the revision the idea has now");
            env.Ctx.Unload();
        });

        r.Add("ideas commits: the same review state is written once, not churned onto the idea by every sweep", async () =>
        {
            var env = new Env();
            await env.StartAsync();
            var idea = await env.AddIdea("A big rework", "- one\n- two");
            var review = new IdeaReview(env.Ctx, IdeasRepository.Open(env.Ctx.Data, env.Ctx.Access, env.Ctx.Log, env.Ctx.Paths.Home));
            var commits = new List<JsonObject> { new JsonObject { ["hash"] = "abc1234def", ["short"] = "abc1234", ["subject"] = "rework", ["repo"] = env.Repo.Path } };

            Check.True(review.Record(idea, "11 linked commits", commits), "the state is recorded");
            var revision = (await env.Rpc("ideas.get", new JsonObject { ["id"] = idea }))["revision"]!.GetValue<long>();
            Check.True(review.Record(idea, "11 linked commits", commits), "the same evidence asked again is still recorded");
            Check.Equal(revision, (await env.Rpc("ideas.get", new JsonObject { ["id"] = idea }))["revision"]!.GetValue<long>(),
                "without writing: a sweep that finds the same state does not churn the idea's revision");

            // Evidence that changed: the state is refreshed where it is, still as one section.
            Check.True(review.Record(idea, "the patch of abc1234 was cut by git and is not complete", commits));
            var idea2 = await env.Rpc("ideas.get", new JsonObject { ["id"] = idea });
            var sections = (idea2["sections"] as JsonArray ?? []).AsArray().Where(s => s!["title"]!.Str() == IdeaReview.Title).ToList();
            Check.Equal(1, sections.Count, "one review state on the idea");
            Check.Contains(sections[0]!["content"]!.Str(), "cut by git", "and it is the newer evidence that is on it");

            var untouched = await env.AddIdea("Nothing to review here");
            Check.Equal(null, await env.Review(untouched), "and an idea that never hit a bound carries no review state");
            env.Ctx.Unload();
        });

        r.Add("ideas commits: complete verified patches mark an idle idea done without a card", async () =>
        {
            var env = new Env();
            env.Ctx.SettingsFake.Set("ideas.applyVerifiedUpdates", true);
            env.Ctx.ModelsFake.VerifierResponder = _ => new ChatMessage { Role = MessageRole.Assistant, Parts = [new TextPart
                { Text = "{\"verified\":true,\"confidence\":0.95,\"reason\":\"The complete patch and tests satisfy the whole plan\"}" }] };
            await env.StartAsync();
            var id = await env.AddIdea("Finish the full implementation");
            env.Repo.Commit($"implement {id}");
            await env.Check!.SweepNowAsync();
            var idea = await env.Rpc("ideas.get", new JsonObject { ["id"] = id });
            Check.Equal("done", idea["status"].Str());
            Check.Equal(0, await env.Cards());
            Check.True(env.Ctx.ModelsFake.Requests.Any(q => q.SystemPrompt!.StartsWith("Independently verify")));
            env.Ctx.Unload();
        });

        r.Add("ideas commits: defer while a project run is active and re-read what the agent closed", async () =>
        {
            var env = new Env();
            var runtime = new FakeAgentRuntime();
            var agent = new AgentInfo { Id = "working", SessionId = env.Session.Id, Status = AgentStatus.Running };
            runtime.Agents.Add(agent);
            env.Ctx.ServicesFake.Register<IAgentRuntime>(runtime);
            await env.StartAsync();
            var idea = await env.AddIdea("Finish after the agent", "The agent will close this itself.");
            env.Repo.Commit($"implement {idea}");
            foreach (var status in new[] { AgentStatus.Running, AgentStatus.Yielded, AgentStatus.Queued })
            {
                agent.Status = status;
                await env.Check!.SweepNowAsync();
                Check.Equal(0, env.Decisions, "no background decision before the run ends");
                Check.Equal(0, (await env.CommitsOn(idea)).Count);
            }
            await env.Rpc("ideas.update", new JsonObject { ["id"] = idea, ["patch"] = new JsonObject { ["status"] = "done" } });
            agent.Status = AgentStatus.Idle;
            await env.Check!.SweepNowAsync();
            Check.Equal(env.Repo.Commits[^1].Hash, env.Cursor, "the deferred commit is handled after the run");
            Check.Equal(0, env.Decisions, "an already closed idea needs no decision");
            Check.Equal(0, await env.Cards());
            env.Ctx.Unload();
        });

        r.Add("ideas commits: a done decision cannot bless an idea revised while it was being judged", async () =>
        {
            var env = new Env();
            await env.StartAsync();
            var idea = await env.AddIdea("A complete implementation", "All of this must be built before it is done.");
            env.Repo.Commit($"implement {idea}");
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = env.Ctx.RpcFake.Register("decide.decision", async (_, ct) =>
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(ct);
                return Answer(new() { ["DONE"] = 0.95, ["MORE"] = 0.05 });
            });
            var sweep = env.Check!.SweepNowAsync();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await env.Rpc("ideas.update", new JsonObject { ["id"] = idea, ["patch"] = new JsonObject { ["summary"] = "New requirements remain to implement." } });
            release.TrySetResult();
            await sweep;
            Check.Equal(0, await env.Cards(), "the old answer is discarded");
            Check.True(env.Cursor != env.Repo.Commits[^1].Hash, "the next sweep must judge the new revision");
            env.Ctx.Unload();
        });

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
            env.Ctx.SettingsFake.Set("ideas.commitRetrySeconds", 0); // no backoff: this test drives the retries back to back
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

        r.Add("ideas commits: with decide.lane on, a full model does not stop the sweep's question (the decision lane)", async () =>
        {
            var env = new Env();
            var full = new FullScheduler();
            env.Ctx.ServicesFake.Register<IAgentScheduler>(full);
            await env.StartAsync();
            env.Ctx.SettingsFake.Set("ideas.checkWaitSeconds", 1);   // what still takes a slot (the verifier, a generation) gives up fast
            var idea = await env.AddIdea("Bound the sweep");
            env.Repo.Commit("a commit the sweep cannot afford right now");
            await env.Check!.SweepNowAsync();
            Check.Equal(1, (await env.CommitsOn(idea)).Count, "the link question was answered while the chats hold every slot");
            Check.NotContains(string.Join("|", env.Ctx.Log.Lines), "the link question was dropped");
            env.Ctx.Unload();
        });

        r.Add("ideas commits: a full model drops the sweep's question, and the commit is read again", async () =>
        {
            var env = new Env();
            var full = new FullScheduler();
            env.Ctx.ServicesFake.Register<IAgentScheduler>(full);
            env.Ctx.SettingsFake.Set("decide.lane", false); // decisions take slots like a chat: the drop path
            await env.StartAsync();
            env.Ctx.SettingsFake.Set("ideas.commitRetrySeconds", 0); // no backoff: this test drives the retries back to back
            var idea = await env.AddIdea("Bound the sweep");
            env.Repo.Commit("a commit the sweep cannot afford right now");
            env.Ctx.SettingsFake.Set("ideas.checkWaitSeconds", 1);

            await env.Check!.SweepNowAsync();
            Check.True(full.Asked > 0, "the sweep asked for a slot");
            Check.Equal(0, (await env.CommitsOn(idea)).Count, "nothing was linked");
            Check.Equal(null, env.Cursor, "and the cursor did not move: the commit is still unseen");
            Check.Contains(string.Join("|", env.Ctx.Log.Lines), "was dropped", "the log says the work was dropped, not run without a slot");

            full.Full = false;
            await env.Check.SweepNowAsync();
            Check.Equal(1, (await env.CommitsOn(idea)).Count, "the next sweep reads it");
            Check.Equal(env.Repo.Commits[0].Hash, env.Cursor);
            env.Ctx.Unload();
        });

        r.Add("ideas commits: a skip is a decision, and the cursor moves past it (only a drop retries)", async () =>
        {
            var env = new Env();
            await env.StartAsync();
            env.Ctx.SettingsFake.Set("ideas.model", "cloud-9"); // a paid model (IsLocal defaults to false)
            env.Ctx.ModelsFake.Models.Add(new ModelInfo { Provider = "aiproxy", Id = "cloud-9" });
            // ideas.allowPaidModel is off (the default): a background check must never invoice.
            var idea = await env.AddIdea("Skip me");
            env.Repo.Commit("a commit about something, no id");
            await env.Check!.SweepNowAsync();
            Check.Equal(0, (await env.CommitsOn(idea)).Count, "nothing was linked (there is no model allowed to judge it)");
            Check.Equal(env.Repo.Commits[0].Hash, env.Cursor, "but the cursor moved past it: the same skip happens on every retry, so it is not retried");
            var skipped = env.Ctx.Log.Lines.Where(l => l.Contains("is not linked")).ToList();
            Check.Equal(1, skipped.Count, "and it says so, once");
            Check.Contains(skipped[0]!, "paid model", "naming the reason");
            Check.Equal(0, env.Decisions, "and no decision was ever asked");

            // A commit that names an idea id is still linked: that path needs no model at all.
            env.Repo.Commit($"named work ({idea})");
            await env.Check.SweepNowAsync();
            Check.Equal(1, (await env.CommitsOn(idea)).Count, "and a named commit is linked without the model");
            Check.Equal(env.Repo.Commits[1].Hash, env.Cursor, "with the cursor past it");
            env.Ctx.Unload();
        });

        r.Add("ideas commits: a failed check backs off instead of being retried on every trigger", async () =>
        {
            var env = new Env();
            await env.StartAsync();
            env.Ctx.SettingsFake.Set("ideas.commitRetrySeconds", 3600); // an hour: the next attempt is long past due
            var idea = await env.AddIdea("Back off");
            env.Repo.Commit("a commit whose check will not answer");
            env.FailDecisions = 1; // the model is down for this one
            await env.Check!.SweepNowAsync();
            Check.Equal(0, (await env.CommitsOn(idea)).Count, "nothing was linked");
            Check.Equal(null, env.Cursor, "and the cursor did not move");

            // A trigger that would have retried it (the timer, an agent-status change, a burst of file events):
            // the failed check backs off instead of re-paying the decision on every one of them (idea-kooctc).
            var asked = env.Decisions;
            await env.Check.SweepNowAsync();
            Check.Equal(asked, env.Decisions, "the backoff held: the same commit was not asked again");
            Check.Equal(null, env.Cursor, "and it is still at the same place");

            env.Ctx.SettingsFake.Set("ideas.commitRetrySeconds", 0); // the backoff off: the sweep retries
            env.FailDecisions = 0;
            await env.Check.SweepNowAsync();
            Check.Equal(1, (await env.CommitsOn(idea)).Count, "and the next allowed sweep reads it");
            Check.Equal(env.Repo.Commits[0].Hash, env.Cursor);
            env.Ctx.Unload();
        });

        r.Add("ideas commits: a commit that cannot be decided is recorded unread after its bound, and later commits are read", async () =>
        {
            var env = new Env();
            await env.StartAsync();
            env.Ctx.SettingsFake.Set("ideas.commitRetrySeconds", 0); // no backoff: the attempts run back to back
            var idea = await env.AddIdea("Read me once");
            env.Repo.Commit("a commit that will never be judged");
            env.FailDecisions = 999; // the decision never answers

            for (var attempt = 1; attempt < IdeaCommitCheck.MaxTries; attempt++)
            {
                await env.Check!.SweepNowAsync();
                Check.Equal(null, env.Cursor, $"attempt {attempt} leaves the cursor at the commit");
            }
            await env.Check!.SweepNowAsync(); // the last allowed attempt
            Check.Equal(env.Repo.Commits[0].Hash, env.Cursor, "after the bound the cursor moves past the commit");
            Check.Contains(string.Join("|", env.Ctx.Log.Lines), "recorded unread", "and it says so in the log");
            var repo = IdeasRepository.Open(env.Ctx.Data, env.Ctx.Access, env.Ctx.Log, env.Ctx.Paths.Home);
            var unread = repo.Unread().Single(u => u["hash"]!.Str() == env.Repo.Commits[0].Hash);
            Check.Equal(IdeaCommitCheck.MaxTries, unread["tries"]!.GetValue<long>(), "with its attempts");
            Check.Equal(1, ((JsonArray)(await env.Ctx.RpcFake.Call("ideas.unread", new JsonObject()))!).Count, "and it is listed in ideas.unread");

            // A later commit is read while the first one stays unread: it names the idea, so the link needs no model
            // at all (the done question runs on the recovered model and is refused: the verifier does not bless it).
            env.FailDecisions = 0;
            env.Repo.Commit($"read the second one ({idea})");
            await env.Check.SweepNowAsync();
            Check.Equal(env.Repo.Commits[1].Hash, env.Cursor, "the later commit is read, not blocked by the unread one");
            Check.Equal(1, (await env.CommitsOn(idea)).Count, "and it is linked on the idea it names");
            Check.Equal(1, repo.Unread().Count, "the first commit is still recorded unread");
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
                // Nearly tied ideas must not authorize a link, even when each clears the absolute threshold.
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
            Check.Equal(1, (await env.CommitsOn(first)).Count, "a near tie links neither idea");
            Check.Equal(1, (await env.CommitsOn(second)).Count, "the runner-up is not linked");
            env.Link = labels => new() { [labels[0]!.Str()!] = 0.9, [labels[1]!.Str()!] = 0.07, [labels[^1]!.Str()!] = 0.03 };
            env.Repo.Commit("finish the rewrite specifically");
            await env.Check.SweepNowAsync();
            Check.Equal(2, (await env.CommitsOn(first)).Count, "a clear winner links only the best idea");
            Check.Equal(1, (await env.CommitsOn(second)).Count);
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

        r.Add("ideas commits: a watched repository keeps its cursor when it is quiet for longer than the keep window", async () =>
        {
            var env = new Env();
            await env.StartAsync();
            var idea = await env.AddIdea("A repository nobody commits to");
            env.Repo.Commit($"first {idea}");
            await env.Check!.SweepNowAsync();
            Check.Equal(env.Repo.Commits[0].Hash, env.Cursor, "the first commit was read");

            // The repository is watched but quiet for months. Its cursor is the progress made before the last restart:
            // forgetting it makes the next sweep read the whole history again and record months-old commits on an idea
            // that did not exist when they were made (idea-g6siz0).
            env.AgeCursor();
            env.Repo.Commit($"second {idea}");
            await env.Check.SweepNowAsync();
            Check.Equal(env.Repo.Commits[0].Hash, env.Repo.Asked[^1].Since, "the sweep continued from the cursor, not from the start of the history");
            Check.Equal(env.Repo.Commits[1].Hash, env.Cursor, "and it read only what came after it");
            Check.Equal(2, (await env.CommitsOn(idea)).Count);
            env.Ctx.Unload();
        });

        r.Add("ideas commits: a linked commit this repository cannot read is skipped, not read again for ever", async () =>
        {
            var env = new Env();
            await env.StartAsync();
            var idea = await env.AddIdea("Finished somewhere else", "The commit that finishes it was made in another repository.");
            env.Repo.Commit($"first {idea}");
            await env.Check!.SweepNowAsync();
            Check.Equal(env.Repo.Commits[0].Hash, env.Cursor, "the first commit is linked");

            // The completion evidence is read in the repository the sweep is on, and this hash is not in it: an
            // unavailable patch is missing evidence, not a failure of the check (idea-g6siz0).
            env.Unreadable.Add(env.Repo.Commits[0].Hash);
            env.Repo.Commit($"second {idea}");
            await env.Check.SweepNowAsync();
            Check.Equal(env.Repo.Commits[1].Hash, env.Cursor, "the cursor moved past it instead of pinning the sweep");
            Check.Equal(0, await env.Cards(), "no offer was made on evidence we do not have");
            Check.Equal(0, IdeasRepository.Open(env.Ctx.Data, env.Ctx.Access, env.Ctx.Log, env.Ctx.Paths.Home).Unread().Count,
                "and the commit is not recorded unread either: it was read, only its patch was missing");
            Check.Contains(IdeaOps.Str((await env.Rpc("ideas.get", new JsonObject { ["id"] = idea }))["commits"]![0]!["repo"]), env.Repo.Path,
                "every commit entry says which repository it was made in");
            env.Ctx.Unload();
        });

        r.Add("ideas commits: a sweep passes back the git directories it was answered with, so only git log runs", async () =>
        {
            var env = new Env();
            await env.StartAsync();
            await WaitAsked(env, 2, "the start's probe and sweep");

            var (probeCwd, probeGit, probeCommon) = env.Repo.Roots[0];
            Check.Equal(env.Project.Path, probeCwd, "the first contact asks from the project path");
            Check.Equal(null, probeGit, "and carries no git directory it has not been told");
            Check.Equal(null, probeCommon);

            var (sweepCwd, sweepGit, sweepCommon) = env.Repo.Roots[1];
            Check.Equal(env.Repo.Path, sweepCwd, "the sweep asks from the repository it was answered with");
            Check.Equal(env.Repo.GitDir, sweepGit, "and carries the git directory, so the rev-parses do not run again");
            Check.Equal(env.Repo.GitDir, sweepCommon, "and the common one");
            env.Ctx.Unload();
        });

        r.Add("ideas commits: an unchanged repository skips the periodic sweep until a ref moves", async () =>
        {
            var env = new Env();
            // The git directory the fake repository stands in for: what the periodic check marks (the HEAD, the refs
            // directory, the packed refs) are real files here, so an unchanged repository really is unchanged.
            var gitDir = env.Repo.GitDir;
            Directory.CreateDirectory(Path.Combine(gitDir, "refs", "heads"));
            File.WriteAllText(Path.Combine(gitDir, "HEAD"), "ref: refs/heads/main\n");
            File.WriteAllText(Path.Combine(gitDir, "packed-refs"), string.Empty);
            var refFile = Path.Combine(gitDir, "refs", "heads", "main");
            File.WriteAllText(refFile, "1" + new string('0', 39) + "\n");

            await env.StartAsync();
            await WaitAsked(env, 2, "the start's probe and sweep");

            var idea = await env.AddIdea("Skipped while it sits", "A plan long enough for the done question to see it in full.");
            env.Ctx.RpcFake.Register("decide.decision", (req, _) =>
            {
                env.Decisions++;
                var labels = req.Params.GetProperty("branches")[0].GetProperty("labels").EnumerateArray().Select(e => e.GetString() ?? "").ToList();
                if (labels is ["DONE", "MORE"]) return Task.FromResult<object?>(Answer(new() { ["DONE"] = 0.2, ["MORE"] = 0.8 }));
                return Task.FromResult<object?>(Answer(new() { [labels[0]!] = 0.95, [labels[^1]!] = 0.03 }));
            });
            env.Repo.Commit($"implement {idea}");   // in the fake repository: nothing on disk moves

            env.Ctx.Bus.Publish(new BusEvent { Type = EventTypes.AgentStatus, Data = new JsonObject { ["agent"] = new JsonObject { ["status"] = "idle" } } });
            await WaitLog(env, "the sweep is skipped");
            Check.Equal(2, env.Repo.Roots.Count, "an unchanged repository is not re-read");
            Check.Equal(0, env.Decisions, "nothing was decided");
            Check.Equal(0, (await env.CommitsOn(idea)).Count, "and nothing was linked");
            Check.Equal(null, env.Cursor, "the cursor did not move");

            // A commit moves a ref: git writes a fresh file and renames it over the old one, so the refs
            // directory's mark moves.
            var fresh = Path.Combine(gitDir, "refs", "heads", "main.fresh");
            File.WriteAllText(fresh, "2" + new string('1', 39) + "\n");
            File.Move(fresh, refFile, overwrite: true);
            env.Ctx.Bus.Publish(new BusEvent { Type = EventTypes.AgentStatus, Data = new JsonObject { ["agent"] = new JsonObject { ["status"] = "idle" } } });
            await WaitCursor(env);
            Check.Equal(env.Repo.Commits[0].Hash, env.Cursor, "a moved ref wakes the sweep");
            Check.Equal(1, (await env.CommitsOn(idea)).Count, "and the commit is linked");
            Check.Equal(0, await env.Cards(), "but not offered: the commits do not finish it");
            env.Ctx.Unload();
        });
    }

    private static JsonObject Answer(Dictionary<string, double> probs)
    {
        var answer = new JsonObject();
        foreach (var (k, v) in probs) answer[k] = v;
        return new JsonObject { ["branches"] = new JsonArray(new JsonObject { ["id"] = "pick", ["probabilities"] = answer }) };
    }

    /// <summary>The start sweeps in the background; these waits are the test's way in.</summary>
    private static async Task WaitAsked(Env env, int count, string what, int ms = 5000)
    {
        for (var waited = 0; waited < ms; waited += 25)
        {
            if (env.Repo.Asked.Count >= count) return;
            await Task.Delay(25);
        }
        Check.True(false, $"{env.Repo.Asked.Count} of {count} files.commits reads within {ms} ms ({what})");
    }

    private static async Task WaitLog(Env env, string text, int ms = 5000)
    {
        for (var waited = 0; waited < ms; waited += 25)
        {
            if (env.Ctx.Log.Lines.Any(l => l.Contains(text))) return;
            await Task.Delay(25);
        }
        Check.True(false, $"no log line with \"{text}\" within {ms} ms");
    }

    private static async Task WaitCursor(Env env, int ms = 5000)
    {
        for (var waited = 0; waited < ms; waited += 25)
        {
            if (env.Cursor is not null) return;
            await Task.Delay(25);
        }
        Check.True(false, $"the cursor did not move within {ms} ms");
    }
}
