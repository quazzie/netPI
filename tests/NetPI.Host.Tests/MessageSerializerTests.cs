using System;
using System.Linq;
using NetPI.Abstractions;
using NetPI.Storage.Sqlite;
using Xunit;

namespace NetPI.Host.Tests;

/// <summary>
/// Regression for the responses-wire 400 "function_call_output must contain a
/// non-empty call_id": ToolResultPart was serialized with positional PartDto
/// arguments that landed ToolCallId/ToolName in the signature/mimeType fields,
/// so every persisted tool-result round-tripped with an empty ToolCallId.
/// </summary>
public class MessageSerializerTests
{
    [Fact]
    public void ToolResult_RoundTrips_ToolCallId()
    {
        var msg = new AgentMessage("m1", MessageRole.Tool,
            [
                new ToolResultPart("call_abc", "bash", [new TextPart("ok")]),
            ], DateTimeOffset.UtcNow);

        var back = MessageSerializer.Deserialize(MessageSerializer.Serialize(msg));

        var part = back.Parts.OfType<ToolResultPart>().Single();
        Assert.Equal("call_abc", part.ToolCallId);
        Assert.Equal("bash", part.ToolName);
        Assert.Equal("ok", part.Parts.OfType<TextPart>().Single().Text);
    }

    [Fact]
    public void MixedToolBatch_RoundTrips_AllCallIds()
    {
        var msg = new AgentMessage("m1", MessageRole.Tool,
            [
                new ToolResultPart("call_a", "bash", [new TextPart("x")]),
                new ToolResultPart("call_b", "grep", [new TextPart("y")], IsError: true),
            ], DateTimeOffset.UtcNow);

        var back = MessageSerializer.Deserialize(MessageSerializer.Serialize(msg));
        var parts = back.Parts.OfType<ToolResultPart>().ToList();

        Assert.Equal(2, parts.Count);
        Assert.All(parts, p => Assert.False(string.IsNullOrEmpty(p.ToolCallId)));
        Assert.Contains(parts, p => p.ToolCallId == "call_a" && p.ToolName == "bash");
        Assert.Contains(parts, p => p.ToolCallId == "call_b" && p.ToolName == "grep" && p.IsError);
    }
}
