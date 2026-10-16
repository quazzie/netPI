using System.Text.Json;
using System.Text.Json.Nodes;
using NetPI.Host.Plugins;
using NetPI.Runtime;

namespace NetPI.Agent.Tests;

/// <summary>Regressions of the runtime/agents review: save ordering, the spawn batch, promote while stopping, the before-call gate,
/// a corrupt waiting batch, the agent wait's reason, subagent cost, and the spawn caps (maxUsd, maxTurns).</summary>
public static class ReviewRuntimeTests
{
    public static void Register(TestRunner t)
    {
        t.Add("review: a record's older save never overwrites a newer one", SaveOrdering);
        t.Add("review: a corrupt waiting batch is logged and the agent says so", CorruptPending);
        t.Add("review: a before-call hook that throws blocks the call and names itself", ThrowingGateBlocks);
        t.Add("review: promoting a queued follow-up while the runtime stops keeps it queued", PromoteWhileStopping);
        t.Add("review: agent_spawn batch with an unknown model starts none", SpawnBatchUnknownModel);
        t.Add("review: agent_spawn batch whose second spawn fails stops the first child", SpawnBatchRollback);
        t.Add("review: agent wait says why it returned (the user wrote)", WaitReasonUser);
        t.Add("review: WaitAsync reports a timeout as its end", WaitEndTimeout);
        t.Add("review: agent list/result/wait show a subagent's cost", SubagentCost);
        t.Add("review: agent_spawn maxTurns ends the child's run at its cap", MaxTurns);
        t.Add("review: agent_spawn maxUsd refuses the call that would go over", MaxUsd);
    }

    private static bool IsChild(ModelRequest r) => r.SystemPrompt?.Contains("a subagent working for") == true;

    private static AgentInfo Info(string id, string task) => new() { Id = id, SessionId = "ses_" + id, Name = "n", Task = task, Status = AgentStatus.Completed, CreatedAt = DateTimeOffset.UtcNow };

    private static async Task SaveOrdering()
    {
        await using var h = await TestHost.StartAsync();
        var store = new AgentStore(new TestPluginContext(h, "netpi.runtime"));
        store.Initialize();
        // two saves racing: the snapshot of version 2 is written first, then the older one arrives
        store.Save(Info("agt_race", "newer"), null, true, [], null, version: 2);
        store.Save(Info("agt_race", "older"), null, true, [], null, version: 1);
        Check.Equal("newer", store.LoadBySession("ses_agt_race")!.Info.Task, "the older snapshot is dropped");
        store.Save(Info("agt_race", "newest"), null, true, [], null, version: 3);
        Check.Equal("newest", store.LoadBySession("ses_agt_race")!.Info.Task);

        // many threads, versions taken in order: whatever the interleaving, the last version is what loads
        var gate = new object();
        long next = 0;
        var tasks = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 50; i++)
            {
                long v;
                string text;
                lock (gate) { v = ++next; text = "v" + v; }
                store.Save(Info("agt_many", text), null, true, [], null, version: v);
            }
        })).ToArray();
        await Task.WhenAll(tasks);
        Check.Equal("v400", store.LoadBySession("ses_agt_many")!.Info.Task);
    }

    private static async Task CorruptPending()
    {
        await using var h = await TestHost.StartAsync();
        h.Storage.Plugins.For("netpi.runtime").Collection("agent_records", new CollectionSpec().Text("sessionId").Text("createdAt").Text("status"))
            .Put("agt_bad", new JsonObject
            {
                ["id"] = "agt_bad", ["sessionId"] = "ses_bad", ["name"] = "bad", ["status"] = "yielded",
                ["createdAt"] = "2026-01-01T00:00:00.0000000Z",
                ["pending"] = new JsonObject { ["open"] = "not an array" },
            });
        await h.StopPluginAsync("netpi.runtime");
        await h.StopPluginAsync("netpi.tools.agents");
        await h.StartPluginAsync(new RuntimePlugin());
        await h.StartPluginAsync(new NetPI.Tools.Agents.AgentToolsPlugin());
        var bad = h.Runtime.Get("agt_bad")!;
        Check.Contains(bad.Error, "could not be read");
    }

    private sealed class ThrowingHook : IAgentHook
    {
        public ValueTask<ToolCallDecision?> OnBeforeToolCallAsync(AgentTurnContext turn, ToolCallPart call) => throw new InvalidOperationException("policy store down");
    }

    private static async Task ThrowingGateBlocks()
    {
        await using var h = await TestHost.StartAsync();
        var tool = new FakeTool("probe", (c, a, ct) => Task.FromResult(ToolResult.Ok("ran")));
        h.AddTool(tool);
        h.Services.Register<IAgentHook>(new ThrowingHook());
        var call = Reply.Call("probe", new { });
        h.Catalog.Handler = (r, ct) => Reply.HasToolResult(r) ? Reply.Text("done") : Reply.Tools(call);
        var s = h.NewSession();
        await h.SendAsync(s.Id, "go");
        await h.IdleAsync(s.Id);
        Check.Equal(0, tool.Calls, "the call did not run");
        var result = h.Messages(s.Id).SelectMany(m => m.ToolResults).Single(r => r.CallId == call.Id);
        Check.True(result.IsError);
        Check.Contains(result.Content, "ThrowingHook");
        Check.Contains(result.Content, "policy store down");
    }

    private static async Task PromoteWhileStopping()
    {
        var gate = new ReloadGate();
        await using var h = await TestHost.StartAsync(x => x.Services.Register<IReloadGate>(gate));
        h.AddTool(new FakeTool("confirm", (c, a, ct) => Task.FromResult(ToolResult.WaitForUser(new UserWait
        {
            Id = "w_promote", Kind = "test", Activity = "waiting", Data = new JsonObject(),
            IfSteered = ToolResult.Ok("steered"), IfCancelled = ToolResult.Ok("cancelled"),
        }))));
        var call = Reply.Call("confirm", new { });
        h.Catalog.Handler = (r, ct) => Reply.HasToolResult(r) ? Reply.Text("went on") : Reply.Tools(call);
        var s = h.NewSession();
        await h.SendAsync(s.Id, "go");
        await Wait.Until(() => h.Runtime.GetBySession(s.Id) is { Status: AgentStatus.Yielded } && h.Runtime.Waits(s.Id).Count == 1, "it waits");
        await h.SendAsync(s.Id, "later", DeliveryMode.Queue);
        var queued = h.Runtime.GetQueue(s.Id).Single();
        var rt = (AgentRuntime)h.Runtime;
        await h.StopPluginAsync("netpi.runtime");
        // the runtime is stopping: nothing can be delivered, so the follow-up must stay where it is (and nothing throws)
        Check.False(rt.PromoteQueued(s.Id, queued.Id), "not promoted while stopping");
        Check.Equal(1, rt.GetQueue(s.Id).Count, "the input is still queued");
    }

    // ---------------------------------------------------------------- spawn batches

    private sealed class FailingSecondSpawn(IAgentRuntime inner) : IAgentRuntime
    {
        private int _spawns;
        public IReadOnlyList<AgentInfo> List(bool includeFinished = true) => inner.List(includeFinished);
        public AgentInfo? Get(string agentId) => inner.Get(agentId);
        public AgentInfo? GetBySession(string sessionId) => inner.GetBySession(sessionId);
        public Task<AgentInfo> SendAsync(string sessionId, UserInput input, DeliveryMode mode = DeliveryMode.Auto, CancellationToken ct = default) => inner.SendAsync(sessionId, input, mode, ct);
        public Task<AgentInfo> SpawnAsync(SpawnRequest request, CancellationToken ct = default) =>
            Interlocked.Increment(ref _spawns) == 2 ? throw new InvalidOperationException("no room for a second one") : inner.SpawnAsync(request, ct);
        public Task<AgentWaitResult> WaitAsync(string? callerAgentId, IReadOnlyList<string> agentIds, bool yieldSlot = true, TimeSpan? timeout = null, CancellationToken ct = default, bool any = false) =>
            inner.WaitAsync(callerAgentId, agentIds, yieldSlot, timeout, ct, any);
        public Task<bool> AbortAsync(string agentOrSessionId) => inner.AbortAsync(agentOrSessionId);
        public Task<bool> MessageAsync(string fromAgentId, string toAgentId, string text, DeliveryMode mode = DeliveryMode.Auto, CancellationToken ct = default) => inner.MessageAsync(fromAgentId, toAgentId, text, mode, ct);
        public IReadOnlyList<QueuedInput> GetQueue(string sessionId) => inner.GetQueue(sessionId);
        public bool RemoveQueued(string sessionId, string inputId) => inner.RemoveQueued(sessionId, inputId);
    }

    private static ToolContext ContextOf(TestHost h, AgentInfo parent) =>
        new() { SessionId = parent.SessionId, AgentId = parent.Id, CallId = "c~1", Cwd = h.Workspace, Services = h.Services, Events = h.Bus };

    private static async Task<AgentInfo> ParentAsync(TestHost h)
    {
        var s = h.NewSession();
        await h.SendAsync(s.Id, "hello");
        return await h.IdleAsync(s.Id);
    }

    private static async Task SpawnBatchUnknownModel()
    {
        await using var h = await TestHost.StartAsync();
        h.Catalog.Handler = (r, ct) => Reply.Text("ok");
        var parent = await ParentAsync(h);
        var args = JsonSerializer.SerializeToElement(new { subagents = new object[] { new { task = "a", name = "first" }, new { task = "b", name = "second", model = "nowhere/none" } } });
        var result = await h.Tools.Get("agent_spawn")!.ExecuteAsync(ContextOf(h, parent), args, CancellationToken.None);
        Check.True(result.IsError);
        Check.Contains(result.Content, "subagents[1]");
        Check.Contains(result.Content, "None of them was started");
        Check.Equal(0, h.Runtime.List(true).Count(a => a.ParentAgentId == parent.Id), "nothing was started");
    }

    private static async Task SpawnBatchRollback()
    {
        await using var h = await TestHost.StartAsync();
        h.Catalog.Cached.Single(m => m.Ref == "fake/local").Concurrency = 3;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Catalog.Handler = (r, ct) => IsChild(r) ? Reply.Text("child", c => gate.Task.WaitAsync(c)) : Reply.Text("ok");
        var parent = await ParentAsync(h);
        h.Services.Register<IAgentRuntime>(new FailingSecondSpawn(h.Runtime), 1000);
        var args = JsonSerializer.SerializeToElement(new { subagents = new object[] { new { task = "a", name = "first" }, new { task = "b", name = "second" } } });
        var result = await h.Tools.Get("agent_spawn")!.ExecuteAsync(ContextOf(h, parent), args, CancellationToken.None);
        Check.True(result.IsError);
        Check.Contains(result.Content, "subagents[1] (second) could not start: no room for a second one");
        Check.Contains(result.Content, "first");
        Check.Contains(result.Content, "stopped");
        var first = h.Runtime.List(true).Single(a => a.ParentAgentId == parent.Id);
        await Wait.Until(() => h.Runtime.Get(first.Id)!.Status == AgentStatus.Cancelled, "the started child was stopped");
        gate.TrySetResult();
    }

    // ---------------------------------------------------------------- agent wait, cost, caps

    private static async Task WaitReasonUser()
    {
        await using var h = await TestHost.StartAsync();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Catalog.Handler = (r, ct) =>
        {
            if (IsChild(r)) return Reply.Text("slow report", c => gate.Task.WaitAsync(c));
            if (Reply.LastUser(r) == "stop waiting") return Reply.Text("ok, not waiting");
            var names = r.Messages.Where(m => m.Role == MessageRole.Tool).SelectMany(m => m.ToolResults).Select(x => x.Name).ToList();
            if (names.Count == 0) return Reply.Tool("agent_spawn", new { task = "slow", name = "slowpoke" });
            if (names.Count == 1) return Reply.Tool("agent", new { action = "wait" });
            return Reply.Text("done waiting");
        };
        var s = h.NewSession();
        await h.SendAsync(s.Id, "go");
        await Wait.Until(() => h.Runtime.GetBySession(s.Id) is { Status: AgentStatus.Yielded }, "it waits for the child");
        await h.SendAsync(s.Id, "stop waiting");
        await h.IdleAsync(s.Id);
        gate.TrySetResult();
        var wait = h.Messages(s.Id).SelectMany(m => m.ToolResults).Single(r => r.Name == "agent");
        Check.Contains(wait.Content, "the user wrote a message");
        Check.False(wait.Content.Contains("you asked for the first report"), "not the first-report text");
        Check.Equal("user", (string?)wait.Details!["reason"]);
    }

    private static async Task WaitEndTimeout()
    {
        await using var h = await TestHost.StartAsync();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Catalog.Handler = (r, ct) => IsChild(r) ? Reply.Text("slow", c => gate.Task.WaitAsync(c)) : Reply.Tool("agent_spawn", new { task = "slow", name = "slowpoke" });
        var s = h.NewSession();
        await h.SendAsync(s.Id, "go");
        await Wait.Until(() => h.Runtime.List(true).Any(a => a.ParentAgentId != null), "the child started");
        var child = h.Runtime.List(true).First(a => a.ParentAgentId != null);
        var timedOut = await h.Runtime.WaitAsync(null, [child.Id], yieldSlot: false, TimeSpan.FromMilliseconds(200), CancellationToken.None);
        Check.Equal(AgentWaitEnd.Timeout, timedOut.End, "the timeout ended it");
        gate.TrySetResult();
        var done = await h.Runtime.WaitAsync(null, [child.Id], yieldSlot: false, null, CancellationToken.None);
        Check.Equal(AgentWaitEnd.Report, done.End, "the finished run ended it");
    }

    private static void Priced(TestHost h) =>
        h.Catalog.Cached.First(m => m.Ref == "cloud/big").Extra = new JsonObject
        {
            ["pricing"] = new JsonObject { ["prompt"] = "0.000003", ["completion"] = "0.000015" },
        };

    private static IAsyncEnumerable<ModelStreamEvent> Costing(string text, double costUsd)
    {
        var m = Reply.Message(new TextPart { Text = text });
        m.Usage!.CostUsd = costUsd;
        return Reply.Stream(m);
    }

    private static async Task SubagentCost()
    {
        await using var h = await TestHost.StartAsync(Priced);
        h.Catalog.Handler = (r, ct) =>
        {
            if (r.Model.Ref == "cloud/big") return Costing("child report", 0.5);
            var names = r.Messages.Where(m => m.Role == MessageRole.Tool).SelectMany(m => m.ToolResults).Select(x => x.Name).ToList();
            if (names.Count == 0) return Reply.Tool("agent_spawn", new { task = "do it", name = "payer", model = "cloud/big" });
            if (names.Count == 1) return Reply.Tool("agent", new { action = "wait" });
            if (names.Count == 2) return Reply.Tool("agent", new { action = "list" });
            if (names.Count == 3) return Reply.Tool("agent", new { action = "result", id = "payer" });
            return Reply.Text("done");
        };
        var s = h.NewSession();
        await h.SendAsync(s.Id, "go");
        await h.IdleAsync(s.Id, 15_000);
        var results = h.Messages(s.Id).SelectMany(m => m.ToolResults).Where(r => r.Name == "agent").ToList();
        Check.Contains(results[0].Content, "$0.50");
        Check.Contains(results[1].Content, "$0.50");
        Check.Contains(results[2].Content, "$0.50");
        Check.Equal(0.5, results[2].Details!["costUsd"]!.GetValue<double>());
    }

    private static async Task MaxTurns()
    {
        await using var h = await TestHost.StartAsync();
        h.AddTool(new FakeTool("probe", (c, a, ct) => Task.FromResult(ToolResult.Ok("ran"))));
        h.Catalog.Handler = (r, ct) =>
        {
            if (IsChild(r)) return Reply.Tool("probe", new { });   // never ends by itself
            var names = r.Messages.Where(m => m.Role == MessageRole.Tool).SelectMany(m => m.ToolResults).Select(x => x.Name).ToList();
            return names.Count == 0 ? Reply.Tool("agent_spawn", new { task = "loop", name = "looper", maxTurns = 2 })
                : names.Count == 1 ? Reply.Tool("agent", new { action = "wait" }) : Reply.Text("done");
        };
        var s = h.NewSession();
        await h.SendAsync(s.Id, "go");
        await h.IdleAsync(s.Id, 15_000);
        var child = h.Runtime.List(true).Single(a => a.IsSubagent);
        Check.Equal(AgentStatus.Failed, child.Status);
        Check.Contains(child.Error, "maxTurns of 2");
        Check.Equal(2, h.Catalog.Requests.Count(r => r.SessionId == child.SessionId), "two model calls, not more");
        Check.Equal(2, h.Sessions.GetSession(child.SessionId)!.Meta!["maxTurns"]!.GetValue<int>(), "kept on the child's session");
    }

    private static async Task MaxUsd()
    {
        await using var h = await TestHost.StartAsync(Priced);
        h.AddTool(new FakeTool("probe", (c, a, ct) => Task.FromResult(ToolResult.Ok("ran"))));
        var cap = 0.5;
        h.Catalog.Handler = (r, ct) =>
        {
            if (IsChild(r))
            {
                var m = Reply.Message([Reply.Call("probe", new { })]);
                m.Usage!.CostUsd = 0.4;
                return Reply.Stream(m);   // every call costs 0.4 and asks for another
            }
            var names = r.Messages.Where(m => m.Role == MessageRole.Tool).SelectMany(m => m.ToolResults).Select(x => x.Name).ToList();
            return names.Count == 0 ? Reply.Tool("agent_spawn", new { task = "pay", name = "spender", model = "cloud/big", maxUsd = cap })
                : names.Count == 1 ? Reply.Tool("agent", new { action = "wait" }) : Reply.Text("done");
        };
        var s = h.NewSession();
        await h.SendAsync(s.Id, "go");
        await h.IdleAsync(s.Id, 15_000);
        var child = h.Runtime.List(true).Single(a => a.IsSubagent);
        Check.Equal(AgentStatus.Failed, child.Status);
        Check.Contains(child.Error, "its maxUsd of $0.50 is spent");
        Check.Equal(1, h.Catalog.Requests.Count(r => r.SessionId == child.SessionId), "the second call was refused before it was made");

        // a cap below one call's reservation: refused at the first call
        cap = 0.001;
        var s2 = h.NewSession();
        await h.SendAsync(s2.Id, "go");
        await h.IdleAsync(s2.Id, 15_000);
        var tight = h.Runtime.List(true).Single(a => a.IsSubagent && a.ParentAgentId == h.Runtime.GetBySession(s2.Id)!.Id);
        Check.Contains(tight.Error, "its maxUsd of $0.001 is spent");
        Check.Equal(0, h.Catalog.Requests.Count(r => r.SessionId == tight.SessionId));
    }
}
