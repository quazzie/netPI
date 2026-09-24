using System.Text.Json;
using System.Text.Json.Nodes;

namespace NetPI.Agent;

/// <summary>
/// The per-session tool switches (<see cref="SessionTools"/>, <c>meta.toolsOff</c>) behind <c>agent.tools</c> and
/// <c>agent.setTools</c>. Before the first message a change is free; later it applies from the next model call, which
/// re-reads the conversation once (the tool definitions lead the request prefix) and gets a "tools" notice.
/// </summary>
internal static class SessionToolSwitches
{
    /// <summary>The tools the session's agent can have, each with its switch.</summary>
    public static JsonObject Info(IPluginContext ctx, AgentRuntime runtime, string sessionId)
    {
        var session = ctx.Sessions.GetSession(sessionId) ?? throw new RpcException("not_found", $"No session {sessionId}");
        var off = SessionTools.Off(session);
        var plugins = new Dictionary<IAgentTool, string>(ReferenceEqualityComparer.Instance);
        try { foreach (var r in ctx.Tools.Registrations) plugins.TryAdd(r.Tool, r.PluginId); } catch { }

        var tools = new JsonArray();
        foreach (var t in runtime.ToolsFor(runtime.GetBySession(sessionId), session, includeOff: true))
        {
            var d = t.Definition;
            tools.Add(new JsonObject
            {
                ["name"] = d.Name,
                ["label"] = d.Label,
                ["category"] = d.Category,
                ["description"] = d.Description,
                ["readOnly"] = d.ReadOnly,
                ["pluginId"] = plugins.GetValueOrDefault(t),
                ["on"] = !off.Contains(d.Name),
            });
        }
        return new JsonObject
        {
            ["sessionId"] = session.Id,
            ["started"] = session.MessageCount > 0,
            ["contextTokens"] = session.ContextTokens,
            ["off"] = new JsonArray([.. off.Order(StringComparer.Ordinal).Select(n => (JsonNode?)n)]),
            ["tools"] = tools,
        };
    }

    /// <summary>Switch tools off (<paramref name="off"/>) or back on (<paramref name="on"/>) for one session.</summary>
    public static JsonObject Set(IPluginContext ctx, AgentRuntime runtime, string sessionId, IReadOnlyList<string> off, IReadOnlyList<string> on)
    {
        if (ctx.Sessions.GetSession(sessionId) is null) throw new RpcException("not_found", $"No session {sessionId}");
        ctx.Sessions.UpdateSession(sessionId, s =>
        {
            var names = SessionTools.Off(s);
            foreach (var n in off) names.Add(n);
            foreach (var n in on) names.Remove(n);
            if (names.Count == 0)
            {
                s.Meta?.Remove(SessionTools.MetaKey);
                return;
            }
            s.Meta ??= new JsonObject();
            s.Meta[SessionTools.MetaKey] = new JsonArray([.. names.Order(StringComparer.Ordinal).Select(n => (JsonNode?)n)]);
        });
        return Info(ctx, runtime, sessionId);
    }

    /// <summary>Tool names from a JSON array of strings.</summary>
    public static List<string> Names(JsonElement? value) =>
        value is { ValueKind: JsonValueKind.Array } arr
            ? [.. arr.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!.Trim()).Where(n => n.Length > 0)]
            : [];
}
