namespace NetPI.Abstractions;

/// <summary>
/// Request to start one agent run (PLAN §10, §41). Transport-neutral: the Web
/// plugin builds this from a chat.send command; the agent plugin executes it.
/// </summary>
public sealed record AgentRunRequest(
    string? SessionId,
    string? WorkspacePath,
    string? ModelId,
    string Text,
    string? ReasoningLevel = null,
    float? Temperature = null,
    IReadOnlyList<AgentMessage>? PriorMessages = null)
{
    public override string ToString() => $"run[{SessionId ?? "new"}] {Text.Length} chars";
}

/// <summary>Outcome of starting a run (the run itself streams via the event bus).</summary>
public sealed record AgentRunStart(string? SessionId, string? Note, string? RunId = null);

/// <summary>
/// Starts and cancels agent runs. Implemented by the agent plugin; resolved by
/// the Web plugin through the service registry (PLAN §10, §36). Lives in
/// Abstractions so Web and Agent never reference each other's ALC (reload-safe).
/// </summary>
public interface IAgentRunner
{
    /// <summary>Kick off a run (background); returns once it has started.</summary>
    ValueTask<AgentRunStart> StartRunAsync(AgentRunRequest request, CancellationToken cancellationToken = default);

    /// <summary>Cancel the in-flight run, if any.</summary>
    ValueTask CancelRunAsync(CancellationToken cancellationToken = default);

    /// <summary>True while ANY run is executing — aggregate (PLAN §10, Package E). Includes
    /// preparation and cleanup so the host's idle reload gate stays simple.</summary>
    bool IsRunning { get; }

    /// <summary>astra-1 E: all runs the runner owns (active + finished, newest first).</summary>
    IReadOnlyList<RunInfo> ListRuns();

    /// <summary>astra-1 E: a specific run by its id (null when unknown).</summary>
    RunInfo? GetRun(string runId);

    /// <summary>astra-1 E: the ACTIVE run for a session (null when none).</summary>
    RunInfo? GetSessionRun(string sessionId);

    /// <summary>astra-1 E: cancel one specific run. True when a live run was signalled.</summary>
    bool CancelRun(string runId);
}

/// <summary>Terminal / in-flight state of a single run (Package E).</summary>
public enum RunState
{
    /// <summary>Preparation, model call, or tool execution still in flight.</summary>
    Running = 0,
    /// <summary>The run finished cleanly (assistant turn closed, no error).</summary>
    Completed = 1,
    /// <summary>The run was cancelled by an explicit cancel before it finished.</summary>
    Cancelled = 2,
    /// <summary>The run escaped the model loop with an unhandled exception.</summary>
    Failed = 3,
}

/// <summary>Read-only summary of one run (Package E — the run-query contract).</summary>
public sealed record RunInfo(
    string RunId,
    string? SessionId,
    string? ModelId,
    AgentState State,
    DateTimeOffset StartTime,
    DateTimeOffset? EndTime,
    RunState Outcome)
{
}
