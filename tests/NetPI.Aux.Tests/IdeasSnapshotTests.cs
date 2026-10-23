using System.Text.Json.Nodes;
using NetPI.Ideas;

namespace NetPI.Aux.Tests;

/// <summary>
/// The portable snapshot of the backlog (<c>ideas.export</c> / <c>ideas.import</c>): what is written, what is taken
/// in, and what a merge refuses to do twice. Every test works on its own home with its own store; none of them
/// touches a real one.
/// <para>
/// The one-off cutover of the JSON files of the earlier versions (ideas.json, ideas-pending.json, the per-project
/// copies and their receipt) is not a test of this build any more: this build neither reads nor writes those files.
/// The one-off migration of the owner's own store was a script outside the product (commit c6480ff), not product code.
/// </para>
/// </summary>
public static class IdeasSnapshotTests
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
            Ctx = new FakePluginContext(T.TempDir("ideas-snapshot"));
            ProjectDir = T.TempDir("ideas-snapshot-proj");
            Project = Ctx.SessionsFake.CreateProject("Demo", ProjectDir);
            Session = Ctx.SessionsFake.CreateSession(new SessionInfo { Title = "s", ProjectId = Project.Id });
        }

        public Task StartAsync() => new IdeasPlugin().StartAsync(Ctx, CancellationToken.None);

        public async Task<JsonObject> Rpc(string method, JsonObject p) => (JsonObject)(await Ctx.RpcFake.Call(method, p))!;

        public async Task<List<JsonObject>> Ideas() => ((JsonArray)(await Rpc("ideas.list", new JsonObject()))["ideas"]!).OfType<JsonObject>().ToList();

        public string PathOf(string name) => Path.Combine(Home, name);
    }

    public static void Register(TestRunner r)
    {
        r.Add("ideas snapshot: an export is a portable snapshot, and importing it back changes nothing twice", async () =>
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

        r.Add("ideas snapshot: an imported document is stored sanitized — no escape, no more images than the cap", async () =>
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

            await env.StartAsync();
            var snapshot = new JsonObject
            {
                ["version"] = 1, ["format"] = "netpi.ideas.export", ["exportedAt"] = "2026-09-01T00:00:00Z",
                ["ideas"] = new JsonArray(new JsonObject { ["id"] = "idea-imp0001", ["title"] = "Imported", ["images"] = images.DeepClone() }),
            };
            var file = env.PathOf("snapshot.json");
            File.WriteAllText(file, snapshot.ToJsonString());
            var report = await env.Rpc("ideas.import", new JsonObject { ["path"] = file, ["mode"] = "merge" });
            Check.Equal(1, report["imported"]!["ideas"]!.GetValue<int>(), "the idea is in");
            CheckSanitized((await env.Ideas()).Single(i => i["id"].Str() == "idea-imp0001"));
            env.Ctx.Unload();
        });

        r.Add("ideas snapshot: an import announces once after it commits, and a status this build does not know is read, not stored", async () =>
        {
            var env = new Env();
            await env.StartAsync();
            var seen = new List<BusEvent>();
            using var _ = env.Ctx.Events.Subscribe(IdeasPlugin.ChangedEvent, seen.Add);
            var snapshot = new JsonObject
            {
                ["version"] = 1, ["format"] = "netpi.ideas.export", ["exportedAt"] = "2026-09-01T00:00:00Z",
                ["ideas"] = new JsonArray(
                    new JsonObject { ["id"] = "idea-imp0001", ["title"] = "Odd", ["status"] = "wip", ["priority"] = "urgent!!" },
                    new JsonObject { ["id"] = "idea-imp0002", ["title"] = "Empty", ["status"] = "" },
                    new JsonObject { ["id"] = "idea-imp0003", ["title"] = "Fine", ["status"] = "parked" }),
            };
            var file = env.PathOf("odd.json");
            File.WriteAllText(file, snapshot.ToJsonString());
            await env.Rpc("ideas.import", new JsonObject { ["path"] = file, ["mode"] = "merge" });
            Check.Equal(1, seen.Count, "one announcement for the import, not one per idea: " + string.Join(",", seen.Select(e => ((JsonObject)e.Data!)["reason"].Str())));
            Check.Equal("import", ((JsonObject)seen[0].Data!)["reason"].Str());
            var byId = (await env.Ideas()).ToDictionary(i => i["id"].Str());
            Check.Equal("in-progress", byId["idea-imp0001"]["status"].Str(), "wip is the status the tab knows as in-progress");
            Check.Equal("medium", byId["idea-imp0001"]["priority"].Str(), "an unknown priority falls back");
            Check.Equal("open", byId["idea-imp0002"]["status"].Str(), "an empty status falls back to open");
            Check.Equal("parked", byId["idea-imp0003"]["status"].Str());
            env.Ctx.Unload();
        });

        r.Add("ideas images: a size that is not an int is 0, not an internal error; files no idea references are swept once old", () =>
        {
            var images = JsonNode.Parse("""[{"path":"idea-images/a.png","bytes":1.5},{"path":"idea-images/b.png","bytes":3000000000},{"path":"idea-images/c.png","bytes":12}]""")!;
            var kept = IdeaImages.Sanitize(null, images).OfType<JsonObject>().Select(o => o["bytes"]!.GetValue<int>()).ToList();
            Check.Equal("0,0,12", string.Join(",", kept));

            var home = T.TempDir("ideas-sweep");
            var dir = Path.Combine(home, IdeaImages.Dir);
            Directory.CreateDirectory(dir);
            foreach (var name in new[] { "used.png", "orphan-old.png", "orphan-new.png" }) File.WriteAllBytes(Path.Combine(dir, name), [1]);
            foreach (var name in new[] { "used.png", "orphan-old.png" }) File.SetLastWriteTimeUtc(Path.Combine(dir, name), DateTime.UtcNow.AddDays(-3));
            var idea = new JsonObject { ["images"] = new JsonArray(new JsonObject { ["path"] = "idea-images/used.png" }) };
            Check.Equal(1, IdeaImages.Sweep(home, [idea], IdeaImages.SweepAge), "only the old orphan goes");
            Check.True(File.Exists(Path.Combine(dir, "used.png")), "a referenced file stays however old");
            Check.True(File.Exists(Path.Combine(dir, "orphan-new.png")), "a fresh orphan may belong to a composer that has not saved yet");
            Check.False(File.Exists(Path.Combine(dir, "orphan-old.png")));
            return Task.CompletedTask;
        });
    }
}
