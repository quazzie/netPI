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

        /// <summary>An idea of the backlog, as the storage tests add them (through the repository: this suite needs no RPC).</summary>
        public string AddIdea(string title = "An idea") => Repo.Add(IdeaOps.CreateIdea(
            new JsonObject { ["title"] = title }, Repo.TakenIds(), "user", Session.Id)).Doc["id"]!.Str()!;
    }

    public static void Register(TestRunner r)
    {
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
                repo.Update(kept.Doc["id"]!.Str()!, new JsonObject { ["title"] = "Changed inside" }, fromUi: true);
                throw new InvalidOperationException("the middle of the work failed");
            }));
            Check.Equal("the middle of the work failed", rolled.Message);
            Check.Equal(1, repo.Count(), "the idea written inside the transaction is gone");
            Check.Equal("Kept", repo.Idea(kept.Doc["id"]!.Str())!["title"].Str(), "and so is everything else it wrote");
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

        r.Add("ideas storage: a document stored under a key its id does not name is updated in place, not copied under its id", async () =>
        {
            var env = new Env();
            await env.StartAsync();
            var repo = env.Repo;
            var legacy = IdeaOps.CreateIdea(new JsonObject { ["title"] = "Old title" }, repo.TakenIds(), "user", null);
            legacy["id"] = "idea-real";
            legacy["ord"] = 0L; legacy["revision"] = 1L; legacy["idLower"] = "legacy-key";
            env.Ctx.Data.Collection("items", new CollectionSpec().Text("idLower").Integer("ord").Text("status").Text("projectId")).Put("legacy-key", legacy);
            Check.Equal(1, repo.Count());
            repo.Update("legacy-key", new JsonObject { ["title"] = "New title" }, fromUi: true);
            Check.Equal(1, repo.Count(), "the update did not leave a second document under the id field");
            Check.Equal("New title", repo.Idea("legacy-key")!["title"].Str());
            repo.AddSession("legacy-key", env.Session.Id);
            Check.Equal(1, repo.Count(), "nor did the session record");
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
            repo.Update(idea["id"]!.Str()!, new JsonObject { ["status"] = "done" }, fromUi: true);
            var after = repo.Idea(idea["id"]!.Str())!;
            Check.Equal("{\"days\":3}", after["estimate"]!.ToJsonString());
            Check.Equal("bob", after["sections"]![0]!["author"].Str());
            Check.Equal("done", after["status"].Str());
            Check.Equal(env.Session.Id, after["sessionIds"]![0].Str());
            env.Ctx.Unload();
        });

        // ---------------------------------------------------------------- the storage descriptor and the events

        r.Add("ideas storage: a plugin reload - a new instance over the same tables - keeps the backlog", async () =>
        {
            var env = new Env();
            await env.StartAsync();
            var idea = await env.Call("ideas.add", new JsonObject { ["idea"] = new JsonObject { ["title"] = "Survives a reload" } });
            // A reload is a swap: the new instance starts while the old one is still registered. The store is the only
            // thing the two share, and neither of them holds a second copy of the backlog.
            var (other, second) = env.Second();
            try
            {
                Check.Equal(1, second.All().Count, "the new instance reads what the old one wrote");
                second.Update(idea["id"].Str(), new JsonObject { ["status"] = "done" }, fromUi: true);
                Check.Equal("done", env.Repo.Idea(idea["id"].Str())!["status"]!.Str(), "the old instance sees the new one's write");
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
            env.Ctx.Unload();
        });

        r.Add("ideas storage: every write announces itself, and only after it committed", async () =>
        {
            var env = new Env();
            await env.StartAsync();
            var seen = new List<BusEvent>();
            using var _ = env.Ctx.Events.Subscribe(IdeasPlugin.ChangedEvent, seen.Add);
            await env.Call("ideas.add", new JsonObject { ["idea"] = new JsonObject { ["title"] = "Announced" } });
            Check.Equal(1, seen.Count, "one event per write, no storm");
            var data = (JsonObject)seen[0].Data!;
            Check.Equal("add", data["reason"].Str());
            Check.Equal("sqlite", data["backend"].Str(), "the event says where the backlog lives now");
            Check.True(seen[0].SessionId is null, "and it is a broadcast: every window refreshes, not only the chat that wrote");
            // A refused write announces nothing: the event says what happened, not what was attempted.
            await env.Try("ideas.update", new JsonObject { ["id"] = "idea-nope", ["patch"] = new JsonObject { ["title"] = "x" } });
            Check.Equal(1, seen.Count, "a write that did not happen is not announced");
            env.Ctx.Unload();
        });
    }
}
