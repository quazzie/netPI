using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace NetPI.Host.Data;

/// <summary>
/// <see cref="IDatabase"/> over a single SQLite connection. Every call is serialized by a reentrant monitor, so a
/// <see cref="Transaction{T}"/> body (or a Query row mapper) can freely call other methods on the same thread.
/// Statements are prepared once and cached per SQL text.
/// </summary>
internal sealed unsafe class Database : IDatabase, IDisposable
{
    private const int MaxCachedCommands = 256;

    private readonly object _gate = new();
    private readonly Dictionary<string, Command> _cache = new(StringComparer.Ordinal);
    private readonly ILogger? _log;
    private IntPtr _db;
    private int _txDepth;

    public string FilePath { get; }

    public Database(string filePath, ILogger? log = null)
    {
        _log = log;
        FilePath = filePath;
        Sqlite3.EnsureLoaded();

        var nameZ = Utf8.ToZ(filePath);
        IntPtr db;
        int rc;
        fixed (byte* p = nameZ)
            rc = Sqlite3.sqlite3_open_v2(p, &db, Sqlite3.OPEN_READWRITE | Sqlite3.OPEN_CREATE | Sqlite3.OPEN_NOMUTEX, null);
        if (rc != Sqlite3.OK)
        {
            var ex = SqliteException.From(db, rc, null, $"Cannot open database '{filePath}'");
            if (db != IntPtr.Zero) Sqlite3.sqlite3_close_v2(db);
            throw ex;
        }
        _db = db;
        Sqlite3.sqlite3_busy_timeout(_db, 5000);
        ExecScript("PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=ON; PRAGMA temp_store=MEMORY;");
        _log?.LogDebug("SQLite {Version} ({Lib}) opened {File}", Sqlite3.Version, Sqlite3.LoadedFrom, filePath);
    }

    // ------------------------------------------------------------------ IDatabase

    public int Execute(string sql, object? args = null)
    {
        lock (_gate)
        {
            var cmd = Rent(sql);
            try
            {
                var changes = 0;
                foreach (var st in cmd.Statements)
                {
                    Bind(st, args, sql);
                    StepToEnd(st, sql);
                    changes = Sqlite3.sqlite3_changes(_db);
                }
                return changes;
            }
            finally { Return(cmd); }
        }
    }

    public long Insert(string sql, object? args = null)
    {
        lock (_gate)
        {
            Execute(sql, args);
            return Sqlite3.sqlite3_last_insert_rowid(_db);
        }
    }

    public T? Scalar<T>(string sql, object? args = null)
    {
        lock (_gate)
        {
            var found = false;
            object? value = null;
            Run(sql, args, row =>
            {
                if (row.FieldCount > 0) value = row.GetValue(0);
                found = true;
                return false;
            });
            return found ? DbConvert.To<T>(value) : default;
        }
    }

    public List<T> Query<T>(string sql, object? args, Func<IDbRow, T> map)
    {
        ArgumentNullException.ThrowIfNull(map);
        List<RowSnapshot>? rows = null;
        lock (_gate)
            rows = Snapshots(sql, args);
        // The mapper is the caller's: it deserializes JSON (a message's parts can be megabytes), and that must not
        // happen under the gate, or one long read blocks every other statement in the process for its whole length.
        var list = new List<T>(rows.Count);
        foreach (var row in rows) list.Add(map(row));
        return list;
    }

    public T? QuerySingle<T>(string sql, object? args, Func<IDbRow, T> map)
    {
        ArgumentNullException.ThrowIfNull(map);
        List<RowSnapshot>? rows = null;
        lock (_gate)
            rows = Snapshots(sql, args, firstRowOnly: true);
        return rows.Count == 0 ? default : map(rows[0]);
    }

    public T Transaction<T>(Func<IDatabase, T> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        lock (_gate)
        {
            var depth = _txDepth;
            var savepoint = "netpi_sp" + depth.ToString(CultureInfo.InvariantCulture);
            ExecScript(depth == 0 ? "BEGIN IMMEDIATE" : "SAVEPOINT " + savepoint);
            _txDepth++;
            var committed = false;
            try
            {
                var result = work(this);
                ExecScript(depth == 0 ? "COMMIT" : "RELEASE " + savepoint);
                committed = true;
                return result;
            }
            finally
            {
                _txDepth--;
                if (!committed)
                {
                    try { ExecScript(depth == 0 ? "ROLLBACK" : $"ROLLBACK TO {savepoint}; RELEASE {savepoint}"); }
                    catch (Exception ex) { _log?.LogWarning(ex, "Rollback failed"); }
                }
            }
        }
    }

    public void Transaction(Action<IDatabase> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        Transaction<object?>(db => { work(db); return null; });
    }

    public void Migrate(string scope, params string[] migrations)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        lock (_gate)
        {
            ExecScript("CREATE TABLE IF NOT EXISTS _migrations (scope TEXT PRIMARY KEY, version INTEGER NOT NULL)");
            var current = Scalar<long?>("SELECT version FROM _migrations WHERE scope = @scope", new { scope }) ?? 0;
            if (current > migrations.Length)
                _log?.LogWarning("Database scope '{Scope}' is at version {Version}, newer than the {Count} known migrations", scope, current, migrations.Length);
            for (var i = (int)current; i < migrations.Length; i++)
            {
                var step = i;
                try
                {
                    Transaction(db =>
                    {
                        ExecScript(migrations[step]);
                        Execute("INSERT INTO _migrations(scope, version) VALUES(@scope, @v) ON CONFLICT(scope) DO UPDATE SET version = excluded.version",
                            new { scope, v = step + 1 });
                    });
                }
                catch (SqliteException ex)
                {
                    throw new SqliteException(ex.Code, $"Migration {step + 1} of scope '{scope}' failed: {ex.Message}", null);
                }
                _log?.LogDebug("Database scope '{Scope}' migrated to version {Version}", scope, step + 1);
            }
        }
    }

    // ------------------------------------------------------------------ execution core

    /// <summary>Runs every statement of <paramref name="sql"/>; rows of the last one are passed to <paramref name="onRow"/> until it returns false.</summary>
    private void Run(string sql, object? args, Func<Row, bool> onRow)
    {
        var cmd = Rent(sql);
        try
        {
            var statements = cmd.Statements;
            for (var i = 0; i < statements.Length - 1; i++)
            {
                Bind(statements[i], args, sql);
                StepToEnd(statements[i], sql);
            }
            var st = statements[^1];
            Bind(st, args, sql);
            var row = new Row(st, sql);
            while (true)
            {
                var rc = Sqlite3.sqlite3_step(st.Handle);
                if (rc == Sqlite3.ROW)
                {
                    if (!onRow(row)) break;
                    continue;
                }
                if (rc == Sqlite3.DONE) return;
                throw SqliteException.From(_db, rc, sql);
            }
            // The caller stopped reading early (a single-row query): finish the statement anyway. A statement reset
            // before DONE is abandoned, not committed — which would silently drop an INSERT ... RETURNING's write.
            StepToEnd(st, sql);
        }
        finally { Return(cmd); }
    }

    private void StepToEnd(Statement st, string sql)
    {
        while (true)
        {
            var rc = Sqlite3.sqlite3_step(st.Handle);
            if (rc == Sqlite3.DONE) return;
            if (rc != Sqlite3.ROW) throw SqliteException.From(_db, rc, sql);
        }
    }

    /// <summary>Execute a parameterless multi-statement script (PRAGMAs, migrations, transaction control).</summary>
    private void ExecScript(string sql)
    {
        ThrowIfDisposed();
        var bytes = Utf8.ToZ(sql);
        byte* err = null;
        int rc;
        fixed (byte* p = bytes)
            rc = Sqlite3.sqlite3_exec(_db, p, IntPtr.Zero, IntPtr.Zero, &err);
        if (rc == Sqlite3.OK) return;
        var message = Utf8.FromZ(err);
        if (err != null) Sqlite3.sqlite3_free(err);
        var str = Utf8.FromZ(Sqlite3.sqlite3_errstr(rc));
        throw new SqliteException(rc, $"SQLite error {rc} ({str}): {message ?? str}", sql);
    }

    // ------------------------------------------------------------------ statement cache

    private sealed class Command(Statement[] statements)
    {
        public readonly Statement[] Statements = statements;
        public bool InUse;
        public bool Cached;
        /// <summary>When this command was last rented, so the cache can drop the coldest one rather than all of them.</summary>
        public long Used;
    }

    private long _useClock;

    private Command Rent(string sql)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);
        ThrowIfDisposed();
        if (_cache.TryGetValue(sql, out var cached))
        {
            if (!cached.InUse)
            {
                cached.InUse = true;
                cached.Used = ++_useClock;
                return cached;
            }
            // Reentrant use of the same SQL (e.g. from a row mapper): use a private, uncached copy.
            var copy = new Command(Prepare(sql)) { InUse = true };
            return copy;
        }
        var cmd = new Command(Prepare(sql)) { InUse = true, Cached = true, Used = ++_useClock };
        if (_cache.Count >= MaxCachedCommands) TrimCache();
        _cache[sql] = cmd;
        return cmd;
    }

    private void Return(Command cmd)
    {
        foreach (var st in cmd.Statements)
        {
            Sqlite3.sqlite3_reset(st.Handle);
            Sqlite3.sqlite3_clear_bindings(st.Handle);
        }
        cmd.InUse = false;
        if (!cmd.Cached) Finalize(cmd);
    }

    /// <summary>Makes room for one more statement by dropping the least recently used ones. Dropping the whole cache
    /// instead would make every new statement re-prepare all of them, so the cache would swing between full and empty.</summary>
    private void TrimCache()
    {
        foreach (var (sql, cmd) in _cache.ToList().OrderBy(e => e.Value.Used).ToList())
        {
            if (_cache.Count < MaxCachedCommands) return;
            if (cmd.InUse) continue;   // a statement being stepped right now stays; the next trim will catch it
            _cache.Remove(sql);
            Finalize(cmd);
        }
    }

    private static void Finalize(Command cmd)
    {
        foreach (var st in cmd.Statements) Sqlite3.sqlite3_finalize(st.Handle);
    }

    private Statement[] Prepare(string sql)
    {
        var bytes = Encoding.UTF8.GetBytes(sql);
        var list = new List<Statement>(1);
        fixed (byte* start = bytes)
        {
            var cur = start;
            var end = start + bytes.Length;
            while (cur < end)
            {
                IntPtr handle;
                byte* tail;
                var rc = Sqlite3.sqlite3_prepare_v2(_db, cur, (int)(end - cur), &handle, &tail);
                if (rc != Sqlite3.OK)
                {
                    var ex = SqliteException.From(_db, rc, sql);
                    foreach (var s in list) Sqlite3.sqlite3_finalize(s.Handle);
                    throw ex;
                }
                if (handle != IntPtr.Zero) list.Add(new Statement(handle));
                if (tail == null || tail <= cur) break;
                cur = tail;
            }
        }
        if (list.Count == 0) throw new ArgumentException("SQL contains no statement: " + sql);
        return [.. list];
    }

    // ------------------------------------------------------------------ binding

    private void Bind(Statement st, object? args, string sql)
    {
        var names = st.ParameterNames;
        if (names.Length == 0) return;
        if (args is null)
            throw new ArgumentException($"SQL has parameters ({string.Join(", ", names.Select(n => n ?? "?"))}) but no arguments were given\n  SQL: {sql}");

        var positional = SqlArgs.IsPositional(args);
        for (var i = 0; i < names.Length; i++)
        {
            var raw = names[i];
            object? value;
            if (raw is null || raw[0] == '?')
            {
                if (!positional) throw new ArgumentException($"Positional parameter #{i + 1} needs an array/list argument\n  SQL: {sql}");
                var index = raw is { Length: > 1 } && int.TryParse(raw.AsSpan(1), out var n) ? n - 1 : i;
                value = SqlArgs.GetPositional(args, index, sql);
            }
            else if (positional)
            {
                value = SqlArgs.GetPositional(args, i, sql);
            }
            else if (!SqlArgs.TryGet(args, raw[1..], out value))
            {
                throw new ArgumentException($"Missing SQL parameter '{raw}'\n  SQL: {sql}");
            }
            var rc = BindValue(st.Handle, i + 1, value);
            if (rc != Sqlite3.OK) throw SqliteException.From(_db, rc, sql, $"Binding parameter '{raw ?? "?"}' failed");
        }
    }

    private static int BindValue(IntPtr stmt, int index, object? value)
    {
        switch (value)
        {
            case null:
            case DBNull:
                return Sqlite3.sqlite3_bind_null(stmt, index);
            case string s:
                return BindText(stmt, index, s);
            case long l:
                return Sqlite3.sqlite3_bind_int64(stmt, index, l);
            case int i:
                return Sqlite3.sqlite3_bind_int64(stmt, index, i);
            case bool b:
                return Sqlite3.sqlite3_bind_int64(stmt, index, b ? 1 : 0);
            case double d:
                return Sqlite3.sqlite3_bind_double(stmt, index, d);
            case float f:
                return Sqlite3.sqlite3_bind_double(stmt, index, f);
            case decimal m:
                return Sqlite3.sqlite3_bind_double(stmt, index, (double)m);
            case short sh:
                return Sqlite3.sqlite3_bind_int64(stmt, index, sh);
            case byte by:
                return Sqlite3.sqlite3_bind_int64(stmt, index, by);
            case sbyte sb:
                return Sqlite3.sqlite3_bind_int64(stmt, index, sb);
            case ushort us:
                return Sqlite3.sqlite3_bind_int64(stmt, index, us);
            case uint ui:
                return Sqlite3.sqlite3_bind_int64(stmt, index, ui);
            case ulong ul:
                return Sqlite3.sqlite3_bind_int64(stmt, index, checked((long)ul));
            case Enum e:
                return Sqlite3.sqlite3_bind_int64(stmt, index, Convert.ToInt64(e, CultureInfo.InvariantCulture));
            case DateTimeOffset dto:
                return Sqlite3.sqlite3_bind_int64(stmt, index, dto.ToUnixTimeMilliseconds());
            case DateTime dt:
                var utc = dt.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(dt, DateTimeKind.Utc) : dt;
                return Sqlite3.sqlite3_bind_int64(stmt, index, new DateTimeOffset(utc).ToUnixTimeMilliseconds());
            case TimeSpan ts:
                return Sqlite3.sqlite3_bind_int64(stmt, index, (long)ts.TotalMilliseconds);
            case Guid g:
                return BindText(stmt, index, g.ToString());
            case char c:
                return BindText(stmt, index, c.ToString());
            case byte[] bytes:
                return BindBlob(stmt, index, bytes);
            case ReadOnlyMemory<byte> rom:
                return BindBlob(stmt, index, rom.Span);
            case Memory<byte> mem:
                return BindBlob(stmt, index, mem.Span);
            case JsonElement je:
                return je.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
                    ? Sqlite3.sqlite3_bind_null(stmt, index)
                    : BindText(stmt, index, je.GetRawText());
            case JsonNode jn:
                return BindText(stmt, index, jn.ToJsonString());
            default:
                // Anything else (lists, records...) is stored as JSON text.
                return BindText(stmt, index, NetPiJson.Serialize(value));
        }
    }

    private static int BindText(IntPtr stmt, int index, string s)
    {
        var max = Encoding.UTF8.GetMaxByteCount(s.Length);
        byte[]? rented = null;
        Span<byte> buffer = max <= 1024 ? stackalloc byte[max] : (rented = ArrayPool<byte>.Shared.Rent(max));
        try
        {
            var n = Encoding.UTF8.GetBytes(s, buffer);
            fixed (byte* p = buffer)
                return Sqlite3.sqlite3_bind_text(stmt, index, p, n, Sqlite3.TRANSIENT);
        }
        finally
        {
            if (rented is not null) ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static int BindBlob(IntPtr stmt, int index, ReadOnlySpan<byte> data)
    {
        // A zero-length blob must still be a blob (not NULL): pass a valid pointer.
        byte dummy = 0;
        fixed (byte* p = data)
            return Sqlite3.sqlite3_bind_blob(stmt, index, data.Length == 0 ? &dummy : p, data.Length, Sqlite3.TRANSIENT);
    }

    // ------------------------------------------------------------------ rows

    private sealed class Statement
    {
        public readonly IntPtr Handle;
        /// <summary>Raw parameter names including their prefix (@x, :x, $x, ?1) or null for anonymous '?'.</summary>
        public readonly string?[] ParameterNames;
        public string[]? ColumnNames;
        public Dictionary<string, int>? Ordinals;

        public Statement(IntPtr handle)
        {
            Handle = handle;
            var count = Sqlite3.sqlite3_bind_parameter_count(handle);
            ParameterNames = new string?[count];
            for (var i = 0; i < count; i++) ParameterNames[i] = Utf8.FromZ(Sqlite3.sqlite3_bind_parameter_name(handle, i + 1));
        }

        public void EnsureColumns()
        {
            var count = Sqlite3.sqlite3_column_count(Handle);
            if (ColumnNames is not null && ColumnNames.Length == count) return;
            ColumnNames = new string[count];
            Ordinals = new Dictionary<string, int>(count, StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < count; i++)
            {
                ColumnNames[i] = Utf8.FromZ(Sqlite3.sqlite3_column_name(Handle, i)) ?? "";
                Ordinals.TryAdd(ColumnNames[i], i);
            }
        }
    }

    /// <summary>Steps a query and copies each row's values out of SQLite, so nothing points into the statement
    /// afterwards. <paramref name="firstRowOnly"/> stops after the first row, which is all a single-row query needs.</summary>
    private List<RowSnapshot> Snapshots(string sql, object? args, bool firstRowOnly = false)
    {
        var list = new List<RowSnapshot>();
        Run(sql, args, row =>
        {
            var st = row.Statement;
            st.EnsureColumns();
            var values = new object?[st.ColumnNames!.Length];
            for (var i = 0; i < values.Length; i++) values[i] = row.GetValue(i);
            list.Add(new RowSnapshot(st.ColumnNames!, st.Ordinals!, values, sql));
            return !firstRowOnly;
        });
        return list;
    }

    /// <summary>A row's values, copied: strings and blobs are already fresh, so it stays valid after the lock is gone.</summary>
    private sealed class RowSnapshot(string[] names, Dictionary<string, int> ordinals, object?[] values, string sql) : IDbRow
    {
        public int FieldCount => values.Length;

        public string GetName(int ordinal) => names[ordinal];

        public int Ordinal(string name) => ordinals.TryGetValue(name, out var i)
            ? i
            : throw new IndexOutOfRangeException($"Column '{name}' is not in the result of: {sql}");

        public bool IsNull(string name) => values[Ordinal(name)] is null;
        public long GetInt64(string name) => ToInt64(values[Ordinal(name)]);
        public double GetDouble(string name) => ToDouble(values[Ordinal(name)]);
        public string GetString(string name) => values[Ordinal(name)] is null ? "" : Text(values[Ordinal(name)]);
        public string? GetStringOrNull(string name) => values[Ordinal(name)] is { } v ? Text(v) : null;

        public long? GetInt64OrNull(string name) => values[Ordinal(name)] is null ? null : ToInt64(values[Ordinal(name)]);

        public byte[]? GetBlob(string name) => values[Ordinal(name)] switch { null => null, byte[] b => b, string s => System.Text.Encoding.UTF8.GetBytes(s), var o => System.Text.Encoding.UTF8.GetBytes(Text(o)) };

        public object? GetValue(int ordinal) => values[ordinal];

        // SQLite hands out text, so a number read back from a column must be formatted the way SQLite would: the
        // invariant culture, never the caller's (a comma decimal separator would round-trip as "2,5").
        private static long ToInt64(object? v) => v switch { long l => l, double d => (long)d, byte[] b => BitConverter.ToInt64(b), _ => Convert.ToInt64(v ?? 0L, CultureInfo.InvariantCulture) };

        private static double ToDouble(object? v) => v switch { long l => l, double d => d, _ => Convert.ToDouble(v ?? 0d, CultureInfo.InvariantCulture) };

        private static string Text(object? v) => v switch
        {
            null => "", string s => s, byte[] b => System.Text.Encoding.UTF8.GetString(b),
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => v.ToString() ?? "",
        };
    }

    private sealed class Row(Statement st, string sql) : IDbRow
    {
        /// <summary>The statement this row belongs to: its column names and ordinals, for a snapshot.</summary>
        internal Statement Statement => st;

        private readonly IntPtr _h = st.Handle;

        public int FieldCount => Sqlite3.sqlite3_column_count(_h);

        public string GetName(int ordinal)
        {
            st.EnsureColumns();
            return st.ColumnNames![ordinal];
        }

        public int Ordinal(string name)
        {
            st.EnsureColumns();
            return st.Ordinals!.TryGetValue(name, out var i)
                ? i
                : throw new IndexOutOfRangeException($"Column '{name}' is not in the result of: {sql}");
        }

        public bool IsNull(string name) => Sqlite3.sqlite3_column_type(_h, Ordinal(name)) == Sqlite3.NULL;
        public long GetInt64(string name) => Sqlite3.sqlite3_column_int64(_h, Ordinal(name));

        public long? GetInt64OrNull(string name)
        {
            var i = Ordinal(name);
            return Sqlite3.sqlite3_column_type(_h, i) == Sqlite3.NULL ? null : Sqlite3.sqlite3_column_int64(_h, i);
        }

        public double GetDouble(string name) => Sqlite3.sqlite3_column_double(_h, Ordinal(name));
        public string GetString(string name) => Text(Ordinal(name)) ?? "";
        public string? GetStringOrNull(string name) => Text(Ordinal(name));

        public byte[]? GetBlob(string name)
        {
            var i = Ordinal(name);
            return Sqlite3.sqlite3_column_type(_h, i) == Sqlite3.NULL ? null : Blob(i);
        }

        public object? GetValue(int ordinal) => Sqlite3.sqlite3_column_type(_h, ordinal) switch
        {
            Sqlite3.INTEGER => Sqlite3.sqlite3_column_int64(_h, ordinal),
            Sqlite3.FLOAT => Sqlite3.sqlite3_column_double(_h, ordinal),
            Sqlite3.TEXT => Text(ordinal),
            Sqlite3.BLOB => Blob(ordinal),
            _ => null,
        };

        private string? Text(int i)
        {
            if (Sqlite3.sqlite3_column_type(_h, i) == Sqlite3.NULL) return null;
            var p = Sqlite3.sqlite3_column_text(_h, i); // must precede column_bytes
            return Utf8.From(p, Sqlite3.sqlite3_column_bytes(_h, i));
        }

        private byte[] Blob(int i)
        {
            var p = Sqlite3.sqlite3_column_blob(_h, i);
            var n = Sqlite3.sqlite3_column_bytes(_h, i);
            if (p == null || n == 0) return [];
            return new ReadOnlySpan<byte>(p, n).ToArray();
        }
    }

    // ------------------------------------------------------------------ lifetime

    private void ThrowIfDisposed()
    {
        if (_db == IntPtr.Zero) throw new ObjectDisposedException(nameof(Database));
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_db == IntPtr.Zero) return;
            try { ExecScript("PRAGMA optimize"); } catch { }
            foreach (var cmd in _cache.Values) Finalize(cmd);
            _cache.Clear();
            Sqlite3.sqlite3_close_v2(_db);
            _db = IntPtr.Zero;
        }
    }
}
