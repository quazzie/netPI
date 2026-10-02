using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace NetPI.Plan;

/// <summary>An MCP tool as its server declared it: what <c>mcp.tool</c> answers (the id a call carries is a hash, the pattern matches the real name).</summary>
internal sealed record McpToolInfo(string Server, string Name, bool ReadOnly);

/// <summary>
/// What a plan-mode chat may call. Nothing may change while the user has not approved a plan: a tool that only reads
/// passes (<see cref="ToolDefinition.ReadOnly"/>, or a call a tool with actions says only reads), the plan's own tools
/// and the questions pass, the shell and the browser never do (a read-only shell cannot be told from a writing one;
/// grep, find, ls and read cover exploring), and an MCP tool passes when its server declared it read-only
/// (<c>readOnlyHint</c>) or it matches <c>plan.mcpAllow</c>.
/// </summary>
internal static class PlanPolicy
{
    /// <summary>Blocked whatever their flags say: a shell can do anything, the browser acts on live pages.</summary>
    internal static readonly HashSet<string> Never = new(StringComparer.OrdinalIgnoreCase) { "bash", "pwsh", "ssh", "process", "browser" };

    /// <summary>Pass without being read-only: they change nothing but the plan, the checklist and the subagents' reports.</summary>
    internal static readonly HashSet<string> Allowed = new(StringComparer.OrdinalIgnoreCase)
    {
        "ask_user", "todo_write", "plan_submit", "compact", "agent", "agent_spawn",
    };

    internal static readonly HashSet<string> McpGateways = new(StringComparer.OrdinalIgnoreCase) { "mcp_search", "mcp_call", "mcp_resource" };

    /// <summary>The reason the call is blocked, or null when it may run. <paramref name="mcp"/> is read for an MCP tool that is not flagged read-only.</summary>
    public static string? Block(string name, ToolDefinition? def, bool readOnlyCall, McpToolInfo? mcp, IReadOnlyList<string> mcpAllow)
    {
        if (Never.Contains(name))
            return name.Equals("browser", StringComparison.OrdinalIgnoreCase)
                ? "Plan mode does not drive the browser: it acts on live pages. Use web_fetch or web_search to read, or put the browsing in the plan."
                : "Plan mode has no shell: nothing runs while you plan. Explore with read, grep, find and ls (web_search and web_fetch for facts outside the repository) and put the commands you want run in the plan's steps.";
        if (name.Equals("plan_enter", StringComparison.OrdinalIgnoreCase)) return "This chat is already in plan mode.";
        if (Allowed.Contains(name)) return null;
        if (name.Equals("mcp_call", StringComparison.OrdinalIgnoreCase))
            return "Plan mode runs only MCP tools that read: the call was not resolved to one. Search again (mcp_search) and call a tool that only reads.";
        if (def is { ReadOnly: true } || readOnlyCall) return null;
        if (def?.Category == "mcp" && !McpGateways.Contains(name))
        {
            if (mcp is { ReadOnly: true }) return null;
            if (mcp is not null && Matches(mcp, mcpAllow)) return null;
            var what = mcp is null ? name : $"{mcp.Server}/{mcp.Name}";
            return $"Plan mode runs only MCP tools that read (the server declares them read-only, or plan.mcpAllow names them): {what} is not one of them. " +
                   "Say in the plan what you would call and why.";
        }
        if (name.Equals("write", StringComparison.OrdinalIgnoreCase) || name.Equals("edit", StringComparison.OrdinalIgnoreCase))
            return $"Plan mode is read-only: {name} is blocked until a plan is approved. Describe the change in the plan (the file, what changes and why) and submit it with plan_submit.";
        return $"Plan mode is read-only: {name} can change things, so it is blocked until a plan is approved. Say in the plan what you would do with it.";
    }

    /// <summary>A pattern is a name with * as wildcard, matched against the MCP tool's own name and <c>server/name</c>, case-insensitively.</summary>
    internal static bool Matches(McpToolInfo mcp, IReadOnlyList<string> patterns)
    {
        foreach (var raw in patterns)
        {
            var pattern = raw?.Trim();
            if (string.IsNullOrEmpty(pattern)) continue;
            var re = "^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$";
            if (Regex.IsMatch(mcp.Name, re, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) ||
                Regex.IsMatch(mcp.Server + "/" + mcp.Name, re, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return true;
        }
        return false;
    }

    /// <summary>The tools a research subagent of a plan gets: the ones that only read, and the way to search MCP (its calls are checked like the chat's).</summary>
    internal static List<string> ChildTools(IEnumerable<IAgentTool> all) => [.. all
        .Select(t => t.Definition)
        .Where(d => !Never.Contains(d.Name) && !Allowed.Contains(d.Name) && !d.Name.StartsWith("plan_", StringComparison.Ordinal)
                    && ((d.ReadOnly && d.Category != "mcp") || McpGateways.Contains(d.Name)))
        .Select(d => d.Name).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal)];

    internal const string ChildInstructions =
        "You are doing research for a plan: this chat is read-only (no writes, no shell). Report what you found with file paths and symbols " +
        "and short quotes, not your opinion of what to build.";

    /// <summary>
    /// The <c>agent_spawn</c> arguments for a plan: every subagent gets only <paramref name="tools"/> (the ones it asked for that
    /// pass), no checkout of its own (it shares the plan chat's, so it sees what the plan sees), and the research instructions.
    /// </summary>
    internal static string RewriteSpawn(string arguments, IReadOnlyList<string> tools)
    {
        if (JsonNode.Parse(string.IsNullOrWhiteSpace(arguments) ? "{}" : arguments) is not JsonObject root) return arguments;
        if (root["subagents"] is JsonArray list)
        {
            foreach (var item in list.OfType<JsonObject>()) Rewrite(item, tools);
        }
        else Rewrite(root, tools);
        return root.ToJsonString();
    }

    private static void Rewrite(JsonObject item, IReadOnlyList<string> tools)
    {
        var wanted = (item["tools"] as JsonArray)?.Select(n => n?.GetValue<string>()?.Trim()).OfType<string>().ToList();
        var allowed = wanted is { Count: > 0 } && wanted.Where(w => tools.Contains(w, StringComparer.OrdinalIgnoreCase)).ToList() is { Count: > 0 } some ? some : [.. tools];
        item["tools"] = new JsonArray([.. allowed.Select(t => (JsonNode)t)]);
        item["isolated"] = false;
        item.Remove("workspace");
        var instructions = item["instructions"]?.GetValue<string>();
        item["instructions"] = string.IsNullOrWhiteSpace(instructions) ? ChildInstructions : instructions.TrimEnd() + "\n\n" + ChildInstructions;
    }
}
