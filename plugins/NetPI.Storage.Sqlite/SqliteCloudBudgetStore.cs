using Microsoft.Data.Sqlite;
using NetPI.Abstractions;

namespace NetPI.Storage.Sqlite;

/// <summary>
/// astra-2 §11.2 (package F): the SQLite implementation of
/// <see cref="ICloudBudgetStore"/> — the shared team cloud allowance with
/// atomic reservations.
///
/// Double-spend is prevented by making the draw a SINGLE write transaction:
/// an IMMEDIATE transaction grabs the WAL write lock before the read, so a
/// concurrent reserve's read+write cannot interleave with ours. SQLite
/// serializes writers on the file (WAL + busy timeout), never on an instance
/// lock, which keeps a plugin reload (two store generations on the same file)
/// correct too.
///
/// The tables are owned by the SqliteSessionStore migration (v5); this store
/// re-creates them guardedly on first open so it works standalone (tests and
/// reload ordering).
/// </summary>
public sealed class SqliteCloudBudgetStore : ICloudBudgetStore
{
    private readonly string _connStr;

    public SqliteCloudBudgetStore(string dbPath)
    {
        _connStr = SqliteSessionStore.BuildConnectionString(dbPath);
        EnsureSchema();
    }

    private void EnsureSchema()
    {
        using var conn = NewConn();
        Execute(conn, """
            CREATE TABLE IF NOT EXISTS cloud_budgets (
                team_id        TEXT PRIMARY KEY,
                currency       TEXT NOT NULL DEFAULT '',
                unit           TEXT NOT NULL DEFAULT 'currency',
                limit_value    REAL NOT NULL,
                spent          REAL NOT NULL DEFAULT 0,
                updated_at     INTEGER NOT NULL
            );
            """);
        Execute(conn, """
            CREATE TABLE IF NOT EXISTS cloud_budget_reservations (
                reservation_id TEXT PRIMARY KEY,
                team_id        TEXT NOT NULL,
                run_id         TEXT,
                estimated      REAL NOT NULL,
                actual         REAL,
                convert_rate   REAL NOT NULL DEFAULT 1,
                state          TEXT NOT NULL,
                created_at     INTEGER NOT NULL,
                settled_at     INTEGER
            );
            """);
        Execute(conn, "CREATE INDEX IF NOT EXISTS ix_cloud_res_team ON cloud_budget_reservations(team_id, state);");
    }

    public ValueTask UpsertAllowanceAsync(CloudBudgetAllowance allowance, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(allowance.TeamId))
            throw new ArgumentException("teamId required", nameof(allowance));
        using var conn = NewConn();
        var limit = Math.Max(0, allowance.Limit);
        var unit = UnitName(allowance.Unit);
        Execute(conn,
            """
            INSERT INTO cloud_budgets (team_id, currency, unit, limit_value, spent, updated_at)
            VALUES ($t, $c, $u, $l, 0, $now)
            ON CONFLICT(team_id) DO UPDATE SET
                currency    = excluded.currency,
                unit        = excluded.unit,
                limit_value = MAX(cloud_budgets.limit_value, excluded.limit_value),
                updated_at  = $now;
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("$t", allowance.TeamId);
                cmd.Parameters.AddWithValue("$c", allowance.Currency);
                cmd.Parameters.AddWithValue("$u", unit);
                cmd.Parameters.AddWithValue("$l", limit);
                cmd.Parameters.AddWithValue("$now", Now());
            });
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// One atomic check-and-draw inside a single IMMEDIATE transaction: the
    /// write lock is taken before the read, so when the remainder covers
    /// exactly one estimate, exactly one of N concurrent callers is admitted.
    /// </summary>
    public async ValueTask<CloudReservationResult> ReserveAsync(
        string teamId, double estimate, double convertRate, string? runId = null,
        CancellationToken cancellationToken = default)
    {
        if (estimate < 0) throw new ArgumentException("estimate must be >= 0", nameof(estimate));
        using var conn = NewConn();
        // IMMEDIATE: take the write lock before the read so a concurrent
        // reserve cannot interleave between our read and our write.
        await ExecAsync(conn, "BEGIN IMMEDIATE;", cancellationToken);
        try
        {
            var limit = 0.0;
            var spent = 0.0;
            bool teamExists = false;
            await using (var read = conn.CreateCommand())
            {
                read.CommandText = "SELECT limit_value, spent FROM cloud_budgets WHERE team_id = $t;";
                read.Parameters.AddWithValue("$t", teamId);
                await using var r = await read.ExecuteReaderAsync(cancellationToken);
                if (await r.ReadAsync(cancellationToken))
                {
                    teamExists = true;
                    limit = r.GetDouble(0);
                    spent = r.GetDouble(1);
                }
            }
            if (!teamExists)
            {
                await ExecAsync(conn, "ROLLBACK;", cancellationToken);
                return CloudReservationResult.Denied(
                    $"No cloud budget is configured for team '{teamId}' — the request is rejected; there is no implicit cloud spend and no fallback to another paid route.", 0);
            }
            var remaining = limit - spent;
            if (estimate > remaining)
            {
                await ExecAsync(conn, "ROLLBACK;", cancellationToken);
                return CloudReservationResult.Denied(
                    $"Cloud budget exhausted for team '{teamId}': remaining {remaining:0.###} < estimated {estimate:0.###}. " +
                    "Checkpoint or block the run at a safe boundary — never a hidden fallback.", remaining);
            }
            var reservationId = Guid.NewGuid().ToString("n");
            await ExecAsync(conn,
                "UPDATE cloud_budgets SET spent = spent + $e, updated_at = $now WHERE team_id = $t;",
                cancellationToken,
                ct =>
                {
                    ct.Parameters.AddWithValue("$e", estimate);
                    ct.Parameters.AddWithValue("$now", Now());
                    ct.Parameters.AddWithValue("$t", teamId);
                });
            await ExecAsync(conn,
                "INSERT INTO cloud_budget_reservations (reservation_id, team_id, run_id, estimated, convert_rate, state, created_at) VALUES ($r, $t, $run, $e, $cr, 'reserved', $now);",
                cancellationToken,
                ct =>
                {
                    ct.Parameters.AddWithValue("$r", reservationId);
                    ct.Parameters.AddWithValue("$t", teamId);
                    ct.Parameters.AddWithValue("$run", runId ?? (object)DBNull.Value);
                    ct.Parameters.AddWithValue("$e", estimate);
                    ct.Parameters.AddWithValue("$cr", convertRate);
                    ct.Parameters.AddWithValue("$now", Now());
                });
            await ExecAsync(conn, "COMMIT;", cancellationToken);
            return CloudReservationResult.Ok(reservationId, limit - spent - estimate);
        }
        catch
        {
            try { await ExecAsync(conn, "ROLLBACK;", cancellationToken); } catch { /* connection gone */ }
            throw;
        }
    }

    /// <summary>
    /// Durable facts about one reservation (11.2): the store is the durable home
    /// of reservation state — after a reload an in-memory policy snapshot is gone
    /// but this row persists, so the usage UI + the conclusion path read the
    /// reservation's denomination and estimate from here. Null when unknown.
    /// </summary>
    public ValueTask<CloudReservationInfo?> ReserveInfoAsync(string reservationId, CancellationToken cancellationToken = default)
    {
        using var conn = NewConn();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT b.team_id, b.currency, b.unit, r.convert_rate
            FROM cloud_budget_reservations r
            JOIN cloud_budgets b ON b.team_id = r.team_id
            WHERE r.reservation_id = $r;
            """;
        cmd.Parameters.AddWithValue("$r", reservationId);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return ValueTask.FromResult<CloudReservationInfo?>(null);
        return ValueTask.FromResult<CloudReservationInfo?>(new CloudReservationInfo(
            r.GetString(0), r.GetString(1), UnitFromName(r.GetString(2)), r.GetDouble(3)));
    }

    /// <summary>Release a reservation without spending it (the request never happened).</summary>

    public ValueTask ReleaseAsync(string reservationId, CancellationToken cancellationToken = default)
    {
        using var conn = NewConn();
        using var tx = conn.BeginTransaction();
        using (var upd = conn.CreateCommand())
        {
            upd.Transaction = tx;
            upd.CommandText = "UPDATE cloud_budget_reservations SET state = 'released', settled_at = $now WHERE reservation_id = $r AND state = 'reserved';";
            upd.Parameters.AddWithValue("$now", Now());
            upd.Parameters.AddWithValue("$r", reservationId);
            if (upd.ExecuteNonQuery() == 0) { tx.Rollback(); return ValueTask.CompletedTask; }
        }
        using (var refund = conn.CreateCommand())
        {
            refund.Transaction = tx;
            refund.CommandText = """
                UPDATE cloud_budgets SET spent = MAX(0, spent - (SELECT r.estimated FROM cloud_budget_reservations r WHERE r.reservation_id = $r))
                WHERE team_id = (SELECT team_id FROM cloud_budget_reservations WHERE reservation_id = $r);
                """;
            refund.Parameters.AddWithValue("$r", reservationId);
            refund.ExecuteNonQuery();
        }
        tx.Commit();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Settle a reservation with the reported usage: mark it settled and draw
    /// any excess over the reservation from the remainder. spent tracks true
    /// usage (unknown prices reconcile in tokens, the same allowance).
    /// Idempotent: a second settle returns false.
    /// </summary>
    public ValueTask<bool> SettleAsync(string reservationId, double actual, CancellationToken cancellationToken = default)
    {
        if (actual < 0) throw new ArgumentException("actual must be >= 0", nameof(actual));
        bool done;
        using var conn = NewConn();
        using var tx = conn.BeginTransaction();
        using (var upd = conn.CreateCommand())
        {
            upd.Transaction = tx;
            upd.CommandText = "UPDATE cloud_budget_reservations SET state = 'settled', actual = $a, settled_at = $now WHERE reservation_id = $r AND state = 'reserved';";
            upd.Parameters.AddWithValue("$a", actual);
            upd.Parameters.AddWithValue("$now", Now());
            upd.Parameters.AddWithValue("$r", reservationId);
            done = upd.ExecuteNonQuery() == 1;
        }
        if (done)
        {
            // Reconcile: replace the drawn estimate with the true usage — the
            // net delta (actual - reserved) is positive when the run overran,
            // negative when it undershot; spent tracks true usage.
            using (var reconcile = conn.CreateCommand())
            {
                reconcile.Transaction = tx;
                reconcile.CommandText = """
                    UPDATE cloud_budgets SET spent = MAX(0, spent + ($a - (SELECT COALESCE(r.estimated, 0) FROM cloud_budget_reservations r WHERE r.reservation_id = $r)))
                    WHERE team_id = (SELECT team_id FROM cloud_budget_reservations WHERE reservation_id = $r);
                    """;
                reconcile.Parameters.AddWithValue("$a", actual);
                reconcile.Parameters.AddWithValue("$r", reservationId);
                reconcile.ExecuteNonQuery();
            }
        }
        tx.Commit();
        return ValueTask.FromResult(done);
    }

    public ValueTask<IReadOnlyList<CloudBudgetUsage>> UsageAsync(CancellationToken cancellationToken = default)
    {
        var list = new List<CloudBudgetUsage>();
        using var conn = NewConn();
        using (var reader = conn.CreateCommand())
        {
            reader.CommandText = """
                SELECT b.team_id, b.currency, b.unit, b.limit_value, b.spent,
                       COALESCE((SELECT SUM(r.estimated) FROM cloud_budget_reservations r
                                 WHERE r.team_id = b.team_id AND r.state = 'reserved'), 0)
                FROM cloud_budgets b ORDER BY b.team_id;
                """;
            using var r = reader.ExecuteReader();
            while (r.Read())
            {
                var limit = r.GetDouble(3);
                var spent = r.GetDouble(4);
                var reserved = r.GetDouble(5);
                list.Add(new CloudBudgetUsage(
                    r.GetString(0), r.GetString(1), UnitFromName(r.GetString(2)),
                    limit, reserved, spent, Math.Max(0, limit - spent)));
            }
        }
        return ValueTask.FromResult<IReadOnlyList<CloudBudgetUsage>>(list);
    }

    // ---- helpers -----------------------------------------------------------

    private SqliteConnection NewConn()
    {
        var conn = new SqliteConnection(_connStr);
        conn.Open();
        RunPragma(conn);
        return conn;
    }

    private static void RunPragma(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA foreign_keys=ON;";
        cmd.ExecuteNonQuery();
    }

    private static int Execute(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteNonQuery();
    }

    private static int Execute(SqliteConnection conn, string sql, Action<SqliteCommand> configure)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        configure(cmd);
        return cmd.ExecuteNonQuery();
    }

    /// <summary>Run one statement (with optional parameters) inside the caller's open transaction.</summary>
    private static async ValueTask ExecAsync(SqliteConnection conn, string sql, CancellationToken ct, Action<SqliteCommand>? configure = null)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        configure?.Invoke(cmd);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private static string UnitName(CloudBudgetUnit u) => u == CloudBudgetUnit.Tokens ? "tokens" : "currency";
    private static CloudBudgetUnit UnitFromName(string n) => string.Equals(n, "tokens", StringComparison.OrdinalIgnoreCase)
        ? CloudBudgetUnit.Tokens : CloudBudgetUnit.Currency;
}
