using System.Net;
using System.Text;
using System.Text.Json;

namespace NetPI.E2E;

/// <summary>Cross-cutting flows: waiting + steering, aborting orchestrators, concurrency, provider switches, images, instructions.</summary>
public static class AdvancedTests
{
    // 1×1 PNG
    private const string Png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==";

    private static async Task<List<JsonElement>> Children(Env env, string parentId) =>
        (await env.Rpc("runs.list", new { includeFinished = true })).Arr().Where(a => a.S("parentAgentId") == parentId).ToList();

    private static async Task<JsonElement> Pool(Env env, string key) =>
        (await env.Rpc("agents.list")).Arr().FirstOrDefault(p => p.S("key") == key);

    public static void Register(TestRunner r, Env env)
    {
        r.Add("wait + steer: a user message interrupts agent_wait; a late report arrives as an agent-result notice", async () =>
        {
            var s = await env.NewSession(model: CoreTests.Qwen);
            var sid = s.S("id")!;
            var mark = env.Client.Mark();
            // worker-1 needs ~2s, worker-2 ~4.5s; both hold the two qwen slots while the parent is yielded
            var agent = await env.Rpc("agent.send", new { sessionId = sid, text = "Two workers [s:spawn n=2 delay=2000 stagger=2500]" });
            var parentId = agent.S("id")!;
            await env.Client.WaitFor(mark, e => e.Type == "agent.status" && e.D.P("agent").S("id") == parentId && e.D.P("agent").S("status") == "yielded", "parent waiting", 20_000);
            await env.Rpc("agent.send", new { sessionId = sid, text = "How is it going? [s:echo]", mode = "steer" });
            // the wait ends at once; the parent then queues for a slot with priority (both are held by its workers)
            await env.Client.WaitFor(mark, e => e.Type == "agent.status" && e.D.P("agent").S("id") == parentId && e.D.P("agent").S("status") == "queued", "parent re-queued for a slot", 10_000);
            var idle = await env.WaitIdle(sid, mark, agent.L("runs"), 20_000);
            var kids = await Children(env, parentId);
            Check.Equal("completed,running", string.Join(",", kids.OrderBy(k => k.S("name")).Select(k => k.S("status"))), "worker-2 still runs when the parent answered");
            var first = await env.Result(sid, mark, idle);
            var wait = first.Parts("tool_result").Last();
            Check.Equal("agent_wait", wait.S("name"));
            Check.Contains(wait.S("content"), "1 of 2 agents finished");
            Check.Contains(wait.S("content"), "Report from worker-1");
            Check.Contains(first.FinalText, "How is it going?");
            Check.Equal("user,assistant,tool,tool,assistant,tool,user,assistant", string.Join(",", first.Messages.Select(m => m.S("role"))));
            Check.Equal("steer", first.Messages[6].P("meta").S("kind"));
            // worker-2's report comes back as a notice and wakes the parent
            var notice = await env.Client.WaitFor(idle.Index, e => e.Type == "message.added" && e.Sid == sid && e.D.P("message").P("meta").S("kind") == "agent-result", "agent-result notice", 20_000);
            Check.Equal("worker-2", notice.D.P("message").P("meta").S("agentName"));
            var done = await env.WaitIdle(sid, notice.Index, agent.L("runs") + 1, 20_000);
            var all = await env.Result(sid, mark, done);
            Check.Contains(all.FinalText, "AGENT-RESULT-RECEIVED");
            Check.Contains(all.FinalText, "2 squared is 4");
            Check.Equal(1, all.Messages.Count(m => m.P("meta").S("kind") == "agent-result"), "worker-1's result was consumed by agent_wait, not duplicated");
            Check.True((await Children(env, parentId)).All(k => k.S("status") == "completed"), "workers completed");
            Check.Equal(0L, (await Pool(env, CoreTests.Qwen)).L("busy"), "slots released");
        }, 90);

        r.Add("abort orchestrator: running subagents are cancelled, slots released, no notices", async () =>
        {
            var s = await env.NewSession(model: CoreTests.Qwen);
            var sid = s.S("id")!;
            var mark = env.Client.Mark();
            var agent = await env.Rpc("agent.send", new { sessionId = sid, text = "Three workers [s:spawn n=3 delay=8000]" });
            var parentId = agent.S("id")!;
            await env.Client.WaitFor(mark, e => e.Type == "agent.status" && e.D.P("agent").S("id") == parentId && e.D.P("agent").S("status") == "yielded", "parent waiting", 20_000);
            await env.Client.WaitFor(mark, e => e.Type == "stream.start" && e.Sid != sid, "a worker streaming", 20_000);
            Check.True((await env.Rpc("agent.abort", new { sessionId = sid })).GetBoolean(), "aborted");
            await env.WaitIdle(sid, mark, agent.L("runs"), 10_000);
            await Wait.UntilAsync(async () => (await Children(env, parentId)).All(k => k.S("status") == "cancelled") ? "ok" : null, "all workers cancelled", 10_000);
            await Wait.UntilAsync(async () => (await Pool(env, CoreTests.Qwen)) is var p && p.L("busy") == 0 && p.L("queued") == 0 ? "ok" : null, "qwen pool empty", 10_000);
            await Wait.UntilAsync(async () => (await env.MockStats()).P("models").P("qwen3.8-27b").L("inflight") == 0 ? "ok" : null, "no request in flight", 10_000);
            await Task.Delay(1000);
            var msgs = (await env.Rpc("sessions.messages", new { id = sid, limit = 200 })).Arr("messages").ToList();
            Check.False(msgs.Any(m => m.P("meta").S("kind") == "agent-result"), "no agent-result notices after an abort");
            Check.Equal("idle", (await env.Rpc("agent.get", new { id = parentId })).S("status"));
            var kids = await Children(env, parentId);
            Check.True(kids.All(k => (k.S("error") ?? "").Contains("parent")), "cancel reason: " + string.Join(" | ", kids.Select(k => k.S("error"))));
            var wait = msgs.SelectMany(m => m.Arr("parts")).Last(p => p.S("type") == "tool_result");
            Check.Equal("agent_wait", wait.S("name"));
            Check.True(wait.B("isError"), "the interrupted agent_wait is recorded as aborted");
        }, 60);

        r.Add("slots: three chats on qwen (2 slots) at once → 2 run, 1 queued, all complete", async () =>
        {
            await Wait.UntilAsync(async () => (await Pool(env, CoreTests.Qwen)).L("busy") == 0 ? "ok" : null, "idle qwen pool");
            await env.MockReset();
            var mark = env.Client.Mark();
            var sessions = new List<string>();
            for (var i = 0; i < 3; i++) sessions.Add((await env.NewSession(model: CoreTests.Qwen)).S("id")!);
            var sends = await Task.WhenAll(sessions.Select(id => env.Rpc("agent.send", new { sessionId = id, text = "take your time [s:slow ms=1500]" })));
            var queued = await env.Client.WaitFor(mark, e => e.Type == "agent.status" && sessions.Contains(e.D.P("agent").S("sessionId") ?? "")
                                                             && e.D.P("agent").S("status") == "queued", "one chat queued for a slot", 10_000);
            Check.Contains(queued.D.P("agent").S("activity"), "waiting for a slot on");
            for (var i = 0; i < 3; i++) await env.WaitIdle(sessions[i], mark, sends[i].L("runs"), 30_000);
            var q = (await env.MockStats()).P("models").P("qwen3.8-27b");
            Check.Equal(2L, q.L("maxInflight"), "the backend never saw more than 2");
            Check.Equal(3L, q.L("total"));
            foreach (var id in sessions)
            {
                var msgs = (await env.Rpc("sessions.messages", new { id })).Arr("messages").ToList();
                Check.Contains(RunResult.Text(msgs.Last()), "SLOW-DONE");
            }
        }, 60);

        r.Add("slots: aborting a chat that waits for a slot removes it from the queue without leaking one", async () =>
        {
            await Wait.UntilAsync(async () => (await Pool(env, CoreTests.Qwen)).L("busy") == 0 ? "ok" : null, "idle qwen pool");
            var mark = env.Client.Mark();
            var sessions = new List<string>();
            for (var i = 0; i < 3; i++) sessions.Add((await env.NewSession(model: CoreTests.Qwen)).S("id")!);
            var sends = new List<JsonElement>();
            foreach (var id in sessions) sends.Add(await env.Rpc("agent.send", new { sessionId = id, text = "slow [s:slow ms=2500]" }));
            var queued = await env.Client.WaitFor(mark, e => e.Type == "agent.status" && e.D.P("agent").S("status") == "queued"
                                                             && sessions.Contains(e.D.P("agent").S("sessionId") ?? ""), "a queued chat", 10_000);
            var waiting = queued.D.P("agent").S("sessionId")!;
            Check.True((await env.Rpc("agent.abort", new { sessionId = waiting })).GetBoolean(), "abort the waiting chat");
            await env.WaitIdle(waiting, mark, sends[sessions.IndexOf(waiting)].L("runs"), 10_000);
            var pool = await Pool(env, CoreTests.Qwen);
            Check.Equal(0L, pool.L("queued"), "no waiter left");
            Check.Equal(2L, pool.L("busy"), "the two running chats keep their slots");
            for (var i = 0; i < 3; i++)
                if (sessions[i] != waiting) await env.WaitIdle(sessions[i], mark, sends[i].L("runs"), 30_000);
            await Wait.UntilAsync(async () => (await Pool(env, CoreTests.Qwen)).L("busy") == 0 ? "ok" : null, "all slots free again", 5000);
            var msgs = (await env.Rpc("sessions.messages", new { id = waiting })).Arr("messages").ToList();
            Check.Equal("user", string.Join(",", msgs.Select(m => m.S("role"))), "the aborted chat never reached the model");
            var next = await env.Run(waiting, "now? [s:echo]");
            Check.Contains(next.FinalText, "ECHO-DONE");
        }, 60);

        r.Add("delete a session during a run: the run stops, nothing crashes, the server keeps working", async () =>
        {
            var s = await env.NewSession();
            var sid = s.S("id")!;
            var mark = env.Client.Mark();
            var agent = await env.Rpc("agent.send", new { sessionId = sid, text = "Run the commands [s:slowtools]" });
            await env.Client.WaitFor(mark, e => e.Type == "tool.start" && e.Sid == sid, "tool running", 20_000);
            Check.True((await env.Rpc("sessions.delete", new { id = sid })).GetBoolean());
            await env.Client.WaitFor(mark, e => e.Type == "session.deleted" && e.D.S("id") == sid, "session.deleted");
            var end = await env.Client.WaitFor(mark, e => e.Type == "agent.status" && e.D.P("agent").S("id") == agent.S("id")
                                                          && e.D.P("agent").S("status") != "running", "the agent stops", 15_000);
            Check.True(end.D.P("agent").S("status") is "idle" or "failed", "stopped: " + end.D.P("agent").S("status"));
            await Task.Delay(500);
            var log = env.ReadServerLog();
            Check.NotContains(log, "Unhandled exception");
            Check.NotContains(log, "Agent run failed");
            var procs = (await env.Rpc("processes.list")).Arr().Where(p => p.S("sessionId") == sid).ToList();
            Check.True(procs.All(p => p.S("status") != "running"), "no process left running for the deleted session");
            await Assert404(env, "sessions.messages", new { id = sid });
            var again = await env.Run((await env.NewSession()).S("id")!, "still fine? [s:echo]");
            Check.Contains(again.FinalText, "ECHO-DONE");
        }, 60);

        r.Add("delete an orchestrator session while its subagents run: everything stops, slots released", async () =>
        {
            var s = await env.NewSession(model: CoreTests.Qwen);
            var sid = s.S("id")!;
            var mark = env.Client.Mark();
            var agent = await env.Rpc("agent.send", new { sessionId = sid, text = "Three workers [s:spawn n=3 delay=6000]" });
            var parentId = agent.S("id")!;
            await env.Client.WaitFor(mark, e => e.Type == "agent.status" && e.D.P("agent").S("id") == parentId && e.D.P("agent").S("status") == "yielded", "parent waiting", 20_000);
            var kids = await Children(env, parentId);
            Check.True((await env.Rpc("sessions.delete", new { id = sid })).GetBoolean());
            await Wait.UntilAsync(async () => (await Pool(env, CoreTests.Qwen)) is var p && p.L("busy") == 0 && p.L("queued") == 0 ? "ok" : null, "qwen pool empty", 15_000);
            await Wait.UntilAsync(async () => (await env.MockStats()).P("models").P("qwen3.8-27b").L("inflight") == 0 ? "ok" : null, "no request in flight", 10_000);
            var busy = (await env.Rpc("runs.list", new { includeFinished = false })).Arr().ToList();
            Check.False(busy.Any(a => a.S("id") == parentId || kids.Any(k => k.S("id") == a.S("id"))), "no agent of the deleted tree still busy: " + string.Join(",", busy.Select(a => a.S("name") + ":" + a.S("status"))));
            foreach (var k in kids) await Assert404(env, "sessions.get", new { id = k.S("sessionId") });
            var log = env.ReadServerLog();
            Check.NotContains(log, "Unhandled exception");
            Check.NotContains(log, "Agent run failed");
            var run = await env.Run((await env.NewSession(model: CoreTests.Qwen)).S("id")!, "pool usable again [s:echo]");
            Check.Contains(run.FinalText, "ECHO-DONE");
        }, 60);

        r.Add("abort one subagent directly: the waiting parent gets it back as cancelled and continues", async () =>
        {
            var s = await env.NewSession(model: CoreTests.Qwen);
            var sid = s.S("id")!;
            var mark = env.Client.Mark();
            var agent = await env.Rpc("agent.send", new { sessionId = sid, text = "Two workers [s:spawn n=2 delay=1500 stagger=6000]" });
            var parentId = agent.S("id")!;
            await env.Client.WaitFor(mark, e => e.Type == "agent.status" && e.D.P("agent").S("id") == parentId && e.D.P("agent").S("status") == "yielded", "parent waiting", 20_000);
            var slow = (await Children(env, parentId)).Single(k => k.S("name") == "worker-2");
            await env.Client.WaitFor(mark, e => e.Type == "stream.start" && e.Sid == slow.S("sessionId"), "worker-2 streaming", 20_000);
            Check.True((await env.Rpc("agent.abort", new { sessionId = slow.S("sessionId") })).GetBoolean(), "worker aborted");
            var done = await env.WaitIdle(sid, mark, agent.L("runs"), 30_000);
            var run = await env.Result(sid, mark, done);
            var wait = run.Parts("tool_result").Last();
            Check.Contains(wait.S("content"), "worker-2");
            Check.Contains(wait.S("content"), "cancelled");
            Check.Contains(run.FinalText, "SPAWN-DONE");
            Check.Contains(run.FinalText, "Report from worker-1");
            Check.Equal("cancelled", (await Children(env, parentId)).Single(k => k.S("name") == "worker-2").S("status"));
        }, 60);

        r.Add("provider reload mid-stream: the running call finishes, the next call uses the new provider instance", async () =>
        {
            var s = await env.NewSession();
            var sid = s.S("id")!;
            var mark = env.Client.Mark();
            var agent = await env.Rpc("agent.send", new { sessionId = sid, text = "slow please [s:slow ms=4000]" });
            await env.WaitStreamed(sid, mark, "part 2", 20_000);
            Check.True((await env.Rpc("plugins.reload", new { id = "netpi.providers.aiproxy" }, 60_000)).GetBoolean());
            var done = await env.WaitIdle(sid, mark, agent.L("runs"), 30_000);
            var run = await env.Result(sid, mark, done);
            // The in-flight stream keeps running on the old instance (its response is already open); the next call
            // uses the new one. Either way the user sees no error.
            Check.False(run.Role("notice").Any(), "no error notice: " + string.Join(" | ", run.Role("notice").Select(RunResult.Text)));
            Check.Contains(run.FinalText, "SLOW-DONE");
            var unloaded = await env.Client.WaitFor(mark, e => e.Type == "plugins.unloaded" && e.D.S("id") == "netpi.providers.aiproxy", "old provider unloaded", 40_000);
            Check.True(unloaded.D.B("collected"), "the old provider was collected once its stream finished");
            var next = await env.Run(sid, "again [s:echo]");
            Check.Contains(next.FinalText, "ECHO-DONE");
        }, 60);

        r.Add("budgets and stopped backends: clear error notices", async () =>
        {
            await env.Rpc("settings.set", new { path = "budget.providers.anthropic.dailyTokens", value = 1 });
            try
            {
                // spend the tiny budget (unless earlier tests already did), then a fresh session is refused
                await env.Run((await env.NewSession(model: "anthropic/claude-haiku-4-5")).S("id")!, "spend the budget [s:echo]");
                var s = await env.NewSession(model: "anthropic/claude-haiku-4-5");
                var run = await env.Run(s.S("id")!, "hi");
                var notice = run.Role("notice").Single();
                Check.Equal("error", notice.P("meta").S("kind"));
                Check.Contains(RunResult.Text(notice), "daily token budget for provider 'anthropic' is exhausted");
            }
            finally { await env.Rpc("settings.set", new { path = "budget.providers", value = (object?)null }); }

            await env.Rpc("settings.set", new { path = "retry.maxAttempts", value = 2 });
            try
            {
                var s2 = await env.NewSession(model: "aiproxy/qwen38-27b-iq3s");
                var run2 = await env.Run(s2.S("id")!, "hi", 30_000);
                var notice2 = run2.Role("notice").Single();
                Check.Contains(RunResult.Text(notice2), "503");
                Check.Contains(RunResult.Text(notice2), "backend_unavailable");
            }
            finally { await env.Rpc("settings.set", new { path = "retry.maxAttempts", value = (object?)null }); }
        }, 60);

        r.Add("provider switch mid-session: qwen (responses) → claude (anthropic) → gemma (chat) keeps a valid transcript", async () =>
        {
            await env.Rpc("settings.set", new { path = "providers.aiproxy.models.gemma-4", value = new { transport = "chat" } });
            try
            {
                var p = await env.NewProject("switch-models", CoreTests.Seed);
                var dir = p.S("path")!;
                var s = await env.NewSession(model: CoreTests.Qwen, projectId: p.S("id"));
                var sid = s.S("id")!;
                var mockMark = await env.MockMark();
                var r1 = await env.Run(sid, "one [s:tools file=notes.txt old=alpha new=A1 create=c1.txt]");
                Check.Contains(r1.FinalText, "TOOLS-DONE");
                await env.Rpc("sessions.update", new { id = sid, model = "anthropic/claude-sonnet-4-5" });
                var r2 = await env.Run(sid, "two [s:tools file=notes.txt old=beta new=B2 create=c2.txt]");
                Check.Contains(r2.FinalText, "TOOLS-DONE");
                await env.Rpc("sessions.update", new { id = sid, model = "aiproxy/gemma-4" });
                var r3 = await env.Run(sid, "three [s:tools file=notes.txt old=gamma new=G3 create=c3.txt]");
                Check.Contains(r3.FinalText, "TOOLS-DONE");
                await env.Rpc("sessions.update", new { id = sid, model = CoreTests.Qwen });
                var r4 = await env.Run(sid, "and back [s:echo]");
                Check.Contains(r4.FinalText, "ECHO-DONE");
                var log = await env.MockLog(mockMark);
                Check.True(log.All(e => e.L("status") == 200), "every provider accepted the mixed transcript: " +
                                                                 string.Join(" | ", log.Where(e => e.L("status") != 200).Select(e => $"{e.S("api")}: {e.S("error")}")));
                var apis = log.Select(e => e.S("api")).Aggregate(new List<string?>(), (l, a) => { if (l.Count == 0 || l[^1] != a) l.Add(a); return l; });
                Check.Equal("responses,anthropic,chat,responses", string.Join(",", apis));
                Check.Equal("A1 line\nB2 line\nG3 line\n", File.ReadAllText(Path.Combine(dir, "notes.txt")));
                Check.True(log.Where(e => e.S("api") == "anthropic").All(e => e.B("thinkingEnabled")), "claude keeps thinking on after a foreign transcript");
            }
            finally
            {
                await env.Rpc("settings.set", new { path = "providers.aiproxy.models.gemma-4", value = (object?)null });
            }
        }, 90);

        r.Add("images: attached image reaches vision models (responses + anthropic) and is omitted for text-only models", async () =>
        {
            async Task<string> Send(string model)
            {
                var s = await env.NewSession(model: model);
                var sid = s.S("id")!;
                var mark = env.Client.Mark();
                var agent = await env.Rpc("agent.send", new { sessionId = sid, text = "What is in this picture? [s:echo]", images = new[] { new { mediaType = "image/png", data = Png } } });
                var done = await env.WaitIdle(sid, mark, agent.L("runs"));
                var run = await env.Result(sid, mark, done);
                var user = run.Role("user").Single();
                Check.True(user.Arr("parts").Any(p => p.S("type") == "image" && p.S("data") == Png), "image persisted with the user message");
                return run.FinalText;
            }
            Check.Contains(await Send(CoreTests.Qwen), "| images | 1 |");
            Check.Contains(await Send("anthropic/claude-sonnet-4-5"), "| images | 1 |");
            var textOnly = await Send("aiproxy/gemma-4");
            Check.Contains(textOnly, "| images | 0 |");
            Check.Contains(textOnly, "image omitted");
        });

        r.Add("instructions: AGENTS.md and the working directory arrive as notices; an edit is appended, the prefix stays byte-identical", async () =>
        {
            var p = await env.NewProject("agentsmd", d => File.WriteAllText(Path.Combine(d, "AGENTS.md"), "# Rules\nAlways run the tests. E2E-AGENTS-MARKER\n"));
            var s = await env.NewSession(projectId: p.S("id"));
            var sid = s.S("id")!;
            async Task<JsonElement> LastRequest(long mark) => await env.MockRequest((await env.MockLog(mark)).Last().L("seq"));

            var mark = await env.MockMark();
            await env.Run(sid, "hi [s:echo]");
            var first = await LastRequest(mark);
            var instructions = first.S("instructions") ?? "";
            Check.NotContains(instructions, "E2E-AGENTS-MARKER", "AGENTS.md is not in the system prompt");
            Check.NotContains(instructions, p.S("path")!, "neither is the working directory");
            var input1 = first.Arr("input").ToList();
            Check.True(input1.Any(i => i.GetRawText().Contains("E2E-AGENTS-MARKER") && i.GetRawText().Contains("kind=\\\"instructions\\\"")), "instructions notice sent");
            Check.True(input1.Any(i => i.GetRawText().Contains("Working directory: " + p.S("path")!.Replace("\\", "\\\\"))), "working directory notice sent");

            // an edited AGENTS.md is appended as a new notice: the system prompt and everything sent before stay byte-identical
            File.WriteAllText(Path.Combine(p.S("path")!, "AGENTS.md"), "# Rules\nAlways run the tests. E2E-AGENTS-MARKER-V2\n");
            mark = await env.MockMark();
            await env.Run(sid, "again [s:echo]");
            var second = await LastRequest(mark);
            Env.PrefixKept(first, second, "after the AGENTS.md edit");
            Check.True(second.Arr("input").Skip(input1.Count).Any(i => i.GetRawText().Contains("E2E-AGENTS-MARKER-V2")), "the edit arrives as a new notice");

            var files = await env.Rpc("agentsmd.list", new { sessionId = sid });
            Check.True(files.Arr().Any(f => f.S("path") == Path.Combine(p.S("path")!, "AGENTS.md")), "agentsmd.list");
            var preview = await env.Rpc("context.preview", new { sessionId = sid });
            Check.Equal(instructions, preview.S("systemPrompt"), "the preview shows the frozen prompt");
            Check.True(preview.B("frozen"), "frozen");
            Check.True(preview.L("estimatedTokens") > 1000, "preview estimate");
        });

        r.Add("skills: a project's skill is announced in a notice, /skill:name loads it for the message, skills.list", async () =>
        {
            var p = await env.NewProject("skills", d =>
            {
                Directory.CreateDirectory(Path.Combine(d, ".git"));
                var dir = Path.Combine(d, ".agents", "skills", "e2e-skill");
                Directory.CreateDirectory(Path.Combine(dir, "scripts"));
                File.WriteAllText(Path.Combine(dir, "SKILL.md"), "---\nname: e2e-skill\ndescription: Use when: the E2E suite asks for it.\n---\n\n# E2E skill\nE2E-SKILL-BODY\n");
                File.WriteAllText(Path.Combine(dir, "scripts", "run.sh"), "echo ok\n");
            });
            var sid = (await env.NewSession(projectId: p.S("id"))).S("id")!;
            var skill = (await env.Rpc("skills.list", new { sessionId = sid })).Arr("skills").First();
            Check.Equal("e2e-skill", skill.S("name"), "the project's skill comes first");
            Check.Equal("project", skill.S("scope"));
            Check.Equal("Use when: the E2E suite asks for it.", skill.S("description"));

            var mark = await env.MockMark();
            var run = await env.Run(sid, "/skill:e2e-skill go [s:echo]");
            Check.Contains(run.FinalText, "ECHO-DONE");
            var request = await env.MockRequest((await env.MockLog(mark)).Last().L("seq"));
            var input = request.Arr("input").Select(i => i.GetRawText()).ToList();
            Check.True(input.Any(i => i.Contains("kind=\\\"skills\\\"") && i.Contains("<name>e2e-skill</name>")), "the catalog notice was sent");
            Check.True(input.Any(i => i.Contains("kind=\\\"skill\\\"") && i.Contains("E2E-SKILL-BODY") && i.Contains("<file>scripts/run.sh</file>")), "the skill for the message was sent");
            Check.NotContains(request.S("instructions"), "<name>e2e-skill</name>", "the catalog is not in the system prompt");
            var notice = run.AllMessages.Single(m => m.S("role") == "notice" && m.P("meta").S("kind") == "skill");
            Check.Equal("e2e-skill", notice.P("meta").S("skill"));
            var user = run.AllMessages.Single(m => m.S("role") == "user");
            Check.Equal(user.L("id").ToString(), notice.P("meta").S("for"), "tied to the message");
        });

        r.Add("retry: a stalled stream is abandoned after retry.stallTimeoutSeconds and retried", async () =>
        {
            await env.Rpc("settings.set", new { path = "retry.stallTimeoutSeconds", value = 1.5 });
            try
            {
                var s = await env.NewSession();
                var mockMark = await env.MockMark();
                var run = await env.Run(s.S("id")!, "stall please [s:stall]", 30_000);
                Check.Contains(run.FinalText, "STALL-RECOVERED");
                Check.Equal(1, run.OfType("stream.reset").Count());
                Check.True(run.OfType("agent.notice").Any(e => (e.D.S("text") ?? "").Contains("stalled")), "stall notice");
                var log = (await env.MockLog(mockMark)).Where(e => e.S("scenario") == "stall").ToList();
                Check.Equal(2, log.Count);
                Check.True(log[0].B("cancelled"), "the stalled request was cancelled by the client");
            }
            finally { await env.Rpc("settings.set", new { path = "retry.stallTimeoutSeconds", value = (object?)null }); }
        });

        r.Add("retry: gives up after retry.maxAttempts with an error notice", async () =>
        {
            await env.Rpc("settings.set", new { path = "retry.maxAttempts", value = 3 });
            try
            {
                var s = await env.NewSession();
                var mockMark = await env.MockMark();
                var run = await env.Run(s.S("id")!, "always failing [s:error status=503 n=99]", 30_000);
                var notice = run.Role("notice").Single();
                Check.Equal("error", notice.P("meta").S("kind"));
                Check.Contains(RunResult.Text(notice), "503");
                Check.Equal(3, (await env.MockLog(mockMark)).Count(e => e.S("scenario") == "error"), "3 attempts");
                Check.Equal("idle", run.Final.S("status"));
            }
            finally { await env.Rpc("settings.set", new { path = "retry.maxAttempts", value = (object?)null }); }
        });

        r.Add("plugins: disabling tool repair removes its hook (textual call → nudge), re-enabling restores it", async () =>
        {
            await env.Rpc("plugins.setEnabled", new { id = "netpi.toolrepair", enabled = false });
            try
            {
                await Wait.UntilAsync(async () => (await env.Rpc("plugins.list")).Arr().First(p => p.S("id") == "netpi.toolrepair").S("state") == "disabled" ? "ok" : null, "disabled");
                var s = await env.NewSession();
                var run = await env.Run(s.S("id")!, "agents please [s:textcall]");
                Check.False(run.Role("assistant").Any(m => m.P("meta").B("repaired")), "not repaired");
                var notice = run.Role("notice").Single();
                Check.Equal("nudge", notice.P("meta").S("kind"));
                Check.Contains(RunResult.Text(notice), "tool call written as text");
                Check.Contains(run.FinalText, "NUDGE-RESUMED");
            }
            finally
            {
                await env.Rpc("plugins.setEnabled", new { id = "netpi.toolrepair", enabled = true });
            }
            await Wait.UntilAsync(async () => (await env.Rpc("plugins.list")).Arr().First(p => p.S("id") == "netpi.toolrepair").S("state") == "running" ? "ok" : null, "re-enabled");
            var s2 = await env.NewSession();
            var again = await env.Run(s2.S("id")!, "agents please [s:textcall]");
            Check.Contains(again.FinalText, "TEXTCALL-DONE");
        });

        r.Add("websocket: session-scoped events only reach subscribers; HTTP RPC fallback and auth", async () =>
        {
            var a = (await env.NewSession()).S("id")!;
            var b = (await env.NewSession()).S("id")!;
            await using var c2 = await NetPiClient.ConnectAsync(env.BaseUrl, Env.Token);
            await c2.Subscribe(a);
            await Task.Delay(100);
            var m2 = c2.Mark();
            await env.Run(b, "for b [s:echo]");
            await env.Run(a, "for a [s:echo]");
            await Task.Delay(200);
            var evs = c2.Since(m2);
            Check.False(evs.Any(e => e.Sid == b), "no scoped events of session b");
            Check.True(evs.Any(e => e.Type == "stream.delta" && e.Sid == a), "stream events of session a");
            Check.True(evs.Any(e => e.Type == "agent.status" && e.D.P("agent").S("sessionId") == b), "broadcast events of b still arrive");

            using var ok = new HttpRequestMessage(HttpMethod.Post, $"{env.BaseUrl}/api/rpc/sessions.get") { Content = new StringContent($"{{\"id\":\"{a}\"}}", Encoding.UTF8, "application/json") };
            ok.Headers.Add("X-NetPI-Token", Env.Token);
            var resp = await env.Http.SendAsync(ok);
            Check.Equal(HttpStatusCode.OK, resp.StatusCode);
            using (var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync())) Check.Equal(a, doc.RootElement.S("id"));
            var anon = await env.Http.PostAsync($"{env.BaseUrl}/api/rpc/sessions.list", new StringContent("{}"));
            Check.Equal(HttpStatusCode.Unauthorized, anon.StatusCode);
            using var missing = new HttpRequestMessage(HttpMethod.Post, $"{env.BaseUrl}/api/rpc/sessions.get") { Content = new StringContent("{\"id\":\"ses_nope\"}") };
            missing.Headers.Add("X-NetPI-Token", Env.Token);
            Check.Equal(HttpStatusCode.NotFound, (await env.Http.SendAsync(missing)).StatusCode);
        });

        r.Add("settings: editing settings.json on disk applies live (settings.changed, default model)", async () =>
        {
            var file = (await env.Rpc("settings.get")).S("path")!;
            var original = await File.ReadAllTextAsync(file);
            var node = System.Text.Json.Nodes.JsonNode.Parse(original)!.AsObject();
            node["defaultModel"] = "aiproxy/gemma-4";
            var mark = env.Client.Mark();
            try
            {
                await File.WriteAllTextAsync(file, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                await env.Client.WaitFor(mark, e => e.Type == "settings.changed", "settings.changed after an external edit", 10_000);
                Check.Equal("aiproxy/gemma-4", (await env.Rpc("models.list")).S("defaultModel"));
                var s = await env.NewSession();
                var mockMark = await env.MockMark();
                await env.Run(s.S("id")!, "which model? [s:echo]");
                Check.Equal("gemma-4", (await env.MockLog(mockMark)).Last().S("model"), "a session without a model uses the new default");
            }
            finally
            {
                var mark2 = env.Client.Mark();
                await File.WriteAllTextAsync(file, original);
                await env.Client.WaitFor(mark2, e => e.Type == "settings.changed", "settings restored", 10_000);
            }
            Check.Equal(CoreTests.Qwen, (await env.Rpc("models.list")).S("defaultModel"));
        });

        r.Add("sessions: automatic title from the first message, usage summary, projects CRUD", async () =>
        {
            var s = await env.Rpc("sessions.create", new { });
            var sid = s.S("id")!;
            await env.Run(sid, "Refactor the agent scheduler\nand more details [s:echo]");
            Check.Equal("Refactor the agent scheduler", (await env.Rpc("sessions.get", new { id = sid })).S("title"));
            var usage = await env.Rpc("usage.summary");
            var aiproxy = usage.Arr("providers").First(p => p.S("provider") == "aiproxy");
            Check.True(aiproxy.L("calls") > 10 && aiproxy.L("inputTokens") > 10_000, "usage recorded: " + aiproxy.GetRawText());
            var dir = env.NewProjectDir("crud");
            var p = await env.Rpc("projects.create", new { name = "crud", path = dir });
            var renamed = await env.Rpc("projects.update", new { id = p.S("id"), name = "crud2" });
            Check.Equal("crud2", renamed.S("name"));
            var s2 = await env.NewSession(projectId: p.S("id"));
            Check.True((await env.Rpc("projects.delete", new { id = p.S("id") })).GetBoolean());
            var detached = await env.Rpc("sessions.get", new { id = s2.S("id") });
            Check.True(detached.S("projectId") is null, "sessions are detached from a deleted project");
            var run = await env.Run(s2.S("id")!, "where now [s:where file=after-delete.txt]");
            Check.Contains(run.FinalText, "PWD=" + Env.BashPath((await env.Rpc("app.info")).S("defaultWorkspace")!));
        });
    }

    private static async Task Assert404(Env env, string method, object p)
    {
        try
        {
            await env.Rpc(method, p);
            throw new AssertException($"{method} should fail with not_found");
        }
        catch (RpcError e)
        {
            Check.Equal("not_found", e.Code);
        }
    }
}
