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
    }
}
