using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NetPI.Ideas;

namespace NetPI.Aux.Tests;

public static class IdeasTests
{
    private sealed class Env
    {
        public FakePluginContext Ctx { get; }
        public string ProjectDir { get; }
        public ProjectInfo Project { get; }
        public SessionInfo Session { get; }
        public SessionInfo GlobalSession { get; }
        public string File => Path.Combine(ProjectDir, "ideas.json");

        public Env()
        {
            Ctx = new FakePluginContext(T.TempDir("ideas-home"));
            ProjectDir = T.TempDir("ideas-proj");
            Project = Ctx.SessionsFake.CreateProject("Demo", ProjectDir);
            Session = Ctx.SessionsFake.CreateSession(new SessionInfo { Title = "s", ProjectId = Project.Id });
            GlobalSession = Ctx.SessionsFake.CreateSession(new SessionInfo { Title = "g" });
        }

        public async Task StartAsync() => await new IdeasPlugin().StartAsync(Ctx, CancellationToken.None);

        public IAgentTool Tool(string name) => Ctx.ToolsFake.Get(name) ?? throw new AssertException("no tool " + name);

        public Task<ToolResult> Run(string tool, object args, bool withProject = true) =>
            Tool(tool).ExecuteAsync(new ToolContext
            {
                SessionId = withProject ? Session.Id : GlobalSession.Id, AgentId = "agt_7", CallId = "call_1",
                Cwd = ProjectDir, Project = withProject ? Project : null, Services = Ctx.Services, Events = Ctx.Events,
            }, T.Args(args), CancellationToken.None);

        public async Task<JsonObject> Rpc(string method, JsonObject p) => (JsonObject)(await Ctx.RpcFake.Call(method, p))!;

        public string Raw() => Encoding.UTF8.GetString(System.IO.File.ReadAllBytes(File));
    }

    private static void Ok(ToolResult r) { if (r.IsError) throw new AssertException("tool error: " + r.Content); }

    private static JsonObject Idea(ToolResult r) => (JsonObject)((JsonObject)NetPiJson.ToNode(r.Details)!)["idea"]!;

    public static void Register(TestRunner r)
    {
        r.Add("ideas: plugin registers tools, RPC, tab and /idea command", async () =>
        {
            var env = new Env();
            await env.StartAsync();
            Check.Equal("idea_add,idea_get,idea_list,idea_remove,idea_update", string.Join(",", env.Ctx.ToolsFake.Tools.Select(t => t.Definition.Name).Order()));
            Check.True(env.Ctx.ToolsFake.Tools.All(t => t.Definition.Category == "ideas"));
            Check.True(env.Tool("idea_list").Definition.ReadOnly && env.Tool("idea_get").Definition.ReadOnly);
            Check.Contains(string.Join(" ", env.Tool("idea_add").Definition.PromptGuidelines!), "record them with idea_add instead of losing them");
            foreach (var m in new[] { "ideas.list", "ideas.get", "ideas.add", "ideas.update", "ideas.delete", "ideas.reorder", "ideas.toPrompt", "ideas.quickAdd" })
                Check.True(env.Ctx.RpcFake.Exists(m), m);
            var tab = env.Ctx.UiFake.TabList.Single();
            Check.True(tab is { Id: "ideas", Title: "Ideas", Panel: UiPanel.Right, Icon: "idea", Order: 20, Module: "ui.js" });
            var cmd = env.Ctx.UiFake.CommandList.Single();
            Check.True(cmd is { Name: "idea", ArgsHint: "<title>", Rpc: "ideas.quickAdd" });
            env.Ctx.Unload();
        });

        r.Add("ideas: agent tools round trip (add, list, get, update sections, remove)", async () =>
        {
            var env = new Env();
            await env.StartAsync();
            var add = await env.Run("idea_add", new JsonObject
            {
                ["title"] = "Cache model list",
                ["summary"] = "Avoid refetching /v1/models on every session switch.",
                ["priority"] = "high",
                ["tags"] = new JsonArray("perf", "providers"),
                ["sections"] = new JsonArray(
                    new JsonObject { ["kind"] = "research", ["title"] = "Findings", ["content"] = "models.list takes 800ms on AiProxy." },
                    new JsonObject { ["kind"] = "plan", ["content"] = "1. Cache for 60s\n2. Invalidate on models.changed" }),
            });
            Ok(add);
            var idea = Idea(add);
            var id = idea["id"].Str();
            Check.True(id.StartsWith("idea-") && id.Length == 11, id);
            Check.Equal("agent:agt_7", idea["createdBy"].Str());
            Check.Equal(env.Session.Id, idea["sessionIds"]![0].Str());
            Check.Equal("open", idea["status"].Str());
            Check.True(System.IO.File.Exists(env.File));
            var raw = env.Raw();
            Check.NotContains(raw, "\r\n");
            Check.True(raw.StartsWith("{\n  \"version\": 1,\n  \"ideas\": [\n"), raw[..Math.Min(60, raw.Length)]);

            await env.Run("idea_add", new { title = "Old thing", tags = "misc, #old" });
            var list = await env.Run("idea_list", new { });
            Ok(list);
            Check.Contains(list.Content, $"- {id} [open · high] Cache model list — Avoid refetching");
            Check.Contains(list.Content, "#perf #providers (2 sections)");
            Check.Contains(list.Content, "project \"Demo\"");
            Check.Contains((await env.Run("idea_list", new { tag = "old" })).Content, "Old thing");
            Check.Contains((await env.Run("idea_list", new { query = "invalidate models" })).Content, "Cache model list");
            Check.Contains((await env.Run("idea_list", new { query = "nomatch" })).Content, "No matching ideas");

            var get = await env.Run("idea_get", new { id });
            Ok(get);
            Check.Contains(get.Content, "# Cache model list\n`" + id + "` · status: open · priority: high · tags: perf, providers");
            var secId = idea["sections"]![0]!["id"].Str();
            Check.Contains(get.Content, $"## Research: Findings [{secId}]\nmodels.list takes 800ms");
            Check.Contains(get.Content, "## Plan [");

            var upd = await env.Run("idea_update", new JsonObject
            {
                ["id"] = id,
                ["status"] = "in progress",
                ["add_sections"] = "[{\"kind\":\"decision\",\"content\":\"Use IMemoryCache-free dictionary\"}]",
                ["updateSections"] = new JsonArray(new JsonObject { ["id"] = secId, ["content"] = "800ms measured twice." }),
                ["removeSectionIds"] = new JsonArray(idea["sections"]![1]!["id"].Str()),
            });
            Ok(upd);
            Check.Contains(upd.Content, "status (open → in-progress)");
            Check.Contains(upd.Content, "added 1 section");
            var u = Idea(upd);
            Check.Equal("in-progress", u["status"].Str());
            Check.Equal(2, ((JsonArray)u["sections"]!).Count);
            Check.Equal("800ms measured twice.", u["sections"]![0]!["content"].Str());
            Check.Equal("decision", u["sections"]![1]!["kind"].Str());

            var bad = await env.Run("idea_update", new { id, status = "maybe" });
            Check.True(bad.IsError);
            Check.Contains(bad.Content, "open, parked, planned, in-progress, done, rejected");
            Check.True((await env.Run("idea_get", new { id = "idea-nope00" })).IsError);
            Check.True((await env.Run("idea_add", new { summary = "no title" })).IsError);

            // done ideas are hidden by default
            await env.Run("idea_update", new { id, status = "done" });
            var active = await env.Run("idea_list", new { });
            Check.NotContains(active.Content, "Cache model list");
            Check.Contains(active.Content, "1 done/rejected hidden");
            Check.Contains((await env.Run("idea_list", new { status = "all" })).Content, "Cache model list");

            var rm = await env.Run("idea_remove", new { id });
            Ok(rm);
            Check.NotContains(env.Raw(), id);
            env.Ctx.Unload();
        });

        r.Add("ideas: RPC round trip for the UI tab", async () =>
        {
            var env = new Env();
            await env.StartAsync();
            var p = new JsonObject { ["sessionId"] = env.Session.Id };
            var empty = await env.Rpc("ideas.list", (JsonObject)p.DeepClone());
            Check.Equal("project", empty["scope"].Str());
            Check.Equal("Demo", empty["projectName"].Str());
            Check.Equal(env.Project.Id, empty["projectId"].Str());
            Check.Equal(Path.GetFullPath(env.File), empty["file"].Str());
            Check.Equal(false, (bool)empty["exists"]!);
            Check.Equal(0, ((JsonArray)empty["ideas"]!).Count);

            var a = await env.Rpc("ideas.add", new JsonObject
            {
                ["projectId"] = env.Project.Id,
                ["idea"] = new JsonObject { ["title"] = "First", ["tags"] = new JsonArray("ui"), ["color"] = "red",
                    ["sections"] = new JsonArray(new JsonObject { ["kind"] = "todo", ["content"] = "- [ ] a" }) },
            });
            Check.Equal("user", a["createdBy"].Str());
            Check.Equal("red", a["color"].Str()); // UI extra fields are kept
            var b = await env.Rpc("ideas.add", new JsonObject { ["sessionId"] = env.Session.Id, ["idea"] = new JsonObject { ["title"] = "Second" } });
            Check.Equal(env.Session.Id, b["sessionIds"]![0].Str());
            var quick = await env.Ctx.RpcFake.Call("ideas.quickAdd", new JsonObject { ["sessionId"] = env.Session.Id, ["args"] = "  Third idea " });
            Check.Contains((string?)quick, "Idea added (project Demo): Third idea (idea-");
            var usage = await Check.ThrowsAsync<RpcException>(() => env.Ctx.RpcFake.Call("ideas.quickAdd", new JsonObject { ["sessionId"] = env.Session.Id, ["args"] = "" }));
            Check.Contains(usage.Message, "Usage: /idea <title>");

            var aid = a["id"].Str();
            var bid = b["id"].Str();
            var got = await env.Rpc("ideas.get", new JsonObject { ["sessionId"] = env.Session.Id, ["id"] = aid });
            Check.Equal("First", got["title"].Str());

            var patched = await env.Rpc("ideas.update", new JsonObject
            {
                ["sessionId"] = env.Session.Id, ["id"] = aid,
                ["patch"] = new JsonObject
                {
                    ["title"] = "First (renamed)", ["priority"] = "low", ["color"] = null, ["estimate"] = "2d",
                    ["sections"] = new JsonArray(
                        new JsonObject { ["id"] = a["sections"]![0]!["id"].Str(), ["content"] = "- [x] a", ["collapsed"] = true },
                        new JsonObject { ["kind"] = "links", ["content"] = "https://example.com" }),
                },
            });
            Check.Equal("First (renamed)", patched["title"].Str());
            Check.Equal("low", patched["priority"].Str());
            Check.True(patched["color"] is null && !patched.ContainsKey("color"), "null removes a UI field");
            Check.Equal("2d", patched["estimate"].Str());
            Check.Equal(2, ((JsonArray)patched["sections"]!).Count);
            Check.Equal(a["sections"]![0]!["id"].Str(), patched["sections"]![0]!["id"].Str());
            Check.Equal("- [x] a", patched["sections"]![0]!["content"].Str());
            Check.Equal("todo", patched["sections"]![0]!["kind"].Str());
            Check.Equal(true, (bool)patched["sections"]![0]!["collapsed"]!);

            await env.Ctx.RpcFake.Call("ideas.reorder", new JsonObject { ["sessionId"] = env.Session.Id, ["ids"] = new JsonArray(bid) });
            var listed = await env.Rpc("ideas.list", new JsonObject { ["sessionId"] = env.Session.Id });
            Check.Equal(true, (bool)listed["exists"]!);
            var order = ((JsonArray)listed["ideas"]!).Select(i => i!["title"].Str()).ToList();
            Check.Equal("Second|First (renamed)|Third idea", string.Join("|", order));

            var prompt = (string?)await env.Ctx.RpcFake.Call("ideas.toPrompt", new JsonObject { ["sessionId"] = env.Session.Id, ["id"] = aid });
            Check.Contains(prompt, "Implement the following idea from the ideas backlog (`" + aid + "` in ideas.json)");
            Check.Contains(prompt, "# First (renamed)\nPriority: low · Tags: ui");
            Check.Contains(prompt, "## To do\n- [x] a");
            Check.Contains(prompt, "## Links\nhttps://example.com");

            Check.Equal(true, (bool)(await env.Ctx.RpcFake.Call("ideas.delete", new JsonObject { ["sessionId"] = env.Session.Id, ["id"] = aid }))!);
            var nf = await Check.ThrowsAsync<RpcException>(() => env.Ctx.RpcFake.Call("ideas.get", new JsonObject { ["sessionId"] = env.Session.Id, ["id"] = aid }));
            Check.Equal("not_found", nf.Code);
            var badSession = await Check.ThrowsAsync<RpcException>(() => env.Ctx.RpcFake.Call("ideas.list", new JsonObject { ["sessionId"] = "ses_missing" }));
            Check.Equal("not_found", badSession.Code);
            var badPatch = await Check.ThrowsAsync<RpcException>(() => env.Ctx.RpcFake.Call("ideas.update", new JsonObject
                { ["sessionId"] = env.Session.Id, ["id"] = bid, ["patch"] = new JsonObject { ["status"] = "??" } }));
            Check.Equal("bad_request", badPatch.Code);
            env.Ctx.Unload();
        });

        r.Add("ideas: sessions without a project use ~/.netpi/ideas.json; ideas.fileName", async () =>
        {
            var env = new Env();
            await env.StartAsync();
            Ok(await env.Run("idea_add", new { title = "Global one" }, withProject: false));
            var globalFile = Path.Combine(env.Ctx.Paths.Home, "ideas.json");
            Check.True(System.IO.File.Exists(globalFile));
            Check.False(System.IO.File.Exists(env.File));
            var listed = await env.Rpc("ideas.list", new JsonObject { ["sessionId"] = env.GlobalSession.Id });
            Check.Equal("global", listed["scope"].Str());
            Check.True(!listed.ContainsKey("projectName"));
            Check.Equal("Global one", listed["ideas"]![0]!["title"].Str());
            Check.Equal("global", (await env.Rpc("ideas.list", new JsonObject()))["scope"].Str());

            env.Ctx.SettingsFake.Set("ideas.fileName", "backlog.json");
            Ok(await env.Run("idea_add", new { title = "Named" }));
            Check.True(System.IO.File.Exists(Path.Combine(env.ProjectDir, "backlog.json")));
            env.Ctx.Unload();
        });

        r.Add("ideas: CRLF, BOM, indentation and unknown fields are preserved", async () =>
        {
            var env = new Env();
            var original =
                "{\r\n    \"version\": 1,\r\n    \"owner\": \"team-a\",\r\n    \"ideas\": [\r\n        {\r\n            \"id\": \"idea-abc123\",\r\n" +
                "            \"title\": \"Handwritten\",\r\n            \"status\": \"open\",\r\n            \"estimate\": { \"days\": 3 },\r\n" +
                "            \"sections\": [ { \"id\": \"sec-aa\", \"kind\": \"note\", \"content\": \"x\", \"author\": \"bob\" } ]\r\n        }\r\n    ],\r\n" +
                "    // comments are tolerated\r\n    \"extra\": [1, 2],\r\n}\r\n";
            System.IO.File.WriteAllBytes(env.File, [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(original)]);
            await env.StartAsync();

            Ok(await env.Run("idea_update", new { id = "idea-abc123", priority = "high", addSections = new[] { new { kind = "plan", content = "multi\nline" } } }));
            var bytes = System.IO.File.ReadAllBytes(env.File);
            Check.True(bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "BOM kept");
            var text = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
            Check.Contains(text, "\r\n");
            Check.Equal(0, text.Replace("\r\n", "").Count(c => c == '\n'));
            Check.True(text.StartsWith("{\r\n    \"version\": 1,\r\n    \"owner\": \"team-a\","), text[..60]);
            var root = JsonNode.Parse(text)!.AsObject();
            Check.Equal("team-a", root["owner"].Str());
            Check.Equal("[1,2]", root["extra"]!.ToJsonString());
            var idea = root["ideas"]![0]!.AsObject();
            Check.Equal("{\"days\":3}", idea["estimate"]!.ToJsonString());
            Check.Equal("bob", idea["sections"]![0]!["author"].Str());
            Check.Equal("high", idea["priority"].Str());
            Check.Equal("multi\nline", idea["sections"]![1]!["content"].Str());
            // The JSON string content keeps its own \n escapes; only the file's line breaks are CRLF.
            Check.Contains(text, "\"content\": \"multi\\nline\"");
            env.Ctx.Unload();
        });

        r.Add("ideas: invalid JSON is reported and never overwritten", async () =>
        {
            var env = new Env();
            System.IO.File.WriteAllText(env.File, "{ \"ideas\": [ { \"title\": ");
            await env.StartAsync();
            var res = await env.Run("idea_add", new { title = "x" });
            Check.True(res.IsError);
            Check.Contains(res.Content, "is not valid JSON");
            Check.Equal("{ \"ideas\": [ { \"title\": ", System.IO.File.ReadAllText(env.File));
            var ex = await Check.ThrowsAsync<RpcException>(() => env.Ctx.RpcFake.Call("ideas.list", new JsonObject { ["sessionId"] = env.Session.Id }));
            Check.Equal("invalid_file", ex.Code);
            env.Ctx.Unload();
        });

        r.Add("ideas: ideas.changed on own writes and on external edits (debounced)", async () =>
        {
            var env = new Env();
            await env.StartAsync();
            var bus = env.Ctx.Bus;
            Ok(await env.Run("idea_add", new { title = "A" }));
            Ok(await env.Run("idea_add", new { title = "B" }));
            var first = await bus.WaitForAsync(IdeasStore.ChangedEvent);
            Check.True(first is not null, "event after own write");
            Check.Equal(Path.GetFullPath(env.File), ((JsonObject)first!.Data!)["file"].Str());
            Check.True(first.SessionId is null, "broadcast");
            await Task.Delay(600);
            var afterOwn = bus.OfType(IdeasStore.ChangedEvent).Count;
            Check.True(afterOwn <= 2, $"debounced: {afterOwn} events for two quick writes");

            // External edit (another editor / git checkout).
            var json = JsonNode.Parse(System.IO.File.ReadAllText(env.File))!;
            ((JsonArray)json["ideas"]!).Add(new JsonObject { ["id"] = "idea-ext001", ["title"] = "External" });
            System.IO.File.WriteAllText(env.File, json.ToJsonString());
            var ext = await bus.WaitForAsync(IdeasStore.ChangedEvent, skip: afterOwn);
            Check.True(ext is not null, "event after external edit");
            var listed = await env.Rpc("ideas.list", new JsonObject { ["sessionId"] = env.Session.Id });
            Check.Contains(listed.ToJsonString(), "External");
            env.Ctx.Unload();
        });

        r.Add("ideas: concurrent adds are serialized per file", async () =>
        {
            var env = new Env();
            await env.StartAsync();
            var results = await Task.WhenAll(Enumerable.Range(0, 25).Select(i => Task.Run(() => env.Run("idea_add", new { title = "T" + i }))));
            Check.True(results.All(x => !x.IsError), string.Join("; ", results.Where(x => x.IsError).Select(x => x.Content)));
            var root = JsonNode.Parse(System.IO.File.ReadAllText(env.File))!;
            var ids = ((JsonArray)root["ideas"]!).Select(i => i!["id"].Str()).ToList();
            Check.Equal(25, ids.Count);
            Check.Equal(25, ids.Distinct().Count());
            env.Ctx.Unload();
        });

        r.Add("ideas: IdeaOps normalization helpers", () =>
        {
            Check.Equal("in-progress", IdeaOps.NormalizeStatus("In Progress"));
            Check.Equal("in-progress", IdeaOps.NormalizeStatus("wip"));
            Check.Equal("parked", IdeaOps.NormalizeStatus("deferred"));
            Check.Equal("done", IdeaOps.NormalizeStatus("implemented"));
            Check.Equal("rejected", IdeaOps.NormalizeStatus("won't do"));
            Check.Equal("high", IdeaOps.NormalizePriority("urgent"));
            Check.Equal("requirements", IdeaOps.NormalizeKind("spec"));
            Check.Equal("note", IdeaOps.NormalizeKind("whatever"));
            Check.Equal("a|b", string.Join("|", IdeaOps.ParseTags(JsonValue.Create("a, #b, A"))));
            Check.Equal("x|y", string.Join("|", IdeaOps.ParseTags(JsonValue.Create("[\"x\",\"y\"]"))));
            Check.Equal("\r\n", IdeasStore.DetectEol("{\r\n}\r\n"));
            Check.Equal("\n", IdeasStore.DetectEol("{}"));
            Check.Equal(('\t', 1), IdeasStore.DetectIndent("{\n\t\"a\": 1\n}"));
            Check.Equal((' ', 4), IdeasStore.DetectIndent("{\n    \"a\": 1\n}"));
        });
    }
}
