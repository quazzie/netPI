using System.Text.Json;
using System.Text.Json.Nodes;

namespace NetPI;

/// <summary>Describes a tool to the model and to the UI.</summary>
public sealed class ToolDefinition
{
    /// <summary>Function name sent to the model (snake_case, [a-z0-9_]).</summary>
    public required string Name { get; init; }
    /// <summary>What the model reads in every request: keep it to a sentence or two (details go in <see cref="Help"/>).</summary>
    public required string Description { get; init; }
    /// <summary>
    /// The tool's full manual (details, options, examples), sent only when the model calls the tool with
    /// <c>{"help": true}</c>: the runtime answers that call with the description, this text and the argument schema, and
    /// does not run the tool. Keeps the definitions every request carries short.
    /// </summary>
    public string? Help { get; init; }
    /// <summary>JSON schema of the arguments object.</summary>
    public JsonObject Parameters { get; init; } = new() { ["type"] = "object", ["properties"] = new JsonObject() };
    /// <summary>Short label for the UI ("Read", "Bash").</summary>
    public string? Label { get; init; }
    /// <summary>Extra guidance bullets the context plugin adds to the system prompt when this tool is active.</summary>
    public IReadOnlyList<string>? PromptGuidelines { get; init; }
    /// <summary>Tool does not modify anything; several read-only calls may run in parallel.</summary>
    public bool ReadOnly { get; init; }
    /// <summary>Selectable and executable, but omitted from ordinary model-visible schemas and prompt guidelines.</summary>
    public bool Deferred { get; init; }
    /// <summary>Stable definition revision, used by discovery and change notices.</summary>
    public string? Revision { get; init; }
    /// <summary>files | shell | agents | ideas | general</summary>
    public string Category { get; init; } = "general";
    /// <summary>Argument name whose value best summarizes a call in the UI (e.g. "path", "command").</summary>
    public string? SummaryArg { get; init; }

    private int _parametersChars = -1;
    /// <summary>
    /// Length of <see cref="Parameters"/> as JSON, worked out once per definition. The compaction estimate needs it
    /// for every tool on every turn, and serializing the whole schema tree just to measure it is tens of kilobytes of
    /// garbage per turn for nothing.
    /// </summary>
    public int ParametersChars => _parametersChars < 0 ? _parametersChars = Parameters.ToJsonString().Length : _parametersChars;
}

/// <summary>
/// A tool whose calls differ (one tool with several actions): some only read, others change things. The runtime asks it per
/// call whether a call only reads, so read-only calls of a turn can run in parallel; its <see cref="ToolDefinition.ReadOnly"/>
/// stays false.
/// </summary>
public interface IReadOnlyCalls
{
    bool IsReadOnly(JsonElement args);
}

/// <summary>
/// Names in tool lists: a session's switched-off tools, a subagent's allowlist, a profile's list. An entry is a tool's
/// name; an entry that names no registered tool but reads <c>&lt;tool&gt;_&lt;action&gt;</c> names that tool, so lists written
/// before the tools with actions were merged (ssh_run, agent_wait, process_kill) keep working. One rule, no list of old
/// names: agent_spawn, a tool of its own, still names only itself.
/// </summary>
public static class ToolLists
{
    /// <summary>Whether an entry of <paramref name="list"/> names <paramref name="tool"/> (<paramref name="registered"/>: the tool names there are).</summary>
    public static bool Names(IEnumerable<string> list, string tool, IReadOnlySet<string> registered)
    {
        foreach (var entry in list)
            if (Names(entry, tool, registered)) return true;
        return false;
    }

    public static bool Names(string entry, string tool, IReadOnlySet<string> registered) =>
        string.Equals(entry, tool, StringComparison.OrdinalIgnoreCase)
        || (entry.Length > tool.Length + 1 && entry.StartsWith(tool + "_", StringComparison.OrdinalIgnoreCase) && !registered.Contains(entry));
}

/// <summary>A tool the agent can call. Tools are plugins and can be replaced by registering the same name with a higher priority.</summary>
public interface IAgentTool
{
    ToolDefinition Definition { get; }
    Task<ToolResult> ExecuteAsync(ToolContext context, JsonElement args, CancellationToken ct);
}

public sealed class ToolContext
{
    /// <summary>What the part that built this context knows beyond the core's own concepts (the checkout the session works in, the slot the run holds): ask by type.</summary>
    public FeatureSet Features { get; } = new();
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
    /// <summary>Current eligible tools, supplied by the runtime; re-evaluated after policy changes.</summary>
    public Func<IReadOnlyList<IAgentTool>>? EligibleTools { get; init; }
    /// <summary>Extra per-call state.</summary>
    public Dictionary<string, object?> Items { get; } = [];

    /// <summary>
    /// Resolve a possibly relative path against <see cref="Cwd"/>. Accepts both / and \ and ~, and on Windows the paths
    /// Git Bash prints: <c>/c/foo</c> (and <c>/c</c>) for drives, <c>/tmp/foo</c> for the user's temp folder.
    /// </summary>
    public string ResolvePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return Cwd;
        path = path.Trim().Trim('"');
        if (path == "~" || path.StartsWith("~/") || path.StartsWith("~\\"))
            path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path.Length > 2 ? path[2..] : "");
        if (OperatingSystem.IsWindows())
        {
            if (path.Length >= 2 && path[0] == '/' && char.IsLetter(path[1]) && (path.Length == 2 || path[2] == '/'))
                path = $"{char.ToUpperInvariant(path[1])}:\\{(path.Length > 3 ? path[3..] : "")}";
            else if (path == "/tmp" || path.StartsWith("/tmp/", StringComparison.Ordinal))
                path = Path.Combine(Path.GetTempPath(), path.Length > 5 ? path[5..] : "");
            path = path.Replace('/', '\\');
        }
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

/// <summary>
/// The longest tool result the model is sent (setting <c>agent.maxToolResultChars</c>). The agent runner saves a longer
/// result to a file and sends its start, end and path; tools that page or tail their own output stay under it, so
/// nothing is cut twice.
/// </summary>
public static class ToolResultLimit
{
    public const string Setting = "agent.maxToolResultChars";
    public const int Default = 20_000;

    /// <summary>The limit in characters (0 or less: none).</summary>
    public static int Get(ISettings? settings)
    {
        try { return settings?.Get(Setting, Default) ?? Default; }
        catch { return Default; }
    }

    /// <summary>A tool's own cap, kept under the limit with room for its notes.</summary>
    public static int Fit(ISettings? settings, int own, int room = 1000)
    {
        var limit = Get(settings);
        return limit > 0 ? Math.Clamp(limit - room, 1024, own) : own;
    }
}

public interface IToolRegistry
{
    IDisposable Register(IAgentTool tool, int priority = 0);
    /// <summary>Effective tools: highest priority per name, minus tools disabled in settings (tools.disabled).</summary>
    IReadOnlyList<IAgentTool> All { get; }
    IAgentTool? Get(string name);
    /// <summary>All registrations including shadowed ones (for diagnostics).</summary>
    IReadOnlyList<ToolRegistration> Registrations { get; }
}
