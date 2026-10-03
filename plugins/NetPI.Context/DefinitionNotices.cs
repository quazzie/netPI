using System.Text.Json.Nodes;

namespace NetPI.Context;

/// <summary>Only definitions a chat has actually seen are eligible for a revision notice.</summary>
internal static class DefinitionNotices
{
    public static bool Announce(IPluginContext ctx, PromptStore store, ToolChanges changes, AgentTurnContext turn, ToolChanges.MetaChange? pending = null)
    {
        var sessionId = turn.Run.Session.Id;
        var context = ctx.Sessions.GetContextMessages(sessionId);
        var known = new Dictionary<string, string>(StringComparer.Ordinal);
        if (store.LastSentRevisions(sessionId) is { } sent)   // in memory: PromptStore keeps the last-sent name→revision (idea-l1o09d)
            foreach (var (name, revision) in sent) known[name] = revision;
        foreach (var message in context)
        {
            foreach (var result in message.Parts.OfType<ToolResultPart>())
                if (!result.IsError && result.Details is JsonObject details && details["kind"]?.GetValue<string>() == "mcp-discovery"
                    && details["schemas"] is JsonArray schemas)
                    foreach (var schema in schemas.OfType<JsonObject>())
                        if (schema["id"] is { } id && schema["revision"] is { } revision) known[id.GetValue<string>()] = revision.GetValue<string>();
            if (message.Meta?["revisions"] is JsonObject revisions)
                foreach (var (name, revision) in revisions) if (revision is not null) known[name] = revision.GetValue<string>();
        }
        if (known.Count == 0) return false;
        var eligible = ToolSelection.Eligible(ctx.Tools, turn.Run.Agent, ctx.Sessions.GetSession(sessionId), ToolSelection.MaxDepth(ctx.Settings));
        var current = eligible.ToDictionary(t => t.Definition.Name, t => t.Definition.Revision, StringComparer.Ordinal);
        var visibleKnown = store.GetTools(sessionId) is { } names ? ToolNotices.Known(names, context) : new HashSet<string>();
        var updated = new List<string>(); var removed = new List<string>(); var revisionsOut = new JsonObject();
        foreach (var (name, previous) in known)
        {
            var revision = current.GetValueOrDefault(name) ?? "unavailable";
            if (revision == previous) continue;
            // The ordinary name-diff notice already announces disappearance of visible/pinned tools.
            if (revision == "unavailable" && visibleKnown.Contains(name)) continue;
            if (revision == "unavailable") removed.Add(name); else updated.Add(name);
            revisionsOut[name] = revision;
        }
        if (updated.Count + removed.Count == 0) return false;
        var session = ctx.Sessions.GetSession(sessionId);
        var off = SessionTools.Off(session);
        var cause = changes.Cause(sessionId, updated, removed, off, context, pending);
        var text = "Disclosed tool definitions changed.";
        if (updated.Count > 0) text += " Updated: " + string.Join(", ", updated.Take(20)) + ". Inspect the current schema before calling.";
        if (removed.Count > 0) text += " No longer available: " + string.Join(", ", removed.Take(20)) + ".";
        if (updated.Count + removed.Count > 20) text += $" ({updated.Count + removed.Count} affected tools.)";
        if (cause.Clause.Length > 0) text += " Cause: " + cause.Clause + ".";
        var notice = ChatMessage.NoticeText(text, ToolNotices.Kind);
        notice.Meta!["added"] = new JsonArray();
        notice.Meta["removed"] = ServerArray(removed);
        notice.Meta["updated"] = ServerArray(updated);
        notice.Meta["revisions"] = revisionsOut;
        notice.Meta["cause"] = cause.Cause;
        notice.Meta["plugins"] = ServerArray(cause.Plugins);
        ctx.Sessions.AppendMessage(sessionId, notice);
        return true;
    }
    private static JsonArray ServerArray(IEnumerable<string> names) => new(names.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray());
}
