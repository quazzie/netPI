using System.Text.Json;
using System.Text.Json.Nodes;
using NetPI.Ideas;

namespace NetPI.Aux.Tests;

public static class IdeasTests
{
    private sealed class StubTool(string name, bool readOnly) : IAgentTool
    {
        public ToolDefinition Definition { get; } = new() { Name = name, Description = name, ReadOnly = readOnly };
        public Task<ToolResult> ExecuteAsync(ToolContext context, JsonElement args, CancellationToken ct) => Task.FromResult(ToolResult.Ok("ok"));
    }

    /// <summary>An agent runtime that only records what it was asked to run (or refuses).</summary>
    private sealed class RecordingRuntime : IAgentRuntime
    {
        public List<(string SessionId, UserInput Input)> Sent { get; } = [];
        public bool Fail { get; init; }
        public IReadOnlyList<AgentInfo> List(bool includeFinished = true) => [];
        public AgentInfo? Get(string agentId) => null;
        public AgentInfo? GetBySession(string sessionId) => null;
        public Task<AgentInfo> SendAsync(string sessionId, UserInput input, DeliveryMode mode = DeliveryMode.Auto, CancellationToken ct = default)
        {
            if (Fail) throw new InvalidOperationException("the runtime is stopping");
            Sent.Add((sessionId, input));
            return Task.FromResult(new AgentInfo { Id = "agt_t", SessionId = sessionId, Status = AgentStatus.Running });
        }
        public Task<AgentInfo> SpawnAsync(SpawnRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<AgentInfo>> WaitAsync(string? callerAgentId, IReadOnlyList<string> agentIds, bool yieldSlot = true, TimeSpan? timeout = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> AbortAsync(string agentOrSessionId) => Task.FromResult(false);
        public Task<bool> MessageAsync(string fromAgentId, string toAgentId, string text, DeliveryMode mode = DeliveryMode.Auto, CancellationToken ct = default) => Task.FromResult(false);
        public IReadOnlyList<QueuedInput> GetQueue(string sessionId) => [];
        public bool RemoveQueued(string sessionId, string inputId) => false;
    }

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
        public Env()
        {
            Ctx = new FakePluginContext(T.TempDir("ideas-home"));
            Ctx.ModelsFake.VerifierResponder = _ => new ChatMessage { Role = MessageRole.Assistant, Parts = [new TextPart { Text = "{\"verified\":true,\"confidence\":0.95,\"reason\":\"The scripted evidence supports this proposal\"}" }] };
            Ctx.SettingsFake.Set("ideas.applyVerifiedUpdates", false); // These cases exercise verified review cards.
            // The checks decide on a model that has to exist, and the commit sweep and the recall both go through
            // admission before they ask, so the catalog needs one.
            Ctx.ModelsFake.Models.Add(new ModelInfo { Provider = "aiproxy", Id = "qwen3.8-27b", IsLocal = true, MaxOutputTokens = 16384 });
            ProjectDir = T.TempDir("ideas-proj");
            Project = Ctx.SessionsFake.CreateProject("Demo", ProjectDir);
            Project2Dir = T.TempDir("ideas-proj2");
            Project2 = Ctx.SessionsFake.CreateProject("Other", Project2Dir);
            Session = Ctx.SessionsFake.CreateSession(new SessionInfo { Title = "s", ProjectId = Project.Id });
            Session2 = Ctx.SessionsFake.CreateSession(new SessionInfo { Title = "s2", ProjectId = Project2.Id });
            GlobalSession = Ctx.SessionsFake.CreateSession(new SessionInfo { Title = "g" });
        }

        public async Task StartAsync() => await new IdeasPlugin().StartAsync(Ctx, CancellationToken.None);

        /// <summary>The name the JSON backlog had: nothing reads that file any more, it is reported as a hint.</summary>
        public string File => Path.Combine(Ctx.Paths.Home, "ideas.json");

        /// <summary>The backlog as the UI reads it: collections in the store, not a file.</summary>
        public async Task<List<JsonObject>> Ideas() =>
            ((JsonArray)(await Rpc("ideas.list", new JsonObject()))["ideas"]!).OfType<JsonObject>().ToList();

        public async Task<List<string>> Titles() => (await Ideas()).Select(i => i["title"].Str()).ToList();

        /// <summary>The repository itself, for a test that writes where the plugin writes.</summary>
        public IdeasRepository Repo => IdeasRepository.Open(Ctx.Data, Ctx.Access, Ctx.Log, Ctx.Paths.Home);

        public IAgentTool Tool(string name) => Ctx.ToolsFake.Get(name) ?? throw new AssertException("no tool " + name);

        public Task<ToolResult> Run(string tool, object args, bool withProject = true) =>
            Run(tool, args, withProject ? Session : GlobalSession, withProject ? Project : null);

        public Task<ToolResult> Run(string tool, object args, SessionInfo session, ProjectInfo? project) =>
            Tool(tool).ExecuteAsync(new ToolContext
            {
                SessionId = session.Id, AgentId = "agt_7", CallId = "call_1",
                Cwd = project?.Path ?? Path.GetTempPath(), Project = project, Services = Ctx.Services, Events = Ctx.Events,
            }, T.Args(args), CancellationToken.None);

        public async Task<JsonObject> Rpc(string method, JsonObject p) => (JsonObject)NetPiJson.ToNode(await Ctx.RpcFake.Call(method, p))!;
    }

    private static void Ok(ToolResult r) { if (r.IsError) throw new AssertException("tool error: " + r.Content); }

    private static JsonObject Idea(ToolResult r) => (JsonObject)((JsonObject)NetPiJson.ToNode(r.Details)!)["idea"]!;

    /// <summary>What the commit check's fake `files.commits` answers with: the real method's own types.</summary>
    private sealed record CommitsResult(string Repo, string GitDir, string CommonDir, IReadOnlyList<GitCommit> Commits, bool Reachable);

    private sealed record GitCommit(string Hash, string Short, string Subject, string Author, string At);

    public static void Register(TestRunner r)
    {
        r.Add("ideas.refine: a chat on the idea's project with the idea attached, an agent chosen, the writing tools off and the task sent", async () =>
        {
            var env = new Env();
            var runtime = new RecordingRuntime();
            env.Ctx.ServicesFake.Register<IAgentRuntime>(runtime);
            var used = new List<JsonObject>();
            env.Ctx.RpcFake.Register("agents.use", (req, _) =>
            {
                used.Add(new JsonObject { ["sessionId"] = req.Str("sessionId"), ["agent"] = req.Str("agent") });
                return Task.FromResult<object?>(true);
            }, "test");
            foreach (var (name, readOnly) in new[] { ("read", true), ("grep", true), ("write", false), ("bash", false), ("agent_spawn", false), ("ask_user", false), ("todo_write", false) })
                env.Ctx.ToolsFake.Register(new StubTool(name, readOnly));
            await env.StartAsync();   // registers the ideas tool, which writes and has to stay on
            var idea = await env.Rpc("ideas.add", new JsonObject { ["idea"] = new JsonObject { ["title"] = "Make the thing faster", ["summary"] = "it is slow" }, ["projectId"] = env.Project.Id });
            var id = idea["id"]!.GetValue<string>();

            var started = await env.Rpc("ideas.refine", new JsonObject { ["id"] = id, ["hint"] = "look at the cache first" });
            var sessionId = started["sessionId"].Str();
            var session = env.Ctx.SessionsFake.GetSession(sessionId)!;
            Check.Equal("Refine idea: Make the thing faster", session.Title);
            Check.Equal(env.Project.Id, session.ProjectId, "the chat is on the idea's project");
            var off = ((JsonArray)session.Meta![SessionTools.MetaKey]!).Select(n => n!.GetValue<string>()).ToList();
            Check.Equal("agent_spawn,bash,write", string.Join(",", off.Order(StringComparer.Ordinal)), "what can change something is off");
            Check.True(!off.Contains("ideas") && !off.Contains("ask_user") && !off.Contains("todo_write") && !off.Contains("read") && !off.Contains("grep"), "the ideas tool, the questions and the reading tools stay on");
            Check.Equal("any", used.Single()["agent"].Str(), "any available agent by default");
            Check.Equal(sessionId, used.Single()["sessionId"].Str());

            var sent = runtime.Sent.Single();
            Check.Equal(sessionId, sent.SessionId);
            Check.Contains(sent.Input.Text, $"`{id}`");
            Check.Contains(sent.Input.Text, "look at the cache first", "the owner's hint reaches the agent");
            Check.Contains(sent.Input.Text, "Do not change any files");
            Check.True(env.Ctx.SessionsFake.GetMessages(sessionId).Any(m => m.Meta?["ideaId"]?.GetValue<string>() == id), "the idea is in the chat as a notice");
            Check.Contains(env.Repo.Find(id)!.Doc["sessionIds"]?.ToJsonString() ?? "", sessionId, "and the chat is recorded on the idea");
            Check.True((env.Repo.Find(id)!.Doc["sessions"] as JsonArray)?.Any(s => s?["sessionId"].Str() == sessionId) == true, "and listed on its card under Chats");

            // another agent by name
            await env.Rpc("ideas.refine", new JsonObject { ["id"] = id, ["agent"] = "qwen" });
            Check.Equal("qwen", used[^1]["agent"].Str());

            // an idea that is not there, and a runtime that is not there
            try { await env.Rpc("ideas.refine", new JsonObject { ["id"] = "idea-nope00" }); throw new AssertException("expected not_found"); }
            catch (RpcException ex) { Check.Equal("not_found", ex.Code); }
        });

        r.Add("ideas.refine: a task that cannot start leaves no chat behind, and no runtime is an error", async () =>
        {
            var env = new Env();
            await env.StartAsync();
            var idea = await env.Rpc("ideas.add", new JsonObject { ["idea"] = new JsonObject { ["title"] = "No runtime yet" } });
            try { await env.Rpc("ideas.refine", new JsonObject { ["id"] = idea["id"].Str() }); throw new AssertException("expected unavailable"); }
            catch (RpcException ex) { Check.Equal("unavailable", ex.Code); }

            var runtime = new RecordingRuntime { Fail = true };
            env.Ctx.ServicesFake.Register<IAgentRuntime>(runtime);
            var before = env.Ctx.SessionsFake.ListSessions(new SessionQuery { Limit = 100 }).Count;
            try { await env.Rpc("ideas.refine", new JsonObject { ["id"] = idea["id"].Str() }); throw new AssertException("expected the send to fail"); }
            catch (InvalidOperationException) { }
            Check.Equal(before, env.Ctx.SessionsFake.ListSessions(new SessionQuery { Limit = 100 }).Count, "the chat it created is gone again");
        });

        r.Add("ideas: plugin registers one tool, RPC, tab and /idea command", async () =>
        {
            var env = new Env();
            await env.StartAsync();
            Check.Equal("ideas", string.Join(",", env.Ctx.ToolsFake.Tools.Select(t => t.Definition.Name)));
            Check.Equal("ideas", env.Tool("ideas").Definition.Category);
            Check.Contains(env.Tool("ideas").Definition.Help!, "kept in NetPI's own database");
            var guideline = string.Join(" ", env.Tool("ideas").Definition.PromptGuidelines!);
            Check.Contains(guideline, "(ideas, action add)");
            Check.Contains(guideline, "When you finish the work an idea describes, set it to done");
            foreach (var m in new[] { "ideas.list", "ideas.get", "ideas.add", "ideas.update", "ideas.delete", "ideas.reorder", "ideas.toPrompt", "ideas.quickAdd", "ideas.addImage", "ideas.removeImage", "ideas.image" })
                Check.True(env.Ctx.RpcFake.Exists(m), m);
            var tab = env.Ctx.UiFake.TabList.Single();
            Check.True(tab is { Id: "ideas", Title: "Ideas", Panel: UiPanel.Right, Icon: "idea", Order: 20, Module: "ui.js" });
            var cmd = env.Ctx.UiFake.CommandList.Single();
            Check.True(cmd is { Name: "idea", ArgsHint: "<title>", Rpc: "ideas.quickAdd" });
            env.Ctx.Unload();
        });

        r.Add("ideas: the pure reads are registered read-only, so a tool can call them without --write", async () =>
        {
            var env = new Env();
            await env.StartAsync();
            var flags = env.Ctx.RpcFake.List().ToDictionary(m => m.Method, m => m.ReadOnly);
            foreach (var m in new[] { "ideas.capabilities", "ideas.list", "ideas.picks", "ideas.get", "ideas.toPrompt", "ideas.image" })
            {
                Check.True(flags.TryGetValue(m, out var readOnly), $"{m} is registered");
                Check.True(readOnly, $"{m} only reads, so it is marked read-only");
            }
            // The other side of the claim: what writes stays unmarked, so nothing reaches it by accident.
            foreach (var m in new[] { "ideas.add", "ideas.update", "ideas.delete", "ideas.reorder", "ideas.addImage", "ideas.removeImage", "ideas.import", "ideas.quickAdd" })
                Check.False(flags.GetValueOrDefault(m), $"{m} changes something, so it stays writable");
            env.Ctx.Unload();
        });

        r.Add("ideas: ideas.picks offers the target project's open ideas, then the global ones, and nothing being worked on", async () =>
        {
            var env = new Env();
            await env.StartAsync();
            async Task<JsonObject> Add(string project, JsonObject idea) => await env.Rpc("ideas.add", new JsonObject { ["projectId"] = project, ["idea"] = idea });

            // Demo: one high open (added first, so the priority decides it is still first), one low open, one planned,
            // one done and one in-progress — the last two are somebody's work in progress, not something to offer.
            var high = await Add(env.Project.Id, new JsonObject { ["title"] = "Demo high open", ["priority"] = "high" });
            await Add(env.Project.Id, new JsonObject { ["title"] = "Demo low open", ["priority"] = "low" });
            await Add(env.Project.Id, new JsonObject { ["title"] = "Demo planned", ["status"] = "planned" });
            await Add(env.Project.Id, new JsonObject { ["title"] = "Demo done", ["status"] = "done" });
            await Add(env.Project.Id, new JsonObject { ["title"] = "Demo running", ["status"] = "in-progress", ["priority"] = "high" });
            await Add(env.Project2.Id, new JsonObject { ["title"] = "Other open", ["priority"] = "high" });
            await Add("global", new JsonObject { ["title"] = "Global open" });

            var picks = (JsonArray)(await env.Rpc("ideas.picks", new JsonObject { ["projectId"] = env.Project.Id }))["picks"]!;
            var titles = picks.Select(p => p!["title"].Str()).ToList();
            Check.Equal("Demo high open, Demo low open, Demo planned, Global open", string.Join(", ", titles));
            Check.True(titles.Contains("Demo high open") && titles.IndexOf("Demo high open") < titles.IndexOf("Demo low open"),
                "priority before recency, so a high idea older than a low one still leads");
            Check.False(titles.Any(t => t.StartsWith("Demo done") || t.StartsWith("Demo running")), "never what is done or in progress");

            // Lean on purpose: the welcome screen reads this on every window start.
            var first = (JsonObject)picks[0]!;
            Check.Equal(high["id"].Str(), first["id"].Str());
            Check.Equal("Demo", first["projectName"].Str());
            Check.Equal(env.Project.Id, first["projectId"].Str());
            Check.Equal("high", first["priority"].Str());
            Check.True(first["updatedAt"].Str() is { Length: > 0 }, "updatedAt, so the row can say how old it is");
            foreach (var heavy in new[] { "sections", "images", "commits", "sessions", "tags" })
                Check.False(first.ContainsKey(heavy), $"a pick carries no {heavy}");

            // Another project sees its own ideas first; an unknown one only the global ones.
            var other = (JsonArray)(await env.Rpc("ideas.picks", new JsonObject { ["projectId"] = env.Project2.Id }))["picks"]!;
            Check.Equal("Other open, Global open", string.Join(", ", other.Select(p => p!["title"].Str())));
            var unknown = (JsonArray)(await env.Rpc("ideas.picks", new JsonObject { ["projectId"] = "prj_nope00" }))["picks"]!;
            Check.Equal("Global open", string.Join(", ", unknown.Select(p => p!["title"].Str())));
            var none = (JsonArray)(await env.Rpc("ideas.picks", new JsonObject()))["picks"]!;
            Check.Equal("Global open", string.Join(", ", none.Select(p => p!["title"].Str())),
                "with no target project only the global ones are relevant");

            // The limit is the caller's, clamped to something a screen can show.
            Check.Equal(1, ((JsonArray)(await env.Rpc("ideas.picks", new JsonObject { ["projectId"] = env.Project.Id, ["limit"] = 1 }))["picks"]!).Count);
            Check.Equal(1, ((JsonArray)(await env.Rpc("ideas.picks", new JsonObject { ["projectId"] = env.Project.Id, ["limit"] = 0 }))["picks"]!).Count);
            Check.Equal(4, ((JsonArray)(await env.Rpc("ideas.picks", new JsonObject { ["projectId"] = env.Project.Id, ["limit"] = "99" }))["picks"]!).Count,
                "there are only four to show");
            env.Ctx.Unload();
        });

        r.Add("ideas: images attach to an idea, come back for the card, and go when it does", async () =>
        {
            var env = new Env();
            await env.StartAsync();
            var png = Convert.ToBase64String(new byte[] { 0x89, 0x50, 0x4e, 0x47, 1, 2, 3, 4 });   // the bytes do not have to be a real image

            // 1. stored under the home, referenced home-relative, and reported back with the same bytes
            var shot = await env.Rpc("ideas.addImage", new JsonObject { ["data"] = png, ["mediaType"] = "image/png", ["name"] = "shot.png" });
            Check.Equal("shot.png", shot["name"].Str());
            Check.True(shot["path"].Str()!.StartsWith(IdeaImages.Dir + "/"), shot["path"].Str());
            var file = Path.Combine(env.Ctx.Paths.Home, shot["path"].Str()!.Replace('/', Path.DirectorySeparatorChar));
            Check.True(File.Exists(file), file);
            Check.Equal(png, Convert.ToBase64String(File.ReadAllBytes(file)));
            var back = await env.Rpc("ideas.image", new JsonObject { ["path"] = shot["path"].Str() });
            Check.Equal(png, back["data"].Str());

            // 2. the idea carries the reference, and the prompt hands the path to whoever works on it
            var added = await env.Rpc("ideas.add", new JsonObject
            {
                ["idea"] = new JsonObject { ["title"] = "Composer is cramped", ["images"] = new JsonArray { shot.DeepClone() } },
            });
            var stored = await env.Ideas();
            Check.Equal(1, stored.Count);
            Check.Equal(shot["path"].Str(), ((JsonArray)stored[0]["images"]!)[0]!.AsObject()["path"].Str());
            var prompt = (await env.Ctx.RpcFake.Call("ideas.toPrompt", new JsonObject { ["id"] = added["id"].Str() }))!.ToString()!;
            Check.Contains(prompt, "## Images");
            Check.Contains(prompt, file);

            // 3. only files this host wrote are readable or removable
            foreach (var outside in new[] { "C:/Windows/win.ini", "../../secrets.txt", "ideas.json" })
            {
                var bad = await Check.ThrowsAsync<RpcException>(() => env.Rpc("ideas.image", new JsonObject { ["path"] = outside }));
                Check.Contains(bad.Message, "not a stored idea image");
            }
            Check.True(File.Exists(file), "a refused path deleted nothing");

            // 4. a type that is not an image, and one over the cap, are refused before anything is written
            var notImage = await Check.ThrowsAsync<RpcException>(() => env.Rpc("ideas.addImage", new JsonObject { ["data"] = png, ["mediaType"] = "application/pdf" }));
            Check.Equal("bad_request", notImage.Code);
            var huge = new string('A', IdeaImages.MaxBytes * 2);
            var tooBig = await Check.ThrowsAsync<RpcException>(() => env.Rpc("ideas.addImage", new JsonObject { ["data"] = huge, ["mediaType"] = "image/png" }));
            Check.Contains(tooBig.Message, "larger than");
            Check.Equal(1, Directory.GetFiles(Path.Combine(env.Ctx.Paths.Home, IdeaImages.Dir)).Length, "only the one image is on disk");

            // 5. removing it takes the file with it
            await env.Ctx.RpcFake.Call("ideas.removeImage", new JsonObject { ["path"] = shot["path"].Str() });
            Check.False(File.Exists(file), "the file is gone");
            // …and deleting the idea takes whatever it still carried
            var again = await env.Rpc("ideas.addImage", new JsonObject { ["data"] = png, ["mediaType"] = "image/png" });
            await env.Rpc("ideas.add", new JsonObject
            {
                ["idea"] = new JsonObject { ["title"] = "With a shot", ["images"] = new JsonArray { again.DeepClone() } },
            });
            var second = (await env.Ideas()).Single(i => i["title"].Str() == "With a shot");
            await env.Ctx.RpcFake.Call("ideas.delete", new JsonObject { ["id"] = second["id"].Str() });
            Check.False(File.Exists(Path.Combine(env.Ctx.Paths.Home, again["path"].Str()!.Replace('/', Path.DirectorySeparatorChar))), "deleting the idea deleted its image");
        });

        r.Add("ideas: an image reference that escapes the images directory is not read and not deleted", async () =>
        {
            var env = new Env();
            await env.StartAsync();
            var home = env.Ctx.Paths.Home;
            var settings = Path.Combine(home, "settings.json");
            File.WriteAllText(settings, "{\"api_key\": \"sekret\"}");
            Directory.CreateDirectory(Path.Combine(home, IdeaImages.Dir));
            File.WriteAllBytes(Path.Combine(home, IdeaImages.Dir, "shot.png"), new byte[] { 0x89, 0x50, 0x4e, 0x47, 1 });

            // The escape from the report (idea-3m2h1g): a reference that steps out of the images directory names a
            // file the host must not touch — neither for reading nor for deleting.
            var bad = await Check.ThrowsAsync<RpcException>(() => env.Rpc("ideas.image", new JsonObject { ["path"] = "idea-images/../settings.json" }));
            Check.Contains(bad.Message, "not a stored idea image");
            await env.Ctx.RpcFake.Call("ideas.removeImage", new JsonObject { ["path"] = "idea-images/../settings.json" });
            Check.True(File.Exists(settings), "the file outside the directory is still there");

            // Absolute references, references that walk up, and the directory itself are not stored images either.
            foreach (var outside in new[] { settings.Replace('\\', '/'), "../../secrets.txt", "idea-images", "IDEA-IMAGES/../settings.json", "idea-images/shot.png/../../settings.json" })
            {
                Check.True(IdeaImages.Inside(home, outside) is null, $"refused: {outside}");
                Check.True(IdeaImages.Absolute(home, outside) is null, $"no absolute path for: {outside}");
            }

            // A reference that stays in the directory resolves — with the directory's own spelling, a walk that ends
            // inside normalized, and on case-insensitive platforms under either case.
            Check.Equal("idea-images/shot.png", IdeaImages.Inside(home, "idea-images/shot.png"));
            Check.Equal("idea-images/shot.png", IdeaImages.Inside(home, "idea-images/shot.png/../shot.png"), "a walk that ends inside stays inside");
            if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
                Check.True(IdeaImages.Inside(home, "IDEA-IMAGES/shot.png") is not null, "the platform's case handling");
            else
                Check.True(IdeaImages.Inside(home, "IDEA-IMAGES/shot.png") is null, "the platform's case handling");

            // A link inside the directory that points out of it is a file outside the directory.
            var link = Path.Combine(home, IdeaImages.Dir, "outward");
            try
            {
                File.CreateSymbolicLink(link, settings);
                Check.True(IdeaImages.Inside(home, "idea-images/outward") is null, "a symlink out of the directory is refused");
                Check.True(IdeaImages.Absolute(home, "idea-images/outward") is null);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Console.WriteLine("    (no symlink privilege on this machine: the symlink case is not checked)");
            }
            finally
            {
                try { if (File.Exists(link) || new FileInfo(link).LinkTarget is not null) File.Delete(link); } catch { }
            }

            Check.True(File.Exists(Path.Combine(home, IdeaImages.Dir, "shot.png")), "and nothing inside the directory was touched");
            Check.True(File.Exists(settings), "the file outside is still there");
            env.Ctx.Unload();
        });

        r.Add("ideas: removing an image from an idea deletes the file only once the update commits", async () =>
        {
            var env = new Env();
            await env.StartAsync();
            var png = Convert.ToBase64String(new byte[] { 0x89, 0x50, 0x4e, 0x47, 1, 2, 3, 4 });
            var shot = await env.Rpc("ideas.addImage", new JsonObject { ["data"] = png, ["mediaType"] = "image/png" });
            var path = shot["path"].Str()!;
            var file = Path.Combine(env.Ctx.Paths.Home, path.Replace('/', Path.DirectorySeparatorChar));
            var added = await env.Rpc("ideas.add", new JsonObject
            {
                ["idea"] = new JsonObject { ["title"] = "With a shot", ["images"] = new JsonArray { shot.DeepClone() } },
            });
            var id = added["id"].Str()!;
            Check.True(File.Exists(file), "the file is on disk: " + file);

            // Someone else edits the idea after the card opened. The card's removal, still on the old revision, is
            // refused: neither the document nor the file moves (idea-qpaghc).
            await env.Rpc("ideas.update", new JsonObject { ["id"] = id, ["patch"] = new JsonObject { ["summary"] = "someone else's edit" } });
            var refused = await Check.ThrowsAsync<RpcException>(() => env.Rpc("ideas.update", new JsonObject
            {
                ["id"] = id, ["expectedRevision"] = 1,
                ["patch"] = new JsonObject { ["images"] = new JsonArray() },
            }));
            Check.Equal("conflict", refused.Code, refused.Message);
            Check.True(File.Exists(file), "the refused update left the file on disk");
            var doc = await env.Rpc("ideas.get", new JsonObject { ["id"] = id });
            Check.Equal(1, ((JsonArray)doc["images"]!).Count, "…and the reference is still on the idea");

            // The same removal, on the revision the idea is actually at: the reference goes and the file follows —
            // after the write has committed.
            await env.Rpc("ideas.update", new JsonObject
            {
                ["id"] = id, ["expectedRevision"] = 2,
                ["patch"] = new JsonObject { ["images"] = new JsonArray() },
            });
            Check.False(File.Exists(file), "the file left the disk once the update committed");
            doc = await env.Rpc("ideas.get", new JsonObject { ["id"] = id });
            Check.Equal(0, ((JsonArray)doc["images"]!).Count, "and the reference is gone from the idea");
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
            Check.True((await env.Ideas()).Any(i => i["id"].Str() == id), "it is in the backlog");

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
            // how to change a section, for a model that never read the manual (a local agent went to ideas.json by hand)
            Check.Contains(get.Content, "To change a section: action update with updateSections [{\"id\": \"sec-…\", \"content\": \"…\"}]");
            var schema = env.Tool("ideas").Definition.Parameters!["properties"]!;
            Check.Equal("id|title|content|kind", string.Join("|", schema["updateSections"]!["items"]!["properties"]!.AsObject().Select(p => p.Key)));
            Check.Equal("id", schema["updateSections"]!["items"]!["required"]![0].Str());
            Check.Equal("kind|title|content", string.Join("|", schema["addSections"]!["items"]!["properties"]!.AsObject().Select(p => p.Key)));
            Check.True(schema["sections"]!["items"]!["properties"]!["kind"]!["enum"]!.AsArray().Any(k => k.Str() == "decision"), "section kinds listed");

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
            Check.True((await env.Ideas()).Any(i => i["id"].Str() == id), "and still in the backlog: deleting is the user's");
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

            var quick = (JsonObject)(await env.Ctx.RpcFake.Call("ideas.quickAdd", new JsonObject { ["sessionId"] = env.Session.Id, ["args"] = "  Fourth idea " }))!;
            Check.Contains(quick["text"].Str(), "Added idea-");
            Check.Contains(quick["text"].Str(), "Fourth idea (project Demo)");
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
            var order = ((JsonArray)listed["ideas"]!).Select(i => i!["title"].Str()).ToList();
            Check.Equal("Second|First (renamed)|Third|Fourth idea", string.Join("|", order));

            var prompt = (string?)await env.Ctx.RpcFake.Call("ideas.toPrompt", new JsonObject { ["id"] = aid });
            Check.Contains(prompt, "Implement the following idea from the ideas backlog (`" + aid + "`).");
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

        r.Add("ideas: one backlog in the store for all (project and projectless)", async () =>
        {
            var env = new Env();
            await env.StartAsync();
            Ok(await env.Run("ideas", new { action = "add", title = "Global one" }, withProject: false));
            Ok(await env.Run("ideas", new { action = "add", title = "Project one" }));
            Check.False(System.IO.File.Exists(env.File), "the backlog is not a file any more");
            Check.False(System.IO.File.Exists(Path.Combine(env.ProjectDir, ".netpi", "ideas.json")), "and never was a per-project one");
            var ideas = await env.Ideas();
            var titles = ideas.Select(i => i["title"].Str()).ToList();
            Check.True(titles.Contains("Global one"));
            Check.True(titles.Contains("Project one"));
            Check.True(ideas.Single(i => i["title"].Str() == "Global one")["project"] is null, "the projectless session's idea is unbound");
            Check.Equal(env.Project.Id, ideas.Single(i => i["title"].Str() == "Project one")["project"]!["id"].Str());

            Check.Equal(2, (await env.Ideas()).Count);
            var storage = (await env.Rpc("ideas.list", new JsonObject()))["storage"]!;
            Check.Equal("sqlite", storage["backend"].Str());
            Check.Equal("netpi.ideas", storage["scope"].Str());
            env.Ctx.Unload();
        });

        r.Add("ideas: unknown fields survive every write", async () =>
        {
            var env = new Env();
            await env.StartAsync();
            var added = await env.Rpc("ideas.add", new JsonObject
            {
                ["idea"] = new JsonObject
                {
                    ["title"] = "Handwritten",
                    ["estimate"] = new JsonObject { ["days"] = 3 },
                    ["sections"] = new JsonArray(new JsonObject { ["kind"] = "note", ["content"] = "x", ["author"] = "bob" }),
                },
            });
            Check.Equal("{\"days\":3}", added["estimate"]!.ToJsonString(), "a field this build does not know is stored");

            Ok(await env.Run("ideas", new { action = "update", id = added["id"]!.Str(), priority = "high", addSections = new[] { new { kind = "plan", content = "multi\nline" } } }));
            var again = (await env.Ideas()).Single();
            Check.Equal("{\"days\":3}", again["estimate"]!.ToJsonString(), "and still there after a write");
            Check.Equal("bob", again["sections"]![0]!["author"].Str(), "on a section too");
            Check.Equal("high", again["priority"].Str());
            Check.Equal("multi\nline", again["sections"]![1]!["content"].Str());
            Check.Equal(2, ((JsonArray)again["sections"]!).Count);
            Check.True(again["revision"]!.GetValue<long>() >= 1, "and it carries the revision an editor submits back");
            env.Ctx.Unload();
        });

        r.Add("ideas: concurrent adds are serialized by the store", async () =>
        {
            var env = new Env();
            await env.StartAsync();
            var results = await Task.WhenAll(Enumerable.Range(0, 25).Select(i => Task.Run(() => env.Run("ideas", new { action = "add", title = "T" + i }))));
            Check.True(results.All(x => !x.IsError), string.Join("; ", results.Where(x => x.IsError).Select(x => x.Content)));
            var ids = (await env.Ideas()).Select(i => i["id"].Str()).ToList();
            Check.Equal(25, ids.Count);
            Check.Equal(25, ids.Distinct().Count(), "every add got its own id");
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
        });

        async Task<(Env Env, string Mine, string Global, string Other)> RecallEnv()
        {
            var env = new Env();
            await env.StartAsync();
            var mine = (await env.Rpc("ideas.add", new JsonObject { ["sessionId"] = env.Session.Id, ["idea"] = new JsonObject { ["title"] = "Nudge reset", ["summary"] = "Reset the nudge counter" } }))["id"].Str()!;
            var global = (await env.Rpc("ideas.add", new JsonObject { ["projectId"] = "global", ["idea"] = new JsonObject { ["title"] = "Calm ideas tab" } }))["id"].Str()!;
            var other = (await env.Rpc("ideas.add", new JsonObject { ["sessionId"] = env.Session2.Id, ["idea"] = new JsonObject { ["title"] = "Other project idea" } }))["id"].Str()!;
            var done = (await env.Rpc("ideas.add", new JsonObject { ["sessionId"] = env.Session.Id, ["idea"] = new JsonObject { ["title"] = "Finished one", ["status"] = "done" } }))["id"].Str()!;
            return (env, mine, global, other);
        }

        r.Add("ideas: attach adds the idea to the chat as a notice and records the session on the idea", async () =>
        {
            var (env, mine, _, _) = await RecallEnv();
            var res = await env.Rpc("ideas.attach", new JsonObject { ["sessionId"] = env.GlobalSession.Id, ["id"] = mine });
            Check.Equal(mine, res["ideaId"].Str());
            var notice = env.Ctx.SessionsFake.Messages.Last(m => m.SessionId == env.GlobalSession.Id);
            Check.Equal(MessageRole.Notice, notice.Role);
            Check.Equal("idea", notice.MetaString("kind"));
            Check.Equal(mine, notice.MetaString("ideaId"));
            Check.Contains(notice.Text, "The user added an idea from the ideas backlog");
            Check.Contains(notice.Text, "# Nudge reset");
            Check.Contains(notice.Text, "Reset the nudge counter");
            var idea = await env.Rpc("ideas.get", new JsonObject { ["id"] = mine });
            Check.True(idea["sessionIds"]!.AsArray().Any(x => x.Str() == env.GlobalSession.Id), "session recorded");

            await Check.ThrowsAsync<RpcException>(() => env.Rpc("ideas.attach", new JsonObject { ["sessionId"] = env.Session.Id, ["id"] = "idea-nope00" }));
        });
    }
}
