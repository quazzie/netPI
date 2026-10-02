using System.Text.Json;

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
    public static List<IAgentTool> Eligible(IToolRegistry registry, AgentInfo? agent, SessionInfo? session,
        int maxDepth = 3, bool includeOff = false)
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
        return selected.OrderBy(t => t.Definition.Name, StringComparer.Ordinal).ToList();
    }

    public static IReadOnlyList<ToolDefinition> Visible(IEnumerable<IAgentTool> eligible) =>
        eligible.Where(t => !t.Definition.Deferred).Select(t => t.Definition).ToList();
}
