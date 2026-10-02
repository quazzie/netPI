using System.Text.Json.Nodes;

namespace NetPI;

// The storage port: what the core and the plugins may ask of "the database", with no SQL and no hint of an engine in it.
// A provider (sqlite, memory, a future SQL Server) implements these interfaces in its own terms and passes the conformance
// suite (tests/NetPI.Storage.Tests), which is the contract in executable form. Nothing here may name an engine.

/// <summary>A storage provider fails in a way the caller should see (a corrupt store, a version it cannot open).</summary>
public sealed class StorageException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Opens the store. Chosen by the <c>storage.provider</c> setting (default <c>sqlite</c>) at startup only.</summary>
public interface IStorageProvider
{
    string Id { get; }

    /// <summary>Open (creating or upgrading as needed) the store, or throw <see cref="StorageException"/> with a message that says what to do.</summary>
    IStorage Open(StorageOpenOptions options);
}

public sealed class StorageOpenOptions
{
    /// <summary>The NetPI home folder. A file-based provider keeps its files here.</summary>
    public required string Home { get; init; }
    public required Microsoft.Extensions.Logging.ILogger Logger { get; init; }
    /// <summary>Provider settings live under <c>storage.&lt;id&gt;.*</c> (for example <c>storage.sqlite.path</c>).</summary>
    public required ISettings Settings { get; init; }
}

public interface IStorage : IDisposable
{
    string ProviderId { get; }

    /// <summary>Projects, sessions and messages.</summary>
    ISessionRepository Sessions { get; }

    /// <summary>Small string values the core keeps (UI state, remembered fork-reset keys).</summary>
    IKeyValueStore Values { get; }

    /// <summary>Each plugin's own data, by plugin id.</summary>
    IPluginDataStore Plugins { get; }

    IStorageSnapshot Snapshot { get; }

    /// <summary>
    /// The provider's one in-process <b>re-entrant</b> lock (a <see cref="System.Threading.Monitor"/>). Every
    /// <see cref="ISessionRepository.Atomic{T}"/> and every plugin transaction takes it, and the session service guards its
    /// in-memory (message-less) sessions with the same lock: two locks taken in two orders is the deadlock this port exists to
    /// remove. Nothing may call into a plugin while holding it, and nothing may take a plugin's own lock inside it.
    /// </summary>
    object Lock { get; }

    StorageInfo Info { get; }
}

/// <summary>What <c>app.info</c> and diagnostics say about the store. Everything but <see cref="Provider"/> may be null.</summary>
public sealed record StorageInfo(string Provider, string? Version = null, string? Location = null, long? SizeBytes = null);

public interface IKeyValueStore
{
    string? Get(string key);
    /// <summary>Set a value; null removes the key.</summary>
    void Set(string key, string? value);
}

/// <summary>
/// A consistent copy of the whole store, taken while it is in use. The provider writes whatever files it needs into the
/// directory; Backup lists them in the manifest next to <see cref="StorageInfo.Provider"/> so a restore knows which
/// provider reads them.
/// </summary>
public interface IStorageSnapshot
{
    /// <summary>Write a consistent snapshot into <paramref name="directory"/> (which exists and is empty); return the file names written, relative to it.</summary>
    IReadOnlyList<string> Write(string directory);
}

// ------------------------------------------------------------------ sessions

/// <summary>A message's identity without its parts, for work that only needs seq, role, flags and meta (fork bookkeeping).</summary>
public sealed record MessageStub(long Id, long Seq, MessageRole Role, bool Compacted, JsonObject? Meta);

/// <summary>
/// Persistence of projects, sessions and messages, and nothing else: events, transient (message-less) sessions, the
/// context cache, titles and fork rules belong to the session service in the kernel, which drives these primitives.
/// Times are stored with millisecond precision. Strings compare ordinally unless a method says otherwise.
/// </summary>
public interface ISessionRepository
{
    /// <summary>
    /// Run <paramref name="work"/> atomically under <see cref="IStorage.Lock"/>: all of it or none (an exception rolls back
    /// everything the work wrote). Re-entrant: a nested call joins the outer one. The repository passed in is this one.
    /// </summary>
    T Atomic<T>(Func<ISessionRepository, T> work);
    void Atomic(Action<ISessionRepository> work);

    // ---- projects

    /// <summary>Most recently used first (<c>LastUsedAt</c>, else <c>UpdatedAt</c>), then by name, case-insensitive for ASCII.</summary>
    IReadOnlyList<ProjectInfo> ListProjects();
    ProjectInfo? GetProject(string id);
    /// <summary>Insert a project with the id it carries; throws <see cref="StorageException"/> when the id exists.</summary>
    void InsertProject(ProjectInfo project);
    /// <summary>Write <c>Name</c>, <c>Path</c>, <c>UpdatedAt</c> and <c>Meta</c>; false when there is no such project.</summary>
    bool UpdateProject(ProjectInfo project);
    void TouchProject(string id, DateTimeOffset at);
    /// <summary>Remove the project row only. Sessions that named it are cleared first with <see cref="ClearProject"/>.</summary>
    bool DeleteProject(string id);
    /// <summary>Set <c>ProjectId</c> to null and <c>UpdatedAt</c> to <paramref name="at"/> on every session of the project; the count.</summary>
    int ClearProject(string projectId, DateTimeOffset at);

    // ---- sessions (persisted ones: a session without messages is not stored)

    /// <summary>
    /// Pinned first, then <c>UpdatedAt</c> descending, then id descending. <see cref="SessionQuery.Search"/> is a substring match on the title,
    /// case-insensitive for ASCII, with <c>%</c>, <c>_</c> and <c>\</c> taken literally. <see cref="SessionQuery.ProjectId"/> of "" means "no project".
    /// <see cref="SessionQuery.AttachedKey"/> keeps the sessions whose <c>Meta[key]</c> is the string <see cref="SessionQuery.AttachedValue"/>.
    /// <c>Limit</c> is clamped to 1..5000 (0 or less means 100).
    /// </summary>
    IReadOnlyList<SessionInfo> ListSessions(SessionQuery query);
    SessionInfo? GetSession(string id);
    /// <summary>Insert the whole row, counters included; throws <see cref="StorageException"/> when the id exists.</summary>
    void InsertSession(SessionInfo session);
    /// <summary>Write every field except <c>Id</c> and <c>CreatedAt</c>; false when there is no such session.</summary>
    bool UpdateSession(SessionInfo session);
    /// <summary>Delete the session, every session below it (by <c>ParentSessionId</c>, any depth) and all their messages. The ids, parents before children; empty when there was none.</summary>
    IReadOnlyList<string> DeleteSessionTree(string id);

    // ---- messages

    /// <summary>
    /// Store the message: <c>Id</c> is allocated from one sequence for the whole store (ids are never reused and never shared between
    /// sessions), <c>Seq</c> is the session's highest plus one (1 for the first). Both are set on <paramref name="message"/>, which is returned.
    /// </summary>
    ChatMessage AppendMessage(ChatMessage message);
    /// <summary>Bookkeeping of an append: <c>UpdatedAt</c> = at, <c>MessageCount</c> + 1, <c>Title</c> = title.</summary>
    void RecordAppend(string sessionId, DateTimeOffset at, string title);
    /// <summary>Rewrite role, parts, provider, model, stop reason, usage, duration, compacted and meta of the message with that id; false when missing.</summary>
    bool UpdateMessage(ChatMessage message);
    ChatMessage? GetMessage(long id);
    /// <summary>Ascending by seq. With <paramref name="limit"/> (and optionally <paramref name="beforeSeq"/>): the page of that many messages ending just before it, still ascending.</summary>
    IReadOnlyList<ChatMessage> GetMessages(string sessionId, long? beforeSeq = null, int? limit = null);
    /// <summary>The session's uncompacted messages ascending, and the highest uncompacted seq (0 when none), read as one consistent view.</summary>
    (IReadOnlyList<ChatMessage> Rows, long Newest) ReadContext(string sessionId);
    /// <summary>Mark every message of the session with <c>Seq</c> &lt;= <paramref name="upToSeq"/> compacted.</summary>
    void MarkCompacted(string sessionId, long upToSeq);
    /// <summary>Copy the messages with <c>Seq</c> &lt;= <paramref name="upToSeq"/> into another existing session: same seq, time, parts, usage, flags and meta, new ids. The count.</summary>
    int CopyMessages(string fromSessionId, string toSessionId, long upToSeq);
    IReadOnlyList<MessageStub> MessageStubs(string sessionId);
    /// <summary>Make exactly the messages with these seqs compacted and every other message of the session not.</summary>
    void SetCompacted(string sessionId, IReadOnlySet<long> compactedSeqs);
    void UpdateMessageMeta(long id, JsonObject? meta);
}

// ------------------------------------------------------------------ plugin data

/// <summary>The data of every plugin; <see cref="For"/> binds one plugin's view.</summary>
public interface IPluginDataStore
{
    /// <summary>The plugin's data. The same plugin id always sees the same collections, across hot reloads.</summary>
    IPluginData For(string pluginId);
}

public enum DataFieldType { Text, Integer, Real }

/// <summary>
/// A collection's declared <b>index fields</b>: the only fields a query may filter or order by. A field's value is read from the
/// document's top-level property of that name (missing or JSON null: no value). A provider applies a changed declaration
/// (a new field is back-filled from the stored documents).
/// </summary>
public sealed class CollectionSpec
{
    public Dictionary<string, DataFieldType> Fields { get; } = new(StringComparer.Ordinal);

    public CollectionSpec Text(string field) { Fields[field] = DataFieldType.Text; return this; }
    public CollectionSpec Integer(string field) { Fields[field] = DataFieldType.Integer; return this; }
    public CollectionSpec Real(string field) { Fields[field] = DataFieldType.Real; return this; }
}

public enum DataOp { Eq, Ne, Lt, Le, Gt, Ge, In, NotIn, IsNull, NotNull }

/// <summary>
/// One condition on an index field. <see cref="Value"/> is a string, long, double or bool (bool stores as 0/1 in an Integer field);
/// for <see cref="DataOp.In"/> and <see cref="DataOp.NotIn"/> an <c>IEnumerable</c> of those; ignored for <see cref="DataOp.IsNull"/>
/// and <see cref="DataOp.NotNull"/>. A comparison never matches a field with no value (including <c>Ne</c> and <c>NotIn</c>): ask for it with
/// <c>IsNull</c>.
/// </summary>
public sealed record DataFilter(string Field, DataOp Op, object? Value = null);

public sealed record DataOrder(string Field, bool Descending = false);

public sealed class DataQuery
{
    public List<DataFilter> Where { get; } = [];
    /// <summary>Applied in order; documents that tie keep key order (ascending, ordinal). A field with no value sorts first ascending, last descending.</summary>
    public List<DataOrder> OrderBy { get; } = [];
    public int? Limit { get; set; }
    public int Offset { get; set; }

    public DataQuery Eq(string field, object? value) { Where.Add(new DataFilter(field, DataOp.Eq, value)); return this; }
    public DataQuery Ne(string field, object? value) { Where.Add(new DataFilter(field, DataOp.Ne, value)); return this; }
    public DataQuery Lt(string field, object? value) { Where.Add(new DataFilter(field, DataOp.Lt, value)); return this; }
    public DataQuery Le(string field, object? value) { Where.Add(new DataFilter(field, DataOp.Le, value)); return this; }
    public DataQuery Gt(string field, object? value) { Where.Add(new DataFilter(field, DataOp.Gt, value)); return this; }
    public DataQuery Ge(string field, object? value) { Where.Add(new DataFilter(field, DataOp.Ge, value)); return this; }
    public DataQuery In(string field, IEnumerable<object?> values) { Where.Add(new DataFilter(field, DataOp.In, values)); return this; }
    public DataQuery NotIn(string field, IEnumerable<object?> values) { Where.Add(new DataFilter(field, DataOp.NotIn, values)); return this; }
    public DataQuery IsNull(string field) { Where.Add(new DataFilter(field, DataOp.IsNull)); return this; }
    public DataQuery NotNull(string field) { Where.Add(new DataFilter(field, DataOp.NotNull)); return this; }
    public DataQuery Order(string field, bool descending = false) { OrderBy.Add(new DataOrder(field, descending)); return this; }
    public DataQuery Take(int limit) { Limit = limit; return this; }
}

public sealed record DataDoc(string Key, JsonObject Doc);

/// <summary>
/// A named set of JSON documents, each under a string key (ordinal, case-sensitive). What comes back is the caller's own copy.
/// Calls outside <see cref="IPluginData.Transaction{T}"/> are each atomic.
/// </summary>
public interface IDataCollection
{
    string Name { get; }
    JsonObject? Get(string key);
    /// <summary>Insert or replace.</summary>
    void Put(string key, JsonObject doc);
    /// <summary>Insert only: false, and nothing written, when the key exists.</summary>
    bool Insert(string key, JsonObject doc);
    bool Delete(string key);
    /// <summary>The matching documents in the query's order. Filters and order may name index fields only (anything else throws <see cref="ArgumentException"/>).</summary>
    IReadOnlyList<DataDoc> Find(DataQuery? query = null);
    long Count(DataQuery? query = null);
    /// <summary>The sum of an Integer or Real index field over the matching documents (0 when none; documents with no value add nothing).</summary>
    double Sum(string field, DataQuery? query = null);
    /// <summary>Delete the matching documents (a query's limit and order are ignored); the count.</summary>
    int DeleteWhere(DataQuery query);
}

/// <summary>One plugin's data (<c>ctx.Data</c>).</summary>
public interface IPluginData
{
    /// <summary>
    /// Open a named collection with its index fields. Idempotent: the same name again returns the same collection and applies a
    /// changed <paramref name="spec"/>. Collection names are the plugin's own (two plugins may use the same name).
    /// </summary>
    IDataCollection Collection(string name, CollectionSpec spec);

    /// <summary>
    /// Run <paramref name="work"/> as one atomic unit over this plugin's collections: it sees its own writes, an exception rolls
    /// everything back, and no other transaction of the <b>same plugin id</b> runs at the same time — across hot-reload generations, so two
    /// versions of a plugin never interleave. Re-entrant on the same thread. This is the compare-and-set of the port: read, decide and
    /// write inside it. Do not call into the session store or another plugin's data from inside (take their locks first or not at all).
    /// </summary>
    T Transaction<T>(Func<T> work);
    void Transaction(Action work);
}
