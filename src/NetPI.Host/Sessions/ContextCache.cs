namespace NetPI.Host.Sessions;

/// <summary>
/// The deserialized context of the sessions being worked on. Without it every turn re-reads and re-parses the whole
/// history (measured: ~30 ms and megabytes of garbage for a 1,500-message chat), and does so again for every hook
/// that appends a notice. Only the few most recent sessions are kept, and a session too big to be worth retaining
/// is never cached at all.
/// </summary>
internal sealed class ContextCache
{
    /// <summary>The few most recent sessions whose context is kept.</summary>
    private const int Sessions = 3;

    /// <summary>A context bigger than this is never cached: the read it saves is not worth the memory.</summary>
    private const int MaxMessages = 2000;

    /// <summary>A session's uncompacted messages in storage order, and the seq they end at.</summary>
    private sealed record Entry(List<ChatMessage> Rows, long LastSeq);

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _lru = new();

    /// <summary>
    /// A generation per cached session, bumped by every drop: a cold read captures its value before it reads
    /// storage, and may fill the slot only if nothing dropped in the meantime. That is the check the seq compare
    /// cannot give: rows and newest are read as two separate statements, and a write that changes a message in
    /// place (or compacts) commits between them with the seq still matching, leaving pre-write rows in the cache.
    /// </summary>
    private readonly Dictionary<string, long> _generation = new(StringComparer.Ordinal);

    private readonly object _lock = new();
    private long _hits, _reads;

    /// <summary>
    /// A read that will miss the cache and go to storage: it counts, and returns the session's generation — the
    /// read may fill the slot only if nothing dropped in the meantime (see <see cref="Fill"/>).
    /// </summary>
    public long BeginRead(string sessionId)
    {
        Interlocked.Increment(ref _reads);
        lock (_lock) return _generation.GetValueOrDefault(sessionId);
    }

    /// <summary>The session's cached context (a hit), copied for the caller — or null. The copy is what a warm read costs.</summary>
    public List<ChatMessage>? TryGet(string sessionId)
    {
        lock (_lock)
        {
            if (!_entries.TryGetValue(sessionId, out var entry)) return null;
            Interlocked.Increment(ref _hits);
            Touch(sessionId);
            return [.. entry.Rows];
        }
    }

    /// <summary>
    /// A cold read's result, with the newest seq the store reported in the same read. The slot is filled only when
    /// the result may be the newest: it is not bigger than the cache can keep, it ends at the newest seq (a message
    /// that committed while the read ran would otherwise be missing from the cache with nothing left to drop it —
    /// an append that finds no entry has nothing to extend), and nothing dropped or filled since the read began
    /// (an append that ran alongside already extended the slot, and it knows more).
    /// </summary>
    public void Fill(string sessionId, List<ChatMessage> rows, long newest, long generation)
    {
        if (rows.Count > MaxMessages) return;
        if (rows.Count == 0 ? newest != 0 : rows[^1].Seq != newest) return;
        lock (_lock)
        {
            if (generation != _generation.GetValueOrDefault(sessionId) || _entries.ContainsKey(sessionId)) return;
            _entries[sessionId] = new Entry(rows, newest);
            _lru.AddLast(sessionId);
            while (_lru.Count > Sessions)
            {
                _entries.Remove(_lru.First!.Value);
                _lru.RemoveFirst();
            }
        }
    }

    /// <summary>
    /// A committed append goes INTO the cached context instead of dropping it, because dropping it is what made
    /// the turn-start read a guaranteed miss: a turn appends its own messages, so every turn paid the full read
    /// again. Only an append that lands after the last cached seq can be added: two appends to one session can
    /// commit in either order (a plugin's notice and the runtime's own message), and a message that arrives out of
    /// order would corrupt the order the model sees — that case drops the entry and the next read rebuilds it. A
    /// message stored as already compacted does not belong in this list at all.
    /// </summary>
    public void Append(ChatMessage message)
    {
        if (string.IsNullOrEmpty(message.SessionId) || message.Compacted) return;
        lock (_lock)
        {
            if (!_entries.TryGetValue(message.SessionId, out var entry)) return;
            if (message.Seq <= entry.LastSeq || entry.Rows.Count >= MaxMessages)
            {
                _entries.Remove(message.SessionId);
                _lru.Remove(message.SessionId);
                return;
            }
            entry.Rows.Add(message);
            _entries[message.SessionId] = entry with { LastSeq = message.Seq };
            Touch(message.SessionId);
        }
    }

    /// <summary>Forget a session's cached context. Every write that changes a message calls it; a read that started before the drop may no longer fill (its generation is stale).</summary>
    public void Drop(string sessionId)
    {
        lock (_lock)
        {
            if (_entries.Remove(sessionId)) _lru.Remove(sessionId);
            _generation[sessionId] = _generation.GetValueOrDefault(sessionId) + 1;
        }
    }

    /// <summary>A session that is going away: forget its cached context and let its generation go with it.</summary>
    public void Forget(string sessionId)
    {
        lock (_lock)
        {
            if (_entries.Remove(sessionId)) _lru.Remove(sessionId);
            _generation.Remove(sessionId);
        }
    }

    private void Touch(string sessionId)
    {
        var node = _lru.Find(sessionId);
        if (node is null) return;
        _lru.Remove(node);
        _lru.AddLast(node);
    }

    /// <summary>Times the cached context answered a read; the rest went to storage. Diagnostics and tests.</summary>
    public (long Hits, long Reads) Counters => (Interlocked.Read(ref _hits), Interlocked.Read(ref _reads));
}
