using System.Text.Json;
using System.Text.Json.Nodes;

namespace NetPI;

/// <summary>Describes a tool to the model and to the UI.</summary>
public sealed class ToolDefinition
{
    /// <summary>Function name sent to the model (snake_case, [a-z0-9_]).</summary>
    public required string Name { get; init; }
    public required string Description { get; init; }
    /// <summary>JSON schema of the arguments object.</summary>
    public JsonObject Parameters { get; init; } = new() { ["type"] = "object", ["properties"] = new JsonObject() };
    /// <summary>Short label for the UI ("Read", "Bash").</summary>
    public string? Label { get; init; }
    /// <summary>Extra guidance bullets the context plugin adds to the system prompt when this tool is active.</summary>
    public IReadOnlyList<string>? PromptGuidelines { get; init; }
    /// <summary>Tool does not modify anything; several read-only calls may run in parallel.</summary>
    public bool ReadOnly { get; init; }
    /// <summary>files | shell | agents | ideas | general</summary>
    public string Category { get; init; } = "general";
    /// <summary>Argument name whose value best summarizes a call in the UI (e.g. "path", "command").</summary>
    public string? SummaryArg { get; init; }
}

/// <summary>A tool the agent can call. Tools are plugins and can be replaced by registering the same name with a higher priority.</summary>
public interface IAgentTool
{
    ToolDefinition Definition { get; }
    Task<ToolResult> ExecuteAsync(ToolContext context, JsonElement args, CancellationToken ct);
}

public sealed class ToolContext
{
    public required string SessionId { get; init; }
    public required string AgentId { get; init; }
    public required string CallId { get; init; }
    /// <summary>Working directory (project folder or default workspace). Relative paths resolve against it.</summary>
    public required string Cwd { get; init; }
    public ProjectInfo? Project { get; init; }
    public ModelInfo? Model { get; init; }
    public required IServiceRegistry Services { get; init; }
    public required IEventBus Events { get; init; }
    /// <summary>Stream incremental output to the UI (e.g. shell output).</summary>
    public Action<string>? Output { get; init; }
    /// <summary>Extra per-call state.</summary>
    public Dictionary<string, object?> Items { get; } = [];

    /// <summary>Resolve a possibly relative path against <see cref="Cwd"/>. Accepts both / and \ and ~.</summary>
    public string ResolvePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return Cwd;
        path = path.Trim().Trim('"');
        if (path == "~" || path.StartsWith("~/") || path.StartsWith("~\\"))
            path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path.Length > 2 ? path[2..] : "");
        // Git-bash style /c/foo on Windows
        if (OperatingSystem.IsWindows() && path.Length >= 3 && path[0] == '/' && char.IsLetter(path[1]) && path[2] == '/')
            path = $"{char.ToUpperInvariant(path[1])}:\\{path[3..]}";
        if (OperatingSystem.IsWindows()) path = path.Replace('/', '\\');
        return Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(Cwd, path));
    }
}

public sealed class ToolResult
{
    /// <summary>Text returned to the model.</summary>
    public string Content { get; init; } = "";
    public bool IsError { get; init; }
    public List<ImagePart>? Images { get; init; }
    /// <summary>Structured data for the UI (serialized to JSON and stored with the result).</summary>
    public object? Details { get; init; }

    public static ToolResult Ok(string content, object? details = null) => new() { Content = content, Details = details };
    public static ToolResult Error(string message, object? details = null) => new() { Content = message, IsError = true, Details = details };
}

public sealed record ToolRegistration(IAgentTool Tool, string PluginId, int Priority);

public interface IToolRegistry
{
    IDisposable Register(IAgentTool tool, int priority = 0);
    /// <summary>Effective tools: highest priority per name, minus tools disabled in settings (tools.disabled).</summary>
    IReadOnlyList<IAgentTool> All { get; }
    IAgentTool? Get(string name);
    /// <summary>All registrations including shadowed ones (for diagnostics).</summary>
    IReadOnlyList<ToolRegistration> Registrations { get; }
}
