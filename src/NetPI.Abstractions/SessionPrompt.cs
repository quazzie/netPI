using System.Text.Json.Nodes;

namespace NetPI;

/// <summary>Durable explicit prompt invalidation; ordinary settings/model changes do not change a sent prefix.</summary>
public static class SessionPrompt
{
    public const string RevisionKey = "promptRevision";
    public const string FallbackKey = "runtimePrompt";
    public const string FallbackRevisionKey = "runtimePromptRevision";
    public const string ForkResetKey = "promptForkReset";
    public const string HistoryKey = "sentPromptHistory";
    private static long Number(JsonNode? node) => node is JsonValue v ? v.TryGetValue<long>(out var n) ? n : v.TryGetValue<int>(out var i) ? i : 0 : 0;
    public static long Revision(SessionInfo session) => Number(session.Meta?[RevisionKey]);
    public static void Invalidate(SessionInfo session)
    {
        var next = checked(Revision(session) + 1);
        session.Meta ??= new JsonObject();
        session.Meta[RevisionKey] = next;
        if (session.Meta["forkedFrom"] is not null) session.Meta[ForkResetKey] = true;
    }
    public static string? Fallback(SessionInfo session) => session.Meta?[FallbackRevisionKey] is JsonValue r
        && Number(r) == Revision(session)
        && session.Meta?[FallbackKey] is JsonValue p && p.TryGetValue<string>(out var prompt) ? prompt : null;
    /// <summary>
    /// Record a prompt sent at the model send boundary, in session-owned durable JSON: the one record of what was sent and
    /// which tool definitions went with it. A <paramref name="final"/> prompt also becomes the session's frozen prefix
    /// (<see cref="Fallback"/>); a provisional one (a prompt section failed) is only appended to the history, marked
    /// <c>provisional</c>. The history entry is <c>{ afterSeq, revision, prompt, createdAt, tools: [{ name, def, revision? }],
    /// provisional? }</c>; <c>def</c> is <see cref="ToolDefinition.DefinitionHash"/>. An entry is appended unless the newest one has
    /// the same prompt and the same provisional state; the same text sent final after a provisional one marks it final.
    /// </summary>
    public static void RecordSent(SessionInfo session, string prompt, long revision, long afterSeq, IReadOnlyList<ToolDefinition> tools, bool final = true)
    {
        if (Revision(session) != revision) return;
        session.Meta ??= new JsonObject();
        if (final)
        {
            session.Meta[FallbackKey] = prompt;
            session.Meta[FallbackRevisionKey] = revision;
        }
        if (session.Meta[HistoryKey] is not JsonArray history) session.Meta[HistoryKey] = history = [];
        if (history.LastOrDefault() is JsonObject last && last["prompt"]?.GetValue<string>() == prompt)
        {
            var provisional = last["provisional"]?.GetValue<bool>() == true;
            if (provisional == !final) return;
            if (final) { last.Remove("provisional"); return; }
        }
        var entry = new JsonObject
        {
            ["afterSeq"] = afterSeq, ["revision"] = revision, ["prompt"] = prompt,
            ["createdAt"] = DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            ["tools"] = new JsonArray([.. tools.Select(t =>
            {
                var tool = new JsonObject { ["name"] = t.Name, ["def"] = t.DefinitionHash };
                if (t.Revision is { } r) tool["revision"] = r;
                return (JsonNode?)tool;
            })]),
        };
        if (!final) entry["provisional"] = true;
        history.Add(entry);
    }

    /// <summary>
    /// Whether the final <paramref name="prompt"/> is already the session's recorded prefix for its current revision and the
    /// last distinct entry of its history: <see cref="RecordSent"/> would change nothing, so the send boundary may skip
    /// it (and the row rewrite and <c>session.updated</c> it brings).
    /// </summary>
    public static bool Recorded(SessionInfo session, string prompt) =>
        Fallback(session) == prompt
        && (session.Meta?[HistoryKey] as JsonArray)?.LastOrDefault() is JsonObject last
        && last["prompt"]?.GetValue<string>() == prompt
        && last["provisional"]?.GetValue<bool>() != true;

    /// <summary>The newest history entry, or null when nothing was recorded.</summary>
    public static SentPromptEntry? LastSent(SessionInfo session)
    {
        var history = History(session);
        return history.Count == 0 ? null : history[^1];
    }

    /// <summary>The prompts the session was sent, oldest first; <see cref="SentPromptEntry.Version"/> is the 1-based position.</summary>
    public static IReadOnlyList<SentPromptEntry> History(SessionInfo session)
    {
        if (session.Meta?[HistoryKey] is not JsonArray history) return [];
        var list = new List<SentPromptEntry>(history.Count);
        foreach (var node in history)
        {
            if (node is not JsonObject e) continue;
            var tools = new List<SentTool>();
            foreach (var t in (e["tools"] as JsonArray)?.OfType<JsonObject>() ?? [])
                if (Text(t["name"]) is { } name)
                    tools.Add(new SentTool(name, Text(t["def"]) ?? "", Text(t["revision"])));
            list.Add(new SentPromptEntry(list.Count + 1, Number(e["afterSeq"]), Number(e["revision"]), Text(e["prompt"]) ?? "",
                Text(e["createdAt"]), tools, e["provisional"]?.GetValue<bool>() == true));
        }
        return list;
    }
    private static string? Text(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    /// <summary>Copy only prefixes used at or before the fork point. Legacy sessions keep their known fallback.</summary>
    public static void Fork(JsonObject meta, long upToSeq)
    {
        if (meta[HistoryKey] is not JsonArray history) return;
        var sent = history.OfType<JsonObject>().Where(p => Number(p["afterSeq"]) <= upToSeq).ToList();
        meta[HistoryKey] = new JsonArray(sent.Select(p => p.DeepClone()).ToArray());
        if (sent.LastOrDefault() is { } selected)
        {
            meta[FallbackKey] = selected["prompt"]?.DeepClone();
            // A fork retains current profile setup; its inherited prefix belongs to its initial revision.
            meta[FallbackRevisionKey] = Number(meta[RevisionKey]);
        }
        else
        {
            meta.Remove(FallbackKey); meta.Remove(FallbackRevisionKey);
        }
    }
}

/// <summary>One prompt a session was sent (see <see cref="SessionPrompt.History"/>).</summary>
public sealed record SentPromptEntry(int Version, long AfterSeq, long Revision, string Prompt, string? CreatedAt, IReadOnlyList<SentTool> Tools, bool Provisional);

/// <summary>A tool that went with a sent prompt: its name, <see cref="ToolDefinition.DefinitionHash"/> and (MCP tools) revision.</summary>
public sealed record SentTool(string Name, string Def, string? Revision);
