using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using NetPI.Abstractions;
using NetPI.Provider.AiProxy;
using Xunit;

namespace NetPI.Host.Tests;

/// <summary>
/// Regression: the OpenAI wire (BOTH responses and chat completions) rejects
/// function names outside [A-Za-z0-9_-]{1,64} (HTTP 400), but netPI tools carry
/// namespace DOTS (agents.delegate, agents.spawn, ...). The provider is the wire
/// boundary, so it must sanitize OUT (dot -> dash) and restore IN (dash -> dot);
/// without it every real agent run 400s (observed live against AiProxy on the
/// pooled qwen3.8-27b model).
/// </summary>
public class AiProxyWireToolNameTests
{
    private const string Dotted = "agents.delegate";

    private static ModelRequest RequestWithTools(params string[] names)
    {
        var tools = names.Select(n => new ToolDefinition(
            n,
            "helper tool",
            JsonDocument.Parse("""{"type":"object","properties":{}}""").RootElement)).ToArray();
        return new ModelRequest
        {
            ModelId = "m",
            DeploymentId = "dep",
            Messages = [new AgentMessage("u1", MessageRole.User, [new TextPart("hi")], DateTimeOffset.UtcNow)],
            Tools = tools,
        };
    }

    // ---- helpers (sanitizers on the provider) --------------------------------
    [Theory]
    [InlineData("agents.delegate", "agents-delegate")]
    [InlineData("bash", "bash")]
    [InlineData("", "")]
    public void WireToolName_MapsDotsToDashesAndBack(string internalName, string wire)
    {
        Assert.Equal(wire, AiProxyProvider.WireToolName(internalName));
        Assert.Equal("agents.delegate", AiProxyProvider.InternalToolName("agents-delegate"));
        Assert.Equal(wire, AiProxyProvider.WireToolName(wire)); // already-safe is a no-op
    }

    // ---- request: dotted names never reach the wire bytes ---------------------
    [Fact]
    public async Task ChatWireRequest_SendsDashedNames()
    {
        var handler = new CapturingHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://test") };
        var provider = new AiProxyProvider(http, "http://test", new NullLogger(), wire: "chat");

        await foreach (var _ in provider.RunAsync(RequestWithTools(Dotted, "bash"), CancellationToken.None))
            break;

        Assert.Equal(1, handler.Requests);
        var body = handler.LastRequestBody!;
        Assert.Contains("agents-delegate", body);
        Assert.DoesNotContain("agents.delegate", body); // the wire bytes are spec-clean
    }

    // ---- response: the model sees the dotted internal name --------------------
    [Fact]
    public async Task ChatWireResponse_MapsNamesBackToInternal()
    {
        var handler = new CapturingHandler();
        handler.Body = """
            data: {"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"id":"call_1","type":"function","function":{"name":"agents-delegate","arguments":""}}]}}]}
            data: {"choices":[{"index":0,"delta":{},"finish_reason":"tool_calls"}]}
            data: [DONE]
            """;
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://test") };
        var provider = new AiProxyProvider(http, "http://test", new NullLogger(), wire: "chat");

        var events = new List<ModelEvent>();
        await foreach (var ev in provider.RunAsync(RequestWithTools(Dotted), CancellationToken.None))
            events.Add(ev);

        Assert.Equal(Dotted, events.OfType<ToolCallStarted>().First().Name);
        var part = events.OfType<ModelCompleted>().Single().Message.Parts.OfType<ToolCallPart>().Single();
        Assert.Equal(Dotted, part.Name);
    }

    private sealed class NullLogger : IPluginLogger
    {
        public void Debug(string m) { }
        public void Information(string m) { }
        public void Warning(string m) { }
        public void Error(string m, Exception? e = null) { }
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public int Requests { get; private set; }
        public string? Body { get; set; }
        public string? LastRequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++;
            if (request.Content is not null)
                LastRequestBody = await request.Content.ReadAsStringAsync(ct);
            if (Body is null)
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK);
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(Body, System.Text.Encoding.UTF8, "text/event-stream"),
            };
        }
    }
}
