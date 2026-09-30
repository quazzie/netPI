using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace NetPI.Ideas;

/// <summary>
/// The backlog changed under a writer: the idea it read is not the one it wanted to write (optimistic concurrency).
/// The caller turns this into a <c>conflict</c>; the change the caller had in hand is its own, and the UI keeps it.
/// </summary>
public sealed class IdeasConflictException(string message) : Exception(message);

/// <summary>
/// The database holds a newer Ideas storage than this build understands. Nothing is written and the plugin refuses to
/// serve, rather than reading a shape it would mangle on the next write.
/// </summary>
public sealed class IdeasStorageVersionException(string message) : Exception(message);

/// <summary>What answering a card did: the idea it wrote (null for a discard), whether it had been answered before.</summary>
public sealed record CardResolution(JsonObject? Idea, bool AlreadyResolved, string Action);

/// <summary>
/// An idea as it is stored, with the revision it is at. A class on purpose: a row that is not there has to be
/// distinguishable from a row whose fields happen to be null, which a value tuple cannot express.
/// </summary>
public sealed record IdeaRow(JsonObject Doc, long Revision);

/// <summary>How a card was answered, kept after the card is gone.</summary>
public sealed record CardAnswer(string Action, string? IdeaId);

/// <summary>One conversation's check mark, as it is stored.</summary>
public sealed record CheckMark(string Rev, string State, long Tries, string At, string? Claim);

/// <summary>
/// The Ideas backlog in SQLite: plugin-owned tables in the host's <c>netpi.db</c>, reached through <c>ctx.Db</c> and
/// nothing else. One repository, one write path, one transaction per operation.
/// <para>
/// An idea is one row: a stable id, a monotonically increasing <c>revision</c>, the manual <c>ord</c> (the order the
/// user arranged), the columns the queries filter on (status, project, order) and <c>doc</c> — the complete idea JSON,
/// exactly as the JSON backlog held it, with every field this build does not know about. The columns are projections
/// of <c>doc</c> written in the same statement, so the two cannot drift apart. An idea is still only ever understood as
/// the document it always was (<see cref="IdeaOps"/> works on <see cref="JsonObject"/>s), which is how a field another
/// tool wrote by hand survives a save.
/// </para>
/// <para>
/// Every method is short and synchronous. The host serializes one connection, so a transaction is held for the length
/// of a few statements and never across a model call, a file read or anything else that can block.
/// </para>
/// </summary>
public sealed class IdeasRepository
{
    /// <summary>The migration scope: a plugin's tables live under their own name in the shared database.</summary>
    public const string Scope = "netpi.ideas";

    /// <summary>
    /// The storage shape this build writes. A database at a higher version is refused: a build that does not know a
    /// column must not read it as "absent" and write it back that way.
    /// </summary>
    public const int SchemaVersion = 1;

    /// <summary>How long a running check owns its claim; an older claim is recoverable after a restart.</summary>
    public static readonly TimeSpan CheckClaimFor = TimeSpan.FromMinutes(10);

    /// <summary>How long a "this conversation was checked" mark is kept, so one reopened months later is asked again.</summary>
    public const int CheckMarkKeepDays = 30;

    private const string Migration1 = """
        CREATE TABLE ideas_items (
            id            TEXT PRIMARY KEY,
            ord           INTEGER NOT NULL,
            revision      INTEGER NOT NULL DEFAULT 1,
            title         TEXT NOT NULL DEFAULT '',
            status        TEXT NOT NULL DEFAULT 'open',
            priority      TEXT NOT NULL DEFAULT 'medium',
            project_id    TEXT,
            project_name  TEXT,
            created_at    TEXT,
            updated_at    TEXT,
            doc           TEXT NOT NULL
        );
        CREATE INDEX ideas_items_order ON ideas_items(ord, id);
        CREATE INDEX ideas_items_project_status_order ON ideas_items(project_id, status, ord);
        CREATE INDEX ideas_items_status_order ON ideas_items(status, ord);

        CREATE TABLE ideas_suggestions (
            id         TEXT PRIMARY KEY,
            ord        INTEGER NOT NULL,
            kind       TEXT NOT NULL DEFAULT 'save',
            session_id TEXT,
            idea_id    TEXT,
            project_id TEXT,
            source_rev INTEGER,
            title      TEXT NOT NULL DEFAULT '',
            at         TEXT NOT NULL DEFAULT '',
            doc        TEXT NOT NULL
        );
        CREATE INDEX ideas_suggestions_order ON ideas_suggestions(ord, id);
        CREATE INDEX ideas_suggestions_idea ON ideas_suggestions(idea_id);
        CREATE INDEX ideas_suggestions_session ON ideas_suggestions(session_id, kind, title);

        -- One answer per card, kept after the card is gone: a second window, a retry or a restart then reads the
        -- outcome instead of producing it a second time.
        CREATE TABLE ideas_resolutions (
            suggestion_id TEXT PRIMARY KEY,
            action        TEXT NOT NULL,
            idea_id       TEXT,
            at            TEXT NOT NULL DEFAULT ''
        );

        CREATE TABLE ideas_checks (
            session_id   TEXT PRIMARY KEY,
            rev          TEXT NOT NULL DEFAULT '',
            n            INTEGER NOT NULL DEFAULT 0,
            state        TEXT NOT NULL DEFAULT 'done',
            tries        INTEGER NOT NULL DEFAULT 0,
            at           TEXT NOT NULL DEFAULT '',
            claim        TEXT,
            claim_until  INTEGER,
            error        TEXT
        );
        CREATE INDEX ideas_checks_state ON ideas_checks(state);

        CREATE TABLE ideas_repos (
            repo         TEXT PRIMARY KEY,
            project_id   TEXT,
            project_name TEXT,
            hash         TEXT,
            at           TEXT NOT NULL DEFAULT '',
            tries        INTEGER NOT NULL DEFAULT 0,
            error        TEXT
        );

        CREATE TABLE ideas_imports (
            id             TEXT PRIMARY KEY,
            kind           TEXT NOT NULL DEFAULT 'legacy',
            source         TEXT NOT NULL DEFAULT '',
            checksum       TEXT NOT NULL DEFAULT '',
            schema_version INTEGER NOT NULL DEFAULT 1,
            counts         TEXT NOT NULL DEFAULT '{}',
            at             TEXT NOT NULL DEFAULT ''
        );

        CREATE TABLE ideas_metadata (
            key   TEXT PRIMARY KEY,
            value TEXT NOT NULL
        );
        """;

    private readonly IDatabase _db;
    private readonly ILogger? _log;
    private readonly string _databaseName;

    /// <summary>
    /// Announced after a write has committed (never before, and never for one that rolled back), so every window
    /// re-reads the canonical state. The plugin sets this to publish <c>ideas.changed</c>; the repository itself knows
    /// nothing about events.
    /// </summary>
    public Action<string>? OnChanged { get; set; }

    private void Announce(string reason) => OnChanged?.Invoke(reason);

    private IdeasRepository(IDatabase db, ILogger? log, string databaseName)
    {
        _db = db;
        _log = log;
        _databaseName = databaseName;
    }

    /// <summary>
    /// Open the backlog: create the tables, and refuse a database written by a newer build. Called once per plugin
    /// start, so two instances of this plugin (a reload swap) share one set of tables instead of two backends.
    /// </summary>
    public static IdeasRepository Open(IDatabase db, ILogger? log = null, string? databaseFile = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        var repository = new IdeasRepository(db, log, Path.GetFileName(databaseFile ?? "netpi.db"));
        var version = repository.ScopeVersion();
        if (version > SchemaVersion)
            throw new IdeasStorageVersionException(
                $"The Ideas tables in this database are at version {version}, and this build writes version {SchemaVersion}. " +
                "Update NetPI: an older build must not write a storage it does not understand.");
        db.Migrate(Scope, Migration1);
        return repository;
    }

    /// <summary>Where this backlog lives, for <c>ideas.list</c> and the UI: a table in the app's database, not a file.</summary>
    public JsonObject Storage() => new()
    {
        ["backend"] = "sqlite",
        ["database"] = _databaseName,
        ["scope"] = Scope,
        ["schemaVersion"] = SchemaVersion,
        ["editableFile"] = false,
        ["importExport"] = "json",
    };

    /// <summary>What version this scope's tables are at (0: never migrated; the table itself is created by Migrate).</summary>
    private int ScopeVersion()
    {
        if (_db.Scalar<long?>("SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = '_migrations'") is not > 0) return 0;
        return _db.Scalar<long?>("SELECT version FROM _migrations WHERE scope = @s", new { s = Scope }) is { } v ? (int)v : 0;
    }

    /// <summary>
    /// Run several of these writes as one transaction. The import uses it: the ideas, cards, checks, cursors and the
    /// receipt that accounts for them commit together, or none of them does.
    /// </summary>
    public T Transaction<T>(Func<IdeasRepository, T> work) => _db.Transaction(_ => work(this));

    // ------------------------------------------------------------------ ideas

    /// <summary>Every idea as it is stored, in the user's order (each with the revision an editor submits back).</summary>
    public List<JsonObject> All() =>
        _db.Query("SELECT doc, revision FROM ideas_items ORDER BY ord, id", null, r => Parse(r.GetString("doc"), r.GetInt64("revision")));

    public int Count() => (int)(_db.Scalar<long?>("SELECT COUNT(*) FROM ideas_items") ?? 0);

    /// <summary>The ids in the backlog, for a new id that must not collide with one of them.</summary>
    public List<string> TakenIds() => _db.Query("SELECT id FROM ideas_items", null, r => r.GetString("id"));

    /// <summary>
    /// One idea and its revision, or null. The lookup is as forgiving as it has always been: exact, then with the
    /// "idea-" prefix, then case-insensitive — an id typed by hand or carried over from an old file still finds its
    /// idea, and a legacy id that is not of our making is stored and found exactly as it is.
    /// </summary>
    public IdeaRow? Find(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        var want = id.Trim();
        return _db.QuerySingle(
            "SELECT doc, revision FROM ideas_items WHERE id = @exact OR id = @pref OR lower(id) = lower(@exact) OR lower(id) = lower(@pref) LIMIT 1",
            new { exact = want, pref = "idea-" + want },
            r => new IdeaRow(Parse(r.GetString("doc"), r.GetInt64("revision")), r.GetInt64("revision")));
    }

    /// <summary>One idea as it is stored, or null — for a caller that does not care about the revision.</summary>
    public JsonObject? Idea(string? id) => Find(id)?.Doc;

    /// <summary>Store a new idea at the end of the backlog (or the top of it) and return it with its revision.</summary>
    public (JsonObject Doc, long Revision) Add(JsonObject idea, bool prepend = false)
    {
        var added = _db.Transaction(_ =>
    {
        var doc = (JsonObject)idea.DeepClone();
        var id = IdeaOps.Str(doc["id"]);
        if (string.IsNullOrWhiteSpace(id)) throw new IdeaInputException("An idea needs an id.");
        if (Find(id) is not null) throw new IdeasConflictException($"Idea {id} is already in the backlog.");
        var ord = prepend
            ? _db.Scalar<long?>("SELECT MIN(ord) - 1 FROM ideas_items") ?? 0
            : _db.Scalar<long?>("SELECT MAX(ord) + 1 FROM ideas_items") ?? 0;
        var (stored, revision) = Insert_(doc, ord);
        return (WithRevision(stored, revision), revision);
    });
        Announce("add");
        return added;
    }


    /// <summary>
    /// Apply a patch and store the result, or store nothing at all. The patch runs on a detached copy, so a patch that
    /// fails validation (an empty title, an unknown status) leaves neither the database nor any later writer's copy of
    /// the idea changed. <paramref name="expectedRevision"/> is the optimistic-concurrency check; without it the write is
    /// last-writer-wins, which is what the agent tool does.
    /// </summary>
    public (JsonObject Doc, long Revision, List<string> Changes) Update(
        string id, JsonObject patch, bool fromUi, string? sessionId = null, long? expectedRevision = null, string? expectedUpdatedAt = null)
    {
        var result = _db.Transaction(_ =>
    {
        var current = Find(id) ?? throw new RpcException("not_found", $"Idea {id} not found");
        var storedId = IdeaOps.Str(current.Doc["id"]) ?? id;
        if (expectedRevision is { } want && want != current.Revision)
            throw new IdeasConflictException(
                $"Idea {storedId} changed since you read it (it is at revision {current.Revision}, your copy is {want}). " +
                "Reload it and apply your change again.");
        // Kept for callers that only know the old check. It compares a second-precision timestamp, so two changes in the
        // same second are not a conflict: prefer expectedRevision wherever it is available.
        if (expectedRevision is null && expectedUpdatedAt is { Length: > 0 } stamp && IdeaOps.Str(current.Doc["updatedAt"]) != stamp)
            throw new IdeasConflictException(
                $"Idea {storedId} changed since {stamp} (it is now {IdeaOps.Str(current.Doc["updatedAt"]) ?? "untouched"}). Reload it and apply your change again.");

        var next = (JsonObject)current.Doc.DeepClone();
        var changes = IdeaOps.ApplyPatch(next, patch, fromUi, sessionId);
        if (changes.Count == 0) return (current.Doc, current.Revision, changes); // nothing changed: no write, no new revision
        var revision = current.Revision + 1;
        Save_(storedId, next, revision);
        return (WithRevision(next, revision), revision, changes);
    });
        if (result.Item3.Count > 0) Announce("update"); // a patch that changed nothing is not a change
        return result;
    }

    public bool Delete(string? id)
    {
        var deleted = _db.Transaction(_ =>
    {
        if (Find(id) is not { } found) return false;
        _db.Execute("DELETE FROM ideas_items WHERE id = @id", new { id = IdeaOps.Str(found.Doc["id"]) });
        return true;
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
        _db.Transaction(_ =>
        {
            var stored = _db.Query("SELECT id FROM ideas_items ORDER BY ord, id", null, r => r.GetString("id"));
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
                _db.Execute("UPDATE ideas_items SET ord = @ord WHERE id = @id", new { ord = (long)i, id = ordered[i] });
        });
        Announce("reorder");
    }

    /// <summary>
    /// The open ideas of a project plus the unbound ones (or, with <paramref name="unboundOnly"/>, only the unbound
    /// ones), in backlog order — what the two checks and the recall read, so every decision is offered the same ideas.
    /// </summary>
    public List<JsonObject> OpenIdeas(string? projectId, bool unboundOnly = false) => _db.Query(
        "SELECT doc, revision FROM ideas_items WHERE status NOT IN ('done', 'rejected') " +
        (unboundOnly ? "AND project_id IS NULL " : "AND (project_id IS NULL OR project_id = @p) ") + "ORDER BY ord, id",
        new { p = projectId }, r => Parse(r.GetString("doc"), r.GetInt64("revision")));

    /// <summary>The first idea this conversation is recorded on (a <c>sessions</c> entry), or null.</summary>
    public string? IdeaWithSession(string sessionId) => All()
        .Where(i => IdeaOps.WorkedOnWith(i, sessionId))
        .Select(i => IdeaOps.Str(i["id"]))
        .FirstOrDefault(id => id is { Length: > 0 });

    /// <summary>Record that a conversation worked on an idea (a <c>sessions</c> entry, one per conversation).</summary>
    public bool AddSessionEntry(string id, JsonObject entry)
    {
        var recorded = _db.Transaction(_ =>
        {
            if (Find(id) is not { } found) return false;
            var next = (JsonObject)found.Doc.DeepClone();
            IdeaOps.AddSessionEntry(next, entry);
            Save_(IdeaOps.Str(found.Doc["id"])!, next, found.Revision + 1);
            return true;
        });
        if (recorded) Announce("session-recorded");
        return recorded;
    }

    /// <summary>Add a conversation to an idea's provenance (the recall's "the user added this idea to that chat").</summary>
    public (JsonObject Doc, long Revision) AddSession(string id, string sessionId)
    {
        var attached = _db.Transaction(_ =>
    {
        if (Find(id) is not { } found) throw new RpcException("not_found", $"Idea {id} not found");
        var next = (JsonObject)found.Doc.DeepClone();
        IdeaOps.AddSession(next, sessionId);
        var revision = found.Revision + 1;
        Save_(IdeaOps.Str(found.Doc["id"])!, next, revision);
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
        var fresh = _db.Transaction(_ =>
        {
        var fresh = new List<JsonObject>();
        foreach (var id in ideaIds)
        {
            if (Find(id) is not { } found) continue;
            var next = (JsonObject)found.Doc.DeepClone();
            IdeaOps.AddCommitEntry(next, entry);
            Save_(IdeaOps.Str(found.Doc["id"])!, next, found.Revision + 1);
            fresh.Add(WithRevision(next, found.Revision + 1));
        }
        return fresh;
        });
        if (fresh.Count > 0) Announce("commits");
        return fresh;
    }

    /// <summary>Mark an idea done (a card's "done" answer) and return it as stored.</summary>
    public (JsonObject Doc, long Revision) MarkDone(string id)
    {
        var done = _db.Transaction(_ => MarkDone_(id));
        Announce("done");
        return done;
    }

    private (JsonObject Doc, long Revision) MarkDone_(string id)
    {
        if (Find(id) is not { } found) throw new RpcException("not_found", $"No idea {id}.");
        var next = (JsonObject)found.Doc.DeepClone();
        IdeaOps.ApplyPatch(next, new JsonObject { ["status"] = "done" }, fromUi: true);
        var revision = found.Revision + 1;
        Save_(IdeaOps.Str(found.Doc["id"])!, next, revision);
        return (WithRevision(next, revision), revision);
    }

    // ------------------------------------------------------------------ cards (suggestions)

    public List<JsonObject> Cards() =>
        _db.Query("SELECT doc FROM ideas_suggestions ORDER BY ord, id", null, r => Parse(r.GetString("doc"), 0));

    public JsonObject? Card(string? id) => string.IsNullOrWhiteSpace(id) ? null : _db.QuerySingle(
        "SELECT doc FROM ideas_suggestions WHERE id = @id", new { id = id.Trim() }, r => Parse(r.GetString("doc"), 0));

    public int CardCount() => (int)(_db.Scalar<long?>("SELECT COUNT(*) FROM ideas_suggestions") ?? 0);

    /// <summary>
    /// Add a card unless it is one that is already waiting: a "save" card is one per conversation per plan (kind,
    /// session and title), a "done" card is one per idea. A check that runs twice (a retry, a restart) does not stack
    /// them, and the card is still answerable while another window is on it (the answer is what excludes it).
    /// </summary>
    public bool AddCard(JsonObject card, bool dedupe = true)
    {
        var added = _db.Transaction(_ =>
    {
        var doc = (JsonObject)card.DeepClone();
        var id = IdeaOps.Str(doc["id"]);
        if (string.IsNullOrWhiteSpace(id)) throw new IdeaInputException("A card needs an id.");
        if (Card(id) is not null) return false;
        var kind = IdeaOps.Str(doc["kind"]) ?? "save";
        var ideaId = IdeaOps.Str(doc["ideaId"]);
        var sessionId = IdeaOps.Str(doc["sessionId"]);
        var title = IdeaOps.Str(doc["title"]) ?? "";
        if (dedupe)
        {
            if (ideaId is { Length: > 0 })
            {
                if (_db.Scalar<long?>("SELECT COUNT(*) FROM ideas_suggestions WHERE idea_id = @i", new { i = ideaId }) > 0) return false;
            }
            else if (_db.Scalar<long?>("SELECT COUNT(*) FROM ideas_suggestions WHERE kind = 'save' AND session_id IS @s AND title = @t", new { s = (object?)sessionId, t = title }) > 0)
            {
                return false;
            }
        }
        var ord = _db.Scalar<long?>("SELECT MAX(ord) + 1 FROM ideas_suggestions") ?? 0;
        var project = doc["project"] as JsonObject;
        _db.Execute(
            "INSERT INTO ideas_suggestions (id, ord, kind, session_id, idea_id, project_id, source_rev, title, at, doc) " +
            "VALUES (@id, @ord, @kind, @s, @i, @p, @rev, @t, @at, @doc)", new
            {
                id, ord, kind, s = (object?)sessionId, i = (object?)ideaId, p = (object?)IdeaOps.Str(project?["id"]),
                rev = (object?)SourceRevision(doc), t = title, at = IdeaOps.Str(doc["at"]) ?? IdeaOps.Now(), doc = doc.ToJsonString(),
            });
        return true;
        });
        if (added) Announce("card");
        return added;
    }

    /// <summary>The idea revision a card was made from, when it is about an idea: a card written against one version
    /// of an idea cannot later claim a newer one.</summary>
    private static long? SourceRevision(JsonObject card) =>
        card["ideaRevision"] is JsonValue v && v.TryGetValue<long>(out var n) ? n : null;

    public void RemoveCard(string id) => _db.Execute("DELETE FROM ideas_suggestions WHERE id = @id", new { id });

    /// <summary>Restore a card exactly as it was (the import), including fields this build does not know.</summary>
    public void ImportCard(JsonObject card) => _db.Execute(
        "INSERT INTO ideas_suggestions (id, ord, kind, session_id, idea_id, project_id, source_rev, title, at, doc) " +
        "VALUES (@id, @ord, @kind, @s, @i, @p, @rev, @t, @at, @doc) ON CONFLICT(id) DO NOTHING", new
        {
            id = IdeaOps.Str(card["id"]), ord = _db.Scalar<long?>("SELECT MAX(ord) + 1 FROM ideas_suggestions") ?? 0,
            kind = IdeaOps.Str(card["kind"]) ?? "save", s = (object?)IdeaOps.Str(card["sessionId"]),
            i = (object?)IdeaOps.Str(card["ideaId"]), p = (object?)IdeaOps.Str((card["project"] as JsonObject)?["id"]),
            rev = (object?)SourceRevision(card), t = IdeaOps.Str(card["title"]) ?? "",
            at = IdeaOps.Str(card["at"]) ?? IdeaOps.Now(), doc = card.ToJsonString(),
        });

    /// <summary>Restore a per-conversation check mark (the import), as it was left.</summary>
    public void ImportCheck(string sessionId, JsonObject mark) => _db.Execute(
        "INSERT INTO ideas_checks (session_id, rev, n, state, tries, at, claim, claim_until, error) " +
        "VALUES (@id, @rev, @n, @state, @tries, @at, NULL, NULL, @error) ON CONFLICT(session_id) DO NOTHING", new
        {
            id = sessionId, rev = IdeaOps.Str(mark["rev"]) ?? "", n = (long)(Number(mark["n"]) ?? 0),
            // A "running" mark is a claim nobody holds any more once the process is gone: retryable, not blocking for good.
            state = IdeaOps.Str(mark["state"]) is "running" ? "failed" : (IdeaOps.Str(mark["state"]) ?? "done"),
            tries = (long)(Number(mark["tries"]) ?? 0), at = IdeaOps.Str(mark["at"]) ?? IdeaOps.Now(),
            error = (object?)(IdeaOps.Str(mark["state"]) is "running" ? "NetPI stopped before the check finished" : IdeaOps.Str(mark["error"])),
        });

    /// <summary>Restore a repository cursor (the import).</summary>
    public void ImportRepo(JsonObject repo) => _db.Execute(
        "INSERT INTO ideas_repos (repo, project_id, project_name, hash, at, tries, error) VALUES (@r, @p, @n, @h, @at, 0, NULL) ON CONFLICT(repo) DO NOTHING", new
        {
            r = IdeaOps.Str(repo["repo"]) ?? IdeaOps.Str(repo["id"]) ?? "", p = (object?)IdeaOps.Str(repo["projectId"]),
            n = (object?)IdeaOps.Str(repo["projectName"]), h = (object?)IdeaOps.Str(repo["hash"]),
            at = IdeaOps.Str(repo["at"]) ?? IdeaOps.Now(),
        });

    /// <summary>Every check mark, for the export.</summary>
    public List<JsonObject> Checks() => _db.Query("SELECT * FROM ideas_checks ORDER BY session_id", null, r => new JsonObject
    {
        ["sessionId"] = r.GetString("session_id"), ["rev"] = r.GetString("rev"), ["n"] = r.GetInt64("n"),
        ["state"] = r.GetString("state"), ["tries"] = r.GetInt64("tries"), ["at"] = r.GetString("at"), ["error"] = r.GetStringOrNull("error"),
    });

    /// <summary>Every repository cursor, for the export.</summary>
    public List<JsonObject> Repos() => _db.Query("SELECT * FROM ideas_repos ORDER BY repo", null, r => new JsonObject
    {
        ["repo"] = r.GetString("repo"), ["projectId"] = r.GetStringOrNull("project_id"), ["projectName"] = r.GetStringOrNull("project_name"),
        ["hash"] = r.GetStringOrNull("hash"), ["at"] = r.GetString("at"), ["tries"] = r.GetInt64("tries"), ["error"] = r.GetStringOrNull("error"),
    });

    /// <summary>Every recorded answer, for the export.</summary>
    public List<JsonObject> Resolutions() => _db.Query("SELECT * FROM ideas_resolutions ORDER BY suggestion_id", null, r => new JsonObject
    {
        ["suggestionId"] = r.GetString("suggestion_id"), ["action"] = r.GetString("action"),
        ["ideaId"] = r.GetStringOrNull("idea_id"), ["at"] = r.GetString("at"),
    });

    /// <summary>How a card was answered, if it was.</summary>
    public CardAnswer? Resolution(string cardId) => _db.QuerySingle(
        "SELECT action, idea_id FROM ideas_resolutions WHERE suggestion_id = @id", new { id = cardId },
        r => new CardAnswer(r.GetString("action"), r.GetStringOrNull("idea_id")));

    /// <summary>
    /// Answer a card in one transaction: check what the card asks against what the user answered, write the idea (a new
    /// one, or the existing idea marked done), record the resolution and take the card out. Two windows answering at
    /// once, or the same answer retried after a timeout, therefore have one effect: the second reads the recorded
    /// resolution and reports the same outcome instead of producing it again.
    /// </summary>
    public CardResolution ResolveCard(string cardId, string action, JsonObject? edit, Action<JsonObject>? afterCommit = null)
    {
        var result = _db.Transaction(_ =>
        {
            if (Resolution(cardId) is { } done) // already answered: the same answer, not a second one
                return new CardResolution(done.IdeaId is { Length: > 0 } id ? Find(id)?.Doc : null, true, done.Action);

            var card = Card(cardId) ?? throw new RpcException("not_found", "That card is gone (already answered, discarded, or NetPI restarted).");
            ValidateAnswer(action, card);
            var idea = action switch
            {
                "save" => SaveCard_(card, edit),
                "done" => MarkDone_(IdeaOps.Str(card["ideaId"]) ?? "").Doc,
                _ => null,
            };
            _db.Execute("INSERT INTO ideas_resolutions (suggestion_id, action, idea_id, at) VALUES (@id, @a, @i, @at)",
                new { id = cardId, a = action, i = (object?)IdeaOps.Str(idea?["id"]), at = IdeaOps.Now() });
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
        return Insert_(idea, _db.Scalar<long?>("SELECT MAX(ord) + 1 FROM ideas_items") ?? 0).Doc;
    }

    // ------------------------------------------------------------------ checks (the save check's per-conversation state)

    /// <summary>
    /// Take the check for one conversation revision, or say why not. The claim carries a token and an expiry: a second
    /// close of an unchanged conversation sees the running claim, and a claim left behind by a plugin that died
    /// expires and is retried (bounded by the caller's <paramref name="maxTries"/>) instead of blocking that
    /// conversation forever.
    /// </summary>
    public (bool Started, string Reason, string? Token) ClaimCheck(string sessionId, int users, string rev, int maxTries, TimeSpan retryAfter) => _db.Transaction<(bool, string, string?)>(_ =>
    {
        var now = DateTimeOffset.UtcNow;
        var seen = _db.QuerySingle("SELECT rev, state, tries, at, claim FROM ideas_checks WHERE session_id = @id", new { id = sessionId },
            r => new CheckMark(r.GetString("rev"), r.GetString("state"), r.GetInt64("tries"), r.GetString("at"), r.GetStringOrNull("claim")));
        if (seen is { } row && row.Rev == rev)
        {
            if (row.State is "done" or "running") return (false, "already", (string?)null);
            if (row.State == "failed" && row.Tries >= maxTries && Stamp(row.At) is { } at && now - at < retryAfter)
                return (false, "already", (string?)null);
        }
        var token = Ids.Short(10);
        _db.Execute(
            // The attempts of a revision that is being retried are kept (a check that fails again has to reach its
            // bound); a revision that is new starts counting again.
            "INSERT INTO ideas_checks (session_id, rev, n, state, tries, at, claim, claim_until, error) " +
            "VALUES (@id, @rev, @n, 'running', 0, @at, @claim, @until, NULL) " +
            "ON CONFLICT(session_id) DO UPDATE SET rev = excluded.rev, n = excluded.n, state = 'running', " +
            "tries = CASE WHEN ideas_checks.rev = excluded.rev THEN ideas_checks.tries ELSE 0 END, " +
            "at = excluded.at, claim = excluded.claim, claim_until = excluded.claim_until, error = NULL", new
            {
                id = sessionId, rev, n = (long)users, at = IdeaOps.Now(), claim = token,
                until = now.Add(CheckClaimFor).ToUnixTimeMilliseconds(),
            });
        // Housekeeping in the same transaction: a mark of a conversation nobody checks any more goes away.
        _db.Execute("DELETE FROM ideas_checks WHERE at < @cutoff AND state <> 'running'", new { cutoff = StampOf(now.AddDays(-CheckMarkKeepDays)) });
        return (true, "started", token);
    });

    /// <summary>
    /// The outcome of a check. <paramref name="error"/> null means the check <b>ran</b> — "nothing worth keeping" is an
    /// answer, not a failure — and only a check that could not run leaves the mark retryable. A worker whose claim was
    /// taken over (it expired and a later close started the check again) cannot overwrite the newer outcome.
    /// </summary>
    public bool FinishCheck(string sessionId, string? token, string? error) => _db.Transaction(_ =>
    {
        var seen = _db.QuerySingle("SELECT claim, state FROM ideas_checks WHERE session_id = @id", new { id = sessionId },
            r => (Claim: r.GetStringOrNull("claim"), State: r.GetString("state")));
        if (seen.State != "running") return false; // no mark, or a later close already took it
        if (token is { Length: > 0 } mine && seen.Claim is { Length: > 0 } current && current != mine) return false;
        _db.Execute(
            "UPDATE ideas_checks SET state = @s, at = @at, tries = tries + 1, error = @e, claim = NULL, claim_until = NULL WHERE session_id = @id",
            new { id = sessionId, s = error is null ? "done" : "failed", at = IdeaOps.Now(), e = (object?)Clip(error, 200) });
        return true;
    });

    /// <summary>
    /// A claim nobody owns any more (the plugin was stopped mid-check): at the next start a "running" mark whose claim
    /// has expired becomes a retryable failure, not a claim that blocks the conversation for good.
    /// </summary>
    public int RecoverExpiredClaims()
    {
        var recovered = _db.Execute(
            "UPDATE ideas_checks SET state = 'failed', claim = NULL, claim_until = NULL, at = @at, " +
            "error = COALESCE(error, 'NetPI stopped before the check finished') " +
            "WHERE state = 'running' AND claim_until IS NOT NULL AND claim_until < @now",
            new { now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), at = IdeaOps.Now() });
        if (recovered > 0) _log?.LogInformation("Ideas: {Count} check claim(s) left behind by a stopped run are retryable again", recovered);
        return recovered;
    }

    // ------------------------------------------------------------------ repositories (the commit sweep's cursor)

    public string? LastSeen(string repo) =>
        _db.QuerySingle("SELECT hash FROM ideas_repos WHERE repo = @r", new { r = repo }, x => x.GetStringOrNull("hash"));

    /// <summary>Remember a repository's newest read commit, with the project it belongs to.</summary>
    public void Remember(string repo, string? hash, string? projectId = null, string? projectName = null, string? error = null) => _db.Execute(
        "INSERT INTO ideas_repos (repo, project_id, project_name, hash, at, tries, error) VALUES (@r, @p, @n, @h, @at, 1, @e) " +
        "ON CONFLICT(repo) DO UPDATE SET project_id = COALESCE(excluded.project_id, ideas_repos.project_id), " +
        "project_name = COALESCE(excluded.project_name, ideas_repos.project_name), hash = excluded.hash, at = excluded.at, " +
        "tries = ideas_repos.tries + 1, error = excluded.error",
        new { r = repo, p = (object?)projectId, n = (object?)projectName, h = (object?)hash, at = IdeaOps.Now(), e = (object?)Clip(error, 200) });

    /// <summary>
    /// Anchor a repository this plugin has never read at HEAD — and only then. A stored cursor is the progress made
    /// before a restart, and overwriting it would skip every commit made while NetPI was closed.
    /// </summary>
    public void RememberIfAbsent(string repo, string? hash, string? projectId = null, string? projectName = null)
    {
        if (hash is not { Length: > 0 }) return;
        _db.Execute(
            "INSERT INTO ideas_repos (repo, project_id, project_name, hash, at, tries, error) VALUES (@r, @p, @n, @h, @at, 0, NULL) ON CONFLICT(repo) DO NOTHING",
            new { r = repo, p = (object?)projectId, n = (object?)projectName, h = hash, at = IdeaOps.Now() });
    }

    /// <summary>Forget repositories nothing has read for a while, so the table does not grow with deleted projects.</summary>
    public int ForgetStaleRepos(int keepDays) =>
        _db.Execute("DELETE FROM ideas_repos WHERE at < @cutoff", new { cutoff = StampOf(DateTimeOffset.UtcNow.AddDays(-keepDays)) });

    // ------------------------------------------------------------------ imports and metadata

    public string? Meta(string key) =>
        _db.QuerySingle("SELECT value FROM ideas_metadata WHERE key = @k", new { k = key }, r => r.GetString("value"));

    public void SetMeta(string key, string? value)
    {
        if (value is null) _db.Execute("DELETE FROM ideas_metadata WHERE key = @k", new { k = key });
        else _db.Execute("INSERT INTO ideas_metadata (key, value) VALUES (@k, @v) ON CONFLICT(key) DO UPDATE SET value = excluded.value", new { k = key, v = value });
    }

    public JsonObject? MetaObject(string key)
    {
        var raw = Meta(key);
        if (raw is not { Length: > 0 }) return null;
        try { return JsonNode.Parse(raw) as JsonObject; } catch (JsonException) { return null; }
    }

    /// <summary>Whether an import was committed before (a restart must not import the same source twice).</summary>
    public bool HasImport(string id, string? checksum = null)
    {
        var found = _db.Scalar<string?>("SELECT checksum FROM ideas_imports WHERE id = @id", new { id });
        return found is not null && (checksum is null || found == checksum);
    }

    /// <summary>Record an import in the caller's transaction: the receipt and the data it accounts for commit together.</summary>
    public void RecordImport(string id, string kind, string source, string checksum, JsonObject counts, int schemaVersion = SchemaVersion) =>
        _db.Execute(
            "INSERT INTO ideas_imports (id, kind, source, checksum, schema_version, counts, at) VALUES (@id, @k, @s, @c, @v, @n, @at) " +
            "ON CONFLICT(id) DO UPDATE SET kind = excluded.kind, source = excluded.source, checksum = excluded.checksum, " +
            "schema_version = excluded.schema_version, counts = excluded.counts, at = excluded.at",
            new { id, k = kind, s = source, c = checksum, v = (long)schemaVersion, n = counts.ToJsonString(), at = IdeaOps.Now() });

    public List<JsonObject> Imports() => _db.Query("SELECT id, kind, source, checksum, schema_version, at FROM ideas_imports ORDER BY at, id", null, r => new JsonObject
    {
        ["id"] = r.GetString("id"),
        ["kind"] = r.GetString("kind"),
        ["source"] = r.GetString("source"),
        ["checksum"] = r.GetString("checksum"),
        ["schemaVersion"] = r.GetInt64("schema_version"),
        ["at"] = r.GetString("at"),
    });

    // ------------------------------------------------------------------ the one write path for an idea

    private static readonly JsonDocumentOptions ReadOptions = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };

    /// <summary>Insert a document at a place in the order, and read every projected column back out of it.</summary>
    private (JsonObject Doc, long Revision) Insert_(JsonObject doc, long ord)
    {
        _db.Execute(
            "INSERT INTO ideas_items (id, ord, revision, title, status, priority, project_id, project_name, created_at, updated_at, doc) " +
            "VALUES (@id, @ord, 1, @title, @status, @priority, @pid, @pname, @created, @updated, @doc)", new
            {
                id = IdeaOps.Str(doc["id"]), ord, title = IdeaOps.Str(doc["title"]) ?? "", status = IdeaOps.Str(doc["status"]) ?? "open",
                priority = IdeaOps.Str(doc["priority"]) ?? "medium", pid = (object?)IdeaOps.ProjectOf(doc)?.Id,
                pname = (object?)IdeaOps.ProjectOf(doc)?.Name, created = (object?)IdeaOps.Str(doc["createdAt"]),
                updated = (object?)IdeaOps.Str(doc["updatedAt"]), doc = DocText(doc),
            });
        return (doc, 1);
    }

    /// <summary>The same document at a new revision; the order is the user's, and a patch does not change it.</summary>
    private void Save_(string id, JsonObject doc, long revision) => _db.Execute(
        "UPDATE ideas_items SET revision = @revision, title = @title, status = @status, priority = @priority, " +
        "project_id = @pid, project_name = @pname, created_at = @created, updated_at = @updated, doc = @doc WHERE id = @id", new
        {
            id, revision, title = IdeaOps.Str(doc["title"]) ?? "", status = IdeaOps.Str(doc["status"]) ?? "open",
            priority = IdeaOps.Str(doc["priority"]) ?? "medium", pid = (object?)IdeaOps.ProjectOf(doc)?.Id,
            pname = (object?)IdeaOps.ProjectOf(doc)?.Name, created = (object?)IdeaOps.Str(doc["createdAt"]),
            updated = (object?)IdeaOps.Str(doc["updatedAt"]), doc = DocText(doc),
        });

    private static JsonObject Parse(string json, long revision) =>
        WithRevision(JsonNode.Parse(json, documentOptions: ReadOptions) as JsonObject ?? [], revision);

    /// <summary>
    /// The revision travels with the idea the caller reads, so an editor can submit the version it had. The stored
    /// document itself never carries it: it is a column, not content (see <see cref="DocText"/>).
    /// </summary>
    private static JsonObject WithRevision(JsonObject doc, long revision)
    {
        doc["revision"] = revision;
        return doc;
    }

    /// <summary>The document as it is stored: the idea's own fields, without the projection the caller read it with.</summary>
    private static string DocText(JsonObject doc)
    {
        var copy = (JsonObject)doc.DeepClone();
        copy.Remove("revision");
        return copy.ToJsonString();
    }

    private static long? Number(JsonNode? n) => n switch
    {
        JsonValue v when v.TryGetValue<long>(out var l) => l,
        JsonValue v when v.TryGetValue<double>(out var d) => (long)d,
        _ => null,
    };

    private static string StampOf(DateTimeOffset when) =>
        when.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static DateTimeOffset? Stamp(string? at) => at is { Length: > 0 } s
        && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var when) ? when : null;

    private static string? Clip(string? s, int n) => s is null ? null : s.Length > n ? s[..n] + "…" : s;
}
