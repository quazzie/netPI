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
    /// <summary>Record only distinct prefixes at the model send boundary, in session-owned durable JSON.</summary>
    public static void RecordSent(SessionInfo session, string prompt, long revision, long afterSeq)
    {
        if (Revision(session) != revision) return;
        session.Meta ??= new JsonObject();
        session.Meta[FallbackKey] = prompt;
        session.Meta[FallbackRevisionKey] = revision;
        if (session.Meta[HistoryKey] is not JsonArray history) session.Meta[HistoryKey] = history = [];
        if (history.LastOrDefault() is JsonObject last && last["prompt"]?.GetValue<string>() == prompt) return;
        history.Add(new JsonObject { ["afterSeq"] = afterSeq, ["revision"] = revision, ["prompt"] = prompt });
    }

    /// <summary>
    /// Whether <paramref name="prompt"/> is already the session's recorded prefix for its current revision and the
    /// last distinct entry of its history: <see cref="RecordSent"/> would change nothing, so the send boundary may skip
    /// it (and the row rewrite and <c>session.updated</c> it brings).
    /// </summary>
    public static bool Recorded(SessionInfo session, string prompt) =>
        Fallback(session) == prompt
        && (session.Meta?[HistoryKey] as JsonArray)?.LastOrDefault() is JsonObject last
        && last["prompt"]?.GetValue<string>() == prompt;

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
