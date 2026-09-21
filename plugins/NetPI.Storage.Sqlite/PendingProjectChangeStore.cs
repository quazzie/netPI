using System.Text.Json;
using Microsoft.Data.Sqlite;
using NetPI.Abstractions;

namespace NetPI.Storage.Sqlite;

/// <summary>
/// astra-1 D2: persistence for PENDING project changes — the selections that
/// arrive while a session's run is in flight and are applied at the next safe
/// boundary instead. One row per session (PRIMARY KEY session_id): a later
/// enqueue REPLACES the earlier unapplied selection. The full
/// <see cref="ProjectChangeRequest"/> (operation id + authoritative snapshot)
/// is stored as JSON so the boundary apply is fully self-contained (no
/// re-resolution) and survives a host restart.
///
/// Like the other stores, every operation opens a short-lived pooled
/// connection; writers are serialized by SQLite itself (WAL + busy timeout),
/// never by an instance lock — a plugin reload (two store generations on the
/// same file) must stay correct.
/// </summary>
public sealed class PendingProjectChangeStore : IPendingProjectChangeStore
{
    private static readonly JsonSerializerOptions JsonOpts =
        new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Wire form of a pending change. <c>ProjectChangeRequest</c> carries a
    /// 5-arg positional constructor plus an astra-1 C 4-arg overload, so
    /// System.Text.Json cannot bind one — this record (singular constructor,
    /// init-only properties) is what actually round-trips through the DB.
    /// </summary>
    private sealed class PendingRequestWire
    {
        public string OperationId { get; init; } = string.Empty;
        public string SessionId { get; init; } = string.Empty;
        public string ProjectId { get; init; } = string.Empty;
        public int ExpectedContextRevision { get; init; }
        public ProjectContextSnapshot? Snapshot { get; init; }

        public ProjectChangeRequest ToRequest()
            => new(OperationId, SessionId, ProjectId, ExpectedContextRevision,
                Snapshot ?? throw new InvalidOperationException("snapshot is required"));
    }

    private readonly string _connStr;

    public PendingProjectChangeStore(string dbPath)
        => _connStr = SqliteSessionStore.BuildConnectionString(dbPath);

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connStr);
        conn.Open();
        return conn;
    }

    public async ValueTask<PendingProjectChangeInfo> EnqueueAsync(
        ProjectChangeRequest change, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(change.SessionId))
            throw new ArgumentException("SessionId is required", nameof(change));
        if (string.IsNullOrEmpty(change.OperationId))
            throw new ArgumentException("OperationId is required", nameof(change));
        if (change.Snapshot is null)
            throw new ArgumentException("Snapshot is required", nameof(change));

        // One pending selection per session: the newest one wins (REPLACE
        // semantics — the earlier unapplied selection is discarded).
        await using var conn = Open();
        await using var tx = conn.BeginTransaction();
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO pending_project_changes
                    (session_id, operation_id, project_id, payload_json, enqueued_at)
                VALUES ($s, $op, $p, $payload, $now)
                ON CONFLICT (session_id) DO UPDATE SET
                    operation_id = excluded.operation_id,
                    project_id   = excluded.project_id,
                    payload_json = excluded.payload_json,
                    enqueued_at  = excluded.enqueued_at;
                """;
            cmd.Parameters.AddWithValue("$s", change.SessionId);
            cmd.Parameters.AddWithValue("$op", change.OperationId);
            cmd.Parameters.AddWithValue("$p", change.ProjectId);
            var wire = new PendingRequestWire
            {
                OperationId = change.OperationId,
                SessionId = change.SessionId,
                ProjectId = change.ProjectId,
                ExpectedContextRevision = change.ExpectedContextRevision,
                Snapshot = change.Snapshot,
            };
            cmd.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(wire, JsonOpts));
            cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            await cmd.ExecuteNonQueryAsync(cancellationToken);
            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }

        return new PendingProjectChangeInfo(
            change.SessionId,
            change.OperationId,
            change.ProjectId,
            change.Snapshot.ProjectName,
            DateTimeOffset.UtcNow,
            change);
    }

    public async ValueTask<PendingProjectChangeInfo?> PendingAsync(
        string sessionId, CancellationToken cancellationToken = default)
    {
        await using var conn = Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT operation_id, project_id, payload_json, enqueued_at
            FROM pending_project_changes WHERE session_id = $s;
            """;
        cmd.Parameters.AddWithValue("$s", sessionId);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;

        var opId = reader.GetString(0);
        var project = reader.GetString(1);
        var payload = reader.GetString(2);
        var wire = JsonSerializer.Deserialize<PendingRequestWire>(payload, JsonOpts)
            ?? throw new InvalidOperationException($"malformed pending change for session {sessionId}");
        var change = wire.ToRequest();
        return new PendingProjectChangeInfo(
            sessionId, opId, project,
            change.Snapshot?.ProjectName ?? string.Empty,
            DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(3)),
            change);
    }

    public async ValueTask<bool> ClearAsync(
        string sessionId, string operationId, CancellationToken cancellationToken = default)
    {
        await using var conn = Open();
        await using var cmd = conn.CreateCommand();
        // Conditional on operationId: a NEWER selection enqueued in between
        // must survive (the delete then simply matches nothing).
        cmd.CommandText = """
            DELETE FROM pending_project_changes
            WHERE session_id = $s AND operation_id = $op;
            """;
        cmd.Parameters.AddWithValue("$s", sessionId);
        cmd.Parameters.AddWithValue("$op", operationId);
        return await cmd.ExecuteNonQueryAsync(cancellationToken) > 0;
    }
}
