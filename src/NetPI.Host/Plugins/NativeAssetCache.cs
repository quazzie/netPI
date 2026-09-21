using System.Runtime.InteropServices;

namespace NetPI.Host.Plugins;

/// <summary>
/// astra-1 P4 — content-addressed, process-lifetime native library cache.
///
/// Native libraries (e.g. SQLitePCLRaw's e_sqlite3) are process-global
/// mappings: loading the same native build from two different snapshot
/// directories maps it twice. The old blanket preloader (every RID folder,
/// every generation) accumulated one unmapped-until-exit mapping per reload.
///
/// Policy (narrow, per plan): an approved native build is loaded ONCE, from
/// an immutable content-addressed cache under the runtime home
/// (<c>~/.netpi/native-cache/&lt;sha256-16&gt;/&lt;file&gt;</c>). Compatible reloads
/// (same file bytes) reuse the exact same cached path — no new process
/// mapping. A changed native build produces a new cache entry and is
/// classified RestartRequired by the reload coordinator (the host refuses to
/// map two versions of the same native library side by side).
/// </summary>
public sealed class NativeAssetCache
{
    private readonly object _gate = new();
    private readonly Dictionary<string, string> _bySource = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _loadedCachePaths = new(StringComparer.OrdinalIgnoreCase);

    private static readonly string CacheRoot =
        System.IO.Path.Combine(NetPI.Abstractions.RuntimeHome.Dir, "native-cache");

    /// <summary>Native cache paths loaded this process (diagnostics).</summary>
    public IReadOnlyCollection<string> LoadedCachePaths { get { lock (_gate) return [.. _loadedCachePaths]; } }

    /// <summary>
    /// Returns the cached path for the native file at <paramref name="sourcePath"/>
    /// (copying it into the cache on first use). The same content always maps
    /// to the same cached file, so a second generation loading the same native
    /// build never creates a second process mapping.
    /// </summary>
    public string EnsureCached(string sourcePath)
    {
        lock (_gate)
        {
            if (_bySource.TryGetValue(sourcePath, out var known))
                return known;

            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.IO.File.ReadAllBytes(sourcePath)))[..32].ToLowerInvariant();
            var target = System.IO.Path.Combine(CacheRoot, hash[..16], System.IO.Path.GetFileName(sourcePath));
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
            if (!System.IO.File.Exists(target))
                System.IO.File.Copy(sourcePath, target, overwrite: false);
            _bySource[sourcePath] = target;
            _loadedCachePaths.Add(target);
            return target;
        }
    }

    /// <summary>
    /// Native library files under a plugin directory's CURRENT-RID folder
    /// (plus top-level *.so / *.dylib for non-Windows layouts) — the set the
    /// host preloads into the process cache for a generation.
    /// </summary>
    public static List<string> NativeRidFiles(string pluginDir)
    {
        var result = new List<string>();
        var ridDir = System.IO.Path.Combine(pluginDir, "runtimes", CurrentRid(), "native");
        if (System.IO.Directory.Exists(ridDir))
            result.AddRange(System.IO.Directory.EnumerateFiles(ridDir));
        foreach (var f in System.IO.Directory.EnumerateFiles(pluginDir, "*", System.IO.SearchOption.TopDirectoryOnly))
            if (f.EndsWith(".so", System.StringComparison.OrdinalIgnoreCase) ||
                f.EndsWith(".dylib", System.StringComparison.OrdinalIgnoreCase))
                result.Add(f);
        return result;
    }

    /// <summary>
    /// All native file content hashes (sha256-32, lower) under a plugin
    /// directory's CURRENT-RID folders — the "native build id" of a
    /// generation, used to decide whether a reload changes the native build
    /// (a changed native build is RestartRequired, not hot-swappable).
    /// </summary>
    public static HashSet<string> NativeBuildHashes(string pluginDir)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in NativeRidFiles(pluginDir))
        {
            try { result.Add(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.IO.File.ReadAllBytes(f)))[..32].ToLowerInvariant()); }
            catch { /* unreadable file: ignore for the comparison */ }
        }
        return result;
    }

    /// <summary>The RID matching this process (win-x64, win-arm64, linux-x64, …).</summary>
    public static string CurrentRid()
    {
        string arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            Architecture.X86 => "x86",
            Architecture.Arm => "arm",
            _ => throw new PlatformNotSupportedException($"unsupported architecture {RuntimeInformation.ProcessArchitecture}")
        };
        if (OperatingSystem.IsWindows()) return $"win-{arch}";
        if (OperatingSystem.IsMacOS()) return $"osx-{arch}";
        return $"linux-{arch}";
    }
}
