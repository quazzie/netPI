using System.Text.Json;
using NetPI.Activity;
using Xunit;

namespace NetPI.Host.Tests;

/// <summary>
/// astra-2 §12.3: the panel→shell navigation bridge envelope validation
/// (ActivityBridge.Validate). The bridge accepts the versioned generic envelope
/// (netpi.panel.openSession, version 1) and the legacy Activity envelope
/// (netpi.activity.openSession, deprecated) — with identical SHAPE rules:
/// a bounded, well-formed sessionId; a wrong version, a wrong type, a missing
/// payload, an overlong id, or a whitespace/control-laden id is rejected.
///
/// (The frame/origin/source checks live in the Svelte shell —
/// App.svelte + RightPanel.svelte + panel-bridge.ts; this class is the single
/// canonical SHAPE parse shared by the server-side consumer and the tests.)
/// </summary>
public class WorkPanelBridgeTests
{
    private static JsonElement Parse(string json) =>
        JsonDocument.Parse(json).RootElement.Clone();

    private static string? Validate(string json) =>
        ActivityBridge.Validate(Parse(json));

    [Fact]
    public void CurrentEnvelope_IsAccepted()
    {
        var sid = Validate("""
            {"type":"netpi.panel.openSession","version":1,"payload":{"sessionId":"sess-abc123"}}
            """);
        Assert.Equal("sess-abc123", sid);
    }

    [Fact]
    public void LegacyEnvelope_IsAcceptedWithoutVersion()
    {
        var sid = Validate("""
            {"type":"netpi.activity.openSession","payload":{"sessionId":"sess-legacy"}}
            """);
        Assert.Equal("sess-legacy", sid);
    }

    [Fact]
    public void UnknownType_IsRejected()
    {
        Assert.Null(Validate("""
            {"type":"netpi.other.openSession","version":1,"payload":{"sessionId":"s"}}
            """));
    }

    [Fact]
    public void WrongVersion_IsRejected()
    {
        Assert.Null(Validate("""
            {"type":"netpi.panel.openSession","version":2,"payload":{"sessionId":"s"}}
            """));
    }

    [Fact]
    public void MissingVersion_OnCurrentType_IsRejected()
    {
        Assert.Null(Validate("""
            {"type":"netpi.panel.openSession","payload":{"sessionId":"s"}}
            """));
    }

    [Fact]
    public void MissingPayload_IsRejected()
    {
        Assert.Null(Validate("""
            {"type":"netpi.panel.openSession","version":1}
            """));
    }

    [Fact]
    public void NonStringSessionId_IsRejected()
    {
        Assert.Null(Validate("""
            {"type":"netpi.panel.openSession","version":1,"payload":{"sessionId":42}}
            """));
    }

    [Fact]
    public void EmptySessionId_IsRejected()
    {
        Assert.Null(Validate("""
            {"type":"netpi.panel.openSession","version":1,"payload":{"sessionId":""}}
            """));
    }

    [Fact]
    public void OverlongSessionId_IsRejected()
    {
        var sid = new string('a', ActivityBridge.MaxSessionIdLength + 1);
        Assert.Null(Validate(
            JsonSerializer.Serialize(new { type = ActivityBridge.OpenSessionType, version = 1, payload = new { sessionId = sid } })));
    }

    [Fact]
    public void MaxLengthSessionId_IsAccepted()
    {
        var sid = new string('a', ActivityBridge.MaxSessionIdLength);
        Assert.Equal(sid, Validate(
            JsonSerializer.Serialize(new { type = ActivityBridge.OpenSessionType, version = 1, payload = new { sessionId = sid } })));
    }

    [Fact]
    public void WhitespaceOrControlSessionId_IsRejected()
    {
        Assert.Null(Validate("""
            {"type":"netpi.panel.openSession","version":1,"payload":{"sessionId":"has space"}}
            """));
        Assert.Null(Validate("""
            {"type":"netpi.activity.openSession","payload":{"sessionId":"has\ttab"}}
            """));
    }

    [Fact]
    public void NonObjectEnvelope_IsRejected()
    {
        Assert.Null(ActivityBridge.Validate(Parse("[1,2,3]")));
        Assert.Null(ActivityBridge.Validate(Parse("\"netpi.panel.openSession\"")));
    }
}
