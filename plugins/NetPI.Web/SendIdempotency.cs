using System.Collections.Concurrent;

namespace NetPI.Web;

/// <summary>
/// astra-1 §11a (F/A): idempotent chat.send. The browser's requestId only
/// correlates replies and expires after a client timeout, so it does NOT
/// establish durable command identity. A send can be accepted while its
/// acknowledgement is lost, and a user retry would otherwise start duplicate
/// work (a second message plus a second run). A client-generated operationId
/// names the operation; accepting it here means a retry of the SAME id replays
/// the existing result/status instead of appending another message or executing
/// another run. A deliberate repeat uses a NEW id (even for identical text).
///
/// The table is bounded (evicts the oldest) and is per host instance. It is
/// stamped at run ACCEPTANCE (before the ack is delivered), so a disconnect
/// between acceptance and acknowledgement still lets the retry replay.
/// </summary>
public sealed class SendIdempotency
{
    /// <summary>The accepted outcome of a send: which session it landed in, the
    /// original text it carried (so a replay echoes the ORIGINAL bytes, not a
    /// mutated retry), and the status reported to the client.</summary>
    public sealed record Accepted(string SessionId, string Text, string Status, DateTimeOffset At);

    private readonly ConcurrentDictionary<string, Entry> _byId = new();
    private readonly int _maxEntries;
    private readonly object _gate = new();
    private long _nextSeq = 1;

    public SendIdempotency(int maxEntries = 1024) => _maxEntries = maxEntries <= 0 ? 1024 : maxEntries;

    private sealed class Entry(Accepted accepted, long seq)
    {
        public readonly Accepted Accepted = accepted;
        public readonly long Seq = seq;
    }

    /// <summary>Returns the accepted result for <paramref name="id"/> if this operation
    /// has already been accepted, otherwise null.</summary>
    public Accepted? TryGet(string id)
        => _byId.TryGetValue(id, out var e) ? e.Accepted : null;

    /// <summary>
    /// Records that <paramref name="id"/> was accepted with <paramref name="result"/>.
    /// Idempotent: if the id is already recorded, the FIRST accepted result wins and
    /// is returned unchanged (a late/duplicate accept never overwrites the outcome a
    /// replay already observed). Evicts the oldest entry once the cap is exceeded.
    /// </summary>
    public Accepted Accept(string id, Accepted result)
    {
        if (_byId.TryGetValue(id, out var existing))
            return existing.Accepted;

        lock (_gate)
        {
            _byId[id] = new Entry(result, _nextSeq++);
            EvictIfOverCap();
        }
        return result;
    }

    /// <summary>Removes an id (used if acceptance later fails and a retry is allowed).
    /// No-op if absent.</summary>
    public void Release(string id) => _byId.TryRemove(id, out _);

    public int Count => _byId.Count;

    private void EvictIfOverCap()
    {
        if (_byId.Count <= _maxEntries) return;
        // Caller holds _gate. Drop the oldest by insertion sequence.
        var oldestId = _byId
            .OrderBy(e => e.Value.Seq)
            .Select(e => e.Key)
            .First();
        _byId.TryRemove(oldestId, out _);
    }
}
