namespace NetPI.Abstractions;

/// <summary>State of the agent (PLAN §44). Minimal stub for Phase 1; the agent plugin owns the full state machine.</summary>
public enum AgentState
{
    Idle = 0,
    /// <summary>A run is in progress (model call, tool execution, or between them).</summary>
    Running = 1,
    /// <summary>Cancellation was requested; run is winding down.</summary>
    Cancelling = 2,
}

/// <summary>Read-only view of the agent's current state (PLAN §44).</summary>
public interface IAgentState
{
    AgentState State { get; }
    /// <summary>True while an agent run is executing; used by the "AgentIdle" reload policy (PLAN §6).</summary>
    bool IsRunning { get; }
    /// <summary>Current session, if any.</summary>
    string? ActiveSessionId { get; }
    /// <summary>True once the agent has finished at least one run (gate opens for reloads).</summary>
    bool IsIdle { get; }
}

/// <summary>
/// The agent runtime (PLAN §10). Phase 1 ships only the contract stub;
/// the agent plugin implements it in a later phase.
/// </summary>
public interface IAgentRuntime
{
    IAgentState State { get; }
}
