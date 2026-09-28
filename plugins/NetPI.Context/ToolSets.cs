using System.Globalization;
using System.Text.Json.Nodes;

namespace NetPI.Context;

/// <summary>
/// What <c>context.toolsets</c> returns: a session's tools now, and every change since its baseline, each with the cause
/// <see cref="ToolChanges"/> worked out for the notice. It reads what the context plugin already keeps — the baseline
/// (<c>context_tools</c>, "what the model was last told") and the "tools" notices in the chat ("what it was told since")
/// — so neither a SQL client nor a message-store dig is needed to answer "what changed in my environment, when and why".
/// A session that never called a model has no baseline yet and no changes.
/// </summary>
internal static class ToolSets
{
    /// <summary>How many messages back the notices are read (a page of the newest, then filtered).</summary>
    private const int MessagePage = 500;

    public static JsonObject Build(IPluginContext ctx, ToolNotices notices, PromptStore store, string sessionId)
    {
        var session = ctx.Sessions.GetSession(sessionId) ?? throw new RpcException("not_found", $"No session {sessionId}");
        var agent = ctx.Services.Get<IAgentRuntime>()?.GetBySession(sessionId);
        var baseline = store.GetTools(sessionId);
        var changes = Changes(ctx, sessionId);
        var tools = ContextPlugin.ActiveTools(ctx, agent, session).Select(t => t.Definition.Name).OrderBy(n => n, StringComparer.Ordinal);

        return new JsonObject
        {
            ["sessionId"] = sessionId,
            ["tools"] = new JsonArray(tools.Select(n => (JsonNode?)n).ToArray()),
            ["baseline"] = baseline is null
                ? null
                : new JsonObject
                {
                    ["tools"] = new JsonArray(baseline.Names.Select(n => (JsonNode?)n).ToArray()),
                    ["sinceSeq"] = baseline.SinceSeq,
                },
            ["changes"] = new JsonArray([.. changes]),
            ["reloads"] = new JsonArray([.. notices.Reloads().Select(r => (JsonNode?)new JsonObject
            {
                ["ids"] = new JsonArray(r.Ids.Select(id => (JsonNode?)id).ToArray()),
                ["time"] = r.Time.ToString("O", CultureInfo.InvariantCulture),
                ["kind"] = r.Kind,
            })]),
        };
    }

    /// <summary>
    /// Every tool-set change the session was told about, oldest first, from the "tools" notices in the whole chat (a
    /// notice the context compacted away still happened, so it stays in the history).
    /// </summary>
    private static List<JsonObject> Changes(IPluginContext ctx, string sessionId)
    {
        var list = new List<JsonObject>();
        foreach (var m in ctx.Sessions.GetMessages(sessionId, null, MessagePage))
        {
            if (m.Role != MessageRole.Notice || m.MetaString("kind") != ToolNotices.Kind) continue;
            list.Add(new JsonObject
            {
                ["seq"] = m.Seq,
                ["time"] = m.CreatedAt.ToString("O", CultureInfo.InvariantCulture),
                ["added"] = Strings(m, "added"),
                ["removed"] = Strings(m, "removed"),
                ["cause"] = m.MetaString("cause") ?? ToolChanges.Unknown,
                ["plugins"] = Strings(m, "plugins"),
                ["text"] = m.Text,
            });
        }
        list.Sort((a, b) => ((long)a["seq"]!).CompareTo((long)b["seq"]!));
        return list;
    }

    private static JsonArray Strings(ChatMessage m, string key) =>
        new([.. (m.Meta?[key] as JsonArray ?? []).Select(n => n?.DeepClone())]);
}
