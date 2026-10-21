using System.Text.Json.Nodes;
using NetPI.Ideas;

namespace NetPI.Aux.Tests;

/// <summary>The automatic refine drain of the Ideas plugin (ideas.autoRefine.*), with a recording runtime and a scripted agents.list.</summary>
public static class IdeasAutoRefineTests
{
    private sealed class Runtime : IAgentRuntime
    {
        public List<string> Sent { get; } = [];
        public Dictionary<string, AgentInfo> Agents { get; } = [];
        public bool Fail { get; set; }
        public IReadOnlyList<AgentInfo> List(bool includeFinished = true) => [.. Agents.Values];
        public AgentInfo? Get(string agentId) => null;
        public AgentInfo? GetBySession(string sessionId) => Agents.GetValueOrDefault(sessionId);
        public Task<AgentInfo> SendAsync(string sessionId, UserInput input, DeliveryMode mode = DeliveryMode.Auto, CancellationToken ct = default)
        {
            if (Fail) throw new InvalidOperationException("the runtime is stopping");
            Sent.Add(input.Text);
            var info = new AgentInfo { Id = "agt_" + sessionId, SessionId = sessionId, Status = AgentStatus.Running };
            Agents[sessionId] = info;
            return Task.FromResult(info);
        }
        public Task<AgentInfo> SpawnAsync(SpawnRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AgentWaitResult> WaitAsync(string? callerAgentId, IReadOnlyList<string> agentIds, bool yieldSlot = true, TimeSpan? timeout = null, CancellationToken ct = default, bool any = false) => throw new NotSupportedException();
        public Task<bool> AbortAsync(string agentOrSessionId) => Task.FromResult(false);
        public Task<bool> MessageAsync(string fromAgentId, string toAgentId, string text, DeliveryMode mode = DeliveryMode.Auto, CancellationToken ct = default) => Task.FromResult(false);
        public IReadOnlyList<QueuedInput> GetQueue(string sessionId) => [];
        public bool RemoveQueued(string sessionId, string inputId) => false;
    }

    private sealed class StubTool(string name, bool readOnly) : IAgentTool
    {
        public ToolDefinition Definition { get; } = new() { Name = name, Description = name, ReadOnly = readOnly };
        public Task<ToolResult> ExecuteAsync(ToolContext context, System.Text.Json.JsonElement args, CancellationToken ct) => Task.FromResult(ToolResult.Ok("ok"));
    }

    private sealed class Env
    {
        public FakePluginContext Ctx { get; } = new(T.TempDir("autorefine-home"));
        public Runtime Runtime { get; } = new();
        public IdeasPlugin Plugin { get; } = new();
        public List<string> Used { get; } = [];
        public ProjectInfo Project { get; }
        public IdeasRepository Repo => IdeasRepository.Open(Ctx.Data, Ctx.Access, Ctx.Log, Ctx.Paths.Home);
        public IdeaAutoRefine Drain => Plugin.AutoRefine!;
        private int _n;

        public Env(bool enabled = true, string[]? agents = null, int? max = null)
        {
            Ctx.ModelsFake.Models.Add(new ModelInfo { Provider = "aiproxy", Id = "qwen3.8-27b", IsLocal = true, MaxOutputTokens = 16384 });
            Project = Ctx.SessionsFake.CreateProject("Demo", T.TempDir("autorefine-proj"));
            Ctx.ServicesFake.Register<IAgentRuntime>(Runtime);
            Ctx.RpcFake.Register("agents.use", (req, _) => { Used.Add(req.Str("agent")!); return Task.FromResult<object?>(true); }, "test");
            foreach (var (name, ro) in new[] { ("read", true), ("write", false), ("bash", false), ("ask_user", false), ("todo_write", false) })
                Ctx.ToolsFake.Register(new StubTool(name, ro));
            Ctx.SettingsFake.Set("ideas.autoRefine.delayMinutes", 10);
            if (enabled) Ctx.SettingsFake.Set("ideas.autoRefine.enabled", true);
            if (agents is not null) Ctx.SettingsFake.Set("ideas.autoRefine.agents", new JsonArray([.. agents.Select(a => (JsonNode)a)]));
            if (max is { } m) Ctx.SettingsFake.Set("ideas.autoRefine.maxConcurrent", m);
        }

        public async Task<Env> StartAsync()
        {
            await Plugin.StartAsync(Ctx, CancellationToken.None);
            Drain.Grace = TimeSpan.Zero;
            return this;
        }

        public string Idea(string title = "An idea", string createdBy = "user", int ageMinutes = 60, string[]? tags = null, string status = "open", bool refined = false)
        {
            var id = "idea-t" + (++_n).ToString("D5");
            var at = DateTimeOffset.UtcNow.AddMinutes(-ageMinutes).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
            var doc = new JsonObject
            {
                ["id"] = id, ["title"] = title, ["summary"] = "s", ["status"] = status, ["priority"] = "medium",
                ["tags"] = new JsonArray([.. (tags ?? []).Select(t => (JsonNode)t)]), ["createdAt"] = at, ["updatedAt"] = at, ["createdBy"] = createdBy,
                ["sections"] = new JsonArray(), ["sessionIds"] = new JsonArray(),
                ["project"] = new JsonObject { ["id"] = Project.Id, ["name"] = Project.Name },
            };
            if (refined) doc["autoRefine"] = new JsonObject { ["state"] = "done", ["manual"] = true, ["at"] = at };
            Repo.Add(doc);
            return id;
        }

        public JsonObject Doc(string id) => Repo.Find(id)!.Doc;
        public string State(string id) => Doc(id)["autoRefine"]?["state"]?.GetValue<string>() ?? "none";
        public string SessionOf(string id) => Doc(id)["autoRefine"]!["sessionId"]!.GetValue<string>();
        public async Task Tick() => await Drain.TickAsync(CancellationToken.None);
        public async Task<JsonObject> Status() => (JsonObject)NetPiJson.ToNode(await Ctx.RpcFake.Call("ideas.autoRefine.status", new JsonObject()))!;

        public void Finish(string sessionId, AgentStatus status, string? error = null)
        {
            Runtime.Agents[sessionId].Status = status;
            Runtime.Agents[sessionId].Error = error;
            Ctx.Events.Publish(EventTypes.AgentStatus, new JsonObject { ["agent"] = new JsonObject { ["id"] = "agt", ["sessionId"] = sessionId, ["status"] = status.ToString().ToLowerInvariant(), ["error"] = error } });
        }
    }

    private static async Task Settled(Func<bool> done)
    {
        for (var i = 0; i < 200 && !done(); i++) await Task.Delay(25);
    }

    public static void Register(TestRunner r)
    {
        r.Add("ideas.autoRefine: disabled, or enabled with no agents, runs nothing", async () =>
        {
            var off = await new Env(enabled: false, agents: ["qwen"]).StartAsync();
            var a = off.Idea();
            await off.Tick();
            Check.Equal(0, off.Runtime.Sent.Count, "disabled");
            Check.Equal("none", off.State(a));

            var none = await new Env(enabled: true).StartAsync();
            var b = none.Idea();
            await none.Tick();
            Check.Equal(0, none.Runtime.Sent.Count, "no agents listed: nothing, never any agent");
            Check.Equal("none", none.State(b));
            var status = await none.Status();
            Check.Equal(true, status["enabled"]!.GetValue<bool>());
            Check.Equal(1, ((JsonArray)status["queued"]!).Count, "it waits");
        });

        r.Add("ideas.autoRefine: an eligible idea after the delay gets one low-priority chat with ask_user off", async () =>
        {
            var env = await new Env(agents: ["qwen"]).StartAsync();
            var fresh = env.Idea("Fresh", ageMinutes: 1);
            await env.Tick();
            Check.Equal(0, env.Runtime.Sent.Count, "an idea touched a minute ago is left alone");
            var id = env.Idea("Old enough", ageMinutes: 30);
            await env.Tick();
            Check.Equal(1, env.Runtime.Sent.Count);
            Check.Equal("running", env.State(id));
            Check.Equal("none", env.State(fresh));
            var session = env.Ctx.SessionsFake.GetSession(env.SessionOf(id))!;
            Check.Equal("low", session.Meta![RunPriority.MetaKey]!.GetValue<string>());
            var off = ((JsonArray)session.Meta![SessionTools.MetaKey]!).Select(n => n!.GetValue<string>()).ToList();
            Check.True(off.Contains("ask_user") && off.Contains("write") && off.Contains("bash") && !off.Contains("ideas") && !off.Contains("todo_write"), "writing tools and ask_user off: " + string.Join(",", off));
            Check.Equal("qwen", env.Used.Single());
            Check.Contains(env.Runtime.Sent[0], "Nobody is watching");
            Check.Contains(env.Runtime.Sent[0], "Questions");
            var status = await env.Status();
            Check.Equal(1, ((JsonArray)status["running"]!).Count);
            Check.Equal(1, ((JsonArray)status["queued"]!).Count);
        });

        r.Add("ideas.autoRefine: maxConcurrent is respected and the agents are taken in turn", async () =>
        {
            var env = await new Env(agents: ["a", "b"], max: 2).StartAsync();
            var ids = new[] { env.Idea("1", ageMinutes: 90), env.Idea("2", ageMinutes: 80), env.Idea("3", ageMinutes: 70) };
            await env.Tick();
            Check.Equal(2, env.Runtime.Sent.Count);
            Check.Equal("a,b", string.Join(",", env.Used));
            Check.Equal("none", env.State(ids[2]));
            await env.Tick();
            Check.Equal(2, env.Runtime.Sent.Count, "still two running");
            env.Finish(env.SessionOf(ids[0]), AgentStatus.Idle);
            await Settled(() => env.State(ids[0]) == "done");
            await env.Tick();
            Check.Equal(3, env.Runtime.Sent.Count, "a slot is free again");
            Check.Equal("a", env.Used[2]);
        });

        r.Add("ideas.autoRefine: keeps going until every eligible idea is refined (no daily cap)", async () =>
        {
            var env = await new Env(agents: ["qwen"], max: 1).StartAsync();
            var ids = Enumerable.Range(0, 30).Select(i => env.Idea("i" + i, ageMinutes: 60 + i)).ToList();
            for (var n = 0; n < ids.Count; n++)
            {
                await env.Tick();
                Check.Equal(n + 1, env.Runtime.Sent.Count, "one more started");
                var running = ids.Single(id => env.State(id) == "running");
                env.Finish(env.SessionOf(running), AgentStatus.Idle);
                await Settled(() => env.State(running) == "done");
            }
            Check.True(ids.All(id => env.State(id) == "done"), "all 30 refined");
        });

        r.Add("ideas.autoRefine: agent-filed, no-refine, already refined, not open and edited-just-now ideas are skipped", async () =>
        {
            var env = await new Env(agents: ["qwen"], max: 4).StartAsync();
            env.Idea("by agent", createdBy: "agent:agt_1");
            env.Idea("opted out", tags: ["No-Refine"]);
            env.Idea("refined by hand", refined: true);
            env.Idea("parked", status: "parked");
            env.Idea("just edited", ageMinutes: 2);
            var ok = env.Idea("the one");
            await env.Tick();
            Check.Equal(1, env.Runtime.Sent.Count);
            Check.Equal("running", env.State(ok));
        });

        r.Add("ideas.autoRefine: a manual refine marks the idea refined; skip adds the no-refine tag", async () =>
        {
            var env = await new Env(enabled: false, agents: ["qwen"]).StartAsync();
            var id = env.Idea();
            await env.Ctx.RpcFake.Call("ideas.refine", new JsonObject { ["id"] = id });
            Check.Equal("done", env.State(id));
            Check.Equal(true, env.Doc(id)["autoRefine"]!["manual"]!.GetValue<bool>());
            var other = env.Idea("other", tags: ["perf"]);
            await env.Ctx.RpcFake.Call("ideas.autoRefine.skip", new JsonObject { ["id"] = other });
            Check.Equal("perf,no-refine", string.Join(",", ((JsonArray)env.Doc(other)["tags"]!).Select(t => t!.GetValue<string>())));
            Check.True(!IdeaAutoRefine.Candidate(env.Doc(other)));
        });

        r.Add("ideas.autoRefine: a finished run is done and its chat archived (or kept, by setting); an edit during the run changes nothing", async () =>
        {
            var env = await new Env(agents: ["qwen"], max: 2).StartAsync();
            var id = env.Idea("one", ageMinutes: 90);
            var keep = env.Idea("two", ageMinutes: 80);
            env.Ctx.SettingsFake.Set("ideas.autoRefine.archiveWhenDone", true);
            await env.Tick();
            await env.Ctx.RpcFake.Call("ideas.update", new JsonObject { ["id"] = id, ["patch"] = new JsonObject { ["summary"] = "the owner edits meanwhile" } });
            await env.Tick();
            Check.Equal("running", env.State(id), "the run is left alone");
            var s1 = env.SessionOf(id);
            env.Finish(s1, AgentStatus.Idle);
            await Settled(() => env.State(id) == "done");
            Check.Equal("done", env.State(id));
            Check.True(env.Ctx.SessionsFake.GetSession(s1)!.Archived, "archived");
            Check.True(env.Doc(id)["sessions"]!.ToJsonString().Contains(s1), "and still linked on the card");

            env.Ctx.SettingsFake.Set("ideas.autoRefine.archiveWhenDone", false);
            var s2 = env.SessionOf(keep);
            env.Finish(s2, AgentStatus.Idle);
            await Settled(() => env.State(keep) == "done");
            Check.True(!env.Ctx.SessionsFake.GetSession(s2)!.Archived, "not archived when the setting is off");
        });

        r.Add("ideas.autoRefine: a failed run is failed with its error and its chat stays", async () =>
        {
            var env = await new Env(agents: ["qwen"]).StartAsync();
            var id = env.Idea();
            await env.Tick();
            var s = env.SessionOf(id);
            env.Finish(s, AgentStatus.Failed, "model not loaded");
            await Settled(() => env.State(id) == "failed");
            Check.Equal("failed", env.State(id));
            Check.Equal("model not loaded", env.Doc(id)["autoRefine"]!["error"]!.GetValue<string>());
            Check.True(!env.Ctx.SessionsFake.GetSession(s)!.Archived);
            await env.Tick();
            Check.Equal(1, env.Runtime.Sent.Count, "not retried by itself");

            // a start that fails is failed too, and leaves no chat
            var env2 = await new Env(agents: ["qwen"]).StartAsync();
            env2.Runtime.Fail = true;
            var id2 = env2.Idea();
            await env2.Tick();
            Check.Equal("failed", env2.State(id2));
            Check.Contains(env2.Doc(id2)["autoRefine"]!["error"]!.GetValue<string>(), "stopping");
        });

        r.Add("ideas.autoRefine: the sweep settles runs whose event was dropped or that were running at a restart", async () =>
        {
            var env = await new Env(agents: ["qwen"], max: 3).StartAsync();
            var dropped = env.Idea("dropped", ageMinutes: 90);
            var restarted = env.Idea("restarted", ageMinutes: 80);
            var died = env.Idea("died", ageMinutes: 70);
            await env.Tick();
            Check.Equal(3, env.Runtime.Sent.Count);
            // the status moved on without an event
            env.Runtime.Agents[env.SessionOf(dropped)].Status = AgentStatus.Idle;
            // after a restart no agent is known: the transcript decides
            env.Runtime.Agents.Remove(env.SessionOf(restarted));
            env.Ctx.SessionsFake.AppendMessage(env.SessionOf(restarted), new ChatMessage { SessionId = env.SessionOf(restarted), Role = MessageRole.Assistant, Parts = [new TextPart { Text = "done" }] });
            env.Runtime.Agents.Remove(env.SessionOf(died));
            env.Drain.Sweep();
            Check.Equal("done", env.State(dropped));
            Check.Equal("done", env.State(restarted));
            Check.Equal("failed", env.State(died));
            Check.Contains(env.Doc(died)["autoRefine"]!["error"]!.GetValue<string>(), "interrupted");
        });
    }
}
