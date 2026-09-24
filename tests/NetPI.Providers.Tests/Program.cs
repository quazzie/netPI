using System.Text;
using System.Text.Json.Nodes;
using NetPI;
using NetPI.Providers.Tests;
using AN = NetPI.Providers.Anthropic;
using AP = NetPI.Providers.AiProxy;
using OR = NetPI.Providers.OpenRouter;

Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", null);
Environment.SetEnvironmentVariable("OPENROUTER_API_KEY", null);

var t = new TestRunner();
await using var mock = new MockServer();
await mock.StartAsync();
Console.WriteLine($"mock server: {mock.BaseUrl}");

// ------------------------------------------------------------------ helpers

static ToolDefinition Tool(string name) => new()
{
    Name = name,
    Description = $"The {name} tool",
    Parameters = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject { ["path"] = new JsonObject { ["type"] = "string" } } },
};

var tools = new[] { Tool("read"), Tool("ls") };

ModelRequest Req(ModelInfo model, IReadOnlyList<ChatMessage>? messages = null, string? effort = null) => new()
{
    Model = model,
    SystemPrompt = "You are NetPI.",
    Messages = messages ?? [ChatMessage.UserText("hi")],
    Tools = tools,
    ReasoningEffort = effort,
    SessionId = "ses_1",
};

static ModelInfo M(string provider, string id) => new() { Provider = provider, Id = id };

static async Task<List<ModelStreamEvent>> Collect(IModelProvider p, ModelRequest r, CancellationToken ct = default)
{
    var list = new List<ModelStreamEvent>();
    await foreach (var e in p.StreamAsync(r, ct)) list.Add(e);
    return list;
}

static async Task<ModelException?> Fails(IModelProvider p, ModelRequest r)
{
    try { await Collect(p, r); return null; }
    catch (ModelException ex) { return ex; }
}

static string Img(string data) => Convert.ToBase64String(Encoding.UTF8.GetBytes(data));

// A conversation exercising every part type (normalized: tool results directly after their calls).
List<ChatMessage> Conversation(string assistantProvider) =>
[
    new() { Role = MessageRole.User, Parts = [new TextPart { Text = "Read files" }, new ImagePart { MediaType = "image/png", Data = Img("u1") }] },
    new()
    {
        Role = MessageRole.Assistant, Provider = assistantProvider,
        Parts =
        [
            new ThinkingPart { Text = "I should read", Signature = "S1", ProviderData = new JsonObject { ["id"] = "rs_9", ["encrypted_content"] = "E9" } },
            new ThinkingPart { Redacted = "R1" },
            new TextPart { Text = "Reading" },
            new ToolCallPart { Id = "call:1", Name = "read", Arguments = """{"path":"a"}""" },
            new ToolCallPart { Id = "t2", Name = "ls", Arguments = "not json" },
        ],
    },
    new()
    {
        Role = MessageRole.Tool,
        Parts = [new ToolResultPart { CallId = "call:1", Name = "read", Content = "content A", Images = [new ImagePart { MediaType = "image/png", Data = Img("t1") }] }],
    },
    new() { Role = MessageRole.Tool, Parts = [new ToolResultPart { CallId = "t2", Name = "ls", Content = "", IsError = true }] },
    new() { Role = MessageRole.User, Parts = [new TextPart { Text = "and also this" }, new ImagePart { MediaType = "image/jpeg", Data = Img("u2") }] },
];

// ================================================================== unit tests

await t.Run("SSE reader: event names, multi-line data, comments, CRLF, missing separators, [DONE], EOF", async () =>
{
    var raw = "event: a\r\ndata: {\"x\":\r\ndata: 1}\r\n\r\n: comment\n\nretry: 5\ndata: {\"y\":2}\ndata: {\"z\":3}\n\nid: 7\ndata:[DONE]\n\ndata: last";
    var events = new List<AP.SseEvent>();
    await foreach (var e in AP.SseReader.ReadAsync(new MemoryStream(Encoding.UTF8.GetBytes(raw)))) events.Add(e);
    t.Eq(5, events.Count, "event count");
    t.Eq("a", events[0].Event, "event name");
    t.Eq("{\"x\":\n1}", events[0].Data, "multi-line data");
    t.Eq("{\"y\":2}", events[1].Data, "split 1");
    t.Eq("{\"z\":3}", events[2].Data, "split 2");
    t.Check(events[3].IsDone && events[3].Id == "7", "[DONE] + id");
    t.Eq("last", events[4].Data, "trailing event without blank line");
});

await t.Run("Think-tag splitter: tags split at every chunk boundary", () =>
{
    const string s = "  <think>abc</think>\n\nhello <b>x</b> <thin end";
    var bad = 0;
    for (var i = 0; i <= s.Length; i++)
    for (var j = i; j <= s.Length; j++)
    {
        var sp = new AP.ThinkTagSplitter();
        var th = new StringBuilder(); var tx = new StringBuilder();
        void Emit(bool thinking, string text) => (thinking ? th : tx).Append(text);
        sp.Process(s[..i], Emit); sp.Process(s[i..j], Emit); sp.Process(s[j..], Emit); sp.Flush(Emit);
        if (th.ToString() != "abc" || tx.ToString() != "hello <b>x</b> <thin end") bad++;
    }
    t.Eq(0, bad, "mismatching splits");
    return Task.CompletedTask;
});

await t.Run("Effort mapping to catalog efforts", () =>
{
    var r = new ReasoningInfo { Supported = true, Efforts = ["none", "low", "medium", "xhigh"], Default = "medium" };
    t.Eq("xhigh", AP.EffortMap.Resolve("high", r), "high -> xhigh");
    t.Eq("medium", AP.EffortMap.Resolve("MEDIUM", r), "case-insensitive");
    t.Eq("xhigh", AP.EffortMap.Resolve("max", r), "max -> xhigh");
    t.Eq("none", AP.EffortMap.Resolve("minimal", new ReasoningInfo { Supported = true, Efforts = ["none", "max"] }), "minimal -> none");
    t.Eq("max", AP.EffortMap.Resolve("medium", new ReasoningInfo { Supported = true, Efforts = ["none", "max"] }), "tie prefers higher");
    t.Eq(null, AP.EffortMap.Resolve(null, r), "no effort");
    t.Eq(null, AP.EffortMap.Resolve("default", r), "default");
    t.Eq(null, AP.EffortMap.Resolve("high", new ReasoningInfo { Supported = false }), "unsupported");
    t.Eq("high", AP.EffortMap.Resolve("high", null), "unknown catalog passes through");
    return Task.CompletedTask;
});

await t.Run("Error classification (HTTP)", () =>
{
    var e503 = AP.ProviderErrors.FromHttp("AiProxy", 503, "Service Unavailable", """{"error":{"type":"backend_unavailable"}}""");
    t.Check(e503.Transient && e503.StatusCode == 503 && e503.ErrorType == "backend_unavailable" && !e503.ContextOverflow, "503 backend_unavailable");
    t.Check(e503.Message.Contains("backend_unavailable"), "body in message");
    foreach (var s in new[] { 408, 409, 425, 429, 500, 502, 504, 529 })
        t.Check(AP.ProviderErrors.FromHttp("X", s, null, "").Transient, $"{s} transient");
    foreach (var s in new[] { 400, 401, 403, 404, 422 })
        t.Check(!AP.ProviderErrors.FromHttp("X", s, null, "{}").Transient, $"{s} not transient");
    var ov = AP.ProviderErrors.FromHttp("X", 400, null, """{"error":{"message":"This model's maximum context length is 8192 tokens","type":"invalid_request_error","code":"context_length_exceeded"}}""");
    t.Check(ov.ContextOverflow && !ov.Transient && ov.ErrorType == "context_length_exceeded", "openai overflow");
    var ov2 = AN.ProviderErrors.FromHttp("Anthropic", 413, null, """{"type":"error","error":{"type":"request_too_large","message":"prompt is too long: 250000 tokens > 200000 maximum"}}""");
    t.Check(ov2.ContextOverflow, "413 prompt too long");
    var notOv = AP.ProviderErrors.FromHttp("X", 400, null, """{"error":{"message":"invalid tool schema","type":"invalid_request_error"}}""");
    t.Check(!notOv.ContextOverflow && !notOv.Transient, "plain 400");
    var ovl = AN.ProviderErrors.FromHttp("Anthropic", 529, null, """{"type":"error","error":{"type":"overloaded_error","message":"Overloaded"}}""");
    t.Check(ovl.Transient && ovl.ErrorType == "overloaded_error", "529 overloaded");
    var big = AP.ProviderErrors.FromHttp("X", 500, null, new string('x', 10_000));
    t.Check(big.Message.Length < 2200, "body truncated");
    return Task.CompletedTask;
});

// ================================================================== AiProxy plugin

var apCtx = new FakePluginContext(new JsonObject
{
    ["providers"] = new JsonObject
    {
        ["aiproxy"] = new JsonObject { ["baseUrl"] = mock.BaseUrl, ["modelsCacheSeconds"] = 10 },
        ["openaiCompatible"] = new JsonArray(
            new JsonObject { ["id"] = "vllm", ["name"] = "vLLM box", ["baseUrl"] = mock.BaseUrl + "/v1", ["apiKey"] = "sk-test", ["headers"] = new JsonObject { ["X-Extra"] = "1" } },
            new JsonObject { ["id"] = "bad/id", ["baseUrl"] = "http://example.invalid" },
            new JsonObject { ["id"] = "nourl" }),
    },
}, "netpi.providers.aiproxy");
var apPlugin = new AP.AiProxyPlugin();
await apPlugin.StartAsync(apCtx, CancellationToken.None);
var aiproxy = apPlugin.Providers[0];
IReadOnlyList<ModelInfo> catalog = [];

await t.Run("aiproxy: plugin registers aiproxy + valid extra endpoints", () =>
{
    var ids = apCtx.ServicesImpl.GetAll<IModelProvider>().Select(p => p.Id).OrderBy(x => x).ToList();
    t.Eq("aiproxy,vllm", string.Join(",", ids), "registered providers");
    t.Check(aiproxy.IsLocal && aiproxy.Id == "aiproxy", "aiproxy is local");
    t.Eq("vLLM box", apPlugin.Providers[1].DisplayName, "extra display name");
    t.Check(apPlugin.Providers[1].IsLocal, "loopback extra defaults to local");
    t.Check(apCtx.Log.Lines.Any(l => l.Contains("bad/id")) && apCtx.Log.Lines.Any(l => l.Contains("nourl")), "invalid entries logged");
    return Task.CompletedTask;
});

await t.Run("aiproxy: /v1/models maps every catalog field", async () =>
{
    catalog = await aiproxy.ListModelsAsync(false, CancellationToken.None);
    t.Eq(4, catalog.Count, "model count");
    var gemma = catalog.Single(m => m.Id == "gemma-4");
    t.Eq("aiproxy/gemma-4", gemma.Ref, "ref");
    t.Eq(65536, gemma.ContextWindow, "context_window");
    t.Eq(null, gemma.MaxOutputTokens, "max_output_tokens null");
    t.Eq(1, gemma.Concurrency, "concurrency");
    t.Eq("text", string.Join(",", gemma.InputModalities), "modalities");
    t.Check(gemma.Reasoning is { Supported: true, Default: "max" } && string.Join(",", gemma.Reasoning.Efforts) == "none,max", "reasoning");
    t.Eq("unloaded", gemma.Status, "status");
    t.Check(gemma.IsLocal, "IsLocal");
    t.Eq("llamacpp", gemma.Extra?["owned_by"]?.GetValue<string>(), "owned_by in Extra");
    var qwen = catalog.Single(m => m.Id == "qwen3.8-27b");
    t.Check(qwen.MaxOutputTokens == 16384 && qwen.Concurrency == 2 && qwen.SupportsImages && qwen.Status == "loaded", "qwen3.8");
    var stopped = catalog.Single(m => m.Id == "qwen38-27b-iq3s");
    t.Check(stopped.Reasoning is null && stopped.Concurrency is null && stopped.Status == "stopped" && stopped.ContextWindow == 524288, "reasoning null / stopped");
});

await t.Run("aiproxy: model list cache, refresh and models.changed", async () =>
{
    var calls = mock.ModelsCalls;
    var changed = apCtx.Bus.Count(EventTypes.ModelsChanged);
    t.Check(changed >= 1, "models.changed published after first load");
    await aiproxy.ListModelsAsync(false, CancellationToken.None);
    t.Eq(calls, mock.ModelsCalls, "cached (no request)");
    await aiproxy.ListModelsAsync(true, CancellationToken.None);
    t.Eq(calls + 1, mock.ModelsCalls, "refresh bypasses cache");
    t.Eq(changed, apCtx.Bus.Count(EventTypes.ModelsChanged), "identical list: no models.changed");
});

await t.Run("aiproxy: server down -> last good list marked offline; unreachable endpoint -> empty", async () =>
{
    var changed = apCtx.Bus.Count(EventTypes.ModelsChanged);
    mock.ModelsDown = true;
    try
    {
        var list = await aiproxy.ListModelsAsync(true, CancellationToken.None);
        t.Eq(4, list.Count, "last good list kept");
        t.Check(list.All(m => m.Status == "offline"), "all offline");
        t.Eq(changed + 1, apCtx.Bus.Count(EventTypes.ModelsChanged), "models.changed on offline");
        t.Check(catalog.All(m => m.Status != "offline"), "original instances untouched");
    }
    finally { mock.ModelsDown = false; }
    var back = await aiproxy.ListModelsAsync(true, CancellationToken.None);
    t.Check(back.Any(m => m.Status == "loaded"), "back online");

    using var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
    l.Start(); var port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port; l.Stop();
    using var http = new HttpClient();
    var dead = new AP.OpenAiCompatibleProvider("dead", "Dead", http, () => new JsonObject { ["baseUrl"] = $"http://127.0.0.1:{port}" });
    var empty = await dead.ListModelsAsync(false, CancellationToken.None);
    t.Eq(0, empty.Count, "unreachable -> empty list, no throw");
});

// ------------------------------------------------------------------ Responses transport

ModelInfo Cat(string id) => catalog.Single(m => m.Id == id);

await t.Run("responses: streams reasoning, text, function call and usage", async () =>
{
    var events = await Collect(aiproxy, Req(Cat("qwen3.8-27b")));
    t.Check(events[^1] is StreamCompleted && events.OfType<StreamCompleted>().Count() == 1, "exactly one final StreamCompleted");
    t.Eq("Let me think.", string.Concat(events.OfType<ThinkingDelta>().Select(d => d.Text)), "thinking deltas");
    t.Eq("Hello world", string.Concat(events.OfType<TextDelta>().Select(d => d.Text)), "text deltas (no dup from .done)");
    t.Eq(2, events.OfType<TextDelta>().Count(), "text delta count");
    t.Check(events.OfType<ToolCallStarted>().Single() is { Id: "call_abc", Name: "read" }, "tool call started");
    t.Eq("{\"path\":\"a.txt\"}", string.Concat(events.OfType<ToolCallArgsDelta>().Select(d => d.Delta)), "args deltas");
    t.Check(events.OfType<UsageUpdate>().Any(), "usage update");

    var msg = ((StreamCompleted)events[^1]).Message;
    t.Eq(MessageRole.Assistant, msg.Role, "role");
    t.Eq("aiproxy", msg.Provider, "provider");
    t.Eq("qwen3.8-27b", msg.Model, "model");
    t.Eq("ses_1", msg.SessionId, "session");
    t.Eq("tool_use", msg.StopReason, "stop reason");
    t.Eq("ThinkingPart,TextPart,ToolCallPart", string.Join(",", msg.Parts.Select(p => p.GetType().Name)), "part order");
    var th = (ThinkingPart)msg.Parts[0];
    t.Check(th.Text == "Let me think." && th.ProviderData?["id"]?.GetValue<string>() == "rs_1" && th.ProviderData?["encrypted_content"]?.GetValue<string>() == "ENC123", "reasoning provider data");
    t.Eq("Hello world", ((TextPart)msg.Parts[1]).Text, "text part");
    var call = (ToolCallPart)msg.Parts[2];
    t.Check(call is { Id: "call_abc", Name: "read", Arguments: "{\"path\":\"a.txt\"}" }, "tool call part");
    t.Check(msg.Usage is { InputTokens: 400, CacheReadTokens: 600, OutputTokens: 50, ReasoningTokens: 20, CacheWriteTokens: 0 }, "usage normalized (uncached input)");
});

await t.Run("responses: request body (instructions, tools, effort, max tokens, input items)", async () =>
{
    apCtx.SettingsImpl.Set("providers.aiproxy.replayReasoning", false);
    try { await Collect(aiproxy, Req(Cat("qwen3.8-27b"), Conversation("aiproxy"), effort: "high")); }
    finally { apCtx.SettingsImpl.Set("providers.aiproxy.replayReasoning", null); }
    var req = mock.Last("/v1/responses");
    var b = req.Json;
    t.Eq("qwen3.8-27b", b["model"]!.GetValue<string>(), "model");
    t.Eq("You are NetPI.", b["instructions"]!.GetValue<string>(), "instructions");
    t.Check(b["stream"]!.GetValue<bool>() && !b["store"]!.GetValue<bool>() && b["parallel_tool_calls"]!.GetValue<bool>(), "stream/store/parallel");
    t.Eq(16384, b["max_output_tokens"]!.GetValue<int>(), "max_output_tokens from catalog");
    t.Eq("xhigh", b["reasoning"]?["effort"]?.GetValue<string>(), "effort mapped to catalog");
    var tool0 = b["tools"]![0]!;
    t.Check(tool0["type"]!.GetValue<string>() == "function" && tool0["name"]!.GetValue<string>() == "read" && tool0["strict"]!.GetValue<bool>() == false
            && tool0["parameters"]?["type"]?.GetValue<string>() == "object", "tool shape");
    t.Check(b["include"] is null, "no include without includeEncryptedReasoning");

    var input = b["input"]!.AsArray();
    string Kind(JsonNode? n) => n?["type"]?.GetValue<string>() ?? n?["role"]?.GetValue<string>() ?? "?";
    t.Eq("user,message,function_call,function_call,function_call_output,function_call_output,user,user",
        string.Join(",", input.Select(Kind)), "input item sequence (no reasoning replay)");
    t.Eq("input_image", input[0]!["content"]![1]!["type"]!.GetValue<string>(), "user image");
    t.Check(input[0]!["content"]![1]!["image_url"]!.GetValue<string>().StartsWith("data:image/png;base64,"), "data url");
    t.Eq("output_text", input[1]!["content"]![0]!["type"]!.GetValue<string>(), "assistant output_text");
    t.Check(input[2]!["call_id"]!.GetValue<string>() == "call:1" && input[2]!["arguments"]!.GetValue<string>() == "{\"path\":\"a\"}", "function_call");
    t.Check(input[4]!["call_id"]!.GetValue<string>() == "call:1" && input[4]!["output"]!.GetValue<string>() == "content A", "function_call_output");
    t.Eq("", input[5]!["output"]!.GetValue<string>(), "empty output kept");
    var toolImgMsg = input[6]!["content"]!.AsArray();
    t.Check(toolImgMsg.Any(c => c!["type"]!.GetValue<string>() == "input_image"), "tool-result image appended as user message after outputs");
    t.Eq("input_text", input[7]!["content"]![0]!["type"]!.GetValue<string>(), "final user text");
    t.Eq("text/event-stream", req.Headers.GetValueOrDefault("accept"), "accept header");
    t.Check(!req.Headers.ContainsKey("authorization"), "no auth header without apiKey");
});

await t.Run("responses: replayReasoning replays reasoning items (live settings)", async () =>
{
    apCtx.SettingsImpl.Set("providers.aiproxy.replayReasoning", true);
    apCtx.SettingsImpl.Set("providers.aiproxy.includeEncryptedReasoning", true);
    try
    {
        await Collect(aiproxy, Req(Cat("qwen3.8-27b"), Conversation("aiproxy")));
        var b = mock.Last("/v1/responses").Json;
        var r = b["input"]![1]!;
        t.Eq("reasoning", r["type"]!.GetValue<string>(), "reasoning item");
        t.Eq("rs_9", r["id"]?.GetValue<string>(), "reasoning id");
        t.Eq("I should read", r["content"]![0]!["text"]!.GetValue<string>(), "reasoning_text");
        t.Eq("E9", r["encrypted_content"]?.GetValue<string>(), "encrypted_content");
        t.Eq("reasoning.encrypted_content", b["include"]?[0]?.GetValue<string>(), "include");
        t.Check(b["reasoning"] is null, "no effort -> reasoning omitted");
    }
    finally
    {
        apCtx.SettingsImpl.Set("providers.aiproxy.replayReasoning", null);
        apCtx.SettingsImpl.Set("providers.aiproxy.includeEncryptedReasoning", null);
    }
});

await t.Run("responses: reasoning is replayed by default (standard stateless usage), chat does not replay it", async () =>
{
    await Collect(aiproxy, Req(Cat("qwen3.8-27b"), Conversation("aiproxy")));
    var input = mock.Last("/v1/responses").Json["input"]!.AsArray();
    string Kind(JsonNode? n) => n?["type"]?.GetValue<string>() ?? n?["role"]?.GetValue<string>() ?? "?";
    t.Eq("user,reasoning,message,function_call,function_call,function_call_output,function_call_output,user,user",
        string.Join(",", input.Select(Kind)), "reasoning → message → function calls");
    t.Check(mock.Last("/v1/responses").Json["include"] is null, "no include by default");

    apCtx.SettingsImpl.Set("providers.aiproxy.transport", "chat");
    try
    {
        await Collect(aiproxy, Req(Cat("qwen3.8-27b"), Conversation("aiproxy")));
        var msgs = mock.Last("/v1/chat/completions").Json["messages"]!.AsArray();
        t.Check(msgs.All(m => m?["reasoning_content"] is null), "chat: no reasoning_content by default");
    }
    finally { apCtx.SettingsImpl.Set("providers.aiproxy.transport", null); }
});

await t.Run("errors: server ids are added to the message and the failed request is saved", async () =>
{
    var dir = Path.Combine(Path.GetTempPath(), "netpi-dump-" + Guid.NewGuid().ToString("N"));
    var p = new AP.OpenAiCompatibleProvider("aiproxy", "AiProxy", new HttpClient(), () => apCtx.SettingsImpl.GetNode("providers.aiproxy") as JsonObject,
        null, null, null, dir);
    mock.NextResponsesFailure = ("resp_fail1", "req_abc123", "capture owner has no planning ID");
    ModelException? caught = null;
    try { await Collect(p, Req(Cat("qwen3.8-27b"), Conversation("aiproxy"))); }
    catch (ModelException ex) { caught = ex; }
    t.Check(caught is not null, "failure surfaces");
    t.Check(caught!.Message.Contains("capture owner has no planning ID"), "server text kept");
    t.Check(caught.Message.Contains("request req_abc123") && caught.Message.Contains("response resp_fail1"), "ids added: " + caught.Message);
    t.Check(!caught.Transient, "response.failed is not retried");
    var files = Directory.GetFiles(Path.Combine(dir, "failed-requests"), "*.json");
    t.Eq(1, files.Length, "one dump");
    var dump = JsonNode.Parse(File.ReadAllText(files[0]))!;
    t.Eq("req_abc123", dump["requestId"]?.GetValue<string>(), "dump request id");
    t.Check(dump["request"]?["input"] is JsonArray, "dump has the request body");
    Directory.Delete(dir, true);
});

// E2E regression: <think> tags in message text were split on the Chat transport only, so Qwen/Gemma behind a backend
// without a reasoning parser leaked raw "<think>…</think>" text on the default (Responses) transport.
await t.Run("responses: inline <think> tags in output_text become thinking (parseThinkTags), raw when off", () =>
{
    static AP.SseEvent Ev(object o) => new(null, System.Text.Json.JsonSerializer.Serialize(o));
    string[] deltas = ["<thi", "nk>plan ", "it</think>\n\nAnswer ", "<b>x</b>"];
    var full = string.Concat(deltas);
    foreach (var split in new[] { true, false })
    {
        var asm = new AP.MessageAssembler();
        var p = new AP.ResponsesStreamParser(asm, "T", split);
        p.Handle(Ev(new { type = "response.output_item.added", output_index = 0, item = new { id = "msg_1", type = "message", content = Array.Empty<object>() } }));
        foreach (var d in deltas) p.Handle(Ev(new { type = "response.output_text.delta", item_id = "msg_1", output_index = 0, content_index = 0, delta = d }));
        p.Handle(Ev(new { type = "response.output_text.done", item_id = "msg_1", output_index = 0, content_index = 0, text = full }));
        p.Handle(Ev(new { type = "response.output_item.done", output_index = 0, item = new { id = "msg_1", type = "message", content = new[] { new { type = "output_text", text = full } } } }));
        p.Handle(Ev(new { type = "response.completed", response = new { status = "completed" } }));
        p.Finish();
        var msg = asm.Build("aiproxy", "m", null, 0);
        if (split)
        {
            t.Eq("ThinkingPart,TextPart", string.Join(",", msg.Parts.Select(x => x.GetType().Name)), "parts");
            t.Eq("plan it", ((ThinkingPart)msg.Parts[0]).Text, "thinking");
            t.Eq("Answer <b>x</b>", msg.Text, "text without think tags (no duplication from .done events)");
        }
        else t.Eq(full, msg.Text, "parseThinkTags=false keeps the raw text");
    }
    // a server that only sends the finished item
    var asm2 = new AP.MessageAssembler();
    var p2 = new AP.ResponsesStreamParser(asm2, "T");
    p2.Handle(Ev(new { type = "response.completed", response = new { status = "completed", output = new[] { new { id = "m", type = "message", content = new[] { new { type = "output_text", text = "<think>a</think>b" } } } } } }));
    p2.Finish();
    var m2 = asm2.Build("aiproxy", "m", null, 0);
    t.Check(m2.Text == "b" && m2.Parts.OfType<ThinkingPart>().Single().Text == "a", "done-only output split");
    return Task.CompletedTask;
});

await t.Run("responses: servers that only send .done events", async () =>
{
    var events = await Collect(aiproxy, Req(M("aiproxy", "done-only")));
    var msg = ((StreamCompleted)events[^1]).Message;
    t.Eq("Summary A\n\nSummary B", string.Concat(events.OfType<ThinkingDelta>().Select(d => d.Text)), "thinking from summary");
    t.Eq("Only done", string.Concat(events.OfType<TextDelta>().Select(d => d.Text)), "text emitted once");
    t.Check(events.OfType<ToolCallStarted>().Single() is { Id: "call_d", Name: "ls" }, "call started");
    t.Check(msg.ToolCalls.Single().Arguments == "{\"dir\":\".\"}", "args");
    t.Eq("ThinkingPart,TextPart,ToolCallPart", string.Join(",", msg.Parts.Select(p => p.GetType().Name)), "parts");
    t.Check(msg.Usage is { InputTokens: 7, OutputTokens: 3 }, "usage");
    t.Eq(16384, mock.Last("/v1/responses").Json["max_output_tokens"]!.GetValue<int>(), "default max output when catalog unknown");
});

await t.Run("responses: only response.completed with output list; generated call id; empty args -> {}", async () =>
{
    var events = await Collect(aiproxy, Req(M("aiproxy", "output-only")));
    var msg = ((StreamCompleted)events[^1]).Message;
    t.Eq("From output", msg.Text, "text");
    var call = msg.ToolCalls.Single();
    t.Check(call.Id.StartsWith("call_") && call.Id.Length > 10 && call.Arguments == "{}", "generated id / {} args");
    t.Eq("tool_use", msg.StopReason, "stop");
});

await t.Run("responses: incomplete (max_output_tokens) -> stop reason length", async () =>
{
    var msg = ((StreamCompleted)(await Collect(aiproxy, Req(M("aiproxy", "incomplete"))))[^1]).Message;
    t.Eq("length", msg.StopReason, "length");
    t.Eq("Truncated ans", msg.Text, "partial text kept");
});

await t.Run("responses: errors (503, context overflow, cut-off, EOF, response.failed)", async () =>
{
    var e1 = await Fails(aiproxy, Req(M("aiproxy", "err-503")));
    t.Check(e1 is { Transient: true, StatusCode: 503, ErrorType: "backend_unavailable" }, "503 backend_unavailable transient");
    t.Check(e1?.Message.Contains("offline") == true, "server message included");
    var e2 = await Fails(aiproxy, Req(M("aiproxy", "overflow")));
    t.Check(e2 is { Transient: false, ContextOverflow: true, StatusCode: 400 }, "400 exceed_context_size -> overflow");
    var e3 = await Fails(aiproxy, Req(M("aiproxy", "cutoff")));
    t.Check(e3 is { Transient: true }, $"cut-off stream transient ({e3?.Message})");
    var e4 = await Fails(aiproxy, Req(M("aiproxy", "eof")));
    t.Check(e4 is { Transient: true, ErrorType: "stream_truncated" }, "EOF before completed -> transient");
    var e5 = await Fails(aiproxy, Req(M("aiproxy", "failed")));
    t.Check(e5 is { Transient: true, ErrorType: "server_error" }, "response.failed server_error transient");

    using var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
    l.Start(); var port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port; l.Stop();
    using var http = new HttpClient();
    var dead = new AP.OpenAiCompatibleProvider("dead", "Dead", http, () => new JsonObject { ["baseUrl"] = $"http://127.0.0.1:{port}" });
    var e6 = await Fails(dead, Req(M("dead", "x")));
    t.Check(e6 is { Transient: true, ErrorType: "network_error" }, "connection refused -> transient");
});

await t.Run("cancellation propagates OperationCanceledException", async () =>
{
    using var cts = new CancellationTokenSource();
    Exception? caught = null;
    var sw = System.Diagnostics.Stopwatch.StartNew();
    try
    {
        await foreach (var e in aiproxy.StreamAsync(Req(M("aiproxy", "slow")), cts.Token))
            if (e is TextDelta) cts.Cancel();
    }
    catch (Exception ex) { caught = ex; }
    t.Check(caught is OperationCanceledException, $"got {caught?.GetType().Name}");
    t.Check(sw.ElapsedMilliseconds < 5000, "cancelled promptly");

    using var cts2 = new CancellationTokenSource();
    cts2.Cancel();
    caught = null;
    try { await Collect(aiproxy, Req(M("aiproxy", "slow")), cts2.Token); } catch (Exception ex) { caught = ex; }
    t.Check(caught is OperationCanceledException, "pre-cancelled token");
});

// ------------------------------------------------------------------ Chat Completions transport

await t.Run("chat: reasoning_content, split <think> tags, tool calls by index, usage", async () =>
{
    var events = await Collect(apPlugin.Providers[1], Req(M("vllm", "gemma-4")));
    var msg = ((StreamCompleted)events[^1]).Message;
    t.Eq("ThinkingPart,TextPart,ToolCallPart,ToolCallPart,ToolCallPart", string.Join(",", msg.Parts.Select(p => p.GetType().Name)), "parts");
    t.Eq("Plan: call tools.inline thought", ((ThinkingPart)msg.Parts[0]).Text, "thinking merged (reasoning_content + <think>)");
    t.Eq("Answer: 42 <b>ok</b>", msg.Text, "text without think tags");
    t.Eq("Plan: call tools.inline thought", string.Concat(events.OfType<ThinkingDelta>().Select(d => d.Text)), "thinking deltas");
    t.Eq("Answer: 42 <b>ok</b>", string.Concat(events.OfType<TextDelta>().Select(d => d.Text)), "text deltas");
    var calls = msg.ToolCalls.ToList();
    t.Check(calls[0] is { Id: "call_1", Name: "read", Arguments: "{\"path\":\"b.txt\"}" }, "call 1 args across chunks");
    t.Check(calls[1] is { Id: "call_2", Name: "ls", Arguments: "{}" }, "call 2");
    t.Check(calls[2].Name == "bash" && calls[2].Id.StartsWith("call_") && calls[2].Arguments == "{\"cmd\":\"ls\"}", "call 3 generated id");
    t.Eq(3, events.OfType<ToolCallStarted>().Count(), "3 ToolCallStarted");
    t.Check(msg.Usage is { InputTokens: 300, CacheReadTokens: 200, OutputTokens: 80, ReasoningTokens: 30 }, "usage normalized");
    t.Eq("tool_use", msg.StopReason, "stop reason");
    t.Eq("vllm", msg.Provider, "provider id");
});

await t.Run("chat: request body (system, tool_calls, tool results, images, stream_options, auth headers)", async () =>
{
    var model = M("vllm", "gemma-4");
    model.Reasoning = new ReasoningInfo { Supported = true, Efforts = ["none", "max"], Default = "max" };
    await Collect(apPlugin.Providers[1], Req(model, Conversation("vllm"), effort: "low"));
    var req = mock.Last("/v1/chat/completions");
    var b = req.Json;
    t.Eq("Bearer sk-test", req.Headers.GetValueOrDefault("authorization"), "bearer auth");
    t.Eq("1", req.Headers.GetValueOrDefault("x-extra"), "custom header");
    t.Check(b["stream_options"]?["include_usage"]?.GetValue<bool>() == true, "include_usage");
    t.Eq(16384, b["max_tokens"]!.GetValue<int>(), "max_tokens default");
    t.Eq("none", b["reasoning_effort"]?.GetValue<string>(), "reasoning_effort mapped");
    t.Eq("function", b["tools"]![0]!["type"]!.GetValue<string>(), "tool type");
    t.Eq("read", b["tools"]![0]!["function"]!["name"]!.GetValue<string>(), "tool function name");
    var msgs = b["messages"]!.AsArray();
    t.Eq("system,user,assistant,tool,tool,user,user", string.Join(",", msgs.Select(m => m!["role"]!.GetValue<string>())), "roles");
    t.Eq("image_url", msgs[1]!["content"]![1]!["type"]!.GetValue<string>(), "user image (modalities unknown for extra endpoint)");
    var asst = msgs[2]!;
    t.Eq("Reading", asst["content"]!.GetValue<string>(), "assistant content");
    t.Check(asst["reasoning_content"] is null, "no reasoning replay by default");
    t.Check(asst["tool_calls"]![0]!["id"]!.GetValue<string>() == "call:1" && asst["tool_calls"]![1]!["function"]!["arguments"]!.GetValue<string>() == "not json", "tool_calls");
    t.Check(msgs[3]!["tool_call_id"]!.GetValue<string>() == "call:1" && msgs[3]!["content"]!.GetValue<string>() == "content A", "tool result");
    t.Check(msgs[5]!["content"]!.AsArray().Any(c => c!["type"]!.GetValue<string>() == "image_url"), "tool images after tool messages");
});

await t.Run("chat: images dropped for a text-only catalog model (aiproxy gemma-4 via transport override)", async () =>
{
    apCtx.SettingsImpl.Set("providers.aiproxy.transport", "chat");
    try
    {
        await Collect(aiproxy, Req(Cat("gemma-4"), Conversation("aiproxy")));
        var b = mock.Last("/v1/chat/completions").Json;
        var msgs = b["messages"]!.AsArray();
        t.Check(msgs[1]!["content"]!.AsArray().All(c => c!["type"]!.GetValue<string>() == "text"), "user image replaced by text");
        t.Check(msgs.All(m => m!["content"] is not JsonArray a || a.All(c => c!["type"]!.GetValue<string>() != "image_url")), "no image_url anywhere");
        t.Check(b["max_tokens"]!.GetValue<int>() == 16384, "max_tokens default when catalog null");
    }
    finally { apCtx.SettingsImpl.Set("providers.aiproxy.transport", null); }
});

await t.Run("per-model transport override with a dotted model id", async () =>
{
    apCtx.SettingsImpl.Set("providers.aiproxy.models", new JsonObject { ["qwen3.8-27b"] = new JsonObject { ["transport"] = "chat" } });
    try
    {
        var before = mock.Requests.Count(r => r.Path == "/v1/chat/completions");
        await Collect(aiproxy, Req(Cat("qwen3.8-27b")));
        t.Eq(before + 1, mock.Requests.Count(r => r.Path == "/v1/chat/completions"), "went to chat/completions");
        var gemma = mock.Requests.Count(r => r.Path == "/v1/responses");
        await Collect(aiproxy, Req(Cat("gemma-4")));
        t.Eq(gemma + 1, mock.Requests.Count(r => r.Path == "/v1/responses"), "other models still use responses");
    }
    finally { apCtx.SettingsImpl.Set("providers.aiproxy.models", null); }
});

await t.Run("chat: finish length, EOF, cut-off, error chunk, 400 overflow, non-streamed JSON body", async () =>
{
    var vllm = apPlugin.Providers[1];
    var len = ((StreamCompleted)(await Collect(vllm, Req(M("vllm", "length"))))[^1]).Message;
    t.Eq("length", len.StopReason, "length");
    var e1 = await Fails(vllm, Req(M("vllm", "eof")));
    t.Check(e1 is { Transient: true, ErrorType: "stream_truncated" }, "EOF without finish_reason");
    var e2 = await Fails(vllm, Req(M("vllm", "cutoff")));
    t.Check(e2 is { Transient: true }, "cut-off");
    var e3 = await Fails(vllm, Req(M("vllm", "err-chunk")));
    t.Check(e3 is { Transient: true, ErrorType: "server_error" }, $"error chunk ({e3?.Message})");
    var e4 = await Fails(vllm, Req(M("vllm", "overflow")));
    t.Check(e4 is { ContextOverflow: true, Transient: false }, "overflow");
    var e5 = await Fails(vllm, Req(M("vllm", "err-503")));
    t.Check(e5 is { Transient: true, StatusCode: 503 }, "503");
    var events = await Collect(vllm, Req(M("vllm", "json")));
    var msg = ((StreamCompleted)events[^1]).Message;
    t.Check(msg.Text == "Plain JSON" && ((ThinkingPart)msg.Parts[0]).Text == "t" && msg.ToolCalls.Single().Id == "call_j", "JSON body parsed");
    t.Check(msg.Usage is { InputTokens: 10, CacheReadTokens: 2, OutputTokens: 4 }, "DeepSeek-style cache hit tokens");
});

await t.Run("disabled provider: empty list + non-transient error", async () =>
{
    apCtx.SettingsImpl.Set("providers.aiproxy.enabled", false);
    try
    {
        t.Eq(0, (await aiproxy.ListModelsAsync(false, CancellationToken.None)).Count, "empty list");
        var e = await Fails(aiproxy, Req(Cat("gemma-4")));
        t.Check(e is { Transient: false, ErrorType: "provider_disabled" }, "disabled error");
    }
    finally { apCtx.SettingsImpl.Set("providers.aiproxy.enabled", true); }
    t.Eq(4, (await aiproxy.ListModelsAsync(false, CancellationToken.None)).Count, "re-enabled");
});

await t.Run("aiproxy plugin: extras reconciled on settings.changed; StopAsync cleans up", async () =>
{
    var changed = apCtx.Bus.Count(EventTypes.ModelsChanged);
    apCtx.SettingsImpl.Set("providers.openaiCompatible", new JsonArray(
        new JsonObject { ["id"] = "remote", ["name"] = "Remote", ["baseUrl"] = mock.BaseUrl, ["transport"] = "responses", ["local"] = false }));
    apCtx.Events.Publish(EventTypes.SettingsChanged, new JsonObject { ["path"] = "providers.openaiCompatible" });
    var ids = apCtx.ServicesImpl.GetAll<IModelProvider>().Select(p => p.Id).OrderBy(x => x).ToList();
    t.Eq("aiproxy,remote", string.Join(",", ids), "vllm removed, remote added");
    t.Check(apCtx.Bus.Count(EventTypes.ModelsChanged) > changed, "models.changed published");
    var remote = apCtx.ServicesImpl.GetAll<IModelProvider>().Single(p => p.Id == "remote");
    t.Check(!remote.IsLocal, "local=false honoured");
    var msg = ((StreamCompleted)(await Collect(remote, Req(M("remote", "qwen3.8-27b"))))[^1]).Message;
    t.Eq("remote", msg.Provider, "extra provider streams via responses");

    // Endpoint identity change (api key) triggers a background refresh.
    var calls = mock.ModelsCalls;
    apCtx.SettingsImpl.Set("providers.aiproxy.apiKey", "k2");
    t.Check(aiproxy.SettingsChangedSinceLastList, "fingerprint changed");
    apCtx.Events.Publish(EventTypes.SettingsChanged, null);
    for (var i = 0; i < 100 && aiproxy.SettingsChangedSinceLastList; i++) await Task.Delay(20);
    t.Check(mock.ModelsCalls > calls && !aiproxy.SettingsChangedSinceLastList, "background refresh after endpoint change");
    t.Eq("Bearer k2", mock.Requests.Last(r => r.Path == "/v1/models").Headers.GetValueOrDefault("authorization"), "new key used");

    var subs = apCtx.Bus.SubscriberCount;
    await apPlugin.StopAsync(CancellationToken.None);
    t.Eq(subs - 1, apCtx.Bus.SubscriberCount, "settings subscription disposed");
    t.Eq("aiproxy", string.Join(",", apCtx.ServicesImpl.GetAll<IModelProvider>().Select(p => p.Id)), "extras unregistered on stop");
    var after = await aiproxy.ListModelsAsync(true, CancellationToken.None);
    t.Check(after.Count == 4 && after.All(m => m.Status == "offline"), "disposed client -> offline list, no throw");
});

// ================================================================== Anthropic

var anCtx = new FakePluginContext(new JsonObject
{
    ["providers"] = new JsonObject { ["anthropic"] = new JsonObject { ["apiKey"] = "test-key", ["baseUrl"] = mock.BaseUrl } },
}, "netpi.providers.anthropic");
var anPlugin = new AN.AnthropicPlugin();
await anPlugin.StartAsync(anCtx, CancellationToken.None);
var anthropic = anPlugin.Provider!;
var sonnet = AN.ClaudeCapabilities.ToModelInfo("anthropic", "claude-sonnet-4-5", null);

await t.Run("anthropic: no API key -> empty list and helpful non-transient error", async () =>
{
    var ctx = new FakePluginContext(new JsonObject { ["providers"] = new JsonObject { ["anthropic"] = new JsonObject { ["baseUrl"] = mock.BaseUrl } } });
    using var http = new HttpClient();
    var p = new AN.AnthropicProvider(http, () => ctx.Settings.GetNode("providers.anthropic") as JsonObject);
    t.Eq(0, (await p.ListModelsAsync(true, CancellationToken.None)).Count, "empty list");
    var e = await Fails(p, Req(sonnet));
    t.Check(e is { Transient: false } && e.Message.Contains("ANTHROPIC_API_KEY"), "helpful error");
    Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", "env-key");
    try
    {
        var list = await p.ListModelsAsync(true, CancellationToken.None);
        t.Eq(2, list.Count, "env var fallback key");
        t.Eq("env-key", mock.Requests.Last(r => r.Path == "/v1/models").Headers.GetValueOrDefault("x-api-key"), "env key sent");
    }
    finally { Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", null);
Environment.SetEnvironmentVariable("OPENROUTER_API_KEY", null); }
});

await t.Run("anthropic: models list merged with capability table; fallback list on failure", async () =>
{
    var list = await anthropic.ListModelsAsync(false, CancellationToken.None);
    t.Eq(2, list.Count, "count");
    var s = list.Single(m => m.Id == "claude-sonnet-4-5-20250929");
    t.Check(s is { DisplayName: "Claude Sonnet 4.5", ContextWindow: 200000, MaxOutputTokens: 64000, IsLocal: false } && s.SupportsImages, "sonnet caps");
    t.Check(s.Reasoning is { Supported: true, Default: "medium" } && string.Join(",", s.Reasoning.Efforts) == "none,low,medium,high,max", "reasoning info");
    var h = list.Single(m => m.Id == "claude-3-5-haiku-20241022");
    t.Check(h.MaxOutputTokens == 8192 && h.Reasoning is null, "legacy haiku caps");
    var req = mock.Requests.Last(r => r.Path == "/v1/models" && r.Headers.ContainsKey("x-api-key"));
    t.Eq("2023-06-01", req.Headers.GetValueOrDefault("anthropic-version"), "version header");
    t.Check(anCtx.Bus.Count(EventTypes.ModelsChanged) == 1, "models.changed on first load");

    anCtx.SettingsImpl.Set("providers.anthropic.apiKey", "bad-key");
    try
    {
        var fb = await anthropic.ListModelsAsync(false, CancellationToken.None);
        t.Check(fb.Count == AN.ClaudeCapabilities.FallbackModels.Length && fb.Any(m => m.Id == "claude-sonnet-4-5"), "static fallback list");
        t.Eq("Claude Opus 4.6", fb[0].DisplayName, "prettified display name");
    }
    finally { anCtx.SettingsImpl.Set("providers.anthropic.apiKey", "test-key"); }
});

await t.Run("anthropic: streams thinking+signature, text, tool_use with input_json_delta, usage", async () =>
{
    var events = await Collect(anthropic, Req(sonnet));
    t.Check(events[^1] is StreamCompleted && events.OfType<StreamCompleted>().Count() == 1, "one StreamCompleted");
    var msg = ((StreamCompleted)events[^1]).Message;
    t.Eq("ThinkingPart,TextPart,ToolCallPart", string.Join(",", msg.Parts.Select(p => p.GetType().Name)), "parts");
    var th = (ThinkingPart)msg.Parts[0];
    t.Check(th.Text == "Hmm, let me see." && th.Signature == "SIG==", "thinking + signature");
    t.Eq("Sure, reading.", msg.Text, "text");
    t.Check(msg.ToolCalls.Single() is { Id: "toolu_01", Name: "read", Arguments: "{\"path\": \"c.txt\"}" }, "tool_use");
    t.Eq("{\"path\": \"c.txt\"}", string.Concat(events.OfType<ToolCallArgsDelta>().Select(d => d.Delta)), "args deltas");
    t.Check(msg.Usage is { InputTokens: 100, CacheReadTokens: 50, CacheWriteTokens: 20, OutputTokens: 77 }, "usage");
    t.Eq(2, events.OfType<UsageUpdate>().Count(), "usage at message_start and message_delta");
    t.Eq("tool_use", msg.StopReason, "stop reason");
    t.Eq("anthropic", msg.Provider, "provider");
});

await t.Run("anthropic: request body (cache_control placement, tool_result-first merge, thinking replay, budget)", async () =>
{
    var r = Req(sonnet, Conversation("anthropic"), effort: "high");
    r.Temperature = 0.5;
    await Collect(anthropic, r);
    var req = mock.Last("/v1/messages");
    var b = req.Json;
    t.Eq("test-key", req.Headers.GetValueOrDefault("x-api-key"), "x-api-key");
    t.Eq("2023-06-01", req.Headers.GetValueOrDefault("anthropic-version"), "anthropic-version");
    t.Check(b["stream"]!.GetValue<bool>(), "stream");
    t.Eq("ephemeral", b["system"]![0]!["cache_control"]?["type"]?.GetValue<string>(), "system cache_control");
    t.Check(b["tools"]![0]!["cache_control"] is null && b["tools"]![1]!["cache_control"] is not null, "cache_control on last tool only");
    t.Check(b["tools"]![0]!["input_schema"]?["type"]?.GetValue<string>() == "object", "input_schema");
    t.Check(b["thinking"]?["type"]?.GetValue<string>() == "enabled" && b["thinking"]?["budget_tokens"]?.GetValue<int>() == 16384, "thinking budget (high)");
    t.Eq(32000, b["max_tokens"]!.GetValue<int>(), "max_tokens");
    t.Check(b["temperature"] is null, "no temperature with thinking");

    var msgs = b["messages"]!.AsArray();
    t.Eq("user,assistant,user", string.Join(",", msgs.Select(m => m!["role"]!.GetValue<string>())), "turns merged");
    var a = msgs[1]!["content"]!.AsArray();
    t.Eq("thinking,redacted_thinking,text,tool_use,tool_use", string.Join(",", a.Select(x => x!["type"]!.GetValue<string>())), "assistant blocks");
    t.Eq("S1", a[0]!["signature"]!.GetValue<string>(), "signature replayed");
    t.Eq("R1", a[1]!["data"]!.GetValue<string>(), "redacted data");
    t.Check(a[3]!["id"]!.GetValue<string>() == "call_1" && a[3]!["input"]!["path"]!.GetValue<string>() == "a", "tool_use id sanitized + input parsed");
    t.Check(a[4]!["input"] is JsonObject { Count: 0 }, "invalid args -> {}");
    var u = msgs[2]!["content"]!.AsArray();
    t.Eq("tool_result,tool_result,text,image", string.Join(",", u.Select(x => x!["type"]!.GetValue<string>())), "tool_result blocks first");
    t.Eq("call_1", u[0]!["tool_use_id"]!.GetValue<string>(), "tool_use_id sanitized");
    t.Eq("text,image", string.Join(",", u[0]!["content"]!.AsArray().Select(x => x!["type"]!.GetValue<string>())), "tool_result with image content array");
    t.Check(u[1]!["is_error"]?.GetValue<bool>() == true && u[1]!["content"]!.GetValue<string>() == "(no output)", "error result");
    t.Check(u[^1]!["cache_control"] is not null, "cache_control on last block of last user message");
    t.Check(u.Take(u.Count - 1).All(x => x!["cache_control"] is null) && msgs[0]!["content"]!.AsArray().All(x => x!["cache_control"] is null), "only one message breakpoint");
    t.Eq("image/jpeg", u[3]!["source"]!["media_type"]!.GetValue<string>(), "image source");
});

await t.Run("anthropic: thinking modes (foreign assistant turn, adaptive, off, max budget, none)", async () =>
{
    // Previous assistant turn from another provider: thinking cannot be replayed -> thinking disabled for this call.
    var r = Req(sonnet, Conversation("aiproxy").Take(4).ToList(), effort: "high");
    r.Temperature = 0.5;
    await Collect(anthropic, r);
    var b = mock.Last("/v1/messages").Json;
    t.Check(b["thinking"] is null, "thinking disabled when last assistant turn lacks thinking");
    t.Eq(0.5, b["temperature"]?.GetValue<double>(), "temperature sent without thinking");
    t.Check(b["messages"]![1]!["content"]!.AsArray().All(x => x!["type"]!.GetValue<string>() is "text" or "tool_use"), "foreign thinking not replayed");

    await Collect(anthropic, Req(sonnet, effort: "max"));
    b = mock.Last("/v1/messages").Json;
    t.Check(b["thinking"]?["budget_tokens"]?.GetValue<int>() == 32000 && b["max_tokens"]!.GetValue<int>() > 32000, "max budget bumps max_tokens");

    await Collect(anthropic, Req(sonnet, effort: "none"));
    t.Check(mock.Last("/v1/messages").Json["thinking"] is null, "effort none -> no thinking");

    await Collect(anthropic, Req(sonnet));
    t.Eq(8192, mock.Last("/v1/messages").Json["thinking"]?["budget_tokens"]?.GetValue<int>(), "no effort -> model default (medium)");

    anCtx.SettingsImpl.Set("providers.anthropic.thinking", "adaptive");
    await Collect(anthropic, Req(sonnet, effort: "high"));
    b = mock.Last("/v1/messages").Json;
    t.Check(b["thinking"]?["type"]?.GetValue<string>() == "adaptive" && b["output_config"]?["effort"]?.GetValue<string>() == "high", "adaptive");

    anCtx.SettingsImpl.Set("providers.anthropic.thinking", "off");
    anCtx.SettingsImpl.Set("providers.anthropic.promptCaching", false);
    await Collect(anthropic, Req(sonnet, effort: "high"));
    b = mock.Last("/v1/messages").Json;
    t.Check(b["thinking"] is null, "off");
    t.Check(!b.ToJsonString().Contains("cache_control"), "promptCaching=false");
    anCtx.SettingsImpl.Set("providers.anthropic.thinking", null);
    anCtx.SettingsImpl.Set("providers.anthropic.promptCaching", null);
});

await t.Run("anthropic: no tools defined -> tool blocks flattened to text; short explicit max tokens -> no thinking", async () =>
{
    var r = Req(sonnet, Conversation("anthropic"));
    r.Tools = [];
    r.MaxOutputTokens = 200;
    await Collect(anthropic, r);
    var b = mock.Last("/v1/messages").Json;
    var json = b["messages"]!.ToJsonString();
    t.Check(!json.Contains("\"tool_use\"") && !json.Contains("\"tool_result\""), "no tool blocks without tools");
    t.Check(json.Contains("[Tool call read (call:1)]") && json.Contains("[Tool result for read (call:1)]"), "flattened text");
    t.Check(b["tools"] is null && b["thinking"] is null && b["max_tokens"]!.GetValue<int>() == 200, "title-style call: no thinking, max_tokens kept");
});

await t.Run("anthropic: errors and stop reasons (529, in-stream error, prompt too long, cut-off, redacted, max_tokens)", async () =>
{
    var e1 = await Fails(anthropic, Req(M("anthropic", "overloaded")));
    t.Check(e1 is { Transient: true, StatusCode: 529, ErrorType: "overloaded_error" }, "529");
    var e2 = await Fails(anthropic, Req(M("anthropic", "stream-error")));
    t.Check(e2 is { Transient: true, ErrorType: "overloaded_error" }, "in-stream overloaded_error");
    var e3 = await Fails(anthropic, Req(M("anthropic", "prompt-too-long")));
    t.Check(e3 is { Transient: false, ContextOverflow: true, StatusCode: 400 } && e3.Message.Contains("prompt is too long"), "prompt too long");
    var e4 = await Fails(anthropic, Req(M("anthropic", "cutoff")));
    t.Check(e4 is { Transient: true }, "cut-off");

    var red = ((StreamCompleted)(await Collect(anthropic, Req(M("anthropic", "redacted"))))[^1]).Message;
    t.Check(red.Parts[0] is ThinkingPart { Redacted: "REDACTED_BLOB", Text: "" } && red.Text == "Done." && red.StopReason == "stop", "redacted thinking + end_turn");
    var mt = ((StreamCompleted)(await Collect(anthropic, Req(M("anthropic", "max-tokens"))))[^1]).Message;
    t.Eq("length", mt.StopReason, "max_tokens -> length");
    t.Check(mt.Usage is { InputTokens: 3, OutputTokens: 100 }, "usage");
});

await t.Run("anthropic plugin: registration and stop", async () =>
{
    t.Eq("anthropic", anCtx.ServicesImpl.Get<IModelProvider>()?.Id, "registered");
    t.Check(!anthropic.IsLocal && anthropic.DisplayName == "Anthropic", "cloud provider");
    var subs = anCtx.Bus.SubscriberCount;
    await anPlugin.StopAsync(CancellationToken.None);
    t.Eq(subs - 1, anCtx.Bus.SubscriberCount, "subscription disposed");
    var e = await Fails(anthropic, Req(sonnet));
    t.Check(e is { Transient: true }, "calls after stop fail as transient (disposed client)");
});

// ================================================================== OpenRouter

var orCtx = new FakePluginContext(new JsonObject
{
    ["providers"] = new JsonObject
    {
        ["openrouter"] = new JsonObject
        {
            ["baseUrl"] = mock.BaseUrl + "/openrouter/api", ["apiKey"] = "or-key", ["headers"] = new JsonObject { ["X-OpenRouter-Title"] = "NetPI" },
        },
    },
}, "netpi.providers.openrouter");
var orDumps = Path.Combine(Path.GetTempPath(), "netpi-or-" + Guid.NewGuid().ToString("N"));
var openrouter = new OR.OpenRouterProvider(new HttpClient(), () => orCtx.SettingsImpl.GetNode("providers.openrouter") as JsonObject, null, orCtx.Bus, orDumps);
async Task<ModelInfo> OrModel(string id) => (await openrouter.ListModelsAsync(false, CancellationToken.None)).Single(m => m.Id == id);

await t.Run("openrouter: catalog = tool-capable models; context, output, modalities, reasoning efforts; include and overrides", async () =>
{
    var models = await openrouter.ListModelsAsync(true, CancellationToken.None);
    t.Eq("anthropic/claude-x,stealth/bunny,vendor/any-effort,vendor/plain:free,vendor/switch", string.Join(",", models.Select(m => m.Id).OrderBy(x => x)), "no model without tool support");
    var bunny = models.Single(m => m.Id == "stealth/bunny");
    t.Check(bunny is { Provider: "openrouter", DisplayName: "Space Bunny", ContextWindow: 1000000, MaxOutputTokens: 524288, IsLocal: false }, "bunny basics");
    t.Check(bunny.SupportsImages, "image input");
    t.Eq("low,medium,high,xhigh,max", string.Join(",", bunny.Reasoning!.Efforts), "efforts low→high, no none (mandatory)");
    t.Eq("max", bunny.Reasoning.Default, "default effort");
    t.Eq("none,low,medium,high", string.Join(",", models.Single(m => m.Id == "anthropic/claude-x").Reasoning!.Efforts), "none when it can be turned off");
    t.Eq("none", string.Join(",", models.Single(m => m.Id == "vendor/switch").Reasoning!.Efforts), "on/off only");
    t.Check(models.Single(m => m.Id == "vendor/plain:free").Reasoning is { Supported: false }, "no reasoning");
    t.Eq("none,minimal,low,medium,high,xhigh,max", string.Join(",", models.Single(m => m.Id == "vendor/any-effort").Reasoning!.Efforts), "every effort (supported_efforts: null)");
    t.Eq("Bearer or-key", mock.Last("/openrouter/api/v1/models").Headers.GetValueOrDefault("authorization"), "key sent");

    orCtx.SettingsImpl.Set("providers.openrouter.include", new JsonArray("stealth/*", "vendor/no-tools", "*:free"));
    orCtx.SettingsImpl.Set("providers.openrouter.models", new JsonObject { ["vendor/plain:free"] = new JsonObject { ["hidden"] = true }, ["stealth/bunny"] = new JsonObject { ["displayName"] = "Bunny" } });
    try
    {
        t.Check(openrouter.SettingsChangedSinceLastList, "settings change noticed");
        var narrowed = await openrouter.ListModelsAsync(false, CancellationToken.None);
        t.Eq("stealth/bunny,vendor/no-tools", string.Join(",", narrowed.Select(m => m.Id).OrderBy(x => x)), "globs; an exact id is offered even without tools; hidden");
        t.Eq("Bunny", narrowed.Single(m => m.Id == "stealth/bunny").DisplayName, "override");
    }
    finally
    {
        orCtx.SettingsImpl.Set("providers.openrouter.include", null);
        orCtx.SettingsImpl.Set("providers.openrouter.models", null);
        await openrouter.ListModelsAsync(true, CancellationToken.None);
    }
});

await t.Run("openrouter: request (reasoning object, max_tokens cap, session_id, routing, caching, headers)", async () =>
{
    var bunny = await OrModel("stealth/bunny");
    await Collect(openrouter, Req(bunny));
    var sent = mock.Last("/openrouter/api/v1/chat/completions");
    var b = sent.Json;
    t.Eq("stealth/bunny", b["model"]?.GetValue<string>(), "model id");
    t.Check(b["stream"]?.GetValue<bool>() == true, "streaming");
    t.Eq(32768, b["max_tokens"]?.GetValue<int>(), "524k catalog output capped at maxOutputTokens");
    t.Check(b["reasoning"] is null, "no effort chosen: the model default");
    t.Eq("ses_1", b["session_id"]?.GetValue<string>(), "session id for sticky routing");
    t.Eq("system", b["messages"]![0]!["role"]?.GetValue<string>(), "system prompt first");
    t.Eq("read", b["tools"]![0]!["function"]!["name"]?.GetValue<string>(), "tools");
    t.Check(b["cache_control"] is null && b["provider"] is null, "no caching field for non-Claude, no routing unless set");
    t.Eq("Bearer or-key", sent.Headers.GetValueOrDefault("authorization"), "bearer key");
    t.Eq("NetPI", sent.Headers.GetValueOrDefault("x-openrouter-title"), "extra header");

    JsonNode? R(ModelInfo m, string? effort) => OR.OpenRouterChat.Reasoning(effort, m);
    t.Eq("""{"effort":"xhigh"}""", R(bunny, "xhigh")?.ToJsonString(), "listed effort");
    t.Eq("""{"effort":"low"}""", R(bunny, "minimal")?.ToJsonString(), "nearest listed effort");
    t.Check(R(bunny, "none") is null, "mandatory reasoning is never turned off");
    var claude = await OrModel("anthropic/claude-x");
    t.Eq("""{"effort":"high"}""", R(claude, "max")?.ToJsonString(), "max → high");
    t.Eq("""{"enabled":false}""", R(claude, "none")?.ToJsonString(), "off when none is not a listed effort");
    var sw = await OrModel("vendor/switch");
    t.Eq("""{"enabled":true}""", R(sw, "high")?.ToJsonString(), "no effort levels: on");
    t.Eq("""{"enabled":false}""", R(sw, "none")?.ToJsonString(), "no effort levels: off");
    t.Eq("""{"effort":"minimal"}""", R(await OrModel("vendor/any-effort"), "minimal")?.ToJsonString(), "supported_efforts null: any effort");
    t.Check(R(await OrModel("vendor/plain:free"), "high") is null, "no reasoning model: nothing");
    t.Check(R(bunny, "default") is null, "default");

    orCtx.SettingsImpl.Set("providers.openrouter.provider", new JsonObject { ["sort"] = "throughput" });
    try
    {
        await Collect(openrouter, Req(claude, effort: "medium"));
        var c = mock.Last("/openrouter/api/v1/chat/completions").Json;
        t.Eq("""{"type":"ephemeral"}""", c["cache_control"]?.ToJsonString(), "automatic prompt caching for Claude");
        t.Eq("""{"sort":"throughput"}""", c["provider"]?.ToJsonString(), "routing preferences passed through");
        t.Eq("""{"effort":"medium"}""", c["reasoning"]?.ToJsonString(), "session effort");
        t.Eq(32768, c["max_tokens"]?.GetValue<int>(), "cap below the catalog's 64000");
    }
    finally { orCtx.SettingsImpl.Set("providers.openrouter.provider", null); }
});

await t.Run("openrouter: stream → thinking + merged reasoning_details, text, tool call, usage split, meta", async () =>
{
    var events = await Collect(openrouter, Req(await OrModel("stealth/bunny")));
    t.Eq("Let me think.", string.Concat(events.OfType<ThinkingDelta>().Select(d => d.Text)), "thinking streamed from the reasoning text");
    t.Eq("Reading it.", string.Concat(events.OfType<TextDelta>().Select(d => d.Text)), "text");
    var msg = events.OfType<StreamCompleted>().Single().Message;
    var th = msg.Parts.OfType<ThinkingPart>().Single();
    t.Eq("Let me think.", th.Text, "thinking part");
    t.Eq("""[{"type":"reasoning.text","text":"Let me think.","format":"anthropic-claude-v1","index":0,"signature":"sig-1"},{"type":"reasoning.encrypted","data":"ENC","id":"rs_7","format":"openai-responses-v1","index":1}]""",
        th.ProviderData?["reasoning_details"]?.ToJsonString(), "details merged by index for replay");
    var call = msg.ToolCalls.Single();
    t.Check(call is { Id: "call_or1", Name: "read", Arguments: "{\"path\":\"a.txt\"}" }, "tool call assembled");
    t.Eq("tool_use", msg.StopReason, "stop reason (finish_reason repeated on the usage chunk)");
    t.Check(msg.Usage is { InputTokens: 300, CacheReadTokens: 600, CacheWriteTokens: 100, OutputTokens: 50, ReasoningTokens: 20 }, "usage: prompt = uncached + cache reads + cache writes");
    t.Eq("""{"openrouter":{"generationId":"gen-stealth-bunny","provider":"Stealth","cost":0.0012}}""", msg.Meta?.ToJsonString(), "generation id, upstream provider, cost");
});

await t.Run("openrouter: reasoning_details go back unmodified, only to the model that produced them", async () =>
{
    var bunny = await OrModel("stealth/bunny");
    var first = (await Collect(openrouter, Req(bunny))).OfType<StreamCompleted>().Single().Message;
    var history = new List<ChatMessage>
    {
        ChatMessage.UserText("read a.txt"),
        first,
        new() { Role = MessageRole.Tool, Parts = [new ToolResultPart { CallId = "call_or1", Name = "read", Content = "A" }] },
        new() { Role = MessageRole.Assistant, Provider = "aiproxy", Model = "qwen3.8-27b", Parts = [new ThinkingPart { Text = "local thoughts" }, new TextPart { Text = "Earlier" }] },
        new() { Role = MessageRole.Assistant, Provider = "openrouter", Model = "stealth/bunny", Parts = [new ThinkingPart { Text = "plain only" }, new TextPart { Text = "Before" }] },
        ChatMessage.UserText("go on"),
    };
    await Collect(openrouter, Req(bunny, history));
    var msgs = mock.Last("/openrouter/api/v1/chat/completions").Json["messages"]!.AsArray();
    var assistants = msgs.Where(m => m!["role"]!.GetValue<string>() == "assistant").ToList();
    t.Eq(first.Parts.OfType<ThinkingPart>().Single().ProviderData!["reasoning_details"]!.ToJsonString(), assistants[0]!["reasoning_details"]?.ToJsonString(), "details replayed as received");
    t.Eq("call_or1", assistants[0]!["tool_calls"]![0]!["id"]?.GetValue<string>(), "with its tool call");
    t.Check(assistants[1]!["reasoning"] is null && assistants[1]!["reasoning_details"] is null, "another provider's reasoning is not sent");
    t.Eq("plain only", assistants[2]!["reasoning"]?.GetValue<string>(), "plain reasoning when there are no details");

    await Collect(openrouter, Req(await OrModel("anthropic/claude-x"), history));
    var other = mock.Last("/openrouter/api/v1/chat/completions").Json["messages"]!.AsArray();
    t.Check(other.All(m => m!["reasoning_details"] is null && m["reasoning"] is null), "a different OpenRouter model gets no reasoning");

    orCtx.SettingsImpl.Set("providers.openrouter.replayReasoning", false);
    try
    {
        await Collect(openrouter, Req(bunny, history));
        t.Check(mock.Last("/openrouter/api/v1/chat/completions").Json["messages"]!.AsArray().All(m => m!["reasoning_details"] is null), "replayReasoning: false");
    }
    finally { orCtx.SettingsImpl.Set("providers.openrouter.replayReasoning", null); }
});

await t.Run("openrouter: errors keep the server's text and add generation id, upstream provider, Retry-After; request saved", async () =>
{
    var mid = await Fails(openrouter, Req(M("openrouter", "vendor/mid-error")));
    t.Check(mid is { Transient: true } && mid.Message.Contains("Provider disconnected unexpectedly"), "mid-stream error: " + mid?.Message);
    t.Check(mid!.Message.Contains("upstream provider UpstreamX") && mid.Message.Contains("generation gen-vendor-mid-error"), "upstream + generation id: " + mid.Message);
    var rate = await Fails(openrouter, Req(M("openrouter", "vendor/rate-limited")));
    t.Check(rate is { Transient: true, StatusCode: 429 } && rate.Message.Contains("free-models-per-min") && rate.Message.Contains("retry after 7 s"), "429: " + rate?.Message);
    var bad = await Fails(openrouter, Req(M("openrouter", "vendor/upstream-502")));
    t.Check(bad is { StatusCode: 502 } && bad.Message.Contains("upstream provider SomeHost") && bad.Message.Contains("upstream exploded"), "502: " + bad?.Message);
    var dumps = Directory.GetFiles(Path.Combine(orDumps, "failed-requests"), "*.json");
    t.Eq(3, dumps.Length, "three failed requests saved");
    t.Check(dumps.Select(f => JsonNode.Parse(File.ReadAllText(f))!).All(d => d["request"]?["messages"] is JsonArray && d["generationId"] is not null), "dumps have the body and the generation id");
    Directory.Delete(orDumps, true);
});

await t.Run("openrouter: without an API key no models are offered and calls fail clearly", async () =>
{
    var p = new OR.OpenRouterProvider(new HttpClient(), () => new JsonObject { ["baseUrl"] = mock.BaseUrl + "/openrouter/api" });
    t.Eq(0, (await p.ListModelsAsync(true, CancellationToken.None)).Count, "no models without a key");
    var e = await Fails(p, Req(M("openrouter", "stealth/bunny")));
    t.Check(e is { Transient: false } && e.Message.Contains("OPENROUTER_API_KEY"), "clear error: " + e?.Message);
});

// ================================================================== hot reload

await t.Run("built plugins load, run and unload in a collectible AssemblyLoadContext", async () =>
{
    var root = PluginLoadTest.FindRepoRoot();
    t.Check(root is not null, "repo root found");
    if (root is null) return;

    var apDll = Path.Combine(root, "artifacts/app/plugins/NetPI.Providers.AiProxy/NetPI.Providers.AiProxy.dll");
    var apSettings = new JsonObject { ["providers"] = new JsonObject { ["aiproxy"] = new JsonObject { ["baseUrl"] = mock.BaseUrl } } };
    t.Check(!File.Exists(Path.Combine(Path.GetDirectoryName(apDll)!, "NetPI.Abstractions.dll")), "Abstractions not copied next to the plugin");
    t.Check(File.Exists(Path.Combine(Path.GetDirectoryName(apDll)!, "plugin.json")), "plugin.json copied");
    var r1 = await PluginLoadTest.LoadRunUnloadAsync(apDll, apSettings, p => Req(M(p.Id, "qwen3.8-27b")));
    t.Check(r1 is { PluginId: "netpi.providers.aiproxy", Providers: 1, Models: 4, StreamedText: "Hello world", AbstractionsShared: true }, $"aiproxy ran in ALC ({r1})");
    t.Check(await PluginLoadTest.WaitCollectedAsync(r1.Alc), "aiproxy ALC collected after unload");

    var anDll = Path.Combine(root, "artifacts/app/plugins/NetPI.Providers.Anthropic/NetPI.Providers.Anthropic.dll");
    var anSettings = new JsonObject { ["providers"] = new JsonObject { ["anthropic"] = new JsonObject { ["baseUrl"] = mock.BaseUrl, ["apiKey"] = "k" } } };
    var r2 = await PluginLoadTest.LoadRunUnloadAsync(anDll, anSettings, _ => Req(AN.ClaudeCapabilities.ToModelInfo("anthropic", "claude-sonnet-4-5", null)));
    t.Check(r2 is { PluginId: "netpi.providers.anthropic", Providers: 1, Models: 2, StreamedText: "Sure, reading." }, $"anthropic ran in ALC ({r2})");
    t.Check(await PluginLoadTest.WaitCollectedAsync(r2.Alc), "anthropic ALC collected after unload");

    var orDll = Path.Combine(root, "artifacts/app/plugins/NetPI.Providers.OpenRouter/NetPI.Providers.OpenRouter.dll");
    var orPluginSettings = new JsonObject { ["providers"] = new JsonObject { ["openrouter"] = new JsonObject { ["baseUrl"] = mock.BaseUrl + "/openrouter/api", ["apiKey"] = "k" } } };
    var r3 = await PluginLoadTest.LoadRunUnloadAsync(orDll, orPluginSettings, p => Req(M(p.Id, "vendor/plain:free")));
    t.Check(r3 is { PluginId: "netpi.providers.openrouter", Providers: 1, Models: 5, StreamedText: "Reading it.", AbstractionsShared: true }, $"openrouter ran in ALC ({r3})");
    t.Check(await PluginLoadTest.WaitCollectedAsync(r3.Alc), "openrouter ALC collected after unload");
});

return t.Summary();
