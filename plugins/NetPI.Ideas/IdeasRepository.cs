using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace NetPI.Ideas;

/// <summary>
/// The backlog collections in <c>ctx.Data</c> (plugin id "netpi.ideas"): JSON documents under a string key, with
/// declared index fields — the only fields a query may filter or order by. The layout, in step with the code:
/// <code>
///   items        key: the idea id
///                doc: the idea's own JSON fields (the complete document it always was) + revision (long) +
///                     ord (long) + idLower (the key, ASCII-lower-cased) + projectId (only when bound to a project)
///                index: ord Integer, status Text, projectId Text, idLower Text
///   imports      key: the import id
///                doc: kind, source, checksum, schemaVersion (long), counts (object), at
///                index: none
/// </code>
/// An absent document property is the same as a NULL column the old tables had: no value, not matched by a
/// comparison. A field the queries order or filter on is an index field and is always written when it has a value.
/// </summary>

/// <summary>
/// The backlog changed under a writer: the idea it read is not the one it wanted to write (optimistic concurrency).
/// The caller turns this into a <c>conflict</c>; the change the caller had in hand is its own, and the UI keeps it.
/// </summary>
public sealed class IdeasConflictException(string message) : Exception(message);

/// <summary>
/// An idea as it is stored, with the revision it is at. A class on purpose: a document that is not there has to be
/// distinguishable from one whose fields happen to be null, which a value tuple cannot express.
/// </summary>
public sealed record IdeaRow(JsonObject Doc, long Revision);

/// <summary>
/// The Ideas backlog in the store's plugin data: one collection per table, one repository, one write path, one
/// transaction per operation.
/// <para>
/// An idea is one document: a stable id (the key), a monotonically increasing <c>revision</c>, the manual <c>ord</c>
/// (the order the user arranged) and the complete idea JSON, exactly as the JSON backlog held it, with every field
/// this build does not know about. <c>ord</c>, <c>idLower</c> and <c>projectId</c> are the index fields, written at
/// the top level of the same document in the same operation, so they cannot drift apart from the document they
/// describe. An idea is still only ever understood as the document it always was (<see cref="IdeaOps"/> works on
/// <see cref="JsonObject"/>s), which is how a field another tool wrote by hand survives a save.
/// </para>
/// <para>
/// Every method is short and synchronous. A transaction is held for the length of a few calls and never across a
/// model call, a file read or anything else that can block.
/// </para>
/// </summary>
public sealed class IdeasRepository
{
    /// <summary>The scope name: what <c>ideas.list</c> and an export say the backlog's data lives under.</summary>
    public const string Scope = "netpi.ideas";

    /// <summary>
    /// The storage shape this build writes. A newer shape must be read by a newer build: a revision is carried on
    /// every idea, and a patch that does not know a field leaves it exactly as it was.
    /// </summary>
    public const int SchemaVersion = 2;

    private readonly IPluginData _data;
    private readonly IStorageAccess? _storage;
    private readonly ILogger? _log;
    private readonly string? _home;
    private readonly IDataCollection _items, _imports;

    /// <summary>
    /// Announced after a write has committed (never before, and never for one that rolled back), so every window
    /// re-reads the canonical state. The plugin sets this to publish <c>ideas.changed</c>; the repository itself knows
    /// nothing about events.
    /// </summary>
    public Action<string>? OnChanged { get; set; }

    private void Announce(string reason) => OnChanged?.Invoke(reason);

    private IdeasRepository(IPluginData data, IStorageAccess? storage, ILogger? log, string? home)
    {
        _data = data;
        _storage = storage;
        _log = log;
        _home = home;
        // The same plugin id sees the same collections across hot-reload generations, so a swap shares one backlog
        // instead of two backends (the old code guaranteed the same with one set of tables).
        _items = data.Collection("items", new CollectionSpec().Text("idLower").Integer("ord").Text("status").Text("projectId"));
        _imports = data.Collection("imports", new CollectionSpec());
    }

    /// <summary>
    /// Open the backlog: the collections are created on first ask (idempotent, per plugin id). Called once per plugin
    /// start.
    /// </summary>
    public static IdeasRepository Open(IPluginData data, IStorageAccess? storage = null, ILogger? log = null, string? home = null)
        => new(data, storage, log, home);

    /// <summary>
    /// Where this backlog lives, for <c>ideas.list</c> and the UI: the store the collections are in, not a file.
    /// <c>backend</c> and <c>database</c> come from the store's own info (the kernel's <see cref="IStorageAccess"/>);
    /// the rest is what this plugin owns.
    /// </summary>
    public JsonObject Storage()
    {
        var info = _storage?.Info;
        return new JsonObject
        {
            ["backend"] = info?.Provider,
            ["database"] = info?.Location is { } location ? Path.GetFileName(location) : null,
            ["scope"] = Scope,
            ["schemaVersion"] = SchemaVersion,
            ["editableFile"] = false,
            ["importExport"] = "json",
        };
    }

    /// <summary>
    /// Run several of these writes as one transaction. The import uses it: the ideas and the receipt that accounts for
    /// them commit together, or neither does.
    /// </summary>
    public T Transaction<T>(Func<IdeasRepository, T> work) => _data.Transaction(() => work(this));

    // ------------------------------------------------------------------ ideas

    /// <summary>Every idea as it is stored, in the user's order (each with the revision an editor submits back).</summary>
    public List<JsonObject> All() =>
        _items.Find(new DataQuery().Order("ord")).Select(d => Read(d.Doc)).ToList();

    public int Count() => (int)_items.Count();

    /// <summary>The ids in the backlog, for a new id that must not collide with one of them.</summary>
    public List<string> TakenIds() => _items.Find().Select(d => d.Key).ToList();

    /// <summary>
    /// One idea and its revision, or null. The lookup is as forgiving as it has always been: exact, then with the
    /// "idea-" prefix, then case-insensitive — an id typed by hand or carried over from an old file still finds its
    /// idea, and a legacy id that is not of our making is stored and found exactly as it is.
    /// </summary>
    public IdeaRow? Find(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        var want = id.Trim();
        var row = ReadItem_(want) ?? ReadItem_("idea-" + want);
        if (row is null)
        {
            // The case-insensitive halves: the id as typed, or with the prefix. idLower is ASCII-folded like the old
            // lower() was, and compared ordinally, so the two folds agree.
            var byId = _items.Find(new DataQuery().Eq("idLower", Lower(want)));
            if (byId.Count > 0) return new IdeaRow(Read(byId[0].Doc), Rev(byId[0].Doc));
            var byPref = _items.Find(new DataQuery().Eq("idLower", Lower("idea-" + want)));
            if (byPref.Count > 0) return new IdeaRow(Read(byPref[0].Doc), Rev(byPref[0].Doc));
        }
        return row;
    }

    /// <summary>One idea as it is stored, or null — for a caller that does not care about the revision.</summary>
    public JsonObject? Idea(string? id) => Find(id)?.Doc;

    /// <summary>
    /// Store a new idea at the end of the backlog (or the top of it) and return it with its revision. An idea comes
    /// in here as a whole document — <c>ideas.import</b> brings in documents this host did not write — so its images
    /// are sanitized here, where the home is known, and a stored reference can never name a file outside the images
    /// directory (idea-w6v48t).
    /// </summary>
    public (JsonObject Doc, long Revision) Add(JsonObject idea, bool prepend = false)
    {
        var added = _data.Transaction(() =>
        {
            var doc = (JsonObject)idea.DeepClone();
            var id = IdeaOps.Str(doc["id"]);
            if (string.IsNullOrWhiteSpace(id)) throw new IdeaInputException("An idea needs an id.");
            if (Find(id) is not null) throw new IdeasConflictException($"Idea {id} is already in the backlog.");
            if (doc["images"] is { } images) doc["images"] = IdeaImages.Sanitize(_home, images);
            PutItem_(id, doc, prepend ? MinOrd() : MaxOrd(), 1);
            return (WithRevision(doc, 1), 1L);
        });
        Announce("add");
        return added;
    }

    /// <summary>
    /// Apply a patch and store the result, or store nothing at all. The patch runs on a detached copy, so a patch that
    /// fails validation (an empty title, an unknown status) leaves neither the store nor any later writer's copy of
    /// the idea changed. <paramref name="expectedRevision"/> is the optimistic-concurrency check; without it the write is
    /// last-writer-wins, which is what the agent tool does.
    /// </summary>
    public (JsonObject Doc, long Revision, List<string> Changes) Update(
        string id, JsonObject patch, bool fromUi, string? sessionId = null, long? expectedRevision = null, string? expectedUpdatedAt = null)
    {
        var removedImages = new List<string>();
        var result = _data.Transaction(() =>
        {
            var current = Find(id) ?? throw new RpcException("not_found", $"Idea {id} not found");
            var storedId = IdeaOps.Str(current.Doc["id"]) ?? id;
            if (expectedRevision is { } want && want != current.Revision)
                throw new IdeasConflictException(
                    $"Idea {storedId} changed since you read it (it is at revision {current.Revision}, your copy is {want}). " +
                    "Reload it and apply your change again.");
            // Kept for callers that only know the old check. It compares a second-precision timestamp, so two changes in
            // the same second are not a conflict: prefer expectedRevision wherever it is available.
            if (expectedRevision is null && expectedUpdatedAt is { Length: > 0 } stamp && IdeaOps.Str(current.Doc["updatedAt"]) != stamp)
                throw new IdeasConflictException(
                    $"Idea {storedId} changed since {stamp} (it is now {IdeaOps.Str(current.Doc["updatedAt"]) ?? "untouched"}). Reload it and apply your change again.");

            var next = (JsonObject)current.Doc.DeepClone();
            var changes = IdeaOps.ApplyPatch(next, patch, fromUi, sessionId, _home);
            if (changes.Count == 0) return (current.Doc, current.Revision, changes); // nothing changed: no write, no revision
            if (changes.Contains("images")) removedImages = ImagesLeaving(current.Doc, next);
            var revision = current.Revision + 1;
            PutItem_(storedId, next, OrdOf(storedId), revision);
            return (WithRevision(next, revision), revision, changes);
        });
        if (result.Item3.Count > 0)
        {
            Announce("update"); // a patch that changed nothing is not a change
            // The files an idea no longer references leave the disk only once the write has committed: an update
            // refused as stale (or rolled back) leaves the document and every file exactly where they were
            // (idea-qpaghc). A reference that is not a stored image is left alone by the delete itself.
            if (_home is { } home) foreach (var gone in removedImages) IdeaImages.Delete(home, gone);
        }
        return result;
    }

    /// <summary>The image references a document no longer carries — the files on disk that should follow them out.</summary>
    private static List<string> ImagesLeaving(JsonObject before, JsonObject after)
    {
        var kept = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in (after["images"] as JsonArray ?? []).OfType<JsonObject>()
            .Select(o => IdeaOps.Str(o["path"])))
            if (path is { Length: > 0 }) kept.Add(path);
        var leaving = new List<string>();
        foreach (var path in (before["images"] as JsonArray ?? []).OfType<JsonObject>()
            .Select(o => IdeaOps.Str(o["path"])))
            if (path is { Length: > 0 } && !kept.Contains(path)) leaving.Add(path);
        return leaving;
    }

    public bool Delete(string? id)
    {
        var deleted = _data.Transaction(() =>
        {
            if (Find(id) is not { } found) return false;
            // A document stored under a key it does not name has no id to delete by (the old WHERE id = NULL matched nothing).
            if (IdeaOps.Str(found.Doc["id"]) is not { Length: > 0 } storedId) return false;
            return _items.Delete(storedId);
        });
        if (deleted) Announce("delete");
        return deleted;
    }

    /// <summary>
    /// The user's order: the listed ideas first, in the order given, the rest where they were. Ids that are not ideas
    /// (a stale window) are ignored, and a reorder that names only part of the backlog never drops the ideas it did
    /// not name.
    /// </summary>
    public void Reorder(IEnumerable<string> ids)
    {
        var wanted = ids.Where(i => !string.IsNullOrWhiteSpace(i)).Select(i => i.Trim()).ToList();
        _data.Transaction(() =>
        {
            var stored = _items.Find(new DataQuery().Order("ord")).Select(d => d.Key).ToList();
            var ordered = new List<string>();
            foreach (var want in wanted)
            {
                var real = stored.FirstOrDefault(k => string.Equals(k, want, StringComparison.Ordinal))
                           ?? stored.FirstOrDefault(k => string.Equals(k, "idea-" + want, StringComparison.OrdinalIgnoreCase))
                           ?? stored.FirstOrDefault(k => string.Equals(k, want, StringComparison.OrdinalIgnoreCase));
                if (real is not null && !ordered.Contains(real)) ordered.Add(real);
            }
            foreach (var id in stored)
                if (!ordered.Contains(id)) ordered.Add(id);
            for (var i = 0; i < ordered.Count; i++)
            {
                var doc = _items.Get(ordered[i]);
                if (doc is null) continue; // gone under a writer: leave the rest as they are
                PutItem_(ordered[i], Read(doc), i, Rev(doc));
            }
            return 0;
        });
        Announce("reorder");
    }

    /// <summary>
    /// The open ideas of a project plus the unbound ones (or, with <paramref name="unboundOnly"/>, only the unbound
    /// ones), in backlog order — what the commit notice names.
    /// A document without a status is open: the old table gave it a default column, and a comparison never matches a
    /// field with no value, so the "open" side of the filter is the NotIn and the IsNull together.
    /// </summary>
    public List<JsonObject> OpenIdeas(string? projectId, bool unboundOnly = false)
    {
        var rows = new List<DataDoc>();
        void AddFor(string? project)
        {
            foreach (var noStatus in new[] { false, true })
            {
                var q = new DataQuery();
                if (noStatus) q.IsNull("status");
                else q.NotIn("status", new object?[] { "done", "rejected" });
                if (project is { Length: > 0 } p) q.Eq("projectId", p);
                else q.IsNull("projectId");
                rows.AddRange(_items.Find(q));
            }
        }
        if (unboundOnly || projectId is not { Length: > 0 }) AddFor(null);
        else { AddFor(null); AddFor(projectId); }
        return rows
            .OrderBy(d => Long_(d.Doc["ord"]))
            .ThenBy(d => d.Key, StringComparer.Ordinal)
            .Select(d => Read(d.Doc))
            .ToList();
    }

    /// <summary>Record that a conversation worked on an idea (a <c>sessions</c> entry, one per conversation).</summary>
    public bool AddSessionEntry(string id, JsonObject entry)
    {
        var recorded = _data.Transaction(() =>
        {
            if (Find(id) is not { } found) return false;
            var storedId = IdeaOps.Str(found.Doc["id"]) ?? id;
            var next = (JsonObject)found.Doc.DeepClone();
            IdeaOps.AddSessionEntry(next, entry);
            PutItem_(storedId, next, OrdOf(storedId), found.Revision + 1);
            return true;
        });
        if (recorded) Announce("session-recorded");
        return recorded;
    }

    /// <summary>Add a conversation to an idea's provenance (the recall's "the user added this idea to that chat").</summary>
    public (JsonObject Doc, long Revision) AddSession(string id, string sessionId)
    {
        var attached = _data.Transaction(() =>
        {
            if (Find(id) is not { } found) throw new RpcException("not_found", $"Idea {id} not found");
            var storedId = IdeaOps.Str(found.Doc["id"]) ?? id;
            var next = (JsonObject)found.Doc.DeepClone();
            IdeaOps.AddSession(next, sessionId);
            var revision = found.Revision + 1;
            PutItem_(storedId, next, OrdOf(storedId), revision);
            return (WithRevision(next, revision), revision);
        });
        Announce("attach");
        return attached;
    }

    // ------------------------------------------------------------------ imports and metadata

    /// <summary>Record an import in the caller's transaction: the receipt and the data it accounts for commit together.</summary>
    public void RecordImport(string id, string kind, string source, string checksum, JsonObject counts, int schemaVersion = SchemaVersion)
    {
        _imports.Put(id, new JsonObject
        {
            ["kind"] = kind,
            ["source"] = source,
            ["checksum"] = checksum,
            ["schemaVersion"] = (long)schemaVersion,
            ["counts"] = (JsonObject)counts.DeepClone(),
            ["at"] = IdeaOps.Now(),
        });
    }

    // ------------------------------------------------------------------ the one write path for an idea

    /// <summary>
    /// Store an idea at a place in the user's order: its document plus the index fields at the top level, so the
    /// document and what a query sees cannot drift apart. The key is the id; <c>idLower</c> is what the forgiving
    /// lookup (<see cref="Find"/>) matches case-insensitively on.
    /// </summary>
    private void PutItem_(string id, JsonObject doc, long ord, long revision)
    {
        var stored = (JsonObject)doc.DeepClone();
        stored.Remove("revision");
        stored["revision"] = revision;
        stored["ord"] = ord;
        stored["idLower"] = Lower(id);
        var project = IdeaOps.ProjectOf(doc);
        if (project is { } p) stored["projectId"] = p.Id;
        else stored.Remove("projectId"); // unbound: the field is absent, not empty
        _items.Put(id, stored);
    }

    /// <summary>The stored document back to the idea it is: the index fields come off, the revision stays (the caller
    /// reads it and submits it back).</summary>
    private static JsonObject Read(JsonObject stored)
    {
        var doc = (JsonObject)stored.DeepClone();
        doc.Remove("ord");
        doc.Remove("idLower");
        doc.Remove("projectId");
        return doc;
    }

    private IdeaRow? ReadItem_(string key)
    {
        var doc = _items.Get(key);
        if (doc is null) return null;
        return new IdeaRow(Read(doc), Rev(doc));
    }

    private long OrdOf(string id) => Long_(_items.Get(id)?["ord"]);

    /// <summary>Where a new idea lands: before the first or after the last, in the user's order.</summary>
    private long MinOrd()
    {
        var first = _items.Find(new DataQuery().NotNull("ord").Order("ord").Take(1));
        return first.Count == 0 ? 0 : Long_(first[0].Doc["ord"]) - 1;
    }

    private long MaxOrd()
    {
        var last = _items.Find(new DataQuery().Order("ord", true).Take(1));
        return last.Count == 0 ? 0 : Long_(last[0].Doc["ord"]) + 1;
    }

    /// <summary>
    /// The revision travels with the idea the caller reads, so an editor can submit the version it had. It is a field
    /// of the stored document and of the one returned — the only bookkeeping that crosses into the idea's own shape.
    /// </summary>
    private static JsonObject WithRevision(JsonObject doc, long revision)
    {
        doc["revision"] = revision;
        return doc;
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>SQLite's lower() folded ASCII only; idLower is compared ordinally, so the fold must match it.</summary>
    private static string Lower(string s)
    {
        var chars = s.Select(c => c is >= 'A' and <= 'Z' ? (char)(c + 32) : c).ToArray();
        return new string(chars);
    }

    private static long Long_(JsonNode? n) => n is JsonValue v && v.TryGetValue<long>(out var l) ? l : 0;

    private static long Rev(JsonObject doc) => doc["revision"] is JsonValue v && v.TryGetValue<long>(out var r) ? r : 1;
}
