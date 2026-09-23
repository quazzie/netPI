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

        var clean = TranscriptSanitizer.Normalize(msgs).Messages;

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

        var clean = TranscriptSanitizer.Normalize(msgs).Messages;

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

        var clean = TranscriptSanitizer.Normalize(msgs).Messages;

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

        var clean = TranscriptSanitizer.Normalize(msgs).Messages;

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

        var once = TranscriptSanitizer.Normalize(msgs).Messages.ToList();
        var twice = TranscriptSanitizer.Normalize(once);

        Assert.Equal(2, once.Count);
        Assert.Same(once, twice.Messages); // second pass sees the synthetic result → no change
    }

    [Fact]
    public void Empty_Transcript_ReturnsUnchanged()
    {
        var msgs = new List<AgentMessage>();
        Assert.Same(msgs, TranscriptSanitizer.Normalize(msgs).Messages);
    }

    [Fact]
    public void CleanTranscript_ProducesNoRepairNoise()
    {
        var msgs = new List<AgentMessage>
        {
            User("u1", "go"),
            Assist("a1", Call("call_1", "bash")),
            ToolResult("call_1", "bash", "ok"),
        };

        var r = TranscriptSanitizer.Normalize(msgs);

        Assert.False(r.Changed);
        Assert.False(r.Report.AnyRepairs);
        Assert.Null(r.Report.ValidationFailure);
    }

    [Fact]
    public void DuplicateResult_IsRemovedOnce_AndPreservedAsANote()
    {
        var msgs = new List<AgentMessage>
        {
            Assist("a1", Call("call_1", "bash")),
            ToolResult("call_1", "bash", "ok"),
            ToolResult("call_1", "bash", "ok-again"), // same id, already consumed
        };

        var r = TranscriptSanitizer.Normalize(msgs);

        Assert.Equal(1, r.Report.DuplicateResults);
        // The duplicate is NOT replayed as a structured result: exactly one
        // result part for call_1 remains, plus a labeled user note (not a tool msg).
        var toolMsgs = r.Messages.Where(m => m.Role == MessageRole.Tool).ToList();
        Assert.Single(toolMsgs);
        Assert.Equal("call_1", Assert.IsType<ToolResultPart>(toolMsgs[0].Parts.Single()).ToolCallId);
        var note = Assert.Single(r.Messages.Where(m =>
            m.Parts.OfType<TextPart>().Any(t => t.Text.StartsWith(TranscriptSanitizer.RecoveryNotePrefix))));
        Assert.Equal(MessageRole.User, note.Role);
    }

    [Fact]
    public void OrphanResult_CalledNowhere_IsRemoved_AndPreservedAsANote()
    {
        var msgs = new List<AgentMessage>
        {
            Assist("a1", Call("call_1", "bash")),
            ToolResult("call_1", "bash", "ok"),
            User("u2", "later"),
            ToolResult("call_9", "bash", "stray"), // its call was never issued
        };

        var r = TranscriptSanitizer.Normalize(msgs);

        Assert.Equal(1, r.Report.OrphanResults);
        Assert.DoesNotContain(r.Messages, m =>
            m.Role == MessageRole.Tool && m.Parts.OfType<ToolResultPart>().Any(p => p.ToolCallId == "call_9"));
        Assert.Contains(r.Messages, m =>
            m.Parts.OfType<TextPart>().Any(t => t.Text.StartsWith(TranscriptSanitizer.RecoveryNotePrefix)));
    }

    [Fact]
    public void MisplacedResult_CallFromAnotherExchange_IsRemoved()
    {
        var msgs = new List<AgentMessage>
        {
            Assist("a1", Call("call_1", "bash")),
            ToolResult("call_1", "bash", "ok"),
            User("u2", "next turn"),            // closes the exchange
            ToolResult("call_1", "bash", "late"), // call belongs to the earlier exchange
        };

        var r = TranscriptSanitizer.Normalize(msgs);

        Assert.Equal(1, r.Report.MisplacedResults);
        Assert.DoesNotContain(r.Messages, m =>
            m.Role == MessageRole.Tool && m.Parts.OfType<ToolResultPart>().Any(p => p.ToolCallId == "call_1"
                && p.Parts.OfType<TextPart>().Any(t => t.Text == "late")));
    }

    [Fact]
    public void EmptyCallId_IsAnActionableValidationFailure_NotARepair()
    {
        var msgs = new List<AgentMessage>
        {
            Assist("a1", Call("", "bash")),
        };

        var r = TranscriptSanitizer.Normalize(msgs);

        Assert.NotNull(r.Report.ValidationFailure);
    }

    [Fact]
    public void Repairs_AreIdempotent_IncludingNotes()
    {
        var msgs = new List<AgentMessage>
        {
            Assist("a1", Call("call_1", "bash")),
            ToolResult("call_9", "bash", "stray"), // orphan → note
        };

        var once = TranscriptSanitizer.Normalize(msgs);
        var twice = TranscriptSanitizer.Normalize(once.Messages);

        // Second pass: the note is a plain user message (no structured result), so
        // nothing new is removed and the output is identical.
        Assert.Equal(once.Messages.Count, twice.Messages.Count);
        Assert.Equal(once.Messages.Select(m => (m.Id, m.Role, m.Parts.Count)).ToList(),
                     twice.Messages.Select(m => (m.Id, m.Role, m.Parts.Count)).ToList());
        Assert.False(twice.Changed);
    }
}
