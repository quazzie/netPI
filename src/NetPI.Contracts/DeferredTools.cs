using System.Text.Json;
using System.Text.Json.Nodes;

namespace NetPI;

/// <summary>A tool that makes deferred targets usable without enumerating their schemas.</summary>
public interface IDeferredToolInfrastructure
{
    bool Supports(ToolDefinition target);
}

/// <summary>An indirect tool is resolved by the runner, never executed by the gateway itself.</summary>
public interface IIndirectAgentTool : IAgentTool
{
    ValueTask<ResolvedToolCall> ResolveAsync(ToolContext context, JsonElement arguments, CancellationToken ct);
    /// <summary>Recheck disclosure/revision immediately before execution, including after policy hooks.</summary>
    ValueTask ValidateAsync(ToolContext context, JsonElement originalArguments, CancellationToken ct) => ValueTask.CompletedTask;
}

public sealed class ResolvedToolCall
{
    public required string ToolName { get; init; }
    public required JsonElement Arguments { get; init; }
    public string? ServerId { get; init; }
}

/// <summary>One rule for eligibility across runtime, context preview and deferred search.</summary>
public static class ToolSelection
{
    /// <summary><c>agents.maxDepth</c>: how deep subagents may nest (the runtime, context and mcp all read it from here).</summary>
    public const int DefaultMaxDepth = 3;

    /// <summary>The setting, clamped to at least 1 like the schema's minimum (a bad value is the default).</summary>
    public static int MaxDepth(ISettings? settings)
    {
        try { return Math.Max(1, settings?.Get("agents.maxDepth", DefaultMaxDepth) ?? DefaultMaxDepth); } catch { return DefaultMaxDepth; }
    }

    public static List<IAgentTool> Eligible(IToolRegistry registry, AgentInfo? agent, SessionInfo? session,
        int maxDepth = DefaultMaxDepth, bool includeOff = false)
    {
        var all = registry.All;
        var names = all.Select(t => t.Definition.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var off = includeOff ? new HashSet<string>() : SessionTools.Off(session);
        bool Allowed(IAgentTool tool, bool infrastructure = false)
        {
            var d = tool.Definition;
            if (ToolLists.Names(off, d.Name, names)) return false;
            if (agent is not null && agent.Depth >= maxDepth && d.Category == "agents" && d.Name != "agent") return false;
            return infrastructure || agent?.ToolAllowlist is not { } allow || ToolLists.Names(allow, d.Name, names);
        }
        var selected = all.Where(t => Allowed(t)).ToList();
        // An explicit gateway allowlist never grants a remote target. A selected target grants infrastructure only.
        foreach (var tool in all)
            if (tool is IDeferredToolInfrastructure infra && Allowed(tool, infrastructure: true)
                && selected.Any(t => t.Definition.Deferred && infra.Supports(t.Definition))
                && !selected.Contains(tool)) selected.Add(tool);
        // infrastructure with nothing deferred to reach (tool_search in a chat without MCP tools) is only noise in the request
        selected.RemoveAll(t => t is IDeferredToolInfrastructure infra && !selected.Any(d => d.Definition.Deferred && infra.Supports(d.Definition)));
        return selected.OrderBy(t => t.Definition.Name, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// The definitions the model is sent: every eligible tool that is not deferred, and the deferred ones loaded into this
    /// session (<see cref="LoadedTools"/>).
    /// </summary>
    public static IReadOnlyList<ToolDefinition> Visible(IEnumerable<IAgentTool> eligible, SessionInfo? session) =>
        Declared(eligible, session).Select(t => t.Definition).ToList();

    /// <summary>The eligible tools whose definitions the model is sent (see <see cref="Visible"/>).</summary>
    public static IEnumerable<IAgentTool> Declared(IEnumerable<IAgentTool> eligible, SessionInfo? session)
    {
        var loaded = LoadedTools.Of(session);
        return eligible.Where(t => IsDeclared(t.Definition, loaded));
    }

    /// <summary>Whether the model is sent this definition, given the session's loaded tools.</summary>
    public static bool IsDeclared(ToolDefinition definition, IReadOnlySet<string> loaded) =>
        !definition.Deferred || loaded.Contains(definition.Name);
}

/// <summary>
/// Deferred tools loaded into one session: <c>meta.toolsLoaded</c> lists the names <c>tool_search</c> found for it. A loaded
/// tool is declared to the model like any other from the next model call on, so it is called directly with its own schema;
/// the list is the session's, so it survives a restart and a fork keeps it. A tool switched off (<c>meta.toolsOff</c>) stays
/// off, and a name that no longer names an eligible tool is simply not sent.
/// </summary>
public static class LoadedTools
{
    public const string MetaKey = "toolsLoaded";

    /// <summary>The tool names loaded into the session (case-sensitive, like tool names in a request); empty when none.</summary>
    public static HashSet<string> Of(SessionInfo? session)
    {
        var loaded = new HashSet<string>(StringComparer.Ordinal);
        if (session?.Meta?[MetaKey] is JsonArray names)
            foreach (var n in names)
                if (n is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s)) loaded.Add(s.Trim());
        return loaded;
    }
}
