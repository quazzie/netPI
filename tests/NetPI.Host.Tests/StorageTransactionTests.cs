using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using NetPI.Abstractions;
using NetPI.Storage.Sqlite;
using Xunit;

namespace NetPI.Host.Tests;

/// <summary>
/// astra-1 Package B — storage readiness for concurrent runs and project
/// transactions. Verifies, against isolated temp-dir databases, that:
///   * concurrent appends yield unique, gap-free, strictly-increasing
///     sequences — both on a single store and across two instances sharing a
///     DB (the DB, not a C# lock, serializes writers);
///   * a mid-transaction failure rolls back the whole append (transcript AND
///     session metadata unchanged) — and a clean append advances by exactly 1;
///   * the versioned migration runs against an EXISTING (old-shape) database,
///     records schema_version, preserves data, and is idempotent on re-open;
///   * old sessions migrate cleanly (project_id NULL, context_revision 0,
///     workspace preserved) and canonically-equivalent Windows paths
///     normalize to one key.
/// </summary>
public sealed class StorageTransactionTests : IDisposable
{
    private readonly string _dir =
        Directory.CreateTempSubdirectory("netpi-stor-tx-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* best effort; isolated per run */ }
    }

    private SqliteSessionStore Store() =>
        new(Path.Combine(_dir, Guid.NewGuid().ToString("n") + ".db"));

    private static SessionEntry Msg(string sid, string id, string text)
        => new(id, sid, EntryKind.Message,
            new AgentMessage(id, MessageRole.User, [new TextPart(text)], DateTimeOffset.UtcNow),
            null, DateTimeOffset.UtcNow, Sequence: 0);

    // ---- 1. concurrent appends -> unique, increasing sequences ----------

    [Fact]
    public async Task ConcurrentAppends_SingleStore_UniqueGapFreeSequences()
    {
        const int n = 50;
        using var store = Store();
        var info = await store.CreateAsync("/ws");

        // 50 concurrent appends against ONE store instance.
        var tasks = Enumerable.Range(0, n).Select(i =>
            store.AppendAsync(Msg(info.Id, "e" + i, "m" + i), CancellationToken.None).AsTask());
        await Task.WhenAll(tasks);

        var all = await store.ReadAsync(info.Id, 0, n * 2, CancellationToken.None);
        var seqs = all.Select(e => e.Sequence).ToList();

        Assert.Equal(n, seqs.Count);                       // none lost
        Assert.Equal(n, seqs.Distinct().Count());          // none duplicated
        Assert.Equal(Enumerable.Range(1, n), seqs.OrderBy(x => x)); // exactly 1..n
    }

    [Fact]
    public async Task ConcurrentAppends_TwoInstancesSameDb_UniqueGapFreeSequences()
    {
        // Two store instances opening the SAME file: proves the DB (WAL +
        // transactions), not an instance-level lock, serializes the writers.
        const int n = 40;
        var dbPath = Path.Combine(_dir, "twostores.db");
        using var a = new SqliteSessionStore(dbPath);
        using var b = new SqliteSessionStore(dbPath);

        var info = await a.CreateAsync("/ws");
        var half = n / 2;

        var fromA = Enumerable.Range(0, half).Select(i =>
            a.AppendAsync(Msg(info.Id, "a" + i, "A" + i), CancellationToken.None).AsTask());
        var fromB = Enumerable.Range(half, n - half).Select(i =>
            b.AppendAsync(Msg(info.Id, "b" + i, "B" + i), CancellationToken.None).AsTask());
        await Task.WhenAll(fromA.Concat(fromB).ToArray());

        var all = await a.ReadAsync(info.Id, 0, n * 2, CancellationToken.None);
        var seqs = all.Select(e => e.Sequence).ToList();

        Assert.Equal(n, seqs.Count);
        Assert.Equal(n, seqs.Distinct().Count());
        Assert.Equal(Enumerable.Range(1, n), seqs.OrderBy(x => x));
    }

    // ---- 2. transaction atomicity ---------------------------------------

    [Fact]
    public async Task FailedMidTransaction_Append_RollsBack_TranscriptAndMetadata()
    {
        using var store = Store();
        var info = await store.CreateAsync("/ws");
        // Seed one committed entry; note its ID (we'll collide with it).
        await store.AppendAsync(Msg(info.Id, "seed", "seed"), CancellationToken.None);

        var before = await store.GetAsync(info.Id, CancellationToken.None)!;
        var beforeEntries = await store.ReadAsync(info.Id, 0, 100, CancellationToken.None);
        Assert.Equal(1, before.EntryCount);

        // Appending an entry that reuses an existing id violates the
        // UNIQUE(id) constraint INSIDE the append transaction: the INSERT fails
        // after the sequence is allocated, so the whole transaction must roll
        // back. If the append were not atomic, the entry insert would survive
        // and the metadata would not — a detectable divergence.
        await Assert.ThrowsAsync<SqliteException>(() =>
            store.AppendAsync(Msg(info.Id, "seed", "collide"), CancellationToken.None).AsTask());

        var after = await store.GetAsync(info.Id, CancellationToken.None)!;
        var afterEntries = await store.ReadAsync(info.Id, 0, 100, CancellationToken.None);

        // Metadata unchanged: count and updated_at exactly as before.
        Assert.Equal(before.EntryCount, after.EntryCount);
        Assert.Equal(before.UpdatedAt, after.UpdatedAt);
        // Transcript unchanged: same single entry, the collision was rolled back.
        Assert.Equal(beforeEntries.Count, afterEntries.Count);
        Assert.Single(afterEntries);
        Assert.DoesNotContain(afterEntries, e => e.Id == "seed" && (e.Message!.Parts[0] as TextPart)!.Text == "collide");
        Assert.Equal(beforeEntries.Select(e => e.Sequence), afterEntries.Select(e => e.Sequence));
    }

    [Fact]
    public async Task SuccessfulAppend_AdvancesCountAndSequenceByExactlyOne()
    {
        using var store = Store();
        var info = await store.CreateAsync("/ws");
        var before = await store.GetAsync(info.Id, CancellationToken.None)!;
        Assert.Equal(0, before.EntryCount);

        await store.AppendAsync(Msg(info.Id, "one", "one"), CancellationToken.None);
        await store.AppendAsync(Msg(info.Id, "two", "two"), CancellationToken.None);

        var after = await store.GetAsync(info.Id, CancellationToken.None)!;
        Assert.Equal(2, after.EntryCount);
        Assert.True(after.UpdatedAt >= before.UpdatedAt);

        var all = await store.ReadAsync(info.Id, 0, 10, CancellationToken.None);
        Assert.Equal(1, all[0].Sequence);
        Assert.Equal(2, all[1].Sequence);
        Assert.Equal("one", (all[0].Message!.Parts[0] as TextPart)!.Text);
    }

    // ---- 3. migration against an existing (old-shape) DB -----------------

    [Fact]
    public async Task Migration_ExistingOldDatabase_PreservesDataAndRecordsVersion()
    {
        var dbPath = Path.Combine(_dir, "migrated.db");
        CreateOldShapeDatabase(dbPath);

        using var store = new SqliteSessionStore(dbPath);   // triggers migration

        // schema_version recorded for both applied versions.
        var versions = MigrationVersions(dbPath);
        Assert.Equal(new[] { 1, 2, 3 }, versions.OrderBy(v => v).ToArray());
        Assert.Equal(SqliteSessionStore.CurrentSchemaVersion, versions.Max());

        // Every row from the old shape survives: title, model, workspace, entries.
        var info = await store.GetAsync("legacy-1", CancellationToken.None);
        Assert.NotNull(info);
        Assert.Equal("Legacy Title", info!.Title);
        Assert.Equal("legacy-model", info.ModelId);
        Assert.Equal("/old/workspace", info.WorkspacePath);
        Assert.Equal(2, info.EntryCount);

        var entries = await store.ReadAsync("legacy-1", 0, 100, CancellationToken.None);
        Assert.Equal(2, entries.Count);
        Assert.Equal(1, entries[0].Sequence);
        Assert.Equal(2, entries[1].Sequence);
        Assert.Equal("first", (entries[0].Message!.Parts[0] as TextPart)!.Text);
        Assert.Equal("second", (entries[1].Message!.Parts[0] as TextPart)!.Text);

        // The store still functions after migration: an append works and a
        // new session can be created.
        await store.AppendAsync(Msg("legacy-1", "new-e", "third"), CancellationToken.None);
        var again = await store.GetAsync("legacy-1", CancellationToken.None)!;
        Assert.Equal(3, again.EntryCount);
        Assert.NotNull(await store.CreateAsync("/new"));
    }

    [Fact]
    public async Task Migration_ReOpen_IsIdempotent_NoDuplicateRowsNoDataChange()
    {
        var dbPath = Path.Combine(_dir, "idem.db");
        CreateOldShapeDatabase(dbPath);

        using (var first = new SqliteSessionStore(dbPath))
        {
            // Let the first open apply the migration and add one more row.
            await first.AppendAsync(Msg("legacy-1", "extra", "extra"), CancellationToken.None);
        }
        var versionsAfterFirst = MigrationVersions(dbPath);
        Assert.Equal(3, versionsAfterFirst.Count);
        Assert.Equal(Enumerable.Range(1, SqliteSessionStore.CurrentSchemaVersion),
            versionsAfterFirst.OrderBy(v => v));

        // Re-open (simulates a plugin reload): migration must be a no-op.
        using (var second = new SqliteSessionStore(dbPath))
        {
            var info = await second.GetAsync("legacy-1", CancellationToken.None)!;
            Assert.Equal(3, info!.EntryCount);   // seed rows + the one appended
            Assert.Equal("Legacy Title", info.Title);

            // The projects table exists but was not repopulated (it was empty).
            var projects = ProjectCount(dbPath);
            Assert.Equal(0, projects);
        }

        // No duplicate migration rows after the second open.
        var versionsAfterSecond = MigrationVersions(dbPath);
        Assert.Equal(3, versionsAfterSecond.Count);
        Assert.Equal(new[] { 1, 2, 3 }, versionsAfterSecond.OrderBy(v => v).ToArray());
    }

    // ---- 4. old sessions migrate cleanly (Package C base) ----------------

    [Fact]
    public void Migration_OldSessions_GetNullProjectAndZeroRevision_WorkspacePreserved()
    {
        var dbPath = Path.Combine(_dir, "cols.db");
        CreateOldShapeDatabase(dbPath);

        using var store = new SqliteSessionStore(dbPath);

        var cols = ColumnInfo(dbPath, "sessions");
        Assert.Contains("project_id", cols);
        Assert.Contains("context_revision", cols);
        Assert.Contains("workspace", cols);

        var (projectId, revision, workspace) = SessionProjectColumns(dbPath, "legacy-1");
        Assert.True(projectId == null, "project_id must be NULL for a migrated session");
        Assert.Equal(0, revision);
        Assert.Equal("/old/workspace", workspace);
    }

    // ---- 5. canonical Windows path key ----------------------------------

    [Fact]
    public void NormalizeProjectPathKey_CanonicalWindowsPaths_CollapseToOne()
    {
        Assert.Equal(
            SqliteSessionStore.NormalizeProjectPathKey(@"C:\AI\X"),
            SqliteSessionStore.NormalizeProjectPathKey("c:/ai/x"));
        Assert.Equal(
            SqliteSessionStore.NormalizeProjectPathKey(@"C:\AI\X"),
            SqliteSessionStore.NormalizeProjectPathKey(@"C:\AI\X\"));
        // distinct paths stay distinct
        Assert.NotEqual(
            SqliteSessionStore.NormalizeProjectPathKey(@"C:\AI\X"),
            SqliteSessionStore.NormalizeProjectPathKey(@"C:\AI\Y"));
    }

    // ---- raw-DB helpers (to build an old-shape DB and inspect schema) ----

    /// <summary>
    /// Builds a pre-migration database: the old <c>sessions</c> shape (no
    /// project_id/context_revision), <c>session_entries</c> and <c>settings</c>,
    /// with no <c>migrations</c> table and one seeded session.
    /// </summary>
    private static void CreateOldShapeDatabase(string dbPath)
    {
        var connStr = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString();

        using var conn = new SqliteConnection(connStr);
        conn.Open();
        Run(conn,
            """
            CREATE TABLE sessions (
                id TEXT PRIMARY KEY,
                title TEXT,
                workspace TEXT,
                provider_id TEXT,
                model_id TEXT,
                reasoning_level TEXT,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                last_sequence INTEGER NOT NULL DEFAULT 0
            );
            CREATE TABLE session_entries (
                id TEXT PRIMARY KEY,
                session_id TEXT NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
                seq INTEGER NOT NULL,
                entry_type TEXT NOT NULL,
                created_at TEXT NOT NULL,
                payload_json TEXT NOT NULL,
                UNIQUE (session_id, seq)
            );
            CREATE TABLE settings (key TEXT PRIMARY KEY, value_json TEXT NOT NULL);
            """
        );
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        Run(conn,
            "INSERT INTO sessions (id, title, workspace, model_id, created_at, updated_at, last_sequence) " +
            "VALUES ('legacy-1', 'Legacy Title', '/old/workspace', 'legacy-model', $now, $now, 2);",
            c => { c.Parameters.AddWithValue("$now", now); });
        // Two seeded message entries (payloads are real MessageSerializer JSON
        // so Rehydrate round-trips them).
        for (var i = 1; i <= 2; i++)
        {
            var e = Msg("legacy-1", "seed-" + i, i == 1 ? "first" : "second");
            Run(conn,
                "INSERT INTO session_entries (id, session_id, seq, entry_type, created_at, payload_json) " +
                "VALUES ($id, 'legacy-1', $seq, 'Message', $now, $payload);",
                c =>
                {
                    c.Parameters.AddWithValue("$id", e.Id);
                    c.Parameters.AddWithValue("$seq", i);
                    c.Parameters.AddWithValue("$now", e.CreatedAt.ToUnixTimeMilliseconds());
                    c.Parameters.AddWithValue("$payload",
                        MessageSerializer.Serialize(e.Message!).ToString());
                });
        }
    }

    private static void Run(SqliteConnection conn, string sql, Action<SqliteCommand>? cfg = null)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cfg?.Invoke(cmd);
        cmd.ExecuteNonQuery();
    }

    private static IReadOnlyList<int> MigrationVersions(string dbPath)
    {
        using var conn = OpenRaw(dbPath);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT version FROM migrations ORDER BY version;";
        var list = new List<int>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(Convert.ToInt32(r.GetValue(0)));
        return list;
    }

    private static int ProjectCount(string dbPath)
    {
        using var conn = OpenRaw(dbPath);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM projects;";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private static IReadOnlyList<string> ColumnInfo(string dbPath, string table)
    {
        using var conn = OpenRaw(dbPath);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table});";
        var list = new List<string>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(r.GetString(1));
        return list;
    }

    private static (int? projectId, int revision, string? workspace) SessionProjectColumns(string dbPath, string id)
    {
        using var conn = OpenRaw(dbPath);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT project_id, context_revision, workspace FROM sessions WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        Assert.True(r.Read());
        int? projectId = r.IsDBNull(0) ? null : Convert.ToInt32(r.GetValue(0));
        var revision = Convert.ToInt32(r.GetValue(1));
        var workspace = r.IsDBNull(2) ? null : r.GetString(2);
        return (projectId, revision, workspace);
    }

    private static SqliteConnection OpenRaw(string dbPath)
    {
        var s = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString();
        var conn = new SqliteConnection(s);
        conn.Open();
        return conn;
    }
}
