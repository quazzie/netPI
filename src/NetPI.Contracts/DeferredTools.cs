using System.Text.Json.Nodes;

namespace NetPI;

/// <summary>A tool that makes deferred targets usable without enumerating their schemas.</summary>
public interface IDeferredToolInfrastructure
{
    bool Supports(ToolDefinition target);
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

    /// <summary>
    /// The tools an agent may call in a session: not switched off for it, within its allowlist, no orchestration tools at the
    /// deepest level (<c>agents.maxDepth</c> in <paramref name="settings"/>), and the deferred-tool infrastructure when there is
    /// something deferred for it to reach. Deferred tools are eligible: <see cref="ToolExposure"/> says which are declared.
    /// </summary>
    public static List<IAgentTool> Eligible(IToolRegistry registry, AgentInfo? agent, SessionInfo? session,
        ISettings? settings, bool includeOff = false)
    {
        var maxDepth = MaxDepth(settings);
        var exposure = ToolExposure.For(settings, agent, session);
        var all = registry.All;
        var names = all.Select(t => t.Definition.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var off = includeOff ? new HashSet<string>() : SessionTools.Off(session);
        bool Allowed(IAgentTool tool, bool infrastructure = false)
        {
            var d = tool.Definition;
            if (!infrastructure && ToolLists.Names(off, d.Name, names)) return false;
            if (agent is not null && agent.Depth >= maxDepth && d.Category == "agents" && d.Name != "agent") return false;
            return infrastructure || agent?.ToolAllowlist is not { } allow || ToolLists.Names(allow, d.Name, names);
        }
        var selected = all.Where(t => Allowed(t)).ToList();
        // A deferred tool that is selected brings the infrastructure that reaches it (tool_search), even past an allowlist and the chat's off-list (a chat that could find a deferred tool but not run it is stuck).
        foreach (var tool in all)
            if (tool is IDeferredToolInfrastructure infra && Allowed(tool, infrastructure: true)
                && selected.Any(t => exposure.IsDeferred(t.Definition) && infra.Supports(t.Definition))
                && !selected.Contains(tool)) selected.Add(tool);
        // infrastructure with nothing deferred to reach (tool_search in a chat that defers nothing) is only noise in the request
        selected.RemoveAll(t => t is IDeferredToolInfrastructure infra && !selected.Any(d => exposure.IsDeferred(d.Definition) && infra.Supports(d.Definition)));
        return selected.OrderBy(t => t.Definition.Name, StringComparer.Ordinal).ToList();
    }

    /// <summary>The definitions the model is sent: the eligible tools <paramref name="exposure"/> declares.</summary>
    public static IReadOnlyList<ToolDefinition> Visible(IEnumerable<IAgentTool> eligible, ToolExposure exposure) =>
        Declared(eligible, exposure).Select(t => t.Definition).ToList();

    /// <summary>The eligible tools whose definitions the model is sent (see <see cref="Visible"/>).</summary>
    public static IEnumerable<IAgentTool> Declared(IEnumerable<IAgentTool> eligible, ToolExposure exposure) =>
        eligible.Where(t => exposure.IsDeclared(t.Definition));
}

/// <summary>
/// Which tools are declared to the model and which are deferred: registered and callable, but never sent, so a
/// request carries the tools a chat uses, not every tool there is: <c>tool_search</c> shows a deferred tool's schema and
/// <c>call_tool</c> runs it, and neither changes the request's tools. A tool is deferred when its definition says so (an MCP server's unpinned tools) or when the chat's
/// deferred list names it: the session's <c>meta.toolsDeferred</c> (a profile's <c>toolsDeferred</c>, written when the profile
/// is applied), else the setting <c>tools.deferred</c>, else <see cref="DefaultDeferred"/>. <c>[]</c> declares every tool the
/// definition does not defer. An entry is a tool name, <c>"*"</c> (every tool) or <c>"!name"</c> (not this one, whatever
/// else matches): <c>["*", "!codemode"]</c> declares codemode alone. A subagent's explicit tool list declares the tools it
/// names that the list would defer.
/// </summary>
public sealed class ToolExposure
{
    public const string Setting = "tools.deferred";
    /// <summary>The session's own list (from its profile), in place of the setting.</summary>
    public const string MetaKey = "toolsDeferred";

    /// <summary>
    /// The built-in tools deferred by default: everything but the file and shell tools a chat works with all the time,
    /// <c>codemode</c>, <c>tool_search</c> and <c>ask_user</c> (a question to the user is a call of its own, never a
    /// script's). Each is a call away: the model calls it by name or finds it with <c>tool_search</c>.
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultDeferred =
    [
        "agent", "agent_choices", "agent_spawn", "browser", "decide", "diag", "goal_set", "goal_update", "ideas", "lsp",
        "mcp_resource", "memory_search", "plan_enter", "plan_submit", "schedules", "show_image", "skill", "ssh",
        "test", "todo_write", "web_fetch", "web_search", "windows", "workspace",
    ];

    private readonly DeferredList _list;
    private readonly IReadOnlyList<string>? _allowlist;

    private ToolExposure(DeferredList list, IReadOnlyList<string>? allowlist)
    {
        _list = list;
        _allowlist = allowlist;
    }

    /// <summary>The exposure for one agent's session: its deferred list and the agent's tool list.</summary>
    public static ToolExposure For(ISettings? settings, AgentInfo? agent, SessionInfo? session) =>
        new(DeferredList.Parse(Entries(settings, session)), agent?.ToolAllowlist);

    /// <summary>The chat's deferred list as written: the session's <c>meta.toolsDeferred</c>, else the setting, else the default.</summary>
    public static IReadOnlyList<string> Entries(ISettings? settings, SessionInfo? session)
    {
        if (session?.Meta?[MetaKey] is JsonArray own)
            return [.. own.OfType<JsonValue>().Select(v => v.TryGetValue<string>(out var s) ? s : null).OfType<string>()];
        List<string>? list = null;
        try { list = settings?.Get<List<string>>(Setting); } catch { }
        return list ?? [.. DefaultDeferred];
    }

    /// <summary>A deferred list: names, <c>"*"</c> for every tool, <c>"!name"</c> for an exception.</summary>
    private sealed class DeferredList
    {
        private bool _all;
        private readonly HashSet<string> _names = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _except = new(StringComparer.OrdinalIgnoreCase);

        public static DeferredList Parse(IEnumerable<string> entries)
        {
            var list = new DeferredList();
            foreach (var raw in entries)
            {
                var e = raw?.Trim() ?? "";
                if (e.Length == 0) continue;
                if (e == "*") list._all = true;
                else if (e[0] == '!') { if (e.Length > 1) list._except.Add(e[1..].Trim()); }
                else list._names.Add(e);
            }
            return list;
        }

        public bool Names(string tool) => !_except.Contains(tool) && (_all || _names.Contains(tool));
    }

    /// <summary>Not declared: deferred by its definition, or by the chat's list and not named by the agent's tool list.</summary>
    public bool IsDeferred(ToolDefinition definition) =>
        definition.Deferred
        || (_list.Names(definition.Name) && !IsInfrastructure(definition.Name)
            && !(_allowlist?.Contains(definition.Name, StringComparer.OrdinalIgnoreCase) ?? false));

    /// <summary>The tools that reach the deferred ones: a list such as <c>["*"]</c> must not defer them too, or the rest is out of reach.</summary>
    public static bool IsInfrastructure(string name) => name is "tool_search" or "call_tool";

    /// <summary>Whether the model is sent this definition: it is not deferred. A deferred tool is never sent (that would change the
    /// request's tools, and with them the whole conversation is prefilled again): <c>tool_search</c> shows its schema and
    /// <c>call_tool</c> runs it.</summary>
    public bool IsDeclared(ToolDefinition definition) => !IsDeferred(definition);
}
