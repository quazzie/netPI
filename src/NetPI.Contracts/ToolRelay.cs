namespace NetPI;

/// <summary>
/// Runs a NetPI tool for a model call a provider is still streaming. A provider whose model works in a loop of its own
/// (the Claude CLI) can offer the run's NetPI tools to it (over MCP), and each call it makes comes back here: the runtime
/// prepares and runs it like one of its own — the before-call hooks (guardrails and their approvals, plan mode, the
/// workspace guard), the tool with the session's current selection, its live output under <see cref="ToolCallPart.Id"/> —
/// while the provider's stream reports the call (tool.start/tool.end, the after-call hooks, the transcript), as for every
/// call a provider runs. Registered by the runtime; a provider asks <c>Services.Get&lt;IToolRelay&gt;()</c> (idea-0qo9xb).
/// </summary>
public interface IToolRelay
{
    /// <summary>
    /// Run <paramref name="call"/> in the model call <paramref name="sessionId"/>'s run has in flight. Null when it has none
    /// (the run ended or was stopped): the caller refuses the call.
    /// </summary>
    Task<ToolResult?> RunAsync(string sessionId, ToolCallPart call, CancellationToken ct);
}
