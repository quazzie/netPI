using System.Text.Json.Nodes;
using NetPI;
using NetPI.Host.Storage.Sqlite;

namespace NetPI.Migration001;

// The collections this migration creates (plugin netpi.ideas) — the shape the Ideas plugin's IdeasRepository reads and
// writes (plugins/NetPI.Ideas/IdeasRepository.cs), kept here for the one-off store migration:
//   items        key: the idea id
//                doc: the idea's own JSON (the old doc column, the complete document) + revision (long) +
//                     ord (long) + idLower (the key, ASCII-lower-cased) + projectId (only when bound to a project)
//                index: idLower Text, ord Integer, status Text, projectId Text   (status: the idea's own field)
//   cards        key: the card id
//                doc: doc (the card's JSON), ord (long), kind, title, sessionId, ideaId, projectId, sourceRev (long), at
//                index: ord Integer, kind Text, sessionId Text, ideaId Text, title Text
//   resolutions  key: the card id (the suggestion answered)
//                doc: action, ideaId (only when the answer wrote or named an idea), at
//                index: (none)
//   checks       key: the session id
//                doc: rev, n (long), state, tries (long), at, claim (only while a check runs),
//                     claimUntil (long, only while a check runs), error (only when the last check failed)
//                index: state Text, at Text, claimUntil Integer
//   repos        key: the repository path
//                doc: projectId, projectName, hash (only when one was read), at, tries (long), error (only after a failure)
//                index: at Text
//   imports      key: the import id
//                doc: kind, source, checksum, schemaVersion (long), counts (object), at
//                index: (none)
//   meta         key: the key
//                doc: value (a string)
//                index: (none)
//   unread       key: the repository path + '\n' + the commit hash
//                doc: repo, hash, subject, tries (long), error (only when set), at
//                index: at Text
//
// Every document is built the way the repository's own write path builds it (PutItem_, CardForm, PutResolution_, the
// checks/repos/unread puts), so reading it back through IdeasRepository gives the same idea, card or mark: an absent
// property is the same as the old NULL column, and a field a query orders or filters on is written whenever it has a
// value. The index fields are written in the same operation as the document they describe, the way PutItem_ does.
internal static class IdeasMigration
{
    public const string PluginId = "netpi.ideas";

    public static CollectionSpec ItemsSpec() => new CollectionSpec().Text("idLower").Integer("ord").Text("status").Text("projectId");
    public static CollectionSpec CardsSpec() => new CollectionSpec().Integer("ord").Text("kind").Text("sessionId").Text("ideaId").Text("title");
    public static CollectionSpec ResolutionsSpec() => new CollectionSpec();
    public static CollectionSpec ChecksSpec() => new CollectionSpec().Text("state").Text("at").Integer("claimUntil");
    public static CollectionSpec ReposSpec() => new CollectionSpec().Text("at");
    public static CollectionSpec ImportsSpec() => new CollectionSpec();
    public static CollectionSpec MetaSpec() => new CollectionSpec();
    public static CollectionSpec UnreadSpec() => new CollectionSpec().Text("at");

    public static void Run(Database old, IStorage storage, List<string> notes)
    {
        var data = storage.Plugins.For(PluginId);
        var items = data.Collection("items", ItemsSpec());
        var cards = data.Collection("cards", CardsSpec());
        var resolutions = data.Collection("resolutions", ResolutionsSpec());
        var checks = data.Collection("checks", ChecksSpec());
        var repos = data.Collection("repos", ReposSpec());
        var imports = data.Collection("imports", ImportsSpec());
        var meta = data.Collection("meta", MetaSpec());
        var unread = data.Collection("unread", UnreadSpec());

        var itemRows = Read(old, "SELECT id, ord, revision, doc FROM old.ideas_items ORDER BY ord, id",
            r => (r.GetString("id"), r.GetInt64("ord"), r.GetInt64("revision"), r.GetString("doc")));
        var cardRows = Read(old, "SELECT id, ord, kind, title, session_id, idea_id, project_id, source_rev, at, doc FROM old.ideas_suggestions ORDER BY ord, id",
            r => (r.GetString("id"), r.GetInt64("ord"), r.GetString("kind"), r.GetString("title"), r.GetStringOrNull("session_id"),
                  r.GetStringOrNull("idea_id"), r.GetStringOrNull("project_id"), r.GetInt64OrNull("source_rev"), r.GetString("at"), r.GetString("doc")));
        var resolutionRows = Read(old, "SELECT suggestion_id, action, idea_id, at FROM old.ideas_resolutions ORDER BY suggestion_id",
            r => (r.GetString("suggestion_id"), r.GetString("action"), r.GetStringOrNull("idea_id"), r.GetString("at")));
        var checkRows = Read(old, "SELECT session_id, rev, n, state, tries, at, claim, claim_until, error FROM old.ideas_checks ORDER BY session_id",
            r => (r.GetString("session_id"), r.GetString("rev"), r.GetInt64("n"), r.GetString("state"), r.GetInt64("tries"),
                  r.GetString("at"), r.GetStringOrNull("claim"), r.GetInt64OrNull("claim_until"), r.GetStringOrNull("error")));
        var repoRows = Read(old, "SELECT repo, project_id, project_name, hash, at, tries, error FROM old.ideas_repos ORDER BY repo",
            r => (r.GetString("repo"), r.GetStringOrNull("project_id"), r.GetStringOrNull("project_name"), r.GetStringOrNull("hash"),
                  r.GetString("at"), r.GetInt64("tries"), r.GetStringOrNull("error")));
        var importRows = Read(old, "SELECT id, kind, source, checksum, schema_version, counts, at FROM old.ideas_imports ORDER BY id",
            r => (r.GetString("id"), r.GetString("kind"), r.GetString("source"), r.GetString("checksum"), r.GetInt64("schema_version"),
                  r.GetString("counts"), r.GetString("at")));
        var metaRows = Read(old, "SELECT `key`, value FROM old.ideas_metadata ORDER BY `key`",
            r => (r.GetString("key"), r.GetString("value")));
        var unreadRows = Read(old, "SELECT repo, hash, subject, tries, error, at FROM old.ideas_unread ORDER BY repo, hash",
            r => (r.GetString("repo"), r.GetString("hash"), r.GetStringOrNull("subject"), r.GetInt64("tries"), r.GetStringOrNull("error"), r.GetString("at")));

        data.Transaction(() =>
        {
            foreach (var (id, ord, revision, docText) in itemRows)
                items.Put(id, ItemForm(id, revision, ord, docText, notes));
            foreach (var (id, ord, kind, title, sessionId, ideaId, projectId, sourceRev, at, docText) in cardRows)
                cards.Put(id, CardForm(id, docText, ord, kind, title, sessionId, ideaId, projectId, sourceRev, at, notes));
            foreach (var (cardId, action, ideaId, at) in resolutionRows)
            {
                var doc = new JsonObject { ["action"] = action, ["at"] = at };
                if (ideaId is not null) doc["ideaId"] = ideaId;   // the answer wrote or named an idea
                resolutions.Put(cardId, doc);
            }
            foreach (var (sessionId, rev, n, state, tries, at, claim, claimUntil, error) in checkRows)
            {
                var doc = new JsonObject
                {
                    ["rev"] = rev,
                    ["n"] = n,
                    ["state"] = state,
                    ["tries"] = tries,
                    ["at"] = at,
                };
                if (claim is not null) doc["claim"] = claim;               // only while a check runs
                if (claimUntil is not null) doc["claimUntil"] = claimUntil;
                if (error is not null) doc["error"] = error;               // only when the last check failed
                checks.Put(sessionId, doc);
            }
            foreach (var (repo, projectId, projectName, hash, at, tries, error) in repoRows)
            {
                var doc = new JsonObject { ["at"] = at, ["tries"] = tries };
                if (projectId is not null) doc["projectId"] = projectId;
                if (projectName is not null) doc["projectName"] = projectName;
                if (hash is not null) doc["hash"] = hash;                  // only when one was read
                if (error is not null) doc["error"] = error;               // only after a failure
                repos.Put(repo, doc);
            }
            foreach (var (id, kind, source, checksum, schemaVersion, countsText, at) in importRows)
            {
                var counts = Jsonx.Object(countsText);
                if (counts is null)
                {
                    // The old column promised an object ({} the default); anything else is carried as it was.
                    notes.Add($"import {id}: the counts column is not a JSON object; kept as-is");
                    counts = Jsonx.ArrayOrRaw(countsText) as JsonObject ?? new JsonObject();
                }
                imports.Put(id, new JsonObject
                {
                    ["kind"] = kind,
                    ["source"] = source,
                    ["checksum"] = checksum,
                    ["schemaVersion"] = schemaVersion,
                    ["counts"] = counts,
                    ["at"] = at,
                });
            }
            foreach (var (key, value) in metaRows)
                meta.Put(key, new JsonObject { ["value"] = value });
            foreach (var (repo, hash, subject, tries, error, at) in unreadRows)
            {
                var doc = new JsonObject
                {
                    ["repo"] = repo,
                    ["hash"] = hash,
                    ["tries"] = tries,
                    ["at"] = at,
                };
                if (subject is not null) doc["subject"] = subject;
                if (error is not null) doc["error"] = error;
                unread.Put(repo + "\n" + hash, doc);
            }
        });
    }

    /// <summary>The old doc column (the idea's own JSON) with the index fields PutItem_ writes over it.</summary>
    private static JsonObject ItemForm(string id, long revision, long ord, string docText, List<string> notes)
    {
        var doc = Jsonx.Object(docText);
        if (doc is null)
        {
            // The old doc is written by NetPI's own code (the idea's JSON as text), so this is a tampered or damaged row:
            // the row is kept (its counters still migrate), its content is not.
            if (docText.Length > 0)
                notes.Add($"idea {id}: the doc column does not parse as a JSON object; stored without its content");
            doc = new JsonObject();
        }
        doc.Remove("revision");
        doc["revision"] = revision;
        doc["ord"] = ord;
        doc["idLower"] = Lower(id);
        var project = ProjectOf(doc);
        if (project is { } p) doc["projectId"] = p.Id;
        else doc.Remove("projectId");   // unbound: the field is absent, not empty
        return doc;
    }

    /// <summary>CardForm in IdeasRepository: the card's JSON under "doc" plus the fields its queries filter on.
    /// A doc that does not parse is rebuilt from the row's columns (the projections of the same card) and noted.</summary>
    private static JsonObject CardForm(string id, string docText, long ord, string kind, string title, string? sessionId, string? ideaId,
        string? projectId, long? sourceRev, string at, List<string> notes)
    {
        var card = Jsonx.Object(docText);
        if (card is null)
        {
            card = new JsonObject();
            if (docText.Length > 0)
            {
                notes.Add($"card {id}: the doc column does not parse as a JSON object; rebuilt from the row's columns");
                var project = new JsonObject();
                if (projectId is not null) project["id"] = projectId;
                card["project"] = project;
            }
            card["id"] = id;
            card["kind"] = kind;
            card["title"] = title;
            if (sessionId is not null) card["sessionId"] = sessionId;
            if (ideaId is not null) card["ideaId"] = ideaId;
            if (sourceRev is not null) card["ideaRevision"] = sourceRev;
            card["at"] = at;
        }
        var doc = new JsonObject { ["doc"] = (JsonObject)card.DeepClone() };
        doc["ord"] = ord;
        doc["kind"] = Str(card["kind"]) ?? "save";
        doc["title"] = Str(card["title"]) ?? "";
        if (Str(card["sessionId"]) is { } s) doc["sessionId"] = s;
        if (Str(card["ideaId"]) is { } i) doc["ideaId"] = i;
        if (Str((card["project"] as JsonObject)?["id"]) is { } p) doc["projectId"] = p;
        if (SourceRevision(card) is { } r) doc["sourceRev"] = r;
        doc["at"] = Str(card["at"]) ?? at;
        return doc;
    }

    // ------------------------------------------------------------------ the repository's own helpers, mirrored
    // (the migration references only the Host and the contracts, so it cannot call the plugin's IdeaOps)

    /// <summary>SQLite's lower() folded ASCII only; idLower is compared ordinally, so the fold must match it.</summary>
    private static string Lower(string s) => string.Concat(s.Select(c => c is >= 'A' and <= 'Z' ? (char)(c + 32) : c));

    /// <summary>IdeaOps.ProjectOf: the project an idea's own "project" field names (an object or a bare id).</summary>
    private static (string Id, string? Name)? ProjectOf(JsonObject? doc) => doc?["project"] switch
    {
        JsonObject o => Str(o["id"]) is { Length: > 0 } id ? (id, Str(o["name"])) : null,
        JsonValue v when v.TryGetValue<string>(out var s) && s.Length > 0 => (s, null),
        _ => null,
    };

    /// <summary>The idea revision a card was made from, when it is about an idea.</summary>
    private static long? SourceRevision(JsonObject card) =>
        card["ideaRevision"] is JsonValue v && v.TryGetValue<long>(out var n) ? n : null;

    /// <summary>IdeaOps.Str: a JSON value as the text it is (a non-text value as its JSON).</summary>
    private static string? Str(JsonNode? n) => n switch
    {
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonValue v => v.ToJsonString(),
        _ => null,
    };

    /// <summary>One old table as rows; a table an older old build never created is nothing to migrate.</summary>
    private static List<T> Read<T>(Database old, string sql, Func<ISqlRow, T> map)
    {
        try
        {
            return old.Query(sql, null, map).ToList();
        }
        catch (SqliteException)
        {
            return [];
        }
    }
}
