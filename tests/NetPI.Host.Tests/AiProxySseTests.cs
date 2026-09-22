using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NetPI.Abstractions;
using NetPI.Provider.AiProxy;
using Xunit;

namespace NetPI.Host.Tests;

/// <summary>
/// PLAN §50: AiProxy provider tested by replaying captured OpenAI-compatible
/// SSE streams through a fake <see cref="HttpClient"/>. No network is touched.
/// </summary>
public class AiProxySseTests
{
    private sealed class NullLogger : IPluginLogger
    {
        public void Debug(string m) { }
        public void Information(string m) { }
        public void Warning(string m) { }
        public void Error(string m, Exception? e = null) { }
    }

    private static (AiProxyProvider provider, List<HttpMessage> sent) Make(string sseBody)
    {
        var sent = new List<HttpMessage>();
        var handler = new SseHandler(sseBody, sent);
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://test") };
        return (new AiProxyProvider(http, "http://test", new NullLogger()), sent);
    }

    private static ModelRequest Req() => new()
    {
        ModelId = "m",
        Messages = [new AgentMessage("u", MessageRole.User, [new TextPart("hi")], DateTimeOffset.UtcNow)],
    };

    private static async Task<List<ModelEvent>> Collect(AiProxyProvider p)
    {
        var list = new List<ModelEvent>();
        await foreach (var ev in p.RunAsync(Req(), CancellationToken.None))
            list.Add(ev);
        return list;
    }

    /// <summary>SSE fixture: text-only stream with a usage tail and [DONE].</summary>
    private const string TextOnly =
        "data: {\"id\":\"1\",\"choices\":[{\"delta\":{\"content\":\"Hel\"}}]}\n" +
        "data: {\"id\":\"1\",\"choices\":[{\"delta\":{\"content\":\"lo\"}}]}\n" +
        "data: {\"id\":\"1\",\"choices\":[{\"delta\":{\"content\":\"\"},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":5,\"completion_tokens\":3,\"total_tokens\":8}}\n" +
        "data: [DONE]\n";

    [Fact]
    public async Task TextOnly_StreamsTextAndCapturesUsage()
    {
        var (p, _) = Make(TextOnly);
        var ev = await Collect(p);

        Assert.Contains(ev, e => e is ModelStarted);
        Assert.Equal("Hello", string.Concat(ev.OfType<TextDelta>().Select(t => t.Text)));
        Assert.Contains(ev, e => e is TextCompleted);
        var u = Assert.Single(ev.OfType<UsageUpdated>());
        Assert.Equal(5, u.PromptTokens);
        Assert.Equal(3, u.CompletionTokens);
        Assert.Equal(8, u.TotalTokens);
        var done = Assert.Single(ev.OfType<ModelCompleted>());
        Assert.Equal("Hello", (done.Message.Parts.OfType<TextPart>().Single()).Text);
    }

    private const string Reasoning =
        "data: {\"choices\":[{\"delta\":{\"reasoning_content\":\"let me think\"}}]}\n" +
        "data: {\"choices\":[{\"delta\":{\"reasoning_content\":\"\"}}]}\n" +
        "data: {\"choices\":[{\"delta\":{\"content\":\"answer\"}}]}\n" +
        "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}],\"usage\":{\"total_tokens\":9}}\n" +
        "data: [DONE]\n";

    [Fact]
    public async Task Reasoning_EmitsThinkingLaneBeforeText()
    {
        var (p, _) = Make(Reasoning);
        var ev = await Collect(p);

        Assert.Contains(ev, e => e is ThinkingStarted);
        Assert.Equal("let me think", string.Concat(ev.OfType<ThinkingDelta>().Select(t => t.Text)));
        Assert.Contains(ev, e => e is ThinkingCompleted);
        Assert.Equal("answer", string.Concat(ev.OfType<TextDelta>().Select(t => t.Text)));

        // thinking must come before the first text delta (lane ordering).
        var thinkIdx = ev.ToList().FindIndex(e => e is ThinkingDelta);
        var textIdx = ev.ToList().FindIndex(e => e is TextDelta);
        Assert.True(thinkIdx < textIdx, "thinking should precede text");
    }

    private const string Interleaved =
        "data: {\"choices\":[{\"delta\":{\"reasoning_content\":\"think1\"}}]}\n" +
        "data: {\"choices\":[{\"delta\":{\"content\":\"a\"}}]}\n" +
        "data: {\"choices\":[{\"delta\":{\"reasoning_content\":\"think2\"}}]}\n" +
        "data: {\"choices\":[{\"delta\":{\"content\":\"b\"}}]}\n" +
        "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}],\"usage\":{\"total_tokens\":4}}\n" +
        "data: [DONE]\n";

    [Fact]
    public async Task Interleaved_ReasoningAndText_KeepsBothLanes()
    {
        var (p, _) = Make(Interleaved);
        var ev = await Collect(p);

        // The lane marker is emitted once; deltas from both lanes are retained
        // in arrival order.
        Assert.Equal("think1think2", string.Concat(ev.OfType<ThinkingDelta>().Select(t => t.Text)));
        Assert.Equal("ab", string.Concat(ev.OfType<TextDelta>().Select(t => t.Text)));
    }

    private const string OneToolCall =
        "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"c1\",\"function\":{\"name\":\"get_weather\"}}]}}]}\n" +
        "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":{\"arguments\":\"{\\\"city\\\":\"}}]}}]}\n" +
        "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":{\"arguments\":\"\\\"Paris\\\"}\"}}]}}]}\n" +
        "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"tool_calls\"}],\"usage\":{\"total_tokens\":7}}\n" +
        "data: [DONE]\n";

    [Fact]
    public async Task OneToolCall_FragmentsArgumentsAndReassembles()
    {
        var (p, _) = Make(OneToolCall);
        var ev = await Collect(p);

        Assert.Contains(ev, e => e is ToolCallStarted { Name: "get_weather" });
        // Argument deltas are reassembled into a complete JSON object.
        var done = Assert.Single(ev.OfType<ModelCompleted>());
        var tc = done.Message.Parts.OfType<ToolCallPart>().Single();
        Assert.Equal("get_weather", tc.Name);
        Assert.True(tc.Arguments.TryGetProperty("city", out _), "args should contain 'city'");
        Assert.Equal("Paris", tc.Arguments.GetProperty("city").GetString());
    }

    private const string ParallelToolCalls =
        "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"a\",\"function\":{\"name\":\"t0\"}},{\"index\":1,\"id\":\"b\",\"function\":{\"name\":\"t1\"}}]}}]}\n" +
        "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":{\"arguments\":\"{}\"}},{\"index\":1,\"function\":{\"arguments\":\"{}\"}}]}}]}\n" +
        "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"tool_calls\"}],\"usage\":{\"total_tokens\":9}}\n" +
        "data: [DONE]\n";

    [Fact]
    public async Task ParallelToolCalls_YieldsDistinctIds()
    {
        var (p, _) = Make(ParallelToolCalls);
        var ev = await Collect(p);

        var done = Assert.Single(ev.OfType<ModelCompleted>());
        var calls = done.Message.Parts.OfType<ToolCallPart>().ToList();
        Assert.Equal(2, calls.Count);
        Assert.Equal(2, calls.Select(c => c.Id).Distinct().Count());
    }

    /// <summary>
    /// plan §3C: the chat wire's completed message must carry tool calls in the
    /// wire's numeric index order, NOT raw SSE arrival order. Here index 1's
    /// events stream before index 0's — the assembled calls must still be [0,1].
    /// </summary>
    private const string ReorderedIndices =
        "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":1,\"id\":\"b\",\"function\":{\"name\":\"t1\",\"arguments\":\"{}\"}}]}}]}\n" +
        "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"a\",\"function\":{\"name\":\"t0\",\"arguments\":\"{}\"}}]}}]}\n" +
        "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"tool_calls\"}],\"usage\":{\"total_tokens\":9}}\n" +
        "data: [DONE]\n";

    [Fact]
    public async Task ReorderedIndices_AssembleInIndexOrderNotArrivalOrder()
    {
        var (p, _) = Make(ReorderedIndices);
        var ev = await Collect(p);

        var done = Assert.Single(ev.OfType<ModelCompleted>());
        var ids = done.Message.Parts.OfType<ToolCallPart>().Select(c => c.Id).ToList();
        // index 1 arrived first, but the completed message is ordered by index.
        Assert.Equal(new[] { "a", "b" }, ids);
    }

    [Fact]
    public async Task DisconnectMidStream_EmitsWhatArrivedThenCompletes()
    {
        // Stream ends WITHOUT a usage tail or [DONE]; provider must still
        // close out the lanes and emit a defaulted UsageUpdated.
        var body =
            "data: {\"choices\":[{\"delta\":{\"content\":\"partial\"}}]}\n" +
            "data: {\"choices\":[{\"delta\":{\"content\":\" text\"}}]}\n";
        var (p, _) = Make(body);
        var ev = await Collect(p);

        Assert.Equal("partial text", string.Concat(ev.OfType<TextDelta>().Select(t => t.Text)));
        var u = ev.OfType<UsageUpdated>().Last();
        Assert.Equal(0, u.TotalTokens); // defaulted, none was streamed
        Assert.Contains(ev, e => e is ModelCompleted);
    }

    [Fact]
    public async Task NonSuccessHttp_YieldsModelFailed()
    {
        var handler = new SseHandler("boom", new(), status: HttpStatusCode.BadGateway);
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://test") };
        var p = new AiProxyProvider(http, "http://test", new NullLogger());
        var ev = await Collect(p);

        Assert.Contains(ev, e => e is ModelFailed);
        Assert.DoesNotContain(ev, e => e is ModelCompleted);
    }

    [Fact]
    public async Task Catalog_ParsesNestedReasoningProfilesAndContext()
    {
        const string catalog = """
            {
              "data": [{
                "id": "qwen3.8-27b",
                "context_window": 262144,
                "reasoning": {
                  "supported": true,
                  "profiles": {
                    "low": { "budget": 2048 },
                    "medium": { "budget": 8192 },
                    "high": { "budget": 16384 }
                  },
                  "default_effort": "medium"
                }
              }]
            }
            """;
        var handler = new SseHandler(catalog, new());
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://test") };
        var p = new AiProxyProvider(http, "http://test", new NullLogger(), wire: "chat");

        var models = await p.RefreshAsync(CancellationToken.None);
        var model = Assert.Single(models);

        Assert.Equal(262144, model.ContextWindowTokens);
        Assert.Equal(new[] { "low", "medium", "high" }, model.ReasoningLevels!);
        Assert.Equal("medium", model.DefaultReasoningLevel);
        Assert.True(model.SupportsThinking);
    }

    [Fact]
    public async Task Catalog_ParsesReasoningEffortArrayFromUnknownWrapperName()
    {
        const string catalog = """
            {
              "data": [{
                "id": "reasoner",
                "reasoning": {
                  "capabilities": {
                    "effort_levels": ["minimal", "low", "medium", "high"]
                  }
                }
              }]
            }
            """;
        var handler = new SseHandler(catalog, new());
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://test") };
        var p = new AiProxyProvider(http, "http://test", new NullLogger(), wire: "chat");

        var model = Assert.Single(await p.RefreshAsync(CancellationToken.None));

        Assert.Equal(new[] { "minimal", "low", "medium", "high" }, model.ReasoningLevels!);
    }

    // ---- fakes ---------------------------------------------------------

    private sealed record HttpMessage(string url, string body);

    private sealed class SseHandler : HttpMessageHandler
    {
        private readonly string _body;
        private readonly List<HttpMessage> _sent;
        private readonly HttpStatusCode _status;

        public SseHandler(string body, List<HttpMessage> sent, HttpStatusCode status = HttpStatusCode.OK)
        {
            _body = body;
            _sent = sent;
            _status = status;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            _sent.Add(new HttpMessage(request.RequestUri!.ToString(),
                request.Content is null ? "" : Encoding.UTF8.GetString(request.Content.ReadAsByteArrayAsync().Result)));
            var resp = new HttpResponseMessage(_status);
            resp.Content = new StringContent(_body, Encoding.UTF8, "text/event-stream");
            return Task.FromResult(resp);
        }
    }
}
