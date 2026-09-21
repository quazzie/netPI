using System.Text.Json;
using Microsoft.Data.Sqlite;
using NetPI.Abstractions;

namespace NetPI.Storage.Sqlite;

/// <summary>
/// Append-only session storage on SQLite (PLAN §29-§31). Single connection
/// (SQLite locks the file); WAL mode for concurrent readers; foreign keys on.
///
/// Schema:
/// <code>
///   sessions(id TEXT PK, title, workspace, provider_id, model_id,
///            reasoning_level, created_at, updated_at, last_sequence)
///   session_entries(id TEXT PK, session_id, seq, entry_type, created_at,
///                   payload_json, FK -> sessions ON DELETE CASCADE)
///   settings(key TEXT PK, value_json)
/// </code>
/// </summary>
public sealed class SqliteSessionStore : ISessionStore, IDisposable
{
    private const string Schema = """
        PRAGMA journal_mode=WAL;
        PRAGMA synchronous=NORMAL;
        PRAGMA foreign_keys=ON;

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

        CREATE TABLE IF NOT EXISTS session_entries (
            id           TEXT PRIMARY KEY,
            session_id   TEXT NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
            seq          INTEGER NOT NULL,
            entry_type   TEXT NOT NULL,
            created_at   TEXT NOT NULL,
            payload_json TEXT NOT NULL,
            UNIQUE (session_id, seq)
        );

        CREATE INDEX IF NOT EXISTS ix_sessions_updated ON sessions(updated_at);
        CREATE INDEX IF NOT EXISTS ix_entries_session_seq
            ON session_entries(session_id, seq);

        CREATE TABLE IF NOT EXISTS settings (
            key        TEXT PRIMARY KEY,
            value_json TEXT NOT NULL
        );
        """;

    private readonly SqliteConnection _connection;

    /// <summary>Path to the database file (exposed for diagnostics).</summary>
    public string DbPath { get; }

    public SqliteSessionStore(string dbPath)
    {
        DbPath = dbPath;
        var dir = Path.GetDirectoryName(Path.GetFullPath(dbPath));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        _connection = new SqliteConnection($"Data Source={dbPath}");
        _connection.Open();
        ApplySchema();
    }

    private void ApplySchema()
    {
        foreach (var statement in System.Text.RegularExpressions.Regex.Split(Schema, @";\s*").ToArray())
        {
            var sql = statement.Trim();
            if (string.IsNullOrEmpty(sql)) continue;
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }
    }

    // ---- sessions ------------------------------------------------------

    public async ValueTask<SessionInfo> CreateAsync(string? workspacePath, CancellationToken ct = default)
    {
        var id = Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow;
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO sessions (id, title, workspace, created_at, updated_at, last_sequence)
            VALUES ($id, 'untitled', $ws, $now, $now, 0);
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$ws", workspacePath ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
        await cmd.ExecuteNonQueryAsync(ct);
        return await GetAsync(id, ct)!;
    }

    public async ValueTask<SessionInfo?> GetAsync(string id, CancellationToken ct = default)
    {
        await using var cmd = _connection.CreateCommand();
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
        await using var cmd = _connection.CreateCommand();
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
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sessions;";
        var raw = await cmd.ExecuteScalarAsync(ct);
        return Convert.ToInt32(raw);
    }

    public async ValueTask RenameAsync(string id, string title, CancellationToken ct = default)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = "UPDATE sessions SET title = $t, updated_at = $now WHERE id = $id;";
        cmd.Parameters.AddWithValue("$t", title);
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async ValueTask DeleteAsync(string id, CancellationToken ct = default)
    {
        // Entries fall out via ON DELETE CASCADE (PRAGMA foreign_keys=ON).
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = "DELETE FROM sessions WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ---- entries (append-only) ------------------------------------------

    public async ValueTask AppendAsync(SessionEntry entry, CancellationToken ct = default)
    {
        var seq = await NextSeqAsync(entry.SessionId, ct);
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO session_entries (id, session_id, seq, entry_type, created_at, payload_json)
            VALUES ($id, $s, $seq, $type, $now, $payload);
            UPDATE sessions SET last_sequence = $seq, updated_at = $now WHERE id = $s;
            """;
        cmd.Parameters.AddWithValue("$id", entry.Id);
        cmd.Parameters.AddWithValue("$s", entry.SessionId);
        cmd.Parameters.AddWithValue("$seq", seq);
        cmd.Parameters.AddWithValue("$type", entry.Kind.ToString());
        cmd.Parameters.AddWithValue("$now", entry.CreatedAt.ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("$payload", PayloadOf(entry));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async ValueTask SetModelAsync(string sessionId, string? modelId, string? reasoningLevel, CancellationToken ct = default)
    {
        await using var cmd = _connection.CreateCommand();
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
        await using var cmd = _connection.CreateCommand();
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
        await using var cmd = _connection.CreateCommand();
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
        await using var cmd = _connection.CreateCommand();
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

    // ---- helpers ----------------------------------------------------------

    private static DateTimeOffset FromTicks(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms);

    private async ValueTask<int> NextSeqAsync(string sessionId, CancellationToken ct)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(MAX(seq), 0) + 1 FROM session_entries WHERE session_id = $s;";
        cmd.Parameters.AddWithValue("$s", sessionId);
        var raw = await cmd.ExecuteScalarAsync(ct);
        return raw is long l ? (int)l : 1;
    }

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


    public void Dispose()
    {
        _connection.Dispose();
    }
}
