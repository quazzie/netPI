using Microsoft.Extensions.Logging;

namespace NetPI.Host.Storage.Memory;

// The memory provider: the whole store in this process, and one file a snapshot writes. It exists for tests
// (the conformance suite runs on it) and for `storage.provider = "memory"` (--ephemeral), so it never writes to
// the home folder: the only file it ever produces is the one a snapshot is asked for.
//
// The collections it holds, in the order a snapshot writes them. A one-off migration tool is written from this
// block, so every field is named here.
//
//   projects   key: the project id (ordinal)
//              doc:  { id, name, path, createdAt, updatedAt, lastUsedAt|null, meta|null }
//              list: lastUsedAt ?? updatedAt descending, then name ascending (ASCII case-insensitive), then id
//
//   sessions   key: the session id (ordinal)
//              doc:  { id, title, projectId|null, parentSessionId|null, kind, model|null, reasoning|null,
//                     createdAt, updatedAt, archived, pinned, messageCount, contextTokens, meta|null }
//              list: pinned descending, updatedAt descending, id descending
//              (no workspace: the port has no workspace methods, and a binding is meta.workspaceId on the
//               session — the kernel's sessions.workspace_id column goes with it)
//
//   messages   key: the session id (ordinal) -> that session's messages, ascending by seq
//              doc:  { id, seq, role, parts, createdAt, provider|null, model|null, stopReason|null, usage|null,
//                     durationMs|null, compacted, meta|null }
//              ids come from nextMessageId, one counter for the whole store and never reused; seq is the
//              session's highest plus one. parts, usage and meta are the same JSON text the SQL columns hold.
//
//   values     key: the key (ordinal) -> the value string; a null value removes the key.
//
//   plugins    key: the plugin id (ordinal) -> the collection name (ordinal) -> { fields, docs }
//              fields: the declared index fields, name -> "Text" | "Integer" | "Real"
//              docs:   the document key (ordinal) -> the document as its own JSON
//              (a document is stored as written; a field it does not carry is a field with no value, which is
//               what a new index field reads as in the documents that were stored before it was declared)
//
// Times are Unix milliseconds; every other number is a JSON number.

/// <summary>
/// The <c>memory</c> storage provider: a store in this process only. Selected by <c>storage.provider = "memory"</c>,
/// which is how <c>--ephemeral</c> runs without a database file — a NetPI that loses its store on every start is
/// only ever asked for deliberately.
/// </summary>
internal sealed class MemoryStorageProvider : IStorageProvider
{
    public string Id => "memory";

    public IStorage Open(StorageOpenOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Home);
        ArgumentNullException.ThrowIfNull(options.Logger);
        options.Logger.LogDebug("Storage provider 'memory': in memory only, nothing is written under {Home}", options.Home);
        return new MemoryStorage();
    }
}

/// <summary>
/// A store in this process. Every operation runs under <see cref="Lock"/> — the one re-entrant lock the port hands
/// the kernel, which the session service takes for its message-less sessions too — and the rows a transaction
/// touches are remembered until it commits, so an exception puts them back.
/// </summary>
internal sealed class MemoryStorage : IStorage
{
    /// <summary>One lock for everything: the port's, taken by every operation and by every transaction.</summary>
    public object Lock { get; } = new();

    public string ProviderId => "memory";

    /// <summary>Nothing is written here, so there is no location, version or size to report.</summary>
    public StorageInfo Info { get; } = new("memory");

    public ISessionRepository Sessions { get; }
    public IKeyValueStore Values { get; }
    public IPluginDataStore Plugins { get; }
    public IStorageSnapshot Snapshot { get; }

    // ---- the collections (see the shape block at the top of this file)

    internal readonly Dictionary<string, ProjectRow> Projects = new(StringComparer.Ordinal);
    internal readonly Dictionary<string, SessionRow> SessionRows = new(StringComparer.Ordinal);
    /// <summary>Session id -> its messages, always ascending by seq.</summary>
    internal readonly Dictionary<string, List<MessageRow>> Messages = new(StringComparer.Ordinal);
    /// <summary>Message id -> the message, so <c>GetMessage</c> does not scan a session.</summary>
    internal readonly Dictionary<long, MessageRow> MessageById = [];
    internal readonly Dictionary<string, string> ValuesByKey = new(StringComparer.Ordinal);
    internal readonly Dictionary<string, PluginData> PluginsById = new(StringComparer.Ordinal);
    /// <summary>The next message id: one counter for the whole store, handed out in order and never reused.</summary>
    internal long NextMessageId = 1;

    /// <summary>The transaction in progress, innermost first.</summary>
    private readonly Stack<UndoLog> _undo = new();

    public MemoryStorage()
    {
        Sessions = new MemorySessionRepository(this);
        Values = new MemoryKeyValueStore(this);
        Plugins = new MemoryPluginDataStore(this);
        Snapshot = new MemorySnapshot(this);
    }

    /// <summary>There is nothing to release: no file, no connection, nothing outside this process.</summary>
    public void Dispose() { }

    /// <summary>
    /// Run <paramref name="work"/> as one atomic unit: an exception puts back every row it touched. Re-entrant, so a
    /// nested call runs inside the transaction already in progress and keeps a log of its own. The lock is taken
    /// first, which is what keeps the logs apart: a second thread waits for the lock instead of joining this one.
    /// </summary>
    internal T Atomic<T>(Func<T> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        lock (Lock)
        {
            // Every level keeps its own log, like a savepoint: a nested failure puts back what it wrote even when
            // the caller catches the exception and lets the outer one commit.
            var log = new UndoLog();
            _undo.Push(log);
            try { return work(); }
            catch
            {
                log.Rollback();
                throw;
            }
            finally { _undo.Pop(); }
        }
    }

    internal void Atomic(Action work)
    {
        ArgumentNullException.ThrowIfNull(work);
        Atomic<object?>(() => { work(); return null; });
    }

    /// <summary>
    /// A write: it joins the transaction in progress if there is one, and is an atomic unit of its own when there
    /// is not (which is what the port promises about a call made outside a transaction). Only an explicit
    /// <see cref="Atomic{T}"/> or <c>Transaction</c> starts a transaction of its own, so a write inside one can be
    /// taken back by the exception that ends it.
    /// </summary>
    internal T Apply<T>(Func<T> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        lock (Lock)
            return _undo.Count > 0 ? work() : Atomic(work);
    }

    internal void Apply(Action work)
    {
        ArgumentNullException.ThrowIfNull(work);
        lock (Lock) Apply<object?>(() => { work(); return null; });
    }

    /// <summary>
    /// A read: the lock and nothing else (no undo log, so a long list is not walked for nothing). A read inside a
    /// transaction sees that transaction's own writes, because the transaction still holds the lock.
    /// </summary>
    internal T Read<T>(Func<T> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        lock (Lock) return work();
    }

    /// <summary>
    /// Keep what a row holds now, so a rollback can put it back. The value is read <em>here</em>, before the write,
    /// and the undo is applied newest first: a row touched twice ends up with what it held before the first touch.
    /// </summary>
    internal void Touch<T>(Func<T?> read, Action<T?> restore) where T : class
    {
        if (_undo.Count == 0) return;
        _undo.Peek().Keep(read, restore);
    }

    /// <summary>The same for the one counter in the store.</summary>
    internal void TouchCounter(Func<long> read, Action<long> restore)
    {
        if (_undo.Count == 0) return;
        _undo.Peek().Keep(read, restore);
    }

    // ---- messages, the two collections that have to agree

    internal IReadOnlyList<MessageRow> MessagesOf(string sessionId) =>
        Messages.TryGetValue(sessionId, out var rows) ? rows : [];

    /// <summary>The session's messages with <paramref name="seq"/> &lt;= <paramref name="upToSeq"/>, in seq order.</summary>
    internal List<MessageRow> MessagesUpTo(string sessionId, long upToSeq)
    {
        var list = new List<MessageRow>();
        foreach (var row in MessagesOf(sessionId))
            if (row.Seq <= upToSeq) list.Add(row);
        return list;
    }

    /// <summary>Add a message, keeping the session's list in seq order and the by-id index in step.</summary>
    internal void StoreMessage(MessageRow row)
    {
        Touch(() => (MessageRow?)MessageById.GetValueOrDefault(row.Id), was => { if (was is null) MessageById.Remove(row.Id); else MessageById[row.Id] = was; });
        if (!Messages.TryGetValue(row.SessionId, out var rows))
        {
            var fresh = new List<MessageRow>();
            Touch(() => (List<MessageRow>?)null, was => { if (was is null) Messages.Remove(row.SessionId); else Messages[row.SessionId] = was; });
            rows = fresh;
            Messages[row.SessionId] = rows;
        }
        else
        {
            // a new list, so the one a reader may still be walking is never edited under it
            var found = rows;
            Touch(() => (List<MessageRow>?)found, was => { if (was is not null) Messages[row.SessionId] = was; });
            rows = new List<MessageRow>(found);
            Messages[row.SessionId] = rows;
        }
        var at = rows.FindIndex(r => r.Id == row.Id);
        if (at < 0) rows.Add(row);
        else rows[at] = row;
        rows.Sort(static (a, b) => a.Seq.CompareTo(b.Seq));   // seq is unique inside a session, so this is a total order
        MessageById[row.Id] = row;
    }

    /// <summary>Drop a session's messages (the whole list) — a tree delete, not a per-message removal.</summary>
    internal void DropMessages(string sessionId)
    {
        if (!Messages.TryGetValue(sessionId, out var rows)) return;
        Touch(() => (List<MessageRow>?)rows, was => { if (was is not null) Messages[sessionId] = was; });
        foreach (var row in rows)
            Touch(() => (MessageRow?)MessageById.GetValueOrDefault(row.Id), was => { if (was is null) MessageById.Remove(row.Id); else MessageById[row.Id] = was; });
        Messages.Remove(sessionId);
    }

    /// <summary>The undo log of one transaction: what every row it touched held when it touched it.</summary>
    private sealed class UndoLog
    {
        private readonly List<Action> _entries = [];

        public void Keep<T>(Func<T?> read, Action<T?> restore) where T : class
        {
            var before = read();
            _entries.Add(() => restore(before));
        }

        public void Keep(Func<long> read, Action<long> restore)
        {
            var before = read();
            _entries.Add(() => restore(before));
        }

        public void Rollback()
        {
            for (var i = _entries.Count - 1; i >= 0; i--) _entries[i]();
        }
    }
}
