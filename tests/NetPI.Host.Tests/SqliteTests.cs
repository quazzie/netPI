using System.Text.Json;
using System.Text.Json.Nodes;
using NetPI.Host.Storage.Sqlite;

namespace NetPI.Host.Tests;

public static class SqliteTests
{
    private enum Color { Red = 1, Blue = 7 }

    private static Database Open(out string file)
    {
        file = Path.Combine(T.TempDir("sqlite"), "test.db");
        return new Database(file);
    }

    public static void Register(TestRunner r)
    {
        r.Add("sqlite: WAL, foreign keys and busy timeout are configured", () =>
        {
            using var db = Open(out _);
            Check.Equal("wal", db.Scalar<string>("PRAGMA journal_mode"));
            Check.Equal(1L, db.Scalar<long>("PRAGMA foreign_keys"));
            Check.Equal(5000L, db.Scalar<long>("PRAGMA busy_timeout"));
            Check.Equal(1L, db.Scalar<long>("PRAGMA synchronous")); // NORMAL
            // The tuning pragmas (idea-z86rze): reads through a map and a cache that fits the database, a capped WAL
            // file, and no inline checkpoint (the idle timer truncates instead).
            Check.True(db.Scalar<long>("PRAGMA mmap_size") >= 1073741824, "mmap at least a GB: " + db.Scalar<long>("PRAGMA mmap_size"));
            Check.Equal(-65536L, db.Scalar<long>("PRAGMA cache_size"));
            Check.Equal(67108864L, db.Scalar<long>("PRAGMA journal_size_limit"));
            Check.Equal(0L, db.Scalar<long>("PRAGMA wal_autocheckpoint"));
        });

        r.Add("sqlite: the hot queries seek their indexes (EXPLAIN QUERY PLAN)", () =>
        {
            using var db = Open(out _);
            SqliteStorage.EnsureSchema(db);
            db.Execute("INSERT INTO sessions(id, title, created_at, updated_at) VALUES('s1', 't', 1, 1)");
            for (var i = 1; i <= 50; i++)
                db.Execute("INSERT INTO messages(session_id, seq, role, parts, created_at, compacted) VALUES('s1', @seq, 'user', '[]', 1, @c)",
                    new { seq = i, c = i <= 25 ? 1 : 0 });
            string Plan(string sql, object? args) =>
                string.Join(" | ", db.Query("EXPLAIN QUERY PLAN " + sql, args, r => r.GetString("detail")));

            // The context read of every turn: the session's live rows only.
            var context = Plan("""
                SELECT id, session_id, seq, role, parts, created_at, provider, model, stop_reason, usage, duration_ms, compacted, meta
                FROM messages WHERE session_id = @s AND compacted = 0 ORDER BY seq
                """, new { s = "s1" });
            Check.Contains(context, "ix_messages_live", "the context read seeks the live-only index: " + context);
            Check.NotContains(context, "SCAN messages", context);

            // The paged read (sessions.messages) and the compaction's flag update: both over (session_id, seq).
            var page = Plan("""
                SELECT id, session_id, seq, role, parts, created_at, provider, model, stop_reason, usage, duration_ms, compacted, meta
                FROM messages
                WHERE session_id = @s AND seq < COALESCE(@before, 9223372036854775807)
                ORDER BY seq DESC LIMIT @limit
                """, new { s = "s1", before = (long?)40, limit = 10 });
            Check.Contains(page, "ix_messages_session_seq", "the paged read seeks (session_id, seq): " + page);
            Check.NotContains(page, "SCAN messages", page);

            var mark = Plan("UPDATE messages SET compacted = 1 WHERE session_id = @s AND seq <= @upTo AND compacted = 0",
                new { s = "s1", upTo = 40L });
            Check.Contains(mark, "ix_messages_live", "MarkCompacted finds its rows through the live-only index: " + mark);
            Check.NotContains(mark, "SCAN messages", mark);
        });

        r.Add("sqlite: the backup is a consistent copy of a live database, un-checkpointed WAL included", () =>
        {
            using var db = Open(out var file);
            SqliteStorage.EnsureSchema(db);
            db.Execute("INSERT INTO sessions(id, title, created_at, updated_at) VALUES('s1', 't', 1, 1)");
            for (var i = 1; i <= 200; i++)
                db.Execute("INSERT INTO messages(session_id, seq, role, parts, created_at) VALUES('s1', @seq, 'user', '[]', 1)", new { seq = i });
            var wal = file + "-wal";
            Check.True(File.Exists(wal) && new FileInfo(wal).Length > 0, "with auto-checkpoint off the frames are in the WAL");
            var copy = Path.Combine(Path.GetDirectoryName(file)!, "copy.db");
            db.BackupTo(copy);
            using (var dest = new Database(copy))
            {
                Check.Equal("ok", dest.Scalar<string>("PRAGMA integrity_check"));
                Check.Equal(1L, dest.Scalar<long>("SELECT COUNT(*) FROM sessions WHERE id = @id", new { id = "s1" }));
                Check.Equal(200L, dest.Scalar<long>("SELECT COUNT(*) FROM messages"), "the un-checkpointed frames are in the copy");
            }
        });

        r.Add("sqlite: the TRUNCATE checkpoint shrinks the WAL back to nothing", () =>
        {
            using var db = Open(out var file);
            db.Execute("CREATE TABLE t (v INTEGER)");
            for (var i = 1; i <= 200; i++) db.Execute("INSERT INTO t VALUES(@v)", new { v = i });
            var wal = file + "-wal";
            Check.True(File.Exists(wal) && new FileInfo(wal).Length > 0, "the writes left a WAL");
            db.Execute("PRAGMA wal_checkpoint(TRUNCATE)");
            Check.True(!File.Exists(wal) || new FileInfo(wal).Length == 0, "and the TRUNCATE checkpoint takes it back");
            Check.Equal(200L, db.Scalar<long>("SELECT COUNT(*) FROM t"), "everything was in the database before too");
        });

        r.Add("sqlite: type mapping round-trips", () =>
        {
            using var db = Open(out _);
            db.Execute("CREATE TABLE t (id INTEGER PRIMARY KEY, i INTEGER, l INTEGER, d REAL, s TEXT, u TEXT, b BLOB, n TEXT, f INTEGER, ts INTEGER, dt INTEGER, g TEXT, e INTEGER, j TEXT, je TEXT, m REAL, eb BLOB)");
            var when = new DateTimeOffset(2026, 9, 23, 10, 11, 12, 345, TimeSpan.FromHours(2));
            var guid = Guid.NewGuid();
            var id = db.Insert("""
                INSERT INTO t(i, l, d, s, u, b, n, f, ts, dt, g, e, j, je, m, eb)
                VALUES(@i, @l, @d, @s, @u, @b, @n, @f, @ts, @dt, @g, @e, @j, @je, @m, @eb)
                """, new
            {
                i = 42, l = long.MaxValue, d = 3.25, s = "hello", u = "ünïcødé ✓ 日本語 🎉", b = new byte[] { 0, 1, 2, 255 },
                n = (string?)null, f = true, ts = when, dt = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc), g = guid, e = Color.Blue,
                j = new JsonObject { ["a"] = 1, ["b"] = new JsonArray("x") }, je = JsonDocument.Parse("""{"k":[1,2]}""").RootElement,
                m = 1.5m, eb = Array.Empty<byte>(),
            });
            Check.Equal(1L, id);
            var row = db.QuerySingle("SELECT * FROM t WHERE id = @id", new { id }, x => new
            {
                I = x.GetInt64("i"), L = x.GetInt64("l"), D = x.GetDouble("d"), S = x.GetString("s"), U = x.GetString("u"),
                B = x.GetBlob("b"), NIsNull = x.IsNull("n"), N = x.GetStringOrNull("n"), F = x.GetInt64("f"), Ts = x.GetInt64("ts"),
                Dt = x.GetInt64("dt"), G = x.GetString("g"), E = x.GetInt64("e"), J = x.GetString("j"), Je = x.GetString("je"),
                M = x.GetDouble("m"), Eb = x.GetBlob("eb"), EbType = x.GetValue(x.Ordinal("eb"))?.GetType(), NullInt = x.GetInt64OrNull("n"),
            })!;
            Check.Equal(42L, row.I);
            Check.Equal(long.MaxValue, row.L);
            Check.Equal(3.25, row.D);
            Check.Equal("hello", row.S);
            Check.Equal("ünïcødé ✓ 日本語 🎉", row.U);
            Check.Equal("0,1,2,255", string.Join(",", row.B!));
            Check.True(row.NIsNull);
            Check.Equal<string?>(null, row.N);
            Check.Equal<long?>(null, row.NullInt);
            Check.Equal(1L, row.F);
            Check.Equal(when.ToUnixTimeMilliseconds(), row.Ts);
            Check.Equal(new DateTimeOffset(2020, 1, 2, 3, 4, 5, TimeSpan.Zero).ToUnixTimeMilliseconds(), row.Dt);
            Check.Equal(guid.ToString(), row.G);
            Check.Equal(7L, row.E);
            Check.Equal("""{"a":1,"b":["x"]}""", row.J);
            Check.Equal("""{"k":[1,2]}""", row.Je);
            Check.Equal(1.5, row.M);
            Check.Equal(0, row.Eb!.Length);
            Check.Equal(typeof(byte[]), row.EbType, "empty blob stays a blob, not NULL");

            Check.Equal(when.ToUnixTimeMilliseconds(), db.Scalar<DateTimeOffset>("SELECT ts FROM t").ToUnixTimeMilliseconds());
            Check.Equal(Color.Blue, db.Scalar<Color>("SELECT e FROM t"));
            Check.Equal(guid, db.Scalar<Guid>("SELECT g FROM t"));
            Check.Equal(true, db.Scalar<bool>("SELECT f FROM t"));
            Check.Equal(42, db.Scalar<int>("SELECT i FROM t"));
            Check.Equal<int?>(null, db.Scalar<int?>("SELECT i FROM t WHERE id = -1"));
            Check.Equal(1, db.Scalar<JsonObject>("SELECT j FROM t")!["a"]!.GetValue<int>());
            Check.Equal("42", db.Scalar<string>("SELECT i FROM t"));
        });

        r.Add("sqlite: parameters by @, : and $ from objects, dictionaries and JsonObject", () =>
        {
            using var db = Open(out _);
            db.Execute("CREATE TABLE p (a TEXT, b INTEGER, c REAL)");
            db.Execute("INSERT INTO p VALUES(@a, :b, $c)", new { a = "anon", b = 1, c = 1.5 });
            db.Execute("INSERT INTO p VALUES(@a, :b, $c)", new Dictionary<string, object?> { ["a"] = "dict", ["B"] = 2L, ["c"] = null });
            db.Execute("INSERT INTO p VALUES(@a, :b, $c)", new JsonObject { ["a"] = "json", ["b"] = 3, ["c"] = 2.5 });
            db.Execute("INSERT INTO p VALUES(?, ?, ?)", new object?[] { "positional", 4, 3.5 });
            var rows = db.Query("SELECT a, b, c FROM p ORDER BY b", null, x => $"{x.GetString("a")}|{x.GetInt64("b")}|{x.GetStringOrNull("c") ?? "null"}");
            Check.Equal("anon|1|1.5,dict|2|null,json|3|2.5,positional|4|3.5", string.Join(",", rows));
            // Same parameter referenced twice, and a reused cached statement.
            Check.Equal(2L, db.Scalar<long>("SELECT COUNT(*) FROM p WHERE b >= @min AND b <= @min + 1", new { min = 2 }));
            Check.Equal(2L, db.Scalar<long>("SELECT COUNT(*) FROM p WHERE b >= @min AND b <= @min + 1", new { min = 3 }));
            var missing = Check.Throws<InvalidOperationException>(() => db.Execute("INSERT INTO p VALUES(@a, @b, @c)", new { a = "x", b = 1 }));
            Check.Contains(missing.Message, "@c");
        });

        r.Add("sqlite: errors carry the SQL and the SQLite message", () =>
        {
            using var db = Open(out _);
            db.Execute("CREATE TABLE u (id INTEGER PRIMARY KEY, name TEXT NOT NULL UNIQUE)");
            db.Execute("INSERT INTO u(name) VALUES('a')");
            var ex = Check.Throws<SqliteException>(() => db.Execute("INSERT INTO u(name) VALUES('a')"));
            Check.Contains(ex.Message, "UNIQUE constraint failed");
            Check.Contains(ex.Message, "INSERT INTO u(name) VALUES('a')");
            Check.Equal(19, ex.PrimaryCode);
            var syntax = Check.Throws<SqliteException>(() => db.Query("SELEC nonsense", null, x => 1));
            Check.Contains(syntax.Message, "syntax error");
        });

        r.Add("sqlite: a programmer's SQL error is an internal error, not a 400 for the caller", () =>
        {
            using var db = Open(out _);
            db.Execute("CREATE TABLE p (id INTEGER PRIMARY KEY, v TEXT)");
            // The SQL comes from this process, not from a client: it must surface as a 500 with a log line
            // (InvalidOperationException falls to MapError's default), not as a 400 "bad_request" blaming the
            // caller of the RPC that happened to be running.
            var noArgs = Check.Throws<InvalidOperationException>(() => db.Query("SELECT v FROM p WHERE id = @id", null, x => x.GetString("v")));
            Check.Contains(noArgs.Message, "no arguments were given");
            var missing = Check.Throws<InvalidOperationException>(() => db.Query("SELECT v FROM p WHERE id = @id", new { other = 1 }, x => 1));
            Check.Contains(missing.Message, "Missing SQL parameter");
            var noStatement = Check.Throws<InvalidOperationException>(() => db.Scalar<long>("-- nothing but a comment"));
            Check.Contains(noStatement.Message, "no statement");
            var positional = Check.Throws<InvalidOperationException>(() => db.Query("SELECT ? FROM p", 42, x => 1));
            Check.Contains(positional.Message, "needs an array/list argument");
        });

        r.Add("sqlite: transactions commit, roll back and nest (reentrant lock)", () =>
        {
            using var db = Open(out _);
            db.Execute("CREATE TABLE x (v INTEGER)");
            db.Transaction(tx =>
            {
                tx.Execute("INSERT INTO x VALUES(1)");
                // Reentrancy: the mapper calls back into the database while a statement is being stepped.
                var nested = tx.Query("SELECT v FROM x", null, row => tx.Scalar<long>("SELECT COUNT(*) FROM x") + row.GetInt64("v"));
                Check.Equal(2L, nested.Single());
            });
            Check.Throws<InvalidOperationException>(() => db.Transaction(tx =>
            {
                tx.Execute("INSERT INTO x VALUES(2)");
                throw new InvalidOperationException("boom");
            }));
            Check.Equal(1L, db.Scalar<long>("SELECT COUNT(*) FROM x"), "rolled back");
            db.Transaction(tx =>
            {
                tx.Execute("INSERT INTO x VALUES(3)");
                try
                {
                    tx.Transaction(inner =>
                    {
                        inner.Execute("INSERT INTO x VALUES(4)");
                        throw new InvalidOperationException("inner");
                    });
                }
                catch (InvalidOperationException) { }
            });
            Check.Equal("1,3", string.Join(",", db.Query("SELECT v FROM x ORDER BY v", null, x => x.GetInt64("v"))), "inner savepoint rolled back only");
            var total = db.Transaction(tx => tx.Scalar<long>("SELECT SUM(v) FROM x"));
            Check.Equal(4L, total);
        });

        r.Add("sqlite: concurrent callers are serialized", async () =>
        {
            using var db = Open(out _);
            db.Execute("CREATE TABLE c (v INTEGER)");
            await Task.WhenAll(Enumerable.Range(0, 8).Select(t => Task.Run(() =>
            {
                for (var i = 0; i < 100; i++) db.Transaction(tx => tx.Execute("INSERT INTO c VALUES(@v)", new { v = t * 1000 + i }));
            })));
            Check.Equal(800L, db.Scalar<long>("SELECT COUNT(*) FROM c"));
        });

        r.Add("sqlite: migrations are applied once, per scope, incrementally", () =>
        {
            string file;
            using (var db = Open(out file))
            {
                db.Migrate("alpha", "CREATE TABLE a1 (x INTEGER)", "ALTER TABLE a1 ADD COLUMN y TEXT");
                db.Migrate("alpha", "CREATE TABLE a1 (x INTEGER)", "ALTER TABLE a1 ADD COLUMN y TEXT");
                db.Migrate("beta", "CREATE TABLE b1 (x INTEGER); CREATE INDEX ix_b1 ON b1(x);");
                Check.Equal(2L, db.Scalar<long>("SELECT version FROM _migrations WHERE scope = 'alpha'"));
                Check.Equal(1L, db.Scalar<long>("SELECT version FROM _migrations WHERE scope = 'beta'"));
            }
            using (var db = new Database(file))
            {
                db.Migrate("alpha", "CREATE TABLE a1 (x INTEGER)", "ALTER TABLE a1 ADD COLUMN y TEXT", "CREATE TABLE a2 (z INTEGER)");
                Check.Equal(3L, db.Scalar<long>("SELECT version FROM _migrations WHERE scope = 'alpha'"));
                db.Execute("INSERT INTO a1(x, y) VALUES(1, 'y')");
                db.Execute("INSERT INTO a2(z) VALUES(1)");
                var ex = Check.Throws<SqliteException>(() => db.Migrate("gamma", "CREATE TABLE g (x INTEGER)", "THIS IS NOT SQL"));
                Check.Contains(ex.Message, "Migration 2 of scope 'gamma'");
                Check.Equal(1L, db.Scalar<long>("SELECT version FROM _migrations WHERE scope = 'gamma'"), "failed step rolled back, first kept");
            }
        });

        r.Add("sqlite: a library older than the minimum is refused at open, naming the library and the minimum", () =>
        {
            SqliteStorageProvider.CheckVersion(3_035_000, "3.35.0", "(bundled)");
            SqliteStorageProvider.CheckVersion(3_041_000, "3.41.0", "(bundled)");
            var failure = Check.Throws<StorageException>(() => SqliteStorageProvider.CheckVersion(3_034_999, "3.34.9", "C:\\old\\sqlite3.dll"));
            Check.Contains(failure.Message, "3.34.9", "it names the version found: " + failure.Message);
            Check.Contains(failure.Message, "3.35.0", "and the one needed");
            Check.Contains(failure.Message, "sqlite3.dll", "and where the library came from");
        });

        r.Add("sqlite: the session list seeks its own index (EXPLAIN QUERY PLAN)", () =>
        {
            using var db = Open(out _);
            SqliteStorage.EnsureSchema(db);
            db.Execute("INSERT INTO sessions(id, title, created_at, updated_at) VALUES('s1', 't', 1, 1)");
            db.Execute("INSERT INTO sessions(id, title, created_at, updated_at, pinned) VALUES('s2', 't', 2, 2, 1)");
            var plan = string.Join(" | ", db.Query(
                """
                EXPLAIN QUERY PLAN
                SELECT id, title, project_id, parent_session_id, kind, model, reasoning, created_at, updated_at, archived, pinned, message_count, context_tokens, meta
                FROM sessions ORDER BY pinned DESC, updated_at DESC, id DESC
                """, null, r => r.GetString("detail")));
            // No WHERE: the whole point is the order, and the index is in that order, so the read walks the index itself
            // (a scan of it, not of the table) instead of scanning the table and sorting.
            Check.Contains(plan, "USING INDEX ix_sessions_list", "the list read walks the list index: " + plan);
            Check.NotContains(plan, "TEMP B-TREE", "the ordered index means no sort: " + plan);
        });

        r.Add("sqlite: an idle checkpoint leaves an empty WAL alone, and takes a non-empty one only when quiet", () =>
        {
            using var db = Open(out var file);
            db.Execute("CREATE TABLE t(x)");
            db.Execute("INSERT INTO t VALUES(1)");
            db.Execute("PRAGMA wal_checkpoint(TRUNCATE)");
            Check.Equal(0, new FileInfo(file + "-wal").Length, "the WAL is empty");

            db.LastActivity = 0;   // long quiet
            db.IdleCheckpoint(null);
            Check.Equal(0, db.LastActivity, "an empty WAL is not activity: the checkpoint that would refresh it is the one that never comes");
            Check.Equal(0, new FileInfo(file + "-wal").Length, "and nothing was written into it");

            db.Execute("INSERT INTO t VALUES(2)");
            Check.True(new FileInfo(file + "-wal").Length > 0, "the write left the WAL non-empty");
            db.LastActivity = 0;
            db.IdleCheckpoint(null);
            Check.Equal(0, new FileInfo(file + "-wal").Length, "a quiet database with a WAL to empty is checkpointed");
            Check.Equal(0, db.LastActivity, "the checkpoint itself counts as no activity: only statements make the database busy");
        });
    }
}
