using System.Text;
using System.Text.Json;

namespace NetPI.E2E;

/// <summary>Startup, catalog, the basic chat loop, real tool execution, transports and providers.</summary>
public static class CoreTests
{
    public static readonly string[] PluginIds =
    [
        "netpi.runtime", "netpi.agentsmd", "netpi.compaction", "netpi.context", "netpi.diagnostics", "netpi.ideas", "netpi.agents",
        "netpi.nudge", "netpi.providers.aiproxy", "netpi.providers.anthropic", "netpi.providers.openrouter", "netpi.retry", "netpi.toolrepair",
        "netpi.tools.agents", "netpi.tools.files", "netpi.tools.shell", "netpi.work",
    ];

    public const string Qwen = "aiproxy/qwen3.8-27b";

    /// <summary>A project folder with an LF and a CRLF text file.</summary>
    public static void Seed(string dir)
    {
        File.WriteAllText(Path.Combine(dir, "notes.txt"), "alpha line\nbeta line\ngamma line\n");
        File.WriteAllBytes(Path.Combine(dir, "crlf.txt"), Encoding.UTF8.GetBytes("first\r\nbeta value\r\nthird\r\n"));
        Directory.CreateDirectory(Path.Combine(dir, "src"));
        File.WriteAllText(Path.Combine(dir, "src", "app.cs"), "// alpha\nclass App {}\n");
    }

    public static void Register(TestRunner r, Env env)
    {
        r.Add("startup.plugins", "startup: all plugins running, tabs, commands and tools registered", async () =>
        {
            var plugins = await env.Rpc("plugins.list");
            foreach (var id in PluginIds)
            {
                var p = plugins.Arr().FirstOrDefault(x => x.S("id") == id);
                Check.True(p.ValueKind == JsonValueKind.Object, $"plugin {id} is listed");
                Check.Equal("running", p.S("state"), $"state of {id} (error: {p.S("error")})");
            }
            var tabs = (await env.Rpc("ui.tabs")).Arr().Select(t => $"{t.S("pluginId")}/{t.S("id")}").ToList();
            foreach (var t in new[] { "netpi.tools.files/files", "netpi.work/work", "netpi.ideas/ideas", "netpi.diagnostics/diagnostics" })
                Check.True(tabs.Contains(t), $"tab {t} registered: {string.Join(", ", tabs)}");
            var commands = (await env.Rpc("ui.commands")).Arr().Select(c => c.S("name")).ToList();
            foreach (var c in new[] { "compact", "reload", "idea" }) Check.True(commands.Contains(c), $"slash command /{c}");
            var tools = (await env.Rpc("tools.list")).Arr().Where(t => t.B("active")).Select(t => t.S("name")).ToList();
            foreach (var t in new[] { "read", "write", "edit", "grep", "find", "ls", "bash", "agent_spawn", "agent", "agent_choices", "process", "browser", "ideas", "skill" })
                Check.True(tools.Contains(t), $"tool {t} active");
            // plugin UI bundles are served
            foreach (var id in new[] { "netpi.work", "netpi.ideas", "netpi.diagnostics", "netpi.tools.files" })
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, $"{env.BaseUrl}/plugins/{id}/ui.js");
                var resp = await env.Http.SendAsync(req);
                Check.Equal(200, (int)resp.StatusCode, $"GET /plugins/{id}/ui.js");
            }
            var info = await env.Rpc("app.info");
            Check.Equal(Path.GetFullPath(env.Home), Path.GetFullPath(info.S("home")!), "app.info home");
        });

        r.Add("startup.models", "models: mock catalog, default model; no agents set up, nothing running", async () =>
        {
            var list = await env.Rpc("models.list", new { refresh = true });
            var models = list.Arr("models").ToList();
            JsonElement M(string r) => models.FirstOrDefault(m => m.S("ref") == r);
            var qwen = M(Qwen);
            Check.True(qwen.ValueKind == JsonValueKind.Object, "qwen listed: " + string.Join(", ", models.Select(m => m.S("ref"))));
            Check.Equal(262144L, qwen.L("contextWindow"));
            Check.Equal(16384L, qwen.L("maxOutputTokens"));
            Check.Equal(2L, qwen.L("concurrency"));
            Check.Equal("loaded", qwen.S("status"));
            Check.True(qwen.B("isLocal"), "aiproxy models are local");
            Check.Equal("none,low,medium,xhigh", string.Join(",", qwen.P("reasoning").Arr("efforts").Select(e => e.GetString())));
            Check.Equal("text,image", string.Join(",", qwen.Arr("inputModalities").Select(e => e.GetString())));
            Check.Equal((long)env.Options.TinyContext, M("aiproxy/tiny-ctx").L("contextWindow"));
            Check.Equal("stopped", M("aiproxy/qwen38-27b-iq3s").S("status"));
            var claude = M("anthropic/claude-sonnet-4-5");
            Check.True(claude.ValueKind == JsonValueKind.Object, "anthropic model listed");
            Check.True(claude.P("reasoning").B("supported"), "claude supports thinking");
            Check.Equal(Qwen, list.S("defaultModel"), "default model = first loaded local model");

            var pools = await env.Rpc("agents.list");
            Check.False(pools.Arr().Any(p => p.B("configured")), "the suite's settings set up no agents: " + pools.GetRawText());
        });

        r.Add("chat.stream", "chat: default model streams thinking + markdown, events in order, message persisted", async () =>
        {
            var s = await env.NewSession();
            var sid = s.S("id")!;
            var mockMark = await env.MockMark();
            var run = await env.Run(sid, "Hello NetPI, please echo this.");
            var types = run.Types;
            int First(string t) => types.IndexOf(t);
            int Last(string t) => types.LastIndexOf(t);
            Check.True(First("stream.start") >= 0, "stream.start: " + string.Join(" ", types));
            Check.True(run.OfType("stream.delta").Any(e => e.D.S("kind") == "thinking"), "thinking deltas");
            Check.True(run.OfType("stream.delta").Any(e => e.D.S("kind") == "text"), "text deltas");
            Check.True(First("stream.start") < First("stream.delta") && Last("stream.delta") < First("stream.end"), "start < deltas < end");
            var assistantAdded = run.Events.FindIndex(e => e.Type == "message.added" && e.D.P("message").S("role") == "assistant");
            Check.True(assistantAdded > First("stream.end"), "assistant message.added after stream.end");
            Check.True(run.Events.FindIndex(e => e.Type == "message.added" && e.D.P("message").S("role") == "user") < First("stream.start"), "user message first");
            Check.True(run.OfType("agent.status").Any(e => e.D.P("agent").S("status") == "running"), "agent.status running");
            Check.True(run.OfType("session.context").Any(), "session.context event");
            Check.True(run.OfType("usage.recorded").Any(), "usage.recorded event");
            var streamed = string.Concat(run.OfType("stream.delta").Where(e => e.D.S("kind") == "text").Select(e => e.D.S("text")));

            Check.Equal(2, run.Messages.Count, "user + assistant");
            var a = run.LastAssistant;
            Check.Equal(streamed, run.FinalText, "streamed text == persisted text");
            Check.Contains(run.FinalText, "ECHO-DONE");
            Check.Contains(run.FinalText, "Hello NetPI");
            Check.Equal("stop", a.S("stopReason"));
            Check.Equal("aiproxy", a.S("provider"));
            Check.Equal("qwen3.8-27b", a.S("model"));
            Check.True(a.P("usage").L("inputTokens") > 0 && a.P("usage").L("outputTokens") > 0, "usage persisted");
            var thinking = a.Arr("parts").First(p => p.S("type") == "thinking");
            Check.Contains(thinking.S("text"), "echo");
            Check.True(thinking.Has("durationMs"), "thinking duration");
            Check.Equal("idle", run.Final.S("status"));

            var session = await env.Rpc("sessions.get", new { id = sid });
            Check.Equal(3L, session.L("messageCount"), "user, working-directory notice, assistant");
            Check.True(session.L("contextTokens") > 0, "session context tokens");
            var log = (await env.MockLog(mockMark)).Where(e => e.S("scenario") == "default").ToList();
            Check.Equal(1, log.Count, "one model request");
            Check.Equal("responses", log[0].S("api"), "AiProxy defaults to the Responses API");
            Check.True(log[0].L("tools") >= 10, "tools sent to the model");
        });

        r.Add("chat.fork", "fork: sessions.fork copies the chat up to a message; the fork goes on with its prompt, the original stays", async () =>
        {
            var s = await env.NewSession();
            var sid = s.S("id")!;
            await env.Run(sid, "First question, please echo.");
            await env.Run(sid, "Second question, please echo.");
            async Task<List<JsonElement>> Messages(string id) => (await env.Rpc("sessions.messages", new { id, limit = 200 })).Arr("messages").ToList();
            var original = await Messages(sid);
            var firstAnswer = original.First(m => m.S("role") == "assistant").L("seq");

            var mark = env.Client.Mark();
            var fork = await env.Rpc("sessions.fork", new { id = sid, upToSeq = firstAnswer });
            var fid = fork.S("id")!;
            Check.Equal("e2e (fork)", fork.S("title"));
            Check.Equal(sid, fork.P("meta").P("forkedFrom").S("sessionId"));
            Check.Equal(firstAnswer, fork.P("meta").P("forkedFrom").L("seq"));
            var copied = await Messages(fid);
            Check.Equal(string.Join(",", original.Where(m => m.L("seq") <= firstAnswer).Select(m => m.L("seq"))), string.Join(",", copied.Select(m => m.L("seq"))), "the same seqs");
            Check.Equal(original.Count, (await Messages(sid)).Count, "the original stays");
            var forked = await env.Client.WaitFor(mark, e => e.Type == "session.forked" && e.D.S("sessionId") == fid, "session.forked", 5000);
            Check.Equal(sid, forked.D.S("fromSessionId"));

            // the prompt the original was sent goes on in the fork (the context plugin copies it on session.forked)
            var originalPrompt = (await env.Rpc("context.prompts", new { sessionId = sid })).Arr("prompts").First().S("systemPrompt");
            await Wait.UntilAsync(async () => (await env.Rpc("context.prompts", new { sessionId = fid })).Arr("prompts").Any() ? "ok" : null, "the fork's prompt", 5000);
            Check.Equal(originalPrompt, (await env.Rpc("context.prompts", new { sessionId = fid })).Arr("prompts").Single().S("systemPrompt"));
            var run = await env.Run(fid, "Third question, in the fork, please echo.");
            Check.Contains(run.FinalText, "ECHO-DONE");
            Check.Equal(1, (await env.Rpc("context.prompts", new { sessionId = fid })).Arr("prompts").Count(), "no new render: the same prompt");
            Check.Equal("e2e (fork 2)", (await env.Rpc("sessions.fork", new { id = sid })).S("title"), "the next fork of the same chat");

            // a subagent's chat is not forked
            var bgMark = env.Client.Mark();
            var bg = await env.Rpc("agent.send", new { sessionId = sid, text = "Start a background job [s:spawnbg delay=100]" });
            await env.WaitIdle(sid, bgMark, bg.L("runs"));
            var sub = (await env.Rpc("sessions.list", new { includeSubagents = true, parentSessionId = sid })).Arr().First(x => x.S("kind") == "subagent");
            try
            {
                await env.Rpc("sessions.fork", new { id = sub.S("id") });
                throw new AssertException("expected bad_request");
            }
            catch (RpcError ex) { Check.Equal("bad_request", ex.Code); }
            // The worker's report reaches the parent (waking it, or in the middle of its run). The test ends once the parent has
            // answered it: a run still going when the test returns makes its model requests inside the next test's window.
            await env.WaitReportHandled(sid, bgMark);
        });

        r.Add("tools.files", "tools: ls/read/edit/write really run in the project (CRLF kept), results persisted, paging", async () =>
        {
            var p = await env.NewProject("tools", Seed);
            var dir = p.S("path")!;
            var s = await env.NewSession(projectId: p.S("id"));
            var sid = s.S("id")!;
            var run = await env.Run(sid, "Edit the CRLF file please [s:tools file=crlf.txt old=beta new=BETA-EDITED create=created/by-mock.txt]");

            Check.Contains(run.FinalText, "TOOLS-DONE");
            var starts = run.OfType("tool.start").Select(e => e.D.S("name")).ToList();
            Check.Equal("ls,read,edit,write", string.Join(",", starts), "tool.start order");
            Check.Equal(4, run.OfType("tool.end").Count(), "tool.end count");
            Check.True(run.OfType("tool.end").All(e => !e.D.B("isError")), "no tool errors: " + string.Join(" | ", run.Parts("tool_result").Select(x => x.S("content"))));
            Check.True(run.OfType("stream.tool").Count() >= 4, "stream.tool events");
            foreach (var st in run.OfType("tool.start"))
            {
                var end = run.Events.FindIndex(e => e.Type == "tool.end" && e.D.S("callId") == st.D.S("callId"));
                Check.True(end > run.Events.IndexOf(st), "tool.end after tool.start");
            }

            // files on disk
            var bytes = File.ReadAllBytes(Path.Combine(dir, "crlf.txt"));
            var text = Encoding.UTF8.GetString(bytes);
            Check.Equal("first\r\nBETA-EDITED value\r\nthird\r\n", text, "CRLF file edited in place with CRLF preserved");
            Check.Equal("created by the mock model\nsecond line\n", File.ReadAllText(Path.Combine(dir, "created", "by-mock.txt")));

            // results persisted as tool messages, one per call, matching the assistant's calls
            var calls = run.Parts("tool_call").ToList();
            var results = run.Parts("tool_result").ToList();
            Check.Equal(4, calls.Count);
            Check.Equal(4, results.Count);
            Check.Equal(string.Join(",", calls.Select(c => c.S("id"))), string.Join(",", results.Select(x => x.S("callId"))), "results answer the calls in order");
            var edit = results.First(x => x.S("name") == "edit");
            Check.Contains(edit.P("details").S("diff"), "+BETA-EDITED value");
            Check.Equal("crlf", edit.P("details").S("eol"));
            Check.Contains(results.First(x => x.S("name") == "ls").S("content"), "notes.txt");
            Check.Contains(results.First(x => x.S("name") == "read").S("content"), "beta value");
            Check.True(results.All(x => x.Has("durationMs")), "durations");

            // message order: user, (assistant, tool+)*, assistant
            var roles = string.Join(",", run.Messages.Select(m => m.S("role")));
            Check.Equal("user,assistant,tool,assistant,tool,assistant,tool,tool,assistant", roles);
            var seqs = run.AllMessages.Select(m => m.L("seq")).ToList();
            Check.True(seqs.Zip(seqs.Skip(1)).All(x => x.First < x.Second), "seq ascending");

            // paging with beforeSeq
            var collected = new List<long>();
            long? before = null;
            var pages = 0;
            while (true)
            {
                var page = await env.Rpc("sessions.messages", new { id = sid, limit = 4, beforeSeq = before });
                var msgs = page.Arr("messages").ToList();
                pages++;
                collected.InsertRange(0, msgs.Select(m => m.L("seq")));
                if (!page.B("hasMore")) break;
                before = msgs[0].L("seq");
                Check.True(pages < 10, "paging terminates");
            }
            Check.Equal(3, pages, "10 messages (with the working-directory notice) in pages of 4");
            Check.Equal(string.Join(",", seqs), string.Join(",", collected), "pages cover every message exactly once");
        });

        r.Add("tools.parallel", "tools: parallel read-only calls run concurrently and all results persist", async () =>
        {
            var p = await env.NewProject("parallel", Seed);
            var s = await env.NewSession(projectId: p.S("id"));
            var run = await env.Run(s.S("id")!, "Look around [s:parallel]");
            Check.Contains(run.FinalText, "PARALLEL-DONE");
            Check.Equal(4, run.Parts("tool_result").Count());
            Check.True(run.Parts("tool_result").All(x => !x.B("isError")), "no errors");
            // all four started before the first ended (they run in parallel)
            var firstEnd = run.Events.FindIndex(e => e.Type == "tool.end");
            Check.Equal(4, run.Events.Take(firstEnd).Count(e => e.Type == "tool.start"), "all tool.start before the first tool.end");
            Check.Contains(run.Parts("tool_result").First(x => x.S("name") == "grep").S("content"), "notes.txt");
        });

        r.Add("tools.bash", "bash: live tool.output, exit code and output in the result", async () =>
        {
            var s = await env.NewSession();
            var run = await env.Run(s.S("id")!, "Run something [s:bash]");
            Check.Contains(run.FinalText, "BASH-DONE");
            var outputs = run.OfType("tool.output").ToList();
            Check.True(outputs.Count >= 2, $"streamed output in several chunks ({outputs.Count})");
            var live = string.Concat(outputs.Select(o => o.D.S("chunk")));
            Check.Contains(live, "line 3");
            var result = run.Parts("tool_result").Single();
            Check.Contains(result.S("content"), "hello from bash\nline 1\nline 2\nline 3");
            Check.Equal(0L, result.P("details").L("exitCode"));
            Check.Equal("exited", result.P("details").S("status"));
            Check.Contains(run.FinalText, "line 2");
            var procs = await env.Rpc("processes.list");
            Check.True(procs.Arr().Any(x => (x.S("command") ?? "").Contains("hello from bash")), "processes.list has the run");
        });

        r.Add("provider.chat-transport", "transport: chat completions via per-model setting, reasoning effort passed through", async () =>
        {
            await env.Rpc("settings.set", new { path = "providers.aiproxy.models.gemma-4", value = new { transport = "chat" } });
            var p = await env.NewProject("chat", Seed);
            var s = await env.NewSession(model: "aiproxy/gemma-4", projectId: p.S("id"), reasoning: "max");
            var mark = await env.MockMark();
            var run = await env.Run(s.S("id")!, "Edit notes [s:tools file=notes.txt old=gamma new=GAMMA-CHAT]");
            Check.Contains(run.FinalText, "TOOLS-DONE");
            Check.Equal("alpha line\nbeta line\nGAMMA-CHAT line\n", File.ReadAllText(Path.Combine(p.S("path")!, "notes.txt")));
            var log = await env.MockLog(mark);
            Check.True(log.Count >= 4, "4 model calls");
            Check.True(log.All(e => e.S("api") == "chat" && e.S("model") == "gemma-4"), "all via chat completions: " + string.Join(",", log.Select(e => e.S("api"))));
            Check.True(log.All(e => e.S("effort") == "max"), "reasoning_effort=max");
            Check.True(log.All(e => e.L("status") == 200), "no request rejected: " + string.Join(" | ", log.Select(e => e.S("error"))));
            Check.True(run.Parts("thinking").Any(), "reasoning_content became thinking parts");

            // the default transport for other models is still Responses
            var s2 = await env.NewSession(model: Qwen, reasoning: "xhigh");
            var mark2 = await env.MockMark();
            await env.Run(s2.S("id")!, "hi");
            var log2 = await env.MockLog(mark2);
            Check.Equal("responses", log2.Single().S("api"));
            Check.Equal("xhigh", log2.Single().S("effort"));
            await env.Rpc("settings.set", new { path = "providers.aiproxy.models.gemma-4", value = (object?)null });
        });

        r.Add("provider.anthropic-thinking", "anthropic: thinking with signatures replayed across tool_use turns", async () =>
        {
            var p = await env.NewProject("claude", Seed);
            var s = await env.NewSession(model: "anthropic/claude-sonnet-4-5", projectId: p.S("id"));
            var mark = await env.MockMark();
            var run = await env.Run(s.S("id")!, "Edit notes [s:tools file=notes.txt old=alpha new=ALPHA-CLAUDE]");
            var log = await env.MockLog(mark);
            Check.True(log.All(e => e.L("status") == 200), "Anthropic accepted every request: " + string.Join(" | ", log.Select(e => e.S("error"))));
            Check.Contains(run.FinalText, "TOOLS-DONE");
            Check.Equal("ALPHA-CLAUDE line\nbeta line\ngamma line\n", File.ReadAllText(Path.Combine(p.S("path")!, "notes.txt")));
            Check.True(log.All(e => e.S("api") == "anthropic"), "anthropic API");
            Check.True(log.All(e => e.B("thinkingEnabled")), "thinking stayed enabled on every turn: " + string.Join(",", log.Select(e => e.B("thinkingEnabled"))));
            Check.True(log.Skip(1).All(e => e.B("thinkingReplayed")), "signed thinking blocks replayed");
            var thinking = run.Parts("thinking").ToList();
            Check.True(thinking.Count >= 4 && thinking.All(t => (t.S("signature") ?? "").StartsWith("mocksig_")), "signatures persisted");
            Check.Equal("anthropic", run.LastAssistant.S("provider"));
            Check.Equal(4, run.Parts("tool_result").Count());
        });
    }
}
