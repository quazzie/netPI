using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace NetPI.Ideas;

/// <summary>
/// The backlog collections in <c>ctx.Data</c> (plugin id "netpi.ideas"): JSON documents under a string key, with
/// declared index fields — the only fields a query may filter or order by. The one-off migration of the old tables
/// (scripts/migrations/001-storage-port) is written from this block, so keep it in step with the code:
/// <code>
///   items        key: the idea id
///                doc: the idea's own JSON fields (the complete document it always was) + revision (long) +
///                     ord (long) + idLower (the key, ASCII-lower-cased) + projectId (only when bound to a project)
///                index: ord Integer, status Text, projectId Text, idLower Text
///   cards        key: the card id
///                doc: doc (the card's JSON), ord (long), kind, sessionId, ideaId, projectId, sourceRev (long),
///                     title, at
///                index: ord Integer, kind Text, sessionId Text, ideaId Text, title Text
///   resolutions  key: the card id (the suggestion answered)
///                doc: action, ideaId (only when the answer wrote or named an idea), at
///                index: none
///   checks       key: the session id
///                doc: rev, n (long), state, tries (long), at, claim (only while a check runs),
///                     claimUntil (long, only while a check runs), error (only when the last check failed)
///                index: state Text, at Text, claimUntil Integer
///   repos        key: the repository path
///                doc: projectId, projectName, hash (only when one was read), at, tries (long), error (only after a failure)
///                index: at Text
///   imports      key: the import id
///                doc: kind, source, checksum, schemaVersion (long), counts (object), at
///                index: none
///   meta         key: the key
///                doc: value (a string)
///                index: none
///   unread       key: the repository path + '\n' + the commit hash
///                doc: repo, hash, subject, tries (long), error (only when set), at
///                index: at Text
/// </code>
/// An absent document property is the same as a NULL column the old tables had: no value, not matched by a
/// comparison. A field the queries order or filter on is an index field and is always written when it has a value.
/// </summary>

/// <summary>
/// The backlog changed under a writer: the idea it read is not the one it wanted to write (optimistic concurrency).
/// The caller turns this into a <c>conflict</c>; the change the caller had in hand is its own, and the UI keeps it.
/// </summary>
public sealed class IdeasConflictException(string message) : Exception(message);

/// <summary>What answering a card did: the idea it wrote (null for a discard), whether it had been answered before.</summary>
public sealed record CardResolution(JsonObject? Idea, bool AlreadyResolved, string Action);

/// <summary>
/// An idea as it is stored, with the revision it is at. A class on purpose: a document that is not there has to be
/// distinguishable from one whose fields happen to be null, which a value tuple cannot express.
/// </summary>
public sealed record IdeaRow(JsonObject Doc, long Revision);

/// <summary>How a card was answered, kept after the card is gone.</summary>
public sealed record CardAnswer(string Action, string? IdeaId);

/// <summary>One conversation's check mark, as it is stored.</summary>
public sealed record CheckMark(string Rev, string State, long Tries, string At, string? Claim);

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

    /// <summary>How long a running check owns its claim; an older claim is recoverable after a restart.</summary>
    public static readonly TimeSpan CheckClaimFor = TimeSpan.FromMinutes(10);

    /// <summary>How long a "this conversation was checked" mark is kept, so one reopened months later is asked again.</summary>
    public const int CheckMarkKeepDays = 30;

    private readonly IPluginData _data;
    private readonly IStorageAccess? _storage;
    private readonly ILogger? _log;
    private readonly string? _home;
    private readonly IDataCollection _items, _cards, _resolutions, _checks, _repos, _imports, _meta, _unread;

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
        _cards = data.Collection("cards", new CollectionSpec().Integer("ord").Text("kind").Text("sessionId").Text("ideaId").Text("title"));
        _resolutions = data.Collection("resolutions", new CollectionSpec());
        _checks = data.Collection("checks", new CollectionSpec().Text("state").Text("at").Integer("claimUntil"));
        _repos = data.Collection("repos", new CollectionSpec().Text("at"));
        _imports = data.Collection("imports", new CollectionSpec());
        _meta = data.Collection("meta", new CollectionSpec());
        _unread = data.Collection("unread", new CollectionSpec().Text("at"));
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
    /// Run several of these writes as one transaction. The import uses it: the ideas, cards, checks, cursors and the
    /// receipt that accounts for them commit together, or none of them does.
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
    /// ones), in backlog order — what the two checks and the recall read, so every decision is offered the same ideas.
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

    /// <summary>The first idea this conversation is recorded on (a <c>sessions</c> entry), or null.</summary>
    public string? IdeaWithSession(string sessionId) => All()
        .Where(i => IdeaOps.WorkedOnWith(i, sessionId))
        .Select(i => IdeaOps.Str(i["id"]))
        .FirstOrDefault(id => id is { Length: > 0 });

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

    /// <summary>
    /// Record a commit on each idea it is about, in one transaction, and return the ideas as they are after the write:
    /// the "is it finished now?" question reads those, and must not read them from before the write.
    /// </summary>
    public List<JsonObject> AddCommitEntries(IEnumerable<string?> ideaIds, JsonObject entry)
    {
        var fresh = _data.Transaction(() =>
        {
            var written = new List<JsonObject>();
            foreach (var id in ideaIds)
            {
                if (Find(id) is not { } found) continue;
                var storedId = IdeaOps.Str(found.Doc["id"])!; // the key it was stored under, as the old path assumed
                var next = (JsonObject)found.Doc.DeepClone();
                IdeaOps.AddCommitEntry(next, entry);
                PutItem_(storedId, next, OrdOf(storedId), found.Revision + 1);
                written.Add(WithRevision(next, found.Revision + 1));
            }
            return written;
        });
        if (fresh.Count > 0) Announce("commits");
        return fresh;
    }

    /// <summary>Mark an idea done (a card's "done" answer) and return it as stored.</summary>
    public (JsonObject Doc, long Revision) MarkDone(string id)
    {
        var done = _data.Transaction(() => MarkDone_(id));
        Announce("done");
        return done;
    }

    private (JsonObject Doc, long Revision) MarkDone_(string id)
    {
        if (Find(id) is not { } found) throw new RpcException("not_found", $"No idea {id}.");
        var storedId = IdeaOps.Str(found.Doc["id"]) ?? id;
        var next = (JsonObject)found.Doc.DeepClone();
        IdeaOps.ApplyPatch(next, new JsonObject { ["status"] = "done" }, fromUi: true, null, _home);
        var revision = found.Revision + 1;
        PutItem_(storedId, next, OrdOf(storedId), revision);
        return (WithRevision(next, revision), revision);
    }

    // ------------------------------------------------------------------ cards (suggestions)

    public List<JsonObject> Cards()
    {
        var cards = new List<JsonObject>();
        foreach (var d in _cards.Find(new DataQuery().Order("ord")))
            if (CardDoc(d.Doc) is { } c) cards.Add(c);
        return cards;
    }

    public JsonObject? Card(string? id) => string.IsNullOrWhiteSpace(id) ? null : CardDoc(_cards.Get(id.Trim()));

    public int CardCount() => (int)_cards.Count();

    /// <summary>
    /// Add a card unless it is one that is already waiting: a "save" card is one per conversation per plan (kind,
    /// session and title), a "done" card is one per idea. A check that runs twice (a retry, a restart) does not stack
    /// them, and the card is still answerable while another window is on it (the answer is what excludes it).
    /// </summary>
    public bool AddCard(JsonObject card, bool dedupe = true)
    {
        var added = _data.Transaction(() =>
        {
            var doc = (JsonObject)card.DeepClone();
            var id = IdeaOps.Str(doc["id"]);
            if (string.IsNullOrWhiteSpace(id)) throw new IdeaInputException("A card needs an id.");
            if (Card(id) is not null) return false;
            var ideaId = IdeaOps.Str(doc["ideaId"]);
            var sessionId = IdeaOps.Str(doc["sessionId"]);
            var title = IdeaOps.Str(doc["title"]) ?? "";
            if (dedupe)
            {
                if (ideaId is { Length: > 0 } i)
                {
                    if (_cards.Count(new DataQuery().Eq("ideaId", i)) > 0) return false;
                }
                else
                {
                    // "save" is the kind the old table checked (a card without an idea is the one it dedupes), and
                    // "session_id IS @s": a card with no session is matched by IS NULL, one with one by equality.
                    var q = new DataQuery().Eq("kind", "save").Eq("title", title);
                    if (sessionId is { Length: > 0 } s) q.Eq("sessionId", s);
                    else q.IsNull("sessionId");
                    if (_cards.Count(q) > 0) return false;
                }
            }
            PutCard_(id, doc, MaxCardOrd());
            return true;
        });
        if (added) Announce("card");
        return added;
    }

    /// <summary>The idea revision a card was made from, when it is about an idea: a card written against one version
    /// of an idea cannot later claim a newer one.</summary>
    private static long? SourceRevision(JsonObject card) =>
        card["ideaRevision"] is JsonValue v && v.TryGetValue<long>(out var n) ? n : null;

    public void RemoveCard(string id) => _cards.Delete(id);

    /// <summary>Restore a card exactly as it was (the import), including fields this build does not know. A card that
    /// is already here stays as it is.</summary>
    public void ImportCard(JsonObject card)
    {
        if (IdeaOps.Str(card["id"]) is not { Length: > 0 } id) return; // no key: nowhere to put it
        _cards.Insert(id, CardForm(id, card, MaxCardOrd()));
    }

    /// <summary>Every check mark, for the export (keyed by session, in the old table's order).</summary>
    public List<JsonObject> Checks() =>
        _checks.Find()
            .OrderBy(d => d.Key, StringComparer.Ordinal)
            .Select(d => new JsonObject
            {
                ["sessionId"] = d.Key,
                ["rev"] = Copy(d.Doc, "rev"),
                ["n"] = Copy(d.Doc, "n"),
                ["state"] = Copy(d.Doc, "state"),
                ["tries"] = Copy(d.Doc, "tries"),
                ["at"] = Copy(d.Doc, "at"),
                ["error"] = Null(d.Doc, "error"),
            }).ToList();

    /// <summary>Every repository cursor, for the export (keyed by repository, in the old table's order).</summary>
    public List<JsonObject> Repos() =>
        _repos.Find()
            .OrderBy(d => d.Key, StringComparer.Ordinal)
            .Select(d => new JsonObject
            {
                ["repo"] = d.Key,
                ["projectId"] = Null(d.Doc, "projectId"),
                ["projectName"] = Null(d.Doc, "projectName"),
                ["hash"] = Null(d.Doc, "hash"),
                ["at"] = Copy(d.Doc, "at"),
                ["tries"] = Copy(d.Doc, "tries"),
                ["error"] = Null(d.Doc, "error"),
            }).ToList();

    /// <summary>Every recorded answer, for the export (keyed by card, in the old table's order).</summary>
    public List<JsonObject> Resolutions() =>
        _resolutions.Find()
            .OrderBy(d => d.Key, StringComparer.Ordinal)
            .Select(d => new JsonObject
            {
                ["suggestionId"] = d.Key,
                ["action"] = Copy(d.Doc, "action"),
                ["ideaId"] = Null(d.Doc, "ideaId"),
                ["at"] = Copy(d.Doc, "at"),
            }).ToList();

    /// <summary>How a card was answered, if it was.</summary>
    public CardAnswer? Resolution(string cardId)
    {
        var doc = _resolutions.Get(cardId);
        if (doc is null) return null;
        return new CardAnswer(IdeaOps.Str(doc["action"]) ?? "", IdeaOps.Str(doc["ideaId"]));
    }

    // ------------------------------------------------------------------ checks (the save check's per-conversation state)

    /// <summary>
    /// Take the check for one conversation revision, or say why not. The claim carries a token and an expiry: a second
    /// close of an unchanged conversation sees the running claim, and a claim left behind by a plugin that died
    /// expires and is retried (bounded by the caller's <paramref name="maxTries"/>) instead of blocking that
    /// conversation forever. The read, the token compare and the write are one transaction, so two closes at once
    /// cannot both start.
    /// </summary>
    public (bool Started, string Reason, string? Token) ClaimCheck(string sessionId, int users, string rev, int maxTries, TimeSpan retryAfter) => _data.Transaction(() =>
    {
        var now = DateTimeOffset.UtcNow;
        var seen = ReadCheck(sessionId);
        if (seen is { } row && row.Rev == rev)
        {
            if (row.State is "done" or "running") return (false, "already", (string?)null);
            if (row.State == "failed" && row.Tries >= maxTries && Stamp(row.At) is { } at && now - at < retryAfter)
                return (false, "already", (string?)null);
        }
        var token = Ids.Short(10);
        // The attempts of a revision that is being retried are kept (a check that fails again has to reach its
        // bound); a revision that is new starts counting again.
        var existing = _checks.Get(sessionId);
        var tries = existing is { } old && Str_(old, "rev") == rev ? Long_(old["tries"]) : 0;
        var mark = new JsonObject
        {
            ["rev"] = rev,
            ["n"] = users,
            ["state"] = "running",
            ["tries"] = tries,
            ["at"] = IdeaOps.Now(),
            ["claim"] = token,
            ["claimUntil"] = now.Add(CheckClaimFor).ToUnixTimeMilliseconds(),
        };
        _checks.Put(sessionId, mark);
        // Housekeeping in the same transaction: a mark of a conversation nobody checks any more goes away.
        _checks.DeleteWhere(new DataQuery().Lt("at", StampOf(now.AddDays(-CheckMarkKeepDays))).Ne("state", "running"));
        return (true, "started", token);
    });

    private CheckMark? ReadCheck(string sessionId)
    {
        var doc = _checks.Get(sessionId);
        if (doc is null) return null;
        return new CheckMark(
            Str_(doc, "rev") ?? "",
            Str_(doc, "state") ?? "",
            Long_(doc["tries"]),
            Str_(doc, "at") ?? "",
            Str_(doc, "claim"));
    }

    /// <summary>
    /// The outcome of a check. <paramref name="error"/> null means the check <b>ran</b> — "nothing worth keeping" is an
    /// answer, not a failure — and only a check that could not run leaves the mark retryable. A worker whose claim was
    /// taken over (it expired and a later close started the check again) cannot overwrite the newer outcome: the token
    /// compare and the write are one transaction.
    /// </summary>
    public bool FinishCheck(string sessionId, string? token, string? error) => _data.Transaction(() =>
    {
        var doc = _checks.Get(sessionId);
        if (doc is null || Str_(doc, "state") != "running") return false; // no mark, or a later close already took it
        if (token is { Length: > 0 } mine && Str_(doc, "claim") is { Length: > 0 } current && current != mine) return false;
        doc["state"] = error is null ? "done" : "failed";
        doc["at"] = IdeaOps.Now();
        doc["tries"] = Long_(doc["tries"]) + 1;
        if (error is null) doc.Remove("error");
        else doc["error"] = Clip(error, 200);
        doc.Remove("claim");
        doc.Remove("claimUntil");
        _checks.Put(sessionId, doc);
        return true;
    });

    /// <summary>
    /// A claim nobody owns any more (the plugin was stopped mid-check): at the next start a "running" mark whose claim
    /// has expired becomes a retryable failure, not a claim that blocks the conversation for good.
    /// </summary>
    public int RecoverExpiredClaims()
    {
        var recovered = _data.Transaction(() =>
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var at = IdeaOps.Now();
            var n = 0;
            foreach (var d in _checks.Find(new DataQuery().Eq("state", "running").Lt("claimUntil", now)))
            {
                var doc = d.Doc;
                doc["state"] = "failed";
                doc["at"] = at;
                doc.Remove("claim");
                doc.Remove("claimUntil");
                if (Str_(doc, "error") is null) doc["error"] = "NetPI stopped before the check finished";
                _checks.Put(d.Key, doc);
                n++;
            }
            return n;
        });
        if (recovered > 0) _log?.LogInformation("Ideas: {Count} check claim(s) left behind by a stopped run are retryable again", recovered);
        return recovered;
    }

    // ------------------------------------------------------------------ repositories (the commit sweep's cursor)

    public string? LastSeen(string repo) => Str_(_repos.Get(repo), "hash");

    /// <summary>
    /// Remember a repository's newest read commit, with the project it belongs to. A read that succeeded clears the
    /// failure state: <c>tries</c> counts the failed attempts at the commit that was just handled, and they do not
    /// carry over to the next one. A project argument that is absent keeps the one that was stored.
    /// </summary>
    public void Remember(string repo, string? hash, string? projectId = null, string? projectName = null) => _data.Transaction(() =>
    {
        var doc = _repos.Get(repo) ?? new JsonObject();
        if (projectId is not null) doc["projectId"] = projectId;
        if (projectName is not null) doc["projectName"] = projectName;
        if (hash is not null) doc["hash"] = hash;
        else doc.Remove("hash"); // a successful read always says which commit it got to
        doc["at"] = IdeaOps.Now();
        doc["tries"] = 0;
        doc.Remove("error");
        _repos.Put(repo, doc);
        return 0;
    });

    /// <summary>
    /// The failure state of a repository's cursor: how many checks in a row failed at the next commit (0: healthy),
    /// the last reason and when that was — what the sweep's backoff is computed from (idea-kooctc).
    /// </summary>
    public (long Tries, string? Error, string? At)? RepoFailure(string repo)
    {
        var doc = _repos.Get(repo);
        if (doc is null) return null;
        return (Long_(doc["tries"]), Str_(doc, "error"), Str_(doc, "at"));
    }

    /// <summary>
    /// Record that the check on a repository's next commit failed, and return the attempt count. The count grows with
    /// every failure and is cleared by a successful read (<see cref="Remember"/>), so it is the attempts at this one
    /// commit: the sweep backs off with it, and a commit that cannot be decided is recorded unread once it reaches its
    /// bound, instead of blocking every later commit of the repository (idea-kooctc).
    /// </summary>
    public int RecordCommitFailure(string repo, string? error) => _data.Transaction(() =>
    {
        var doc = _repos.Get(repo);
        if (doc is { } existing)
        {
            var tries = Long_(existing["tries"]) + 1;
            existing["tries"] = tries;      // the attempt count is what the bound and the backoff are computed from
            existing["at"] = IdeaOps.Now();
            if (error is null) existing.Remove("error");
            else existing["error"] = Clip(error, 200);
            _repos.Put(repo, existing);
            return (int)tries;
        }
        var fresh = new JsonObject { ["at"] = IdeaOps.Now(), ["tries"] = 1 };
        if (error is not null) fresh["error"] = Clip(error, 200);
        _repos.Put(repo, fresh);
        return 1;
    });

    /// <summary>
    /// Record a commit the sweep could not decide after its bound of attempts: the cursor has moved past it
    /// (<see cref="Remember"/>), and the record stays, so the commit is seen (ideas.unread, the export) and later
    /// commits are read instead of being blocked behind it forever (idea-kooctc).
    /// </summary>
    public void RecordUnread(string repo, string hash, string? subject, int tries, string? error)
    {
        var doc = new JsonObject
        {
            ["repo"] = repo,
            ["hash"] = hash,
            ["tries"] = tries,
            ["at"] = IdeaOps.Now(),
        };
        if (subject is not null) doc["subject"] = Clip(subject, 200);
        if (error is not null) doc["error"] = Clip(error, 200);
        _unread.Put(UnreadKey(repo, hash), doc);
    }

    /// <summary>Every commit recorded unread, newest first.</summary>
    public List<JsonObject> Unread() => _unread.Find(new DataQuery().Order("at", true))
        .Select(d => new JsonObject
        {
            ["repo"] = Copy(d.Doc, "repo"),
            ["hash"] = Copy(d.Doc, "hash"),
            ["subject"] = Copy(d.Doc, "subject"),
            ["tries"] = Copy(d.Doc, "tries"),
            ["error"] = Null(d.Doc, "error"),
            ["at"] = Copy(d.Doc, "at"),
        }).ToList();

    /// <summary>
    /// Anchor a repository this plugin has never read at HEAD — and only then. A stored cursor is the progress made
    /// before a restart, and overwriting it would skip every commit made while NetPI was closed.
    /// </summary>
    public void RememberIfAbsent(string repo, string? hash, string? projectId = null, string? projectName = null)
    {
        if (hash is not { Length: > 0 }) return;
        var doc = new JsonObject
        {
            ["hash"] = hash,
            ["at"] = IdeaOps.Now(),
            ["tries"] = 0,
        };
        if (projectId is not null) doc["projectId"] = projectId;
        if (projectName is not null) doc["projectName"] = projectName;
        _repos.Insert(repo, doc); // a stored cursor already is the progress: it stays
    }

    /// <summary>
    /// Forget repositories nothing has read for a while, so the collection does not grow with deleted projects — and
    /// the unread records of the repositories that go with them, so the two cannot drift apart.
    /// </summary>
    public int ForgetStaleRepos(int keepDays)
    {
        var cutoff = StampOf(DateTimeOffset.UtcNow.AddDays(-keepDays));
        _unread.DeleteWhere(new DataQuery().Lt("at", cutoff));
        // What is left is cleaned the way the old NOT IN did: an unread record whose repository is gone has no owner,
        // and the repositories that go are the stale ones. The two collections cannot drift apart.
        var kept = _repos.Find().Select(d => d.Key).ToHashSet();
        foreach (var d in _unread.Find())
            if (!kept.Contains(IdeaOps.Str(d.Doc["repo"]) ?? "")) _unread.Delete(d.Key);
        return _repos.DeleteWhere(new DataQuery().Lt("at", cutoff));
    }

    // ------------------------------------------------------------------ imports and metadata

    public string? Meta(string key) => Str_(_meta.Get(key), "value");

    public void SetMeta(string key, string? value)
    {
        if (value is null) _meta.Delete(key);
        else _meta.Put(key, new JsonObject { ["value"] = value });
    }

    public JsonObject? MetaObject(string key)
    {
        var raw = Meta(key);
        if (raw is not { Length: > 0 }) return null;
        try { return JsonNode.Parse(raw) as JsonObject; } catch (JsonException) { return null; }
    }

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
        var first = _items.Find(new DataQuery().Order("ord").Take(1));
        return first.Count == 0 ? 0 : Long_(first[0].Doc["ord"]) - 1;
    }

    private long MaxOrd()
    {
        var last = _items.Find(new DataQuery().Order("ord", true).Take(1));
        return last.Count == 0 ? 0 : Long_(last[0].Doc["ord"]) + 1;
    }

    private long MaxCardOrd()
    {
        var last = _cards.Find(new DataQuery().Order("ord", true).Take(1));
        return last.Count == 0 ? 0 : Long_(last[0].Doc["ord"]) + 1;
    }

    /// <summary>A card under its id, with the fields its queries filter on (the old table's projections).</summary>
    private void PutCard_(string id, JsonObject card, long ord) => _cards.Put(id, CardForm(id, card, ord));

    private static JsonObject CardForm(string id, JsonObject card, long ord)
    {
        var doc = new JsonObject { ["doc"] = (JsonObject)card.DeepClone() };
        doc["ord"] = ord;
        doc["kind"] = IdeaOps.Str(card["kind"]) ?? "save";
        doc["title"] = IdeaOps.Str(card["title"]) ?? "";
        if (IdeaOps.Str(card["sessionId"]) is { } s) doc["sessionId"] = s;
        if (IdeaOps.Str(card["ideaId"]) is { } i) doc["ideaId"] = i;
        if (IdeaOps.Str((card["project"] as JsonObject)?["id"]) is { } p) doc["projectId"] = p;
        if (SourceRevision(card) is { } r) doc["sourceRev"] = r;
        doc["at"] = IdeaOps.Str(card["at"]) ?? IdeaOps.Now();
        return doc;
    }

    /// <summary>
    /// The card as it was written, a document of its own: the store hands out copies, and a node that still has the row
    /// it was read from as its parent cannot be put into an answer (a JSON node belongs to one parent).
    /// </summary>
    private static JsonObject? CardDoc(JsonObject? doc) => doc?["doc"] is JsonObject card ? (JsonObject)card.DeepClone() : null;

    /// <summary>
    /// The revision travels with the idea the caller reads, so an editor can submit the version it had. It is a field
    /// of the stored document and of the one returned — the only bookkeeping that crosses into the idea's own shape.
    /// </summary>
    private static JsonObject WithRevision(JsonObject doc, long revision)
    {
        doc["revision"] = revision;
        return doc;
    }

    /// <summary>How a card was answered, kept after the card is gone. One answer per card: the write is the exclusion.</summary>
    private void PutResolution_(string cardId, string action, string? ideaId)
    {
        var doc = new JsonObject { ["action"] = action, ["at"] = IdeaOps.Now() };
        if (ideaId is not null) doc["ideaId"] = ideaId;
        _resolutions.Put(cardId, doc);
    }

    private static string UnreadKey(string repo, string hash) => repo + "\n" + hash;

    // ------------------------------------------------------------------ answers

    /// <summary>
    /// Answer a card in one transaction: check what the card asks against what the user answered, write the idea (a new
    /// one, or the existing idea marked done), record the resolution and take the card out. Two windows answering at
    /// once, or the same answer retried after a timeout, therefore have one effect: the second reads the recorded
    /// resolution and reports the same outcome instead of producing it again.
    /// </summary>
    public CardResolution ResolveCard(string cardId, string action, JsonObject? edit, Action<JsonObject>? afterCommit = null) =>
        ResolveCard(cardId, action, edit, afterCommit, requireCard: true);

    /// <summary>
    /// As <see cref="ResolveCard(string, string, JsonObject?, Action{JsonObject}?)"/>, but an answer whose card is
    /// already gone is still applied: that is what an interrupted answer of an older version looks like (the card left
    /// the queue, the idea never landed). A caller that is answering a card the user is looking at wants the card.
    /// </summary>
    public CardResolution ResolveCard(string cardId, string action, JsonObject? edit, Action<JsonObject>? afterCommit, bool requireCard)
    {
        var result = _data.Transaction(() =>
        {
            if (Resolution(cardId) is { } done) // already answered: the same answer, not a second one
                return new CardResolution(done.IdeaId is { Length: > 0 } id ? Find(id)?.Doc : null, true, done.Action);

            var card = Card(cardId);
            if (card is null && requireCard)
                throw new RpcException("not_found", "That card is gone (already answered, discarded, or NetPI restarted).");
            if (card is not null) ValidateAnswer(action, card);
            var idea = action switch
            {
                "save" => SaveCard_(card ?? throw new RpcException("bad_request", "An answer that saves an idea needs the card it was about."), edit),
                "done" => MarkDone_(IdeaOps.Str(card?["ideaId"]) ?? "").Doc,
                _ => null,
            };
            PutResolution_(cardId, action, IdeaOps.Str(idea?["id"]));
            RemoveCard(cardId);
            return new CardResolution(idea, false, action);
        });
        // Only after the commit: nothing is announced that did not happen.
        if (result.Idea is not null) afterCommit?.Invoke(result.Idea);
        Announce("answer");
        return result;
    }

    /// <summary>What the card is about has to match what the user answered, before anything is written.</summary>
    private static void ValidateAnswer(string action, JsonObject card)
    {
        if (action is not ("save" or "done" or "discard"))
            throw new RpcException("bad_request", "action must be \"save\", \"done\" or \"discard\"");
        var kind = IdeaOps.Str(card["kind"]) ?? "save";
        if (action == "done" && kind != "done")
            throw new RpcException("bad_request", "That card asks to save an idea; answer \"save\" or \"discard\".");
        if (action == "save" && kind == "done")
            throw new RpcException("bad_request", "That card marks an existing idea done; answer \"done\" or \"discard\".");
        if (action == "done" && IdeaOps.Str(card["ideaId"]) is not { Length: > 0 })
            throw new RpcException("bad_request", "That card is not about an idea.");
    }

    /// <summary>
    /// The idea a "save" answer writes: the card's title and summary with the user's edit on top, stamped with the
    /// card's project and with the chat it came from. Built and stored inside the answer's transaction, so what is
    /// saved is what the card describes, once.
    /// </summary>
    private JsonObject SaveCard_(JsonObject card, JsonObject? edit)
    {
        var title = IdeaOps.Str(card["title"])?.Trim();
        var summary = IdeaOps.Str(card["summary"]);
        if (edit is not null)
        {
            if (edit["title"] is JsonValue t && t.TryGetValue<string>(out var edited) && edited.Trim().Length > 0) title = edited.Trim();
            if (edit["summary"] is JsonValue sv && sv.TryGetValue<string>(out var editedSummary)) summary = editedSummary.Trim();
        }
        if (string.IsNullOrEmpty(title)) throw new RpcException("bad_request", "An idea needs a title.");

        var project = card["project"] as JsonObject;
        var sessionId = IdeaOps.Str(card["sessionId"]);
        var idea = IdeaOps.CreateIdea(new JsonObject { ["title"] = title, ["summary"] = summary }, TakenIds(),
            "user", sessionId is { Length: > 0 } sid ? sid : null, keepExtraFields: true);
        IdeaOps.SetProject(idea, IdeaOps.Str(project?["id"]), IdeaOps.Str(project?["name"]));
        IdeaOps.AddSessionEntry(idea, new JsonObject
        {
            ["sessionId"] = sessionId,
            ["title"] = IdeaOps.Str(card["sessionTitle"]),
            ["at"] = IdeaOps.Now(),
            ["seen"] = true,
        });
        PutItem_(IdeaOps.Str(idea["id"])!, idea, MaxOrd(), 1);
        return idea; // as the old path returned it: the document it wrote, without the revision the store keeps
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>SQLite's lower() folded ASCII only; idLower is compared ordinally, so the fold must match it.</summary>
    private static string Lower(string s)
    {
        var chars = s.Select(c => c is >= 'A' and <= 'Z' ? (char)(c + 32) : c).ToArray();
        return new string(chars);
    }

    private static string? Str_(JsonObject? doc, string field) => doc is null ? null : IdeaOps.Str(doc[field]);

    private static long Long_(JsonNode? n) => n is JsonValue v && v.TryGetValue<long>(out var l) ? l : 0;

    private static long Rev(JsonObject doc) => doc["revision"] is JsonValue v && v.TryGetValue<long>(out var r) ? r : 1;

    /// <summary>An explicit JSON null where the old column was NULL: the export lists read it back the same way.</summary>
    private static JsonNode? Null(JsonObject doc, string field) => doc[field] is { } v ? (JsonNode)v.DeepClone() : JsonValue.Create((string?)null);

    /// <summary>A stored field as a value of its own: a node read out of a row still belongs to it, and a document
    /// that took it would fail (one node, one parent).</summary>
    private static JsonNode? Copy(JsonObject doc, string field) => doc[field]?.DeepClone();

    private static string StampOf(DateTimeOffset when) =>
        when.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static DateTimeOffset? Stamp(string? at) => at is { Length: > 0 } s
        && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var when) ? when : null;

    private static string? Clip(string? s, int n) => s is null ? null : s.Length > n ? s[..n] + "…" : s;
}
