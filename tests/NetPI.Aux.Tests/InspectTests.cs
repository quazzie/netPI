using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using NetPI.Diagnostics;

namespace NetPI.Aux.Tests;

/// <summary>The diag.* inspection surface of the diagnostics plugin (docs/DEBUGGING.md).</summary>
public static class InspectTests
{
    public static void Register(TestRunner r)
    {
        r.Add("inspect: model calls through the middleware (first token, retries, tokens, errors, cancel, running)", ModelCalls);
        r.Add("inspect: the journal leaves out per-token events and sums up messages; tool calls from tool.start/end", JournalAndTools);
        r.Add("inspect: problems (failed plugin, waiters on an inactive agent, a long wait, errors in the log)", Problems);
        r.Add("inspect: settings without secrets, saved failed requests, the overview lists the diag methods", SettingsFailuresOverview);
        r.Add("inspect: the diag tool answers with the RPC's JSON, defaults to the calling session and refuses every write", DiagTool);
        r.Add("inspect: diag rpc reaches any read-only method and nothing else; a journal type query is global", DiagRpcAndJournalScope);
        r.Add("inspect: a reload says what it cost: the tools at stake, or that a hook-only plugin announces nothing", ReloadImpact);
    }

    // Without the rpc action, reaching anything diag does not wrap meant reading the live token out of server.json and
    // hand-rolling a POST. With it, the gate is the registration's own readOnly flag — a fact, not a name pattern — and
    // the second half is the trap that cost an hour once: a journal query with a type but no session was scoped to the
    // calling chat, so a type filter that could not have seen the event looked like the event never happened.
    private static async Task DiagRpcAndJournalScope()
    {
        var ctx = await StartAsync();
        var tool = ctx.ToolsFake.Tools.OfType<DiagTool>().Single();
        RpcRequest? seen = null;
        ctx.RpcFake.Register("events.recent", (r, _) => { seen = r; return Task.FromResult<object?>(new JsonArray()); }, "recent bus events", readOnly: true);
        ctx.RpcFake.Register("sessions.delete", (_, _) => Task.FromResult<object?>(true), "Delete a session");
        // the host's own list, marked like the real one, so "what can I call" is answerable from inside
        ctx.RpcFake.Register("rpc.list", (_, _) => Task.FromResult<object?>(ctx.RpcFake.List()), "RPC methods", readOnly: true);

        var ok = await Call(tool, """{ "action": "rpc", "method": "events.recent", "params": { "max": 100 } }""");
        Check.False(ok.IsError, ok.Content);
        Check.Equal(100, seen!.Int("max"), "the method's own parameters are passed on");
        // and a quoted one, which is what a model sends for a value nested in an object
        await Call(tool, """{ "action": "rpc", "method": "events.recent", "params": { "max": "7" } }""");
        Check.Equal(7, seen!.Int("max"), "a model that quotes a number means the number");
        // it has to be in the schema's enum, or the model is never told it exists
        var actions = ((JsonArray)tool.Definition.Parameters["properties"]!["action"]!["enum"]!).Select(a => a.Str()).ToList();
        Check.True(actions.Contains("rpc"), "rpc is in the action enum");
        Check.True(tool.Definition.Parameters["properties"]!.AsObject().ContainsKey("method"), "and method is a parameter");

        var write = await Call(tool, """{ "action": "rpc", "method": "sessions.delete" }""");
        Check.True(write.IsError, "an unmarked method may write, so it is not reachable");
        Check.Contains(write.Content, "may change the app");
        Check.Contains(write.Content, "/reload", "and it says who may");

        foreach (var arg in new[]
                 {
                     """{ "action": "rpc" }""",
                     """{ "action": "rpc", "method": "diag.rpc" }""",
                     """{ "action": "rpc", "method": "diag.reload" }""",
                     """{ "action": "rpc", "method": "nope.nope" }""",
                 })
        {
            var refused = await Call(tool, arg);
            Check.True(refused.IsError, arg);
        }
        Check.Contains((await Call(tool, """{ "action": "rpc" }""")).Content, "rpc.list", "and it says where the list is");

        // the list itself is reachable, and it says which are read-only
        var list = await Call(tool, """{ "action": "rpc", "method": "rpc.list" }""");
        Check.False(list.IsError, list.Content);
        var methods = (JsonArray)NetPiJson.ToNode(await ctx.RpcFake.Call("rpc.list"))!;
        Check.True(methods.First(m => m!["method"]!.Str() == "events.recent")!["readOnly"]!.GetValue<bool>(), "rpc.list reports the flag the gate reads");
        Check.False(methods.First(m => m!["method"]!.Str() == "sessions.delete")!["readOnly"]!.GetValue<bool>(), "an unmarked one is not");

        // the journal: a type query is global unless a session is named
        RpcRequest? journal = null;
        ctx.RpcFake.Register("diag.journal", (r, _) => { journal = r; return Task.FromResult<object?>(new JsonArray()); }, "", readOnly: true);
        await Call(tool, """{ "action": "journal", "type": "session.changed" }""");
        Check.Equal(null, journal!.Str("sessionId"), "a type filter is a question about every session");
        Check.Equal("session.changed", journal.Str("type"));
        await Call(tool, """{ "action": "journal" }""");
        Check.Equal("ses_caller", journal!.Str("sessionId"), "without a type it is this chat's timeline");
        await Call(tool, """{ "action": "journal", "type": "session.changed", "sessionId": "ses_other" }""");
        Check.Equal("ses_other", journal!.Str("sessionId"), "and a named session still wins");
    }

    // Only a plugin that registers tools can take a tool away. A hook-only reload (context, nudge) swaps under a
    // running turn and announces nothing, and the report must say that rather than claim a notice.
    private static async Task ReloadImpact()
    {
        var ctx = await StartAsync();
        var runtime = new FakeAgentRuntime();
        runtime.Agents.Add(new AgentInfo { Id = "agt_1", SessionId = "ses_1", Status = AgentStatus.Running });
        ctx.ServicesFake.Register<IAgentRuntime>(runtime);
        ctx.ToolsFake.Register(new StubTool("probe"), "netpi.tools.web");

        // a tool plugin: the tools are named, and a chat that holds one is told to expect a notice
        ctx.Bus.Publish(new BusEvent
        {
            Type = EventTypes.PluginsReloaded,
            Data = new JsonObject { ["ids"] = new JsonArray("netpi.tools.web"), ["kind"] = "reload" },
        });
        await ctx.Bus.WaitForAsync(EventTypes.PluginsReloaded);
        var problems = (JsonArray)(await ctx.RpcFake.Call("diag.problems"))!;
        var withTools = problems.Select(p => (string?)p!["message"] ?? "").FirstOrDefault(m => m.Contains("netpi.tools.web"))!;
        Check.Contains(withTools, "1 chat(s) mid-turn", withTools);
        Check.Contains(withTools, "probe", "the tool at stake is named");
        Check.Contains(withTools, "gets a notice", withTools);

        // a hook-only plugin: nothing goes away, and the line says so
        ctx.Bus.Publish(new BusEvent
        {
            Type = EventTypes.PluginsReloaded,
            Data = new JsonObject { ["ids"] = new JsonArray("netpi.context"), ["kind"] = "reload" },
        });
        await ctx.Bus.WaitForAsync(EventTypes.PluginsReloaded, e => e.As<JsonObject>()?["ids"] is JsonArray { Count: 1 } a && a[0]?.GetValue<string>() == "netpi.context");
        problems = (JsonArray)(await ctx.RpcFake.Call("diag.problems"))!;
        var hookOnly = problems.Select(p => (string?)p!["message"] ?? "").FirstOrDefault(m => m.Contains("netpi.context"))!;
        Check.Contains(hookOnly, "registers no tools, so nothing is announced", hookOnly);
        Check.NotContains(hookOnly, "gets a notice", hookOnly);

        // and the overview's record carries the same, machine-readable
        var overview = (JsonObject)(await ctx.RpcFake.Call("diag.overview"))!;
        var reloads = (JsonArray)overview["reloads"]!;
        var hookRecord = (JsonObject)reloads.First(r => r!["ids"]!.ToJsonString().Contains("netpi.context"))!;
        Check.Equal(0, ((JsonArray)hookRecord["tools"]!).Count, "no tools at stake");
        Check.Equal(1, ((JsonArray)hookRecord["busySessions"]!).Count, "the chat that was running");
    }

    private sealed class StubTool(string name) : IAgentTool
    {
        public ToolDefinition Definition { get; } = new() { Name = name, Description = name };
        public Task<ToolResult> ExecuteAsync(ToolContext context, JsonElement args, CancellationToken ct) => Task.FromResult(ToolResult.Ok("ok"));
    }

    /// <summary>Run the plugin's own tool as the runtime would (the arguments arrive as JSON text).</summary>
    private static Task<ToolResult> Call(DiagTool tool, string args, string sessionId = "ses_caller") =>
        tool.ExecuteAsync(new ToolContext
        {
            SessionId = sessionId, AgentId = "agt_1", CallId = "call_1", Cwd = ".",
            Services = new FakeServices(), Events = new FakeBus(),
        }, JsonDocument.Parse(args).RootElement, CancellationToken.None);

    private static async Task<FakePluginContext> StartAsync()
    {
        var ctx = new FakePluginContext(pluginId: "netpi.diagnostics");
        await new DiagnosticsPlugin().StartAsync(ctx, CancellationToken.None);
        return ctx;
    }

    private static ModelRequest Request(string sid = "ses_1") => new()
    {
        Model = T.Model("m1"), SessionId = sid, AgentId = "agt_1", Purpose = "agent", SystemPrompt = "system",
        Messages = [new ChatMessage { Role = MessageRole.User, Parts = [new TextPart { Text = "write a story" }] }],
    };

    private static async IAsyncEnumerable<ModelStreamEvent> Stream(IEnumerable<Func<Task<ModelStreamEvent?>>> steps, [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var step in steps)
        {
            ct.ThrowIfCancellationRequested();
            if (await step() is { } e) yield return e;
        }
    }

    private static Func<Task<ModelStreamEvent?>> Yield(ModelStreamEvent e, int delayMs = 0) => async () =>
    {
        if (delayMs > 0) await Task.Delay(delayMs);
        return e;
    };

    private static async Task Drain(IAsyncEnumerable<ModelStreamEvent> s)
    {
        await foreach (var _ in s) { }
    }

    private static async Task ModelCalls()
    {
        var ctx = await StartAsync();
        var mw = ctx.ServicesFake.Get<IModelMiddleware>() ?? throw new AssertException("the call recorder is a model middleware");
        Check.Equal(-1000, mw.Order, "outermost: one record per call as the caller saw it");

        var done = new ChatMessage
        {
            Role = MessageRole.Assistant, StopReason = "tool_use",
            Parts = [new TextPart { Text = "hello" }, new ToolCallPart { Id = "c1", Name = "read", Arguments = "{}" }],
            Usage = new Usage { InputTokens = 1200, CacheReadTokens = 800, OutputTokens = 40 },
        };
        await Drain(mw.InvokeAsync(Request(), (_, c) => Stream(
        [
            Yield(new StreamReset("connection lost"), 5), Yield(new ThinkingDelta("hmm"), 30), Yield(new TextDelta("hello")),
            Yield(new StreamCompleted(done)),
        ], c), CancellationToken.None));

        var calls = (JsonArray)(await ctx.RpcFake.Call("diag.calls"))!;
        var ok = calls[0]!;
        Check.Equal("ok", ok["state"].Str());
        Check.Equal("test/m1", ok["model"].Str());
        Check.Equal("ses_1", ok["sessionId"].Str());
        Check.Equal("agt_1", ok["runId"].Str());
        Check.True((long)ok["firstTokenMs"]! >= 25, $"first token after the reset and 30 ms: {ok["firstTokenMs"]}");
        Check.True((long)ok["durationMs"]! >= (long)ok["firstTokenMs"]!);
        Check.Equal(2, (int)ok["attempts"]!, "one reset = two attempts");
        Check.Equal(40L, (long)ok["outputTokens"]!);
        Check.Equal("tool_use", ok["stopReason"].Str());
        var detail = (JsonObject)(await ctx.RpcFake.Call("diag.call", new JsonObject { ["id"] = (long)ok["id"]! }))!;
        Check.Equal(5, (int)detail["response"]!["textChars"]!);
        Check.Equal("read", detail["response"]!["toolCalls"]![0].Str());
        Check.Contains(detail["resets"]![0].Str(), "connection lost");
        Check.Equal("write a story", detail["request"]!["lastUser"].Str());
        Check.Equal(1, (int)detail["request"]!["messages"]!);

        // an error: the message, and its type and status in the detail
        await Check.ThrowsAsync<ModelException>(() => Drain(mw.InvokeAsync(Request(), (_, c) => Stream(
        [
            Yield(new TextDelta("x")), () => throw new ModelException("boom [x-request-id: r1]", transient: false, statusCode: 500, errorType: "server_error"),
        ], c), CancellationToken.None)));
        var failed = ((JsonArray)(await ctx.RpcFake.Call("diag.calls", new JsonObject { ["errors"] = true }))!).Single()!;
        Check.Equal("error", failed["state"].Str());
        Check.Contains(failed["error"].Str(), "boom [x-request-id: r1]");
        var failedDetail = (JsonObject)(await ctx.RpcFake.Call("diag.call", new JsonObject { ["id"] = (long)failed["id"]! }))!;
        Check.Equal(500, (int)failedDetail["errorDetail"]!["status"]!);
        Check.Equal("server_error", failedDetail["errorDetail"]!["type"].Str());

        // the caller stops reading (a steer): cancelled
        await foreach (var _ in mw.InvokeAsync(Request(), (_, c) => Stream([Yield(new TextDelta("a")), Yield(new TextDelta("b"))], c), CancellationToken.None))
            break;
        Check.Equal("cancelled", ((JsonArray)(await ctx.RpcFake.Call("diag.calls"))!)[0]!["state"].Str());

        // in progress: listed as running, then done
        var gate = new TaskCompletionSource();
        var slow = Drain(mw.InvokeAsync(Request("ses_2"), (_, c) => Stream(
            [async () => { await gate.Task; return new TextDelta("late"); }, Yield(new StreamCompleted(new ChatMessage { Role = MessageRole.Assistant }))], c), CancellationToken.None));
        for (var i = 0; i < 100 && ((JsonArray)(await ctx.RpcFake.Call("diag.calls", new JsonObject { ["running"] = true }))!).Count == 0; i++) await Task.Delay(20);
        var running = ((JsonArray)(await ctx.RpcFake.Call("diag.calls", new JsonObject { ["running"] = true }))!).Single()!;
        Check.Equal("ses_2", running["sessionId"].Str());
        Check.True(running["firstTokenMs"] is null, "no token yet");
        gate.SetResult();
        await slow;
        Check.Equal("ok", ((JsonArray)(await ctx.RpcFake.Call("diag.calls", new JsonObject { ["sessionId"] = "ses_2" }))!).Single()!["state"].Str());
    }

    private static async Task JournalAndTools()
    {
        var ctx = await StartAsync();
        ctx.Events.Publish(EventTypes.StreamDelta, new JsonObject { ["text"] = "token" }, "ses_1");
        ctx.Events.Publish(EventTypes.MessageAdded, new JsonObject
        {
            ["message"] = new JsonObject
            {
                ["role"] = "assistant", ["seq"] = 3, ["stopReason"] = "stop", ["meta"] = new JsonObject { ["kind"] = "notice-kind" },
                ["parts"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "hello world " + new string('x', 400) }),
            },
        }, "ses_1");
        ctx.Events.Publish(EventTypes.ToolStart, new JsonObject { ["sessionId"] = "ses_1", ["agentId"] = "agt_1", ["callId"] = "c1", ["name"] = "bash", ["arguments"] = "{\"command\":\"ls -la\"}" }, "ses_1");
        ctx.Events.Publish(EventTypes.ToolEnd, new JsonObject { ["sessionId"] = "ses_1", ["callId"] = "c1", ["name"] = "bash", ["isError"] = true, ["durationMs"] = 12 }, "ses_1");
        ctx.Events.Publish(EventTypes.ToolStart, new JsonObject { ["sessionId"] = "ses_1", ["callId"] = "c2", ["name"] = "read", ["arguments"] = "{}" }, "ses_1");

        var journal = (JsonArray)(await ctx.RpcFake.Call("diag.journal", new JsonObject { ["sessionId"] = "ses_1" }))!;
        Check.Equal("message.added,tool.start,tool.end,tool.start", string.Join(",", journal.Select(e => e!["type"].Str())), "oldest first, no stream.delta");
        var msg = journal[0]!["data"]!;
        Check.Equal("assistant", msg["role"].Str());
        Check.Equal("notice-kind", msg["kind"].Str());
        Check.True(msg["text"].Str()!.Length <= 161 && msg["text"].Str()!.StartsWith("hello world"), "a preview, not the message");

        var tools = (JsonArray)(await ctx.RpcFake.Call("diag.tools"))!;
        Check.Equal("c2,c1", string.Join(",", tools.Select(t => t!["callId"].Str())), "newest first");
        Check.Equal("running", tools[0]!["state"].Str());
        Check.Equal("error", tools[1]!["state"].Str());
        Check.Equal(12L, (long)tools[1]!["durationMs"]!);
        Check.Contains(tools[1]!["arguments"].Str(), "ls -la");
        Check.Equal(1, ((JsonArray)(await ctx.RpcFake.Call("diag.tools", new JsonObject { ["errors"] = true }))!).Count);

        // the results as the model got them: a preview in diag.tools, everything in diag.tool
        var failure = "browser open failed: Index was outside the bounds of the array. " + new string('y', 500);
        ctx.SessionsFake.AppendMessage("ses_1", new ChatMessage { Role = MessageRole.Assistant, Parts = [new ToolCallPart { Id = "c1", Name = "bash", Arguments = "{\"command\":\"ls -la\"}" }] });
        ctx.SessionsFake.AppendMessage("ses_1", new ChatMessage { Role = MessageRole.Tool, Parts = [new ToolResultPart { CallId = "c1", Name = "bash", Content = failure, IsError = true, Details = new JsonObject { ["exitCode"] = 2 } }] });
        tools = (JsonArray)(await ctx.RpcFake.Call("diag.tools"))!;
        Check.True(tools[1]!["result"].Str()!.StartsWith("browser open failed: Index was outside"), "a result preview");
        Check.True(tools[1]!["result"].Str()!.Length < failure.Length, "cut short");
        Check.True(tools[0]!["result"] is null, "no result yet for the running call");
        var one = (JsonObject)(await ctx.RpcFake.Call("diag.tool", new JsonObject { ["callId"] = "c1" }))!;
        Check.Equal(failure, one["result"].Str());
        Check.Equal(true, (bool)one["isError"]!);
        Check.Equal("ls -la", one["arguments"]!["command"].Str());
        Check.Equal(2, (int)one["details"]!["exitCode"]!);
        await Check.ThrowsAsync<RpcException>(() => ctx.RpcFake.Call("diag.tool", new JsonObject { ["callId"] = "nope" }));
    }

    private sealed class FakeScheduler(List<AgentSlots> slots) : IAgentScheduler
    {
        public string Resolve(ModelInfo model) => model.Ref;
        public IReadOnlyList<AgentSlots> Snapshot() => slots;
        public ValueTask<IAgentSlot> AcquireAsync(AgentSlotRequest request, CancellationToken ct) => throw new NotSupportedException();
        public bool TryAcquire(AgentSlotRequest request, out IAgentSlot? lease) { lease = null; return false; }
    }

    private static async Task Problems()
    {
        var ctx = new FakePluginContext(pluginId: "netpi.diagnostics");
        var pm = new FakePluginManager();
        var broken = FakePluginManager.Info("netpi.broken", "Broken");
        broken.State = "failed";
        broken.Error = "TypeLoadException: IOld";
        pm.Plugins.Add(broken);
        ctx.ServicesFake.Register<IPluginManager>(pm);
        var long_ = DateTimeOffset.UtcNow.AddMinutes(-3);
        ctx.ServicesFake.Register<IAgentScheduler>(new FakeScheduler(
        [
            new AgentSlots
            {
                Key = "qwen", Configured = true, Model = "aiproxy/qwen", Capacity = 2, Available = false, Unavailable = "qwen isn't loaded",
                Waiters = [new SlotHolder { AgentId = "agt_w", Label = "story-b", Since = long_ }],
            },
        ]));
        ctx.Rpc.Register("logs.recent", (_, _) => Task.FromResult<object?>(new JsonArray(
            new JsonObject { ["time"] = DateTimeOffset.Now.ToString("O"), ["level"] = "err", ["category"] = "plugin:x", ["message"] = "bad thing" },
            new JsonObject { ["time"] = DateTimeOffset.Now.AddHours(-2).ToString("O"), ["level"] = "err", ["category"] = "old", ["message"] = "long ago" })));
        await new DiagnosticsPlugin().StartAsync(ctx, CancellationToken.None);

        var problems = (JsonArray)(await ctx.RpcFake.Call("diag.problems"))!;
        string Find(string text) => problems.Select(p => $"{p!["severity"]} {p["message"]}").FirstOrDefault(p => p.Contains(text)) ?? throw new AssertException($"no problem with '{text}': {problems.ToJsonString()}");
        Check.True(Find("netpi.broken failed").StartsWith("error"));
        Check.True(Find("wait on agent qwen, which can't take work: qwen isn't loaded").StartsWith("error"));
        Check.True(Find("story-b (agt_w) has waited").StartsWith("warn"));
        Check.Contains(Find("1 error(s) logged"), "bad thing");
        Check.Equal("error", problems[0]!["severity"].Str(), "worst first");
    }

    private static async Task SettingsFailuresOverview()
    {
        var ctx = await StartAsync();
        ctx.SettingsFake.Set("providers.openrouter.apiKey", "sk-or-123456");
        ctx.SettingsFake.Set("providers.anthropic.apiKey", "env:ANTHROPIC_API_KEY");
        ctx.SettingsFake.Set("providers.custom.password", "");
        ctx.SettingsFake.Set("tools.web.searxUrl", "http://127.0.0.1:8888");
        var settings = ((JsonObject)(await ctx.RpcFake.Call("diag.settings"))!)["settings"]!;
        Check.Equal("<secret, 12 chars>", settings["providers"]!["openrouter"]!["apiKey"].Str());
        Check.Equal("env:ANTHROPIC_API_KEY", settings["providers"]!["anthropic"]!["apiKey"].Str(), "a reference is not a secret");
        Check.Equal("", settings["providers"]!["custom"]!["password"].Str());
        Check.Equal("http://127.0.0.1:8888", settings["tools"]!["web"]!["searxUrl"].Str());

        var dir = Path.Combine(ctx.Paths.LogsDir, "failed-requests");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "20260925-024000-000-qwen.json"), new JsonObject
        {
            ["time"] = "2026-09-25T02:40:00+02:00", ["provider"] = "aiproxy", ["model"] = "qwen", ["requestId"] = "req_1",
            ["error"] = new JsonObject { ["message"] = "backend_unavailable", ["status"] = 503 }, ["request"] = new JsonObject { ["input"] = "long" },
        }.ToJsonString());
        var failures = (JsonArray)(await ctx.RpcFake.Call("diag.failures"))!;
        Check.Equal("20260925-024000-000-qwen.json", failures.Single()!["name"].Str());
        Check.Equal("req_1", failures[0]!["requestId"].Str());
        Check.Equal(503, (int)failures[0]!["error"]!["status"]!);
        Check.False(((JsonObject)failures[0]!).ContainsKey("request"), "the list leaves the body out");
        var one = (JsonObject)(await ctx.RpcFake.Call("diag.failure", new JsonObject { ["name"] = "20260925-024000-000-qwen.json" }))!;
        Check.Contains(one["content"].Str(), "\"input\"");
        var bad = await Check.ThrowsAsync<RpcException>(() => ctx.RpcFake.Call("diag.failure", new JsonObject { ["name"] = "../settings.json" }));
        Check.Equal("bad_request", bad.Code);

        var overview = (JsonObject)(await ctx.RpcFake.Call("diag.overview"))!;
        foreach (var key in new[] { "app", "process", "plugins", "models", "agents", "runs", "calls", "tools", "problems", "reloads", "more" })
            Check.True(((JsonObject)overview).ContainsKey(key), key);
        Check.True((int)overview["process"]!["pid"]! > 0);
        Check.True(((JsonArray)overview["more"]!).Any(m => m.Str()!.StartsWith("diag.calls: ")), "it points at the other diag methods");
    }

    // The tool is how an agent reads the harness: the same JSON as the RPCs, the calling session by default, and no way
    // to reach anything that changes the app.
    private static async Task DiagTool()
    {
        var ctx = await StartAsync();
        var tool = ctx.ToolsFake.Tools.OfType<DiagTool>().Single();
        Check.True(tool.Definition.ReadOnly, "read-only, so calls of one turn run in parallel");
        var actions = ((JsonArray)tool.Definition.Parameters["properties"]!["action"]!["enum"]!).Select(a => a.Str()).ToList();
        Check.True(actions.Contains("overview") && actions.Contains("toolsets") && actions.Contains("messages"), "the actions are in the schema");

        // a read: the RPC's own JSON, in Content (for the model) and in Details (for the chat)
        var overview = await Call(tool, """{ "action": "overview" }""");
        Check.False(overview.IsError, overview.Content);
        Check.Contains(overview.Content, "\"pid\"");
        Check.True(overview.Details is JsonObject, "the details the UI gets");
        var settings = await Call(tool, """{ "action": "settings" }""");
        Check.Contains(settings.Content, "\"file\"", "the settings document");
        var empty = await Call(tool, """{ "action": "problems" }""");
        Check.Equal("[]", empty.Content, "nothing wrong in a bare host");

        // the session-scoped actions default to the caller's session; "all" means no filter
        RpcRequest? seen = null;
        ctx.RpcFake.Register("diag.toolsets", (r, _) => { seen = r; return Task.FromResult<object?>(new JsonObject { ["ok"] = true }); });
        await Call(tool, """{ "action": "toolsets" }""");
        Check.Equal("ses_caller", seen!.Str("sessionId"));
        await Call(tool, """{ "action": "toolsets", "sessionId": "ses_other" }""");
        Check.Equal("ses_other", seen!.Str("sessionId"));
        await Call(tool, """{ "action": "toolsets", "sessionId": "all" }""");
        Check.Equal(null, seen!.Str("sessionId"), "no filter");
        seen = null;
        await Call(tool, """{ "action": "overview", "limit": 5 }""");
        Check.True(seen is null, "the global actions are not given a session");

        // messages: another chat in full, with the session under the name that method takes
        RpcRequest? page = null;
        ctx.RpcFake.Register("sessions.messages", (r, _) => { page = r; return Task.FromResult<object?>(new JsonObject { ["messages"] = new JsonArray() }); });
        var read = await Call(tool, """{ "action": "messages", "sessionId": "ses_other", "beforeSeq": 40, "limit": 20 }""");
        Check.False(read.IsError, read.Content);
        Check.Equal("ses_other", page!.Str("id"), "sessions.messages takes the session as id");
        Check.Equal(null, page.Str("sessionId"), "and not under the name the tool uses");
        Check.Equal(40, page.Int("beforeSeq"));
        Check.Equal(20, page.Int("limit"));
        await Call(tool, """{ "action": "messages" }""");
        Check.Equal("ses_caller", page!.Str("id"), "the calling chat by default");

        // writes: the action picks the method out of the read-only list, so there is nothing to reach
        var pm = new FakePluginManager();
        pm.Plugins.Add(FakePluginManager.Info("netpi.context", "Context"));
        ctx.ServicesFake.Register<IPluginManager>(pm);
        var reloaded = 0;
        ctx.RpcFake.Register("diag.reload", (_, _) => { reloaded++; return Task.FromResult<object?>("reloaded"); });
        foreach (var arg in new[] { """{ "action": "reload" }""", """{ "action": "reload", "args": "netpi.context" }""", """{ "action": "RELOAD" }""" })
        {
            var refused = await Call(tool, arg);
            Check.True(refused.IsError, arg);
            Check.Contains(refused.Content, "only reads", arg);
            Check.Contains(refused.Content, "/reload", arg);   // it says who may
        }
        var unknown = await Call(tool, """{ "action": "reset", "sessionId": "ses_1" }""");
        Check.True(unknown.IsError);
        Check.Contains(unknown.Content, "Unknown action");
        var missing = await Call(tool, "{}");
        Check.True(missing.IsError);
        Check.Contains(missing.Content, "Missing 'action'");
        Check.Equal(0, reloaded, "diag.reload was never called");
        Check.Equal(0, pm.Reloaded.Count, "no plugin was reloaded");
    }
}
