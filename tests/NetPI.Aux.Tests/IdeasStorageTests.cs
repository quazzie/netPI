using System.Text;
using System.Text.Json.Nodes;
using NetPI.Ideas;

namespace NetPI.Aux.Tests;

/// <summary>
/// The storage half of the Ideas flow: what happens when a write fails, a second store is live, a run is interrupted
/// between the two files the plugin owns, or the pending file is not what we expect (docs/plans/
/// 2026-09-29-ideas-flow-storage-handoff.md, assignment A). Every test here injects a failure; the happy paths live
/// in <see cref="IdeasTests"/>.
/// </summary>
public static class IdeasStorageTests
{
    private sealed class Env
    {
        public FakePluginContext Ctx { get; }
        public string ProjectDir { get; }
        public ProjectInfo Project { get; }
        public SessionInfo Session { get; }
        public string Backlog => Path.Combine(Ctx.Paths.Home, "ideas.json");
        public string PendingFile => Path.Combine(Ctx.Paths.Home, "ideas-pending.json");

        public Env()
        {
            Ctx = new FakePluginContext(T.TempDir("ideas-home"));
            ProjectDir = T.TempDir("ideas-proj");
            Project = Ctx.SessionsFake.CreateProject("Demo", ProjectDir);
            Session = Ctx.SessionsFake.CreateSession(new SessionInfo { Title = "s", ProjectId = Project.Id });
        }

        public Task StartAsync() => new IdeasPlugin().StartAsync(Ctx, CancellationToken.None);

        public async Task<JsonObject> Rpc(string method, JsonObject p) => (JsonObject)(await Ctx.RpcFake.Call(method, p))!;

        public async Task<JsonObject> Call(string method, JsonObject p)
        {
            try { return await Rpc(method, p); }
            catch (RpcException ex) { throw new AssertException($"{method} failed ({ex.Code}): {ex.Message}"); }
        }

        /// <summary>What the code would get back (the code) as well as the message.</summary>
        public async Task<(string? Code, string Message)> Try(string method, JsonObject p)
        {
            try { await Rpc(method, p); return (null, ""); }
            catch (RpcException ex) { return (ex.Code, ex.Message); }
        }

        /// <summary>A card in the pending file, as a closed chat or a commit check leaves it.</summary>
        public string Card(string id = "sg_card00001", string title = "Unsaved plan", string kind = "save", string? ideaId = null)
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
            Write(new JsonObject { ["suggestions"] = new JsonArray(card) });
            return id;
        }

        public JsonObject ReadPending() => JsonNode.Parse(File.ReadAllText(PendingFile))!.AsObject();

        public void Write(JsonObject pending)
        {
            File.WriteAllText(PendingFile, pending.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }

        public List<string> Titles()
        {
            if (!File.Exists(Backlog)) return [];
            var root = JsonNode.Parse(File.ReadAllText(Backlog))!;
            return ((JsonArray?)root["ideas"] ?? []).Select(i => i!["title"].Str()).ToList();
        }
    }

    /// <summary>Hold a file the way an editor or a backup tool does: readable, but not replaceable and not deletable.</summary>
    private static FileStream Held(string path)
    {
        System.IO.File.WriteAllText(path, System.IO.File.Exists(path) ? System.IO.File.ReadAllText(path) : "");
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
    }

    public static void Register(TestRunner r)
    {
        r.Add("ideas storage: two stores on one file keep each other's idea (a reload swap has two locks, not one)", async () =>
        {
            var env = new Env();
            await env.StartAsync();
            // Two live instances: the old plugin serving while the new one starts.
            var second = new IdeasStore(env.Ctx.Events, env.Ctx.Logger);
            await Task.WhenAll(
                Task.Run(() => second.UpdateAsync<object?>(env.Backlog, f => { f.Ideas.Add(IdeaOps.CreateIdea(new JsonObject { ["title"] = "From the new instance" }, f.Ideas, "user", null)); return null; })),
                Task.Run(() => second.UpdateAsync<object?>(env.Backlog, f => { f.Ideas.Add(IdeaOps.CreateIdea(new JsonObject { ["title"] = "Also new" }, f.Ideas, "user", null)); return null; })),
                Task.Run(() => second.ReadAsync(env.Backlog, f => (object?)f.Ideas.Count)));
            var titles = env.Titles();
            Check.Equal(2, titles.Count, "both updates are in the file: " + string.Join(", ", titles));
            Check.True(titles.Contains("From the new instance") && titles.Contains("Also new"));
            second.Dispose();
            env.Ctx.Unload();
        });

        r.Add("ideas storage: a file an editor holds is reported and the backlog stays whole", async () =>
        {
            var env = new Env();
            await env.StartAsync();
            await env.Call("ideas.add", new JsonObject { ["idea"] = new JsonObject { ["title"] = "Kept" } });
            var before = File.ReadAllText(env.Backlog);

            using (Held(env.Backlog))
            {
                var (code, message) = await env.Try("ideas.add", new JsonObject { ["idea"] = new JsonObject { ["title"] = "Lost?" } });
                Check.Equal("io_error", code, "the write is reported: " + message);
            }
            Check.Equal(before, File.ReadAllText(env.Backlog), "the last valid backlog is untouched");

            // and it writes normally once the editor lets go
            await env.Call("ideas.add", new JsonObject { ["idea"] = new JsonObject { ["title"] = "After" } });
            Check.True(env.Titles().Contains("After"));
            env.Ctx.Unload();
        });

        r.Add("ideas storage: listing the cards does not rewrite the file they live in", async () =>
        {
            var env = new Env();
            await env.StartAsync();
            env.Card();
            var first = (await env.Call("ideas.suggestions", new JsonObject()))["suggestions"]!.AsArray().Count;
            Check.Equal(1, first);
            var bytes = File.ReadAllBytes(env.PendingFile);
            var stamp = File.GetLastWriteTimeUtc(env.PendingFile);
            await Task.Delay(20);
            await env.Call("ideas.suggestions", new JsonObject());
            await env.Call("ideas.suggestions", new JsonObject());
            Check.True(bytes.SequenceEqual(File.ReadAllBytes(env.PendingFile)), "the file is byte for byte the same");
            Check.Equal(stamp, File.GetLastWriteTimeUtc(env.PendingFile), "and it was not even touched");
            env.Ctx.Unload();
        });

        r.Add("ideas storage: a pending file we do not understand is reported, not replaced", async () =>
        {
            var env = new Env();
            await env.StartAsync();
            env.Card();
            File.WriteAllText(env.PendingFile, "{ \"suggestions\": { \"oops\": true } }");
            var (code, message) = await env.Try("ideas.suggestions", new JsonObject());
            Check.Equal("invalid_file", code, "the cards cannot be read: " + message);
            Check.Contains(message, "suggestions");
            Check.Equal("{ \"suggestions\": { \"oops\": true } }", File.ReadAllText(env.PendingFile), "never overwritten");

            // and neither is an answer written while it is broken
            File.WriteAllText(env.PendingFile, "{ \"suggestions\": [ { \"id\": \"sg_x\", \"kind\": \"save\", \"title\": \"t\" } ], \"checked\": 7 }");
            var (code2, _) = await env.Try("ideas.resolve", new JsonObject { ["id"] = "sg_x", ["action"] = "save" });
            Check.Equal("invalid_file", code2);
            Check.Contains(File.ReadAllText(env.PendingFile), "\"checked\": 7", "the broken file is left as it is");
            env.Ctx.Unload();
        });

        r.Add("ideas storage: a card survives a backlog that cannot be written, and the retry saves exactly one idea", async () =>
        {
            var env = new Env();
            await env.StartAsync();
            await env.Call("ideas.add", new JsonObject { ["idea"] = new JsonObject { ["title"] = "Already there" } });
            var card = env.Card();

            using (Held(env.Backlog))
            {
                var (code, _) = await env.Try("ideas.resolve", new JsonObject { ["id"] = card, ["action"] = "save" });
                Check.Equal("io_error", code, "the answer could not be applied");
            }
            Check.Equal(1, (await env.Call("ideas.suggestions", new JsonObject()))["suggestions"]!.AsArray().Count, "the card is still there");
            Check.Equal(0, env.ReadPending()["ops"]!.AsArray().Count, "no journal entry is left behind");
            Check.Equal(1, env.Titles().Count, "and no half-written idea");

            var saved = await env.Call("ideas.resolve", new JsonObject { ["id"] = card, ["action"] = "save" });
            Check.Equal("Unsaved plan", saved["saved"]!["title"].Str());
            Check.Equal(2, env.Titles().Count, "one new idea: " + string.Join(", ", env.Titles()));
            Check.Equal(0, (await env.Call("ideas.suggestions", new JsonObject()))["suggestions"]!.AsArray().Count, "the card is answered");
            Check.Equal(0, env.ReadPending()["ops"]!.AsArray().Count);
            env.Ctx.Unload();
        });

        r.Add("ideas storage: an answer interrupted between the two files is finished once at the next start", async () =>
        {
            var env = new Env();
            await env.StartAsync();
            env.Card("sg_half0001");

            // What a crash between "the answer is written" and "the backlog is written" leaves behind.
            var idea = new JsonObject
            {
                ["id"] = "idea-half001",
                ["title"] = "Unsaved plan",
                ["status"] = "open",
                ["priority"] = "medium",
                ["tags"] = new JsonArray(),
                ["sections"] = new JsonArray(),
                ["createdAt"] = "2026-09-29T10:00:00Z",
                ["updatedAt"] = "2026-09-29T10:00:00Z",
                ["createdBy"] = "user",
            };
            env.Write(new JsonObject
            {
                ["suggestions"] = JsonNode.Parse(File.ReadAllText(env.PendingFile))!["suggestions"]!.DeepClone(),
                ["ops"] = new JsonArray(new JsonObject
                {
                    ["id"] = "op_half0001",
                    ["action"] = "save",
                    ["cardId"] = "sg_half0001",
                    ["at"] = "2026-09-29T10:00:00Z",
                    ["applied"] = false,
                    ["idea"] = idea,
                }),
            });
            env.Ctx.Unload();

            await env.StartAsync(); // the recovery runs at the start
            Check.Equal(1, env.Titles().Count, "the idea is there exactly once");
            Check.Equal("Unsaved plan", env.Titles()[0]);
            Check.Equal(0, (await env.Call("ideas.suggestions", new JsonObject()))["suggestions"]!.AsArray().Count, "the card is gone");
            Check.Equal(0, env.ReadPending()["ops"]!.AsArray().Count, "the journal is empty again");

            // a second start does not save it again
            env.Ctx.Unload();
            await env.StartAsync();
            Check.Equal(1, env.Titles().Count);
            env.Ctx.Unload();
        });

        r.Add("ideas storage: two windows answering the same card save one idea", async () =>
        {
            var env = new Env();
            await env.StartAsync();
            var card = env.Card();
            var results = await Task.WhenAll(
                env.Try("ideas.resolve", new JsonObject { ["id"] = card, ["action"] = "save" }),
                env.Try("ideas.resolve", new JsonObject { ["id"] = card, ["action"] = "save" }));
            Check.True(results.Count(x => x.Code is null) == 1, "exactly one window answered it: " + string.Join(", ", results.Select(x => x.Code ?? "ok")));
            Check.True(results.Any(x => x.Code is "not_found" or "conflict"), "the other is told the card is taken: " + string.Join(", ", results.Select(x => x.Code ?? "ok")));
            Check.Equal(1, env.Titles().Count, "one idea, not two");
            Check.Equal(0, env.ReadPending()["ops"]!.AsArray().Count);
            env.Ctx.Unload();
        });

        r.Add("ideas storage: a card answered with the wrong action is refused, and stays", async () =>
        {
            var env = new Env();
            await env.StartAsync();
            env.Card("sg_wrong001", "Unsaved plan", "save");
            var (code, message) = await env.Try("ideas.resolve", new JsonObject { ["id"] = "sg_wrong001", ["action"] = "done" });
            Check.Equal("bad_request", code, "a save card is not marked done: " + message);
            Check.Equal(1, (await env.Call("ideas.suggestions", new JsonObject()))["suggestions"]!.AsArray().Count, "the card is still there");
            Check.Equal(0, env.Titles().Count);
            env.Ctx.Unload();
        });

        r.Add("ideas storage: an edit with a stale timestamp is a conflict, not an overwrite", async () =>
        {
            var env = new Env();
            await env.StartAsync();
            var idea = (await env.Call("ideas.add", new JsonObject { ["idea"] = new JsonObject { ["title"] = "Editable" } }))!;
            var stamp = idea["updatedAt"].Str();

            await env.Call("ideas.add", new JsonObject { ["idea"] = new JsonObject { ["title"] = "Other" } });
            var (code, message) = await env.Try("ideas.update", new JsonObject
            {
                ["id"] = idea["id"].Str(),
                ["patch"] = new JsonObject { ["summary"] = "from a stale window" },
                ["expectedUpdatedAt"] = "2020-01-01T00:00:00Z",
            });
            Check.Equal("conflict", code, "the stale window is told: " + message);

            var ok = await env.Call("ideas.update", new JsonObject
            {
                ["id"] = idea["id"].Str(),
                ["patch"] = new JsonObject { ["summary"] = "from the window that has it" },
                ["expectedUpdatedAt"] = stamp,
            });
            Check.Equal("from the window that has it", ok["summary"]!.Str());
            Check.Equal(2, env.Titles().Count, "nothing was overwritten or lost");
            env.Ctx.Unload();
        });

        r.Add("ideas storage: a migration that could not delete its source imports it once, not twice", async () =>
        {
            var env = new Env();
            var legacy = Path.Combine(env.ProjectDir, ".netpi");
            Directory.CreateDirectory(legacy);
            var source = Path.Combine(legacy, "ideas.json");
            File.WriteAllText(source, "{ \"ideas\": [ { \"id\": \"idea-once01\", \"title\": \"From the old file\" } ] }\n");

            // The editor has the source open: the import runs, the delete cannot.
            using (var held = Held(source))
            {
                await env.StartAsync();
                Check.Equal(1, env.Titles().Count, "imported");
                Check.True(File.Exists(source), "the source is still there");
                env.Ctx.Unload();
            }

            await env.StartAsync(); // a restart with the same source
            Check.Equal(1, env.Titles().Count, "not imported a second time");
            Check.False(File.Exists(source), "the delete is finished at the next start");
            Check.True(File.Exists(Path.Combine(env.Ctx.Paths.Home, MigrationReceipt.FileName)), "the receipt says what was imported");
            env.Ctx.Unload();
        });

        r.Add("ideas storage: two instances of the plugin share the lock, so the second waits instead of overwriting", async () =>
        {
            var dir = T.TempDir("ideas-lock");
            var file = Path.Combine(dir, "ideas.json");
            File.WriteAllText(file, "{ \"version\": 1, \"ideas\": [] }\n");
            Check.Equal(".ideas.json.lock", Path.GetFileName(FileGate.LockPath(file)));

            // Two gates, as two plugin instances have: the second one cannot read-modify-write while the first holds it.
            var one = new FileGate();
            var two = new FileGate();
            var order = new List<string>();
            var holding = one.WithFileAsync(file, async _ =>
            {
                order.Add("one in");
                await Task.Delay(150);
                order.Add("one out");
                return 0;
            }, CancellationToken.None);
            await Task.Delay(20);
            var waiting = two.WithFileAsync(file, async _ => { order.Add("two in"); return 0; }, CancellationToken.None);
            Check.Equal(1, order.Count(x => x == "one in"), "the second gate is still waiting");
            await Task.WhenAll(holding, waiting);
            Check.Equal("one in,one out,two in", string.Join(",", order), "they take turns");

            // A lock nobody releases makes the writer wait and then report, and write nothing.
            using var held = new FileStream(FileGate.LockPath(file), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            IOException? failure = null;
            try
            {
                await new FileGate().WithFileAsync(file, async token =>
                {
                    await FileGate.WriteAtomicAsync(file, Encoding.UTF8.GetBytes("{ \"version\": 1, \"ideas\": [1] }"), token);
                    return 0;
                }, CancellationToken.None);
            }
            catch (IOException ex) { failure = ex; }
            Check.True(failure?.Message.Contains("locked") == true, "the writer is told why: " + failure?.Message);
            Check.Equal("{ \"version\": 1, \"ideas\": [] }\n", File.ReadAllText(file), "and nothing was written");
        });
    }
}
