using System.Text.Json;

namespace NetPI.E2E;

/// <summary>Subagents on lanes, parent yielding, agent-result notices, ideas and the work snapshot.</summary>
public static class LaneTests
{
    public static void Register(TestRunner r, Env env)
    {
        r.Add("lanes: 3 subagents on qwen (capacity 2): max 2 concurrent at the backend, queueing, parent yields and resumes", async () =>
        {
            var s = await env.NewSession(model: CoreTests.Qwen);
            var sid = s.S("id")!;
            // make sure nothing else is running on the qwen pool
            await Wait.UntilAsync(async () => (await env.Rpc("lanes.list")).Arr().FirstOrDefault(p => p.S("key") == CoreTests.Qwen).L("busy") == 0 ? "ok" : null, "idle qwen pool");
            await env.MockReset();
            var mark = env.Client.Mark();
            var agent = await env.Rpc("agent.send", new { sessionId = sid, text = "Square 1..3 in parallel [s:spawn n=3 delay=2500]" });
            var parentId = agent.S("id")!;

            // mid-flight: one worker queued while the parent holds a lane, then the parent yields
            var queuedEv = await env.Client.WaitFor(mark, e => e.Type == "agent.status" && e.D.P("agent").B("isSubagent")
                                                                && e.D.P("agent").S("status") == "queued", "a queued subagent", 20_000);
            Check.Equal(parentId, queuedEv.D.P("agent").S("parentAgentId"));
            var yielded = await env.Client.WaitFor(mark, e => e.Type == "agent.status" && e.D.P("agent").S("id") == parentId
                                                              && e.D.P("agent").S("status") == "yielded", "parent yields its lane", 20_000);
            Check.Contains(yielded.D.P("agent").S("activity"), "waiting for 3 agents");

            // snapshots while the workers run
            var pools = await env.Rpc("lanes.list");
            var pool = pools.Arr().First(p => p.S("key") == CoreTests.Qwen);
            Check.Equal(2L, pool.L("capacity"));
            Check.True(pool.L("busy") <= 2, "busy <= capacity");
            var work = await env.Rpc("work.snapshot");
            foreach (var part in new[] { "lanes", "agents", "processes", "usage" })
                Check.True(work.P(part).ValueKind != JsonValueKind.Null, $"work.snapshot.{part} available: {work.S("errors")}");
            var workAgents = work.Arr("agents").ToList();
            var parent = workAgents.First(a => a.S("id") == parentId);
            Check.Equal(3, parent.Arr("children").Count());
            var children = workAgents.Where(a => a.S("parentAgentId") == parentId).ToList();
            Check.Equal(3, children.Count);
            Check.True(children.All(c => c.B("isSubagent") && c.L("depth") == 1 && c.S("pool") == CoreTests.Qwen), "children on the qwen pool");
            Check.True(work.P("lanes").Arr().Any(p => p.S("key") == CoreTests.Qwen), "work.snapshot lanes");

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
            Check.Equal(2L, q.L("maxInflight"), "max concurrency at the backend = lane capacity");
            Check.Equal(0L, q.L("overCapacity"));
            var log = await env.MockLog();
            Check.Equal(3, log.Count(e => e.B("subagent")), "one request per subagent");

            // statuses and sessions
            var agents = await env.Rpc("agents.list", new { includeFinished = true });
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
            var lanes = await env.Rpc("lanes.list");
            Check.Equal(0L, lanes.Arr().First(p => p.S("key") == CoreTests.Qwen).L("busy"), "all lanes released");
            Check.True(env.Client.Since(mark).Any(e => e.Type == "lanes.changed"), "lanes.changed events");
        }, 120);

        r.Add("nested subagents: orchestrator → lead → helper (spawn wait=true) on one 2-lane pool, no deadlock, agent_send", async () =>
        {
            await Wait.UntilAsync(async () => (await env.Rpc("lanes.list")).Arr().FirstOrDefault(p => p.S("key") == CoreTests.Qwen).L("busy") == 0 ? "ok" : null, "idle qwen pool");
            await env.MockReset();
            var s = await env.NewSession(model: CoreTests.Qwen);
            var sid = s.S("id")!;
            var run = await env.Run(sid, "Nested delegation [s:nest delay=800]", 45_000);
            Check.Contains(run.FinalText, "NEST-DONE");
            Check.Contains(run.FinalText, "Report from helper: 5 squared is 25");
            var parent = run.Final;
            var agents = (await env.Rpc("agents.list", new { includeFinished = true })).Arr().ToList();
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
            Check.Equal(0L, (await env.Rpc("lanes.list")).Arr().First(p => p.S("key") == CoreTests.Qwen).L("busy"), "all lanes released");
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

        r.Add("ideas: idea_add writes ideas.json in the project, ideas.list and ideas.changed see it", async () =>
        {
            var p = await env.NewProject("ideas");
            var dir = p.S("path")!;
            var s = await env.NewSession(projectId: p.S("id"));
            var sid = s.S("id")!;
            var mark = env.Client.Mark();
            var run = await env.Run(sid, "Remember this [s:ideas title=\"Cache the model list\"]");
            Check.Contains(run.FinalText, "IDEAS-DONE");
            var file = Path.Combine(dir, "ideas.json");
            Check.True(File.Exists(file), "ideas.json in the project folder");
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            var idea = doc.RootElement.Arr("ideas").Single();
            Check.Equal("Cache the model list", idea.S("title"));
            Check.Equal("high", idea.S("priority"));
            Check.True((idea.S("createdBy") ?? "").StartsWith("agent:"), "createdBy agent");
            Check.Equal("research", idea.Arr("sections").Single().S("kind"));
            var list = await env.Rpc("ideas.list", new { sessionId = sid });
            Check.Equal("project", list.S("scope"));
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
