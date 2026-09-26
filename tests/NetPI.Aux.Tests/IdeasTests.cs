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
        public string Project2Dir { get; }
        public ProjectInfo Project2 { get; }
        public SessionInfo Session { get; }
        public SessionInfo Session2 { get; }
        public SessionInfo GlobalSession { get; }
        /// <summary>The single ideas file (the global one).</summary>
        public string File => Path.Combine(Ctx.Paths.Home, "ideas.json");

        public Env()
        {
            Ctx = new FakePluginContext(T.TempDir("ideas-home"));
            ProjectDir = T.TempDir("ideas-proj");
            Project = Ctx.SessionsFake.CreateProject("Demo", ProjectDir);
            Project2Dir = T.TempDir("ideas-proj2");
            Project2 = Ctx.SessionsFake.CreateProject("Other", Project2Dir);
            Session = Ctx.SessionsFake.CreateSession(new SessionInfo { Title = "s", ProjectId = Project.Id });
            Session2 = Ctx.SessionsFake.CreateSession(new SessionInfo { Title = "s2", ProjectId = Project2.Id });
            GlobalSession = Ctx.SessionsFake.CreateSession(new SessionInfo { Title = "g" });
        }

        public async Task StartAsync() => await new IdeasPlugin().StartAsync(Ctx, CancellationToken.None);

        public IAgentTool Tool(string name) => Ctx.ToolsFake.Get(name) ?? throw new AssertException("no tool " + name);

        public Task<ToolResult> Run(string tool, object args, bool withProject = true) =>
            Run(tool, args, withProject ? Session : GlobalSession, withProject ? Project : null);

        public Task<ToolResult> Run(string tool, object args, SessionInfo session, ProjectInfo? project) =>
            Tool(tool).ExecuteAsync(new ToolContext
            {
                SessionId = session.Id, AgentId = "agt_7", CallId = "call_1",
                Cwd = project?.Path ?? Path.GetTempPath(), Project = project, Services = Ctx.Services, Events = Ctx.Events,
            }, T.Args(args), CancellationToken.None);

        public async Task<JsonObject> Rpc(string method, JsonObject p) => (JsonObject)(await Ctx.RpcFake.Call(method, p))!;

        public string Raw() => Encoding.UTF8.GetString(System.IO.File.ReadAllBytes(File));
    }

    private static void Ok(ToolResult r) { if (r.IsError) throw new AssertException("tool error: " + r.Content); }

    private static JsonObject Idea(ToolResult r) => (JsonObject)((JsonObject)NetPiJson.ToNode(r.Details)!)["idea"]!;

    public static void Register(TestRunner r)
    {
        r.Add("ideas: plugin registers one tool, RPC, tab and /idea command", async () =>
        {
            var env = new Env();
            await env.StartAsync();
            Check.Equal("ideas", string.Join(",", env.Ctx.ToolsFake.Tools.Select(t => t.Definition.Name)));
            Check.Equal("ideas", env.Tool("ideas").Definition.Category);
            Check.Contains(env.Tool("ideas").Definition.Description, "single global file");
            var guideline = string.Join(" ", env.Tool("ideas").Definition.PromptGuidelines!);
            Check.Contains(guideline, "(ideas, action add)");
            Check.Contains(guideline, "When you finish the work an idea describes, set it to done");
            foreach (var m in new[] { "ideas.list", "ideas.get", "ideas.add", "ideas.update", "ideas.delete", "ideas.reorder", "ideas.toPrompt", "ideas.quickAdd" })
                Check.True(env.Ctx.RpcFake.Exists(m), m);
            var tab = env.Ctx.UiFake.TabList.Single();
            Check.True(tab is { Id: "ideas", Title: "Ideas", Panel: UiPanel.Right, Icon: "idea", Order: 20, Module: "ui.js" });
            var cmd = env.Ctx.UiFake.CommandList.Single();
            Check.True(cmd is { Name: "idea", ArgsHint: "<title>", Rpc: "ideas.quickAdd" });
            env.Ctx.Unload();
        });

        r.Add("ideas: the ideas tool round trip (stamps the session's project; deleting is the user's)", async () =>
        {
            var env = new Env();
            await env.StartAsync();
            var add = await env.Run("ideas", new JsonObject
            {
                ["action"] = "add",
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
            Check.Equal(env.Project.Id, idea["project"]!["id"].Str(), "the session's project is the default stamp");
            Check.Equal("Demo", idea["project"]!["name"].Str());
            Check.True(System.IO.File.Exists(env.File), "the global ideas file");
            var raw = env.Raw();
            Check.True(raw.StartsWith("{\n  \"version\": 1,\n  \"ideas\": [\n"), raw[..Math.Min(60, raw.Length)]);

            await env.Run("ideas", new { action = "create", title = "Old thing", tags = "misc, #old" });
            var list = await env.Run("ideas", new { action = "list" });
            Ok(list);
            Check.Contains(list.Content, $"- {id} [open · high] Cache model list — Avoid refetching");
            Check.Contains(list.Content, "#perf #providers (2 sections)");
            Check.Contains(list.Content, "project \"Demo\"");
            Check.Contains((await env.Run("ideas", new { action = "list", tag = "old" })).Content, "Old thing");
            Check.Contains((await env.Run("ideas", new { action = "search", query = "invalidate models" })).Content, "Cache model list");
            Check.Contains((await env.Run("ideas", new { action = "list", query = "nomatch" })).Content, "No matching ideas");
            Check.Contains((await env.Run("ideas", new { })).Content, "Cache model list", "without an action (and an id): list");

            var get = await env.Run("ideas", new { action = "get", id });
            Ok(get);
            Check.Contains(get.Content, $"# Cache model list\n`{id}` · status: open · priority: high · project: Demo · tags: perf, providers");
            var secId = idea["sections"]![0]!["id"].Str();
            Check.Contains(get.Content, $"## Research: Findings [{secId}]\nmodels.list takes 800ms");
            Check.Contains(get.Content, "## Plan [");

            Check.Contains((await env.Run("ideas", new { id })).Content, "# Cache model list", "an id alone: get");
            var upd = await env.Run("ideas", new JsonObject
            {
                ["action"] = "update",
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

            // reassigning the project (by name, then unbinding)
            var moved = await env.Run("ideas", new { action = "update", id, project = "Other" });
            Ok(moved);
            Check.Contains(moved.Content, "project (→ Other)");
            Check.Equal(env.Project2.Id, Idea(moved)["project"]!["id"].Str());
            var unbound = await env.Run("ideas", new { action = "update", id, project = "global" });
            Ok(unbound);
            Check.Contains(unbound.Content, "project (→ global)");
            Check.True(Idea(unbound)["project"] is null, "unbound");

            var bad = await env.Run("ideas", new { action = "update", id, status = "maybe" });
            Check.True(bad.IsError);
            Check.Contains(bad.Content, "open, parked, planned, in-progress, done, rejected");
            Check.True((await env.Run("ideas", new { action = "get", id = "idea-nope00" })).IsError);
            Check.True((await env.Run("ideas", new { action = "add", summary = "no title" })).IsError);
            Check.Contains((await env.Run("ideas", new { action = "frobnicate" })).Content, "Unknown action \"frobnicate\"");

            // closing an idea hides it from the list by default
            Ok(await env.Run("ideas", new { action = "close", id }));
            Check.Equal("done", Idea(await env.Run("ideas", new { action = "get", id }))["status"].Str(), "close: status done");
            var active = await env.Run("ideas", new { action = "list" });
            Check.NotContains(active.Content, "Cache model list");
            Check.Contains(active.Content, "1 done/rejected hidden");
            Check.Contains((await env.Run("ideas", new { action = "list", status = "all" })).Content, "Cache model list");

            // deleting is left to the user (the tab calls ideas.delete)
            var rm = await env.Run("ideas", new { action = "delete", id });
            Check.True(rm.IsError);
            Check.Contains(rm.Content, "up to the user, in the Ideas tab");
            Check.Contains(env.Raw(), id);
            env.Ctx.Unload();
        });

        r.Add("ideas: the project argument (default stamp, cross-project list/add, unknown projects)", async () =>
        {
            var env = new Env();
            await env.StartAsync();
            var demoId = Idea(await env.Run("ideas", new { action = "add", title = "In Demo" }))!["id"].Str();
            Ok(await env.Run("ideas", new { action = "add", title = "In Other" }, env.Session2, env.Project2));
            Ok(await env.Run("ideas", new { action = "add", title = "Unbound" }, withProject: false)); // projectless session

            // the default list: the session's project plus the unbound ones
            var list = await env.Run("ideas", new { action = "list" });
            Check.Contains(list.Content, "In Demo");
            Check.Contains(list.Content, "Unbound");
            Check.NotContains(list.Content, "In Other");
            Check.Contains(list.Content, "2 ideas in project \"Demo\" (1 global)");

            // the projectless session sees only the unbound ones
            var glist = await env.Run("ideas", new { action = "list" }, withProject: false);
            Check.Contains(glist.Content, "Unbound");
            Check.NotContains(glist.Content, "In Demo");

            // every project, with a project label per line
            var all = await env.Run("ideas", new { action = "list", project = "all" });
            Check.Contains(all.Content, "3 ideas in all projects");
            Check.Contains(all.Content, "[open · medium · Demo] In Demo");
            Check.Contains(all.Content, "[open · medium · Other] In Other");
            Check.Contains(all.Content, "[open · medium · global] Unbound");

            var other = await env.Run("ideas", new { action = "list", project = "Other" }); // by name
            Check.Contains(other.Content, "In Other");
            Check.NotContains(other.Content, "In Demo");
            var glob = await env.Run("ideas", new { action = "list", project = "global" });
            Check.Contains(glob.Content, "Unbound");
            Check.NotContains(glob.Content, "In Demo");

            // add: the session's project by default, overridable (even to another project)
            var cross = await env.Run("ideas", new { action = "add", title = "Cross", project = "Other" });
            Ok(cross);
            Check.Equal(env.Project2.Id, Idea(cross)["project"]!["id"].Str());
            Check.NotContains((await env.Run("ideas", new { action = "list" })).Content, "Cross", "stays out of Demo's default list");
            Check.Contains((await env.Run("ideas", new { action = "list", project = "Other" })).Content, "Cross");
            var toGlobal = await env.Run("ideas", new { action = "add", title = "To global", project = "global" });
            Ok(toGlobal);
            Check.False(Idea(toGlobal).ContainsKey("project") && Idea(toGlobal)["project"] is not null, "unbound");

            var unknown = await env.Run("ideas", new { action = "add", title = "X", project = "nope" });
            Check.True(unknown.IsError);
            Check.Contains(unknown.Content, "Unknown project 'nope'");
            Check.Contains(unknown.Content, "Demo");
            Check.Contains(unknown.Content, "Other");

            // update: reassign by id or name, unbind with "global"
            var re = await env.Run("ideas", new { action = "update", id = demoId, project = env.Project2.Id });
            Ok(re);
            Check.Equal(env.Project2.Id, Idea(re)["project"]!["id"].Str());
            var re2 = await env.Run("ideas", new { action = "update", id = demoId, project = "Demo" });
            Check.Equal(env.Project.Id, Idea(re2)["project"]!["id"].Str(), "name resolves to the project");
            var un = await env.Run("ideas", new { action = "update", id = demoId, project = "global" });
            Check.Contains(un.Content, "project (→ global)");
            Check.False(Idea(un).ContainsKey("project"), "unbound");
            env.Ctx.Unload();
        });

        r.Add("ideas: RPC round trip for the UI tab", async () =>
        {
            var env = new Env();
            await env.StartAsync();
            var empty = await env.Rpc("ideas.list", new JsonObject());
            Check.Equal(Path.GetFullPath(env.File), empty["file"].Str());
            Check.Equal("ideas.json", empty["fileName"].Str());
            Check.Equal(false, (bool)empty["exists"]!);
            Check.Equal(0, ((JsonArray)empty["ideas"]!).Count);

            var a = await env.Rpc("ideas.add", new JsonObject
            {
                ["projectId"] = env.Project.Id,
                ["idea"] = new JsonObject { ["title"] = "First", ["tags"] = new JsonArray("ui"), ["color"] = "red",
                    ["sections"] = new JsonArray(new JsonObject { ["kind"] = "todo", ["content"] = "- [ ] a" }) },
            });
            Check.Equal("user", a["createdBy"].Str());
            Check.Equal("red", a["color"].Str(), "UI extra fields are kept");
            Check.Equal(env.Project.Id, a["project"]!["id"].Str());
            Check.Equal("Demo", a["project"]!["name"].Str());
            var b = await env.Rpc("ideas.add", new JsonObject { ["sessionId"] = env.Session.Id, ["idea"] = new JsonObject { ["title"] = "Second" } });
            Check.Equal(env.Session.Id, b["sessionIds"]![0].Str());
            Check.Equal(env.Project.Id, b["project"]!["id"].Str(), "stamped with the session's project");
            var c = await env.Rpc("ideas.add", new JsonObject { ["idea"] = new JsonObject { ["title"] = "Third" } });
            Check.True(c["project"] is null || !c.ContainsKey("project"), "no session, no project → unbound");
            var badSession = await Check.ThrowsAsync<RpcException>(() => env.Ctx.RpcFake.Call("ideas.add", new JsonObject
                { ["sessionId"] = "ses_missing", ["idea"] = new JsonObject { ["title"] = "x" } }));
            Check.Equal("not_found", badSession.Code);

            var quick = await env.Ctx.RpcFake.Call("ideas.quickAdd", new JsonObject { ["sessionId"] = env.Session.Id, ["args"] = "  Fourth idea " });
            Check.Contains((string?)quick, "Idea added (project Demo): Fourth idea (idea-");
            var usage = await Check.ThrowsAsync<RpcException>(() => env.Ctx.RpcFake.Call("ideas.quickAdd", new JsonObject { ["sessionId"] = env.Session.Id, ["args"] = "" }));
            Check.Contains(usage.Message, "Usage: /idea <title>");

            var aid = a["id"].Str();
            var bid = b["id"].Str();
            var got = await env.Rpc("ideas.get", new JsonObject { ["id"] = aid });
            Check.Equal("First", got["title"].Str());

            var patched = await env.Rpc("ideas.update", new JsonObject
            {
                ["id"] = aid,
                ["patch"] = new JsonObject
                {
                    ["title"] = "First (renamed)", ["priority"] = "low", ["color"] = null, ["estimate"] = "2d",
                    ["project"] = env.Project2.Id, // a bare id in the patch is resolved
                    ["sections"] = new JsonArray(
                        new JsonObject { ["id"] = a["sections"]![0]!["id"].Str(), ["content"] = "- [x] a", ["collapsed"] = true },
                        new JsonObject { ["kind"] = "links", ["content"] = "https://example.com" }),
                },
            });
            Check.Equal("First (renamed)", patched["title"].Str());
            Check.Equal("low", patched["priority"].Str());
            Check.True(patched["color"] is null && !patched.ContainsKey("color"), "null removes a UI field");
            Check.Equal("2d", patched["estimate"].Str());
            Check.Equal(env.Project2.Id, patched["project"]!["id"].Str());
            Check.Equal("Other", patched["project"]!["name"].Str());
            Check.Equal(2, ((JsonArray)patched["sections"]!).Count);
            Check.Equal(a["sections"]![0]!["id"].Str(), patched["sections"]![0]!["id"].Str());
            Check.Equal("- [x] a", patched["sections"]![0]!["content"].Str());
            Check.Equal("todo", patched["sections"]![0]!["kind"].Str());
            Check.Equal(true, (bool)patched["sections"]![0]!["collapsed"]!);

            await env.Ctx.RpcFake.Call("ideas.reorder", new JsonObject { ["ids"] = new JsonArray(bid) });
            var listed = await env.Rpc("ideas.list", new JsonObject());
            Check.Equal(true, (bool)listed["exists"]!);
            var order = ((JsonArray)listed["ideas"]!).Select(i => i!["title"].Str()).ToList();
            Check.Equal("Second|First (renamed)|Third|Fourth idea", string.Join("|", order));

            var prompt = (string?)await env.Ctx.RpcFake.Call("ideas.toPrompt", new JsonObject { ["id"] = aid });
            Check.Contains(prompt, "Implement the following idea from the ideas backlog (`" + aid + "` in");
            Check.Contains(prompt, "Keep the idea up to date with the ideas tool (action update)");
            Check.Contains(prompt, "# First (renamed)\nPriority: low · Project: Other · Tags: ui");
            Check.Contains(prompt, "## To do\n- [x] a");
            Check.Contains(prompt, "## Links\nhttps://example.com");

            Check.Equal(true, (bool)(await env.Ctx.RpcFake.Call("ideas.delete", new JsonObject { ["id"] = aid }))!);
            var nf = await Check.ThrowsAsync<RpcException>(() => env.Ctx.RpcFake.Call("ideas.get", new JsonObject { ["id"] = aid }));
            Check.Equal("not_found", nf.Code);
            var badPatch = await Check.ThrowsAsync<RpcException>(() => env.Ctx.RpcFake.Call("ideas.update", new JsonObject
                { ["id"] = bid, ["patch"] = new JsonObject { ["status"] = "??" } }));
            Check.Equal("bad_request", badPatch.Code);
            env.Ctx.Unload();
        });

        r.Add("ideas: one global file for all (project and projectless); ideas.fileName", async () =>
        {
            var env = new Env();
            await env.StartAsync();
            Ok(await env.Run("ideas", new { action = "add", title = "Global one" }, withProject: false));
            Ok(await env.Run("ideas", new { action = "add", title = "Project one" }));
            Check.True(System.IO.File.Exists(env.File), "the global file");
            Check.False(System.IO.File.Exists(Path.Combine(env.ProjectDir, ".netpi", "ideas.json")), "no per-project file");
            var listed = await env.Rpc("ideas.list", new JsonObject());
            var titles = ((JsonArray)listed["ideas"]!).Select(i => i!["title"].Str()).ToList();
            Check.True(titles.Contains("Global one"));
            Check.True(titles.Contains("Project one"));
            var g = ((JsonArray)listed["ideas"]!).First(i => i!["title"].Str() == "Global one")!.AsObject();
            Check.True(g["project"] is null, "the projectless session's idea is unbound");
            var p = ((JsonArray)listed["ideas"]!).First(i => i!["title"].Str() == "Project one")!.AsObject();
            Check.Equal(env.Project.Id, p["project"]!["id"].Str());

            env.Ctx.SettingsFake.Set("ideas.fileName", "backlog.json");
            Ok(await env.Run("ideas", new { action = "add", title = "Named" }));
            Check.True(System.IO.File.Exists(Path.Combine(env.Ctx.Paths.Home, "backlog.json")), "the setting names the global file");
            env.Ctx.Unload();
        });

        r.Add("ideas: per-project and legacy root files are merged into the global one and deleted", async () =>
        {
            var env = new Env();
            var dotNetPi = Path.Combine(env.ProjectDir, ".netpi");
            Directory.CreateDirectory(dotNetPi);
            System.IO.File.WriteAllText(Path.Combine(dotNetPi, "ideas.json"),
                "{\n  \"version\": 1,\n  \"ideas\": [\n    { \"id\": \"idea-mig001\", \"title\": \"From .netpi\" },\n" +
                "    { \"id\": \"idea-take01\", \"title\": \"Collides\" }\n  ]\n}\n");
            System.IO.File.WriteAllText(Path.Combine(env.ProjectDir, "ideas.json"), // the legacy place
                "{ \"ideas\": [ { \"id\": \"idea-root01\", \"title\": \"From the root\" } ] }\n");
            System.IO.File.WriteAllText(env.File,
                "{ \"version\": 1, \"ideas\": [ { \"id\": \"idea-take01\", \"title\": \"Stays\" } ] }\n");

            await env.StartAsync(); // migrates
            var listed = await env.Rpc("ideas.list", new JsonObject());
            var ideas = ((JsonArray)listed["ideas"]!).Select(i => i!.AsObject()).ToList();
            Check.Equal(4, ideas.Count);
            Check.Equal("Stays", ideas.Single(i => i["id"].Str() == "idea-take01")["title"].Str(), "the global one keeps its id");
            var colliding = ideas.Single(i => i!["title"].Str() == "Collides");
            Check.True(colliding["id"]!.Str() != "idea-take01", "the colliding one was renamed");
            var fromDotNetPi = ideas.Single(i => i!["title"].Str() == "From .netpi");
            Check.Equal(env.Project.Id, fromDotNetPi["project"]!["id"].Str(), "stamped");
            Check.Equal("Demo", fromDotNetPi["project"]!["name"].Str());
            Check.Equal(env.Project.Id, ideas.Single(i => i!["title"].Str() == "From the root")["project"]!["id"].Str());
            Check.True(colliding.ContainsKey("project"), "the renamed one is stamped too");
            Check.False(System.IO.File.Exists(Path.Combine(dotNetPi, "ideas.json")), "source deleted");
            Check.False(System.IO.File.Exists(Path.Combine(env.ProjectDir, "ideas.json")), "legacy source deleted");

            // a restart migrates nothing (the sources are gone)
            env.Ctx.Unload();
            await env.StartAsync();
            Check.Equal(4, ((JsonArray)(await env.Rpc("ideas.list", new JsonObject()))["ideas"]!).Count);
            env.Ctx.Unload();
        });

        r.Add("ideas: a broken global file blocks the migration until it is fixed (then it is retried)", async () =>
        {
            var env = new Env();
            var dotNetPi = Path.Combine(env.ProjectDir, ".netpi");
            Directory.CreateDirectory(dotNetPi);
            System.IO.File.WriteAllText(Path.Combine(dotNetPi, "ideas.json"),
                "{ \"version\": 1, \"ideas\": [ { \"id\": \"idea-wait01\", \"title\": \"Waiting\" } ] }\n");
            System.IO.File.WriteAllText(env.File, "{ \"ideas\": [ { \"title\": ");

            await env.StartAsync();
            Check.True(System.IO.File.Exists(Path.Combine(dotNetPi, "ideas.json")), "the source stays");
            Check.Contains(env.Ctx.Log.Lines.Where(l => l.Contains("migration")).FirstOrDefault() ?? "", "skipped", "logged");

            var add = await env.Run("ideas", new { action = "add", title = "x" });
            Check.True(add.IsError);
            Check.Contains(add.Content, "is not valid JSON");
            Check.Equal("{ \"ideas\": [ { \"title\": ", System.IO.File.ReadAllText(env.File), "never overwritten");

            // fix the file (a user edits it), restart → the migration runs
            System.IO.File.Delete(env.File);
            env.Ctx.Unload();
            await env.StartAsync();
            var listed = await env.Rpc("ideas.list", new JsonObject());
            Check.Contains(listed.ToJsonString(), "Waiting");
            Check.False(System.IO.File.Exists(Path.Combine(dotNetPi, "ideas.json")));
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

            Ok(await env.Run("ideas", new { action = "update", id = "idea-abc123", priority = "high", addSections = new[] { new { kind = "plan", content = "multi\nline" } } }));
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
            var res = await env.Run("ideas", new { action = "add", title = "x" });
            Check.True(res.IsError);
            Check.Contains(res.Content, "is not valid JSON");
            Check.Equal("{ \"ideas\": [ { \"title\": ", System.IO.File.ReadAllText(env.File));
            var ex = await Check.ThrowsAsync<RpcException>(() => env.Ctx.RpcFake.Call("ideas.list", new JsonObject()));
            Check.Equal("invalid_file", ex.Code);
            env.Ctx.Unload();
        });

        r.Add("ideas: ideas.changed on own writes and on external edits (debounced)", async () =>
        {
            var env = new Env();
            await env.StartAsync();
            var bus = env.Ctx.Bus;
            Ok(await env.Run("ideas", new { action = "add", title = "A" }));
            Ok(await env.Run("ideas", new { action = "add", title = "B" }));
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
            var listed = await env.Rpc("ideas.list", new JsonObject());
            Check.Contains(listed.ToJsonString(), "External");
            env.Ctx.Unload();
        });

        r.Add("ideas: concurrent adds are serialized per file", async () =>
        {
            var env = new Env();
            await env.StartAsync();
            var results = await Task.WhenAll(Enumerable.Range(0, 25).Select(i => Task.Run(() => env.Run("ideas", new { action = "add", title = "T" + i }))));
            Check.True(results.All(x => !x.IsError), string.Join("; ", results.Where(x => x.IsError).Select(x => x.Content)));
            var root = JsonNode.Parse(System.IO.File.ReadAllText(env.File))!;
            var ids = ((JsonArray)root["ideas"]!).Select(i => i!["id"].Str()).ToList();
            Check.Equal(25, ids.Count);
            Check.Equal(25, ids.Distinct().Count());
            env.Ctx.Unload();
        });

        r.Add("ideas: IdeaOps project helpers; the action from the arguments", () =>
        {
            var idea = new JsonObject();
            Check.True(IdeaOps.ProjectOf(idea) is null, "no project by default");
            IdeaOps.SetProject(idea, "proj_x", "Demo");
            Check.Equal(("proj_x", "Demo"), IdeaOps.ProjectOf(idea));
            Check.Equal("Demo", IdeaOps.ProjectLabel(idea));
            IdeaOps.SetProject(idea, null);
            Check.True(IdeaOps.ProjectOf(idea) is null, "unbound");
            var bound = new JsonObject { ["id"] = "idea-x", ["title"] = "t", ["project"] = new JsonObject { ["id"] = "p1", ["name"] = "P" } };
            IdeaOps.ApplyPatch(bound, (JsonObject)JsonNode.Parse("{\"project\": null}")!, fromUi: true);
            Check.True(IdeaOps.ProjectOf(bound) is null, "a JSON null patch unbinds");
            Check.True(IdeaOps.MatchesProject(new JsonObject { ["project"] = new JsonObject { ["id"] = "p1" } }, "p1", false));
            Check.True(!IdeaOps.MatchesProject(new JsonObject { ["project"] = new JsonObject { ["id"] = "p2" } }, "p1", false));
            Check.True(IdeaOps.MatchesProject(new JsonObject(), null, true), "unbound passes includeUnbound");
            Check.True(!IdeaOps.MatchesProject(new JsonObject { ["project"] = new JsonObject { ["id"] = "p1" } }, "p1", false, unboundOnly: true));

            var line = IdeaOps.ListLine(new JsonObject
            {
                ["id"] = "idea-a", ["status"] = "open", ["priority"] = "medium", ["title"] = "T",
                ["project"] = new JsonObject { ["id"] = "p1", ["name"] = "Demo" },
            }, "Demo");
            Check.Contains(line, "[open · medium · Demo] T");

            string A(string json) => IdeasTool.Action((JsonObject)JsonNode.Parse(json)!);
            Check.Equal("list", A("{}"));
            Check.Equal("add", A("{\"title\":\"x\"}"));
            Check.Equal("get", A("{\"id\":\"idea-1\"}"));
            Check.Equal("update", A("{\"id\":\"idea-1\",\"status\":\"done\"}"));
            Check.Equal("update", A("{\"id\":\"idea-1\",\"project\":\"global\"}"));
            Check.Equal("update", A("{\"action\":\"Edit\",\"id\":\"idea-1\"}"));
            Check.Equal("delete", A("{\"action\":\"remove\"}"));
            var close = (JsonObject)JsonNode.Parse("{\"action\":\"done\",\"id\":\"idea-1\"}")!;
            Check.Equal("update", IdeasTool.Action(close));
            Check.Equal("done", close["status"].Str());
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
