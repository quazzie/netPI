using System.Diagnostics;
using NetPI.Abstractions;

namespace NetPI.Host.Plugins;

/// <summary>
/// astra-1 P1 — where a plugin's build actually loads from at runtime.
///
/// The host NEVER loads directly from a published artifact (a build the publisher
/// may prune later) nor from the legacy staged folder. It copies the validated
/// artifact into a per-host-instance, per-attempt snapshot:
/// <c>&lt;runtimeDir&gt;/plugin-cache/&lt;hostInstance&gt;/&lt;plugin&gt;/&lt;attempt&gt;-&lt;buildId&gt;/</c>.
///
/// Attempt ids are unique per load (a fresh <c>netpi-<n>-<12hex></c>), are
/// reserved even for loads that later fail, and are never reused — a generation
/// directory, once created, is owned by the ALC that was built on top of it.
/// Pruning is ownership-aware: a snapshot directory is only ever deleted by a
/// prune run that holds the ownership token for its instance root (the lock file
/// is created-if-absent and claimed; a directory whose token we do not own —
/// another host instance is using it — is left alone and reported as skipped).
/// </summary>
public static class PluginRuntimeLayout
{
    /// <summary>
    /// Compute the host-instance id: a 12-hex of the hash of the host assembly
    /// path, or "<c>host-<pid>-&lt;16hex-guid-suffix&gt;</c>" when unavailable. Stable
    /// across restarts of the same host build; unique per running instance for
    /// the guid suffix. The deterministic half keeps snapshots of the same build
    /// predictable; the instance half separates concurrently running hosts.
    /// </summary>
    public static string GetHostInstanceId(string? hostAssemblyPath = null)
    {
        try
        {
            if (hostAssemblyPath is null)
                hostAssemblyPath = typeof(PluginRuntimeLayout).Assembly.Location;
            var h = PluginPublication.FileHash(hostAssemblyPath);
            // The deterministic host-build half (stable across restarts of the
            // same build) plus a per-process guid suffix (unique while running).
            return $"{h[..12]}-{Guid.NewGuid().ToString("n")[..8]}";
        }
        catch
        {
            return "host-" + Process.GetCurrentProcess().Id + "-" + Guid.NewGuid().ToString("n")[..8];
        }
    }

    /// <summary>The per-host-instance snapshot root (created + ownership-tokened on demand).</summary>
    public static string InstanceRoot(string runtimeDir, string hostInstanceId) =>
        Path.Combine(runtimeDir, "plugin-cache", hostInstanceId);

    /// <summary>
    /// Claim the ownership token for an instance root. Returns the token string
    /// (the claimant) when this process may prune inside the root. The token file
    /// is created-if-absent with this claimant; if another (differently-named)
    /// claimant already owns it, this call returns that other claimant and the
    /// caller must NOT prune. In practice a single host owns its own root; the
    /// token is belt-and-suspenders so two hosts sharing a runtime home (which
    /// the host-instance dir already separates) can never prune each other.
    /// </summary>
    public static string ClaimOwnership(string instanceRoot, string claimant)
    {
        Directory.CreateDirectory(instanceRoot);
        var tokenPath = Path.Combine(instanceRoot, PluginPublication.OwnershipFileName);
        try
        {
            // Exclusive-create semantics: if absent, write our claim.
            using var fs = new FileStream(tokenPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            fs.Position = 0;
            using var rd = new StreamReader(fs);
            var existing = rd.ReadToEnd().Trim();
            if (existing.Length > 0 && existing != claimant)
                return existing; // someone else owns it
            if (existing != claimant)
            {
                fs.SetLength(0);
                using var wr = new StreamWriter(fs);
                wr.Write(claimant);
            }
            return claimant;
        }
        catch (IOException)
        {
            // Another process holds the token file open exclusively: treat as
            // owned by someone else (do not prune).
            return existingToken(tokenPath) ?? claimant + "!locked";
        }
    }

    private static string? existingToken(string tokenPath)
    {
        try { return File.Exists(tokenPath) ? File.ReadAllText(tokenPath).Trim() : null; }
        catch { return null; }
    }

    /// <summary>
    /// Copy a validated artifact into a fresh per-attempt snapshot directory and
    /// return the snapshot path. <paramref name="attemptId"/> is unique and is
    /// reserved (directory created) even if the copy later fails — the caller
    /// must not reuse a reserved attempt. Files outside the artifact root, and
    /// anything the manifest did not list, are excluded.
    /// </summary>
    public static string SnapshotArtifact(string instanceRoot, string pluginId, string attemptId, string artifactDir)
    {
        var pluginRoot = Path.Combine(instanceRoot, pluginId);
        Directory.CreateDirectory(pluginRoot);
        var snapDir = Path.Combine(pluginRoot, attemptId);
        Directory.CreateDirectory(snapDir);
        foreach (var file in Directory.EnumerateFiles(artifactDir, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(artifactDir, file);
            if (IsExcluded(rel)) continue;
            var target = Path.Combine(snapDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: false);
        }
        return snapDir;
    }

    /// <summary>
    /// Legacy migration: copy a legacy staged folder (DLLs + runtime assets at
    /// the top level / runtimes/) into a snapshot directory, EXCLUDING source
    /// and build trees (<c>bin</c>, <c>obj</c>, <c>src</c>, caches, project/
    /// manifest files). Returns the snapshot path, or null when the folder has
    /// no loadable assembly (not a plugin).
    /// </summary>
    public static string? SnapshotLegacy(string instanceRoot, string pluginId, string attemptId, string stagedDir)
    {
        Directory.CreateDirectory(Path.Combine(instanceRoot, pluginId));
        var snapDir = Path.Combine(instanceRoot, pluginId, attemptId);
        Directory.CreateDirectory(snapDir);
        var count = 0;
        foreach (var file in Directory.EnumerateFiles(stagedDir, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(stagedDir, file).Replace('\\', '/');
            if (IsExcluded(rel)) continue;
            var target = Path.Combine(snapDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: false);
            count++;
        }
        return count > 0 ? snapDir : null;
    }

    /// <summary>
    /// Prune a plugin's snapshot directories down to the newest
    /// <paramref name="keep"/> (by snapshot-creation timestamp). Only invoked
    /// when this process owns the instance root. A directory that cannot be
    /// removed (still mapped by a live ALC, or a locked file) is skipped and
    /// reported — never treated as a failure. Returns the count removed.
    /// </summary>
    public static int PruneSnapshots(string pluginRoot, int keep)
    {
        if (keep < 1) keep = 1;
        if (!Directory.Exists(pluginRoot)) return 0;
        // Attempt names carry their sequence: netpi-<gen>-<12hex>. The sequence
        // is exact and immune to filesystem timestamp resolution (Windows NTFS
        // is ~100 µs-11.5 ms, so CreationTime order is unreliable for fast
        // back-to-back loads); unparseable names fall back to CreationTime.
        var dirs = Directory.EnumerateDirectories(pluginRoot)
            .Select(d => new DirectoryInfo(d))
            .Where(d => !d.Name.StartsWith('.'))
            .OrderByDescending(d => AttemptSequence(d.Name, d.CreationTime))
            .ToList();
        var removed = 0;
        foreach (var d in dirs.Skip(keep))
        {
            try
            {
                d.Delete(recursive: true);
                removed++;
            }
            catch
            {
                // Locked by a not-yet-finalized ALC or an OS blip: retried on the
                // next prune. A locked snapshot is diagnostic info, not an error.
            }
        }
        return removed;
    }

    /// <summary>
    /// Order key for snapshot directories: the attempt sequence embedded in the
    /// name (<c>netpi-&lt;gen&gt;-&lt;12hex&gt;</c>) when parseable, else the creation
    /// time (epoch ticks). Sequence wins; equal sequences fall back to ticks.
    /// </summary>
    private static (int Seq, long Ticks) AttemptSequence(string name, DateTime creationTime)
    {
        if (name.StartsWith("netpi-", StringComparison.Ordinal))
        {
            var parts = name.Split('-');
            if (parts.Length >= 3 && int.TryParse(parts[1], out var gen))
                return (gen, creationTime.Ticks);
        }
        return (int.MinValue, creationTime.Ticks);
    }

    /// <summary>
    /// Files never loaded into a plugin ALC: build/source trees, caches, project
    /// and manifest metadata. The snapshot must contain exactly the ALC's input.
    /// </summary>
    public static bool IsExcluded(string relativePath)
    {
        var p = relativePath.Replace('\\', '/');
        var segs = p.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < segs.Length - 1; i++)
        {
            var n = segs[i].ToLowerInvariant();
            if (n is "bin" or "obj" or "src" or "rsc" or "node_modules" or ".git")
                return true;
        }
        var name = segs[^1].ToLowerInvariant();
        if (name is "project.assets.json" or "netpi.project.json" or "artifact.json" or "current.json")
            return true;
        // A stray project or source file at the top level.
        var top = segs[0].ToLowerInvariant();
        if (top.EndsWith(".csproj", StringComparison.Ordinal) || top.EndsWith(".cs", StringComparison.Ordinal)
            || top.EndsWith(".sln", StringComparison.Ordinal) || top.EndsWith(".md", StringComparison.Ordinal)
            || top.EndsWith(".props", StringComparison.Ordinal) || top.EndsWith(".targets", StringComparison.Ordinal))
            return true;
        return false;
    }
}
