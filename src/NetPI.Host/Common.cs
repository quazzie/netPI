using System.Reflection;
using System.Text.Json;

namespace NetPI.Host;

/// <summary>Idempotent, thread-safe disposable returned by every registry (double-dispose is a no-op).</summary>
internal sealed class Registration(Action onDispose) : IDisposable
{
    private Action? _onDispose = onDispose;

    public bool IsDisposed => Volatile.Read(ref _onDispose) is null;

    public void Dispose() => Interlocked.Exchange(ref _onDispose, null)?.Invoke();
}

/// <summary>Coalesces bursts of triggers into one callback after a quiet period.</summary>
internal sealed class Debouncer(TimeSpan delay, Action action) : IDisposable
{
    private readonly Lock _gate = new();
    private Timer? _timer;
    private bool _disposed;

    public void Trigger()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _timer ??= new Timer(static s => ((Debouncer)s!).Fire(), this, Timeout.Infinite, Timeout.Infinite);
            _timer.Change(delay, Timeout.InfiniteTimeSpan);
        }
    }

    private void Fire()
    {
        lock (_gate) if (_disposed) return;
        try { action(); }
        catch { /* callbacks log their own errors */ }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _timer?.Dispose();
            _timer = null;
        }
    }
}

internal static class HostInfo
{
    public static readonly string Version = ReadVersion();

    private static string ReadVersion()
    {
        var asm = typeof(HostInfo).Assembly;
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrEmpty(info))
        {
            var plus = info.IndexOf('+');
            return plus > 0 ? info[..plus] : info;
        }
        return asm.GetName().Version?.ToString(3) ?? "0.0.0";
    }

    public static string OsName =>
        OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : OperatingSystem.IsLinux() ? "linux" : "other";
}

internal static class PathUtil
{
    public static readonly StringComparison Comparison =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static readonly StringComparer Comparer =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>Expand ~ and environment variables, then make absolute.</summary>
    public static string Expand(string path, string? baseDir = null)
    {
        var p = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
        if (p == "~" || p.StartsWith("~/", StringComparison.Ordinal) || p.StartsWith("~\\", StringComparison.Ordinal))
            p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), p.Length > 2 ? p[2..] : "");
        if (!Path.IsPathRooted(p) && baseDir is not null) p = Path.Combine(baseDir, p);
        return Normalize(p);
    }

    public static string Normalize(string path)
    {
        var full = Path.GetFullPath(path);
        var trimmed = Path.TrimEndingDirectorySeparator(full);
        return trimmed.Length == 0 ? full : trimmed;
    }

    /// <summary>
    /// A file that holds secrets (the server token, the API keys in settings.json), open for writing: on Unix it is
    /// created owner-only from the moment of creation — a file made with the umask is readable by every other local
    /// user for as long as it exists, and a moved token file keeps the mode of the file that held it. On Windows there
    /// is no such mode.
    /// </summary>
    public static FileStream CreateOwnerOnly(string path)
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None,
            BufferSize = 4096, Options = FileOptions.None,
        };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return new FileStream(path, options);
    }

    /// <summary>
    /// On Unix, the home is owner-only: it holds the server token, the API keys and the database. On Windows there is no
    /// such mode. Best effort: a home the user can write to but does not own (a group-shared directory, a mounted
    /// volume) refuses the change, and refusing to start over it would be worse; the files in it are created owner-only
    /// regardless (<see cref="CreateOwnerOnly"/>). Returns whether the home is now owner-only.
    /// </summary>
    public static bool OwnerOnly(string path)
    {
        if (OperatingSystem.IsWindows()) return true;
        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException) { return false; }
    }

    /// <summary>Combine <paramref name="root"/> with a relative URL path, refusing anything that escapes the root.</summary>
    public static string? SafeCombine(string root, string relative)
    {
        if (relative.Length == 0) return null;
        foreach (var seg in relative.Split('/', '\\'))
        {
            if (seg is ".." or "." ) return null;
            if (OperatingSystem.IsWindows() && seg.Contains(':')) return null;
        }
        var rootFull = Path.GetFullPath(root);
        var full = Path.GetFullPath(Path.Combine(rootFull, relative.Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar)));
        var prefix = rootFull.EndsWith(Path.DirectorySeparatorChar) ? rootFull : rootFull + Path.DirectorySeparatorChar;
        return full.StartsWith(prefix, Comparison) ? full : null;
    }

    public static bool TryDeleteDirectory(string? dir)
    {
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return true;
        try
        {
            Directory.Delete(dir, recursive: true);
            return true;
        }
        catch
        {
            return false;
        }
    }
}

internal static class JsonUtil
{
    /// <summary>Unwrap exceptions that only wrap the real failure.</summary>
    public static Exception Unwrap(Exception ex)
    {
        while (true)
        {
            switch (ex)
            {
                case AggregateException { InnerExceptions.Count: 1 } ae:
                    ex = ae.InnerExceptions[0];
                    continue;
                case TargetInvocationException { InnerException: { } tieInner }:
                    ex = tieInner;
                    continue;
                default:
                    return ex;
            }
        }
    }

    public static long? Int64(this RpcRequest req, string name) =>
        req.Prop(name) switch
        {
            { ValueKind: JsonValueKind.Number } v when v.TryGetInt64(out var l) => l,
            // a quoted number means the number, as RpcRequest.Int reads it (a declared integer accepts both)
            { ValueKind: JsonValueKind.String } v when long.TryParse(v.GetString(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var q) => q,
            _ => null,
        };
}
