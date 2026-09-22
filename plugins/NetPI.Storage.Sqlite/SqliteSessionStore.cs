using System.Text.Json;
using Microsoft.Data.Sqlite;
using NetPI.Abstractions;

namespace NetPI.Storage.Sqlite;

/// <summary>
/// Append-only session storage on SQLite (PLAN §29-§31).
///
/// astra-1 B: the store no longer holds one long-lived connection. Every
/// operation opens a short-lived, pooled connection (connection string carries
/// Pooling=true), enables per-connection pragmas, and disposes the connection in
/// a <c>using</c> (finally) block. WAL + synchronous=NORMAL are applied once on
/// the initial schema-initializing connection (WAL is persistent per-DB, so it
/// survives to every later pooled connection); foreign_keys is a per-connection
/// setting and is re-enabled on every connection.
///
/// Multi-statement mutations run inside a single SQLite transaction. An append
/// atomically (1) allocates the next sequence (MAX+1 under the WAL write lock),
/// (2) inserts the entry and (3) updates session metadata, then commits — so a
/// failure at any point rolls back the whole transaction and metadata can never
/// diverge from the transcript. Writers are serialized by the DB itself (WAL +
/// transactions + busy timeout), NOT by any instance-level C# lock, which is
/// what a plugin reload (old + new store instance alive at once on the same
/// file) requires.
///
/// Migrations are explicit and versioned: a <c>migrations</c> table records the
/// applied versions and each version's DDL is idempotent (guarded CREATE /
/// existence-checked ALTER), so re-opening the store re-runs the runner but
/// applies nothing that was already applied.
///
/// Schema (v3):
/// <code>
///   migrations(version INTEGER PK, applied_at TEXT)
///   sessions(id TEXT PK, title, workspace, provider_id, model_id,
///            reasoning_level, created_at, updated_at, last_sequence,
///            project_id INTEGER NULL,               -- set by Package C
///            context_revision INTEGER NOT NULL DEFAULT 0)
///   session_entries(id TEXT PK, session_id, seq, entry_type, created_at,
///                   payload_json, FK -> sessions ON DELETE CASCADE,
///                   UNIQUE (session_id, seq))
///   projects(id TEXT PK, name, workspace_path,
///            normalized_path_key TEXT UNIQUE NOT NULL, created_at, updated_at)
///   settings(key TEXT PK, value_json)
///   pending_project_changes(session_id TEXT PK, operation_id, project_id,
///                        payload_json, enqueued_at)          -- astra-1 D2
/// </code>
/// The <c>sessions.workspace</c> column is preserved for compatibility.
/// </summary>
public sealed class SqliteSessionStore : ISessionStore, IDisposable
{
    /// <summary>Highest migration version applied by this store.</summary>
    public const int CurrentSchemaVersion = 7;

    /// <summary>
    /// astra-1 B: canonical key for deduplicating project workspace paths —
    /// Windows paths are case-insensitive and slash-style agnostic, so
    /// <c>C:\AI\X</c> and <c>c:/ai/x</c> normalize to the same key. Package C
    /// keys <c>projects.normalized_path_key</c> uniqueness on this.
    /// </summary>
    public static string NormalizeProjectPathKey(string? path)
        => (path ?? string.Empty).Replace('\\', '/').TrimEnd('/').ToLowerInvariant();

    private readonly string _connStr;
    private readonly object _migrationLock = new();

    /// <summary>Path to the database file (exposed for diagnostics).</summary>
    public string DbPath { get; }

    public SqliteSessionStore(string dbPath)
    {
        DbPath = dbPath;
        var dir = Path.GetDirectoryName(Path.GetFullPath(dbPath));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        // Pooled short-lived connections. A generous busy timeout lets
        // concurrent writers (e.g. old + new store instance during a plugin
        // reload) wait for the WAL write lock instead of failing immediately.
        _connStr = BuildConnectionString(dbPath);

        // One short-lived connection performs schema init + migrations.
        using (var init = OpenConnection())
        {
            // WAL is persistent per database file: setting it once here means
            // every later (pooled) connection inherits it. synchronous is
            // per-connection and is applied below where a write happens.
            RunPragma(init, "PRAGMA journal_mode=WAL;");
            RunMigrations(init);
        }
    }


    /// <summary>
    /// Pooled short-lived connection string — a generous busy timeout lets
    /// concurrent writers (e.g. old + new store instance during a plugin
    /// reload) wait for the WAL write lock instead of failing immediately.
    /// Shared with <see cref="SqliteProjectStore"/> (same file, same pragmas).
    /// </summary>
    public static string BuildConnectionString(string dbPath)
        => new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
            DefaultTimeout = 30,
        }.ToString();

    // ---- migrations ----------------------------------------------------

    /// <summary>
    /// astra-1 B: explicit, versioned, idempotent migrations. Each version's
    /// DDL is guarded (IF NOT EXISTS / column-existence checks) and is recorded
    /// in the <c>migrations</c> table only after it applies, so re-opening the
    /// store (fresh constructor call, plugin reload) re-runs the runner but is a
    /// no-op for anything already applied.
    /// </summary>
    private void RunMigrations(SqliteConnection conn)
    {
        lock (_migrationLock)
        {
            ExecuteSql(conn,
                """
                CREATE TABLE IF NOT EXISTS migrations (
                    version    INTEGER PRIMARY KEY,
                    applied_at TEXT NOT NULL
                );
                """);

            foreach (var version in new[] { 1, 2, 3, 4, 5, 6, 7 })
            {
                if (IsMigrationApplied(conn, version)) continue;

                using var tx = conn.BeginTransaction();
                try
                {
                    ApplyVersion(conn, tx, version);
                    RecordMigration(conn, tx, version);
                    tx.Commit();
                }
                catch
                {
                    tx.Rollback();
                    throw;
                }
            }
        }
    }

    private static bool IsMigrationApplied(SqliteConnection conn, int version)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT EXISTS(SELECT 1 FROM migrations WHERE version = $v);";
        cmd.Parameters.AddWithValue("$v", version);
        var raw = cmd.ExecuteScalar();
        return Convert.ToInt32(raw) == 1;
    }

    private static void RecordMigration(SqliteConnection conn, SqliteTransaction tx, int version)
        => ExecuteSql(conn,
            "INSERT INTO migrations (version, applied_at) VALUES ($v, $now);",
            cmd =>
            {
                cmd.Parameters.AddWithValue("$v", version);
                cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            }, tx);

    private static void ApplyVersion(SqliteConnection conn, SqliteTransaction tx, int version)
    {
        switch (version)
        {
            case 1:
                // Baseline. On a fresh DB this creates everything; on a legacy
                // pre-migration DB every statement is a guarded no-op (the old
                // tables already match this shape) and only the record is added.
                ExecuteSql(conn, """
                    CREATE TABLE IF NOT EXISTS sessions (
                        id              TEXT PRIMARY KEY,
                        title           TEXT,
                        workspace       TEXT,
                        provider_id     TEXT,
                        model_id        TEXT,
                        reasoning_level TEXT,
                        created_at      TEXT NOT NULL,
                        updated_at      TEXT NOT NULL,
                        last_sequence   INTEGER NOT NULL DEFAULT 0
                    );
                    """, null, tx);
                ExecuteSql(conn, """
                    CREATE TABLE IF NOT EXISTS session_entries (
                        id           TEXT PRIMARY KEY,
                        session_id   TEXT NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
                        seq          INTEGER NOT NULL,
                        entry_type   TEXT NOT NULL,
                        created_at   TEXT NOT NULL,
                        payload_json TEXT NOT NULL,
                        UNIQUE (session_id, seq)
                    );
                    """, null, tx);
                ExecuteSql(conn,
                    "CREATE INDEX IF NOT EXISTS ix_sessions_updated ON sessions(updated_at);", null, tx);
                ExecuteSql(conn,
                    "CREATE INDEX IF NOT EXISTS ix_entries_session_seq ON session_entries(session_id, seq);", null, tx);
                ExecuteSql(conn, """
                    CREATE TABLE IF NOT EXISTS settings (
                        key        TEXT PRIMARY KEY,
                        value_json TEXT NOT NULL
                    );
                    """, null, tx);
                break;

            case 2:
                // astra-1 B: project foundation for Package C. Backward
                // compatible — new nullable/defaulted columns; existing rows
                // are left untouched (project_id NULL, context_revision 0,
                // workspace preserved).
                ExecuteSql(conn, """
                    CREATE TABLE IF NOT EXISTS projects (
                        id                TEXT PRIMARY KEY,
                        name              TEXT,
                        workspace_path    TEXT,
                        normalized_path_key TEXT UNIQUE NOT NULL,
                        created_at        TEXT NOT NULL,
                        updated_at        TEXT NOT NULL
                    );
                    """, null, tx);
                if (!HasColumn(conn, tx, "sessions", "project_id"))
                    ExecuteSql(conn, "ALTER TABLE sessions ADD COLUMN project_id INTEGER;", null, tx);
                if (!HasColumn(conn, tx, "sessions", "context_revision"))
                    ExecuteSql(conn,
                        "ALTER TABLE sessions ADD COLUMN context_revision INTEGER NOT NULL DEFAULT 0;", null, tx);
                break;

            case 3:
                // astra-1 D2: pending project changes — one row per session
                // (a selection made while the session's run is in flight).
                // The table is read/written by PendingProjectChangeStore
                // (same file, same pooled-connection pattern); the session
                // store only owns the DDL.
                ExecuteSql(conn, """
                    CREATE TABLE IF NOT EXISTS pending_project_changes (
                        session_id   TEXT PRIMARY KEY,
                        operation_id TEXT NOT NULL,
                        project_id   TEXT NOT NULL,
                        payload_json TEXT NOT NULL,
                        enqueued_at  INTEGER NOT NULL
                    );
                    """, null, tx);
                break;

            case 4:
                // astra-2 §7: agent orchestration records (Package A persistence).
                // All lifecycle/phase/execution-mode values are stored as their
                // stable lower-case strings (AgentAssignmentLifecycleNames /
                // AgentState strings) — the wire and panels never use enum ToString.
                // Timestamps are unix milliseconds. Enforced invariants:
                //   * agents.session_id is UNIQUE (one session per agent).
                //   * agent_assignments enforces ONE nonterminal assignment per
                //     session via a partial unique index (terminal rows are history).
                ExecuteSql(conn, """
                    CREATE TABLE IF NOT EXISTS agent_teams (
                        id            TEXT PRIMARY KEY,
                        title         TEXT,
                        mode          TEXT NOT NULL,
                        pools_json    TEXT,
                        budgets_json  TEXT,
                        created_at    INTEGER NOT NULL,
                        updated_at    INTEGER NOT NULL
                    );
                    """, null, tx);
                ExecuteSql(conn, """
                    CREATE TABLE IF NOT EXISTS agents (
                        agent_id        TEXT PRIMARY KEY,
                        team_id         TEXT,
                        parent_agent_id TEXT,
                        session_id      TEXT UNIQUE NOT NULL,
                        title           TEXT,
                        is_child        INTEGER NOT NULL DEFAULT 0,
                        created_at      INTEGER NOT NULL
                    );
                    """, null, tx);
                ExecuteSql(conn, """
                    CREATE TABLE IF NOT EXISTS agent_assignments (
                        assignment_id   TEXT PRIMARY KEY,
                        run_id          TEXT,
                        agent_id        TEXT NOT NULL REFERENCES agents(agent_id),
                        team_id         TEXT,
                        session_id      TEXT NOT NULL,
                        parent_agent_id TEXT,
                        lifecycle       TEXT NOT NULL,
                        phase           TEXT NOT NULL,
                        execution_mode  TEXT NOT NULL,
                        pool_id         TEXT,
                        lane_id         TEXT,
                        deployment_id   TEXT,
                        model_id        TEXT,
                        title           TEXT,
                        brief_ref       TEXT,
                        checkpoint_ref  TEXT,
                        result_ref      TEXT,
                        ready_seq       INTEGER NOT NULL DEFAULT 0,
                        created_at      INTEGER NOT NULL,
                        started_at      INTEGER,
                        ended_at        INTEGER,
                        reason          TEXT,
                        version         INTEGER NOT NULL DEFAULT 0
                    );
                    """, null, tx);
                ExecuteSql(conn, """
                    CREATE UNIQUE INDEX IF NOT EXISTS uq_assignments_nonterminal
                        ON agent_assignments(session_id)
                        WHERE lifecycle NOT IN ('completed', 'failed', 'cancelled');
                    """, null, tx);
                ExecuteSql(conn, """
                    CREATE INDEX IF NOT EXISTS ix_assignments_agent ON agent_assignments(agent_id);
                    """, null, tx);
                ExecuteSql(conn, """
                    CREATE TABLE IF NOT EXISTS agent_waits (
                        wait_id          TEXT PRIMARY KEY,
                        agent_id         TEXT NOT NULL REFERENCES agents(agent_id),
                        assignment_id    TEXT,
                        any_all          INTEGER NOT NULL DEFAULT 0,
                        targets_json     TEXT NOT NULL,
                        satisfied        INTEGER NOT NULL DEFAULT 0,
                        deadline         INTEGER,
                        continuation_json TEXT,
                        created_at       INTEGER NOT NULL,
                        satisfied_at     INTEGER
                    );
                    """, null, tx);
                ExecuteSql(conn, """
                    CREATE TABLE IF NOT EXISTS agent_messages (
                        message_id       TEXT PRIMARY KEY,
                        from_agent_id     TEXT NOT NULL,
                        to_agent_id       TEXT NOT NULL,
                        team_id           TEXT,
                        kind              TEXT NOT NULL,
                        body              TEXT NOT NULL,
                        artifact_refs_json TEXT,
                        recipient_seq     INTEGER NOT NULL,
                        consumed          INTEGER NOT NULL DEFAULT 0,
                        idempotency_key   TEXT,
                        sent_at           INTEGER NOT NULL
                    );
                    """, null, tx);
                ExecuteSql(conn, """
                    CREATE UNIQUE INDEX IF NOT EXISTS uq_messages_recipient_seq
                        ON agent_messages(to_agent_id, recipient_seq);
                    """, null, tx);
                ExecuteSql(conn, """
                    CREATE INDEX IF NOT EXISTS ix_messages_to_consumed
                        ON agent_messages(to_agent_id, consumed, recipient_seq);
                    """, null, tx);
                ExecuteSql(conn, """
                    CREATE TABLE IF NOT EXISTS agent_checkpoints (
                        checkpoint_id     TEXT PRIMARY KEY,
                        assignment_id     TEXT NOT NULL,
                        agent_id          TEXT NOT NULL,
                        schema_version    INTEGER NOT NULL,
                        transcript_cursor INTEGER,
                        workspace_context TEXT,
                        project_context   TEXT,
                        mailbox_cursor    INTEGER NOT NULL DEFAULT 0,
                        budgets_json      TEXT,
                        resume_json       TEXT,
                        created_at        INTEGER NOT NULL
                    );
                    """, null, tx);
                ExecuteSql(conn, """
                    CREATE TABLE IF NOT EXISTS agent_lane_journal (
                        seq               INTEGER PRIMARY KEY AUTOINCREMENT,
                        host_epoch        TEXT NOT NULL,
                        assignment_id     TEXT NOT NULL,
                        pool_id           TEXT,
                        lane_id           TEXT,
                        state             TEXT NOT NULL,
                        created_at        INTEGER NOT NULL
                    );
                    """, null, tx);
                ExecuteSql(conn, """
                    CREATE TABLE IF NOT EXISTS agent_tasks (
                        task_id          TEXT PRIMARY KEY,
                        team_id          TEXT NOT NULL,
                        title            TEXT,
                        owner_agent_id   TEXT,
                        status           TEXT NOT NULL,
                        depends_on_json  TEXT,
                        version          INTEGER NOT NULL DEFAULT 0,
                        created_at       INTEGER NOT NULL,
                        updated_at       INTEGER NOT NULL
                    );
                    """, null, tx);
                break;
            case 5:
                // astra-2 10/11 (package F): persisted session mode + the shared
                // cloud budget/reservation tables. All additive and guarded:
                // an existing DB gains a nullable mode column and two new
                // tables; nothing here mutates existing rows.
                if (!HasColumn(conn, tx, "sessions", "mode"))
                    ExecuteSql(conn, "ALTER TABLE sessions ADD COLUMN mode TEXT;", null, tx);
                ExecuteSql(conn, """
                    CREATE TABLE IF NOT EXISTS cloud_budgets (
                        team_id        TEXT PRIMARY KEY,
                        currency       TEXT NOT NULL DEFAULT '',
                        unit           TEXT NOT NULL DEFAULT 'currency',
                        limit_value    REAL NOT NULL,
                        spent          REAL NOT NULL DEFAULT 0,
                        updated_at     INTEGER NOT NULL
                    );
                    """, null, tx);
                ExecuteSql(conn, """
                    CREATE TABLE IF NOT EXISTS cloud_budget_reservations (
                        reservation_id TEXT PRIMARY KEY,
                        team_id        TEXT NOT NULL REFERENCES cloud_budgets(team_id) ON DELETE CASCADE,
                        run_id         TEXT,
                        estimated      REAL NOT NULL,
                        actual         REAL,
                        convert_rate   REAL NOT NULL DEFAULT 1,
                        state          TEXT NOT NULL,          -- reserved | settled | released
                        created_at     INTEGER NOT NULL,
                        settled_at     INTEGER
                    );
                    """, null, tx);
                // Guarded upgrade: a DB that applied v5 before convert_rate was
                // added keeps the old row shape; grow it in place (idempotent).
                if (!HasColumn(conn, tx, "cloud_budget_reservations", "convert_rate"))
                    ExecuteSql(conn, "ALTER TABLE cloud_budget_reservations ADD COLUMN convert_rate REAL NOT NULL DEFAULT 1;", null, tx);
                ExecuteSql(conn,
                    "CREATE INDEX IF NOT EXISTS ix_cloud_res_team ON cloud_budget_reservations(team_id, state);", null, tx);
                break;
            case 6:
                // astra-2 §15.D: one in-flight tool-batch checkpoint per
                // assignment — a crash between a successful tool effect and
                // result persistence is exactly the row the recovery path keys
                // on. The table itself (v4) is unchanged.
                ExecuteSql(conn, """
                    CREATE UNIQUE INDEX IF NOT EXISTS uq_checkpoints_assignment
                        ON agent_checkpoints(assignment_id);
                    """, null, tx);
                break;
            case 7:
                // astra-2 §15.C: workspace mode/ownership — each assignment
                // records the workspace mode its child was spawned with
                // (shared-read | isolated-worktree | shared-write) and the
                // concrete workspace the run executes in (a worktree path for
                // isolated children). Additive nullable columns; existing rows
                // stay NULL (historical default: shared-read / inherited workspace).
                if (!HasColumn(conn, tx, "agent_assignments", "workspace_mode"))
                    ExecuteSql(conn, "ALTER TABLE agent_assignments ADD COLUMN workspace_mode TEXT;", null, tx);
                if (!HasColumn(conn, tx, "agent_assignments", "workspace_path"))
                    ExecuteSql(conn, "ALTER TABLE agent_assignments ADD COLUMN workspace_path TEXT;", null, tx);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(version));
        }
    }

    /// <summary>ALTER TABLE ADD COLUMN is not idempotent in SQLite — guard on existence first.</summary>
    private static bool HasColumn(SqliteConnection conn, SqliteTransaction tx, string table, string column)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"PRAGMA table_info({table});";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    // ---- low-level helpers ----------------------------------------------

    private static void RunPragma(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private SqliteConnection OpenConnection()
    {
        var conn = new SqliteConnection(_connStr);
        conn.Open();
        RunPragma(conn, "PRAGMA foreign_keys=ON;");   // per-connection setting
        return conn;
    }

    private static void ExecuteSql(SqliteConnection conn, string sql,
        Action<SqliteCommand>? configure = null, SqliteTransaction? tx = null)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        configure?.Invoke(cmd);
        cmd.ExecuteNonQuery();
    }

    private static async ValueTask<int> NextSeqAsync(SqliteCommand cmd, string sessionId)
    {
        cmd.CommandText = "SELECT COALESCE(MAX(seq), 0) + 1 FROM session_entries WHERE session_id = $s;";
        cmd.Parameters.Clear();
        cmd.Parameters.AddWithValue("$s", sessionId);
        var raw = await cmd.ExecuteScalarAsync();
        return raw is long l ? (int)l : 1;
    }

    // ---- sessions ------------------------------------------------------

    public async ValueTask<SessionInfo> CreateAsync(string? workspacePath, CancellationToken ct = default)
    {
        var id = Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await using var conn = OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO sessions (id, title, workspace, created_at, updated_at, last_sequence)
            VALUES ($id, 'untitled', $ws, $now, $now, 0);
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$ws", workspacePath ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$now", now);
        await cmd.ExecuteNonQueryAsync(ct);
        return await GetAsync(id, ct)!;
    }

    public async ValueTask<SessionInfo?> GetAsync(string id, CancellationToken ct = default)
    {
        await using var conn = OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT s.id, s.title, s.workspace, s.created_at, s.updated_at, s.model_id, s.reasoning_level,
                   COALESCE((SELECT COUNT(*) FROM session_entries e WHERE e.session_id = s.id), 0),
                   s.project_id, s.mode
            FROM sessions s WHERE s.id = $id;
            """;
        cmd.Parameters.AddWithValue("$id", id);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new SessionInfo(
            reader.GetString(0),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            FromTicks(reader.GetInt64(3)),
            FromTicks(reader.GetInt64(4)),
            (int)reader.GetInt64(7),
            reader.IsDBNull(1) ? null : reader.GetString(1))
        {
            ModelId = reader.IsDBNull(5) ? null : reader.GetString(5),
            ReasoningLevel = reader.IsDBNull(6) ? null : reader.GetString(6),
            ProjectId = reader.IsDBNull(8) ? null : reader.GetString(8),
            Mode = reader.IsDBNull(9) ? null : reader.GetString(9),
        };
    }

    public async ValueTask<IReadOnlyList<SessionInfo>> ListAsync(int count = 50, int offset = 0, CancellationToken ct = default)
    {
        var list = new List<SessionInfo>();
        await using var conn = OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, title, workspace, created_at, updated_at, project_id
            FROM sessions ORDER BY updated_at DESC LIMIT $count OFFSET $offset;
            """;
        cmd.Parameters.AddWithValue("$count", count);
        cmd.Parameters.AddWithValue("$offset", Math.Max(0, offset));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            list.Add(ReadSessionInfo(reader));
        return list;
    }

    // astra-1 G1: server-side session search — the global picker must search ALL
    // stored sessions, not just the pages already loaded client-side. Case-insensitive
    // title/workspace match (SQLite LIKE is case-insensitive for ASCII; the title
    // column is indexed-free — session counts stay small).
    public async ValueTask<IReadOnlyList<SessionInfo>> SearchAsync(string query, int count = 100, int offset = 0, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return await ListAsync(count, offset, ct);
        var list = new List<SessionInfo>();
        var like = "%" + query.Trim().Replace("%", "\\%").Replace("_", "\\_") + "%";
        await using var conn = OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, title, workspace, created_at, updated_at, project_id
            FROM sessions
            WHERE (title IS NOT NULL AND title LIKE $q ESCAPE '\') OR (workspace IS NOT NULL AND workspace LIKE $q ESCAPE '\')
            ORDER BY updated_at DESC LIMIT $count OFFSET $offset;
            """;
        cmd.Parameters.AddWithValue("$q", like);
        cmd.Parameters.AddWithValue("$count", Math.Max(1, count));
        cmd.Parameters.AddWithValue("$offset", Math.Max(0, offset));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            list.Add(ReadSessionInfo(reader));
        return list;
    }

    public async ValueTask<int> SearchCountAsync(string query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return await CountAsync(ct);
        var like = "%" + query.Trim().Replace("%", "\\%").Replace("_", "\\_") + "%";
        await using var conn = OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*) FROM sessions
            WHERE (title IS NOT NULL AND title LIKE $q ESCAPE '\') OR (workspace IS NOT NULL AND workspace LIKE $q ESCAPE '\');
            """;
        cmd.Parameters.AddWithValue("$q", like);
        var raw = await cmd.ExecuteScalarAsync(ct);
        return Convert.ToInt32(raw);
    }

    private static SessionInfo ReadSessionInfo(Microsoft.Data.Sqlite.SqliteDataReader reader)
        => new(
            reader.GetString(0),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            FromTicks(reader.GetInt64(3)),
            FromTicks(reader.GetInt64(4)),
            0,
            reader.IsDBNull(1) ? null : reader.GetString(1))
        {
            ProjectId = reader.IsDBNull(5) ? null : reader.GetString(5),
        };

    public async ValueTask<int> CountAsync(CancellationToken ct = default)
    {
        await using var conn = OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sessions;";
        var raw = await cmd.ExecuteScalarAsync(ct);
        return Convert.ToInt32(raw);
    }

    public async ValueTask RenameAsync(string id, string title, CancellationToken ct = default)
    {
        await using var conn = OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE sessions SET title = $t, updated_at = $now WHERE id = $id;";
        cmd.Parameters.AddWithValue("$t", title);
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async ValueTask DeleteAsync(string id, CancellationToken ct = default)
    {
        // Entries fall out via ON DELETE CASCADE (foreign_keys ON per connection).
        await using var conn = OpenConnection();
        await using var tx = conn.BeginTransaction();
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM sessions WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", id);
            await cmd.ExecuteNonQueryAsync(ct);
            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    // ---- entries (append-only) ------------------------------------------

    /// <summary>
    /// astra-1 B: a single transaction that (1) atomically allocates the next
    /// sequence, (2) inserts the entry and (3) updates session metadata, then
    /// commits. A failure at any point rolls back the whole transaction so
    /// metadata and transcript can never diverge — regardless of which store
    /// instance (old or new generation during a plugin reload) performs it.
    /// </summary>
    public async ValueTask AppendAsync(SessionEntry entry, CancellationToken ct = default)
    {
        await using var conn = OpenConnection();
        await using var tx = conn.BeginTransaction();
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;

            var seq = await NextSeqAsync(cmd, entry.SessionId);

            cmd.CommandText = """
                INSERT INTO session_entries (id, session_id, seq, entry_type, created_at, payload_json)
                VALUES ($id, $s, $seq, $type, $now, $payload);
                """;
            cmd.Parameters.Clear();
            cmd.Parameters.AddWithValue("$id", entry.Id);
            cmd.Parameters.AddWithValue("$s", entry.SessionId);
            cmd.Parameters.AddWithValue("$seq", seq);
            cmd.Parameters.AddWithValue("$type", entry.Kind.ToString());
            cmd.Parameters.AddWithValue("$now", entry.CreatedAt.ToUnixTimeMilliseconds());
            cmd.Parameters.AddWithValue("$payload", PayloadOf(entry));
            await cmd.ExecuteNonQueryAsync(ct);

            cmd.CommandText = "UPDATE sessions SET last_sequence = $seq, updated_at = $now WHERE id = $s;";
            cmd.Parameters.Clear();
            cmd.Parameters.AddWithValue("$s", entry.SessionId);
            cmd.Parameters.AddWithValue("$seq", seq);
            cmd.Parameters.AddWithValue("$now", entry.CreatedAt.ToUnixTimeMilliseconds());
            await cmd.ExecuteNonQueryAsync(ct);

            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    public async ValueTask SetModelAsync(string sessionId, string? modelId, string? reasoningLevel, CancellationToken ct = default)
    {
        await using var conn = OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE sessions SET
                model_id = COALESCE($m, model_id),
                reasoning_level = COALESCE($r, reasoning_level),
                updated_at = $now
            WHERE id = $s;
            """;
        cmd.Parameters.AddWithValue("$s", sessionId);
        cmd.Parameters.AddWithValue("$m", modelId ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$r", reasoningLevel ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>astra-2 10 (package F): persist the session mode ("chat"/"orchestrate").</summary>
    public async ValueTask SetModeAsync(string sessionId, string? mode, CancellationToken ct = default)
    {
        var norm = string.IsNullOrWhiteSpace(mode) ? null : mode.Trim().ToLowerInvariant();
        await using var conn = OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE sessions SET mode = $m, updated_at = $now WHERE id = $s;";
        cmd.Parameters.AddWithValue("$s", sessionId);
        cmd.Parameters.AddWithValue("$m", norm ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async ValueTask SetWorkspaceAsync(string sessionId, string? workspacePath, CancellationToken ct = default)
    {
        await using var conn = OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE sessions SET workspace = $w, updated_at = $now WHERE id = $s;
            """;
        cmd.Parameters.AddWithValue("$s", sessionId);
        cmd.Parameters.AddWithValue("$w", workspacePath ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ---- pagination ------------------------------------------------------

    public async ValueTask<IReadOnlyList<SessionEntry>> ReadAsync(
        string sessionId, int offset, int count, CancellationToken ct = default)
    {
        var list = new List<SessionEntry>();
        await using var conn = OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, entry_type, created_at, seq, payload_json FROM session_entries
            WHERE session_id = $s ORDER BY seq LIMIT $cnt OFFSET $off;
            """;
        cmd.Parameters.AddWithValue("$s", sessionId);
        cmd.Parameters.AddWithValue("$off", offset);
        cmd.Parameters.AddWithValue("$cnt", count);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            list.Add(Rehydrate(sessionId, reader));
        return list;
    }

    /// <summary>PLAN §38: entries older than a sequence, for scroll-up pagination.</summary>
    public async ValueTask<IReadOnlyList<SessionEntry>> ReadBeforeAsync(
        string sessionId, int beforeSequence, int count, CancellationToken ct = default)
    {
        var list = new List<SessionEntry>();
        await using var conn = OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, entry_type, created_at, seq, payload_json FROM session_entries
            WHERE session_id = $s AND seq < $b
            ORDER BY seq DESC LIMIT $cnt;
            """;
        cmd.Parameters.AddWithValue("$s", sessionId);
        cmd.Parameters.AddWithValue("$b", beforeSequence);
        cmd.Parameters.AddWithValue("$cnt", count);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            list.Add(Rehydrate(sessionId, reader));
        list.Reverse(); // append order, oldest first
        return list;
    }

    /// <summary>astra-1 A: latest compaction checkpoint by sequence alone (independent of history windows).</summary>
    public async ValueTask<SessionEntry?> LatestCompactionAsync(
        string sessionId, CancellationToken ct = default)
    {
        await using var conn = OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, entry_type, created_at, seq, payload_json FROM session_entries
            WHERE session_id = $s AND entry_type = 'Compaction'
            ORDER BY seq DESC LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$s", sessionId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Rehydrate(sessionId, reader) : null;
    }

    /// <summary>astra-1 D: the most recent project-context entry by sequence (the ACTIVE snapshot), or null.</summary>
    public async ValueTask<SessionEntry?> ActiveProjectContextAsync(
        string sessionId, CancellationToken ct = default)
    {
        await using var conn = OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, entry_type, created_at, seq, payload_json FROM session_entries
            WHERE session_id = $s AND entry_type = 'ProjectContext'
            ORDER BY seq DESC LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$s", sessionId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Rehydrate(sessionId, reader) : null;
    }

    /// <summary>astra-1 A: entries after a sequence in append order (forward paging from a checkpoint).</summary>
    public async ValueTask<IReadOnlyList<SessionEntry>> ReadAfterAsync(
        string sessionId, int afterSequence, int count, CancellationToken ct = default)
    {
        var list = new List<SessionEntry>();
        await using var conn = OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, entry_type, created_at, seq, payload_json FROM session_entries
            WHERE session_id = $s AND seq > $b
            ORDER BY seq LIMIT $cnt;
            """;
        cmd.Parameters.AddWithValue("$s", sessionId);
        cmd.Parameters.AddWithValue("$b", afterSequence);
        cmd.Parameters.AddWithValue("$cnt", count);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            list.Add(Rehydrate(sessionId, reader));
        return list;
    }

    /// <summary>astra-1 A: the newest entries in append order (bounded recent-tail fallback).</summary>
    public async ValueTask<IReadOnlyList<SessionEntry>> ReadRecentAsync(
        string sessionId, int count, CancellationToken ct = default)
    {
        var list = new List<SessionEntry>();
        await using var conn = OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, entry_type, created_at, seq, payload_json FROM session_entries
            WHERE session_id = $s ORDER BY seq DESC LIMIT $cnt;
            """;
        cmd.Parameters.AddWithValue("$s", sessionId);
        cmd.Parameters.AddWithValue("$cnt", count);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            list.Add(Rehydrate(sessionId, reader));
        list.Reverse(); // append order, oldest first
        return list;
    }

    // ---- helpers ----------------------------------------------------------

    private static DateTimeOffset FromTicks(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms);

    private static string PayloadOf(SessionEntry e) => e switch
    {
        { Kind: EntryKind.Message, Message: not null } =>
            MessageSerializer.Serialize(e.Message!).ToString(),
        { Kind: EntryKind.Compaction, Payload: not null } => e.Payload.Value.ToString(),
        { Kind: EntryKind.Metadata, Payload: not null } => e.Payload.Value.ToString(),
        { Kind: EntryKind.ProjectContext, Payload: not null } => e.Payload.Value.ToString(),
        _ => "{}"
    };

    private static SessionEntry Rehydrate(string sessionId, SqliteDataReader r)
    {
        var kind = Enum.Parse<EntryKind>(r.GetString(1), ignoreCase: true);
        var payloadJson = r.GetString(4);
        AgentMessage? msg = kind == EntryKind.Message
            ? MessageSerializer.Deserialize(JsonDocument.Parse(payloadJson).RootElement)
            : null;
        JsonElement? payload = kind != EntryKind.Message
            ? JsonDocument.Parse(payloadJson).RootElement.Clone()
            : null;
        return new SessionEntry(
            r.GetString(0), sessionId, kind, msg, payload,
            DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(2)),
            Sequence: r.GetInt32(3));
    }

    // ---- astra-1 C: projects -------------------------------------------

    /// <summary>
    /// astra-1 C: atomic session-project change. The entry id IS the stable
    /// OperationId, so a retried operation hits the UNIQUE(id) constraint,
    /// rolls back, and returns the ORIGINAL entry — no duplicate transcript
    /// entries, no double context_revision bump.
    /// </summary>
    public async ValueTask<ProjectChangeResult> SetProjectAsync(ProjectChangeRequest change, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(change.OperationId))
            throw new ArgumentException("OperationId is required", nameof(change));
        await using var conn = OpenConnection();
        await using var tx = conn.BeginTransaction();
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;

            // Load the session row for the optimistic-concurrency guard.
            cmd.CommandText = "SELECT context_revision FROM sessions WHERE id = $s;";
            cmd.Parameters.Clear();
            cmd.Parameters.AddWithValue("$s", change.SessionId);
            var revRaw = await cmd.ExecuteScalarAsync(ct);
            if (revRaw is null)
                throw new InvalidOperationException($"Unknown session: {change.SessionId}");
            var currentRevision = Convert.ToInt32(revRaw);

            // astra-1 C: a non-(-1) ExpectedContextRevision that no longer
            // matches the session REJECTS the change (stale caller) instead of
            // clobbering a newer project change.
            if (change.ExpectedContextRevision >= 0
                && currentRevision != change.ExpectedContextRevision)
                throw new ProjectContextChangeException(
                    $"Session {change.SessionId} is at context revision {currentRevision}, " +
                    $"expected {change.ExpectedContextRevision} — refresh the project context and retry.");

            cmd.CommandText = "SELECT 1 FROM projects WHERE id = $p;";
            cmd.Parameters.Clear();
            cmd.Parameters.AddWithValue("$p", change.ProjectId);
            if (await cmd.ExecuteScalarAsync(ct) is null)
                throw new InvalidOperationException($"Unknown project: {change.ProjectId}");

            var seq = await NextSeqAsync(cmd, change.SessionId);
            cmd.CommandText = """
                INSERT INTO session_entries (id, session_id, seq, entry_type, created_at, payload_json)
                VALUES ($id, $s, $seq, $type, $now, $payload);
                """;
            cmd.Parameters.Clear();
            cmd.Parameters.AddWithValue("$id", change.OperationId);
            cmd.Parameters.AddWithValue("$s", change.SessionId);
            cmd.Parameters.AddWithValue("$seq", seq);
            cmd.Parameters.AddWithValue("$type", EntryKind.ProjectContext.ToString());
            cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            cmd.Parameters.AddWithValue("$payload", PayloadOf(change));
            await cmd.ExecuteNonQueryAsync(ct);

            cmd.CommandText = """
                UPDATE sessions SET project_id = $p, workspace = $w,
                    context_revision = context_revision + 1,
                    updated_at = $now, last_sequence = $seq
                WHERE id = $s;
                """;
            cmd.Parameters.Clear();
            cmd.Parameters.AddWithValue("$s", change.SessionId);
            cmd.Parameters.AddWithValue("$p", change.ProjectId);
            cmd.Parameters.AddWithValue("$w", change.Snapshot.WorkspacePath);
            cmd.Parameters.AddWithValue("$seq", seq);
            cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            await cmd.ExecuteNonQueryAsync(ct);

            tx.Commit();
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.SqliteErrorCode is 19)
        {
            // UNIQUE(id) on session_entries: an idempotent retry — the original
            // entry already exists; return it below.
        }
        catch
        {
            tx.Rollback();
            throw;
        }

        var session = await GetAsync(change.SessionId, ct)
            ?? throw new InvalidOperationException($"Session vanished: {change.SessionId}");
        var entry = await EntryByIdAsync(change.SessionId, change.OperationId, ct)
            ?? throw new InvalidOperationException($"No project-change entry for {change.OperationId}");
        return new ProjectChangeResult(session, entry);
    }

    /// <summary>The persisted entry for an operation (entry id == OperationId), null when absent.</summary>
    private async ValueTask<SessionEntry?> EntryByIdAsync(string sessionId, string entryId, CancellationToken ct)
    {
        await using var conn = OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, entry_type, created_at, payload_json, seq FROM session_entries
            WHERE session_id = $s AND id = $id;
            """;
        cmd.Parameters.AddWithValue("$s", sessionId);
        cmd.Parameters.AddWithValue("$id", entryId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        var kind = Enum.Parse<EntryKind>(reader.GetString(1), ignoreCase: true);
        var payload = JsonDocument.Parse(reader.GetString(3)).RootElement.Clone();
        return new SessionEntry(
            reader.GetString(0), sessionId, kind, null, payload,
            DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2)),
            Sequence: reader.GetInt32(4));
    }

    private static string PayloadOf(ProjectChangeRequest change)
        => JsonSerializer.Serialize(change.Snapshot);

    /// <summary>
    /// No long-lived connection to release; pooled connections return to the
    /// runtime on dispose. Kept (IDisposable) so callers and the plugin
    /// lifecycle can dispose the store uniformly.
    /// </summary>
    public void Dispose()
    {
    }
}
