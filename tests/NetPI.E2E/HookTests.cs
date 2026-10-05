using System.Text.Json;

namespace NetPI.E2E;

/// <summary>Agent hooks and model middleware: tool repair, nudge, retry, compaction, error surfacing.</summary>
public static class HookTests
{
    public const string Tiny = "aiproxy/tiny-ctx";

    public static void Register(TestRunner r, Env env)
    {
        r.Add("repair.textual-call", "tool repair: a standalone textual <tool_call> becomes a real call that runs", async () =>
        {
            var s = await env.NewSession();
            var run = await env.Run(s.S("id")!, "Check the agents [s:textcall]");
            Check.Contains(run.FinalText, "TEXTCALL-DONE");
            var repaired = run.Role("assistant").First();
            Check.True(repaired.P("meta").B("repaired"), "meta.repaired");
            Check.Equal("tool_use", repaired.S("stopReason"));
            var call = repaired.Arr("parts").Single(p => p.S("type") == "tool_call");
            Check.Equal("agent_choices", call.S("name"));
            Check.NotContains(RunResult.Text(repaired), "<tool_call>");
            Check.Equal("", RunResult.Text(repaired).Trim()); // a standalone envelope leaves no prose behind
            var result = run.Parts("tool_result").Single();
            Check.Equal(call.S("id"), result.S("callId"));
            Check.False(result.B("isError"));
            Check.Contains(result.S("content"), "No agents are set up");
            Check.True(run.OfType("message.updated").Any(e => e.D.P("message").P("meta").B("repaired")), "message.updated with the repaired message");
            Check.True(run.OfType("tool.start").Any(e => e.D.S("name") == "agent_choices"), "tool.start for the repaired call");
            Check.False(run.Role("notice").Any(), "no nudge for a repaired call");
        });

        r.Add("repair.documented-example", "tool repair: a documented example stays text — it is not executed", async () =>
        {
            var s = await env.NewSession();
            var run = await env.Run(s.S("id")!, "Show me an example [s:doccall]");
            var answered = run.Role("assistant").First();
            Check.False(answered.P("meta").B("repaired"), "not repaired");
            Check.True(answered.S("stopReason") != "tool_use", "a plain answer, not a tool use: " + answered.S("stopReason"));
            Check.Contains(RunResult.Text(answered), "<tool_call>", "the example is still visible as text");
            Check.False(run.OfType("tool.start").Any(), "no tool ran");
            var notice = run.Role("notice").Single();
            Check.Equal("nudge", notice.P("meta").S("kind"));
            Check.Contains(RunResult.Text(notice), "tool call written as text");
        });

        r.Add("nudge.cutoff", "nudge: a response cut off while only thinking (length) gets the stop-deliberating nudge and the agent continues", async () =>
        {
            var s = await env.NewSession();
            var mockMark = await env.MockMark();
            var run = await env.Run(s.S("id")!, "Analyze everything [s:cutoff]");
            var first = run.Role("assistant").First();
            Check.Equal("length", first.S("stopReason"));
            Check.True(first.Arr("parts").All(p => p.S("type") == "thinking"), "only thinking in the cut-off message");
            var notice = run.Role("notice").Single();
            Check.Equal("nudge", notice.P("meta").S("kind"));
            // a cut-off that is only thinking is not told to "continue where you left off" (that is more thinking)
            Check.Contains(RunResult.Text(notice), "whole output budget thinking");
            Check.Contains(RunResult.Text(notice), "smallest next step");
            Check.Contains(run.FinalText, "NUDGE-RESUMED");
            var log = await env.MockLog(mockMark);
            Check.Equal("notice:nudge", log.Last().S("lastRole"), "the model saw the nudge as the last message");
        });

        r.Add("retry.drop", "retry: dropped connection → agent.notice + stream.reset, then the retried answer", async () =>
        {
            var s = await env.NewSession();
            var sid = s.S("id")!;
            var mockMark = await env.MockMark();
            var run = await env.Run(sid, "Answer despite a flaky connection [s:drop]");
            Check.Contains(run.FinalText, "DROP-RECOVERED");
            var notice = run.OfType("agent.notice").FirstOrDefault(e => (e.D.S("text") ?? "").Contains("Retrying"));
            Check.True(notice is not null, "retry notice: " + string.Join(" | ", run.OfType("agent.notice").Select(e => e.D.S("text"))));
            Check.Equal("warn", notice!.D.S("level"));
            var reset = run.OfType("stream.reset").ToList();
            Check.Equal(1, reset.Count, "one stream.reset");
            var types = run.Types;
            Check.True(types.IndexOf("stream.reset") > types.IndexOf("stream.delta"), "reset after the first deltas");
            Check.Equal(2, run.Messages.Count, "only the retried answer is persisted");
            Check.NotContains(run.FinalText, "connection drops");
            var log = (await env.MockLog(mockMark)).Where(e => e.S("scenario") == "drop").ToList();
            Check.Equal(2, log.Count, "two attempts");
            Check.True(log[0].B("dropped"), "first attempt dropped");
            Check.Equal(1, run.OfType("stream.start").Count(), "one stream for both attempts");
        });

        r.Add("retry.503", "retry: HTTP 503 then success", async () =>
        {
            var s = await env.NewSession();
            var mockMark = await env.MockMark();
            var run = await env.Run(s.S("id")!, "backend flaps [s:error status=503 n=2]");
            Check.Contains(run.FinalText, "ERROR-RECOVERED");
            var log = (await env.MockLog(mockMark)).Where(e => e.S("scenario") == "error").ToList();
            Check.Equal("503,503,200", string.Join(",", log.Select(e => e.L("status"))));
            Check.True(run.OfType("agent.notice").Count(e => (e.D.S("text") ?? "").Contains("Retrying")) >= 2, "two retry notices");
            Check.False(run.OfType("stream.reset").Any(), "nothing to reset when no content was streamed");
        });

        r.Add("errors.rejected", "errors: a rejected request ends the run with an error notice, session stays usable", async () =>
        {
            var s = await env.NewSession();
            var sid = s.S("id")!;
            var run = await env.Run(sid, "please fail [s:fail]");
            var notice = run.Role("notice").Single();
            Check.Equal("error", notice.P("meta").S("kind"));
            Check.Contains(RunResult.Text(notice), "rejected on purpose");
            Check.Equal("idle", run.Final.S("status"));
            Check.False(run.Role("assistant").Any(), "no assistant message");
            var again = await env.Run(sid, "hello again");
            Check.Contains(again.FinalText, "ECHO-DONE");
        });

        r.Add("errors.unknown-model", "errors: unknown model gives a clear error notice", async () =>
        {
            var s = await env.NewSession(model: "aiproxy/does-not-exist");
            var run = await env.Run(s.S("id")!, "hi");
            var notice = run.Role("notice").Single();
            Check.Equal("error", notice.P("meta").S("kind"));
            Check.Contains(RunResult.Text(notice), "not found");
        });

        r.Add("compaction.auto", "compaction: long run on tiny-ctx compacts, summary replaces old messages, context meter drops", async () =>
        {
            var s = await env.NewSession(model: Tiny);
            var sid = s.S("id")!;
            var mark = env.Client.Mark();
            var mockMark = await env.MockMark();
            var run = await env.Run(sid, "Produce lots of output [s:long n=10 lines=40]", 120_000);
            var log = await env.MockLog(mockMark);
            Check.True(log.All(e => e.L("status") == 200), "no request rejected: " + string.Join(" | ", log.Where(e => e.L("status") != 200).Select(e => e.S("error"))));
            Check.Contains(run.FinalText, "LONG-DONE");
            var summaries = run.Role("summary").ToList();
            Check.True(summaries.Count >= 1, "at least one compaction summary");
            Check.Equal("compaction", summaries[0].P("meta").S("kind"));
            Check.Contains(RunResult.Text(summaries[0]), "MOCK-SUMMARY");
            Check.True(run.Messages.Any(m => m.B("compacted")), "old messages flagged compacted");
            Check.True(env.Client.Since(mark).Any(e => e.Type == "messages.compacted" && e.Sid == sid), "messages.compacted event");
            Check.True(log.Any(e => e.B("summarizer")), "summarizer called");
            Check.True(log.Where(e => !e.B("summarizer")).Max(e => e.L("inputTokens")) <= env.Options.TinyContext, "no request over the window");

            var used = env.Client.Since(mark).Where(e => e.Type == "session.context" && e.D.S("sessionId") == sid).Select(e => e.D.L("used")).ToList();
            Check.True(used.Count >= 3, "context meter updates");
            Check.True(used.Zip(used.Skip(1)).Any(x => x.Second < x.First), "the context meter dropped after compaction: " + string.Join(",", used));
            Check.True(used.All(u => u <= env.Options.TinyContext), "context meter within the window: " + string.Join(",", used));
            var notices = run.OfType("agent.notice").Select(e => e.D.S("text") ?? "").ToList();
            Check.True(notices.Any(t => t.StartsWith("Context compacted")), "compaction notice: " + string.Join(" | ", notices));
            var session = await env.Rpc("sessions.get", new { id = sid });
            Check.True(session.L("contextTokens") < env.Options.TinyContext, "session.contextTokens within the window");
            // all 10 steps ran exactly once
            var steps = run.Parts("tool_result").Select(x => x.S("content") ?? "").Where(c => c.Contains("LONGSTEP")).Select(c => c[(c.IndexOf("LONGSTEP", StringComparison.Ordinal) + 9)..].Split('/')[0]).ToList();
            Check.Equal("1,2,3,4,5,6,7,8,9,10", string.Join(",", steps), "each step once");
        }, 180);

        r.Add("compaction.overflow", "compaction: backend overflow (window smaller than advertised) compacts and retries the call", async () =>
        {
            // The catalog claims 50k, the backend only holds 12k (e.g. a model loaded with a smaller n_ctx than its static
            // capability entry): auto-compaction never triggers, the backend rejects the prompt, overflow recovery must cope.
            await env.Rpc("settings.set", new { path = "providers.aiproxy.models.tiny-ctx", value = new { contextWindow = 50_000 } });
            try
            {
                await env.Rpc("models.list", new { refresh = true });
                var s = await env.NewSession(model: Tiny);
                var sid = s.S("id")!;
                // only bash, which the scenario uses: the tool definitions would take most of the 12k window, and how much
                // depends on how many tools the plugins register, not on what this test is about
                var all = (await env.Rpc("agent.tools", new { sessionId = sid })).P("tools").EnumerateArray().Select(t => t.S("name")!).ToArray();
                await env.Rpc("agent.setTools", new { sessionId = sid, off = all.Where(n => n != "bash").ToArray() });
                var mockMark = await env.MockMark();
                var run = await env.Run(sid, "Produce lots of output [s:long n=12 lines=80]", 120_000);
                var log = await env.MockLog(mockMark);
                var rejected = log.Where(e => e.L("status") == 400).ToList();
                Check.True(rejected.Count >= 1, "the backend rejected an oversized prompt");
                Check.True(rejected.All(e => (e.S("error") ?? "").Contains("exceed_context_size_error")), "as a context overflow");
                Check.False(run.Role("notice").Any(n => n.P("meta").S("kind") == "error"),
                    "no error notice: " + string.Join(" | ", run.Role("notice").Select(RunResult.Text)));
                Check.Contains(run.FinalText, "LONG-DONE");
                var summaries = run.Role("summary").ToList();
                Check.True(summaries.Count >= 1, "compacted");
                Check.Equal("overflow", summaries[0].P("meta").S("mode"));
                var steps = run.Parts("tool_result").Select(x => x.S("content") ?? "").Where(c => c.Contains("LONGSTEP")).Count();
                Check.Equal(12, steps, "every step ran once");
            }
            finally
            {
                await env.Rpc("settings.set", new { path = "providers.aiproxy.models.tiny-ctx", value = (object?)null });
                await env.Rpc("models.list", new { refresh = true });
            }
        }, 180);

        r.Add("compaction.manual", "compaction: /compact RPC on an idle session", async () =>
        {
            var s = await env.NewSession(model: Tiny);
            var sid = s.S("id")!;
            for (var i = 0; i < 3; i++) await env.Run(sid, $"message {i} " + new string('x', 1200));
            var before = (await env.Rpc("sessions.messages", new { id = sid, limit = 100 })).Arr("messages").Count();
            var result = await env.Rpc("compaction.run", new { sessionId = sid, args = "focus on x" }, 60_000);
            Check.Contains(result.GetString(), "compacted");
            var msgs = (await env.Rpc("sessions.messages", new { id = sid, limit = 100 })).Arr("messages").ToList();
            Check.Equal(before + 1, msgs.Count, "summary appended");
            Check.Equal("manual", msgs.Last().P("meta").S("mode"));
            var after = await env.Run(sid, "still there?");
            Check.Contains(after.FinalText, "ECHO-DONE");
        }, 120);
    }
}
