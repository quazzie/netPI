using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace NetPI.Host.Storage.Sqlite;

/// <summary>
/// Minimal blittable P/Invoke surface of the SQLite C API. The logical library name <c>sqlite3</c> is mapped by a
/// resolver to, in order: <c>NETPI_SQLITE</c> env / <see cref="ConfiguredPath"/>, <c>winsqlite3.dll</c> (Windows,
/// System32), <c>libsqlite3.so.0</c>, <c>libsqlite3.so</c>, <c>libsqlite3.dylib</c>.
/// All strings cross the boundary as UTF-8 byte pointers (no runtime string marshalling).
/// </summary>
internal static unsafe class Sqlite3
{
    private const string Lib = "sqlite3";

    public const int OK = 0;
    public const int ROW = 100;
    public const int DONE = 101;

    public const int INTEGER = 1;
    public const int FLOAT = 2;
    public const int TEXT = 3;
    public const int BLOB = 4;
    public const int NULL = 5;

    public const int OPEN_READWRITE = 0x00000002;
    public const int OPEN_CREATE = 0x00000004;
    public const int OPEN_NOMUTEX = 0x00008000;

    /// <summary>SQLITE_TRANSIENT: SQLite copies bound text/blob data immediately.</summary>
    public static readonly IntPtr TRANSIENT = new(-1);

    private static readonly Lock InitLock = new();
    private static bool _resolverSet;
    private static IntPtr _handle;

    /// <summary>Optional explicit library path (setting <c>database.sqlitePath</c>). Must be set before the first open.</summary>
    public static string? ConfiguredPath { get; set; }

    /// <summary>The candidate that was loaded (for diagnostics).</summary>
    public static string? LoadedFrom { get; private set; }

    /// <summary>Install the resolver and load the native library; throws a descriptive <see cref="DllNotFoundException"/>.</summary>
    public static void EnsureLoaded()
    {
        lock (InitLock)
        {
            if (!_resolverSet)
            {
                NativeLibrary.SetDllImportResolver(typeof(Sqlite3).Assembly, Resolve);
                _resolverSet = true;
            }
            if (_handle != IntPtr.Zero) return;
            if (Load() == IntPtr.Zero)
                throw new DllNotFoundException(
                    "SQLite native library not found. Tried: " + string.Join(", ", Candidates()) +
                    ". Set NETPI_SQLITE (or settings 'database.sqlitePath') to the full path of a sqlite3 library.");
        }
    }

    public static string Version => Utf8.FromZ(sqlite3_libversion()) ?? "?";

    private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (name != Lib) return IntPtr.Zero;
        lock (InitLock) return _handle != IntPtr.Zero ? _handle : Load();
    }

    private static IntPtr Load()
    {
        var asm = typeof(Sqlite3).Assembly;
        foreach (var candidate in Candidates())
        {
            IntPtr h;
            var ok = Path.IsPathRooted(candidate)
                ? NativeLibrary.TryLoad(candidate, out h)
                : NativeLibrary.TryLoad(candidate, asm,
                    OperatingSystem.IsWindows() ? DllImportSearchPath.System32 | DllImportSearchPath.AssemblyDirectory : null, out h);
            if (!ok) continue;
            // A usable library must export the API we need (guards against a random file named like sqlite).
            if (!NativeLibrary.TryGetExport(h, "sqlite3_prepare_v2", out _)) { NativeLibrary.Free(h); continue; }
            _handle = h;
            LoadedFrom = candidate;
            return h;
        }
        return IntPtr.Zero;
    }

    private static IEnumerable<string> Candidates()
    {
        var env = Environment.GetEnvironmentVariable("NETPI_SQLITE");
        if (!string.IsNullOrWhiteSpace(env)) yield return env.Trim();
        if (!string.IsNullOrWhiteSpace(ConfiguredPath)) yield return ConfiguredPath.Trim();
        if (OperatingSystem.IsWindows())
        {
            yield return "winsqlite3.dll";
            yield return "sqlite3.dll";
            yield return "e_sqlite3.dll";
            yield break;
        }
        yield return "libsqlite3.so.0";
        yield return "libsqlite3.so";
        yield return "libsqlite3.dylib";
        if (OperatingSystem.IsMacOS()) yield return "/usr/lib/libsqlite3.dylib";
    }

    // ------------------------------------------------------------------ API

    [DllImport(Lib, ExactSpelling = true)] public static extern int sqlite3_open_v2(byte* filename, IntPtr* db, int flags, byte* vfs);
    [DllImport(Lib, ExactSpelling = true)] public static extern int sqlite3_close_v2(IntPtr db);
    [DllImport(Lib, ExactSpelling = true)] public static extern int sqlite3_prepare_v2(IntPtr db, byte* sql, int nByte, IntPtr* stmt, byte** tail);
    [DllImport(Lib, ExactSpelling = true)] public static extern int sqlite3_step(IntPtr stmt);
    [DllImport(Lib, ExactSpelling = true)] public static extern int sqlite3_reset(IntPtr stmt);
    [DllImport(Lib, ExactSpelling = true)] public static extern int sqlite3_clear_bindings(IntPtr stmt);
    [DllImport(Lib, ExactSpelling = true)] public static extern int sqlite3_finalize(IntPtr stmt);

    [DllImport(Lib, ExactSpelling = true), SuppressGCTransition] public static extern int sqlite3_bind_int64(IntPtr stmt, int index, long value);
    [DllImport(Lib, ExactSpelling = true), SuppressGCTransition] public static extern int sqlite3_bind_double(IntPtr stmt, int index, double value);
    [DllImport(Lib, ExactSpelling = true), SuppressGCTransition] public static extern int sqlite3_bind_null(IntPtr stmt, int index);
    [DllImport(Lib, ExactSpelling = true)] public static extern int sqlite3_bind_text(IntPtr stmt, int index, byte* text, int n, IntPtr destructor);
    [DllImport(Lib, ExactSpelling = true)] public static extern int sqlite3_bind_blob(IntPtr stmt, int index, void* data, int n, IntPtr destructor);
    [DllImport(Lib, ExactSpelling = true)] public static extern int sqlite3_bind_parameter_count(IntPtr stmt);
    [DllImport(Lib, ExactSpelling = true)] public static extern byte* sqlite3_bind_parameter_name(IntPtr stmt, int index);

    [DllImport(Lib, ExactSpelling = true), SuppressGCTransition] public static extern int sqlite3_column_count(IntPtr stmt);
    [DllImport(Lib, ExactSpelling = true)] public static extern byte* sqlite3_column_name(IntPtr stmt, int index);
    [DllImport(Lib, ExactSpelling = true), SuppressGCTransition] public static extern int sqlite3_column_type(IntPtr stmt, int index);
    [DllImport(Lib, ExactSpelling = true), SuppressGCTransition] public static extern long sqlite3_column_int64(IntPtr stmt, int index);
    [DllImport(Lib, ExactSpelling = true), SuppressGCTransition] public static extern double sqlite3_column_double(IntPtr stmt, int index);
    [DllImport(Lib, ExactSpelling = true)] public static extern byte* sqlite3_column_text(IntPtr stmt, int index);
    [DllImport(Lib, ExactSpelling = true)] public static extern void* sqlite3_column_blob(IntPtr stmt, int index);
    [DllImport(Lib, ExactSpelling = true), SuppressGCTransition] public static extern int sqlite3_column_bytes(IntPtr stmt, int index);

    [DllImport(Lib, ExactSpelling = true)] public static extern byte* sqlite3_errmsg(IntPtr db);
    [DllImport(Lib, ExactSpelling = true)] public static extern byte* sqlite3_errstr(int code);
    [DllImport(Lib, ExactSpelling = true)] public static extern int sqlite3_changes(IntPtr db);
    [DllImport(Lib, ExactSpelling = true)] public static extern long sqlite3_last_insert_rowid(IntPtr db);
    [DllImport(Lib, ExactSpelling = true)] public static extern int sqlite3_busy_timeout(IntPtr db, int ms);
    [DllImport(Lib, ExactSpelling = true)] public static extern int sqlite3_exec(IntPtr db, byte* sql, IntPtr callback, IntPtr arg, byte** errmsg);
    [DllImport(Lib, ExactSpelling = true)] public static extern void sqlite3_free(void* p);
    [DllImport(Lib, ExactSpelling = true)] public static extern byte* sqlite3_libversion();
}

/// <summary>UTF-8 helpers for native strings.</summary>
internal static unsafe class Utf8
{
    public static string? FromZ(byte* p) =>
        p == null ? null : Encoding.UTF8.GetString(MemoryMarshal.CreateReadOnlySpanFromNullTerminated(p));

    public static string From(byte* p, int length) =>
        p == null || length <= 0 ? "" : Encoding.UTF8.GetString(p, length);

    /// <summary>Null-terminated UTF-8 copy of <paramref name="s"/>.</summary>
    public static byte[] ToZ(string s)
    {
        var bytes = new byte[Encoding.UTF8.GetByteCount(s) + 1];
        Encoding.UTF8.GetBytes(s, bytes);
        return bytes;
    }
}

/// <summary>A SQLite error with the failing SQL.</summary>
public sealed class SqliteException(int code, string message, string? sql)
    : Exception(sql is null ? message : $"{message}\n  SQL: {Shorten(sql)}")
{
    public int Code { get; } = code;
    /// <summary>Primary result code (e.g. 19 = constraint).</summary>
    public int PrimaryCode => Code & 0xFF;
    public string? Sql { get; } = sql;

    private static string Shorten(string sql)
    {
        var s = sql.Trim();
        return s.Length > 600 ? s[..600] + "…" : s;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static unsafe SqliteException From(IntPtr db, int rc, string? sql, string? context = null)
    {
        var err = db != IntPtr.Zero ? Utf8.FromZ(Sqlite3.sqlite3_errmsg(db)) : null;
        var str = Utf8.FromZ(Sqlite3.sqlite3_errstr(rc));
        var msg = $"SQLite error {rc} ({str}): {err ?? str}";
        if (context is not null) msg = context + ": " + msg;
        return new SqliteException(rc, msg, sql);
    }
}
