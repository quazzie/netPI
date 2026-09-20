using System.Text.Json;

namespace NetPI.Abstractions;

/// <summary>
/// Generic agent event (PLAN §4, §45). Lifecycle/extensibility events flow
/// through the host event bus; high-volume text deltas use direct streaming instead.
/// </summary>
public sealed record AgentEvent(
    string EventId,
    AgentEventType Type,
    DateTimeOffset At,
    string? SessionId,
    JsonElement? Payload);

/// <summary>Known agent event kinds (extensible via plugin payloads).</summary>
public enum AgentEventType
{
    SessionOpened = 0,
    AgentStarting = 1,
    BeforeModelRequest = 2,
    ModelStreamEvent = 3,
    AssistantCompleted = 4,
    BeforeToolBatch = 5,
    BeforeToolCall = 6,
    AfterToolCall = 7,
    AfterToolBatch = 8,
    TurnBoundary = 9,
    ContextBuilding = 10,
    ContextBuilt = 11,
    ModelRequestFailed = 12,
    AgentCompleted = 13,
    AgentCancelled = 14,
    ModelRetrying = 15,
}

