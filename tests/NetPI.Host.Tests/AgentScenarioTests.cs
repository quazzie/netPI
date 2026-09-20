using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NetPI.Abstractions;
using NetPI.Agent;
using Xunit;

namespace NetPI.Host.Tests;

/// <summary>
/// PLAN §50: agent scenarios that are the ones likely to fail — parallel tool
/// calls, provider exception, tool exception, and the retry path (PLAN §34).
/// Reuses the FakeProvider / NoopPluginContext / TestRegistry doubles defined
/// in AgentRuntimeTests.cs.
/// </summary>
public class AgentScenarioTests
{
    private static AgentRuntime MakeRuntime(IModelProvider provider, IToolRegistry? tools = null,
        IModelRetryPolicy? retry = null)
    {
        var ctx = new NoopPluginContext();
        ctx.Add("provider", provider);
        if (tools is not null) ctx.Add("tools", tools);
        if (retry is not null) ctx.Add("retry", retry);
        return new AgentRuntime(ctx);
    }


    private static AgentRunOptions Options(string text) => new()
    {
        SessionId = "s1",
        ModelId = "model",
        Messages = [new AgentMessage("u1", MessageRole.User, [new TextPart(text)], DateTimeOffset.UtcNow)],
    };

    private static AgentMessage Assistant(params MessagePart[] parts)
        => new("a1", MessageRole.Assistant, parts, DateTimeOffset.UtcNow);

    private sealed class RetryPolicy : IModelRetryPolicy
    {
        private readonly int _max;
        public RetryPolicy(int max) => _max = max;
        public bool IsEnabled => true;
        public RetryDecision Decide(int attempt, string error)
            => attempt < _max
                ? new RetryDecision(true, attempt, _max, 1)
                : RetryDecision.Abort;
    }

    private sealed class ThrowingProvider : IModelProvider
    {
        public async IAsyncEnumerable<ModelEvent> RunAsync(ModelRequest request, CancellationToken ct)
        {
            throw new InvalidOperationException("connection refused");
            yield break; // unreachable
        }
    }

    private sealed class FlakyProvider : IModelProvider
    {
        private readonly Queue<IReadOnlyList<ModelEvent>> _turns;
        public FlakyProvider(params IReadOnlyList<ModelEvent>[] turns) => _turns = new(turns);
        public async IAsyncEnumerable<ModelEvent> RunAsync(ModelRequest request, CancellationToken ct)
        {
            var ev = _turns.Count > 0 ? _turns.Dequeue() : null;
            if (ev is null) { await Task.Yield(); yield break; }
            foreach (var e in ev) { await Task.Yield(); yield return e; }
        }
    }

    [Fact]
    public async Task ParallelToolCalls_AllExecuteInOneBatch()
    {
        var tool = new EchoTool();
        var registry = new TestRegistry(tool);
        var provider = new FakeProvider(
            // one assistant message with TWO tool calls -> parallel batch
            [new ModelStarted("model"), new ModelCompleted(Assistant(
                new ToolCallPart("t1", "echo", JsonSerializer.SerializeToElement(new { msg = "one" })),
                new ToolCallPart("t2", "echo", JsonSerializer.SerializeToElement(new { msg = "two" }))))],
            [new ModelCompleted(Assistant(new TextPart("both done")))]);
        var rt = MakeRuntime(provider, registry);

        var result = await rt.RunAsync(Options("two echoes"), CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal(2, result.Turns);
        Assert.True(tool.Saw("echo", "one"));
        Assert.True(tool.Saw("echo", "two"));
    }

    [Fact]
    public async Task ProviderException_NoRetry_ThrowsWithProviderMessage()
    {
        var rt = MakeRuntime(new ThrowingProvider());
        var ex = await Record.ExceptionAsync(async () => await rt.RunAsync(Options("hi"), CancellationToken.None));
        var ioe = Assert.IsType<Exception>(ex);
        Assert.Contains("connection refused", ioe.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProviderException_RetriesThenSucceeds()
    {
        // First attempt yields a failure, second succeeds. With a 2-attempt
        // policy the run must recover (PLAN §34).
        var provider = new FlakyProvider(
            [new ModelStarted("model"), new ModelFailed("model", "connection refused")],
            [new ModelStarted("model"), new ModelCompleted(Assistant(new TextPart("ok")))]);
        var rt = MakeRuntime(provider, retry: new RetryPolicy(3));

        var result = await rt.RunAsync(Options("hi"), CancellationToken.None);

        Assert.True(result.Ok, $"expected recovery, got note: {result.Note}");
        Assert.Equal("ok", result.FinalAssistant!.Parts.OfType<NetPI.Abstractions.TextPart>().Single().Text);
    }

    [Fact]
    public async Task ToolException_BecomesErrorResultNotFailure()
    {
        var throwing = new ThrowingTool();
        var registry = new TestRegistry(throwing);
        var provider = new FakeProvider(
            [new ModelStarted("model"), new ModelCompleted(Assistant(
                new ToolCallPart("t1", "boom", JsonSerializer.SerializeToElement(new { }))))],
            [new ModelCompleted(Assistant(new TextPart("survived")))]);
        var rt = MakeRuntime(provider, registry);

        var result = await rt.RunAsync(Options("boom"), CancellationToken.None);

        // The tool error is surfaced to the model as an error result, but the
        // run itself completes (it did not throw).
        Assert.True(result.Ok);
        Assert.Equal(2, result.Turns);
        // The second model call should have seen a tool result marked as error.
        var last = provider.LastMessages;
        var toolMsg = last!.Last(m => m.Role == MessageRole.Tool);
        var part = toolMsg.Parts.OfType<ToolResultPart>().Single();
        Assert.True(part.IsError);
    }

    // ---- doubles ---------------------------------------------------------

    private sealed class ThrowingTool : IAgentTool
    {
        public string Name => "boom";
        public string Description => "always throws";
        public IReadOnlyList<string> Guidelines => [];
        public JsonElement Parameters => JsonSerializer.SerializeToElement(new { type = "object" });
        public ValueTask<ToolResult> ExecuteAsync(ToolContext context, CancellationToken ct)
            => throw new InvalidOperationException("tool blew up");
    }
}
