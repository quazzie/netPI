using NetPI.Host.Sessions;

namespace NetPI.Host.Tests;

/// <summary>
/// The session context cache on its own (no store, no database): what it keeps, what it refuses to keep, and what
/// it counts — the LRU, the append that extends, the drop that poisons a read that started before it (idea-5zb8w4).
/// </summary>
public static class ContextCacheTests
{
    private static ChatMessage Msg(string id, long seq) => new()
    {
        SessionId = id, Seq = seq, Role = MessageRole.User, Parts = [new TextPart { Text = "m" + seq }],
    };

    private static List<ChatMessage> Rows(string id, params long[] seqs) => seqs.Select(s => Msg(id, s)).ToList();

    private static List<ChatMessage> Rows(string id, int count) =>
        Enumerable.Range(1, count).Select(i => Msg(id, i)).ToList();

    /// <summary>The way the service reads: begin it (counts, captures the generation), "read the store", fill what the store said.</summary>
    private static void Read(ContextCache cache, string id, List<ChatMessage> storeRows, long storeNewest)
    {
        var generation = cache.BeginRead(id);
        cache.Fill(id, storeRows, storeNewest, generation);
    }

    public static void Register(TestRunner r)
    {
        r.Add("context cache: a miss reads and fills, the next read is a hit; a hit hands out a copy", () =>
        {
            var cache = new ContextCache();
            Read(cache, "s", Rows("s", 1, 2, 3), 3);
            var (hits0, reads0) = cache.Counters;
            Check.Equal(0L, hits0);
            Check.Equal(1L, reads0);
            var rows = cache.TryGet("s")!;
            Check.Equal("m1,m2,m3", string.Join(",", rows.Select(m => m.Text)));
            var (hits1, reads1) = cache.Counters;
            Check.Equal(1L, hits1, "the second read is served from the cache");
            Check.Equal(1L, reads1, "and cost no read");

            // The caller's list is its own: the cache hands out a copy, in storage order.
            rows.Reverse();
            rows.Clear();
            Check.Equal(3, cache.TryGet("s")!.Count, "reordering or emptying the returned list changes nothing");
        });

        r.Add("context cache: a committed append extends the warm context instead of dropping it", () =>
        {
            var cache = new ContextCache();
            Read(cache, "s", Rows("s", 1, 2, 3), 3);
            cache.Append(Msg("s", 4));
            var rows = cache.TryGet("s")!;
            Check.Equal("m1,m2,m3,m4", string.Join(",", rows.Select(m => m.Text)), "the append is in the cached context");
            var (hits, reads) = cache.Counters;
            Check.Equal(1L, hits);
            Check.Equal(1L, reads, "the append cost no read");

            // Nothing to extend, no effect: the unknown session, a compacted message.
            cache.Append(Msg("other", 1));
            cache.Append(new ChatMessage { SessionId = "s", Seq = 9, Compacted = true, Parts = [new TextPart { Text = "c" }] });
            Check.Equal("m1,m2,m3,m4", string.Join(",", cache.TryGet("s")!.Select(m => m.Text)), "and neither touched the cache");
        });

        r.Add("context cache: an append that landed before the end of the cache drops the entry", () =>
        {
            var cache = new ContextCache();
            Read(cache, "s", Rows("s", 1, 2, 3), 3);
            cache.Append(Msg("s", 2));   // the seq the cache already passed: the order the model sees would be wrong
            Check.True(cache.TryGet("s") is null, "the entry is dropped and the next read rebuilds it");
            cache.Append(Msg("s", 4));   // nothing left to extend
            Check.True(cache.TryGet("s") is null);
        });

        r.Add("context cache: a read that starts before a drop does not fill with its pre-drop rows", () =>
        {
            var cache = new ContextCache();
            Read(cache, "s", Rows("s", 1, 2), 2);
            Check.True(cache.TryGet("s") is not null);

            // A read begins, a write that changes a message commits (the drop), and the read's rows are the pre-write ones.
            var generation = cache.BeginRead("s");
            cache.Drop("s");
            cache.Fill("s", Rows("s", 1, 2), 2, generation);
            Check.True(cache.TryGet("s") is null, "the stale fill is refused (the seq compare would not see it)");

            // A read that finishes after the drop, on the store's post-drop view, does fill.
            Read(cache, "s", Rows("s", 1, 2, 3), 3);
            Check.True(cache.TryGet("s") is not null);
        });

        r.Add("context cache: a read that missed a message that committed while it ran is not cached", () =>
        {
            var cache = new ContextCache();
            var generation = cache.BeginRead("s");
            cache.Fill("s", Rows("s", 1, 2), 3, generation);   // the store's newest moved past the rows it read
            Check.True(cache.TryGet("s") is null, "no cache of a view without the newest message");

            // The empty view: cached when nothing is there, not when a message is.
            cache.Fill("e", [], 0, cache.BeginRead("e"));
            Check.True(cache.TryGet("e") is { Count: 0 });
            cache.Fill("n", [], 1, cache.BeginRead("n"));
            Check.True(cache.TryGet("n") is null);
        });

        r.Add("context cache: a context bigger than the bound is read but never kept", () =>
        {
            var cache = new ContextCache();
            Read(cache, "big", Rows("big", 2001), 2001);
            Check.True(cache.TryGet("big") is null, "too big to be worth retaining");
            Read(cache, "edge", Rows("edge", 2000), 2000);
            Check.True(cache.TryGet("edge") is not null, "the bound itself is kept");

            // And an append that would pass it does not: the entry goes.
            cache.Append(Msg("edge", 2001));
            Check.True(cache.TryGet("edge") is null, "an append beyond the bound drops the context");
        });

        r.Add("context cache: the few most recent sessions are kept, the oldest is evicted", () =>
        {
            var cache = new ContextCache();
            Read(cache, "s1", Rows("s1", 1), 1);
            Read(cache, "s2", Rows("s2", 1), 1);
            Read(cache, "s3", Rows("s3", 1), 1);
            Check.Equal(1, cache.TryGet("s1")!.Count, "s1 is the most recent now");
            Read(cache, "s4", Rows("s4", 1), 1);
            Check.True(cache.TryGet("s1") is not null, "the just-read one is the most recent and stays");
            Check.True(cache.TryGet("s3") is not null);
            Check.True(cache.TryGet("s4") is not null);
            Check.True(cache.TryGet("s2") is null, "the oldest of the four is out");
        });

        r.Add("context cache: a drop forgets the entry, a forget lets the generation go with it", () =>
        {
            var cache = new ContextCache();
            Read(cache, "s", Rows("s", 1), 1);
            cache.Drop("s");
            var afterDrop = cache.BeginRead("s");
            cache.Fill("s", Rows("s", 1), 1, afterDrop - 1);
            Check.True(cache.TryGet("s") is null, "the generation the drop bumped refuses the old read");

            Read(cache, "gone", Rows("gone", 1), 1);
            cache.Forget("gone");
            var fresh = cache.BeginRead("gone");
            Check.Equal(0L, fresh, "no generation survives the forget");
            cache.Fill("gone", Rows("gone", 1), 1, fresh);
            Check.True(cache.TryGet("gone") is not null);
        });
    }
}
