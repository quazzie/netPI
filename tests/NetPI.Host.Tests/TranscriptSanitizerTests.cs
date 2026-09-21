using System;
using System.Linq;
using System.Text.Json;
using NetPI.Abstractions;
using Xunit;

namespace NetPI.Host.Tests;

/// <summary>
/// PLAN §46: a run that died mid-batch (host crash/restart) leaves assistant
/// tool calls without results — the provider rejects such transcripts with
/// 400 ("function_call_output must contain a non-empty call_id"). The
/// sanitizer must repair them (synthetic interrupted result, idempotent) and
/// leave healthy transcripts untouched.
/// </summary>
public class TranscriptSanitizerTests
{
    private static AgentMessage Assist(string id, params ToolCallPart[] calls) =>
        new(id, MessageRole.Assistant, calls, DateTimeOffset.UtcNow);

    private static AgentMessage User(string id, string text) =>
        new(id, MessageRole.User, [new TextPart(text)], DateTimeOffset.UtcNow);

    private static AgentMessage ToolResult(string callId, string name, string text, bool isError = false) =>
        new(callId + "-result", MessageRole.Tool,
            [new ToolResultPart(callId, name, [new TextPart(text)], isError)], DateTimeOffset.UtcNow);

    private static ToolCallPart Call(string id, string name) =>
        new(id, name, JsonSerializer.SerializeToElement(new { }));

    [Fact]
    public void DanglingCall_GetsSyntheticInterruptedResult()
    {
        // user → assistant(tool call, no result): the host died before the tool ran.
        var msgs = new List<AgentMessage>
        {
            User("u1", "build it"),
            Assist("a1", Call("call_1", "bash")),
        };

        var clean = TranscriptSanitizer.Sanitize(msgs);

        // the transcript grew by exactly one tool message, directly after the
        // assistant turn (adjacency matters to the provider wires).
        Assert.Equal(3, clean.Count);
        var synth = clean[2];
        Assert.Equal(MessageRole.Tool, synth.Role);
        var part = Assert.IsType<ToolResultPart>(synth.Parts.Single());
        Assert.Equal("call_1", part.ToolCallId);
        Assert.True(part.IsError);
        Assert.Equal(TranscriptSanitizer.InterruptedResultText, part.Parts.OfType<TextPart>().Single().Text);
    }

    [Fact]
    public void ResolvedCalls_AreLeftAlone()
    {
        var msgs = new List<AgentMessage>
        {
            User("u1", "build it"),
            Assist("a1", Call("call_1", "bash")),
            ToolResult("call_1", "bash", "ok"),
        };

        var clean = TranscriptSanitizer.Sanitize(msgs);

        // no change → same list instance, same count
        Assert.Same(msgs, clean);
        Assert.Equal(3, clean.Count);
    }

    [Fact]
    public void MixedBatch_OnlyTheDanglingCallIsPatched()
    {
        var msgs = new List<AgentMessage>
        {
            User("u1", "do both"),
            Assist("a1", Call("call_a", "bash"), Call("call_b", "grep")),
            ToolResult("call_a", "bash", "done"),   // call_b was never executed
        };

        var clean = TranscriptSanitizer.Sanitize(msgs);

        // The synthetic result is inserted right after the assistant turn (the
        // wires expect each tool message to follow its call), ahead of the
        // surviving real result of the other call.
        Assert.Equal(4, clean.Count);
        var synth = clean[2];
        Assert.Equal(MessageRole.Tool, synth.Role);
        Assert.Single(synth.Parts.OfType<ToolResultPart>());
        var part = synth.Parts.OfType<ToolResultPart>().Single();
        Assert.Equal("call_b", part.ToolCallId);
        Assert.True(part.IsError);
    }

    [Fact]
    public void MultipleDanglingCalls_AllPatchedInOneMessage()
    {
        var msgs = new List<AgentMessage>
        {
            Assist("a1", Call("x", "bash"), Call("y", "grep")),
        };

        var clean = TranscriptSanitizer.Sanitize(msgs);

        Assert.Equal(2, clean.Count);
        var parts = clean[1].Parts.OfType<ToolResultPart>().ToList();
        Assert.Equal(2, parts.Count);
        Assert.Contains(parts, p => p.ToolCallId == "x");
        Assert.Contains(parts, p => p.ToolCallId == "y");
    }

    [Fact]
    public void Sanitize_IsIdempotent()
    {
        var msgs = new List<AgentMessage>
        {
            Assist("a1", Call("call_1", "bash")),
        };

        var once = TranscriptSanitizer.Sanitize(msgs).ToList();
        var twice = TranscriptSanitizer.Sanitize(once);

        Assert.Equal(2, once.Count);
        Assert.Same(once, twice); // second pass sees the synthetic result → no change
    }

    [Fact]
    public void Empty_Transcript_ReturnsUnchanged()
    {
        var msgs = new List<AgentMessage>();
        Assert.Same(msgs, TranscriptSanitizer.Sanitize(msgs));
    }
}
