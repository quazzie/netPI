using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace NetPI.Agent.Tests;

/// <summary>
/// Minimal <see cref="IDatabase"/> over the system's native SQLite (P/Invoke), to exercise the plugins' real SQL in tests.
/// <see cref="TryCreate"/> returns null when no SQLite library is available.
/// </summary>
public sealed unsafe class TestSqlite : IDatabase, IDisposable
{
    private const int SQLITE_OK = 0, SQLITE_ROW = 100, SQLITE_DONE = 101;
    private const int SQLITE_INTEGER = 1, SQLITE_FLOAT = 2, SQLITE_TEXT = 3, SQLITE_BLOB = 4, SQLITE_NULL = 5;
    private static readonly IntPtr Transient = new(-1);

    private static IntPtr _lib;
    private static delegate* unmanaged[Cdecl]<byte*, IntPtr*, int, IntPtr, int> _open;
    private static delegate* unmanaged[Cdecl]<IntPtr, int> _close;
    private static delegate* unmanaged[Cdecl]<IntPtr, byte*, int, IntPtr*, byte**, int> _prepare;
    private static delegate* unmanaged[Cdecl]<IntPtr, int> _step;
    private static delegate* unmanaged[Cdecl]<IntPtr, int> _finalize;
    private static delegate* unmanaged[Cdecl]<IntPtr, byte*, int> _paramIndex;
    private static delegate* unmanaged[Cdecl]<IntPtr, int, byte*, int, IntPtr, int> _bindText;
    private static delegate* unmanaged[Cdecl]<IntPtr, int, long, int> _bindInt64;
    private static delegate* unmanaged[Cdecl]<IntPtr, int, double, int> _bindDouble;
    private static delegate* unmanaged[Cdecl]<IntPtr, int, int> _bindNull;
    private static delegate* unmanaged[Cdecl]<IntPtr, int> _columnCount;
    private static delegate* unmanaged[Cdecl]<IntPtr, int, byte*> _columnName;
    private static delegate* unmanaged[Cdecl]<IntPtr, int, int> _columnType;
    private static delegate* unmanaged[Cdecl]<IntPtr, int, long> _columnInt64;
    private static delegate* unmanaged[Cdecl]<IntPtr, int, double> _columnDouble;
    private static delegate* unmanaged[Cdecl]<IntPtr, int, byte*> _columnText;
    private static delegate* unmanaged[Cdecl]<IntPtr, int, int> _columnBytes;
    private static delegate* unmanaged[Cdecl]<IntPtr, byte*> _errmsg;
    private static delegate* unmanaged[Cdecl]<IntPtr, int> _changes;
    private static delegate* unmanaged[Cdecl]<IntPtr, long> _lastRowId;

    private readonly IntPtr _db;
    private readonly Lock _gate = new();

    private TestSqlite(IntPtr db) => _db = db;

    public int Statements;

    private static bool Load()
    {
        if (_lib != IntPtr.Zero) return true;
        foreach (var name in new[] { "libsqlite3.so.0", "libsqlite3.so", "libsqlite3.dylib", "winsqlite3", "sqlite3", "e_sqlite3" })
        {
            if (!NativeLibrary.TryLoad(name, Assembly.GetExecutingAssembly(), null, out var lib)) continue;
            try
            {
                IntPtr F(string n) => NativeLibrary.GetExport(lib, n);
                _open = (delegate* unmanaged[Cdecl]<byte*, IntPtr*, int, IntPtr, int>)F("sqlite3_open_v2");
                _close = (delegate* unmanaged[Cdecl]<IntPtr, int>)F("sqlite3_close_v2");
                _prepare = (delegate* unmanaged[Cdecl]<IntPtr, byte*, int, IntPtr*, byte**, int>)F("sqlite3_prepare_v2");
                _step = (delegate* unmanaged[Cdecl]<IntPtr, int>)F("sqlite3_step");
                _finalize = (delegate* unmanaged[Cdecl]<IntPtr, int>)F("sqlite3_finalize");
                _paramIndex = (delegate* unmanaged[Cdecl]<IntPtr, byte*, int>)F("sqlite3_bind_parameter_index");
                _bindText = (delegate* unmanaged[Cdecl]<IntPtr, int, byte*, int, IntPtr, int>)F("sqlite3_bind_text");
                _bindInt64 = (delegate* unmanaged[Cdecl]<IntPtr, int, long, int>)F("sqlite3_bind_int64");
                _bindDouble = (delegate* unmanaged[Cdecl]<IntPtr, int, double, int>)F("sqlite3_bind_double");
                _bindNull = (delegate* unmanaged[Cdecl]<IntPtr, int, int>)F("sqlite3_bind_null");
                _columnCount = (delegate* unmanaged[Cdecl]<IntPtr, int>)F("sqlite3_column_count");
                _columnName = (delegate* unmanaged[Cdecl]<IntPtr, int, byte*>)F("sqlite3_column_name");
                _columnType = (delegate* unmanaged[Cdecl]<IntPtr, int, int>)F("sqlite3_column_type");
                _columnInt64 = (delegate* unmanaged[Cdecl]<IntPtr, int, long>)F("sqlite3_column_int64");
                _columnDouble = (delegate* unmanaged[Cdecl]<IntPtr, int, double>)F("sqlite3_column_double");
                _columnText = (delegate* unmanaged[Cdecl]<IntPtr, int, byte*>)F("sqlite3_column_text");
                _columnBytes = (delegate* unmanaged[Cdecl]<IntPtr, int, int>)F("sqlite3_column_bytes");
                _errmsg = (delegate* unmanaged[Cdecl]<IntPtr, byte*>)F("sqlite3_errmsg");
                _changes = (delegate* unmanaged[Cdecl]<IntPtr, int>)F("sqlite3_changes");
                _lastRowId = (delegate* unmanaged[Cdecl]<IntPtr, long>)F("sqlite3_last_insert_rowid");
                _lib = lib;
                return true;
            }
            catch (EntryPointNotFoundException) { NativeLibrary.Free(lib); }
        }
        return false;
    }

    /// <summary>Open a database file (or ":memory:"); null when SQLite is unavailable.</summary>
    public static TestSqlite? TryCreate(string path = ":memory:")
    {
        if (!Load()) return null;
        var bytes = Encoding.UTF8.GetBytes(path + "\0");
        IntPtr db;
        fixed (byte* p = bytes)
        {
            // READWRITE | CREATE | FULLMUTEX
            if (_open(p, &db, 0x2 | 0x4 | 0x10000, IntPtr.Zero) != SQLITE_OK) return null;
        }
        return new TestSqlite(db);
    }

    private string Error() => Marshal.PtrToStringUTF8((IntPtr)_errmsg(_db)) ?? "sqlite error";

    private static Dictionary<string, object?> Params(object? args)
    {
        var d = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        switch (args)
        {
            case null: break;
            case IDictionary<string, object?> dict:
                foreach (var (k, v) in dict) d[k] = v;
                break;
            default:
                foreach (var p in args.GetType().GetProperties()) d[p.Name] = p.GetValue(args);
                break;
        }
        return d;
    }

    /// <summary>Run every statement in <paramref name="sql"/>; the row callback sees rows of each statement.</summary>
    private void Run(string sql, object? args, Action<IntPtr>? onRow)
    {
        var ps = Params(args);
        var bytes = Encoding.UTF8.GetBytes(sql + "\0");
        lock (_gate)
        {
            fixed (byte* start = bytes)
            {
                var cur = start;
                var end = start + bytes.Length - 1;
                while (cur < end)
                {
                    IntPtr stmt;
                    byte* tail;
                    if (_prepare(_db, cur, -1, &stmt, &tail) != SQLITE_OK) throw new InvalidOperationException($"SQL error: {Error()}\n{sql}");
                    cur = tail;
                    if (stmt == IntPtr.Zero) continue; // whitespace / comment
                    Interlocked.Increment(ref Statements);
                    try
                    {
                        foreach (var (name, value) in ps)
                        {
                            foreach (var prefix in "@:$")
                            {
                                var nb = Encoding.UTF8.GetBytes(prefix + name + "\0");
                                int idx;
                                fixed (byte* n = nb) idx = _paramIndex(stmt, n);
                                if (idx > 0) Bind(stmt, idx, value);
                            }
                        }
                        while (true)
                        {
                            var rc = _step(stmt);
                            if (rc == SQLITE_ROW) { onRow?.Invoke(stmt); continue; }
                            if (rc == SQLITE_DONE) break;
                            throw new InvalidOperationException($"SQL error: {Error()}\n{sql}");
                        }
                    }
                    finally
                    {
                        _finalize(stmt);
                    }
                }
            }
        }
    }

    private static void Bind(IntPtr stmt, int idx, object? value)
    {
        switch (value)
        {
            case null: _bindNull(stmt, idx); break;
            case bool b: _bindInt64(stmt, idx, b ? 1 : 0); break;
            case int i: _bindInt64(stmt, idx, i); break;
            case long l: _bindInt64(stmt, idx, l); break;
            case double d: _bindDouble(stmt, idx, d); break;
            case float f: _bindDouble(stmt, idx, f); break;
            case DateTimeOffset dto: BindText(stmt, idx, dto.ToString("O")); break;
            default: BindText(stmt, idx, value.ToString() ?? ""); break;
        }
    }

    private static void BindText(IntPtr stmt, int idx, string s)
    {
        var b = Encoding.UTF8.GetBytes(s);
        fixed (byte* p = b) _bindText(stmt, idx, p, b.Length, Transient);
    }

    private sealed class Row(IntPtr stmt) : IDbRow
    {
        private Dictionary<string, int>? _names;
        public int FieldCount => _columnCount(stmt);
        public string GetName(int ordinal) => Marshal.PtrToStringUTF8((IntPtr)_columnName(stmt, ordinal)) ?? "";

        public int Ordinal(string name)
        {
            if (_names is null)
            {
                _names = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < FieldCount; i++) _names[GetName(i)] = i;
            }
            return _names.TryGetValue(name, out var o) ? o : throw new KeyNotFoundException($"No column {name}");
        }

        public bool IsNull(string name) => _columnType(stmt, Ordinal(name)) == SQLITE_NULL;
        public long GetInt64(string name) => _columnInt64(stmt, Ordinal(name));
        public long? GetInt64OrNull(string name) => IsNull(name) ? null : GetInt64(name);
        public double GetDouble(string name) => _columnDouble(stmt, Ordinal(name));
        public string GetString(string name) => GetStringOrNull(name) ?? throw new InvalidOperationException($"Column {name} is null");

        public string? GetStringOrNull(string name)
        {
            var i = Ordinal(name);
            if (_columnType(stmt, i) == SQLITE_NULL) return null;
            var p = _columnText(stmt, i);
            var n = _columnBytes(stmt, i);
            return Encoding.UTF8.GetString(p, n);
        }

        public byte[]? GetBlob(string name) => throw new NotSupportedException();

        public object? GetValue(int ordinal) => _columnType(stmt, ordinal) switch
        {
            SQLITE_INTEGER => _columnInt64(stmt, ordinal),
            SQLITE_FLOAT => _columnDouble(stmt, ordinal),
            SQLITE_TEXT => Encoding.UTF8.GetString(_columnText(stmt, ordinal), _columnBytes(stmt, ordinal)),
            SQLITE_BLOB => null,
            _ => null,
        };
    }

    public int Execute(string sql, object? args = null)
    {
        lock (_gate)
        {
            Run(sql, args, null);
            return _changes(_db);
        }
    }

    public long Insert(string sql, object? args = null)
    {
        lock (_gate)
        {
            Run(sql, args, null);
            return _lastRowId(_db);
        }
    }

    public T? Scalar<T>(string sql, object? args = null)
    {
        object? value = null;
        var got = false;
        Run(sql, args, stmt =>
        {
            if (got) return;
            got = true;
            value = new Row(stmt).GetValue(0);
        });
        if (value is null) return default;
        return (T)Convert.ChangeType(value, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T));
    }

    public List<T> Query<T>(string sql, object? args, Func<IDbRow, T> map)
    {
        var list = new List<T>();
        Run(sql, args, stmt => list.Add(map(new Row(stmt))));
        return list;
    }

    public T? QuerySingle<T>(string sql, object? args, Func<IDbRow, T> map)
    {
        var list = Query(sql, args, map);
        return list.Count > 0 ? list[0] : default;
    }

    public T Transaction<T>(Func<IDatabase, T> work)
    {
        lock (_gate)
        {
            Run("BEGIN", null, null);
            try
            {
                var r = work(this);
                Run("COMMIT", null, null);
                return r;
            }
            catch
            {
                Run("ROLLBACK", null, null);
                throw;
            }
        }
    }

    public void Transaction(Action<IDatabase> work) => Transaction<int>(db => { work(db); return 0; });

    public void Migrate(string scope, params string[] migrations)
    {
        lock (_gate)
        {
            Run("CREATE TABLE IF NOT EXISTS _migrations (scope TEXT PRIMARY KEY, version INTEGER NOT NULL)", null, null);
            var version = (int)(Scalar<long?>("SELECT version FROM _migrations WHERE scope = @scope", new Dictionary<string, object?> { ["scope"] = scope }) ?? 0);
            for (var i = version; i < migrations.Length; i++)
            {
                Run(migrations[i], null, null);
                Run("INSERT INTO _migrations (scope, version) VALUES (@scope, @v) ON CONFLICT(scope) DO UPDATE SET version = excluded.version",
                    new Dictionary<string, object?> { ["scope"] = scope, ["v"] = i + 1 }, null);
            }
        }
    }

    public void Dispose()
    {
        if (_db != IntPtr.Zero) _close(_db);
    }
}
