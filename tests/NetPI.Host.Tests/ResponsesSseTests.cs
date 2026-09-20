using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NetPI.Abstractions;
using NetPI.Provider.AiProxy;
using Xunit;

namespace NetPI.Host.Tests;

/// <summary>
/// PLAN §14b: exercises the new /v1/responses wire alongside chat completions.
/// No network is touched — a routing fake handler plays the /v1/models catalog,
/// the responses capability probe, the /v1/responses stream and the
/// /v1/chat/completions stream.
/// </summary>
public class ResponsesSseTests
{
    private sealed class NullLogger : IPluginLogger
    {
        public void Debug(string m) { }
        public void Information(string m) { }
        public void Warning(string m) { }
        public void Error(string m, Exception? e = null) { }
    }

    /// <summary>A recorded request (url, method, serialized body).</summary>
    private sealed record Route(string Url, string Method, string Body);

    /// <summary>
    /// Fake handler that routes on method+path and returns per-route responses.
    /// Records every request so tests can assert WHICH endpoint was hit.
    /// </summary>
    private sealed class WireHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, string, (HttpStatusCode Status, string Body)> _route;
        public List<Route> Sent { get; } = new();

        public WireHandler(Func<HttpRequestMessage, string, (HttpStatusCode, string)> route) => _route = route;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : Encoding.UTF8.GetString(request.Content.ReadAsByteArrayAsync().Result);
            Sent.Add(new Route(request.RequestUri!.ToString(), request.Method.ToString(), body));
            var (status, resp) = _route(request, body);
            var h = new HttpResponseMessage(status)
            {
                Content = new StringContent(resp, Encoding.UTF8, "text/event-stream"),
            };
            return Task.FromResult(h);
        }
    }

    // ---- fixtures ------------------------------------------------------

    private const string Catalog = "{\"data\":[{\"id\":\"m\"}]}";
    // A minimal responses stream: one reasoning lane then one text lane,
    // finishing with usage. Mirrors the chat "Reasoning" transcript.
    private const string RespText =
        "event: response.created\ndata: {\"response\":{\"id\":\"r1\"},\"type\":\"response.created\"}\n" +
        "event: response.in_progress\ndata: {\"response\":{\"id\":\"r1\"},\"type\":\"response.in_progress\"}\n" +
        "event: response.output_item.added\ndata: {\"item\":{\"id\":\"rs_1\",\"type\":\"reasoning\"},\"type\":\"response.output_item.added\"}\n" +
        "event: response.reasoning_text.delta\ndata: {\"delta\":\"think\",\"item_id\":\"rs_1\",\"type\":\"response.reasoning_text.delta\"}\n" +
        "event: response.output_item.added\ndata: {\"item\":{\"id\":\"msg_1\",\"role\":\"assistant\",\"type\":\"message\"},\"type\":\"response.output_item.added\"}\n" +
        "event: response.output_text.delta\ndata: {\"delta\":\"Hello\",\"item_id\":\"msg_1\",\"type\":\"response.output_text.delta\"}\n" +
        "event: response.output_text.delta\ndata: {\"delta\":\" world\",\"item_id\":\"msg_1\",\"type\":\"response.output_text.delta\"}\n" +
        "event: response.completed\ndata: {\"response\":{\"id\":\"r1\",\"usage\":{\"input_tokens\":5,\"output_tokens\":3,\"total_tokens\":8,\"input_tokens_details\":{\"cached_tokens\":2}}},\"type\":\"response.completed\"}\n";

    // The SAME transcript on the chat wire (thinking "think", text "Hello world").
    private const string ChatText =
        "data: {\"choices\":[{\"delta\":{\"reasoning_content\":\"think\"}}]}\n" +
        "data: {\"choices\":[{\"delta\":{\"content\":\"Hello\"}}]}\n" +
        "data: {\"choices\":[{\"delta\":{\"content\":\" world\"}}]}\n" +
        "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":5,\"completion_tokens\":3,\"total_tokens\":8}}\n" +
        "data: [DONE]\n";

    // A responses stream that ends WITHOUT a response.completed event.
    private const string RespNoCompleted =
        "event: response.created\ndata: {\"response\":{\"id\":\"r3\"},\"type\":\"response.created\"}\n" +
        "event: response.output_text.delta\ndata: {\"delta\":\"partial\",\"item_id\":\"msg_x\",\"type\":\"response.output_text.delta\"}\n" +
        "event: response.output_text.delta\ndata: {\"delta\":\" text\",\"item_id\":\"msg_x\",\"type\":\"response.output_text.delta\"}\n";

    // A responses stream with reasoning + a tool call, matching the captured
    // nInfer shapes (fc item id carries the arg deltas; completed carries usage).
    private const string RespTool =
        "event: response.created\ndata: {\"response\":{\"id\":\"r2\"},\"type\":\"response.created\"}\n" +
        "event: response.output_item.added\ndata: {\"item\":{\"id\":\"rs_2\",\"type\":\"reasoning\"},\"type\":\"response.output_item.added\"}\n" +
        "event: response.reasoning_text.delta\ndata: {\"delta\":\"use tool\",\"item_id\":\"rs_2\",\"type\":\"response.reasoning_text.delta\"}\n" +
        "event: response.output_item.added\ndata: {\"item\":{\"id\":\"fc_1\",\"call_id\":\"call_1\",\"name\":\"add\",\"arguments\":\"\",\"type\":\"function_call\"},\"type\":\"response.output_item.added\"}\n" +
        "event: response.function_call_arguments.delta\ndata: {\"delta\":\"{\\\"a\\\":2\",\"item_id\":\"fc_1\",\"type\":\"response.function_call_arguments.delta\"}\n" +
        "event: response.function_call_arguments.delta\ndata: {\"delta\":\",\\\"b\\\":2}\",\"item_id\":\"fc_1\",\"type\":\"response.function_call_arguments.delta\"}\n" +
        "event: response.completed\ndata: {\"response\":{\"id\":\"r2\",\"usage\":{\"input_tokens\":330,\"output_tokens\":79,\"total_tokens\":409,\"input_tokens_details\":{\"cached_tokens\":0}}},\"type\":\"response.completed\"}\n";

    // ---- helpers -------------------------------------------------------

    private static ModelRequest Req() => new()
    {
        ModelId = "m",
        Messages = [new AgentMessage("u", MessageRole.User, [new TextPart("hi")], DateTimeOffset.UtcNow)],
    };

    private static List<string> Kinds(List<ModelEvent> ev) => ev.Select(e => e.Kind).ToList();

    private static async Task<List<ModelEvent>> Collect(AiProxyProvider p, ModelRequest r)
    {
        var list = new List<ModelEvent>();
        await foreach (var e in p.RunAsync(r, CancellationToken.None))
            list.Add(e);
        return list;
    }

    /// <summary>True when the serialized run body (not the probe) is present.</summary>
    private static bool IsResponsesRun(string body) => body.Contains("\"stream\":true", StringComparison.Ordinal);
    private static bool IsResponsesProbe(string body) => body.Contains("\"stream\":false", StringComparison.Ordinal);

    // A catalog route that serves /v1/models and routes /v1/responses by probe-vs-run.
    private static Func<HttpRequestMessage, string, (HttpStatusCode, string)> CatalogRoute(
        string respRun, string respProbe, string chat,
        HttpStatusCode respRunStatus = HttpStatusCode.OK, HttpStatusCode respProbeStatus = HttpStatusCode.OK)
    {
        return (req, body) =>
        {
            var url = req.RequestUri!.ToString();
            if (url.EndsWith("/v1/models", StringComparison.Ordinal)) return (HttpStatusCode.OK, Catalog);
            if (url.EndsWith("/v1/responses", StringComparison.Ordinal))
            {
                if (IsResponsesProbe(body)) return (respProbeStatus, "{\"response\":{\"id\":\"probe\"}}");
                return (respRunStatus, respRun);
            }
            if (url.EndsWith("/v1/chat/completions", StringComparison.Ordinal)) return (HttpStatusCode.OK, chat);
            return (HttpStatusCode.NotFound, "{}");
        };
    }

    private static (AiProxyProvider p, WireHandler h) MakeWire(string wire,
        Func<HttpRequestMessage, string, (HttpStatusCode, string)> route)
    {
        var h = new WireHandler(route);
        var http = new HttpClient(h);
        return (new AiProxyProvider(http, "http://test", new NullLogger(), wire), h);
    }

    // ---- wire selection -------------------------------------------------

    [Fact]
    public async Task Auto_Wire_ProbeOk_UsesResponsesEndpoint()
    {
        var (p, h) = MakeWire("auto", CatalogRoute(RespText, "probe", ChatText));
        await p.RefreshAsync(CancellationToken.None); await p.WaitForProbeAsync();

        var ev = await Collect(p, Req());

        // The run went to /v1/responses (stream:true), never to chat.
        Assert.Contains(h.Sent, s => s.Method == "POST" && s.Url.EndsWith("/v1/responses", StringComparison.Ordinal) && IsResponsesRun(s.Body));
        Assert.DoesNotContain(h.Sent, s => s.Url.EndsWith("/v1/chat/completions", StringComparison.Ordinal));
        // And it produced a clean completed assistant message.
        var done = Assert.Single(ev.OfType<ModelCompleted>());
        Assert.Equal("Hello world", done.Message.Parts.OfType<TextPart>().Single().Text);
    }

    [Fact]
    public async Task Auto_Wire_ProbeFails_FallsBackToChat()
    {
        // Probe returns a non-2xx → SupportsResponses stays false → chat wire.
        var (p, h) = MakeWire("auto", CatalogRoute(RespText, "probe", ChatText, respProbeStatus: HttpStatusCode.BadGateway));
        await p.RefreshAsync(CancellationToken.None); await p.WaitForProbeAsync();

        var ev = await Collect(p, Req());

        // The probe hit /v1/responses once (stream:false), but the RUN used chat.
        Assert.DoesNotContain(h.Sent, s => s.Method == "POST" && s.Url.EndsWith("/v1/responses", StringComparison.Ordinal) && IsResponsesRun(s.Body));
        Assert.Contains(h.Sent, s => s.Method == "POST" && s.Url.EndsWith("/v1/chat/completions", StringComparison.Ordinal));
        Assert.Contains(ev, e => e is ModelCompleted);
    }

    [Fact]
    public async Task ForcedChat_Wire_SkipsResponsesEntirely()
    {
        // wire=chat never probes and never uses /v1/responses.
        var (p, h) = MakeWire("chat", CatalogRoute(RespText, "probe", ChatText));
        await p.RefreshAsync(CancellationToken.None); await p.WaitForProbeAsync();

        var ev = await Collect(p, Req());

        Assert.DoesNotContain(h.Sent, s => s.Url.EndsWith("/v1/responses", StringComparison.Ordinal));
        Assert.Contains(h.Sent, s => s.Method == "POST" && s.Url.EndsWith("/v1/chat/completions", StringComparison.Ordinal));
        Assert.Contains(ev, e => e is ModelCompleted);
    }

    // ---- parity ---------------------------------------------------------

    [Fact]
    public async Task ResponsesAndChat_EmitIdenticalEventKindsAndOrder()
    {
        var (pChat, _) = MakeWire("chat", CatalogRoute(RespText, "probe", ChatText));
        await pChat.RefreshAsync(CancellationToken.None); await pChat.WaitForProbeAsync();
        var chatEv = await Collect(pChat, Req());

        var (pResp, _) = MakeWire("responses", CatalogRoute(RespText, "probe", ChatText));
        await pResp.RefreshAsync(CancellationToken.None); await pResp.WaitForProbeAsync();
        var respEv = await Collect(pResp, Req());

        Assert.Equal(Kinds(chatEv), Kinds(respEv));
        // ...and the assembled text is identical.
        var cText = chatEv.OfType<ModelCompleted>().Single().Message.Parts.OfType<TextPart>().Single().Text;
        var rText = respEv.OfType<ModelCompleted>().Single().Message.Parts.OfType<TextPart>().Single().Text;
        Assert.Equal(cText, rText);
    }

    // ---- responses-specific streams -------------------------------------

    [Fact]
    public async Task Responses_TextAndReasoning_ParsesLanesAndUsage()
    {
        var (p, h) = MakeWire("responses", CatalogRoute(RespText, "probe", ChatText));
        await p.RefreshAsync(CancellationToken.None); await p.WaitForProbeAsync();
        var ev = await Collect(p, Req());

        Assert.Equal("think", string.Concat(ev.OfType<ThinkingDelta>().Select(t => t.Text)));
        Assert.Equal("Hello world", string.Concat(ev.OfType<TextDelta>().Select(t => t.Text)));
        var u = Assert.Single(ev.OfType<UsageUpdated>());
        Assert.Equal(5, u.PromptTokens);
        Assert.Equal(3, u.CompletionTokens);
        Assert.Equal(8, u.TotalTokens);
        Assert.Equal(2, u.CachedTokens); // cached_tokens surfaced from the wire
        Assert.Contains(ev, e => e is ThinkingCompleted);
        Assert.Contains(ev, e => e is TextCompleted);
    }

    [Fact]
    public async Task Responses_ToolCall_FragmentsArgsAndReassembles()
    {
        var (p, _) = MakeWire("responses", CatalogRoute(RespTool, "probe", ChatText));
        await p.RefreshAsync(CancellationToken.None); await p.WaitForProbeAsync();
        var ev = await Collect(p, Req());

        var started = Assert.Single(ev.OfType<ToolCallStarted>());
        Assert.Equal("add", started.Name);

        // Argument fragments are streamed as deltas keyed by the call id.
        var argText = string.Concat(ev.OfType<ToolCallArgumentsDelta>().Select(a => a.Delta));
        Assert.Equal("{\"a\":2,\"b\":2}", argText);

        // The reassembled call is attached to the completed message.
        var done = Assert.Single(ev.OfType<ModelCompleted>());
        var tc = done.Message.Parts.OfType<ToolCallPart>().Single();
        Assert.Equal("add", tc.Name);
        Assert.Equal(2, tc.Arguments.GetProperty("a").GetInt32());
        Assert.Equal(2, tc.Arguments.GetProperty("b").GetInt32());

        var u = Assert.Single(ev.OfType<UsageUpdated>());
        Assert.Equal(330, u.PromptTokens);
        Assert.Equal(79, u.CompletionTokens);
        Assert.Equal(409, u.TotalTokens);
    }

    [Fact]
    public async Task Responses_EofWithoutCompleted_ClosesLanesAndDefaultsUsage()
    {
        var (p, _) = MakeWire("responses", CatalogRoute(RespNoCompleted, "probe", ChatText));
        await p.RefreshAsync(CancellationToken.None); await p.WaitForProbeAsync();
        var ev = await Collect(p, Req());

        Assert.Equal("partial text", string.Concat(ev.OfType<TextDelta>().Select(t => t.Text)));
        var u = ev.OfType<UsageUpdated>().Last();
        Assert.Equal(0, u.TotalTokens); // defaulted, none was streamed
        Assert.Contains(ev, e => e is ModelCompleted);
        Assert.Contains(ev, e => e is TextCompleted);
    }

    [Fact]
    public async Task Responses_HttpErrorBeforeContent_FallsThroughToChat()
    {
        // /v1/responses run returns 502 → ModelFailed before any content →
        // the dispatcher transparently retries via /v1/chat/completions.
        var (p, h) = MakeWire("responses", CatalogRoute(RespText, "probe", ChatText, respRunStatus: HttpStatusCode.BadGateway));
        await p.RefreshAsync(CancellationToken.None); await p.WaitForProbeAsync();
        var ev = await Collect(p, Req());

        // Both endpoints were hit: the failed responses run and the chat retry.
        Assert.Contains(h.Sent, s => s.Method == "POST" && s.Url.EndsWith("/v1/responses", StringComparison.Ordinal) && IsResponsesRun(s.Body));
        Assert.Contains(h.Sent, s => s.Method == "POST" && s.Url.EndsWith("/v1/chat/completions", StringComparison.Ordinal));
        // No raw failure surfaces to the caller — the run completes via chat.
        Assert.DoesNotContain(ev, e => e is ModelFailed);
        var done = Assert.Single(ev.OfType<ModelCompleted>());
        Assert.Equal("Hello world", done.Message.Parts.OfType<TextPart>().Single().Text);
    }

    // ---- chain (PLAN §14c) -------------------------------------------------

    private static ModelRequest Sreq(string sessionId, params AgentMessage[] msgs) => new()
    {
        ModelId = "m",
        SessionId = sessionId,
        Messages = msgs,
    };

    private static AgentMessage User(string id, string text) =>
        new(id, MessageRole.User, [new TextPart(text)], DateTimeOffset.UtcNow);

    private static JsonElement Body(Route r) => JsonDocument.Parse(r.Body).RootElement;

    private static Route ResponsesRun(WireHandler h) =>
        h.Sent.Last(s => s.Method == "POST" && s.Url.EndsWith("/v1/responses", StringComparison.Ordinal) && IsResponsesRun(s.Body));

    private const string RespFailed =
        "event: response.created\ndata: {\"response\":{\"id\":\"rf1\"},\"type\":\"response.created\"}\n" +
        "event: response.failed\ndata: {\"response\":{\"id\":\"rf1\",\"error\":{\"message\":\"boom\"}},\"type\":\"response.failed\"}\n";

    [Fact]
    public async Task Chain_SecondTurn_ChainsPreviousIdWithDeltaOnly()
    {
        var (p, h) = MakeWire("responses", CatalogRoute(RespText, "probe", ChatText));
        await p.RefreshAsync(CancellationToken.None); await p.WaitForProbeAsync();

        var u1 = User("u1", "hi");
        var done = (await Collect(p, Sreq("s1", u1))).OfType<ModelCompleted>().Single();

        // First run: reset shape — full input, no previous id, store:true.
        var first = Body(ResponsesRun(h));
        Assert.False(first.TryGetProperty("previous_response_id", out _));
        Assert.True(first.GetProperty("store").GetBoolean());
        Assert.Equal(1, first.GetProperty("input").GetArrayLength());

        // Second turn: same transcript + the completed assistant message + a new
        // user message → chain: previous_response_id + delta only (the covered
        // u1+assistant items are NOT resent).
        var u2 = User("u2", "next");
        await Collect(p, Sreq("s1", u1, done.Message, u2));

        var second = Body(ResponsesRun(h));
        Assert.Equal("r1", second.GetProperty("previous_response_id").GetString());
        var input = second.GetProperty("input");
        Assert.Equal(1, input.GetArrayLength());
        Assert.Equal("next", input[0].GetProperty("content")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task Chain_FailedRun_DoesNotAdvanceHead()
    {
        var (p, h) = MakeWire("responses", CatalogRoute(RespFailed, "probe", ChatText));
        await p.RefreshAsync(CancellationToken.None); await p.WaitForProbeAsync();

        var u1 = User("u1", "hi");
        var ev = await Collect(p, Sreq("s1", u1));
        // The pre-content failure is retried transparently via chat.
        Assert.DoesNotContain(ev, e => e is ModelFailed);
        Assert.Contains(ev, e => e is ModelCompleted);

        // Next turn over the same transcript: no head was advanced → reset with
        // the full input and no previous_response_id.
        await Collect(p, Sreq("s1", u1, User("u2", "next")));
        var body = Body(ResponsesRun(h));
        Assert.False(body.TryGetProperty("previous_response_id", out _));
        Assert.Equal(2, body.GetProperty("input").GetArrayLength());
    }

    [Fact]
    public async Task Chain_TranscriptChanged_ResetsToFullInput()
    {
        var (p, h) = MakeWire("responses", CatalogRoute(RespText, "probe", ChatText));
        await p.RefreshAsync(CancellationToken.None); await p.WaitForProbeAsync();
        var sys = new AgentMessage("s", MessageRole.System, [new TextPart("be brief")], DateTimeOffset.UtcNow);
        var done = (await Collect(p, Sreq("s1", sys, User("u1", "hi")))).OfType<ModelCompleted>().Single();

        // The first user message was edited after the run → the fingerprint
        // prefix no longer matches → reset, full input (3 items; system goes
        // to instructions), no previous id.
        await Collect(p, Sreq("s1", sys, User("u1", "edited"), done.Message, User("u2", "next")));
        var body = Body(ResponsesRun(h));
        Assert.False(body.TryGetProperty("previous_response_id", out _));
        Assert.Equal("be brief", body.GetProperty("instructions").GetString());
        Assert.Equal(4, body.GetProperty("input").GetArrayLength());
    }

    [Fact]
    public async Task ToolBatch_EmitsOneItemPerResult_BothWires()
    {
        var asst = new AgentMessage("a1", MessageRole.Assistant,
            [
                new ToolCallPart("call_1", "add", JsonDocument.Parse("{\"a\":1}").RootElement.Clone()),
                new ToolCallPart("call_2", "mul", JsonDocument.Parse("{\"b\":2}").RootElement.Clone()),
            ], DateTimeOffset.UtcNow);
        var tool = new AgentMessage("t1", MessageRole.Tool,
            [
                new ToolResultPart("call_1", "add", [new TextPart("2")]),
                new ToolResultPart("call_2", "mul", [new TextPart("4")]),
            ], DateTimeOffset.UtcNow);

        // Responses wire: one function_call_output item per result (not just the
        // first one).
        var (pR, hR) = MakeWire("responses", CatalogRoute(RespText, "probe", ChatText));
        await pR.RefreshAsync(CancellationToken.None); await pR.WaitForProbeAsync();
        await Collect(pR, Sreq("s1", asst, tool, User("u1", "again")));
        var items = Body(ResponsesRun(hR)).GetProperty("input").EnumerateArray().ToList();
        var fco = items.Where(it => it.GetProperty("type").GetString() == "function_call_output").ToList();
        Assert.Equal(2, fco.Count);
        Assert.Contains(fco, it => it.GetProperty("call_id").GetString() == "call_1");
        Assert.Contains(fco, it => it.GetProperty("call_id").GetString() == "call_2");

        // Chat wire: one tool message per tool_call_id.
        var (pC, hC) = MakeWire("chat", CatalogRoute(RespText, "probe", ChatText));
        await pC.RefreshAsync(CancellationToken.None); await pC.WaitForProbeAsync();
        await Collect(pC, Sreq("s1", asst, tool, User("u1", "again")));
        var msgs = Body(hC.Sent.Single(s => s.Url.EndsWith("/v1/chat/completions", StringComparison.Ordinal)))
            .GetProperty("messages").EnumerateArray().ToList();
        var toolMsgs = msgs.Where(m => m.GetProperty("role").GetString() == "tool").ToList();
        Assert.Equal(2, toolMsgs.Count);
        Assert.Equal("call_1", toolMsgs[0].GetProperty("tool_call_id").GetString());
        Assert.Equal("call_2", toolMsgs[1].GetProperty("tool_call_id").GetString());
    }
}
