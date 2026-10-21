using System.Text.Json.Nodes;

namespace NetPI.Compaction;

/// <summary>
/// The exact number of compactions a chat has had and the newest one's reading (the <c>compaction.stats</c> RPC).
/// Every compaction appends a summary message (role summary, meta.kind "compaction") that stays in the history even when a
/// later summary folds it in, so the count is those messages over the whole chat. The history is paged newest-first; the
/// answer is cached per session by the newest message seq, and a changed seq only reads the messages added since.
/// </summary>
public sealed class CompactionStats(IPluginContext ctx)
{
    public const int PageSize = 200;

    private sealed record Entry(long NewestSeq, int Count, JsonObject? Last);

    private readonly SessionState<Entry> _cache = new(ctx.Events);

    /// <summary>How many sessions hold a cached answer (tests).</summary>
    public int Cached => _cache.Count;

    public JsonObject Get(string sessionId)
    {
        var sessions = ctx.Sessions;
        var newest = sessions.GetMessages(sessionId, null, 1);
        if (newest.Count == 0) { _cache.Forget(sessionId); return Shape(0, null); }
        var top = newest[^1].Seq;

        _cache.TryGetValue(sessionId, out var cached);
        if (cached is not null && cached.NewestSeq == top) return Shape(cached.Count, cached.Last);
        // History only grows; a newest seq below the cached one means it was cut back (or replaced): count again.
        var stopAt = cached is not null && cached.NewestSeq < top ? cached.NewestSeq : 0;
        var count = stopAt > 0 ? cached!.Count : 0;
        var last = stopAt > 0 ? cached!.Last : null;

        JsonObject? found = null;
        long? before = null;
        while (true)
        {
            var page = sessions.GetMessages(sessionId, before, PageSize);
            if (page.Count == 0) break;
            var reachedKnown = false;
            for (var i = page.Count - 1; i >= 0; i--)
            {
                var m = page[i];
                if (m.Seq <= stopAt) { reachedKnown = true; break; }
                if (m.Role != MessageRole.Summary || m.Meta?["kind"]?.GetValue<string>() != "compaction") continue;
                count++;
                found ??= Reading(m);
            }
            if (reachedKnown || page.Count < PageSize) break;
            before = page[0].Seq;
        }
        last = found ?? last;
        _cache[sessionId] = new Entry(top, count, last);
        return Shape(count, last);
    }

    private static JsonObject Reading(ChatMessage m) => new()
    {
        ["tokensBefore"] = Num(m.Meta?["tokensBefore"]),
        ["tokensAfter"] = Num(m.Meta?["tokensAfter"]),
        ["mode"] = m.Meta?["mode"]?.GetValue<string>(),
        ["createdAt"] = m.CreatedAt,
        ["seq"] = m.Seq,
    };

    private static JsonNode? Num(JsonNode? n) => n is JsonValue v ? (v.TryGetValue<long>(out var l) ? l : v.TryGetValue<int>(out var i) ? i : v.TryGetValue<double>(out var d) ? (long)d : null) : null;

    private static JsonObject Shape(int count, JsonObject? last) => new() { ["count"] = count, ["last"] = last?.DeepClone() };
}
