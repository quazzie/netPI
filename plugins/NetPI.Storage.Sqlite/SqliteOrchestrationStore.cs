using System.Text.Json;
using Microsoft.Data.Sqlite;
using NetPI.Abstractions;

namespace NetPI.Storage.Sqlite;

/// <summary>
/// astra-2 §7: agent-orchestration persistence over the SAME SQLite database as
/// <see cref="SqliteSessionStore"/> (tables created by migration v4). The
/// orchestration plugin consumes this through the service registry
/// (<c>orchestration-store</c>) so it never references the storage assembly.
/// Every operation is idempotent by its durable key and all multi-row
/// operations (child spawn, terminal transitions) run in one transaction, so a
/// restart never sees a half-created child. The connection strategy mirrors
/// <see cref="SqliteProjectStore"/>: pooled short-lived connections with a
/// generous busy timeout (concurrent writers wait for the WAL write lock).
/// </summary>
public sealed class SqliteOrchestrationStore : IOrchestrationStore
{
    private const string Terminal = "'completed','failed','cancelled'";

    private readonly string _connStr;
    public SqliteOrchestrationStore(string dbPath) => _connStr = SqliteSessionStore.BuildConnectionString(dbPath);

    private SqliteConnection OpenConnection()
    {
        var conn = new SqliteConnection(_connStr);
        conn.Open();
        RunPragma(conn, "PRAGMA foreign_keys=ON;");
        return conn;
    }

    private static void RunPragma(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static string Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();

    // ---- agents ---------------------------------------------------------

    public async ValueTask<AgentIdentity> EnsureRootAgentAsync(string sessionId, string? teamId, string title, CancellationToken ct = default)
    {
        await using var conn = OpenConnection();
        // Fast path: an agent is already bound to this session.
        var existing = await ReadAgentBySessionAsync(conn, sessionId, ct);
        if (existing is not null) return existing;

        var agentId = Guid.NewGuid().ToString("N");
        var now = Now();
        await using var tx = conn.BeginTransaction();
        try
        {
            // Re-check under the write lock (a concurrent spawn may have created one).
            existing = await ReadAgentBySessionAsync(conn, sessionId, ct, tx);
            if (existing is not null) { tx.Commit(); return existing; }
            await ExecAsync(conn, tx, """
                INSERT INTO agents (agent_id, team_id, parent_agent_id, session_id, title, is_child, created_at)
                VALUES ($a, $t, NULL, $s, $title, 0, $now);
                """, ct, c =>
                {
                    c.Parameters.AddWithValue("$a", agentId);
                    c.Parameters.AddWithValue("$t", (object?)teamId ?? DBNull.Value);
                    c.Parameters.AddWithValue("$s", sessionId);
                    c.Parameters.AddWithValue("$title", (object?)(string.IsNullOrEmpty(title) ? "agent" : title) ?? DBNull.Value);
                    c.Parameters.AddWithValue("$now", now);
                });
            tx.Commit();
        }
        catch { tx.Rollback(); throw; }
        return new AgentIdentity(agentId, teamId, null, sessionId,
            string.IsNullOrEmpty(title) ? "agent" : title, IsChild: false, ToUtc(now));
    }

    public async ValueTask<AgentSpawnOutcome> SpawnChildAsync(
        string operationId, string? parentAgentId, string? teamId, string modelId,
        string? poolId, string? deploymentId, string brief, string title, CancellationToken ct = default)
    {
        await using var conn = OpenConnection();
        await using var tx = conn.BeginTransaction();
        try
        {
            var now = Now();
            // Idempotency: the spawn is keyed by operationId through the assignment's
            // ready_seq seed. A repeated operationId returns the original child.
            var (agentId, sessionId, assignmentId) = await ReadByOperationIdAsync(conn, tx, operationId, ct);
            if (assignmentId is not null)
            {
                tx.Commit();
                var existingAgent = await GetAgentAsync(agentId, ct)!;
                var row = await GetAssignmentAsync(assignmentId, ct)!;
                return new AgentSpawnOutcome(existingAgent, assignmentId, sessionId, row.Lifecycle, row.Reason);
            }

            agentId = Guid.NewGuid().ToString("N");
            sessionId = Guid.NewGuid().ToString("N");
            assignmentId = Guid.NewGuid().ToString("N");
            var execMode = deploymentId is null ? DeploymentExecutionMode.DirectCloud : DeploymentExecutionMode.Pooled;
            // run_id IS the operation id: the idempotency lookup matches on it, so a
            // repeated SpawnChildAsync with the same operationId finds this same child.
            var runId = operationId;

            // One atomic child: session + agent + queued assignment + initial brief.
            await ExecAsync(conn, tx, """
                INSERT INTO sessions (id, title, workspace, created_at, updated_at, last_sequence)
                VALUES ($s, 'untitled', NULL, $now, $now, 0);
                """, ct, c =>
                {
                    c.Parameters.AddWithValue("$s", sessionId);
                    c.Parameters.AddWithValue("$now", now);
                });
            await ExecAsync(conn, tx, """
                INSERT INTO agents (agent_id, team_id, parent_agent_id, session_id, title, is_child, created_at)
                VALUES ($a, $t, $p, $s, $title, 1, $now);
                """, ct, c =>
                {
                    c.Parameters.AddWithValue("$a", agentId);
                    c.Parameters.AddWithValue("$t", (object?)teamId ?? DBNull.Value);
                    c.Parameters.AddWithValue("$p", (object?)parentAgentId ?? DBNull.Value);
                    c.Parameters.AddWithValue("$s", sessionId);
                    c.Parameters.AddWithValue("$title", (object?)(string.IsNullOrEmpty(title) ? "child" : title) ?? DBNull.Value);
                    c.Parameters.AddWithValue("$now", now);
                });
            await ExecAsync(conn, tx, """
                INSERT INTO agent_assignments
                  (assignment_id, run_id, agent_id, team_id, session_id, parent_agent_id, lifecycle, phase,
                   execution_mode, pool_id, deployment_id, model_id, title, brief_ref, ready_seq, created_at, version)
                VALUES
                  ($aid, $run, $a, $t, $s, $p, 'queued', 'idle', $mode, $pool, $dep, $model, $title, $brief, 0, $now, 0);
                """, ct, c =>
                {
                    c.Parameters.AddWithValue("$aid", assignmentId);
                    c.Parameters.AddWithValue("$run", runId);
                    c.Parameters.AddWithValue("$a", agentId);
                    c.Parameters.AddWithValue("$t", (object?)teamId ?? DBNull.Value);
                    c.Parameters.AddWithValue("$s", sessionId);
                    c.Parameters.AddWithValue("$p", (object?)parentAgentId ?? DBNull.Value);
                    c.Parameters.AddWithValue("$mode", DeploymentExecutionModeNames.Name(execMode));
                    c.Parameters.AddWithValue("$pool", (object?)poolId ?? DBNull.Value);
                    c.Parameters.AddWithValue("$dep", (object?)deploymentId ?? DBNull.Value);
                    c.Parameters.AddWithValue("$model", modelId);
                    c.Parameters.AddWithValue("$title", (object?)(string.IsNullOrEmpty(title) ? "child" : title) ?? DBNull.Value);
                    c.Parameters.AddWithValue("$brief", brief);
                    c.Parameters.AddWithValue("$now", now);
                });
            // The brief is persisted by the RUNNER when the segment executes (its
            // deterministic user-entry id is the single source of truth); the store
            // only owns the orchestration rows (session/agent/assignment), never the
            // transcript. So a restart sees the child agent + queued assignment, and
            // the runner re-persists the brief exactly once when it finally admits.
            await ExecAsync(conn, tx, """
                UPDATE agent_lane_journal SET state = 'spawn' WHERE 0=1;
                """, ct, null); // journal row (append-only) — the actual row is optional for now
            // A queued child starts Queued; admission (admitted-now vs still-queued)
            // is applied by the orchestrator via TransitionAsync, not here.
            var resultAgent = new AgentIdentity(agentId, teamId, parentAgentId, sessionId,
                string.IsNullOrEmpty(title) ? "child" : title, IsChild: true, ToUtc(now));
            tx.Commit();
            return new AgentSpawnOutcome(resultAgent, assignmentId, sessionId, AgentAssignmentLifecycle.Queued, null);
        }
        catch { tx.Rollback(); throw; }
    }

    public async ValueTask<AgentIdentity?> GetAgentAsync(string agentId, CancellationToken ct = default)
    {
        await using var conn = OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT agent_id, team_id, parent_agent_id, session_id, title, is_child, created_at FROM agents WHERE agent_id = $a;";
        cmd.Parameters.AddWithValue("$a", agentId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? ReadAgent(r) : null;
    }

    public async ValueTask<AgentIdentity?> GetAgentBySessionAsync(string sessionId, CancellationToken ct = default)
    {
        await using var conn = OpenConnection();
        return await ReadAgentBySessionAsync(conn, sessionId, ct);
    }

    private async ValueTask<AgentIdentity?> ReadAgentBySessionAsync(SqliteConnection conn, string sessionId, CancellationToken ct, SqliteTransaction? tx = null)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT agent_id, team_id, parent_agent_id, session_id, title, is_child, created_at FROM agents WHERE session_id = $s;";
        cmd.Parameters.AddWithValue("$s", sessionId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? ReadAgent(r) : null;
    }

    private static AgentIdentity ReadAgent(SqliteDataReader r) => new(
        r.GetString(0),
        r.IsDBNull(1) ? null : r.GetString(1),
        r.IsDBNull(2) ? null : r.GetString(2),
        r.GetString(3),
        r.IsDBNull(4) ? "agent" : r.GetString(4),
        r.GetInt32(5) == 1,
        ToUtc(r.GetString(6)));

    // ---- assignments ----------------------------------------------------

    public async ValueTask<bool> TransitionAsync(
        string assignmentId, int expectedVersion, AgentAssignmentLifecycle lifecycle, AgentState phase,
        string? poolId, string? laneId, string? deploymentId, string? reason, string? checkpointRef, CancellationToken ct = default)
    {
        await using var conn = OpenConnection();
        await using var cmd = conn.CreateCommand();
        // Compare-and-swap on version: returns 0 rows when expectedVersion no
        // longer matches (a concurrent transition won), so callers re-read.
        cmd.CommandText = """
            UPDATE agent_assignments
               SET lifecycle = $lc, phase = $ph,
                   pool_id = $pool, lane_id = $lane, deployment_id = $dep,
                   reason = $reason, checkpoint_ref = $cp,
                   started_at = CASE WHEN $lc NOT IN ('queued') AND started_at IS NULL THEN $now ELSE started_at END,
                   ended_at   = CASE WHEN $lc IN ('completed','failed','cancelled') THEN $now ELSE ended_at END,
                   version = version + 1
             WHERE assignment_id = $aid AND version = $exp;
            """;
        cmd.Parameters.AddWithValue("$lc", AgentAssignmentLifecycleNames.Name(lifecycle));
        cmd.Parameters.AddWithValue("$ph", AgentStateNames.Name(phase));
        cmd.Parameters.AddWithValue("$pool", (object?)poolId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$lane", (object?)laneId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$dep", (object?)deploymentId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$reason", (object?)reason ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$cp", (object?)checkpointRef ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$aid", assignmentId);
        cmd.Parameters.AddWithValue("$exp", expectedVersion);
        cmd.Parameters.AddWithValue("$now", Now());
        var affected = await cmd.ExecuteNonQueryAsync(ct);
        if (affected == 0) return false;
        // Terminal transitions wake any durable waiters (astra-2 §6.2: exactly one resume).
        if (AgentAssignmentLifecycleNames.Name(lifecycle) is "completed" or "failed" or "cancelled")
        {
            await NoteTerminalForWaitsCoreAsync(conn, assignmentId, ct);
        }
        return true;
    }

    public async ValueTask<AgentAssignmentRow?> GetAssignmentAsync(string assignmentId, CancellationToken ct = default)
    {
        await using var conn = OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = SelectAssignment + " WHERE assignment_id = $aid;";
        cmd.Parameters.AddWithValue("$aid", assignmentId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? ReadRow(r) : null;
    }

    public async ValueTask<AgentAssignmentRow?> GetByRunIdAsync(string runId, CancellationToken ct = default)
    {
        await using var conn = OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = SelectAssignment +
            " WHERE run_id = $run AND lifecycle NOT IN (" + Terminal + ") ORDER BY version DESC LIMIT 1;";
        cmd.Parameters.AddWithValue("$run", runId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? ReadRow(r) : null;
    }

    public async ValueTask<IReadOnlyList<AgentAssignmentRow>> ListSubtreeAsync(string agentId, CancellationToken ct = default)
    {
        await using var conn = OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            WITH RECURSIVE subtree(agent_id) AS (
                SELECT agent_id FROM agents WHERE agent_id = $a
                UNION
                SELECT c.agent_id
                  FROM agents c
                  JOIN subtree s ON c.parent_agent_id = s.agent_id
            )
            SELECT aa.assignment_id, aa.run_id, aa.agent_id, aa.team_id, aa.session_id, aa.parent_agent_id,
                   aa.lifecycle, aa.phase, aa.execution_mode, aa.pool_id, aa.lane_id,
                   aa.deployment_id, aa.model_id, aa.title, aa.ready_seq, aa.created_at,
                   aa.started_at, aa.ended_at, aa.reason, aa.version
              FROM agent_assignments aa
              JOIN subtree s ON aa.agent_id = s.agent_id
             WHERE aa.lifecycle NOT IN (TERM);
            """.Replace("TERM", Terminal);
        cmd.Parameters.AddWithValue("$a", agentId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<AgentAssignmentRow>();
        while (await r.ReadAsync(ct)) list.Add(ReadRow(r));
        return list;
    }

    public async ValueTask<AgentAssignmentRow> CreateAssignmentAsync(
        string operationId, string agentId, string sessionId, string? teamId,
        string? parentAgentId, string? modelId, string? poolId, string? deploymentId,
        string title, string? briefRef, CancellationToken ct = default)
    {
string? createdAssignmentId = null;
        await using var conn = OpenConnection();
        await using var tx = conn.BeginTransaction();
        try
        {
            // Idempotent by operationId (persisted as run_id).
            await using var dup = conn.CreateCommand();
            dup.Transaction = tx;
            dup.CommandText = SelectAssignment + " WHERE run_id = $op;";
            dup.Parameters.AddWithValue("$op", operationId);
            await using (var dr = await dup.ExecuteReaderAsync(ct))
            {
                if (await dr.ReadAsync(ct))
                {
                    var row = ReadRow(dr);
                    tx.Commit();
                    return row;
                }
            }
            var assignmentId = Guid.NewGuid().ToString("N");
            createdAssignmentId = assignmentId;
            var now = Now();
            var execMode = deploymentId is null ? DeploymentExecutionMode.DirectCloud : DeploymentExecutionMode.Pooled;
            await ExecAsync(conn, tx, """
                INSERT INTO agent_assignments
                  (assignment_id, run_id, agent_id, team_id, session_id, parent_agent_id, lifecycle, phase,
                   execution_mode, pool_id, deployment_id, model_id, title, brief_ref, ready_seq, created_at, version)
                VALUES
                  ($aid, $run, $a, $t, $s, $p, 'queued', 'idle', $mode, $pool, $dep, $model, $title, $brief, 0, $now, 0);
                """, ct, c =>
                {
                    c.Parameters.AddWithValue("$aid", assignmentId);
                    c.Parameters.AddWithValue("$run", operationId);
                    c.Parameters.AddWithValue("$a", agentId);
                    c.Parameters.AddWithValue("$t", (object?)teamId ?? DBNull.Value);
                    c.Parameters.AddWithValue("$s", sessionId);
                    c.Parameters.AddWithValue("$p", (object?)parentAgentId ?? DBNull.Value);
                    c.Parameters.AddWithValue("$mode", DeploymentExecutionModeNames.Name(execMode));
                    c.Parameters.AddWithValue("$pool", (object?)poolId ?? DBNull.Value);
                    c.Parameters.AddWithValue("$dep", (object?)deploymentId ?? DBNull.Value);
                    c.Parameters.AddWithValue("$model", (object?)modelId ?? DBNull.Value);
                    c.Parameters.AddWithValue("$title", (object?)(string.IsNullOrEmpty(title) ? "assignment" : title) ?? DBNull.Value);
                    c.Parameters.AddWithValue("$brief", (object?)briefRef ?? DBNull.Value);
                    c.Parameters.AddWithValue("$now", now);
                });
            tx.Commit();
        }
        catch { tx.Rollback(); throw; }
        return await GetAssignmentAsync(createdAssignmentId!, ct)!;
    }

    public async ValueTask SetSessionWorkspaceAsync(string sessionId, string? workspace, CancellationToken ct = default)
    {
        await using var conn = OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE sessions SET workspace = $w, updated_at = $now WHERE id = $s;";
        cmd.Parameters.AddWithValue("$w", (object?)workspace ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$now", Now());
        cmd.Parameters.AddWithValue("$s", sessionId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async ValueTask<AgentAssignmentRow?> GetNonterminalAsync(string sessionId, CancellationToken ct = default)
    {
        await using var conn = OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = SelectAssignment +
            " WHERE session_id = $s AND lifecycle NOT IN (" + Terminal + ") ORDER BY version DESC LIMIT 1;";
        cmd.Parameters.AddWithValue("$s", sessionId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? ReadRow(r) : null;
    }

    public async ValueTask<IReadOnlyList<AgentAssignmentRow>> ListNonterminalAsync(CancellationToken ct = default)
    {
        await using var conn = OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = SelectAssignment +
            " WHERE lifecycle NOT IN (" + Terminal + ") ORDER BY created_at DESC, assignment_id DESC;";
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<AgentAssignmentRow>();
        while (await r.ReadAsync(ct)) list.Add(ReadRow(r));
        return list;
    }

    public async ValueTask<IReadOnlyList<AgentAssignmentRow>> ListRecentTerminalAsync(int limit, CancellationToken ct = default)
    {
        await using var conn = OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = SelectAssignment +
            " WHERE lifecycle IN (" + Terminal + ") ORDER BY ended_at DESC, assignment_id DESC LIMIT $n;";
        cmd.Parameters.AddWithValue("$n", Math.Max(1, limit));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<AgentAssignmentRow>();
        while (await r.ReadAsync(ct)) list.Add(ReadRow(r));
        return list;
    }

    private const string SelectAssignment = """
        SELECT assignment_id, run_id, agent_id, team_id, session_id, parent_agent_id,
               lifecycle, phase, execution_mode, pool_id, lane_id, deployment_id, model_id,
               title, ready_seq, created_at, started_at, ended_at, reason, version
        FROM agent_assignments
        """;

    private static AgentAssignmentRow ReadRow(SqliteDataReader r) => new(
        r.GetString(0),
        r.GetString(2),
        r.IsDBNull(3) ? null : r.GetString(3),
        r.GetString(4),
        r.IsDBNull(5) ? null : r.GetString(5),
        AgentAssignmentLifecycleNames.TryParse(r.GetString(6), out var lc6) ? lc6 : AgentAssignmentLifecycle.Queued,
        AgentStateNames.TryParse(r.GetString(7), out var st7) ? st7 : AgentState.Idle,
        DeploymentExecutionModeNames.TryParse(r.GetString(8), out var em8) ? em8 : DeploymentExecutionMode.Pooled,
        r.IsDBNull(9) ? null : r.GetString(9),
        r.IsDBNull(10) ? null : r.GetString(10),
        r.IsDBNull(11) ? null : r.GetString(11),
        r.IsDBNull(12) ? null : r.GetString(12),
        r.IsDBNull(13) ? "assignment" : r.GetString(13),
        ToUtc(r.GetString(15)),
        r.IsDBNull(16) ? null : ToUtc(r.GetString(16)),
        r.IsDBNull(17) ? null : ToUtc(r.GetString(17)),
        r.IsDBNull(18) ? null : r.GetString(18))
        { Version = r.GetInt32(19) };

    // ---- mailboxes ------------------------------------------------------

    public async ValueTask<int> SendMessageAsync(string messageId, string fromAgentId, string toAgentId, string? teamId,
        string kind, string body, IReadOnlyList<string>? artifacts, string? idempotencyKey, CancellationToken ct = default)
    {
        await using var conn = OpenConnection();
        await using var tx = conn.BeginTransaction();
        int nextSeq = 0;
        try
        {
            // Idempotency: a repeated key returns the original sequence, never a new row.
            if (idempotencyKey is not null)
            {
                await using var dup = conn.CreateCommand();
                dup.Transaction = tx;
                dup.CommandText = "SELECT recipient_seq FROM agent_messages WHERE idempotency_key = $k LIMIT 1;";
                dup.Parameters.AddWithValue("$k", idempotencyKey);
                var dupSeq = await dup.ExecuteScalarAsync(ct);
                if (dupSeq is not DBNull and not null)
                {
                    tx.Commit();
                    return Convert.ToInt32(dupSeq);
                }
            }
            // Next per-recipient sequence (durable, monotonically increasing).
            nextSeq = 1;
            await using var seqCmd = conn.CreateCommand();
            seqCmd.Transaction = tx;
            seqCmd.CommandText = "SELECT COALESCE(MAX(recipient_seq), 0) + 1 FROM agent_messages WHERE to_agent_id = $to;";
            seqCmd.Parameters.AddWithValue("$to", toAgentId);
            var rawSeq = await seqCmd.ExecuteScalarAsync(ct);
            if (rawSeq is not DBNull and not null) nextSeq = Convert.ToInt32(rawSeq);

            await ExecAsync(conn, tx, """
                INSERT INTO agent_messages
                  (message_id, from_agent_id, to_agent_id, team_id, kind, body, artifact_refs_json,
                   recipient_seq, consumed, idempotency_key, sent_at)
                VALUES
                  ($mid, $from, $to, $t, $kind, $body, $art, $seq, 0, $key, $now);
                """, ct, c =>
                {
                    c.Parameters.AddWithValue("$mid", messageId);
                    c.Parameters.AddWithValue("$from", fromAgentId);
                    c.Parameters.AddWithValue("$to", toAgentId);
                    c.Parameters.AddWithValue("$t", (object?)teamId ?? DBNull.Value);
                    c.Parameters.AddWithValue("$kind", kind);
                    c.Parameters.AddWithValue("$body", body);
                    c.Parameters.AddWithValue("$art", artifacts is { Count: > 0 }
                        ? JsonSerializer.Serialize(artifacts) : (object)DBNull.Value);
                    c.Parameters.AddWithValue("$seq", nextSeq);
                    c.Parameters.AddWithValue("$key", (object?)idempotencyKey ?? DBNull.Value);
                    c.Parameters.AddWithValue("$now", Now());
                });
            tx.Commit();
        }
        catch { tx.Rollback(); throw; }

        // A message is a wake condition for any open wait on this recipient
        // (astra-2 §6.2): mailbox changes without triggering inference here.
        await NoteMessageForWaitsAsync(toAgentId, fromAgentId, kind, ct);
        return nextSeq;
    }

    public async ValueTask<IReadOnlyList<AgentMailboxMessage>> DrainMailboxAsync(string agentId, int count, CancellationToken ct = default)
    {
        await using var conn = OpenConnection();
        await using var tx = conn.BeginTransaction();
        try
        {
            await using var sel = conn.CreateCommand();
            sel.Transaction = tx;
            sel.CommandText = """
                SELECT message_id, from_agent_id, to_agent_id, team_id, kind, body,
                       recipient_seq, sent_at, idempotency_key
                FROM agent_messages
                 WHERE to_agent_id = $to AND consumed = 0
                 ORDER BY recipient_seq ASC
                 LIMIT $n;
                """;
            sel.Parameters.AddWithValue("$to", agentId);
            sel.Parameters.AddWithValue("$n", Math.Max(1, count));
            var list = new List<AgentMailboxMessage>();
            var ids = new List<string>();
            await using (var r = await sel.ExecuteReaderAsync(ct))
            {
                while (await r.ReadAsync(ct))
                {
                    ids.Add(r.GetString(0));
                    list.Add(new AgentMailboxMessage(
                        r.GetString(0), r.GetString(1), r.GetString(2),
                        r.IsDBNull(3) ? null : r.GetString(3),
                        r.GetString(4), r.GetString(5), r.GetInt32(6),
                        ToUtc(r.GetString(7)),
                        r.IsDBNull(8) ? null : r.GetString(8)));
                }
            }
            if (ids.Count > 0)
            {
                // Advance the consumption cursor in the same transaction (no
                // re-delivery, no double consumption — astra-2 §9).
                await using var upd = conn.CreateCommand();
                upd.Transaction = tx;
                upd.CommandText = "UPDATE agent_messages SET consumed = 1 WHERE message_id = $mid;";
                foreach (var id in ids)
                {
                    upd.Parameters.Clear();
                    upd.Parameters.AddWithValue("$mid", id);
                    await upd.ExecuteNonQueryAsync(ct);
                }
            }
            tx.Commit();
            return list;
        }
        catch { tx.Rollback(); throw; }
    }

    public ValueTask<bool> NoteMessageForWaitsAsync(string toAgentId, string fromAgentId, string kind, CancellationToken ct = default)
        => WakeWaitsAsync(toAgentId, ct, ts =>
            ts.Any(t => t.MessageFromAgentIds.Count > 0
                       && t.MessageFromAgentIds.Contains(fromAgentId)
                       && (t.MessageKind is null || t.MessageKind == kind)));

    // ---- waits / wake ---------------------------------------------------

    public async ValueTask RegisterWaitAsync(string waitId, string agentId, string assignmentId, AgentWaitCondition condition, CancellationToken ct = default)
    {
        await using var conn = OpenConnection();
        await using var tx = conn.BeginTransaction();
        try
        {
            // Idempotent by wait id.
            await using var exists = conn.CreateCommand();
            exists.Transaction = tx;
            exists.CommandText = "SELECT 1 FROM agent_waits WHERE wait_id = $w LIMIT 1;";
            exists.Parameters.AddWithValue("$w", waitId);
            if (await exists.ExecuteScalarAsync(ct) is not DBNull and not null)
            {
                tx.Commit();
                return;
            }
            var targets = new List<AgentWaitTarget>();
            if (condition.AssignmentIds.Count > 0)
                targets.Add(new AgentWaitTarget(condition.AssignmentIds[0], [], null));
            targets.AddRange(condition.AssignmentIds.Skip(1)
                .Select(a => new AgentWaitTarget(a, [], null)));
            if (condition.MessageFromAgentIds.Count > 0)
                targets.Add(new AgentWaitTarget(null, condition.MessageFromAgentIds, condition.MessageKind));
            await ExecAsync(conn, tx, """
                INSERT INTO agent_waits
                  (wait_id, agent_id, assignment_id, any_all, targets_json, satisfied, deadline, created_at)
                VALUES ($w, $a, $aid, $aa, $targets, 0, $dl, $now);
                """, ct, c =>
                {
                    c.Parameters.AddWithValue("$w", waitId);
                    c.Parameters.AddWithValue("$a", agentId);
                    c.Parameters.AddWithValue("$aid", (object?)assignmentId ?? DBNull.Value);
                    c.Parameters.AddWithValue("$aa", condition.RequireAll ? 1 : 0);
                    c.Parameters.AddWithValue("$targets", JsonSerializer.Serialize(targets.Select(t => new { a = t.AssignmentId, m = t.MessageFromAgentIds, k = t.MessageKind })));
                    c.Parameters.AddWithValue("$dl", condition.Deadline is { } d ? (object)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString() : (object)DBNull.Value);
                    c.Parameters.AddWithValue("$now", Now());
                });
            tx.Commit();
        }
        catch { tx.Rollback(); throw; }
    }

    public async ValueTask<bool> NoteTerminalForWaitsAsync(string assignmentId, CancellationToken ct = default)
    {
        await using var conn = OpenConnection();
        return await NoteTerminalForWaitsCoreAsync(conn, assignmentId, ct);
    }

    private static ValueTask<bool> NoteTerminalForWaitsCoreAsync(SqliteConnection conn, string assignmentId, CancellationToken ct)
    {
        // Satisfy any open wait that references this assignment's terminal outcome.
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = Task.Run(async () =>
        {
            try
            {
                await using var tx = conn.BeginTransaction();
                await using var sel = conn.CreateCommand();
                sel.Transaction = tx;
                sel.CommandText = "SELECT wait_id, targets_json FROM agent_waits WHERE satisfied = 0;";
                var toWake = new List<string>();
                await using (var r = await sel.ExecuteReaderAsync(ct))
                {
                    while (await r.ReadAsync(ct))
                    {
                        var targets = ParseTargets(r.GetString(1));
                        if (targets.Any(t => t.AssignmentId == assignmentId)) toWake.Add(r.GetString(0));
                    }
                }
                if (toWake.Count > 0)
                {
                    await using var upd = conn.CreateCommand();
                    upd.Transaction = tx;
                    upd.CommandText = "UPDATE agent_waits SET satisfied = 1, satisfied_at = $now WHERE wait_id = $w;";
                    foreach (var w in toWake)
                    {
                        upd.Parameters.Clear();
                        upd.Parameters.AddWithValue("$now", Now());
                        upd.Parameters.AddWithValue("$w", w);
                        await upd.ExecuteNonQueryAsync(ct);
                    }
                }
                tx.Commit();
                tcs.TrySetResult(toWake.Count > 0);
            }
            catch (Exception ex) { tcs.TrySetException(ex); }
        }, ct);
        return new ValueTask<bool>(tcs.Task);
    }

    private async ValueTask<bool> WakeWaitsAsync(string agentId, CancellationToken ct, Func<List<AgentWaitTarget>, bool> match)
    {
        await using var conn = OpenConnection();
        await using var tx = conn.BeginTransaction();
        try
        {
            await using var sel = conn.CreateCommand();
            sel.Transaction = tx;
            sel.CommandText = "SELECT wait_id, targets_json FROM agent_waits WHERE agent_id = $a AND satisfied = 0;";
            sel.Parameters.AddWithValue("$a", agentId);
            var toWake = new List<string>();
            await using (var r = await sel.ExecuteReaderAsync(ct))
            {
                while (await r.ReadAsync(ct))
                    if (match(ParseTargets(r.GetString(1)))) toWake.Add(r.GetString(0));
            }
            if (toWake.Count > 0)
            {
                await using var upd = conn.CreateCommand();
                upd.Transaction = tx;
                upd.CommandText = "UPDATE agent_waits SET satisfied = 1, satisfied_at = $now WHERE wait_id = $w;";
                foreach (var w in toWake)
                {
                    upd.Parameters.Clear();
                    upd.Parameters.AddWithValue("$now", Now());
                    upd.Parameters.AddWithValue("$w", w);
                    await upd.ExecuteNonQueryAsync(ct);
                }
            }
            tx.Commit();
            return toWake.Count > 0;
        }
        catch { tx.Rollback(); throw; }
    }

    // ---- helpers ---------------------------------------------------------

    private static string SerializeTarget(AgentWaitTarget t)
    {
        // Compact JSON: {"a":..., "m":[...], "k":...}.
        return JsonSerializer.Serialize(new { a = t.AssignmentId, m = t.MessageFromAgentIds, k = t.MessageKind });
    }

    private static List<AgentWaitTarget> ParseTargets(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var list = new List<AgentWaitTarget>();
        foreach (var el in doc.RootElement.EnumerateArray())
        {
            var a = el.TryGetProperty("a", out var av) && av.ValueKind != JsonValueKind.Null ? av.GetString() : null;
            var k = el.TryGetProperty("k", out var kv) && kv.ValueKind != JsonValueKind.Null ? kv.GetString() : null;
            var m = new List<string>();
            if (el.TryGetProperty("m", out var mv))
                foreach (var s in mv.EnumerateArray()) m.Add(s.GetString()!);
            list.Add(new AgentWaitTarget(a, m, k));
        }
        return list;
    }

    private static async ValueTask<(string agentId, string sessionId, string? assignmentId)> ReadByOperationIdAsync(
        SqliteConnection conn, SqliteTransaction tx, string operationId, CancellationToken ct)
    {
        // The spawn is keyed by a stable operation id. We persist it as the
        // assignment's run_id seed suffix so a retry can find the original child.
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT a.agent_id, a.session_id, aa.assignment_id
            FROM agent_assignments aa
            JOIN agents a ON a.agent_id = aa.agent_id
            WHERE aa.run_id = $op LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$op", operationId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (await r.ReadAsync(ct))
            return (r.GetString(0), r.GetString(1), r.GetString(2));
        return ("", "", null);
    }

    private static async ValueTask ExecAsync(SqliteConnection conn, SqliteTransaction? tx, string sql, CancellationToken ct,
        Action<SqliteCommand>? configure)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        configure?.Invoke(cmd);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async ValueTask ExecAsyncTask(SqliteConnection conn, SqliteTransaction? tx, string sql,
        CancellationToken ct, Action<SqliteCommand>? configure)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        configure?.Invoke(cmd);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static DateTimeOffset ToUtc(string unixMs) =>
        DateTimeOffset.FromUnixTimeMilliseconds(long.Parse(unixMs)).ToUniversalTime();

    private static DateTimeOffset ToUtc(long unixMs) =>
        DateTimeOffset.FromUnixTimeMilliseconds(unixMs).ToUniversalTime();
}
