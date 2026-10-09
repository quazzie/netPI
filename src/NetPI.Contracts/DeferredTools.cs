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
            if (ToolLists.Names(off, d.Name, names)) return false;
            if (agent is not null && agent.Depth >= maxDepth && d.Category == "agents" && d.Name != "agent") return false;
            return infrastructure || agent?.ToolAllowlist is not { } allow || ToolLists.Names(allow, d.Name, names);
        }
        var selected = all.Where(t => Allowed(t)).ToList();
        // An explicit gateway allowlist never grants a remote target. A selected target grants infrastructure only.
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
/// Which tools are declared to the model and which are deferred: registered and callable, but not sent until the chat loads
/// them (<c>tool_search</c>, or a call by name; <see cref="LoadedTools"/>), so a request carries the tools a chat uses, not
/// every tool there is. A tool is deferred when its definition says so (an MCP server's unpinned tools) or when the setting
/// <c>tools.deferred</c> names it (<see cref="DefaultDeferred"/> when the setting is absent; <c>[]</c> declares every built-in
/// tool). A subagent's explicit tool list declares the tools it names that the setting would defer.
/// </summary>
public sealed class ToolExposure
{
    public const string Setting = "tools.deferred";

    /// <summary>
    /// The built-in tools deferred by default: everything but the file and shell tools a chat works with all the time,
    /// <c>codemode</c>, <c>tool_search</c> and <c>ask_user</c> (a question to the user is a call of its own, never a
    /// script's). Each is a call away: the model calls it by name or finds it with <c>tool_search</c>.
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultDeferred =
    [
        "agent", "agent_choices", "agent_spawn", "browser", "decide", "diag", "goal_set", "goal_update", "ideas", "lsp",
        "mcp_resource", "memory_search", "plan_enter", "plan_submit", "schedules", "screenshot", "show_image", "skill", "ssh",
        "todo_write", "web_fetch", "web_search", "windows", "workspace",
    ];

    private readonly HashSet<string> _configured;
    private readonly HashSet<string> _loaded;
    private readonly IReadOnlyList<string>? _allowlist;

    private ToolExposure(HashSet<string> configured, HashSet<string> loaded, IReadOnlyList<string>? allowlist)
    {
        _configured = configured;
        _loaded = loaded;
        _allowlist = allowlist;
    }

    /// <summary>The exposure for one agent's session: the setting, the session's loaded tools, the agent's tool list.</summary>
    public static ToolExposure For(ISettings? settings, AgentInfo? agent, SessionInfo? session) =>
        new(Configured(settings), LoadedTools.Of(session), agent?.ToolAllowlist);

    /// <summary>The names <c>tools.deferred</c> defers (the default list when it is not set or cannot be read).</summary>
    public static HashSet<string> Configured(ISettings? settings)
    {
        List<string>? list = null;
        try { list = settings?.Get<List<string>>(Setting); } catch { }
        return new HashSet<string>((list ?? [.. DefaultDeferred]).Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()),
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The session's loaded tools.</summary>
    public IReadOnlySet<string> Loaded => _loaded;

    /// <summary>Not declared unless loaded: deferred by its definition, or by the setting and not named by the agent's tool list.</summary>
    public bool IsDeferred(ToolDefinition definition) =>
        definition.Deferred
        || (_configured.Contains(definition.Name) && !(_allowlist?.Contains(definition.Name, StringComparer.OrdinalIgnoreCase) ?? false));

    /// <summary>Whether the model is sent this definition: not deferred, or loaded into the session.</summary>
    public bool IsDeclared(ToolDefinition definition) => !IsDeferred(definition) || _loaded.Contains(definition.Name);
}

/// <summary>
/// Deferred tools loaded into one session: <c>meta.toolsLoaded</c> lists the names <c>tool_search</c> found for it or the
/// model called by name. A loaded
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

    /// <summary>Load <paramref name="names"/> into the session (its meta, so the next model call declares them).</summary>
    public static void Add(ISessionStore sessions, string sessionId, IEnumerable<string> names)
    {
        var adding = names.ToList();
        if (adding.Count == 0) return;
        sessions.UpdateSession(sessionId, s =>
        {
            var loaded = Of(s);
            foreach (var n in adding) loaded.Add(n);
            s.Meta ??= new JsonObject();
            s.Meta[MetaKey] = new JsonArray([.. loaded.Order(StringComparer.Ordinal).Select(n => (JsonNode?)n)]);
        });
    }
}
