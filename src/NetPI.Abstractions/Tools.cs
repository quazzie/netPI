using System.Text.Json;

namespace NetPI.Abstractions;

/// <summary>
/// A tool the agent can invoke. Tools are contributed by plugins (PLAN §18-25);
/// they are registered into the host registry and resolved through it.
/// </summary>
public interface IAgentTool
{
    /// <summary>Stable tool name as seen by the model (e.g. "bash").</summary>
    string Name { get; }

    string Description { get; }

    /// <summary>JSON Schema describing parameters.</summary>
    JsonElement Parameters { get; }

    /// <summary>
    /// PLAN §15: concise behavioral guidance the prompt should give the model
    /// about this tool (e.g. "use offset to page large files"). Collected by
    /// the agent runner into the system prompt. Tools with no guidance return
    /// an empty list. (Declared abstract so every tool spells out its guidance
    /// explicitly; there is no implicit default.)
    /// </summary>
    IReadOnlyList<string> Guidelines { get; }

    /// <summary>
    /// Execute the tool (PLAN §11/§18/§25). The context carries the parsed
    /// arguments plus the session/workspace scope: relative paths resolve
    /// against <see cref="ToolContext.Workspace"/> and foreground shells start
    /// there.
    /// </summary>
    ValueTask<ToolResult> ExecuteAsync(ToolContext context, CancellationToken cancellationToken);
}

/// <summary>Registry of tools contributed by all active plugins.</summary>
public interface IToolRegistry
{
    /// <summary>Register a tool. Owned by the registering plugin; removed on its unload.</summary>
    IDisposable Register(IAgentTool tool);

    /// <summary>All currently available tools (snapshot).</summary>
    IReadOnlyList<IAgentTool> All();

    /// <summary>Find a tool by name, or null.</summary>
    IAgentTool? Find(string name);
}

/// <summary>
/// Execution context handed to every tool (PLAN §18/§25). Relative file
/// paths resolve against <see cref="Workspace"/>; absolute paths and <c>..</c>
/// are allowed (no permission prompt).
/// </summary>
public sealed record ToolContext(
    JsonElement Arguments,
    string Workspace,
    string? SessionId)
{
    /// <summary>Resolve a path against the session workspace; absolute paths pass through.</summary>
    public string ResolvePath(string? p) =>
        string.IsNullOrEmpty(p) ? Workspace
            : (Path.IsPathRooted(p) ? p : Path.GetFullPath(Path.Combine(Workspace, p)));
}
