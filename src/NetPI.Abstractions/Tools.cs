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

    /// <summary>Execute the tool. <paramref name="arguments"/> is the parsed JSON object.</summary>
    ValueTask<ToolResult> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken);
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
