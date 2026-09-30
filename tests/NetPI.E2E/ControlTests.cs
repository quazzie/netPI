using System.Text.Json;

namespace NetPI.E2E;

/// <summary>Steering, follow-up queue, abort and switching the project mid-session.</summary>
public static class ControlTests
{
    public static void Register(TestRunner r, Env env)
    {
        r.Add("control.steer-batch", "steer: message during a tool batch skips the remaining calls and is delivered next turn", async () =>
        {
            var s = await env.NewSession();
            var sid = s.S("id")!;
            var mark = env.Client.Mark();
            var agent = await env.Rpc("agent.send", new { sessionId = sid, text = "Run the commands [s:slowtools]" });
            // wait until the first command (sleep 2) is running, then steer
            await env.Client.WaitFor(mark, e => e.Type == "tool.start" && e.Sid == sid && (e.D.S("arguments") ?? "").Contains("sleep 2"), "first bash call", 20_000);
            var steered = await env.Rpc("agent.send", new { sessionId = sid, text = "Stop and read this first [s:echo]", mode = "steer" });
            Check.Equal("running", steered.S("status"), "still running when steered");
            Check.Equal(1L, steered.L("queuedMessages"));
            var queueEv = await env.Client.WaitFor(mark, e => e.Type == "agent.queue" && e.Sid == sid && e.D.Arr("items").Any(), "agent.queue with the steer");
            Check.Equal("steer", queueEv.D.Arr("items").First().S("mode"));
            var queue = await env.Rpc("agent.queue", new { sessionId = sid });
            Check.True(queue.Arr().Count() <= 1, "agent.queue RPC");

            var done = await env.WaitIdle(sid, mark, agent.L("runs"));
            var run = await env.Result(sid, mark, done);
            var results = run.Parts("tool_result").ToList();
            Check.Equal(3, results.Count, "one result per call");
            Check.Contains(results[0].S("content"), "first");
            Check.False(results[0].B("isError"));
            Check.True(results.Skip(1).All(x => x.B("isError") && (x.S("content") ?? "").StartsWith("Skipped: a new message arrived")), "calls 2 and 3 skipped");
            // the steer message is persisted after the tool results, flagged as steering, and answered
            var roles = string.Join(",", run.Messages.Select(m => m.S("role")));
            Check.Equal("user,assistant,tool,tool,tool,user,assistant", roles);
            var steer = run.Messages[5];
            Check.Equal("steer", steer.P("meta").S("kind"));
            Check.Contains(run.FinalText, "Stop and read this first");
            Check.Contains(run.FinalText, "ECHO-DONE");
            Check.Equal(0L, run.Final.L("queuedMessages"));
            Check.Equal(agent.L("runs"), run.Final.L("runs"), "the steer did not need a second run");
        });

        r.Add("control.queue", "queue: follow-up sent during a run is delivered after the answer, in the same run", async () =>
        {
            var s = await env.NewSession();
            var sid = s.S("id")!;
            var mark = env.Client.Mark();
            var agent = await env.Rpc("agent.send", new { sessionId = sid, text = "Take your time [s:slow ms=2500]" });
            await env.Client.WaitFor(mark, e => e.Type == "stream.delta" && e.Sid == sid && e.D.S("kind") == "text", "slow text starts");
            var queued = await env.Rpc("agent.send", new { sessionId = sid, text = "Then answer this follow-up [s:echo]", mode = "queue" });
            Check.Equal(1L, queued.L("queuedMessages"));
            var q = await env.Rpc("agent.queue", new { sessionId = sid });
            Check.Equal("queue", q.Arr().Single().S("mode"));
            // a queued item can be listed but the slow answer must finish first
            var done = await env.WaitIdle(sid, mark, agent.L("runs"), 30_000);
            var run = await env.Result(sid, mark, done);
            var roles = string.Join(",", run.Messages.Select(m => m.S("role")));
            Check.Equal("user,assistant,user,assistant", roles);
            Check.Contains(RunResult.Text(run.Messages[1]), "SLOW-DONE");
            Check.Equal("queued", run.Messages[2].P("meta").S("kind"));
            Check.Contains(run.FinalText, "follow-up");
            Check.Equal(agent.L("runs"), run.Final.L("runs"), "the follow-up did not need a second run");
            Check.Equal(0, (await env.Rpc("agent.queue", new { sessionId = sid })).Arr().Count());
        });

        r.Add("control.dequeue", "queue: dequeue removes a pending follow-up", async () =>
        {
            var s = await env.NewSession();
            var sid = s.S("id")!;
            var mark = env.Client.Mark();
            var agent = await env.Rpc("agent.send", new { sessionId = sid, text = "slow [s:slow ms=1500]" });
            await env.Rpc("agent.send", new { sessionId = sid, text = "never mind [s:echo]", mode = "queue" });
            var item = (await env.Rpc("agent.queue", new { sessionId = sid })).Arr().Single();
            Check.True((await env.Rpc("agent.dequeue", new { sessionId = sid, id = item.S("id") })).GetBoolean(), "dequeued");
            var done = await env.WaitIdle(sid, mark, agent.L("runs"));
            var run = await env.Result(sid, mark, done);
            Check.Equal("user,assistant", string.Join(",", run.Messages.Select(m => m.S("role"))));
        });

        r.Add("control.abort-stream", "abort: partial answer persisted as aborted, agent idle, model request cancelled", async () =>
        {
            var s = await env.NewSession();
            var sid = s.S("id")!;
            var mark = env.Client.Mark();
            var mockMark = await env.MockMark();
            var agent = await env.Rpc("agent.send", new { sessionId = sid, text = "Long answer please [s:slow ms=8000]" });
            await env.WaitStreamed(sid, mark, "part 3", 20_000);
            var aborted = await env.Rpc("agent.abort", new { sessionId = sid });
            Check.True(aborted.GetBoolean(), "agent.abort returned true");
            var done = await env.WaitIdle(sid, mark, agent.L("runs"), 10_000);
            Check.Equal("idle", done.D.P("agent").S("status"));
            var run = await env.Result(sid, mark, done);
            var a = run.LastAssistant;
            Check.Equal("aborted", a.S("stopReason"));
            Check.Contains(run.FinalText, "part 3");
            Check.NotContains(run.FinalText, "SLOW-DONE");
            Check.True(run.OfType("stream.end").Any(), "stream.end after abort");
            Check.True(a.Arr("parts").Any(p => p.S("type") == "thinking"), "partial thinking kept");
            await Wait.UntilAsync(async () =>
            {
                var st = await env.MockStats();
                return st.P("models").P("qwen3.8-27b").L("inflight") == 0 ? "ok" : null;
            }, "mock request cancelled (no request in flight)", 5000);
            var log = (await env.MockLog(mockMark)).Where(e => e.S("scenario") == "slow").ToList();
            Check.True(log.Single().B("cancelled"), "the mock saw the client disconnect");
            Check.True(log.Single().L("durationMs") < 7000, "the stream stopped early");

            // the session keeps working after an abort
            var again = await env.Run(sid, "are you there?");
            Check.Contains(again.FinalText, "ECHO-DONE");
            var log2 = await env.MockLog(mockMark);
            Check.Equal(200L, log2.Last().L("status"), "transcript with an aborted message is accepted by the model API");
        });

        r.Add("control.abort-tool", "abort: during a tool call kills the command and records the aborted result", async () =>
        {
            var s = await env.NewSession();
            var sid = s.S("id")!;
            var mark = env.Client.Mark();
            var agent = await env.Rpc("agent.send", new { sessionId = sid, text = "Run the commands [s:slowtools]" });
            await env.Client.WaitFor(mark, e => e.Type == "tool.start" && e.Sid == sid, "first bash call", 20_000);
            await Task.Delay(300);
            Check.True((await env.Rpc("agent.abort", new { sessionId = sid })).GetBoolean());
            var done = await env.WaitIdle(sid, mark, agent.L("runs"), 10_000);
            var run = await env.Result(sid, mark, done);
            var results = run.Parts("tool_result").ToList();
            Check.Equal(3, results.Count, "every call has a result");
            Check.True(results.All(x => x.B("isError")), "all marked as errors: " + string.Join(" | ", results.Select(x => x.S("content"))));
            Check.Contains(results[0].S("content"), "[aborted; the process tree was killed]");
            Check.True(results.Skip(1).All(x => x.S("content") == "Aborted: the run was cancelled before this tool call completed."), "the rest never started");
            var procs = (await env.Rpc("processes.list")).Arr().Where(x => x.S("sessionId") == sid).ToList();
            Check.Equal(1, procs.Count, "only the first command was started: " + string.Join(", ", procs.Select(x => x.S("command"))));
            Check.Equal("killed", procs[0].S("status"));
            Check.True(run.OfType("tool.end").Count() >= 1, "tool.end for the started call");
            var next = await env.Run(sid, "continue [s:echo]");
            Check.Contains(next.FinalText, "ECHO-DONE");
        });

        r.Add("context.project-switch", "project switch mid-session: project notice, new cwd for tools, model sees the notice, prefix unchanged", async () =>
        {
            var s = await env.NewSession();
            var sid = s.S("id")!;
            var info = await env.Rpc("app.info");
            var workspace = info.S("defaultWorkspace")!;
            var firstMark = await env.MockMark();
            var first = await env.Run(sid, "where am I [s:where file=where-1.txt]");
            var before = await env.MockRequest((await env.MockLog(firstMark)).Last().L("seq"));
            Check.Contains(first.FinalText, "PWD=" + Env.BashPath(workspace));
            Check.True(File.Exists(Path.Combine(workspace, "where-1.txt")), "file written in the default workspace");

            var p = await env.NewProject("switch", CoreTests.Seed);
            var dir = p.S("path")!;
            var mark = env.Client.Mark();
            var updated = await env.Rpc("sessions.setProject", new { id = sid, projectId = p.S("id") });
            Check.Equal(p.S("id"), updated.S("projectId"));
            var notice = await env.Client.WaitFor(mark, e => e.Type == "message.added" && e.Sid == sid && e.D.P("message").S("role") == "notice", "project notice");
            Check.Equal("project", notice.D.P("message").P("meta").S("kind"));
            Check.Equal(dir, notice.D.P("message").P("meta").S("cwd"));
            await env.Client.WaitFor(mark, e => e.Type == "session.updated" && e.D.P("session").S("id") == sid, "session.updated");

            var mockMark = await env.MockMark();
            var second = await env.Run(sid, "and now? [s:where file=where-2.txt]");
            Check.Contains(second.FinalText, "PWD=" + Env.BashPath(dir));
            Check.True(File.Exists(Path.Combine(dir, "where-2.txt")), "file written in the new project");
            Check.False(File.Exists(Path.Combine(workspace, "where-2.txt")), "not in the old cwd");
            var log = await env.MockLog(mockMark);
            Check.True(log[0].Arr("notices").Any(n => n.GetString() == "project"), "the model received the project notice");
            // the switch only appended: the system prompt and everything sent before are byte-identical
            Env.PrefixKept(before, await env.MockRequest(log[0].L("seq")), "after the project switch");
            var files = await env.Rpc("files.list", new { sessionId = sid });
            Check.Equal(dir, files.S("root"), "files.list follows the session's project");
        });
    }
}
