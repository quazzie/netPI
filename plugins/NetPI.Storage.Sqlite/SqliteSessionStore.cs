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
/// Schema (v2):
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
/// </code>
/// The <c>sessions.workspace</c> column is preserved for compatibility.
/// </summary>
public sealed class SqliteSessionStore : ISessionStore, IDisposable
{
    /// <summary>Highest migration version applied by this store.</summary>
    public const int CurrentSchemaVersion = 2;

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
        _connStr = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
            DefaultTimeout = 30,
        }.ToString();

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

            foreach (var version in new[] { 1, 2 })
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
                   COALESCE((SELECT COUNT(*) FROM session_entries e WHERE e.session_id = s.id), 0)
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
        };
    }

    public async ValueTask<IReadOnlyList<SessionInfo>> ListAsync(int count = 50, int offset = 0, CancellationToken ct = default)
    {
        var list = new List<SessionInfo>();
        await using var conn = OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, title, workspace, created_at, updated_at
            FROM sessions ORDER BY updated_at DESC LIMIT $count OFFSET $offset;
            """;
        cmd.Parameters.AddWithValue("$count", count);
        cmd.Parameters.AddWithValue("$offset", Math.Max(0, offset));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            list.Add(new SessionInfo(
                reader.GetString(0),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                FromTicks(reader.GetInt64(3)),
                FromTicks(reader.GetInt64(4)),
                0,
                reader.IsDBNull(1) ? null : reader.GetString(1)));
        return list;
    }

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

    /// <summary>
    /// No long-lived connection to release; pooled connections return to the
    /// runtime on dispose. Kept (IDisposable) so callers and the plugin
    /// lifecycle can dispose the store uniformly.
    /// </summary>
    public void Dispose()
    {
    }
}
