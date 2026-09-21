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
    /// <summary>
    /// astra-1 A (run cleanup): a run that FAILED (an unhandled exception escaped
    /// the model loop) — distinct from a clean completion or a cancellation. Every
    /// accepted run emits exactly one terminal event: AgentCompleted, AgentFailed
    /// or AgentCancelled.
    /// </summary>
    AgentFailed = 18,
    /// <summary>
    /// A model turn completed with no assistant text and no tool calls (e.g. a
    /// provider-truncated thinking stream). Nudge plugins use this to steer the
    /// run into a continuation; the web surface renders it as a chat notice.
    /// </summary>
    TurnEmpty = 16,
    /// <summary>
    /// A nudge plugin auto-continued the run after a <see cref="TurnEmpty"/>
    /// (the payload carries the nudge text). Published by the nudge plugin.
    /// </summary>
    Nudged = 17,
}

