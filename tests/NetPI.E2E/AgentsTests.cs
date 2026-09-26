using System.Text.Json;

namespace NetPI.E2E;

/// <summary>Agents, subagents on their slots, parent yielding, agent-result notices, ideas and the work snapshot.</summary>
public static class AgentsTests
{
    public static void Register(TestRunner r, Env env)
    {
        r.Add("agents: set up in settings, active while their model is loaded; a chat on an agent; switched off; a stopped backend", async () =>
        {
            const string stopped = "aiproxy/qwen38-27b-iq3s";
            await env.Rpc("settings.set", new { path = "agents.e2e-qwen", value = new { model = CoreTests.Qwen, use = "The loaded one." } });
            await env.Rpc("settings.set", new { path = "agents.e2e-iq3", value = new { model = stopped } });
            try
            {
                var pools = (await env.Rpc("agents.list")).Arr().ToList();
                var q = pools.FirstOrDefault(p => p.S("key") == "e2e-qwen");
                Check.True(q.B("configured") && q.B("available"), "e2e-qwen active: " + q.GetRawText());
                Check.Equal(2L, q.L("capacity"), "instances: the model's slots");
                Check.Equal("idle", q.S("status"));
                Check.Equal("The loaded one.", q.S("use"));
                var iq = pools.FirstOrDefault(p => p.S("key") == "e2e-iq3");
                Check.False(iq.B("available"), "the stopped backend's agent is inactive");
                Check.Contains(iq.S("unavailable"), "isn't loaded");

                // a chat on an agent: its model follows
                var sid = (await env.NewSession()).S("id")!;
                var used = await env.Rpc("agents.use", new { sessionId = sid, agent = "e2e-qwen" });
                Check.Equal(CoreTests.Qwen, used.S("model"));
                Check.Equal("e2e-qwen", used.P("meta").S("agent"));
                var run = await env.Run(sid, "hello [s:echo]");
                Check.Contains(run.FinalText, "ECHO-DONE");
                Check.True(run.OfType("agent.status").Any(e => e.D.P("agent").S("agent") == "e2e-qwen"), "ran on the agent");

                // switched off: the chat stops at once with a notice
                var mark = env.Client.Mark();
                var listed = await env.Rpc("agents.setEnabled", new { id = "e2e-qwen", enabled = false });
                Check.True(listed.Arr().First(p => p.S("key") == "e2e-qwen").B("disabled"));
                await env.Client.WaitFor(mark, e => e.Type == "agents.changed" && e.D.Arr("agents").Any(p => p.S("key") == "e2e-qwen" && p.B("disabled")), "agents.changed: switched off", 5000);
                var off = await env.Run(sid, "again [s:echo]");
                Check.Contains(RunResult.Text(off.Role("notice").Last(n => n.P("meta").S("kind") == "error")), "The agent \"e2e-qwen\" is disabled.");
                await env.Rpc("agents.setEnabled", new { id = "e2e-qwen", enabled = true });

                // an agent whose model isn't loaded: refused at once, the backend never sees a request
                await env.Rpc("agents.use", new { sessionId = sid, agent = "e2e-iq3" });
                var mockMark = await env.MockMark();
                var notLoaded = await env.Run(sid, "hi [s:echo]");
                Check.Contains(RunResult.Text(notLoaded.Role("notice").Last(n => n.P("meta").S("kind") == "error")), "isn't loaded");
                Check.Equal(0, (await env.MockLog(mockMark)).Count, "no request reached the backend");

                // a chat on a model no agent runs
                var claude = await env.NewSession(model: "anthropic/claude-haiku-4-5");
                var none = await env.Run(claude.S("id")!, "hi [s:echo]");
                Check.Contains(RunResult.Text(none.Role("notice").Last(n => n.P("meta").S("kind") == "error")), "No agent runs anthropic/claude-haiku-4-5.");
            }
            finally
            {
                await env.Rpc("settings.set", new { path = "agents.e2e-qwen", value = (object?)null });
                await env.Rpc("settings.set", new { path = "agents.e2e-iq3", value = (object?)null });
            }
        }, 60);

        r.Add("slots: 3 subagents on qwen (2 slots): max 2 concurrent at the backend, queueing, parent yields and resumes", async () =>
        {
            var s = await env.NewSession(model: CoreTests.Qwen);
            var sid = s.S("id")!;
            // make sure nothing else is running on the qwen pool
            await Wait.UntilAsync(async () => (await env.Rpc("agents.list")).Arr().FirstOrDefault(p => p.S("key") == CoreTests.Qwen).L("busy") == 0 ? "ok" : null, "idle qwen pool");
            await env.MockReset();
            var mark = env.Client.Mark();
            var agent = await env.Rpc("agent.send", new { sessionId = sid, text = "Square 1..3 in parallel [s:spawn n=3 delay=2500]" });
            var parentId = agent.S("id")!;

            // mid-flight: one worker queued while the parent holds a slot, then the parent yields
            var queuedEv = await env.Client.WaitFor(mark, e => e.Type == "agent.status" && e.D.P("agent").B("isSubagent")
                                                                && e.D.P("agent").S("status") == "queued", "a queued subagent", 20_000);
            Check.Equal(parentId, queuedEv.D.P("agent").S("parentAgentId"));
            var yielded = await env.Client.WaitFor(mark, e => e.Type == "agent.status" && e.D.P("agent").S("id") == parentId
                                                              && e.D.P("agent").S("status") == "yielded", "parent yields its slot", 20_000);
            Check.Contains(yielded.D.P("agent").S("activity"), "waiting for 3 agents");

            // snapshots while the workers run
            var pools = await env.Rpc("agents.list");
            var pool = pools.Arr().First(p => p.S("key") == CoreTests.Qwen);
            Check.Equal(2L, pool.L("capacity"));
            Check.True(pool.L("busy") <= 2, "busy <= capacity");
            var work = await env.Rpc("work.snapshot");
            foreach (var part in new[] { "agents", "runs", "processes", "usage" })
                Check.True(work.P(part).ValueKind != JsonValueKind.Null, $"work.snapshot.{part} available: {work.S("errors")}");
            var workAgents = work.Arr("runs").ToList();
            var parent = workAgents.First(a => a.S("id") == parentId);
            Check.Equal(3, parent.Arr("children").Count());
            var children = workAgents.Where(a => a.S("parentAgentId") == parentId).ToList();
            Check.Equal(3, children.Count);
            Check.True(children.All(c => c.B("isSubagent") && c.L("depth") == 1 && c.S("agent") == CoreTests.Qwen), "children on the qwen slots");
            Check.True(work.P("agents").Arr().Any(p => p.S("key") == CoreTests.Qwen), "work.snapshot agents");

            var done = await env.WaitIdle(sid, mark, agent.L("runs"), 60_000);
            var run = await env.Result(sid, mark, done);
            Check.Contains(run.FinalText, "SPAWN-DONE");
            for (var i = 1; i <= 3; i++) Check.Contains(run.FinalText, $"Report from worker-{i}: {i} squared is {i * i}");
            var calls = run.Parts("tool_call").Select(c => c.S("name")).ToList();
            Check.Equal("agent_spawn,agent_spawn,agent_spawn,agent_wait", string.Join(",", calls));
            Check.True(run.Parts("tool_result").All(x => !x.B("isError")), "tool results ok: " + string.Join(" | ", run.Parts("tool_result").Select(x => x.S("content"))));

            // backend saw at most 2 concurrent requests, and actually 2 (parallel work happened)
            var stats = await env.MockStats();
            var q = stats.P("models").P("qwen3.8-27b");
            Check.Equal(2L, q.L("maxInflight"), "max concurrency at the backend = the model's slots");
            Check.Equal(0L, q.L("overCapacity"));
            var log = await env.MockLog();
            Check.Equal(3, log.Count(e => e.B("subagent")), "one request per subagent");

            // statuses and sessions
            var agents = await env.Rpc("runs.list", new { includeFinished = true });
            var subs = agents.Arr().Where(a => a.S("parentAgentId") == parentId).ToList();
            Check.True(subs.All(a => a.S("status") == "completed"), "subagents completed");
            Check.True(subs.All(a => (a.S("result") ?? "").StartsWith("Report from")), "results recorded");
            Check.True(env.Client.Since(mark).Any(e => e.Type == "agent.status" && e.D.P("agent").S("id") == parentId
                                                        && e.D.P("agent").S("status") == "running"
                                                        && env.Client.Since(yielded.Index).Contains(e)), "parent running again after the yield");
            var sessions = await env.Rpc("sessions.list", new { includeSubagents = true, parentSessionId = sid });
            Check.Equal(3, sessions.Arr().Count(x => x.S("kind") == "subagent"), "3 subagent sessions under the parent");
            // nobody got agent-result notices: the parent consumed the results with agent_wait
            Check.False(run.Role("notice").Any(n => n.P("meta").S("kind") == "agent-result"), "no duplicate agent-result notices");
            var slots = await env.Rpc("agents.list");
            Check.Equal(0L, slots.Arr().Where(p => p.S("key") == CoreTests.Qwen).Sum(p => p.L("busy")), "all slots released");
            Check.True(env.Client.Since(mark).Any(e => e.Type == "agents.changed"), "agents.changed events");
        }, 120);

        r.Add("nested subagents: orchestrator → lead → helper (the lead's spawn waits) on a model with 2 slots, no deadlock, agent_send", async () =>
        {
            await Wait.UntilAsync(async () => (await env.Rpc("agents.list")).Arr().FirstOrDefault(p => p.S("key") == CoreTests.Qwen).L("busy") == 0 ? "ok" : null, "idle qwen pool");
            await env.MockReset();
            var s = await env.NewSession(model: CoreTests.Qwen);
            var sid = s.S("id")!;
            var run = await env.Run(sid, "Nested delegation [s:nest delay=800]", 45_000);
            Check.Contains(run.FinalText, "NEST-DONE");
            Check.Contains(run.FinalText, "Report from helper: 5 squared is 25");
            var parent = run.Final;
            var agents = (await env.Rpc("runs.list", new { includeFinished = true })).Arr().ToList();
            var lead = agents.Single(a => a.S("parentAgentId") == parent.S("id"));
            var helper = agents.Single(a => a.S("parentAgentId") == lead.S("id"));
            Check.Equal("lead", lead.S("name"));
            Check.Equal(1L, lead.L("depth"));
            Check.Equal(2L, helper.L("depth"));
            Check.Equal("completed", helper.S("status"));
            Check.Equal("completed", lead.S("status"));
            // the lead's agent_send reached the parent as an agent-message notice
            var msg = run.Role("notice").FirstOrDefault(n => n.P("meta").S("kind") == "agent-message");
            Check.True(msg.ValueKind == JsonValueKind.Object, "agent-message notice in the parent: " + string.Join(",", run.Role("notice").Select(n => n.P("meta").S("kind"))));
            Check.Contains(RunResult.Text(msg), "lead started");
            Check.Equal("lead", msg.P("meta").S("agentName"));
            var q = (await env.MockStats()).P("models").P("qwen3.8-27b");
            Check.True(q.L("maxInflight") <= 2, "never more than 2 requests in flight");
            Check.Equal(0L, q.L("overCapacity"));
            Check.Equal(0L, (await env.Rpc("agents.list")).Arr().Where(p => p.S("key") == CoreTests.Qwen).Sum(p => p.L("busy")), "all slots released");
        }, 90);

        r.Add("agent-result: a background subagent's report wakes the idle parent", async () =>
        {
            var s = await env.NewSession();
            var sid = s.S("id")!;
            var mark = env.Client.Mark();
            var agent = await env.Rpc("agent.send", new { sessionId = sid, text = "Start a background job [s:spawnbg delay=1500]" });
            var first = await env.WaitIdle(sid, mark, agent.L("runs"));
            // the report arrives as a notice and starts a new run of the parent
            var notice = await env.Client.WaitFor(mark, e => e.Type == "message.added" && e.Sid == sid && e.D.P("message").P("meta").S("kind") == "agent-result",
                "agent-result notice", 30_000);
            var meta = notice.D.P("message").P("meta");
            Check.Equal("bg-worker", meta.S("agentName"));
            Check.Equal("completed", meta.S("status"));
            Check.Contains(RunResult.Text(notice.D.P("message")), "Report from bg-worker: 7 squared is 49");
            var done = await env.WaitIdle(sid, first.Index, agent.L("runs") + 1, 30_000);
            var run = await env.Result(sid, mark, done);
            Check.Contains(run.FinalText, "AGENT-RESULT-RECEIVED");
            Check.Contains(run.FinalText, "7 squared is 49");
            Check.Equal("user,assistant,tool,assistant,notice,assistant", string.Join(",", run.Messages.Select(m => m.S("role"))));
        }, 90);

        r.Add("ask_user: the question waits with its agent yielded, every window hears of it, ask.answer goes on", async () =>
        {
            var s = await env.NewSession();
            var sid = s.S("id")!;
            var mark = env.Client.Mark();
            var agent = await env.Rpc("agent.send", new { sessionId = sid, text = "Pick one [s:ask]" });
            var answered = false;
            try
            {
                var asked = await env.Client.WaitFor(mark, e => e.Type == "ask.asked" && e.D.S("sessionId") == sid, "ask.asked", 20_000);
                Check.True(asked.Sid is null, "ask.asked is unscoped");
                var callId = asked.D.S("callId")!;
                Check.Equal("Which way?", asked.D.Arr("questions").Single().S("question"));
                Check.Equal("Thorough", asked.D.Arr("questions").Single().Arr("options").Last().S("label"));
                // the question is out a moment before the runtime shows the agent as yielded
                var yielded = await env.Client.WaitFor(mark, e => e.Type == "agent.status" && e.D.P("agent").S("sessionId") == sid
                                                                  && e.D.P("agent").S("status") == "yielded", "the agent yielded", 5000);
                Check.Equal("waiting for your answer", yielded.D.P("agent").S("activity"));
                Check.Equal(callId, (await env.Rpc("ask.pending", new { sessionId = sid })).Arr().Single().S("callId"));
                Check.True((await env.Rpc("ask.answer", new { callId, answers = new[] { new[] { "Thorough" } } })).GetBoolean());
                answered = true;
            }
            finally
            {
                if (!answered) await env.Rpc("agent.abort", new { sessionId = sid }); // a question left waiting holds its run
            }
            var done = await env.WaitIdle(sid, mark, agent.L("runs"), 30_000);
            var run = await env.Result(sid, mark, done);
            Check.Contains(run.FinalText, "Answer received: The user answered: Thorough");
            Check.Contains(run.FinalText, "ASK-DONE");
            var closed = await env.Client.WaitFor(mark, e => e.Type == "ask.closed" && e.D.S("sessionId") == sid, "ask.closed", 5000);
            Check.Equal("answered", closed.D.S("status"));
            Check.Equal(0, (await env.Rpc("ask.pending", new { })).Arr().Count());
        });

        r.Add("ideas: the ideas tool writes the global ~/.netpi/ideas.json (stamped with the project); ideas.list and ideas.changed see it", async () =>
        {
            var p = await env.NewProject("ideas");
            var s = await env.NewSession(projectId: p.S("id"));
            var sid = s.S("id")!;
            var mark = env.Client.Mark();
            var run = await env.Run(sid, "Remember this [s:ideas title=\"Cache the model list\"]");
            Check.Contains(run.FinalText, "IDEAS-DONE");
            var file = Path.Combine(env.Home, "ideas.json");
            Check.True(File.Exists(file), "the global ideas file");
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            var idea = doc.RootElement.Arr("ideas").Single();
            Check.Equal("Cache the model list", idea.S("title"));
            Check.Equal("high", idea.S("priority"));
            Check.True((idea.S("createdBy") ?? "").StartsWith("agent:"), "createdBy agent");
            Check.Equal("research", idea.Arr("sections").Single().S("kind"));
            Check.Equal(p.S("id"), idea.P("project").S("id"), "stamped with the session's project");
            Check.Equal(p.S("name"), idea.P("project").S("name"));
            var list = await env.Rpc("ideas.list", new { });
            Check.Equal(file, list.S("file"));
            Check.Equal(idea.S("id"), list.Arr("ideas").Single().S("id"));
            var ev = await env.Client.WaitFor(mark, e => e.Type == "ideas.changed" && e.D.S("file") == file, "ideas.changed", 5000);
            Check.True(ev is not null);
            // the RPC side writes the same file
            await env.Rpc("ideas.add", new { projectId = p.S("id"), idea = new { title = "From the UI", tags = "ui, e2e" } });
            using var doc2 = JsonDocument.Parse(File.ReadAllText(file));
            Check.Equal(2, doc2.RootElement.Arr("ideas").Count());
        });
    }
}
