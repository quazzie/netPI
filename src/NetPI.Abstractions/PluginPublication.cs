using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NetPI.Abstractions;

/// <summary>
/// astra-1 P1 — the plugin publication contract. A plugin is built into an
/// IMMUTABLE artifact directory (<c>.artifacts/plugins/&lt;id&gt;/&lt;build-id&gt;/</c>),
/// described by an <see cref="PluginManifest"/> that pins every file's relative
/// path + SHA-256 + size. A tiny <see cref="PluginBuildPointer"/> in the
/// plugin's discovery directory (<c>plugins/&lt;id&gt;/current.json</c>) names the
/// active build and where its artifact lives. The host resolves the pointer,
/// validates the artifact, and snapshots it into a per-host-instance runtime
/// cache before constructing a collectible ALC.
///
/// The JSON schema is shared verbatim with <c>tools/publish-plugins.ps1</c>
/// (the only writer). Keep field names in sync with that script.
/// </summary>
public static class PluginPublication
{
    /// <summary>Schema version of the manifest/pointer format.</summary>
    public const int Schema = 1;

    /// <summary>File name of the manifest inside a finalized artifact directory.</summary>
    public const string ManifestFileName = "artifact.json";

    /// <summary>File name of the build pointer inside a plugin's discovery directory.</summary>
    public const string PointerFileName = "current.json";

    /// <summary>Ownership token kept in each per-host-instance snapshot root.</summary>
    public const string OwnershipFileName = ".owner";

    /// <summary>Build a deterministic 12-hex build id from a set of (relativePath, sha256) pairs.</summary>
    public static string ComputeBuildId(IEnumerable<(string Path, string Sha256)> files)
    {
        using var sha = SHA256.Create();
        var sb = new StringBuilder();
        foreach (var (path, sha256) in files.OrderBy(f => f.Path, StringComparer.Ordinal))
        {
            sb.Append(path).Append('\n').Append(sha256).Append('\n');
        }
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(bytes)[..12].ToLowerInvariant();
    }

    /// <summary>
    /// Validate a finalized artifact directory against its manifest: every listed
    /// file exists, its relative path is safe (no traversal / absolute), and its
    /// SHA-256 + size match. Returns null when valid, or a description of the first
    /// problem. The manifest file itself is not required to be listed in
    /// <see cref="PluginManifest.Files"/> (it is the descriptor, not a payload).
    /// </summary>
    public static string? ValidateArtifact(string artifactDir, PluginManifest manifest)
    {
        if (!Directory.Exists(artifactDir))
            return $"artifact directory does not exist: {artifactDir}";
        if (manifest.BuildId is not { Length: > 0 })
            return "manifest is missing a buildId";
        if (manifest.EntryAssembly is not { Length: > 0 })
            return "manifest is missing an entryAssembly";

        foreach (var f in manifest.Files)
        {
            var rel = f.Path.Replace('\\', '/');
            if (Path.IsPathRooted(rel) || rel.StartsWith('/') ||
                rel.Contains("/../", StringComparison.Ordinal) ||
                rel.EndsWith("/..", StringComparison.Ordinal) ||
                rel.StartsWith("../", StringComparison.Ordinal))
                return $"unsafe relative path in artifact: '{f.Path}'";
            var full = Path.GetFullPath(Path.Combine(artifactDir, rel));
            if (!full.StartsWith(Path.GetFullPath(artifactDir) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                return $"path escapes artifact root: '{f.Path}'";
            if (!File.Exists(full))
                return $"listed file missing from artifact: '{f.Path}'";
            var size = new FileInfo(full).Length;
            if (size != f.Size)
                return $"size mismatch for '{f.Path}': manifest {f.Size}, disk {size}";
            var actual = FileHash(full);
            if (!string.Equals(actual, f.Sha256, StringComparison.OrdinalIgnoreCase))
                return $"sha256 mismatch for '{f.Path}': manifest {f.Sha256}, disk {actual}";
        }

        // The entry assembly must be present (it may or may not be in Files; the
        // loader requires it, so reject an artifact that lacks it).
        var entry = Path.GetFullPath(Path.Combine(artifactDir, manifest.EntryAssembly));
        if (!entry.StartsWith(Path.GetFullPath(artifactDir) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            return $"entry assembly escapes artifact root: '{manifest.EntryAssembly}'";
        if (!File.Exists(entry))
            return $"entry assembly missing from artifact: '{manifest.EntryAssembly}'";

        return null;
    }

    /// <summary>SHA-256 hex of a file's bytes.</summary>
    public static string FileHash(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Load a manifest from its file. Returns (manifest, error): exactly one is
    /// non-null.
    /// </summary>
    public static (PluginManifest? Manifest, string? Error) LoadManifest(string artifactDir)
    {
        var path = Path.Combine(artifactDir, ManifestFileName);
        if (!File.Exists(path))
            return (null, $"manifest not found: {path}");
        try
        {
            var json = File.ReadAllText(path);
            var m = JsonSerializer.Deserialize<PluginManifest>(json, JsonOpts);
            if (m is null) return (null, "manifest deserialized to null");
            if (m.Schema != Schema) return (null, $"unsupported manifest schema {m.Schema}");
            return (m, null);
        }
        catch (Exception ex)
        {
            return (null, $"manifest unreadable: {ex.Message}");
        }
    }

    /// <summary>
    /// Load a build pointer. Returns (pointer, error): exactly one is non-null.
    /// </summary>
    public static (PluginBuildPointer? Pointer, string? Error) LoadPointer(string discoveryDir)
    {
        var path = Path.Combine(discoveryDir, PointerFileName);
        if (!File.Exists(path))
            return (null, "no build pointer (current.json) — legacy layout");
        try
        {
            var json = File.ReadAllText(path);
            var p = JsonSerializer.Deserialize<PluginBuildPointer>(json, JsonOpts);
            if (p is null) return (null, "pointer deserialized to null");
            if (p.Schema != Schema) return (null, $"unsupported pointer schema {p.Schema}");
            if (p.BuildId is not { Length: > 0 }) return (null, "pointer is missing a buildId");
            if (p.ArtifactDir is not { Length: > 0 }) return (null, "pointer is missing an artifactDir");
            return (p, null);
        }
        catch (Exception ex)
        {
            return (null, $"pointer unreadable: {ex.Message}");
        }
    }

    /// <summary>
    /// Atomically write <paramref name="content"/> to <paramref name="path"/> on
    /// the same filesystem: write a sibling temp file, then OS-replace the target
    /// (File.Replace) or move it into place on first creation. A failed write
    /// leaves the previous contents untouched.
    /// </summary>
    public static void WriteAtomic(string path, string content)
    {
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        var tmp = Path.Combine(dir, Path.GetFileName(path) + ".tmp-" + Guid.NewGuid().ToString("n"));
        File.WriteAllText(tmp, content);
        try
        {
            if (File.Exists(path))
                File.Replace(tmp, path, null, ignoreMetadataErrors: true);
            else
                File.Move(tmp, path);
        }
        catch
        {
            try { File.Delete(tmp); } catch { /* best-effort */ }
            throw;
        }
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };
}

/// <summary>One file in an artifact manifest.</summary>
public sealed record PluginManifestFile
{
    [JsonPropertyName("path")]
    public required string Path { get; init; }

    [JsonPropertyName("sha256")]
    public required string Sha256 { get; init; }

    [JsonPropertyName("size")]
    public required long Size { get; init; }
}

/// <summary>Descriptor of one immutable plugin artifact (written as artifact.json).</summary>
public sealed record PluginManifest
{
    [JsonPropertyName("schema")]
    public int Schema { get; init; } = PluginPublication.Schema;

    /// <summary>Plugin id (the discovery-directory name the host keys by).</summary>
    [JsonPropertyName("plugin")]
    public required string Plugin { get; init; }

    /// <summary>Entry assembly file name (top-level) that implements the plugin.</summary>
    [JsonPropertyName("entryAssembly")]
    public required string EntryAssembly { get; init; }

    /// <summary>Unique, content-derived build id (12 hex).</summary>
    [JsonPropertyName("buildId")]
    public required string BuildId { get; init; }

    [JsonPropertyName("targetFramework")]
    public required string TargetFramework { get; init; }

    [JsonPropertyName("runtimeIdentifier")]
    public required string RuntimeIdentifier { get; init; }

    /// <summary>
    /// Compatibility token for the shared contract: the PUBLIC API surface id
    /// (NetPI.Abstractions.ContractId) of the netPI.Abstractions the plugin was
    /// compiled against. Depends only on visible types/members — not on the
    /// git commit, the assembly version, or the PDB — so a commit that does not
    /// change the API never invalidates the pin; a real contract change does.
    /// </summary>
    [JsonPropertyName("abstractionsApiId")]
    public string AbstractionsApiId { get; init; } = string.Empty;

    /// <summary>
    /// Legacy: byte hash of the netPI.Abstractions.dll the plugin was compiled
    /// against. Kept for artifacts published before the API id; a host that
    /// cannot compare API ids falls back to this exact-byte comparison.
    /// </summary>
    [JsonPropertyName("abstractionsBuildId")]
    public string AbstractionsBuildId { get; init; } = string.Empty;

    /// <summary>Every payload file (relative to the artifact root), pinned by hash + size.</summary>
    [JsonPropertyName("files")]
    public IReadOnlyList<PluginManifestFile> Files { get; init; } = [];
}

/// <summary>Build pointer: names the active build for a plugin (written as current.json).</summary>
public sealed record PluginBuildPointer
{
    [JsonPropertyName("schema")]
    public int Schema { get; init; } = PluginPublication.Schema;

    [JsonPropertyName("plugin")]
    public required string Plugin { get; init; }

    [JsonPropertyName("buildId")]
    public required string BuildId { get; init; }

    /// <summary>Absolute path to the finalized artifact directory.</summary>
    [JsonPropertyName("artifactDir")]
    public required string ArtifactDir { get; init; }

    /// <summary>When the pointer was written (UTC), for diagnostics.</summary>
    [JsonPropertyName("publishedAt")]
    public string? PublishedAt { get; init; }
}
