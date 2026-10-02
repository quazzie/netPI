using System.Text.Json.Nodes;
using NetPI;
using NetPI.Host.Storage.Sqlite;

namespace NetPI.Migration001;

/// <summary>
/// The core tables: projects, sessions, messages and kv, copied set-based from the attached old database into the fresh
/// new one (ids and seqs preserved, in this order), then the session workspaces moved out of the <c>workspace_id</c> column
/// into the session's meta (<c>meta.workspaceId</c> + <c>meta.cwd</c>, as the Workspaces plugin now writes them), and the
/// remembered fork-reset keys seeded in <c>kv</c>. The copy takes the intersection of the old table's columns and the new
/// table's columns, so an old database at a lower migration version (no workspace_id, no pinned, no projects.meta) still
/// copies whole.
/// </summary>
internal static class CoreCopy
{
    /// <summary>The new core's own columns, in its own order (the old sessions' workspace_id is the one column left out).</summary>
    private static readonly string[] ProjectColumns =
        ["id", "name", "path", "created_at", "updated_at", "last_used_at", "meta"];
    private static readonly string[] SessionColumns =
        ["id", "title", "project_id", "parent_session_id", "kind", "model", "reasoning", "created_at", "updated_at",
         "archived", "pinned", "message_count", "context_tokens", "meta"];
    private static readonly string[] MessageColumns =
        ["id", "session_id", "seq", "role", "parts", "created_at", "provider", "model", "stop_reason", "usage",
         "duration_ms", "compacted", "meta"];

    /// <summary>
    /// The remembered fork-reset keys the session service drops from a fork's meta (and the plugins re-declare at start):
    /// today's list, seeded so a fork behaves as before the swap. A value already in kv is merged, not replaced.
    /// </summary>
    public static readonly string[] ForkResetKeys =
        ["goal", "todo", "budgetAllowedFrom", "guardrailsAllowed", "agentId", "parentAgentId", "agentInstructions", "runtimeEnvironment"];

    /// <summary>Refuses a database that is not the old shape (and says which way it is off).</summary>
    public static void CheckOldShape(Database db)
    {
        if (!TableExists(db, "sessions")) throw Aborted("not a NetPI database: no sessions table");
        if (TableExists(db, "_collections")) throw Aborted("already in the new storage shape (a _collections table): nothing to migrate");
        var oldEnough = HasColumn(db, "old.sessions", "workspace_id") || TableExists(db, "workspaces") || TableExists(db, "_migrations");
        if (!oldEnough) throw Aborted("not recognized as the old shape: no sessions.workspace_id, no workspaces table, no _migrations table");
    }

    public static void Run(Database db, List<string> notes)
    {
        db.Transaction(_ =>
        {
            CopyTable(db, "projects", ProjectColumns);
            CopyTable(db, "sessions", SessionColumns);
            CopyTable(db, "messages", MessageColumns);
            db.Execute("INSERT INTO kv(key, value) SELECT key, value FROM old.kv");
            BindWorkspaces(db, notes);
            SeedForkResetKeys(db, notes);
        });
    }

    /// <summary>One <c>INSERT ... SELECT</c> per table, over the columns the old table actually has.</summary>
    private static void CopyTable(Database db, string table, string[] newColumns)
    {
        var oldColumns = TableColumns(db, "old." + table);
        if (oldColumns is null) return;   // a table the old build never created: nothing to copy
        var cols = newColumns.Where(oldColumns.Contains).ToList();
        if (cols.Count == 0) return;
        var list = string.Join(", ", cols);
        db.Execute($"INSERT INTO {table}({list}) SELECT {list} FROM old.{table}");
    }

    /// <summary>The column names of an old table, or null when the table does not exist (an empty table has no rows to name).</summary>
    private static HashSet<string>? TableColumns(Database db, string table)
    {
        try
        {
            return db.QuerySingle($"SELECT * FROM {table} LIMIT 1", null, r =>
            {
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < r.FieldCount; i++) names.Add(r.GetName(i));
                return names;
            });
        }
        catch (SqliteException) { return null; }
    }

    /// <summary>
    /// The binding leaves the sessions table and moves into meta, one session per update (sessions are the few): a
    /// workspace_id with a workspace row becomes meta.workspaceId + meta.cwd (the workspace's path); one without is dropped.
    /// </summary>
    private static void BindWorkspaces(Database db, List<string> notes)
    {
        if (!HasColumn(db, "old.sessions", "workspace_id")) return;   // an old database from before workspaces: nothing to bind
        var paths = new Dictionary<string, string>(StringComparer.Ordinal);
        if (TableExists(db, "workspaces"))
            foreach (var (id, path) in db.Query("SELECT id, path FROM old.workspaces", null, r => (r.GetString("id"), r.GetString("path"))))
                paths[id] = path;
        var bound = db.Query("SELECT id, workspace_id, meta FROM old.sessions WHERE workspace_id IS NOT NULL", null,
            r => (r.GetString("id"), r.GetString("workspace_id"), r.GetStringOrNull("meta"))).ToList();
        foreach (var (id, workspaceId, metaText) in bound)
        {
            if (!paths.TryGetValue(workspaceId, out var path))
            {
                notes.Add($"session {id}: workspace_id '{workspaceId}' has no workspace row; dropped");
                continue;
            }
            var meta = Jsonx.Object(metaText) ?? new JsonObject();
            meta["workspaceId"] = workspaceId;
            meta["cwd"] = path;
            db.Execute("UPDATE sessions SET meta = @meta WHERE id = @id", new { meta = meta.ToJsonString(), id });
        }
    }

    private static void SeedForkResetKeys(Database db, List<string> notes)
    {
        var set = new SortedSet<string>(ForkResetKeys, StringComparer.Ordinal);
        var existing = db.Scalar<string>("SELECT value FROM kv WHERE key = 'fork.resetKeys'");
        if (existing is not null)
            if (JsonNode.Parse(existing) is JsonArray old)
                foreach (var n in old)
                    if (n is JsonValue v && v.TryGetValue<string>(out var s)) set.Add(s);
            else notes.Add("kv 'fork.resetKeys' was not a JSON array of strings; it is replaced by today's list");
        var json = new JsonArray([.. set.Select(k => (JsonNode?)JsonValue.Create(k))]).ToJsonString();
        db.Execute("""
            INSERT INTO kv(key, value) VALUES('fork.resetKeys', @value)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value
            """, new { value = json });
    }

    private static bool TableExists(Database db, string table) =>
        db.Scalar<long>("SELECT COUNT(*) FROM old.sqlite_master WHERE type = 'table' AND name = @t", new { t = table }) is > 0;

    private static bool HasColumn(Database db, string table, string column)
    {
        try
        {
            db.Scalar<string>($"SELECT {column} FROM {table} LIMIT 1");
            return true;
        }
        catch (SqliteException) { return false; }
    }

    private static MigrationAbortedException Aborted(string reason) => new(reason);
}
