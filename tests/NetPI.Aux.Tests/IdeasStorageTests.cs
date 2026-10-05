using System.Text.Json.Nodes;
using NetPI.Ideas;

namespace NetPI.Aux.Tests;

/// <summary>
/// The storage half of the Ideas flow: what the store guarantees, and what it refuses. Every test here drives the
/// real repository against a real temporary SQLite store — an in-memory fake could not prove a rollback, a unique id
/// or two instances seeing each other, which is the whole point of the tests below.
/// </summary>
public static class IdeasStorageTests
{
    private sealed class Env
    {
        public FakePluginContext Ctx { get; }
        public string ProjectDir { get; }
        public ProjectInfo Project { get; }
        public SessionInfo Session { get; }

        public Env()
        {
            Ctx = new FakePluginContext(T.TempDir("ideas-home"));
            ProjectDir = T.TempDir("ideas-proj");
            Project = Ctx.SessionsFake.CreateProject("Demo", ProjectDir);
            Session = Ctx.SessionsFake.CreateSession(new SessionInfo { Title = "s", ProjectId = Project.Id });
        }

        public Task StartAsync() => new IdeasPlugin().StartAsync(Ctx, CancellationToken.None);

        /// <summary>A second store over the same home folder, as a hot-reload swap's new instance has.</summary>
        public (FakePluginContext Ctx2, IdeasRepository Repo) Second()
        {
            var second = new FakePluginContext(Ctx.Paths.Home, Ctx.PluginId);
            return (second, IdeasRepository.Open(second.Data, second.Access, second.Log, second.Paths.Home));
        }

        /// <summary>The repository the plugin writes through, for a test that writes where the plugin writes.</summary>
        public IdeasRepository Repo => IdeasRepository.Open(Ctx.Data, Ctx.Access, Ctx.Log, Ctx.Paths.Home);

        /// <summary>An RPC's answer as the UI reads it (an object), or the assertion when it failed.</summary>
        public async Task<JsonObject> Rpc(string method, JsonObject p) => (JsonObject)(await Ctx.RpcFake.Call(method, p))!;

        public async Task<JsonObject> Call(string method, JsonObject p)
        {
            try { return await Rpc(method, p); }
            catch (RpcException ex) { throw new AssertException($"{method} failed ({ex.Code}): {ex.Message}"); }
        }

        /// <summary>What the code would get back (the code) as well as the message. Some methods answer a plain true.</summary>
        public async Task<(string? Code, string Message)> Try(string method, JsonObject p)
        {
            try { await Ctx.RpcFake.Call(method, p); return (null, ""); }
            catch (RpcException ex) { return (ex.Code, ex.Message); }
        }

        public async Task<List<string>> Titles() => ((JsonArray)(await Rpc("ideas.list", new JsonObject()))["ideas"]!)
            .Select(i => i!["title"].Str()).ToList();

        public async Task<List<string>> CardIds() => ((JsonArray)(await Call("ideas.suggestions", new JsonObject()))["suggestions"]!)
            .Select(i => i!["id"].Str()).ToList();

        /// <summary>A card waiting for the user, as a closed chat or a commit check leaves it.</summary>
        public string AddCard(string id = "sg_card00001", string title = "Unsaved plan", string kind = "save", string? ideaId = null)
        {
            var card = new JsonObject
            {
                ["id"] = id,
                ["kind"] = kind,
                ["sessionId"] = Session.Id,
                ["sessionTitle"] = "s",
                ["title"] = title,
                ["summary"] = "what the chat left behind",
                ["at"] = "2026-09-29T10:00:00Z",
                ["project"] = new JsonObject { ["id"] = Project.Id, ["name"] = Project.Name },
            };
            if (ideaId is not null) card["ideaId"] = ideaId;
            Repo.AddCard(card);
            return id;
        }

        /// <summary>A "save" card as the check leaves it: one plan one conversation.</summary>
        public JsonObject Plan(string id, string title) => new()
        {
            ["id"] = id,
            ["kind"] = "save",
            ["sessionId"] = Session.Id,
            ["sessionTitle"] = "s",
            ["title"] = title,
            ["summary"] = "what the chat left behind",
            ["at"] = "2026-09-29T10:00:00Z",
        };

        /// <summary>A "done" card: this idea may be finished.</summary>
        public JsonObject Done(string id, string ideaId) => new()
        {
            ["id"] = id,
            ["kind"] = "done",
            ["ideaId"] = ideaId,
            ["ideaRevision"] = 1,
            ["title"] = "Finished",
            ["commits"] = new JsonArray(),
            ["at"] = "2026-09-29T10:00:00Z",
        };

        /// <summary>An idea of the backlog, as the storage tests add them (through the repository: this suite needs no RPC).</summary>
        public string AddIdea(string title = "An idea") => Repo.Add(IdeaOps.CreateIdea(
            new JsonObject { ["title"] = title }, Repo.TakenIds(), "user", Session.Id)).Doc["id"]!.Str()!;
    }

    public static void Register(TestRunner r)
    {
        r.Add("cards: a bad edit on ideas.resolve is refused the way a bad patch on ideas.update is (bad_request), and nothing is saved", async () =>
        {
            var env = new Env();
            await env.StartAsync();
            var card = env.AddCard(title: "");   // a card without a title: the edit has to supply one
            var (code, message) = await env.Try("ideas.resolve", new JsonObject { ["id"] = card, ["action"] = "save", ["edit"] = new JsonObject { ["title"] = "   " } });
            Check.Equal("bad_request", code);
            Check.Contains(message, "needs a title");
            Check.Equal(0, (await env.Titles()).Count, "nothing was saved");
            Check.Equal(1, (await env.CardIds()).Count, "and the card is still waiting");
        });

        // ---------------------------------------------------------------- the review's three gaps, as they are now

        r.Add("review: a rejected patch leaves no trace, and no later write commits it", async () =>
        {
            var env = new Env();
            await env.StartAsync();
            var repo = env.Repo;
            var idea = IdeaOps.CreateIdea(new JsonObject { ["title"] = "Original" }, repo.TakenIds(), "user", null);
            repo.Add(idea);

            // A patch that fails validation half way: the title is already changed when the status is refused.
            var ex = Check.Throws<IdeaInputException>(() =>
                repo.Update(idea["id"]!.Str(), new JsonObject { ["title"] = "Rejected title", ["status"] = "INVALID" }, fromUi: true));
            Check.Contains(ex.Message, "Invalid status");
            Check.Equal("Original", repo.Idea(idea["id"]!.Str())!["title"]!.Str(), "nothing was written");

            // An unrelated write afterwards: the rejected title must not come back with it.
            repo.Add(IdeaOps.CreateIdea(new JsonObject { ["title"] = "Other" }, repo.TakenIds(), "user", null));
            Check.Equal("Original", repo.Idea(idea["id"]!.Str())!["title"]!.Str(), "a rejected patch is not committed by an unrelated write");
            Check.Equal(1, (repo.Find(idea["id"]!.Str())?.Revision ?? 0), "and it did not move the revision either");

            // The same through the RPC, where a rejected patch is a bad_request.
            var (code, _) = await env.Try("ideas.update", new JsonObject { ["id"] = idea["id"]!.Str(), ["patch"] = new JsonObject { ["title"] = "Nope", ["status"] = "??" } });
            Check.Equal("bad_request", code);
            Check.Equal("Original", repo.Idea(idea["id"]!.Str())!["title"]!.Str());
            env.Ctx.Unload();
        });

        r.Add("review: two instances of the plugin keep each other's write (no watcher, no cache)", async () =>
        {
            var env = new Env();
            await env.StartAsync();
            var (other, second) = env.Second();
            try
            {
                // Nothing coordinates these two: no file lock, no watcher, no shared object. The store is the only
                // thing they have in common, and it is what makes both updates survive.
                await Task.WhenAll(
                    Task.Run(() => env.Repo.Add(IdeaOps.CreateIdea(new JsonObject { ["title"] = "From the first" }, env.Repo.TakenIds(), "user", null))),
                    Task.Run(() => second.Add(IdeaOps.CreateIdea(new JsonObject { ["title"] = "From the second" }, second.TakenIds(), "user", null))));
                var titles = await env.Titles();
                Check.Equal(2, titles.Count, "both independently committed updates must survive: " + string.Join(", ", titles));
                Check.True(titles.Contains("From the first") && titles.Contains("From the second"));
                Check.Equal(2, second.Count(), "and the second instance sees both");
            }
            finally { other.Unload(); env.Ctx.Unload(); }
        });

        r.Add("review: a stale editor is a conflict, and its edit is not lost silently", async () =>
        {
            var env = new Env();
            await env.StartAsync();
            var added = await env.Call("ideas.add", new JsonObject { ["idea"] = new JsonObject { ["title"] = "Editable" } });
            var id = added["id"].Str();
            var read = added["revision"]!.GetValue<long>();

            // The agent changes the idea while the editor has it open.
            await env.Call("ideas.update", new JsonObject { ["id"] = id, ["patch"] = new JsonObject { ["summary"] = "the agent got there first" } });

            var (code, message) = await env.Try("ideas.update", new JsonObject
            {
                ["id"] = id,
                ["patch"] = new JsonObject { ["summary"] = "from the stale window" },
                ["expectedRevision"] = read,
            });
            Check.Equal("conflict", code, "the stale window is told: " + message);
            var now = await env.Call("ideas.get", new JsonObject { ["id"] = id });
            Check.Equal("the agent got there first", now["summary"].Str(), "and the newer value is what is stored");

            // With the revision it has now, the same window succeeds.
            var ok = await env.Call("ideas.update", new JsonObject
            {
                ["id"] = id,
                ["patch"] = new JsonObject { ["summary"] = "from the window that reloaded" },
                ["expectedRevision"] = now["revision"]!.GetValue<long>(),
            });
            Check.Equal("from the window that reloaded", ok["summary"].Str());
            Check.Equal(read + 2, ok["revision"]!.GetValue<long>(), "one revision per change");
            env.Ctx.Unload();
        });

        // ---------------------------------------------------------------- the repository

        r.Add("ideas storage: a change is refused when it changes nothing, and no revision is burned", async () =>
        {
            var env = new Env();
            await env.StartAsync();
            var added = await env.Call("ideas.add", new JsonObject { ["idea"] = new JsonObject { ["title"] = "Same" } });
            Check.True(added.ContainsKey("revision"), "an idea the caller gets back carries the revision it can submit");
            var same = await env.Call("ideas.update", new JsonObject { ["id"] = added["id"].Str(), ["patch"] = new JsonObject { ["title"] = "Same" } });
            Check.Equal(added["revision"]!.GetValue<long>(), same["revision"]!.GetValue<long>(), "no write, no new revision");
            var changed = await env.Call("ideas.update", new JsonObject { ["id"] = added["id"].Str(), ["patch"] = new JsonObject { ["title"] = "Different" } });
            Check.Equal(added["revision"]!.GetValue<long>() + 1, changed["revision"]!.GetValue<long>());
            env.Ctx.Unload();
        });

        r.Add("ideas storage: the revision is a stored integer, and it survives a restart", async () =>
        {
            var env = new Env();
            await env.StartAsync();
            var added = await env.Call("ideas.add", new JsonObject { ["idea"] = new JsonObject { ["title"] = "Durable" } });
            await env.Call("ideas.update", new JsonObject { ["id"] = added["id"].Str(), ["patch"] = new JsonObject { ["summary"] = "twice" } });
            env.Ctx.Unload();
            await env.StartAsync();
            var got = await env.Call("ideas.get", new JsonObject { ["id"] = added["id"].Str() });
            Check.Equal(2, got["revision"]!.GetValue<long>(), "the revision is in the store, not in memory");
            Check.Equal("twice", got["summary"].Str());
            env.Ctx.Unload();
        });

        r.Add("ideas storage: a failed transaction rolls everything back", async () =>
        {
            var env = new Env();
            await env.StartAsync();
            var repo = env.Repo;
            var kept = repo.Add(IdeaOps.CreateIdea(new JsonObject { ["title"] = "Kept" }, repo.TakenIds(), "user", null));
            var rolled = Check.Throws<InvalidOperationException>(() => env.Ctx.Data.Transaction<object?>(() =>
            {
                repo.Add(IdeaOps.CreateIdea(new JsonObject { ["title"] = "Rolled back" }, repo.TakenIds(), "user", null));
                repo.SetMeta("half", "written");
                throw new InvalidOperationException("the middle of the work failed");
            }));
            Check.Equal("the middle of the work failed", rolled.Message);
            Check.Equal(1, repo.Count(), "the idea written inside the transaction is gone");
            Check.True(repo.Meta("half") is null, "and so is everything else it wrote");
            Check.True(repo.Idea(kept.Doc["id"]!.Str()) is not null, "what was committed before is untouched");
            env.Ctx.Unload();
        });

        r.Add("ideas storage: an idea is found by the ids people actually type", async () =>
        {
            var env = new Env();
            await env.StartAsync();
            var repo = env.Repo;
            var legacy = IdeaOps.CreateIdea(new JsonObject { ["title"] = "Legacy id" }, repo.TakenIds(), "user", null);
            legacy["id"] = "IDEA-Custom_7"; // an id that is not of our making, as an old file or another tool may have written
            repo.Add(legacy);
            foreach (var written in new[] { "IDEA-Custom_7", "idea-custom_7", "CUSTOM_7", "custom_7" })
                Check.True(repo.Find(written) is not null, $"found by '{written}'");
            env.Ctx.Unload();
        });

        r.Add("ideas storage: the order the user arranged is the order the backlog answers in", async () =>
        {
            var env = new Env();
            await env.StartAsync();
            var ids = new List<string>();
            for (var i = 0; i < 4; i++)
                ids.Add((await env.Call("ideas.add", new JsonObject { ["idea"] = new JsonObject { ["title"] = "Idea " + i } }))["id"].Str());
            await env.Call("ideas.add", new JsonObject { ["idea"] = new JsonObject { ["title"] = "First" }, ["prepend"] = true });
            Check.Equal("First|Idea 0|Idea 1|Idea 2|Idea 3", string.Join("|", await env.Titles()), "the user's order, newest last");

            var (reordered, message) = await env.Try("ideas.reorder", new JsonObject { ["ids"] = new JsonArray(ids[2], "no-such-idea", ids[0]) });
            Check.True(reordered is null, "reordered: " + message);
            Check.Equal("Idea 2|Idea 0|First|Idea 1|Idea 3", string.Join("|", await env.Titles()), "the listed ones first, the rest where they were, a stale id ignored");
            env.Ctx.Unload();
        });

        r.Add("ideas storage: unknown fields on the idea and on its sections survive every write", async () =>
        {
            var env = new Env();
            await env.StartAsync();
            var repo = env.Repo;
            var idea = IdeaOps.CreateIdea(new JsonObject { ["title"] = "Hand written" }, repo.TakenIds(), "user", null, keepExtraFields: true);
            idea["estimate"] = new JsonObject { ["days"] = 3 };
            idea["sections"]!.AsArray().Add(new JsonObject { ["id"] = "sec-aa", ["kind"] = "note", ["content"] = "x", ["author"] = "bob" });
            repo.Add(idea);
            repo.AddSession(idea["id"]!.Str(), env.Session.Id);
            repo.MarkDone(idea["id"]!.Str());
            var after = repo.Idea(idea["id"]!.Str())!;
            Check.Equal("{\"days\":3}", after["estimate"]!.ToJsonString());
            Check.Equal("bob", after["sections"]![0]!["author"].Str());
            Check.Equal("done", after["status"].Str());
            Check.Equal(env.Session.Id, after["sessionIds"]![0].Str());
            env.Ctx.Unload();
        });

        // ---------------------------------------------------------------- cards

        r.Add("ideas storage: answering a card writes the idea and takes the card in one step", async () =>
        {
            var env = new Env();
            await env.StartAsync();
            var card = env.AddCard();
            var saved = await env.Call("ideas.resolve", new JsonObject { ["id"] = card, ["action"] = "save", ["edit"] = new JsonObject { ["title"] = "Edited by the user" } });
            Check.Equal("Edited by the user", saved["saved"]!["title"].Str());
            Check.Equal(env.Project.Id, saved["saved"]!["project"]!["id"].Str(), "stamped with the card's project");
            Check.Equal(env.Session.Id, saved["saved"]!["sessions"]![0]!["sessionId"].Str());
            Check.Equal(0, (await env.CardIds()).Count, "the card is answered");
            Check.Equal(1, (await env.Titles()).Count);
            env.Ctx.Unload();
        });

        r.Add("ideas storage: two windows answering the same card have one effect", async () =>
        {
            var env = new Env();
            await env.StartAsync();
            var card = env.AddCard();
            var results = await Task.WhenAll(
                env.Try("ideas.resolve", new JsonObject { ["id"] = card, ["action"] = "save" }),
                env.Try("ideas.resolve", new JsonObject { ["id"] = card, ["action"] = "save" }));
            var ok = results.Count(x => x.Code is null);
            Check.True(ok >= 1, "at least one window got an answer: " + string.Join(", ", results.Select(x => x.Code ?? "ok")));
            Check.Equal(1, (await env.Titles()).Count, "one idea, not two");
            Check.Equal(0, (await env.CardIds()).Count);
            // The second window is told the outcome of the answer, not that it failed: retrying is safe and is what a
            // client that timed out does.
            var again = await env.Call("ideas.resolve", new JsonObject { ["id"] = card, ["action"] = "save" });
            Check.Equal(true, (bool)again["alreadyResolved"]!, "a repeated answer reports the recorded one");
            Check.Equal(1, (await env.Titles()).Count, "and still saves nothing new");
            env.Ctx.Unload();
        });

        r.Add("ideas storage: a card answered with the wrong action is refused, and stays", async () =>
        {
            var env = new Env();
            await env.StartAsync();
            env.AddCard("sg_wrong001", "Unsaved plan", "save");
            var (code, message) = await env.Try("ideas.resolve", new JsonObject { ["id"] = "sg_wrong001", ["action"] = "done" });
            Check.Equal("bad_request", code, "a save card is not marked done: " + message);
            Check.Equal(1, (await env.CardIds()).Count, "the card is still there");
            Check.Equal(0, (await env.Titles()).Count);
            env.Ctx.Unload();
        });

        r.Add("ideas storage: a card that is gone is told so, not treated as an empty answer", async () =>
        {
            var env = new Env();
            await env.StartAsync();
            var (code, message) = await env.Try("ideas.resolve", new JsonObject { ["id"] = "sg_nope", ["action"] = "discard" });
            Check.Equal("not_found", code);
            Check.Contains(message, "gone");
            env.Ctx.Unload();
        });

        r.Add("ideas storage: a 'done' card marks the idea, keeps its commits, and is offered once per idea", async () =>
        {
            var env = new Env();
            await env.StartAsync();
            var idea = (await env.Call("ideas.add", new JsonObject { ["idea"] = new JsonObject { ["title"] = "Nudge" } }))!;
            var id = idea["id"].Str();
            env.Repo.AddCommitEntries([id], new JsonObject { ["hash"] = new string('a', 40), ["short"] = "aaaaaaa", ["subject"] = "nudge: first", ["at"] = "2026-09-29T10:00:00Z" });
            env.AddCard("sg_done0001", "Nudge", "done", id);
            env.AddCard("sg_done0002", "Nudge again", "done", id);
            Check.Equal(1, (await env.CardIds()).Count, "one offer per idea");

            var marked = await env.Call("ideas.resolve", new JsonObject { ["id"] = "sg_done0001", ["action"] = "done" });
            Check.Equal("done", marked["saved"]!["status"].Str());
            Check.Equal(1, ((JsonArray)marked["saved"]!["commits"]!).Count, "the commits stay on the idea");
            Check.Equal(0, (await env.CardIds()).Count);
            env.Ctx.Unload();
        });

        r.Add("ideas storage: the same chat and the same plan make one card, however often the check runs", async () =>
        {
            var env = new Env();
            await env.StartAsync();
            var repo = env.Repo;
            var card = new JsonObject { ["id"] = "sg_a1", ["kind"] = "save", ["sessionId"] = env.Session.Id, ["title"] = "One plan", ["at"] = IdeaOps.Now() };
            Check.True(repo.AddCard(card), "the first one is added");
            Check.False(repo.AddCard((JsonObject)card.DeepClone()), "a second run of the same check is not stacked");
            var other = (JsonObject)card.DeepClone();
            other["id"] = "sg_a2";
            other["title"] = "Another plan";
            Check.True(repo.AddCard(other), "a different plan is its own card");
            Check.Equal(2, repo.CardCount());
            env.Ctx.Unload();
        });

        // ---------------------------------------------------------------- checks and cursors

        r.Add("ideas storage: a check is claimed once per conversation revision, and an expired claim is retryable", async () =>
        {
            var env = new Env();
            var repo = env.Repo;
            var (started, reason, token) = repo.ClaimCheck("ses_1", 5, "10:99", 3, TimeSpan.FromMinutes(5));
            Check.True(started, "the first close takes it: " + reason);
            Check.True(token is { Length: > 0 }, "and owns it with a token");

            var (again, reason2, _) = repo.ClaimCheck("ses_1", 5, "10:99", 3, TimeSpan.FromMinutes(5));
            Check.False(again, "a second close of the same revision does not start a second check: " + reason2);
            var (moved, _, movedToken) = repo.ClaimCheck("ses_1", 6, "12:100", 3, TimeSpan.FromMinutes(5));
            Check.True(moved, "a chat that changed since is a different check to run");

            // A worker whose claim was taken over cannot report over the newer outcome.
            Check.False(repo.FinishCheck("ses_1", "some-other-token", null), "a stale worker is ignored");
            Check.False(repo.FinishCheck("ses_1", token, null), "and so cannot the one it took over from");
            Check.True(repo.FinishCheck("ses_1", movedToken, null), "the current owner reports");
            Check.False(repo.ClaimCheck("ses_1", 6, "12:100", 3, TimeSpan.FromMinutes(5)).Started, "a finished check stays finished");

            // A claim left behind by a stopped run: the start recovers it, and the check runs again.
            var checks = env.Ctx.Data.Collection("checks", new CollectionSpec().Text("state").Text("at").Integer("claimUntil"));
            var mark = checks.Get("ses_1")!;
            mark["state"] = "running"; mark["claim"] = "gone"; mark["claimUntil"] = 1;
            checks.Put("ses_1", mark);
            Check.Equal(1, repo.RecoverExpiredClaims());
            Check.True(repo.ClaimCheck("ses_1", 6, "12:100", 3, TimeSpan.FromMinutes(5)).Started, "and is not blocked for good");
            env.Ctx.Unload();
        });

        r.Add("ideas storage: a claim whose time ran out is claimable again, with or without a restart", () =>
        {
            var env = new Env();
            var repo = env.Repo;
            Check.True(repo.ClaimCheck("ses_3", 5, "10:99", 3, TimeSpan.FromMinutes(5)).Started, "the first close takes it");
            var checks = env.Ctx.Data.Collection("checks", new CollectionSpec().Text("state").Text("at").Integer("claimUntil"));
            var mark = checks.Get("ses_3")!;
            mark["claimUntil"] = DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeMilliseconds();   // nobody is running it any more
            checks.Put("ses_3", mark);

            // A reload or a shutdown is not the only way a claim runs out: the recovery at the next start is one, and
            // a chat closed again in between must not wait for it (idea-g6siz0).
            var (again, reason, token) = repo.ClaimCheck("ses_3", 5, "10:99", 3, TimeSpan.FromMinutes(5));
            Check.True(again, "the conversation is checked again: " + reason);
            Check.True(token is { Length: > 0 } && token != mark["claim"]!.Str(), "under a new claim");
            Check.Equal(0, repo.RecoverExpiredClaims(), "and nothing is left for the recovery at the next start to do");
            env.Ctx.Unload();
        });

        r.Add("ideas storage: a discarded card does not come back as a new one", async () =>
        {
            var env = new Env();
            var repo = env.Repo;
            var card = env.AddCard();
            repo.ResolveCard(card, "discard", null);
            Check.Equal(0, repo.CardCount(), "the card is answered");

            // The check runs again on the next close of that conversation and drafts the same plan. The answer was a
            // decision about it: discarding it means it is not wanted, so it must not be offered again (idea-g6siz0).
            Check.False(repo.AddCard(env.Plan("sg_card00002", "Unsaved plan")), "the same plan, in the same conversation, stays discarded");
            Check.False(repo.AddCard(env.Plan("sg_card00003", "  unsaved PLAN ")), "however it is written");
            Check.True(repo.AddCard(env.Plan("sg_card00004", "Another plan the chat left behind")), "another plan is still offered");
            Check.Equal(1, repo.CardCount(), "and the plan that was not answered still waits");

            // A "done" card is keyed on its idea, so a dismissal is final for that idea too.
            var idea = env.AddIdea();
            Check.True(repo.AddCard(env.Done("sg_card00005", idea)), "the idea can be offered as finished");
            repo.ResolveCard("sg_card00005", "discard", null);
            Check.False(repo.AddCard(env.Done("sg_card00006", idea)), "and it is not offered again");
            env.Ctx.Unload();
        });

        r.Add("ideas storage: a failed check is retried a few times and then left alone", () =>
        {
            var env = new Env();
            var repo = env.Repo;
            for (var i = 0; i < 3; i++)
            {
                var (started, _, token) = repo.ClaimCheck("ses_2", 4, "5:7", 3, TimeSpan.FromMinutes(5));
                Check.True(started, $"try {i + 1} runs");
                repo.FinishCheck("ses_2", token, "the model was down");
            }
            var (tries, reason, _) = repo.ClaimCheck("ses_2", 4, "5:7", 3, TimeSpan.FromMinutes(5));
            Check.False(tries, "after three tries the conversation is not asked again: " + reason);
            env.Ctx.Unload();
        });

        r.Add("ideas storage: a repository cursor is remembered once, and only advances", () =>
        {
            var env = new Env();
            var repo = env.Repo;
            repo.RememberIfAbsent("C:/repo", "aaa", "prj_1", "Demo");
            Check.Equal("aaa", repo.LastSeen("C:/repo"));
            repo.RememberIfAbsent("C:/repo", "bbb", "prj_1", "Demo");
            Check.Equal("aaa", repo.LastSeen("C:/repo"), "a stored cursor is never overwritten by anchoring");
            repo.Remember("C:/repo", "ccc", "prj_1", "Demo");
            Check.Equal("ccc", repo.LastSeen("C:/repo"));
            Check.Equal(0, repo.ForgetStaleRepos(60), "and it is not forgotten yet");
            repo.Remember("C:/gone", "ddd");
            repo.SetMeta("x", "y");
            Check.Equal("y", repo.Meta("x"));
            repo.SetMeta("x", null);
            Check.True(repo.Meta("x") is null, "metadata can be cleared");
            env.Ctx.Unload();
        });

        // ---------------------------------------------------------------- the storage descriptor and the events

        r.Add("ideas storage: a plugin reload - a new instance over the same tables - keeps the backlog and the cards", async () =>
        {
            var env = new Env();
            await env.StartAsync();
            var idea = await env.Call("ideas.add", new JsonObject { ["idea"] = new JsonObject { ["title"] = "Survives a reload" } });
            var card = env.AddCard("sg_reload01", "Waiting through a reload");
            // A reload is a swap: the new instance starts while the old one is still registered. The store is the only
            // thing the two share, and neither of them holds a second copy of the backlog.
            var (other, second) = env.Second();
            try
            {
                Check.Equal(1, second.All().Count, "the new instance reads what the old one wrote");
                Check.Equal(1, second.CardCount(), "and the cards waiting for an answer");
                second.Update(idea["id"].Str(), new JsonObject { ["status"] = "done" }, fromUi: true);
                Check.Equal("done", env.Repo.Idea(idea["id"].Str())!["status"]!.Str(), "the old instance sees the new one's write");
                var saved = await env.Call("ideas.resolve", new JsonObject { ["id"] = card, ["action"] = "save" });
                Check.Equal("Waiting through a reload", saved["saved"]!["title"].Str(), "and the card the new instance sees can be answered by the old one");
                Check.Equal(0, second.CardCount(), "the card is gone for both");
            }
            finally { other.Unload(); env.Ctx.Unload(); }
        });

        r.Add("ideas storage: ideas.list describes the storage instead of pretending it is a file", async () =>
        {
            var env = new Env();
            await env.StartAsync();
            var list = await env.Rpc("ideas.list", new JsonObject());
            var storage = list["storage"]!.AsObject();
            Check.Equal("sqlite", storage["backend"].Str(), "the provider's own name, asked of the store: " + storage.ToJsonString());
            Check.Equal("netpi.db", storage["database"].Str());
            Check.Equal("netpi.ideas", storage["scope"].Str());
            Check.Equal(IdeasRepository.SchemaVersion, storage["schemaVersion"]!.GetValue<int>());
            Check.Equal(false, (bool)storage["editableFile"]!, "and says it is not an editable file");
            Check.Equal(false, (bool)list["exists"]!, "an empty backlog is not 'a file that is not there'");
            env.Ctx.Unload();
        });

        r.Add("ideas storage: every write announces itself, and only after it committed", async () =>
        {
            var env = new Env();
            await env.StartAsync();
            var seen = new List<JsonObject>();
            using var _ = env.Ctx.Events.Subscribe(IdeasPlugin.ChangedEvent, e => seen.Add((JsonObject)e.Data!));
            await env.Call("ideas.add", new JsonObject { ["idea"] = new JsonObject { ["title"] = "Announced" } });
            Check.Equal(1, seen.Count);
            Check.Equal("add", seen[0]["reason"].Str());
            // A refused write announces nothing: the event says what happened, not what was attempted.
            await env.Try("ideas.update", new JsonObject { ["id"] = "idea-nope", ["patch"] = new JsonObject { ["title"] = "x" } });
            Check.Equal(1, seen.Count, "a write that did not happen is not announced");
            env.Ctx.Unload();
        });
    }
}
