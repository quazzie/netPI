using System.Text.Json;

namespace NetPI;

/// <summary>
/// Lets a running tool call other tools the way the model does: a codemode script's <c>tools.x(args)</c>. The runtime puts
/// one in <see cref="ToolContext.Features"/> for every call the model made (ask with <c>Features.Get&lt;INestedToolCalls&gt;()</c>);
/// a nested call's own context has none, so a nested tool cannot nest further.
/// <para>A nested call goes through the pipeline of a model-issued one: the tool must be eligible for the chat (deferred tools
/// included, loaded or not: a script reaches the whole catalogue without declaring it), the before-call hooks (guardrails,
/// plan mode, the workspace guard) may block it or rewrite its arguments, and the after-call hooks see its result. It is not a
/// message of its own in the transcript: the calling tool reports it. Calls that change something run one at a time; read-only
/// ones run side by side, a few at once.</para>
/// </summary>
public interface INestedToolCalls
{
    /// <summary>The tools a nested call may name: the chat's eligible tools minus the gateways and the deferred-tool
    /// infrastructure (a script calls a deferred tool directly) and the calling tool itself.</summary>
    IReadOnlyList<ToolDefinition> Tools { get; }

    /// <summary>
    /// Run one call. Never throws for the tool's own failure: an unknown tool, a blocked call, invalid arguments and a tool
    /// that throws are error results. Only the cancellation of <paramref name="ct"/> throws. The content is the tool's whole
    /// text, not cut to <c>agent.maxToolResultChars</c>: the caller decides what reaches the model.
    /// </summary>
    Task<ToolResult> CallAsync(string name, JsonElement arguments, CancellationToken ct);
}
