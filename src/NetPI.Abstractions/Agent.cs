namespace NetPI.Abstractions;

/// <summary>
/// State of the agent (PLAN §44). Small machine for UI status, reload gating,
/// shutdown, and diagnostics — not a workflow engine.
/// </summary>
public enum AgentState
{
    Idle = 0,
    /// <summary>Run starting: context built, first model request pending.</summary>
    Preparing = 1,
    /// <summary>A model request is in flight (streaming).</summary>
    CallingModel = 2,
    /// <summary>Tool calls from the latest assistant message are executing.</summary>
    ExecutingTools = 3,
    /// <summary>Context compaction is in progress.</summary>
    Compacting = 4,
    /// <summary>Model request failed; backing off before the retry.</summary>
    Retrying = 5,
    /// <summary>Cancellation was requested; the run is winding down.</summary>
    Cancelling = 6,
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
