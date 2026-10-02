using System.Text;
using System.Text.Json.Nodes;
using NetPI.Ideas;

namespace NetPI.Aux.Tests;

/// <summary>
/// The cutover and the portable snapshot (docs/plans/2026-09-29-ideas-sqlite-migration.md, assignment B): what is
/// read, what is refused, what survives, and what happens when it is interrupted. Every test works on its own home
/// with its own database; none of them touches a real one.
/// </summary>
public static class IdeasMigrationTests
{
    private sealed class Env
    {
        public FakePluginContext Ctx { get; }
        public string ProjectDir { get; }
        public ProjectInfo Project { get; }
        public SessionInfo Session { get; }
        public string Home => Ctx.Paths.Home;

        public Env()
        {
            Ctx = new FakePluginContext(T.TempDir("ideas-migration"));
            ProjectDir = T.TempDir("ideas-migration-proj");
            Project = Ctx.SessionsFake.CreateProject("Demo", ProjectDir);
            Session = Ctx.SessionsFake.CreateSession(new SessionInfo { Title = "s", ProjectId = Project.Id });
        }

        public Task StartAsync() => new IdeasPlugin().StartAsync(Ctx, CancellationToken.None);

        public async Task<JsonObject> Rpc(string method, JsonObject p) => (JsonObject)(await Ctx.RpcFake.Call(method, p))!;

        public async Task<(string? Code, string Message)> Try(string method, JsonObject p)
        {
            try { await Ctx.RpcFake.Call(method, p); return (null, ""); }
            catch (RpcException ex) { return (ex.Code, ex.Message); }
        }

        public async Task<List<JsonObject>> Ideas() => ((JsonArray)(await Rpc("ideas.list", new JsonObject()))["ideas"]!).OfType<JsonObject>().ToList();

        public async Task<List<JsonObject>> Cards() => ((JsonArray)(await Rpc("ideas.suggestions", new JsonObject()))["suggestions"]!).OfType<JsonObject>().ToList();

        public IdeasRepository Repo => IdeasRepository.Open(Ctx.Db, Ctx.Log, Ctx.Paths.DatabaseFile);

        public string PathOf(string name = "ideas.json") => Path.Combine(Home, name);

        public void Write(string name, string json) => File.WriteAllText(PathOf(name), json);

        /// <summary>Everything the cutover wrote into the home, by name.</summary>
        public List<string> Archive() => Directory.Exists(Path.Combine(Home, IdeasMigration.ArchiveFolder))
            ? [.. Directory.GetFiles(Path.Combine(Home, IdeasMigration.ArchiveFolder), "*", SearchOption.AllDirectories)]
            : [];
    }

    public static void Register(TestRunner r)
    {
        r.Add("ideas migration: the whole legacy state arrives once - backlog, cards, checks, cursors and answers", async () =>
        {
            var env = new Env();
            env.Write("ideas.json", """
                {
                  "version": 1,
                  "ideas": [
                    { "id": "idea-keep001", "title": "Keep me", "status": "open", "tags": ["a"],
                      "project": { "id": "prj_x", "name": "Other" },
                      "sessions": [{ "sessionId": "ses_1", "title": "an old chat", "at": "2026-09-20T10:00:00Z" }],
                      "commits": [{ "hash": "abc123", "short": "abc1234", "subject": "old work", "at": "2026-09-21T10:00:00Z" }],
                      "sections": [{ "id": "sec-old", "kind": "plan", "content": "the plan", "author": "bob" }] },
                    { "id": "idea-orph001", "title": "Bound to a project that is gone", "project": { "id": "prj_gone", "name": "Deleted" } }
                  ]
                }
                """);
            env.Write(IdeasMigration.PendingFileName, """
                {
                  "suggestions": [
                    { "id": "sg_wait001", "kind": "save", "sessionId": "ses_1", "sessionTitle": "an old chat",
                      "title": "A plan the chat left", "summary": "why", "at": "2026-09-22T10:00:00Z", "seen": false,
                      "project": { "id": "prj_x", "name": "Other" } },
                    { "id": "sg_done001", "kind": "done", "ideaId": "idea-keep001", "title": "Keep me",
                      "commits": ["abc1234 old work"], "at": "2026-09-23T10:00:00Z", "seen": false, "project": null }
                  ],
                  "ops": [
                    { "id": "op_half001", "action": "save", "cardId": "sg_half001", "at": "2026-09-24T10:00:00Z", "applied": false,
                      "idea": { "id": "idea-half001", "title": "An interrupted answer", "status": "open", "priority": "medium",
                                "tags": [], "sections": [], "createdAt": "2026-09-24T10:00:00Z", "updatedAt": "2026-09-24T10:00:00Z",
                                "createdBy": "user" } }
                  ],
                  "checked": { "ses_1": { "n": 4, "rev": "10:99", "state": "done", "tries": 0, "at": "2026-09-24T10:00:00Z" } },
                  "repos": { "C:/repo": { "hash": "abc123", "at": "2026-09-24T10:00:00Z" } }
                }
                """);

            await env.StartAsync();

            var ideas = await env.Ideas();
            Check.Equal(3, ideas.Count, "two from the backlog and the interrupted answer: " + string.Join(", ", ideas.Select(i => i["title"].Str())));
            var kept = ideas.Single(i => i["id"].Str() == "idea-keep001");
            Check.Equal("bob", kept["sections"]![0]!["author"].Str(), "unknown fields on a section");
            Check.Equal("ses_1", kept["sessions"]![0]!["sessionId"].Str(), "the chats that worked on it");
            Check.Equal("abc123", kept["commits"]![0]!["hash"].Str(), "its commits");
            Check.Equal("prj_x", kept["project"]!["id"].Str(), "its project");
            var orphan = ideas.Single(i => i["id"].Str() == "idea-orph001");
            Check.Equal("prj_gone", orphan["project"]!["id"].Str(), "a reference to a project that is gone is kept, not dropped");

            var cards = await env.Cards();
            Check.Equal(2, cards.Count, "the cards the user has not answered yet");
            Check.Equal("A plan the chat left", cards[0]!["title"].Str());

            var repo = env.Repo;
            Check.Equal("abc123", repo.LastSeen("C:/repo"), "the commit cursor: the commits made while NetPI was closed are still to be read");
            Check.Equal(1, repo.Checks().Count, "the check mark");
            Check.Equal("done", repo.Checks()[0]!["state"].Str());

            // The interrupted answer was finished, exactly once, and the card it belongs to is gone.
            var answered = await env.Rpc("ideas.resolve", new JsonObject { ["id"] = "sg_half001", ["action"] = "save" });
            Check.Equal(true, answered["alreadyResolved"]!.GetValue<bool>(), "the answer that was in flight is the one that stands");
            Check.Equal(3, (await env.Ideas()).Count, "and it is not saved a second time");

            // A restart changes nothing: the marker is there, and there is nothing left to read.
            env.Ctx.Unload();
            await env.StartAsync();
            Check.Equal(3, (await env.Ideas()).Count);
            Check.Equal(2, (await env.Cards()).Count);
            env.Ctx.Unload();
        });

        r.Add("ideas migration: a 'running' check mark becomes retryable, and a broken journal entry is reported, not guessed", async () =>
        {
            var env = new Env();
            env.Write("ideas.json", "{ \"ideas\": [] }");
            env.Write(IdeasMigration.PendingFileName, """
                {
                  "suggestions": [],
                  "ops": [
                    { "id": "op_ghost001", "action": "save", "cardId": "sg_ghost001", "at": "2026-09-24T10:00:00Z", "applied": true,
                      "idea": { "id": "idea-ghost01", "title": "The journal says this was written", "createdAt": "2026-09-24T10:00:00Z" } }
                  ],
                  "checked": { "ses_9": { "n": 2, "rev": "3:5", "state": "running", "tries": 0, "at": "2026-09-24T10:00:00Z" } }
                }
                """);

            await env.StartAsync();

            Check.Equal(0, (await env.Ideas()).Count, "an entry that claims to be applied while its idea is missing is not replayed as a guess");
            var mark = env.Repo.Checks().Single(m => m["sessionId"]!.Str() == "ses_9");
            Check.Equal("failed", mark["state"].Str(), "a claim nobody owns is retryable, not blocking for good");
            Check.Contains(mark["error"]!.Str(), "stopped before the check finished");
            env.Ctx.Unload();
        });

        r.Add("ideas migration: a custom file name, a bare array and extra root fields all arrive", async () =>
        {
            var env = new Env();
            env.Ctx.SettingsFake.Set("ideas.fileName", "backlog.json");
            env.Write("backlog.json", """[ { "id": "idea-bare001", "title": "From a bare array" } ]""");
            await env.StartAsync();
            var ideas = await env.Ideas();
            Check.Equal(1, ideas.Count, "a bare array is a backlog");
            Check.Equal("idea-bare001", ideas[0]!["id"].Str(), "with the id it had");
            var originals = Directory.GetFiles(Path.Combine(env.Home, IdeasMigration.ArchiveFolder), "*", SearchOption.AllDirectories)
                .Where(p => Path.GetDirectoryName(p)!.EndsWith("originals", StringComparison.Ordinal)).ToList();
            Check.Equal(1, originals.Count, "and the original is archived: " + string.Join(", ", env.Archive()));
            env.Ctx.Unload();
        });

        r.Add("ideas migration: the cutover is refused without a confirmation, and refused twice", async () =>
        {
            var env = new Env();
            env.Write("ideas.json", "{ \"ideas\": [ { \"id\": \"idea-once001\", \"title\": \"Once\" } ] }");
            await env.StartAsync();
            Check.Equal(1, (await env.Ideas()).Count, "the automatic cutover at the start already did it");
            var refused = await Check.ThrowsAsync<RpcException>(() => env.Ctx.RpcFake.Call("ideas.migrate", new JsonObject { ["confirm"] = true }));
            Check.Equal("conflict", refused.Code, "there is nothing left to import: " + refused.Message);
            var unconfirmed = await Check.ThrowsAsync<RpcException>(() => env.Ctx.RpcFake.Call("ideas.migrate", new JsonObject()));
            Check.Equal("bad_request", unconfirmed.Code, "and it never happens because nobody asked: " + unconfirmed.Message);
            Check.Equal(1, (await env.Ideas()).Count);
            env.Ctx.Unload();
        });

        r.Add("ideas migration: an ideas file an older build wrote afterwards is reported, and taken in only on purpose", async () =>
        {
            var env = new Env();
            env.Write("ideas.json", "{ \"ideas\": [ { \"id\": \"idea-old0001\", \"title\": \"From the JSON version\" } ] }");
            await env.StartAsync();
            Check.Equal(1, (await env.Ideas()).Count);

            // An older NetPI (or a hand edit) puts the file back where it reads it.
            env.Write("ideas.json", "{ \"ideas\": [ { \"id\": \"idea-old0002\", \"title\": \"Written by an old build\" } ] }");
            var report = await env.Rpc("ideas.migration", new JsonObject());
            Check.True(report["orphanedSources"] is JsonArray { Count: > 0 }, "the file that came back is reported: " + report.ToJsonString());
            Check.Equal(1, (await env.Ideas()).Count, "and nothing was merged behind the user's back");

            var (code, message) = await env.Try("ideas.migrate", new JsonObject { ["confirm"] = true, ["force"] = true });
            Check.True(code is null, "an explicit import takes it in: " + message);
            Check.Equal(2, (await env.Ideas()).Count, "both the imported one and the one from the old build");
            env.Ctx.Unload();
        });

        r.Add("ideas migration: the source a per-project receipt already merged is not merged again", async () =>
        {
            var env = new Env();
            Directory.CreateDirectory(Path.Combine(env.ProjectDir, ".netpi"));
            File.WriteAllText(Path.Combine(env.ProjectDir, ".netpi", "ideas.json"),
                "{ \"ideas\": [ { \"id\": \"idea-merged1\", \"title\": \"Merged a while ago\" } ] }");
            var merged = Path.Combine(env.ProjectDir, ".netpi", "ideas.json");
            File.WriteAllText(Path.Combine(env.Home, IdeasMigration.ReceiptFileName), new JsonObject
            {
                ["version"] = 1,
                ["imports"] = new JsonArray(new JsonObject
                {
                    ["source"] = merged,
                    ["sha256"] = LegacyBacklog.HashOf(File.ReadAllBytes(merged)),
                    ["imported"] = new JsonArray("idea-merged1"),
                    ["at"] = "2026-09-20T10:00:00Z",
                }),
            }.ToJsonString());
            env.Write("ideas.json", "{ \"ideas\": [ { \"id\": \"idea-global1\", \"title\": \"The global backlog\" } ] }");

            await env.StartAsync();
            var ideas = await env.Ideas();
            Check.Equal(1, ideas.Count, "the per-project file the receipt already accounted for is not merged again: " + string.Join(", ", ideas.Select(i => i["title"].Str())));
            Check.Equal("idea-global1", ideas[0]!["id"].Str());
            // ... and it is left where it is: the receipt accounted for it, so nothing is merged and nothing is deleted.
            // Removing it is housekeeping, not part of a successful import.
            Check.True(File.Exists(Path.Combine(env.ProjectDir, ".netpi", "ideas.json")), "the file the receipt accounted for is left alone");
            env.Ctx.Unload();
        });

        r.Add("ideas migration: an export is a portable snapshot, and importing it back changes nothing twice", async () =>
        {
            var env = new Env();
            await env.StartAsync();
            await env.Rpc("ideas.add", new JsonObject
            {
                ["sessionId"] = env.Session.Id,
                ["idea"] = new JsonObject { ["title"] = "Portable", ["summary"] = "it travels", ["tags"] = new JsonArray("x"),
                    ["custom"] = new JsonObject { ["k"] = 1 },
                    ["sections"] = new JsonArray(new JsonObject { ["kind"] = "plan", ["content"] = "the plan" }) },
            });
            var path = await env.Rpc("ideas.export", new JsonObject { ["path"] = Path.Combine(env.Home, "ideas-export.json") });
            var exported = ((JsonObject)path["json"]!).ToJsonString();
            Check.Equal((long)1, ((JsonObject)path["json"]!)["ideas"]!.AsArray().Count);
            Check.True(File.Exists(path["file"]!.Str()), "and it is written where the user asked: " + path["file"]!.Str());
            var onDisk = JsonNode.Parse(File.ReadAllText(path["file"]!.Str()))!.AsObject();
            Check.Equal(exported, onDisk.ToJsonString(new System.Text.Json.JsonSerializerOptions()), "the file is the snapshot");

            // A second home takes it in.
            var other = new Env();
            await other.StartAsync();
            var report = await other.Rpc("ideas.import", new JsonObject { ["path"] = path["file"]!.Str(), ["mode"] = "merge" });
            Check.Equal(1, report["ideas"]!.GetValue<int>(), "one idea in the snapshot");
            var idea = (await other.Ideas()).Single();
            Check.Equal("Portable", idea["title"].Str());
            Check.Equal("it travels", idea["summary"].Str());
            Check.Equal("{\"k\":1}", idea["custom"]!.ToJsonString(), "a field this build does not know travels too");
            Check.Equal("the plan", idea["sections"]![0]!["content"].Str());
            Check.Equal("1", idea["sections"]![0]!["kind"].Str() == "plan" ? "1" : "0", "and the section is there");

            // Merging it again is a no-op, not a second copy.
            var again = await other.Rpc("ideas.import", new JsonObject { ["path"] = path["file"]!.Str(), ["mode"] = "merge" });
            Check.Equal(1, ((JsonArray)again["conflicts"]!).Count, "the idea is already here: " + again.ToJsonString());
            Check.Equal(1, (await other.Ideas()).Count, "and a merge never overwrites or duplicates");

            // "validate" says what would happen and changes nothing.
            var validated = await other.Rpc("ideas.import", new JsonObject { ["path"] = path["file"]!.Str(), ["mode"] = "validate" });
            Check.Equal(1, ((JsonArray)validated["conflicts"]!).Count);
            Check.False(validated.ContainsKey("imported"), "a preview is not an import");
            Check.Equal(1, (await other.Ideas()).Count);
            other.Ctx.Unload();
            env.Ctx.Unload();
        });

        r.Add("ideas migration: an imported document is stored sanitized — no escape, no more images than the cap", async () =>
        {
            var env = new Env();
            var images = new JsonArray();
            images.Add(new JsonObject { ["path"] = "idea-images/../settings.json", ["name"] = "leak", ["mediaType"] = "image/png", ["bytes"] = 10 });
            images.Add(new JsonObject { ["path"] = "C:/Windows/win.ini", ["name"] = "abs", ["mediaType"] = "image/png", ["bytes"] = 10 });
            for (var i = 1; i <= 8; i++)
                images.Add(new JsonObject { ["path"] = $"idea-images/img-{i:00}.png", ["name"] = $"img-{i:00}.png", ["mediaType"] = "image/png", ["bytes"] = 10 });
            void CheckSanitized(JsonObject stored)
            {
                var kept = ((JsonArray)stored["images"]!).OfType<JsonObject>().Select(o => o["path"]!.Str()).ToList();
                Check.Equal(IdeaImages.MaxPerIdea, kept.Count, "the escape and the absolute reference are out, and the cap stands: " + string.Join(", ", kept));
                Check.Equal("idea-images/img-01.png", kept[0], "and it starts with the first image that is in");
                Check.Equal("idea-images/img-06.png", kept[^1]);
                Check.True(kept.All(p => p!.StartsWith(IdeaImages.Dir + "/")), "every stored reference stays in the images directory");
            }

            // Path one: the cutover brings in a whole legacy document.
            File.WriteAllText(env.PathOf(), new JsonObject
            {
                ["version"] = 1,
                ["ideas"] = new JsonArray(new JsonObject { ["id"] = "idea-cut0001", ["title"] = "Cut over", ["images"] = images.DeepClone() }),
            }.ToJsonString());
            await env.StartAsync();
            CheckSanitized((await env.Ideas()).Single(i => i["id"].Str() == "idea-cut0001"));

            // Path two: ideas.import takes a snapshot in.
            var snapshot = new JsonObject
            {
                ["version"] = 1, ["format"] = "netpi.ideas.export", ["exportedAt"] = "2026-09-01T00:00:00Z",
                ["ideas"] = new JsonArray(new JsonObject { ["id"] = "idea-imp0001", ["title"] = "Imported", ["images"] = images.DeepClone() }),
            };
            var file = Path.Combine(env.Home, "snapshot.json");
            File.WriteAllText(file, snapshot.ToJsonString());
            var report = await env.Rpc("ideas.import", new JsonObject { ["path"] = file, ["mode"] = "merge" });
            Check.Equal(1, report["imported"]!["ideas"]!.GetValue<int>(), "the idea is in");
            CheckSanitized((await env.Ideas()).Single(i => i["id"].Str() == "idea-imp0001"));
            env.Ctx.Unload();
        });

        r.Add("ideas migration: a restart after the commit does not import the old files again", async () =>
        {
            var env = new Env();
            env.Write("ideas.json", "{ \"ideas\": [ { \"id\": \"idea-twice01\", \"title\": \"Twice\" } ] }");
            await env.StartAsync();
            var archived = env.Archive();
            Check.True(archived.Count > 0, "the source is archived: " + string.Join(", ", archived.Select(Path.GetFileName)));
            Check.False(File.Exists(env.PathOf()), "and moved out of the way");
            env.Ctx.Unload();
            await env.StartAsync();
            env.Ctx.Unload();
            await env.StartAsync();
            Check.Equal(1, (await env.Ideas()).Count, "one idea, however often NetPI starts");
            Check.Equal(archived.Count, env.Archive().Count, "and one archive folder");
            env.Ctx.Unload();
        });
    }
}
