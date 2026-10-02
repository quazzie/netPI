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
        r.Add("inspect: problems (failed plugin, a broken settings file, waiters on an inactive agent, a long wait, errors in the log)", Problems);
        r.Add("inspect: settings without secrets, saved failed requests, the overview lists the diag methods", SettingsFailuresOverview);
        r.Add("inspect: a saved failed request can be sliced and summarised; no body is out of reach", FailureSlice);
        r.Add("inspect: a secret hiding in a value is masked too — a token in a baseUrl, a header no name pattern knows", SecretsInValues);
        r.Add("inspect: the tool scans are paged and bounded — one page for a recent call id, and the old window reported", BoundedScans);
        r.Add("inspect: every diag action is cut at the same limit, and a cut answer leaves no details behind", ActionsAreCapped);
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

    // A saved failed request is routinely 300 KB – 2 MB, and the evidence is often in the middle: the only way to
    // reach it was to open the file under ~/.netpi by hand, with paths and JSON parsing the agent should not need
    // (idea-rkgzk5). A slice pages through any body; a summary answers an order complaint from one small result.
    private static async Task FailureSlice()
    {
        var ctx = await StartAsync();
        var dir = Path.Combine(ctx.Paths.LogsDir, "failed-requests");
        Directory.CreateDirectory(dir);
        const string name = "20261002-144909-881-qwen.json";
        // 180 input items: the gateway complained about item 176, far past the 200 000 characters the call returns.
        var input = new JsonArray();
        for (var i = 0; i < 180; i++)
            input.Add(i switch
            {
                176 => new JsonObject { ["type"] = "reasoning", ["summary"] = new JsonArray(), ["id"] = $"rs_{i}", ["content"] = new string('r', 3000) },
                _ when i % 7 == 0 => new JsonObject { ["type"] = "function_call", ["call_id"] = $"c{i}", ["name"] = "read", ["arguments"] = "{}" },
                _ when i % 5 == 0 => new JsonObject { ["type"] = "function_call_output", ["call_id"] = $"c{i}", ["output"] = new string('o', 3000) },
                _ when i % 3 == 0 => new JsonObject { ["type"] = "message", ["role"] = "assistant", ["content"] = new JsonArray(new JsonObject { ["type"] = "output_text", ["text"] = "ok" }) },
                _ => new JsonObject { ["role"] = "user", ["content"] = new JsonArray(new JsonObject { ["type"] = "input_text", ["text"] = new string('u', 3000) }) },
            });
        var dump = new JsonObject
        {
            ["time"] = "2026-10-02T14:49:09+02:00", ["provider"] = "aiproxy", ["model"] = "qwen3.8-27b",
            ["transport"] = "responses", ["requestId"] = "req_x",
            ["error"] = new JsonObject { ["message"] = "input Item 176 reasoning cannot follow assistant message content", ["status"] = 400 },
            ["request"] = new JsonObject { ["model"] = "qwen3.8-27b", ["input"] = input },
        };
        var text = dump.ToJsonString(NetPiJson.Indented);
        File.WriteAllText(Path.Combine(dir, name), text);
        Check.True(text.Length > 200_000, $"the body is past what one call returns ({text.Length} chars)");

        // Without an offset nothing changes: the first maxChars, and a nextOffset to go on from.
        var head = (JsonObject)(await ctx.RpcFake.Call("diag.failure", new JsonObject { ["name"] = name }))!;
        Check.Equal(text[..head["returned"]!.GetValue<int>()], head["content"].Str(), "the head is the start of the file");
        Check.True(head["truncated"]!.GetValue<bool>() && head["nextOffset"] is not null, "and says there is more");

        // A slice is the same window of the file, wherever it sits.
        var offset = text.Length / 2;
        var slice = (JsonObject)(await ctx.RpcFake.Call("diag.failure", new JsonObject { ["name"] = name, ["offset"] = offset, ["limit"] = 5000 }))!;
        Check.Equal(text.Substring(offset, 5000), slice["content"].Str(), "the slice is that window of the file");
        Check.Equal(offset, slice["offset"]!.GetValue<int>(), "the offset it started at");
        Check.Equal(offset + 5000, slice["nextOffset"]!.GetValue<int>(), "and where the next one starts");

        // The tail is reachable too.
        var past = (JsonObject)(await ctx.RpcFake.Call("diag.failure", new JsonObject { ["name"] = name, ["offset"] = text.Length + 10 }))!;
        Check.Equal("", past["content"].Str(), "an offset past the end returns nothing");
        Check.False(past.ContainsKey("nextOffset"), "and no next: there is nothing left");

        // The shape answers the gateway's complaint from one small result: index, kind, counts — no payload.
        var summary = (JsonObject)(await ctx.RpcFake.Call("diag.failure", new JsonObject { ["name"] = name, ["summary"] = true }))!;
        var shape = summary["shape"]!;
        Check.Equal(400, shape["error"]!["status"]!.GetValue<int>(), "the error it was saved for");
        var items = (JsonArray)shape["input"]!["items"]!;
        Check.Equal(180, items.Count, "one entry per input item");
        Check.Equal("reasoning", items[176]!["type"].Str(), "item 176, the one the gateway named");
        Check.Equal(176, items[176]!["i"]!.GetValue<int>(), "with its index");
        var shapeJson = shape["input"]!["items"]!.ToJsonString();
        Check.True(shapeJson.Length < 20_000 && !shapeJson.Contains(new string('u', 50)) && !shapeJson.Contains(new string('o', 50)),
            $"the kinds, not the payload ({shapeJson.Length} chars)");
        var kinds = ((JsonArray)shape["input"]!["kinds"]!).ToDictionary(k => k!["type"]!.Str()!, k => (int)k["count"]!);
        Check.Equal(1, kinds["reasoning"], "one reasoning item");
        Check.Equal(180, kinds.Values.Sum(), "the counts add up to the item count");

        // A chat body is a list of roles.
        File.WriteAllText(Path.Combine(dir, "chat.json"), new JsonObject
        {
            ["transport"] = "chat", ["request"] = new JsonObject { ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = "You are NetPI." },
                new JsonObject { ["role"] = "user", ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "hi" }) }) },
        }.ToJsonString());
        var chat = ((JsonObject)(await ctx.RpcFake.Call("diag.failure", new JsonObject { ["name"] = "chat.json", ["summary"] = true }))!)["shape"]!;
        Check.Equal("system,user", string.Join(",", ((JsonArray)chat["messages"]!["items"]!).Select(i => i!["role"]!.Str())), "the roles in order");

        // and the tool passes them on: the slice is the point, so the model has to be able to ask for one.
        var tool = ctx.ToolsFake.Tools.OfType<DiagTool>().Single();
        var properties = tool.Definition.Parameters["properties"]!.AsObject();
        Check.True(properties.ContainsKey("offset") && properties.ContainsKey("summary"), "the diag tool offers the slice");
        Check.Contains(tool.Definition.Help!, "offset", "and its manual says so");
        var answer = await Call(tool, $$"""{ "action": "failure", "name": "{{name}}", "summary": true }""");
        Check.False(answer.IsError, answer.Content);
        var through = JsonNode.Parse(answer.Content)!;
        Check.Equal(0, through["returned"]!.GetValue<int>(), "summary answers with the shape instead of the body");
        Check.True(through["shape"]!["input"] is not null, "and the shape is there");
        var sliced = await Call(tool, $$"""{ "action": "failure", "name": "{{name}}", "offset": 1000, "limit": 4000 }""");
        var cut = JsonNode.Parse(sliced.Content)!["content"]!.Str()!;
        Check.Equal(text.Substring(1000, cut.Length), cut, "the tool forwarded offset to the method");
    }

    // Redaction keyed on the setting path and the property name, so a credential carried by the *value* came through
    // whole: a baseUrl with ?api_key=… (baseUrl is neither a secret path nor a secret-shaped name) and a headers map
    // whose members were only tested for their names, so "X-Auth" was printed while the apiKey beside it was masked
    // (idea-csuckr). The inspector's own contract is "the settings without secrets".
    private static async Task SecretsInValues()
    {
        const string keyInQuery = "https://gw.example/v1?api_key=sk-live-1234567890";
        const string userinfo = "https://admin:hunter2@gw.example/v1";
        const string searchUrl = "http://127.0.0.1:8888/search?q=netpi";
        const string proxyUrl = "socks5://127.0.0.1:1080?token=socks-secret-1234";
        const string xAuth = "Bearer op-1234567890";
        const string gatewayKey = "gk-9876543210";
        const string accept = "application/json";
        const string bearer = "bearer";
        const string githubToken = "ghp-1234567890";
        // The marker carries the length of the whole value, so a hand-written number would not catch a truncated one.
        static string Masked(string value) => $"<secret, {value.Length} chars>";

        var ctx = await StartAsync();
        ctx.SettingsFake.Set("providers.gateway.baseUrl", keyInQuery);
        ctx.SettingsFake.Set("providers.plain.baseUrl", "https://gw.example/v1");
        ctx.SettingsFake.Set("providers.basic.baseUrl", userinfo);
        ctx.SettingsFake.Set("tools.web.searxUrl", searchUrl);
        ctx.SettingsFake.Set("providers.gateway.headers", new JsonObject
        {
            ["X-Auth"] = xAuth,
            ["X-Gateway-Key"] = gatewayKey,
            ["Accept"] = accept,                   // not a credential, but it is inside a headers map
            ["X-Trace"] = "env:NETPI_TRACE",       // a reference stays a reference
        });
        ctx.SettingsFake.Set("providers.gateway.auth", new JsonObject { ["mode"] = bearer });
        ctx.SettingsFake.Set("mcp.uvx.env", new JsonObject { ["MOCK_SPEED"] = "3", ["GITHUB_TOKEN"] = githubToken });
        ctx.SettingsFake.Set("tools.web.proxy", proxyUrl);
        ctx.SettingsFake.Set("tools.web.mirror", "https://mirror.example/list?page=2&limit=200");

        var settings = ((JsonObject)(await ctx.RpcFake.Call("diag.settings"))!)["settings"]!;
        var gateway = settings["providers"]!["gateway"]!;
        Check.Equal(Masked(keyInQuery), gateway["baseUrl"].Str(), "a token in a baseUrl's query string is a secret");
        Check.Equal(Masked(xAuth), gateway["headers"]!["X-Auth"].Str(), "a header whose name is not secret-shaped");
        Check.Equal(Masked(gatewayKey), gateway["headers"]!["X-Gateway-Key"].Str());
        Check.Equal(Masked(accept), gateway["headers"]!["Accept"].Str(), "every value inside a headers map is masked");
        Check.Equal("env:NETPI_TRACE", gateway["headers"]!["X-Trace"].Str(), "an env: reference stays readable");
        Check.Equal(Masked(bearer), gateway["auth"]!["mode"].Str(), "and every value inside an auth map");

        Check.Equal("https://gw.example/v1", settings["providers"]!["plain"]!["baseUrl"].Str(), "a plain URL is still readable");
        Check.Equal(Masked(userinfo), settings["providers"]!["basic"]!["baseUrl"].Str(), "userinfo in a URL is a secret");
        Check.Equal(Masked(searchUrl), settings["tools"]!["web"]!["searxUrl"].Str(), "any query string in a url-ish setting");
        Check.Equal(Masked(proxyUrl), settings["tools"]!["web"]!["proxy"].Str(), "a secret-shaped query parameter anywhere");
        Check.Equal("https://mirror.example/list?page=2&limit=200", settings["tools"]!["web"]!["mirror"].Str(), "an ordinary query is not a secret");

        var env = settings["mcp"]!["uvx"]!["env"]!;
        Check.Equal("3", env["MOCK_SPEED"].Str(), "an env map is not a credentials map: its members are named");
        Check.Equal(Masked(githubToken), env["GITHUB_TOKEN"].Str(), "and a secret-shaped one in it is still masked");

        // Nothing the inspector prints may still carry the value it masked.
        var printed = ((JsonObject)(await ctx.RpcFake.Call("diag.settings"))!).ToJsonString();
        foreach (var secret in new[] { "sk-live-1234567890", "op-1234567890", "gk-9876543210", "hunter2", "socks-secret-1234", "ghp-1234567890" })
            Check.NotContains(printed, secret);
    }

    // Every wrapped action used to answer uncapped (only the rpc passthrough cut its text), and the runtime persists a tool
// result's Details node in the session database while the model only sees Content: a big messages page was copied three
// times and one of the copies was permanent (idea-5oitnm). Every answer now goes through the same cut.
private static async Task ActionsAreCapped()
{
    var ctx = await StartAsync();
    // Fully qualified: the class also has a test method called DiagTool, which would win the name in this scope.
    const int max = NetPI.Diagnostics.DiagTool.RpcMaxChars;
    var tool = ctx.ToolsFake.Tools.OfType<NetPI.Diagnostics.DiagTool>().Single();
    var big = new string('z', max * 2);
    ctx.RpcFake.Register("diag.journal", (_, _) => Task.FromResult<object?>(new JsonArray(new JsonObject { ["data"] = big })), "events", readOnly: true);

    var cut = await Call(tool, """{ "action": "journal" }""");
    Check.False(cut.IsError, cut.Content);
    Check.True(cut.Content.Length <= max + 120, $"{cut.Content.Length} characters, cut at {max}");
    Check.Contains(cut.Content, $"cut at {max} characters", "and it says so");
    Check.True(cut.Details is null, "a cut answer leaves no details node for the session database to keep");

    // a small answer is untouched: the UI reads the details node
    ctx.RpcFake.Register("diag.settings", (_, _) => Task.FromResult<object?>(new JsonObject { ["settings"] = new JsonObject { ["a"] = 1 } }), "settings", readOnly: true);
    var small = await Call(tool, """{ "action": "settings" }""");
    Check.Contains(small.Content, "\"settings\"");
    Check.True(small.Details is JsonObject, "the details the chat renders");

    // the passthrough behaves the same, as it always did
    ctx.RpcFake.Register("events.recent", (_, _) => Task.FromResult<object?>(new JsonArray(new JsonObject { ["data"] = big })), "recent bus events", readOnly: true);
    var viaRpc = await Call(tool, """{ "action": "rpc", "method": "events.recent" }""");
    Check.Contains(viaRpc.Content, $"cut at {max} characters");
    Check.True(viaRpc.Details is null, "and no details either");
}

// The tool log holds the newest calls, but the store answered a question about one call id by deserialising the parts of
// the last 2000 messages, and the list by doing the same for the last 300 of every session on screen: every tool result
// and image in them, to render a 300-character preview. The scans are paged now and stop as soon as the ids asked for are
// found; the numbers below are rows the store handed out (idea-5oitnm).
private static async Task BoundedScans()
{
    const int History = 1200;
    var ctx = await StartAsync();
    static ChatMessage[] Pair(string callId, string result) =>
    [
        new() { Role = MessageRole.Assistant, Parts = [new ToolCallPart { Id = callId, Name = "read", Arguments = "{\"path\":\"a\"}" }] },
        new() { Role = MessageRole.Tool, Parts = [new ToolResultPart { CallId = callId, Name = "read", Content = result }] },
    ];

    // ses_1 holds 1200 messages with three tool calls at known depths: the newest two rows, 350 rows back, and the
    // oldest two rows — behind the 1000-message window the scan reaches.
    foreach (var m in Pair("c_ancient", "result of c_ancient").Concat(Filler(846)).Concat(Pair("c_mid", "result of c_mid"))
             .Concat(Filler(346)).Concat(Pair("t_ses_1", "listed")).Concat(Pair("c_recent", "result of c_recent")))
        ctx.SessionsFake.AppendMessage("ses_1", m);
    foreach (var sid in new[] { "ses_2", "ses_3" })
        foreach (var m in Filler(History).Concat(Pair($"t_{sid}", "listed")))
            ctx.SessionsFake.AppendMessage(sid, m);
    ctx.SessionsFake.Sessions.Add(new SessionInfo { Id = "ses_1", Title = "s", CreatedAt = DateTimeOffset.UtcNow });
    ctx.SessionsFake.Sessions.Add(new SessionInfo { Id = "ses_2", Title = "t", CreatedAt = DateTimeOffset.UtcNow });
    ctx.SessionsFake.Sessions.Add(new SessionInfo { Id = "ses_3", Title = "u", CreatedAt = DateTimeOffset.UtcNow });
    foreach (var sid in new[] { "ses_1", "ses_2", "ses_3" })
    {
        ctx.Events.Publish(EventTypes.ToolStart, new JsonObject { ["sessionId"] = sid, ["callId"] = $"t_{sid}", ["name"] = "read", ["arguments"] = "{}" }, sid);
        ctx.Events.Publish(EventTypes.ToolEnd, new JsonObject { ["sessionId"] = sid, ["callId"] = $"t_{sid}", ["name"] = "read", ["durationMs"] = 3 }, sid);
    }
    Check.Equal(History, ctx.SessionsFake.GetMessages("ses_1").Count, "the chat is 1200 messages long");

    // A call id in the newest messages: one page of the session, not its whole history.
    ctx.SessionsFake.MessagesRead = 0;
    var recent = (JsonObject)(await ctx.RpcFake.Call("diag.tool", new JsonObject { ["callId"] = "c_recent", ["sessionId"] = "ses_1" }))!;
    Check.Equal("result of c_recent", recent["result"].Str());
    Check.Equal(Inspector.ToolPartPage, ctx.SessionsFake.MessagesRead, $"one page, not the {History} messages the old scan read");

    // Deeper in the same session: found by paging back, and only the pages it took were read.
    ctx.SessionsFake.MessagesRead = 0;
    var mid = (JsonObject)(await ctx.RpcFake.Call("diag.tool", new JsonObject { ["callId"] = "c_mid", ["sessionId"] = "ses_1" }))!;
    Check.Equal("result of c_mid", mid["result"].Str());
    Check.Equal(4 * Inspector.ToolPartPage, ctx.SessionsFake.MessagesRead, "four pages back, then it stops");

    // Older than the window: not found, and the error says how far back it looked.
    ctx.SessionsFake.MessagesRead = 0;
    var ancient = await Check.ThrowsAsync<RpcException>(() => ctx.RpcFake.Call("diag.tool", new JsonObject { ["callId"] = "c_ancient", ["sessionId"] = "ses_1" }));
    Check.Contains(ancient.Message, $"newest {Inspector.ToolPartMessages} messages");
    Check.Equal(Inspector.ToolPartMessages, ctx.SessionsFake.MessagesRead, "the window is the bound");

    // The list: one page per session it shows, and the previews are still there.
    ctx.SessionsFake.MessagesRead = 0;
    var tools = (JsonArray)(await ctx.RpcFake.Call("diag.tools"))!;   // no session filter: every session the log holds
    Check.Equal(3, tools.Count);
    Check.True(tools.All(t => t!["result"].Str() == "listed"), "every row still carries its result preview");
    Check.Equal(3 * Inspector.ToolPartPage, ctx.SessionsFake.MessagesRead, "one page per session on screen");

    static IEnumerable<ChatMessage> Filler(int n) => Enumerable.Range(0, n).Select(i => ChatMessage.UserText($"filler {i}"));
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
        var correlated = Request();
        correlated = new ModelRequest { Model = correlated.Model, Messages = correlated.Messages, CorrelationId = "physical-call-123" };
        await Drain(mw.InvokeAsync(correlated, (_, c) => Stream([Yield(new StreamCompleted(done))], c), CancellationToken.None));
        var found = (JsonObject)(await ctx.RpcFake.Call("diag.call", new JsonObject { ["correlationId"] = "physical-call-123" }))!;
        Check.Equal("physical-call-123", found["correlationId"].Str());
        Check.Equal("ok", found["state"].Str());
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
        ctx.SettingsFake.InvalidOnDisk = true;
        ctx.SettingsFake.InvalidOnDiskError = "'{' expected, got end of file. (line 3, column 5)";
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
        Check.True(Find("settings.json does not parse").StartsWith("error"), "a broken settings file is a problem in its own right");
        Check.Contains(Find("settings.json does not parse"), "expected, got end of file", "the parse error the host saw");
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
        ctx.SettingsFake.Set("providers.openaiCompatible", new JsonArray(
            new JsonObject
            {
                ["id"] = "srv1",
                ["baseUrl"] = "https://compat.example",
                ["apiKey"] = "sk-or-v1-1234567890",
                ["headers"] = new JsonObject { ["Authorization"] = "Bearer 1234567890", ["X-Api-Key"] = "key-9876543210" },
            }));
        var settings = ((JsonObject)(await ctx.RpcFake.Call("diag.settings"))!)["settings"]!;
        Check.Equal("<secret, 12 chars>", settings["providers"]!["openrouter"]!["apiKey"].Str());
        Check.Equal("env:ANTHROPIC_API_KEY", settings["providers"]!["anthropic"]!["apiKey"].Str(), "a reference is not a secret");
        Check.Equal("", settings["providers"]!["custom"]!["password"].Str());
        Check.Equal("http://127.0.0.1:8888", settings["tools"]!["web"]!["searxUrl"].Str());

        // an array of objects: the objects inside were never visited before, so their secrets leaked (idea-zu892z)
        var compat = ((JsonArray)settings["providers"]!["openaiCompatible"]!)[0]!.AsObject();
        Check.Equal("srv1", compat["id"].Str(), "non-secret values pass through");
        Check.Equal("https://compat.example", compat["baseUrl"].Str());
        Check.Equal("<secret, 19 chars>", compat["apiKey"].Str(), "an API key inside the array is redacted");
        Check.Equal("<secret, 17 chars>", compat["headers"]!["Authorization"].Str(), "and headers by name");
        Check.Equal("<secret, 14 chars>", compat["headers"]!["X-Api-Key"].Str());

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
