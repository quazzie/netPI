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
    /// <summary>Messages read per page, and how many pages to walk back (a chat's tool notices are sparse; this is a backstop).</summary>
    private const int MessagePage = 500;
    private const int MaxPages = 20;

    public static JsonObject Build(IPluginContext ctx, ToolNotices notices, PromptStore store, string sessionId)
    {
        var session = ctx.Sessions.Require(sessionId);
        var agent = ctx.Services.Get<IAgentRuntime>()?.GetBySession(sessionId);
        var baseline = store.GetTools(sessionId);
        var (changes, truncated) = Scan(ctx, sessionId);
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
            ["truncated"] = truncated,
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
    /// notice the context compacted away still happened, so it stays in the history). The messages are walked back page
    /// by page: <c>GetMessages</c> serves the <em>newest</em> page, so a long chat would otherwise lose its oldest
    /// changes without a word. <paramref name="truncated"/> says when the walk hit <see cref="MaxPages"/>.
    /// </summary>
    private static (List<JsonObject> Changes, bool Truncated) Scan(IPluginContext ctx, string sessionId)
    {
        var list = new List<JsonObject>();
        long? before = null;
        for (var page = 0; page < MaxPages; page++)
        {
            var batch = ctx.Sessions.GetMessages(sessionId, before, MessagePage);
            foreach (var m in batch)
            {
                if (m.Role != MessageRole.Notice || m.MetaString("kind") != ToolNotices.Kind) continue;
                list.Add(new JsonObject
                {
                    ["seq"] = m.Seq,
                    ["time"] = m.CreatedAt.ToString("O", CultureInfo.InvariantCulture),
                    ["added"] = Strings(m, "added"),
                    ["removed"] = Strings(m, "removed"),
                    ["updated"] = Strings(m, "updated"),
                    ["revisions"] = m.Meta?["revisions"]?.DeepClone(),
                    ["cause"] = m.MetaString("cause") ?? ToolChanges.Unknown,
                    ["plugins"] = Strings(m, "plugins"),
                    ["text"] = m.Text,
                });
            }
            if (batch.Count < MessagePage || batch[0].Seq <= 1) return (list, false);  // that was the first page
            before = batch[0].Seq;   // carry on just before the oldest message of this one
        }
        return (list, Truncated: true);   // the walk ran out of pages, not of messages
    }

    private static JsonArray Strings(ChatMessage m, string key) =>
        new([.. (m.Meta?[key] as JsonArray ?? []).Select(n => n?.DeepClone())]);
}
