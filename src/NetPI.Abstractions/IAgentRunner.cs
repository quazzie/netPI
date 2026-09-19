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
public sealed record AgentRunStart(string? SessionId, string? Note);

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

    /// <summary>True while a run is executing.</summary>
    bool IsRunning { get; }
}
