using System.Text.Json.Nodes;

namespace NetPI.Agent.Tests;

public static class SubagentTests
{
    public static void Register(TestRunner t)
    {
        t.Add("subagents: spawn + wait yields a 1-lane pool (no deadlock)", SpawnWaitYield);
        t.Add("subagents: parent auto-wake with agent-result", ParentAutoWake);
        t.Add("subagents: result steers a running parent", ResultSteersRunningParent);
        t.Add("subagents: agent_wait on several workers, one lane", WaitManyOneLane);
        t.Add("subagents: user steering interrupts agent_wait", SteerInterruptsWait);
        t.Add("subagents: agent-to-agent message wakes an idle agent", AgentMessage);
        t.Add("subagents: agent_send to parent", SendToParent);
        t.Add("subagents: model by pool key, tools allowlist, max depth", SpawnOptions);
        t.Add("subagents: aborting a parent cancels its children", AbortCascade);
        t.Add("subagents: failed subagent reports failure", FailedChild);
        t.Add("subagents: agent tools list / result / cancel / lanes_list", ToolsMisc);
        t.Add("subagents: an agent on a local model spawns onto a lane set up in settings (another provider)", SpawnOntoConfiguredPool);
    }

    private static bool IsChild(ModelRequest r) => r.SystemPrompt?.Contains("a subagent working for") == true;

    private static async Task SpawnWaitYield()
    {
        await using var h = await TestHost.StartAsync();
        var parent = h.NewSession(model: "fake/solo"); // 1 lane
        AgentStatus? parentStatusDuringChild = null;
        List<LaneOwnerInfo>? ownersDuringChild = null;
        string? childPrompt = null;
        h.Catalog.Handler = (r, ct) =>
        {
            if (IsChild(r))
            {
                childPrompt = r.SystemPrompt;
                parentStatusDuringChild = h.Runtime.GetBySession(parent.Id)!.Status;
                ownersDuringChild = h.Lanes!.Snapshot().Single(p => p.Key == "fake/solo").Owners;
                return Reply.Text("REPORT: 42 files");
            }
            return Reply.HasToolResult(r)
                ? Reply.Text("Parent done: " + r.Messages[^1].ToolResults.Single().Content)
                : Reply.Tool("agent_spawn", new { task = "Count the files.", name = "counter", wait = true });
        };
        await h.SendAsync(parent.Id, "delegate");
        var p = await h.IdleAsync(parent.Id, 15_000);

        Check.Equal(AgentStatus.Yielded, parentStatusDuringChild, "parent yielded while the child ran");
        var child = h.Runtime.Get(p.Children.Single())!;
        Check.Equal(child.Id, ownersDuringChild!.Single().AgentId);
        Check.Equal(AgentStatus.Completed, child.Status);
        Check.Equal("REPORT: 42 files", child.Result);
        Check.True(child.IsSubagent);
        Check.Equal(1, child.Depth);
        Check.Equal(p.Id, child.ParentAgentId);
        Check.Equal("counter", child.Name);
        Check.Equal("fake/solo", child.Model, "inherits the parent's model");
        Check.Contains(childPrompt, "\"counter\"");
        Check.Contains(childPrompt, "final report");

        var cs = h.Sessions.GetSession(child.SessionId)!;
        Check.Equal("subagent", cs.Kind);
        Check.Equal(parent.Id, cs.ParentSessionId);
        Check.Equal("counter", cs.Title);
        var childMsgs = h.Messages(child.SessionId);
        Check.Equal("Count the files.", childMsgs[0].Text);
        Check.Equal("agent:" + p.Id, childMsgs[0].MetaString("source"));

        var pm = h.Messages(parent.Id);
        var toolResult = pm.Single(m => m.Role == MessageRole.Tool).ToolResults.Single();
        Check.Contains(toolResult.Content, "REPORT: 42 files");
        Check.Equal(child.Id, (string?)toolResult.Details!["agentId"]);
        Check.Equal(child.SessionId, (string?)toolResult.Details!["sessionId"]);
        Check.Equal("completed", (string?)toolResult.Details!["status"]);
        Check.Contains(pm[^1].Text, "Parent done:");
        Check.False(pm.Any(m => m.MetaString("kind") == "agent-result"), "no duplicate notification");
        Check.Equal(1, p.Runs);
        Check.Equal(0, h.Lanes!.Snapshot().Single(x => x.Key == "fake/solo").Busy);
        // parent went Yielded → Queued/Running again
        var parentStatuses = h.Bus.OfType(EventTypes.AgentStatus).Select(FakeBus.Data)
            .Where(d => (string?)d["agent"]!["id"] == p.Id).Select(d => (string)d["agent"]!["status"]!).ToList();
        Check.True(parentStatuses.Contains("yielded"), "yielded published");
    }

    private static async Task ParentAutoWake()
    {
        await using var h = await TestHost.StartAsync();
        var parent = h.NewSession();
        var childGate = new TaskCompletionSource();
        h.Catalog.Handler = (r, ct) =>
        {
            if (IsChild(r)) return Reply.Text("BG RESULT", c => childGate.Task.WaitAsync(c));
            if (Reply.LastUser(r).Contains("<agent-result")) return Reply.Text("got it");
            return Reply.HasToolResult(r) ? Reply.Text("spawned, carrying on") : Reply.Tool("agent_spawn", new { task = "background job", name = "bg" });
        };
        await h.SendAsync(parent.Id, "start a background job");
        var p = await h.IdleAsync(parent.Id);
        Check.Equal(1, p.Runs);
        Check.Contains(h.Messages(parent.Id).Single(m => m.Role == MessageRole.Tool).ToolResults.Single().Content, "Spawned subagent \"bg\"");

        childGate.SetResult();
        var childId = p.Children.Single();
        await h.StatusAsync(childId, AgentStatus.Completed);
        await Wait.Until(() => h.Runtime.GetBySession(parent.Id)!.Runs == 2, "parent woken");
        p = await h.IdleAsync(parent.Id);
        var msgs = h.Messages(parent.Id);
        var notice = msgs.Single(m => m.MetaString("kind") == "agent-result");
        Check.Equal(MessageRole.Notice, notice.Role);
        Check.Contains(notice.Text, $"<agent-result id=\"{childId}\" name=\"bg\" status=\"completed\">");
        Check.Contains(notice.Text, "BG RESULT");
        Check.Equal("agent:" + childId, notice.MetaString("source"));
        Check.Equal("got it", msgs[^1].Text);
    }

    private static async Task ResultSteersRunningParent()
    {
        await using var h = await TestHost.StartAsync();
        var parent = h.NewSession();
        var slowStarted = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        h.AddTool(new FakeTool("slow", async (ctx, args, ct) =>
        {
            slowStarted.TrySetResult();
            await release.Task.WaitAsync(ct);
            return ToolResult.Ok("slow done");
        }));
        var parentCalls = 0;
        h.Catalog.Handler = (r, ct) =>
        {
            if (IsChild(r)) return Reply.Text("child finished");
            return Interlocked.Increment(ref parentCalls) switch
            {
                1 => Reply.Tool("agent_spawn", new { task = "quick job", name = "quick" }),
                2 => Reply.Tools(Reply.Call("slow"), Reply.Call("slow")),
                _ => Reply.Text("final"),
            };
        };
        await h.SendAsync(parent.Id, "go");
        await slowStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var p = h.Runtime.GetBySession(parent.Id)!;
        await Wait.Until(() => h.Runtime.GetQueue(parent.Id).Count == 1, "notification queued as steering");
        Check.Equal("steer", h.Runtime.GetQueue(parent.Id)[0].Mode);
        release.SetResult();
        p = await h.IdleAsync(parent.Id);
        Check.Equal(1, p.Runs, "delivered within the running run");
        var msgs = h.Messages(parent.Id);
        var tools = msgs.Where(m => m.Role == MessageRole.Tool).Select(m => m.ToolResults.Single().Content).ToList();
        Check.Equal("slow done", tools[1]);
        Check.Contains(tools[2], "Skipped", "second slow call skipped by the steering notice");
        Check.Contains(msgs.Single(m => m.MetaString("kind") == "agent-result").Text, "child finished");
        Check.Equal("final", msgs[^1].Text);
    }

    private static async Task WaitManyOneLane()
    {
        await using var h = await TestHost.StartAsync();
        var parent = h.NewSession(model: "fake/solo");
        var concurrent = 0;
        var maxConcurrent = 0;
        h.Catalog.Handler = (r, ct) =>
        {
            if (IsChild(r))
            {
                return Reply.Text("done " + Reply.LastUser(r), async c =>
                {
                    var n = Interlocked.Increment(ref concurrent);
                    lock (h) maxConcurrent = Math.Max(maxConcurrent, n);
                    await Task.Delay(50, c);
                    Interlocked.Decrement(ref concurrent);
                });
            }
            if (!Reply.HasToolResult(r))
                return Reply.Tools(
                    Reply.Call("agent_spawn", new { task = "job A", name = "a" }),
                    Reply.Call("agent_spawn", new { task = "job B", name = "b" }),
                    Reply.Call("agent_spawn", new { task = "job C", name = "c" }));
            var last = r.Messages[^1].ToolResults.Last();
            return last.Name == "agent_wait" ? Reply.Text("summary") : Reply.Tool("agent_wait", new { });
        };
        await h.SendAsync(parent.Id, "fan out");
        var p = await h.IdleAsync(parent.Id, 15_000);
        Check.Equal(3, p.Children.Count);
        Check.Equal(1, maxConcurrent, "one lane: children ran one at a time");
        var wait = h.Messages(parent.Id).Where(m => m.Role == MessageRole.Tool).Select(m => m.ToolResults.Single()).Single(x => x.Name == "agent_wait");
        Check.Contains(wait.Content, "3 agents finished");
        Check.Contains(wait.Content, "done job A");
        Check.Contains(wait.Content, "done job B");
        Check.Contains(wait.Content, "done job C");
        Check.Equal(3, ((JsonArray)wait.Details!["agents"]!).Count);
        Check.False(h.Messages(parent.Id).Any(m => m.MetaString("kind") == "agent-result"), "results consumed by the wait");
        Check.Equal("summary", h.Messages(parent.Id)[^1].Text);
        Check.Equal(1, p.Runs);
    }

    private static async Task SteerInterruptsWait()
    {
        await using var h = await TestHost.StartAsync();
        var parent = h.NewSession();
        var childGate = new TaskCompletionSource();
        h.Catalog.Handler = (r, ct) =>
        {
            if (IsChild(r)) return Reply.Text("child done", c => childGate.Task.WaitAsync(c));
            if (Reply.LastUser(r) == "status?") return Reply.Text("still working on it");
            if (Reply.LastUser(r).Contains("<agent-result")) return Reply.Text("child reported");
            return Reply.Tool("agent_spawn", new { task = "long job", name = "long", wait = true });
        };
        await h.SendAsync(parent.Id, "go");
        await Wait.Until(() => h.Runtime.GetBySession(parent.Id)?.Status == AgentStatus.Yielded, "parent waiting");
        await h.SendAsync(parent.Id, "status?");
        await Wait.Until(() => h.Messages(parent.Id).Any(m => m.Text == "still working on it"), "parent answered the user");
        var wait = h.Messages(parent.Id).Single(m => m.Role == MessageRole.Tool).ToolResults.Single();
        Check.Contains(wait.Content, "still running");
        // the child keeps running; when it finishes it notifies the (now idle) parent
        childGate.SetResult();
        await Wait.Until(() => h.Messages(parent.Id).Any(m => m.Text == "child reported"), "child result delivered");
        await h.IdleAsync(parent.Id);
    }

    private static async Task AgentMessage()
    {
        await using var h = await TestHost.StartAsync();
        var a = h.NewSession(title: "A");
        var b = h.NewSession(title: "B");
        h.Catalog.Handler = (r, ct) => Reply.Text(r.SessionId == b.Id ? "B got: " + Reply.LastUser(r) : "A here");
        await h.SendAsync(a.Id, "hello");
        var ai = await h.IdleAsync(a.Id);
        Check.True(await h.Runtime.MessageAsync(ai.Id, b.Id, "ping from A"));
        var bi = await h.IdleAsync(b.Id);
        var notice = h.Messages(b.Id)[0];
        Check.Equal(MessageRole.Notice, notice.Role);
        Check.Equal("agent-message", notice.MetaString("kind"));
        Check.Equal("agent:" + ai.Id, notice.MetaString("source"));
        Check.Contains(notice.Text, $"<agent-message from=\"main ({ai.Id})\">");
        Check.Contains(notice.Text, "ping from A");
        Check.Contains(h.Messages(b.Id)[^1].Text, "ping from A");
        Check.Equal(1, bi.Runs);
        Check.False(await h.Runtime.MessageAsync(ai.Id, "agt_nobody", "hello?"));
    }

    private static async Task SendToParent()
    {
        await using var h = await TestHost.StartAsync();
        var parent = h.NewSession();
        h.Catalog.Handler = (r, ct) =>
        {
            if (IsChild(r))
                return Reply.HasToolResult(r) ? Reply.Text("child final") : Reply.Tool("agent_send", new { to = "parent", message = "progress: 50%" });
            if (Reply.LastUser(r).Contains("<agent-message")) return Reply.Text("noted progress");
            if (Reply.LastUser(r).Contains("<agent-result")) return Reply.Text("noted result");
            return Reply.HasToolResult(r) ? Reply.Text("spawned") : Reply.Tool("agent_spawn", new { task = "report progress", name = "reporter" });
        };
        await h.SendAsync(parent.Id, "go");
        await Wait.Until(() => h.Messages(parent.Id).Any(m => m.Text == "noted result"), "parent got the final result");
        await h.IdleAsync(parent.Id);
        var msgs = h.Messages(parent.Id);
        var msg = msgs.Single(m => m.MetaString("kind") == "agent-message");
        Check.Contains(msg.Text, "progress: 50%");
        Check.Contains(msg.Text, "from=\"reporter");
        var child = h.Runtime.List().Single(x => x.IsSubagent);
        var sendResult = h.Messages(child.SessionId).Single(m => m.Role == MessageRole.Tool).ToolResults.Single();
        Check.False(sendResult.IsError, sendResult.Content);
        Check.Contains(sendResult.Content, "Message delivered to main");
    }

    /// <summary>
    /// The user's setup: a "stealth" lane in lanes.pools for one model of a cloud provider; the main agent runs on the local
    /// model and delegates with agent_spawn { model: "stealth" }. No per-lane permission is involved: provider credentials
    /// are global and any agent may use any lane (limits: agents.maxDepth, lane capacity, lanes.budgets).
    /// </summary>
    private static async Task SpawnOntoConfiguredPool()
    {
        await using var h = await TestHost.StartAsync(x =>
            x.Settings.SetQuiet("lanes.stealth", JsonNode.Parse("""{ "model": "cloud/big", "capacity": 1, "use": "Research." }""")));
        h.Catalog.Handler = (r, ct) =>
        {
            if (r.Model.Ref == "cloud/big") return Reply.Text("child report: done on the stealth lane");
            var last = r.Messages[^1];
            if (last.Role == MessageRole.Tool) return Reply.Text("spawned");
            if (last.Text.Contains("<agent-result")) return Reply.Text("thanks");
            return Reply.Tool("agent_spawn", new { task = "Look something up and report.", lane = "stealth" });
        };
        var parent = h.NewSession(); // default model: fake/local
        await h.SendAsync(parent.Id, "delegate it");
        var p = h.Runtime.GetBySession(parent.Id)!;
        await Wait.Until(() => h.Runtime.Get(p.Id)?.Children.Count == 1, "child spawned");
        var child = await h.StatusAsync(h.Runtime.Get(p.Id)!.Children[0], AgentStatus.Completed);
        Check.Equal("cloud/big", child.Model);
        Check.Equal("stealth", child.Pool);
        Check.Equal("child report: done on the stealth lane", child.Result);
        Check.True(h.Lanes!.Snapshot().Any(x => x.Key == "stealth" && x.Capacity == 1), "the configured pool exists with its capacity");
        await Wait.Until(() => h.Messages(parent.Id).Any(m => m.Role == MessageRole.Assistant && m.Text == "thanks"), "the parent got the report");
        Check.Equal("fake/local", h.Catalog.Requests.First(r => r.SessionId == parent.Id).Model.Ref);
    }

    private static async Task SpawnOptions()
    {
        await using var h = await TestHost.StartAsync(x => x.Settings.SetQuiet("agents.maxDepth", 1));
        h.AddTool(new FakeTool("echo", (c, a, t) => Task.FromResult(ToolResult.Ok("e"))));
        h.AddTool(new FakeTool("other", (c, a, t) => Task.FromResult(ToolResult.Ok("o"))));
        var gate = new TaskCompletionSource();
        h.Catalog.Handler = (r, ct) => Reply.Text("ok", c => gate.Task.WaitAsync(c));
        var parent = h.NewSession();
        await h.SendAsync(parent.Id, "go");
        var p = h.Runtime.GetBySession(parent.Id)!;

        // pool key "cloud" → the cloud provider's model; tool allowlist
        var child = await h.Runtime.SpawnAsync(new SpawnRequest
        {
            Task = "cloud work", Model = "cloud", Tools = ["echo", "agent_spawn", "agent_send"], ParentAgentId = p.Id, Instructions = "Be extra careful.",
        });
        Check.Equal("cloud/big", child.Model);
        Check.Equal("agent-1", child.Name);
        await Wait.Until(() => h.Catalog.Requests.Any(r => r.SessionId == child.SessionId), "child called the model");
        var req = h.Catalog.Requests.First(r => r.SessionId == child.SessionId);
        Check.Equal("cloud/big", req.Model.Ref);
        var names = req.Tools.Select(x => x.Name).OrderBy(x => x).ToList();
        Check.Equal("agent_send,echo", string.Join(",", names), "allowlist + no orchestration tools at max depth");
        Check.Contains(req.SystemPrompt, "Be extra careful.");
        Check.Contains(req.SystemPrompt, "# Your role");

        // depth limit
        try
        {
            await h.Runtime.SpawnAsync(new SpawnRequest { Task = "too deep", ParentAgentId = child.Id });
            throw new AssertException("expected depth error");
        }
        catch (InvalidOperationException ex) { Check.Contains(ex.Message, "depth"); }

        // unknown model
        try
        {
            await h.Runtime.SpawnAsync(new SpawnRequest { Task = "x", Model = "nope/nothing", ParentAgentId = p.Id });
            throw new AssertException("expected model error");
        }
        catch (ArgumentException ex) { Check.Contains(ex.Message, "nope/nothing"); }

        // bare model id and full ref
        var c2 = await h.Runtime.SpawnAsync(new SpawnRequest { Task = "solo work", Model = "solo", ParentAgentId = p.Id, Name = "soloist" });
        Check.Equal("fake/solo", c2.Model);
        Check.Equal(2, h.Runtime.Get(p.Id)!.Children.Count);
        gate.SetResult();
        await h.IdleAsync(parent.Id);
        await h.StatusAsync(child.Id, AgentStatus.Completed);
        await h.StatusAsync(c2.Id, AgentStatus.Completed);
    }

    private static async Task AbortCascade()
    {
        await using var h = await TestHost.StartAsync();
        var parent = h.NewSession();
        h.Catalog.Handler = (r, ct) =>
        {
            if (IsChild(r)) return Reply.Text("never", c => Task.Delay(Timeout.Infinite, c));
            return Reply.Tool("agent_spawn", new { task = "hang", name = "hanger", wait = true });
        };
        await h.SendAsync(parent.Id, "go");
        await Wait.Until(() => h.Runtime.GetBySession(parent.Id)?.Status == AgentStatus.Yielded, "parent yielded");
        var childId = h.Runtime.GetBySession(parent.Id)!.Children.Single();
        await h.StatusAsync(childId, AgentStatus.Running);
        await h.Runtime.AbortAsync(parent.Id);
        var p = await h.IdleAsync(parent.Id);
        var c = await h.StatusAsync(childId, AgentStatus.Cancelled);
        Check.Contains(c.Error, "parent");
        await Task.Delay(100);
        Check.Equal(1, h.Runtime.GetBySession(parent.Id)!.Runs, "cancelled child does not wake the parent");
        Check.False(h.Messages(parent.Id).Any(m => m.MetaString("kind") == "agent-result"));
        Check.Equal(0, h.Lanes!.Snapshot().Sum(x => x.Busy + x.Queued));
    }

    private static async Task FailedChild()
    {
        await using var h = await TestHost.StartAsync();
        var parent = h.NewSession();
        h.Catalog.Handler = (r, ct) =>
        {
            if (IsChild(r)) return Reply.Fail(new ModelException("child model broke", false, 503));
            return Reply.HasToolResult(r) ? Reply.Text("handled") : Reply.Tool("agent_spawn", new { task = "doomed", name = "doomed", wait = true });
        };
        await h.SendAsync(parent.Id, "go");
        await h.IdleAsync(parent.Id);
        var child = h.Runtime.List().Single(a => a.IsSubagent);
        Check.Equal(AgentStatus.Failed, child.Status);
        Check.Contains(child.Error, "child model broke");
        var result = h.Messages(parent.Id).Single(m => m.Role == MessageRole.Tool).ToolResults.Single();
        Check.True(result.IsError);
        Check.Contains(result.Content, "failed");
        Check.Contains(result.Content, "child model broke");
    }

    private static async Task ToolsMisc()
    {
        await using var h = await TestHost.StartAsync();
        var parent = h.NewSession();
        var gate = new TaskCompletionSource();
        string? listOutput = null, lanesOutput = null, resultOutput = null, cancelOutput = null;
        var step = 0;
        h.Catalog.Handler = (r, ct) =>
        {
            if (IsChild(r)) return Reply.Text("child result", c => gate.Task.WaitAsync(c));
            var last = Reply.HasToolResult(r) ? r.Messages[^1].ToolResults.Last() : null;
            switch (last?.Name)
            {
                case "agent_list": listOutput = last.Content; break;
                case "lanes_list": lanesOutput = last.Content; break;
                case "agent_result": resultOutput = last.Content; break;
                case "agent_cancel": cancelOutput = last.Content; break;
            }
            return Interlocked.Increment(ref step) switch
            {
                1 => Reply.Tool("agent_spawn", new { task = "slow job", name = "slowpoke" }),
                2 => Reply.Tool("agent_list", new { }),
                3 => Reply.Tool("lanes_list", new { }),
                4 => Reply.Tool("agent_result", new { id = "slowpoke" }),
                5 => Reply.Tool("agent_cancel", new { id = "slowpoke" }),
                _ => Reply.Text("done"),
            };
        };
        await h.SendAsync(parent.Id, "go");
        await h.IdleAsync(parent.Id);
        Check.Contains(listOutput, "slowpoke");
        Check.Contains(listOutput, "task: slow job");
        Check.Contains(lanesOutput, "- fake/local · 2/2 busy");
        Check.Contains(lanesOutput, "main (you)");
        Check.Contains(lanesOutput, "You run on lane fake/local");
        Check.Contains(resultOutput, "still running");
        Check.Contains(cancelOutput, "Cancelled slowpoke");
        var child = h.Runtime.List().Single(a => a.IsSubagent);
        Check.Equal(AgentStatus.Cancelled, child.Status);
    }
}
