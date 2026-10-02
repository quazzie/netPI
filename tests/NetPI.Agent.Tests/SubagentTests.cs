using System.Text.Json.Nodes;

namespace NetPI.Agent.Tests;

public static class SubagentTests
{
    public static void Register(TestRunner t)
    {
        t.Add("subagents: spawn + wait yields the only slot of a model (no deadlock)", SpawnWaitYield);
        t.Add("subagents: parent auto-wake with agent-result", ParentAutoWake);
        t.Add("subagents: result steers a running parent", ResultSteersRunningParent);
        t.Add("subagents: agent_wait on several workers, one slot", WaitManyOneSlot);
        t.Add("subagents: user steering interrupts agent_wait", SteerInterruptsWait);
        t.Add("subagents: agent-to-agent message wakes an idle agent", AgentMessage);
        t.Add("subagents: agent_send to parent", SendToParent);
        t.Add("subagents: another model, tools allowlist, max depth", SpawnOptions);
        t.Add("subagents: aborting a parent cancels its children", AbortCascade);
        t.Add("subagents: failed subagent reports failure", FailedChild);
        t.Add("subagents: agent tools list / result / cancel / agent_choices", ToolsMisc);
        t.Add("subagents: a chat on a local agent spawns onto an agent on another provider; the subagent keeps that agent", SpawnOntoConfiguredPool);
        t.Add("subagents: inherit the parent's effective named-agent model and reasoning", InheritEffectiveModel);
        t.Add("subagents: one agent_spawn starts several together; waiting frees the caller's instance for one of them", BatchSpawn);
        t.Add("subagents: a batch with a bad entry starts none of them", BatchRefused);
        t.Add("subagents: agent_wait without ids also returns a report that arrived while the parent was busy, once", ReportBeforeWait);
        t.Add("subagents: a wait racing a child's finish still drops the agent-result notice", RacedWaitDropsNotice);
    }

    /// <summary>
    /// The user's story test: two subagents; the quick one finishes while the parent is still thinking about its next
    /// step, then the parent calls agent_wait without ids: it returns both reports, and the quick one arrives only once.
    /// </summary>
    private static async Task ReportBeforeWait()
    {
        await using var h = await TestHost.StartAsync();
        // The parent keeps thinking while both children run. With only two slots, starting the slow child first
        // would block the quick child, while the parent waits for quick before releasing slow: a test deadlock.
        h.Catalog.Cached.Single(m => m.Ref == "fake/local").Concurrency = 3;
        var slowGate = new TaskCompletionSource();
        var quickGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var seen = new List<string>();
        h.Catalog.Handler = (r, ct) =>
        {
            if (IsChild(r))
                return Reply.LastUser(r).Contains("quick")
                    ? Reply.Text("QUICK REPORT", c => quickGate.Task.WaitAsync(c))
                    : Reply.Text("SLOW REPORT", c => slowGate.Task.WaitAsync(c));
            var transcript = string.Join("\n", r.Messages.Select(m => string.Join("\n", m.ToolResults.Select(x => x.Content).Prepend(m.Text))));
            lock (seen) seen.Add(transcript);
            var results = r.Messages.Where(m => m.Role == MessageRole.Tool).SelectMany(m => m.ToolResults).Select(x => x.Name).ToList();
            if (results.Count == 0)
                return Reply.Tool("agent_spawn", new { subagents = new object[] { new { task = "quick part", name = "quick" }, new { task = "slow part", name = "slow" } }, background = true });
            if (results.Count == 1)
                // the parent thinks until the quick one is done, then waits
                return Reply.Stream(Reply.Message([Reply.Call("agent", new { action = "wait" })]), async c =>
                {
                    // Hold quick until this model call is in flight, so its notification cannot have been
                    // consumed before the state this test is meant to exercise.
                    quickGate.TrySetResult();
                    while (!h.Runtime.GetQueue(r.SessionId!).Any(i => i.Text.Contains("QUICK REPORT"))) await Task.Delay(10, c);
                    slowGate.TrySetResult();
                });
            return Reply.Text("both in");
        };
        var parent = h.NewSession();
        await h.SendAsync(parent.Id, "go");
        await h.IdleAsync(parent.Id, 15_000);
        // the model call right after agent_wait: it has both reports
        var afterWait = seen[2];
        Check.Contains(afterWait, "SLOW REPORT");
        Check.Contains(afterWait, "QUICK REPORT", "the quick one's report reached the model with the wait's result");
        // agent_wait without ids returned the quick one too (its report was still queued, unseen), and it arrived once
        var wait = h.Messages(parent.Id).Where(m => m.Role == MessageRole.Tool).SelectMany(m => m.ToolResults).Single(x => x.Name == "agent");
        Check.Contains(wait.Content, "2 agents finished.");
        Check.Contains(wait.Content, "QUICK REPORT");
        Check.Contains(wait.Content, "SLOW REPORT");
        Check.Equal(1, h.Messages(parent.Id).Count(m => m.Text.Contains("QUICK REPORT") || m.ToolResults.Any(x => x.Content.Contains("QUICK REPORT"))), "once");
    }

    /// <summary>
    /// The child ends on its own thread and the parent's wait lands right on its finish: the wait's read of the
    /// finished child must see the id of the agent-result notice — recorded together with the run's clearing, not
    /// after the store save that follows it — so the notice is dropped and the parent gets the report with the
    /// wait's result, not twice. The real SQLite store keeps the child's end path long enough for the racing
    /// waits to land inside it, deterministically.
    /// </summary>
    private static async Task RacedWaitDropsNotice()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), "netpi-racedwait-" + Guid.NewGuid().ToString("N") + ".db");
        using var db = TestSqlite.TryCreate(dbPath);
        if (db is null)
        {
            Console.WriteLine("        (skipped: no native sqlite library)");
            return;
        }
        await using var h = await TestHost.StartAsync(db: db);
        for (var i = 0; i < 10; i++)
        {
            var childGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var parentGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var report = "REPORT " + i;
            var gotReport = false;
            h.Catalog.Handler = (r, ct) =>
            {
                if (IsChild(r))
                    return Reply.Text(report, c => childGate.Task.WaitAsync(c));
                var tools = r.Messages.Where(m => m.Role == MessageRole.Tool).SelectMany(m => m.ToolResults).Select(x => x.Name).ToList();
                return tools.Count == 0
                    ? Reply.Tool("agent_spawn", new { task = "job " + i, name = "worker", background = true })
                    : Reply.Text("parent done " + i, c => parentGate.Task.WaitAsync(c));
            };
            var parent = h.NewSession();
            await h.SendAsync(parent.Id, "go " + i);
            var parentId = h.Runtime.GetBySession(parent.Id)!.Id;
            string childId;
            await Wait.Until(() => h.Runtime.GetBySession(parent.Id) is { } p && p.Children.Count == 1, "child spawned");
            childId = h.Runtime.GetBySession(parent.Id)!.Children.Single();

            // Waits that land right on the child's finish (the exact moment a parent's agent_wait call can land):
            // each spinner fires the moment the finished status flips, plus one fired by the status event. All of
            // them race the child's own notification path.
            void FireRacedWait()
            {
                try
                {
                    var r = h.Runtime.WaitAsync(parentId, [childId], yieldSlot: false, null, CancellationToken.None).GetAwaiter().GetResult();
                    if (r[0].Status == AgentStatus.Completed && r[0].Result == report)
                        lock (h) gotReport = true;
                }
                catch { }
            }
            var polls = new List<Task>(4);
            for (var w = 0; w < 4; w++)
                polls.Add(Task.Run(() =>
                {
                    while (true)
                    {
                        var st = h.Runtime.Get(childId)?.Status;
                        if (st is AgentStatus.Completed or AgentStatus.Failed or AgentStatus.Cancelled)
                        {
                            FireRacedWait();
                            return;
                        }
                        Thread.SpinWait(4);
                    }
                }));
            var sub = h.Bus.SubscribeAsync(EventTypes.AgentStatus, evt =>
            {
                var a = FakeBus.Data(evt)["agent"];
                if ((string?)a!["id"] == childId && (string?)a!["status"] == "completed")
                    FireRacedWait();
                return ValueTask.CompletedTask;
            });
            childGate.SetResult();
            await Task.WhenAll(polls);
            await h.StatusAsync(childId, AgentStatus.Completed);
            parentGate.SetResult();
            await h.IdleAsync(parent.Id, 15_000);
            sub.Dispose();
            bool got;
            lock (h) got = gotReport;
            Check.True(got, $"cycle {i}: the racing wait got the report");
            Check.False(h.Messages(parent.Id).Any(m => m.MetaString("kind") == "agent-result"),
                $"cycle {i}: the report went with the wait's result; the agent-result notice must not also reach the parent");
        }
    }

    /// <summary>
    /// The user's story test: a chat on "a" (2 instances, the chat holds one) starts three subagents in one call (which
    /// waits): one on "a", one more on "a" (it gets the chat's instance once the chat waits) and one on "b". All three
    /// run at the same time; the call returns every report.
    /// </summary>
    private static async Task BatchSpawn()
    {
        await using var h = await TestHost.StartAsync(x =>
        {
            x.Settings.SetQuiet("agents.a", JsonNode.Parse("""{ "model": "fake/local", "instances": 2 }"""));
            x.Settings.SetQuiet("agents.b", JsonNode.Parse("""{ "model": "cloud/big" }"""));
        });
        var concurrent = 0;
        var maxConcurrent = 0;
        var allRunning = new TaskCompletionSource();
        h.Catalog.Handler = (r, ct) =>
        {
            if (IsChild(r))
                return Reply.Text("story " + Reply.LastUser(r), async c =>
                {
                    var n = Interlocked.Increment(ref concurrent);
                    lock (h) maxConcurrent = Math.Max(maxConcurrent, n);
                    if (n == 3) allRunning.TrySetResult();
                    await allRunning.Task.WaitAsync(TimeSpan.FromSeconds(3), c).ContinueWith(_ => { }, TaskScheduler.Default);
                    Interlocked.Decrement(ref concurrent);
                });
            if (!Reply.HasToolResult(r))
                return Reply.Tool("agent_spawn", new
                {
                    subagents = new object[]
                    {
                        new { task = "part one", name = "one", agent = "a" },
                        new { task = "part two", name = "two", agent = "a" },
                        new { task = "part three", name = "three", agent = "b" },
                    },
                });
            return Reply.Text("all done");
        };
        var parent = h.NewSession(model: "fake/local");
        await h.SendAsync(parent.Id, "write it with all agents");
        var p = await h.IdleAsync(parent.Id, 15_000);
        Check.Equal(3, maxConcurrent, "the three subagents ran at the same time");
        Check.Equal(3, p.Children.Count);
        var result = h.Messages(parent.Id).Where(m => m.Role == MessageRole.Tool).Select(m => m.ToolResults.Single()).Single();
        Check.Contains(result.Content, "3 subagents finished.");
        foreach (var part in new[] { "part one", "part two", "part three" }) Check.Contains(result.Content, "story " + part);
        Check.Equal(3, ((JsonArray)result.Details!["agents"]!).Count);
        var agents = h.Runtime.List(true).Where(a => a.IsSubagent).ToDictionary(a => a.Name, a => a.Agent);
        Check.Equal("a,a,b", string.Join(",", new[] { "one", "two", "three" }.Select(n => agents[n])));
        Check.False(h.Messages(parent.Id).Any(m => m.MetaString("kind") == "agent-result"), "the reports came with the call");
        Check.Equal("all done", h.Messages(parent.Id)[^1].Text);
        Check.Equal(0, h.Scheduler!.Snapshot().Sum(s => s.Busy + s.Queued), "every slot released");
    }

    private static async Task BatchRefused()
    {
        await using var h = await TestHost.StartAsync(x => x.Settings.SetQuiet("agents.a", JsonNode.Parse("""{ "model": "fake/local" }""")));
        string? refused = null;
        h.Catalog.Handler = (r, ct) =>
        {
            if (!Reply.HasToolResult(r))
                return Reply.Tool("agent_spawn", new { subagents = new object[] { new { task = "fine", agent = "a" }, new { task = "wrong", name = "w", agent = "nope" } } });
            refused = r.Messages[^1].ToolResults.Single().Content;
            return Reply.Text("ok");
        };
        var parent = h.NewSession();
        await h.SendAsync(parent.Id, "go");
        await h.IdleAsync(parent.Id);
        Check.Contains(refused, "subagents[1] (w): There is no agent \"nope\".");
        Check.Contains(refused, "None of them was started.");
        Check.False(h.Runtime.List(true).Any(a => a.IsSubagent), "nothing started");
    }

    private static bool IsChild(ModelRequest r) => r.SystemPrompt?.Contains("a subagent working for") == true;

    private static async Task SpawnWaitYield()
    {
        await using var h = await TestHost.StartAsync();
        var parent = h.NewSession(model: "fake/solo"); // 1 slot
        AgentStatus? parentStatusDuringChild = null;
        List<SlotHolder>? ownersDuringChild = null;
        string? childPrompt = null;
        h.Catalog.Handler = (r, ct) =>
        {
            if (IsChild(r))
            {
                childPrompt = r.SystemPrompt;
                parentStatusDuringChild = h.Runtime.GetBySession(parent.Id)!.Status;
                ownersDuringChild = h.Scheduler!.Snapshot().Single(p => p.Key == "fake/solo").Owners;
                return Reply.Text("REPORT: 42 files");
            }
            return Reply.HasToolResult(r)
                ? Reply.Text("Parent done: " + r.Messages[^1].ToolResults.Single().Content)
                : Reply.Tool("agent_spawn", new { task = "Count the files.", name = "counter" });
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
        Check.Equal(0, h.Scheduler!.Snapshot().Where(x => x.Key == "fake/solo").Sum(x => x.Busy));
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
            return Reply.HasToolResult(r) ? Reply.Text("spawned, carrying on") : Reply.Tool("agent_spawn", new { task = "background job", name = "bg", background = true });
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
                1 => Reply.Tool("agent_spawn", new { task = "quick job", name = "quick", background = true }),
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

    private static async Task WaitManyOneSlot()
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
                    Reply.Call("agent_spawn", new { task = "job A", name = "a", background = true }),
                    Reply.Call("agent_spawn", new { task = "job B", name = "b", background = true }),
                    Reply.Call("agent_spawn", new { task = "job C", name = "c", background = true }));
            var last = r.Messages[^1].ToolResults.Last();
            return last.Name == "agent" ? Reply.Text("summary") : Reply.Tool("agent", new { action = "wait" });
        };
        await h.SendAsync(parent.Id, "fan out");
        var p = await h.IdleAsync(parent.Id, 15_000);
        Check.Equal(3, p.Children.Count);
        Check.Equal(1, maxConcurrent, "one slot: children ran one at a time");
        var wait = h.Messages(parent.Id).Where(m => m.Role == MessageRole.Tool).Select(m => m.ToolResults.Single()).Single(x => x.Name == "agent");
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
            return Reply.Tool("agent_spawn", new { task = "long job", name = "long" });
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
                return Reply.HasToolResult(r) ? Reply.Text("child final") : Reply.Tool("agent", new { action = "send", to = "parent", message = "progress: 50%" });
            if (Reply.LastUser(r).Contains("<agent-message")) return Reply.Text("noted progress");
            if (Reply.LastUser(r).Contains("<agent-result")) return Reply.Text("noted result");
            return Reply.HasToolResult(r) ? Reply.Text("spawned") : Reply.Tool("agent_spawn", new { task = "report progress", name = "reporter", background = true });
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
    /// The user's setup: a "stealth" agent on one model of a cloud provider; the chat runs on the local agent and delegates
    /// with agent_spawn { agent: "stealth" }. No per-agent permission is involved: provider credentials are global and any
    /// run may use any agent (limits: agents.maxDepth, instances, the budget).
    /// </summary>
    private static async Task SpawnOntoConfiguredPool()
    {
        await using var h = await TestHost.StartAsync(x =>
        {
            x.Settings.SetQuiet("agents.main", JsonNode.Parse("""{ "model": "fake/local" }"""));
            x.Settings.SetQuiet("agents.stealth", JsonNode.Parse("""{ "model": "cloud/big", "use": "Research." }"""));
        });
        h.Catalog.Handler = (r, ct) =>
        {
            if (r.Model.Ref == "cloud/big") return Reply.Text("child report: done on the stealth agent");
            var last = r.Messages[^1];
            if (last.Role == MessageRole.Tool) return Reply.Text("spawned");
            if (last.Text.Contains("<agent-result")) return Reply.Text("thanks");
            return Reply.Tool("agent_spawn", new { task = "Look something up and report.", agent = "stealth", background = true });
        };
        var parent = h.NewSession(); // default model: fake/local
        await h.SendAsync(parent.Id, "delegate it");
        var p = h.Runtime.GetBySession(parent.Id)!;
        await Wait.Until(() => h.Runtime.Get(p.Id)?.Children.Count == 1, "child spawned");
        var child = await h.StatusAsync(h.Runtime.Get(p.Id)!.Children[0], AgentStatus.Completed);
        Check.Equal("cloud/big", child.Model);
        Check.Equal("stealth", child.Agent);
        Check.Equal("child report: done on the stealth agent", child.Result);
        Check.True(h.Scheduler!.Snapshot().Any(x => x.Key == "stealth" && x.Capacity == 1), "the agent with its instances");
        Check.Equal("stealth", SessionAgent.Of(h.Sessions.GetSession(child.SessionId)), "the subagent's chat keeps its agent");
        Check.Equal("main", SessionAgent.Of(h.Sessions.GetSession(parent.Id)));
        await Wait.Until(() => h.Messages(parent.Id).Any(m => m.Role == MessageRole.Assistant && m.Text == "thanks"), "the parent got the report");
        Check.Equal("fake/local", h.Catalog.Requests.First(r => r.SessionId == parent.Id).Model.Ref);
    }

    private static async Task InheritEffectiveModel()
    {
        foreach (var defaultRef in new string?[] { "fake/local", null })
        {
            await using var h = await TestHost.StartAsync(x =>
            {
                x.Settings.SetQuiet("agents.main", JsonNode.Parse("""{ "model": "fake/local" }"""));
                x.Settings.SetQuiet("agents.stealth", JsonNode.Parse("""{ "model": "cloud/big", "instances": 2 }"""));
                x.Catalog.DefaultModelRef = defaultRef;
            });
            var parent = h.Sessions.CreateSession(new SessionInfo
            {
                Title = "named agent", Reasoning = "high", Meta = new JsonObject { [SessionAgent.MetaKey] = "stealth" },
            });
            h.Catalog.Handler = (r, ct) => Reply.Text("done");
            await h.SendAsync(parent.Id, "hello");
            var p = await h.IdleAsync(parent.Id);
            Check.Equal("cloud/big", h.Catalog.Requests.First().Model.Ref);
            Check.True(h.Sessions.GetSession(parent.Id)!.Model is null, "selection remains in meta.agent");
            var child = await h.Runtime.SpawnAsync(new SpawnRequest { Task = "inherited", ParentAgentId = p.Id, NotifyParent = false });
            await h.StatusAsync(child.Id, AgentStatus.Completed);
            var request = h.Catalog.Requests.Single(r => r.SessionId == child.SessionId);
            Check.Equal("cloud/big", request.Model.Ref);
            Check.Equal("high", request.ReasoningEffort);

            // A bare id naming the same model inherits effort; an explicit different model does not.
            var same = await h.Runtime.SpawnAsync(new SpawnRequest { Task = "same", Model = "big", ParentAgentId = p.Id, NotifyParent = false });
            await h.StatusAsync(same.Id, AgentStatus.Completed);
            Check.Equal("high", h.Catalog.Requests.Single(r => r.SessionId == same.SessionId).ReasoningEffort);
            var other = await h.Runtime.SpawnAsync(new SpawnRequest { Task = "other", Model = "fake/local", Reasoning = "low", ParentAgentId = p.Id, NotifyParent = false });
            await h.StatusAsync(other.Id, AgentStatus.Completed);
            Check.Equal("fake/local", h.Catalog.Requests.Single(r => r.SessionId == other.SessionId).Model.Ref);
            Check.Equal("low", h.Catalog.Requests.Single(r => r.SessionId == other.SessionId).ReasoningEffort);
        }
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

        // another model; tool allowlist
        var child = await h.Runtime.SpawnAsync(new SpawnRequest
        {
            Task = "cloud work", Model = "cloud/big", Tools = ["echo", "agent_spawn", "agent"], ParentAgentId = p.Id, Instructions = "Be extra careful.",
        });
        Check.Equal("cloud/big", child.Model);
        Check.Equal("agent-1", child.Name);
        await Wait.Until(() => h.Catalog.Requests.Any(r => r.SessionId == child.SessionId), "child called the model");
        var req = h.Catalog.Requests.First(r => r.SessionId == child.SessionId);
        Check.Equal("cloud/big", req.Model.Ref);
        var names = req.Tools.Select(x => x.Name).OrderBy(x => x).ToList();
        Check.Equal("agent,echo", string.Join(",", names), "allowlist + no agent_spawn at max depth (agent stays, to send to the parent)");
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
            return Reply.Tool("agent_spawn", new { task = "hang", name = "hanger" });
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
        Check.Equal(0, h.Scheduler!.Snapshot().Sum(x => x.Busy + x.Queued));
    }

    private static async Task FailedChild()
    {
        await using var h = await TestHost.StartAsync();
        var parent = h.NewSession();
        h.Catalog.Handler = (r, ct) =>
        {
            if (IsChild(r)) return Reply.Fail(new ModelException("child model broke", false, 503));
            return Reply.HasToolResult(r) ? Reply.Text("handled") : Reply.Tool("agent_spawn", new { task = "doomed", name = "doomed" });
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
        string? listOutput = null, choicesOutput = null, resultOutput = null, cancelOutput = null;
        var step = 0;
        string? asked = null;  // the agent action of the last call
        h.Catalog.Handler = (r, ct) =>
        {
            if (IsChild(r)) return Reply.Text("child result", c => gate.Task.WaitAsync(c));
            var last = Reply.HasToolResult(r) ? r.Messages[^1].ToolResults.Last() : null;
            switch (last?.Name == "agent" ? asked : last?.Name)
            {
                case "list": listOutput = last!.Content; break;
                case "agent_choices": choicesOutput = last!.Content; break;
                case "result": resultOutput = last!.Content; break;
                case "cancel": cancelOutput = last!.Content; break;
            }
            IAsyncEnumerable<ModelStreamEvent> Agent(string action, string? id = null) { asked = action; return id is null ? Reply.Tool("agent", new { action }) : Reply.Tool("agent", new { action, id }); }
            return Interlocked.Increment(ref step) switch
            {
                1 => Reply.Tool("agent_spawn", new { task = "slow job", name = "slowpoke", background = true }),
                2 => Agent("list"),
                3 => Reply.Tool("agent_choices", new { }),
                4 => Agent("result", "slowpoke"),
                5 => Agent("cancel", "slowpoke"),
                _ => Reply.Text("done"),
            };
        };
        await h.SendAsync(parent.Id, "go");
        await h.IdleAsync(parent.Id);
        Check.Contains(listOutput, "slowpoke");
        Check.Contains(listOutput, "task: slow job");
        Check.Contains(choicesOutput, "No agents are set up: a subagent runs on your model unless you pass agent_spawn a model ref.");
        Check.Contains(choicesOutput, "- fake/local · 2/2 busy");
        Check.Contains(choicesOutput, "main (you)");
        Check.Contains(resultOutput, "still running");
        Check.Contains(cancelOutput, "Cancelled slowpoke");
        var child = h.Runtime.List().Single(a => a.IsSubagent);
        Check.Equal(AgentStatus.Cancelled, child.Status);
    }
}
