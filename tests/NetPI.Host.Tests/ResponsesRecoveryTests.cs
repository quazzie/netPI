using System.Net;
using System.Net.Http;
using System.Text;
using NetPI.Abstractions;
using NetPI.Provider.AiProxy;
using Xunit;

namespace NetPI.Host.Tests;

/// <summary>
/// docs/plans/compaction-tool-history.md §5: make the Responses reset and
/// recovery observable and precise. A stale chained reference (ninfer's 404
/// <c>response_not_found</c>) is healed with ONE reset retry; anything else — a
/// malformed history (400 invalid_tool_history), a generic 404, or a failure
/// after content — is surfaced, never replayed. The actual request mode
/// (chained vs reset) is computed once and reported truthfully.
/// </summary>
public class ResponsesRecoveryTests
{
    private const string Catalog = "{\"data\":[{\"id\":\"m\"}]}";
    private const string RespText =
        "event: response.created\ndata: {\"response\":{\"id\":\"r1\"},\"type\":\"response.created\"}\n" +
        "event: response.output_item.added\ndata: {\"item\":{\"id\":\"msg_1\",\"role\":\"assistant\",\"type\":\"message\"},\"type\":\"response.output_item.added\"}\n" +
        "event: response.output_text.delta\ndata: {\"delta\":\"Hello\",\"item_id\":\"msg_1\",\"type\":\"response.output_text.delta\"}\n" +
        "event: response.output_text.delta\ndata: {\"delta\":\" world\",\"item_id\":\"msg_1\",\"type\":\"response.output_text.delta\"}\n" +
        "event: response.completed\ndata: {\"response\":{\"id\":\"r1\",\"usage\":{\"input_tokens\":5,\"output_tokens\":3,\"total_tokens\":8,\"input_tokens_details\":{\"cached_tokens\":2}}},\"type\":\"response.completed\"}\n";
    private const string ChatText =
        "data: {\"choices\":[{\"delta\":{\"content\":\"Hello world\"}}]}\n" +
        "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":5,\"completion_tokens\":3,\"total_tokens\":8}}\n" +
        "data: [DONE]\n";

    /// <summary>A stream that emits content, then fails with response_not_found —
    /// a failure AFTER content must never be retried.</summary>
    private const string FailAfterContent =
        "event: response.created\ndata: {\"response\":{\"id\":\"r9\"},\"type\":\"response.created\"}\n" +
        "event: response.output_text.delta\ndata: {\"delta\":\"partial\",\"item_id\":\"m1\",\"type\":\"response.output_text.delta\"}\n" +
        "event: response.failed\ndata: {\"response\":{\"id\":\"r9\",\"error\":{\"code\":\"response_not_found\",\"message\":\"response_not_found\"}},\"type\":\"response.failed\"}\n";

    private const string StaleBody = "{\"error\":{\"code\":\"response_not_found\",\"message\":\"response_not_found\"}}";
    private const string InvalidHistoryBody = "{\"error\":{\"code\":\"invalid_tool_history\",\"message\":\"invalid_tool_history\"}}";

    private sealed class NullLogger : IPluginLogger
    {
        public void Debug(string m) { }
        public void Information(string m) { }
        public void Warning(string m) { }
        public void Error(string m, Exception? e = null) { }
    }

    private sealed record Route(string Url, string Method, string Body);

    /// <summary>Scripted handler: catalog + probe are fixed; /v1/responses RUN
    /// requests consume the queue in order, chat is fixed.</summary>
    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly List<(HttpStatusCode, string)> _respQueue;
        public List<Route> Sent { get; } = new();
        public ScriptedHandler(List<(HttpStatusCode, string)> respQueue) { _respQueue = respQueue; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : Encoding.UTF8.GetString(request.Content.ReadAsByteArrayAsync().Result);
            Sent.Add(new Route(request.RequestUri!.ToString(), request.Method.ToString(), body));
            var (status, resp) = Route(req: request, body);
            return Task.FromResult(new HttpResponseMessage(status)
                { Content = new StringContent(resp, Encoding.UTF8, "text/event-stream") });

            (HttpStatusCode, string) Route(HttpRequestMessage req, string body)
            {
                var url = req.RequestUri!.ToString();
                if (url.EndsWith("/v1/models", StringComparison.Ordinal)) return (HttpStatusCode.OK, Catalog);
                if (url.EndsWith("/v1/responses", StringComparison.Ordinal))
                {
                    if (body.Contains("\"stream\":false", StringComparison.Ordinal))
                        return (HttpStatusCode.OK, "{\"response\":{\"id\":\"probe\"}}");
                    var next = _respQueue.Count > 0 ? _respQueue[0] : (HttpStatusCode.OK, RespText);
                    _respQueue.RemoveAt(0);
                    return next;
                }
                if (url.EndsWith("/v1/chat/completions", StringComparison.Ordinal)) return (HttpStatusCode.OK, ChatText);
                return (HttpStatusCode.NotFound, "{}");
            }
        }
    }

    private sealed class RecordingBus : IEventBus
    {
        public readonly List<ModelRequestDiagnostics> Captured = new();
        public IDisposable Subscribe<TEvent>(NetPI.Abstractions.EventHandler<TEvent> handler, EventSubscriptionOptions? options = null)
            => new Noop();
        public ValueTask PublishAsync<TEvent>(TEvent @event, CancellationToken cancellationToken = default) where TEvent : notnull
        {
            if (@event is ModelRequestDiagnostics d) lock (Captured) Captured.Add(d);
            return ValueTask.CompletedTask;
        }
        private sealed class Noop : IDisposable { public void Dispose() { } }
    }

    private static bool IsResponsesRun(string body) => body.Contains("\"stream\":true", StringComparison.Ordinal);

    private static List<string> RunBodies(ScriptedHandler h)
        => h.Sent.Where(s => s.Url.EndsWith("/v1/responses", StringComparison.Ordinal) && IsResponsesRun(s.Body)).Select(s => s.Body).ToList();

    private static AgentMessage User(string id, string text)
        => new(id, MessageRole.User, [new TextPart(text)], DateTimeOffset.UtcNow);

    private static async Task<List<ModelEvent>> Collect(AiProxyProvider p, ModelRequest r)
    {
        var list = new List<ModelEvent>();
        await foreach (var e in p.RunAsync(r, CancellationToken.None)) list.Add(e);
        return list;
    }

    private static (AiProxyProvider p, ScriptedHandler h) Provider(
        List<(HttpStatusCode, string)> queue, IEventBus? bus = null)
    {
        var h = new ScriptedHandler(queue);
        var p = new AiProxyProvider(new HttpClient(h), "http://test", new NullLogger(), "auto", bus);
        return (p, h);
    }

    [Fact]
    public async Task StaleChain_404ResponseNotFound_RetriesOnceWithReset()
    {
        var queue = new List<(HttpStatusCode, string)> { (HttpStatusCode.OK, RespText) };
        var (p, h) = Provider(queue);
        await p.RefreshAsync(CancellationToken.None);

        // Run 1 (reset, no head) establishes the chain head covering u1 + its reply.
        var r1 = new ModelRequest { ModelId = "m", SessionId = "s", Messages = [User("u1", "hi")] };
        var ev1 = await Collect(p, r1);
        Assert.Single(ev1.OfType<ModelCompleted>());
        var assistant1 = ev1.OfType<ModelCompleted>().Single().Message;

        // Run 2 adds a turn → it CHAINS from the head. Force the chained attempt to
        // 404 response_not_found, then a successful reset.
        queue.Clear();
        queue.Add((HttpStatusCode.NotFound, StaleBody));
        queue.Add((HttpStatusCode.OK, RespText));
        var r2 = new ModelRequest { ModelId = "m", SessionId = "s",
            Messages = [User("u1", "hi"), assistant1, User("u2", "again")] };
        var ev2 = await Collect(p, r2);

        // Recovered: the run still completes.
        Assert.Single(ev2.OfType<ModelCompleted>());
        // Run 2 made exactly ONE chained request (previous_response_id) and ONE reset.
        var bodies = RunBodies(h);
        Assert.Equal(3, bodies.Count); // run1 reset + run2 chained + run2 reset
        Assert.Equal(1, bodies.Count(b => b.Contains("\"previous_response_id\"", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Generic404_WithoutResponseNotFound_IsNotRetried()
    {
        var queue = new List<(HttpStatusCode, string)> { (HttpStatusCode.OK, RespText) };
        var (p, h) = Provider(queue);
        await p.RefreshAsync(CancellationToken.None);
        var r1 = new ModelRequest { ModelId = "m", SessionId = "s", Messages = [User("u1", "hi")] };
        var assistant1 = (await Collect(p, r1)).OfType<ModelCompleted>().Single().Message;

        // A chained request failing with a generic 404 (no response_not_found code)
        // is NOT a stale chain → no reset retry; the failure surfaces.
        queue.Clear();
        queue.Add((HttpStatusCode.NotFound, "an unrelated upstream 404"));
        var r2 = new ModelRequest { ModelId = "m", SessionId = "s",
            Messages = [User("u1", "hi"), assistant1, User("u2", "again")] };
        var ev2 = await Collect(p, r2);

        Assert.Single(ev2.OfType<ModelFailed>());
        // Run 2 hit /v1/responses exactly once (the chained attempt) — no reset retry.
        Assert.Single(RunBodies(h).Where(b => b.Contains("\"previous_response_id\"", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task InvalidToolHistory_IsNeverRetried()
    {
        var queue = new List<(HttpStatusCode, string)> { (HttpStatusCode.BadRequest, InvalidHistoryBody) };
        var (p, h) = Provider(queue);
        await p.RefreshAsync(CancellationToken.None);
        var r = new ModelRequest { ModelId = "m", SessionId = "s", Messages = [User("u1", "hi")] };
        var ev = await Collect(p, r);

        Assert.Single(ev.OfType<ModelFailed>());
        // One run request only: a malformed history is our input bug, not a cache miss.
        Assert.Single(RunBodies(h));
    }

    [Fact]
    public async Task FailureAfterContent_IsNeverRetried()
    {
        var queue = new List<(HttpStatusCode, string)> { (HttpStatusCode.OK, FailAfterContent) };
        var (p, h) = Provider(queue);
        await p.RefreshAsync(CancellationToken.None);
        var r = new ModelRequest { ModelId = "m", SessionId = "s", Messages = [User("u1", "hi")] };
        var ev = await Collect(p, r);

        Assert.Single(ev.OfType<ModelFailed>());
        // Content was seen, so the response_not_found failure is NOT healed with a retry.
        Assert.Single(RunBodies(h));
    }

    [Fact]
    public async Task Diagnostics_ReportTheTruthfulModeAndReason()
    {
        var bus = new RecordingBus();
        var queue = new List<(HttpStatusCode, string)> { (HttpStatusCode.OK, RespText), (HttpStatusCode.OK, RespText) };
        var (p, _) = Provider(queue, bus);
        await p.RefreshAsync(CancellationToken.None);

        // First run: no chain head → a RESET.
        var r1 = new ModelRequest { ModelId = "m", SessionId = "s", Messages = [User("u1", "hi")] };
        var ev1 = await Collect(p, r1);
        var assistant1 = ev1.OfType<ModelCompleted>().Single().Message;
        Assert.Equal("reset", bus.Captured[^1].Mode);
        Assert.Equal("no-chain-head", bus.Captured[^1].Reason);
        Assert.Equal("responses", bus.Captured[^1].WireServed);

        // Second run: the head covers the prefix → a CHAINED request.
        var r2 = new ModelRequest { ModelId = "m", SessionId = "s",
            Messages = [User("u1", "hi"), assistant1, User("u2", "again")] };
        await Collect(p, r2);
        Assert.Equal("chained", bus.Captured[^1].Mode);
        Assert.Equal("prefix-match", bus.Captured[^1].Reason);
        Assert.True(bus.Captured[^1].Chained);
    }
}
