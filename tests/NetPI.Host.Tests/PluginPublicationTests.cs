using NetPI.Abstractions;
using NetPI.Host.Plugins;
using Xunit;

namespace NetPI.Host.Tests;

/// <summary>
/// astra-1 P1 — unit tests for the publication contract (manifest/pointer,
/// build ids, artifact validation) and the host-side runtime layout helpers
/// (host-instance id, snapshots, allowlist exclusion, ownership-aware pruning).
/// </summary>
public sealed class PluginPublicationTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "netpi-pub-" + Guid.NewGuid().ToString("n"));

    public PluginPublicationTests() => Directory.CreateDirectory(_tmp);

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); } catch { }
    }

    private string NewDir(string name)
    {
        var d = Path.Combine(_tmp, name);
        Directory.CreateDirectory(d);
        return d;
    }

    /// <summary>Write a minimal artifact dir + matching manifest. Returns the manifest.</summary>
    private static PluginManifest MakeArtifact(string artifactDir, string plugin, string? extra = null)
    {
        Directory.CreateDirectory(artifactDir);
        var entry = "entry.dll";
        File.WriteAllText(Path.Combine(artifactDir, entry), "MAGICBYTES");
        var files = new List<(string Path, string Sha256)> { (entry, PluginPublication.FileHash(Path.Combine(artifactDir, entry))) };
        if (extra is not null)
        {
            File.WriteAllText(Path.Combine(artifactDir, "extra.txt"), extra);
            files.Add(("extra.txt", PluginPublication.FileHash(Path.Combine(artifactDir, "extra.txt"))));
        }
        var m = new PluginManifest
        {
            Plugin = plugin,
            EntryAssembly = entry,
            BuildId = PluginPublication.ComputeBuildId(files),
            TargetFramework = "net10.0",
            RuntimeIdentifier = "win-x64",
            Files = files.Select(f => new PluginManifestFile
            {
                Path = f.Path,
                Sha256 = f.Sha256,
                Size = new FileInfo(Path.Combine(artifactDir, f.Path)).Length,
            }).ToArray(),
        };
        File.WriteAllText(Path.Combine(artifactDir, PluginPublication.ManifestFileName),
            System.Text.Json.JsonSerializer.Serialize(m, new System.Text.Json.JsonSerializerOptions
            {
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
                WriteIndented = true,
            }));
        return m;
    }

    // ------------------------------------------------------------- build id

    [Fact]
    public void BuildId_IsStable_12Hex_AndOrderIndependent()
    {
        var a = PluginPublication.ComputeBuildId(new[] { ("a.dll", "aa"), ("b.dll", "bb") });
        var b = PluginPublication.ComputeBuildId(new[] { ("b.dll", "bb"), ("a.dll", "aa") });
        Assert.Equal(a, b);
        Assert.Matches("^[0-9a-f]{12}$", a);
        Assert.NotEqual(a, PluginPublication.ComputeBuildId(new[] { ("a.dll", "cc"), ("b.dll", "bb") }));
    }

    // ------------------------------------------------------- validation

    [Fact]
    public void ValidateArtifact_PassOnGoodArtifact()
    {
        var dir = NewDir("good");
        var m = MakeArtifact(dir, "netpi.good");
        Assert.Null(PluginPublication.ValidateArtifact(dir, m));
        // The manifest round-trips through LoadManifest (schema, fields, files).
        var (loaded, err) = PluginPublication.LoadManifest(dir);
        Assert.Null(err);
        Assert.NotNull(loaded);
        Assert.Equal(m.BuildId, loaded!.BuildId);
        Assert.Equal("net10.0", loaded.TargetFramework);
        Assert.Contains(loaded.Files, f => f.Path == "entry.dll");
    }

    [Fact]
    public void ValidateArtifact_RejectsShaMismatch()
    {
        var dir = NewDir("shamismatch");
        var m = MakeArtifact(dir, "netpi.x");
        // Same length, different bytes → sha mismatch (not a size mismatch):
        // flip the first byte of the entry assembly.
        var bytes = File.ReadAllBytes(Path.Combine(dir, "entry.dll"));
        bytes[0] ^= 0xFF;
        File.WriteAllBytes(Path.Combine(dir, "entry.dll"), bytes);
        var err = PluginPublication.ValidateArtifact(dir, m);
        Assert.NotNull(err);
        Assert.Contains("sha256 mismatch", err!);
    }

    [Fact]
    public void ValidateArtifact_RejectsMissingFile()
    {
        var dir = NewDir("missing");
        var m = MakeArtifact(dir, "netpi.x", extra: "payload");
        File.Delete(Path.Combine(dir, "extra.txt"));
        var err = PluginPublication.ValidateArtifact(dir, m);
        Assert.NotNull(err);
        Assert.Contains("listed file missing", err!);
    }

    [Theory]
    [InlineData("../escape.dll")]
    [InlineData("sub/../escape.dll")]
    [InlineData("/abs/path.dll")]
    public void ValidateArtifact_RejectsPathTraversal(string badPath)
    {
        var dir = NewDir("trav-" + badPath.Replace('/', '_').Replace('\\', '_'));
        var m = MakeArtifact(dir, "netpi.x");
        var evil = new PluginManifestFile { Path = badPath, Sha256 = "00", Size = 0 };
        var withEvil = m with { Files = [evil] };
        var err = PluginPublication.ValidateArtifact(dir, withEvil);
        Assert.NotNull(err);
        Assert.Contains("unsafe relative path", err!);
    }

    [Fact]
    public void ValidateArtifact_RejectsMissingEntryAssembly()
    {
        var dir = NewDir("noentry");
        var m = MakeArtifact(dir, "netpi.x") with { EntryAssembly = "other.dll" };
        var err = PluginPublication.ValidateArtifact(dir, m);
        Assert.NotNull(err);
        Assert.Contains("entry assembly missing", err!);
    }

    [Fact]
    public void ValidateArtifact_RejectsMissingArtifactDir()
    {
        var m = MakeArtifact(NewDir("hasfiles"), "netpi.x");
        var err = PluginPublication.ValidateArtifact(Path.Combine(_tmp, "does-not-exist"), m);
        Assert.NotNull(err);
        Assert.Contains("does not exist", err!);
    }

    [Fact]
    public void ValidateArtifact_RejectsMissingBuildIdAndEntry()
    {
        var dir = NewDir("nobuildid");
        var m = MakeArtifact(dir, "netpi.x") with { BuildId = "", EntryAssembly = "" };
        Assert.Contains("missing a buildId", PluginPublication.ValidateArtifact(dir, m));
        Assert.Contains("missing an entryAssembly", PluginPublication.ValidateArtifact(dir, m with { BuildId = "abc123def456" }));
    }

    // ------------------------------------------------------- pointers

    [Fact]
    public void LoadPointer_MissingFile_IsLegacyLayout()
    {
        var dir = NewDir("legacy");
        File.WriteAllText(Path.Combine(dir, "someplugin.dll"), "x");
        var (p, e) = PluginPublication.LoadPointer(dir);
        Assert.Null(p);
        Assert.Contains("legacy", e!);
    }

    [Fact]
    public void LoadPointer_RoundTripsAndValidates()
    {
        var dir = NewDir("ptr");
        var opts = new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase };
        var ptr = new PluginBuildPointer { Plugin = "netpi.p", BuildId = "abcdef123456", ArtifactDir = @"C:\art\abcdef123456" };
        PluginPublication.WriteAtomic(Path.Combine(dir, PluginPublication.PointerFileName),
            System.Text.Json.JsonSerializer.Serialize(ptr, opts));

        var (loaded, err) = PluginPublication.LoadPointer(dir);
        Assert.Null(err);
        Assert.Equal("abcdef123456", loaded!.BuildId);

        // Re-pointing must be atomic: a second write replaces cleanly.
        var ptr2 = ptr with { BuildId = "654321fedcba" };
        PluginPublication.WriteAtomic(Path.Combine(dir, PluginPublication.PointerFileName),
            System.Text.Json.JsonSerializer.Serialize(ptr2, opts));
        var (reloaded, err2) = PluginPublication.LoadPointer(dir);
        Assert.Null(err2);
        Assert.Equal("654321fedcba", reloaded!.BuildId);
        // No temp litter left behind.
        Assert.Empty(Directory.EnumerateFiles(dir, "*.tmp-*"));
    }

    [Fact]
    public void LoadPointer_RejectsBadSchema()
    {
        var dir = NewDir("badschema");
        File.WriteAllText(Path.Combine(dir, PluginPublication.PointerFileName),
            "{\"schema\": 99, \"plugin\": \"p\", \"buildId\": \"abc\", \"artifactDir\": \"x\"}");
        var (p, e) = PluginPublication.LoadPointer(dir);
        Assert.Null(p);
        Assert.Contains("unsupported pointer schema", e!);
    }

    // ----------------------------------------------------- ownership + prune

    [Fact]
    public void ClaimOwnership_FirstClaimant_Wins_AndPruneRespectsIt()
    {
        var root = NewDir("inst-root");
        Assert.Equal("hostA", PluginRuntimeLayout.ClaimOwnership(root, "hostA"));
        // A second, different claimant must NOT take ownership.
        Assert.Equal("hostA", PluginRuntimeLayout.ClaimOwnership(root, "hostB"));

        // Prune only keeps the newest 2 of 4 (by creation time).
        var pluginRoot = Path.Combine(root, "netpi.x");
        var kept = new List<string>();
        for (var i = 1; i <= 4; i++)
        {
            var d = Path.Combine(pluginRoot, $"netpi-{i}-aaaa");
            Directory.CreateDirectory(d);
            File.WriteAllText(Path.Combine(d, "file.txt"), i.ToString());
            if (i >= 3) kept.Add(Path.GetFileName(d));
            Thread.Sleep(20); // ensure distinct creation timestamps
        }
        var removed = PluginRuntimeLayout.PruneSnapshots(pluginRoot, 2);
        Assert.Equal(2, removed);
        var survivors = Directory.EnumerateDirectories(pluginRoot).Select(Path.GetFileName).ToList();
        Assert.Equal(kept, survivors.OrderBy(x => x).ToList());
    }

    [Fact]
    public void SnapshotArtifact_CopiesPayload_ExcludesMetadata()
    {
        var artifactDir = NewDir("artifact");
        MakeArtifact(artifactDir, "netpi.s", extra: "payload");
        // Plant files that must never enter a snapshot.
        Directory.CreateDirectory(Path.Combine(artifactDir, "bin"));
        File.WriteAllText(Path.Combine(artifactDir, "bin", "junk.dll"), "x");
        File.WriteAllText(Path.Combine(artifactDir, "notes.md"), "x");

        var instance = NewDir("instance");
        var snap = PluginRuntimeLayout.SnapshotArtifact(instance, "netpi.s", "netpi-1-deadbeef0000", artifactDir);
        Assert.True(File.Exists(Path.Combine(snap, "entry.dll")));
        Assert.True(File.Exists(Path.Combine(snap, "extra.txt")));
        Assert.False(Directory.Exists(Path.Combine(snap, "bin")));
        Assert.False(File.Exists(Path.Combine(snap, "artifact.json")));
        Assert.False(File.Exists(Path.Combine(snap, "notes.md")));
        Assert.False(File.Exists(Path.Combine(snap, "current.json")));
    }

    [Theory]
    [InlineData("bin/x.dll", true)]
    [InlineData("obj/x.dll", true)]
    [InlineData("src/x.dll", true)]
    [InlineData("node_modules/x.dll", true)]
    [InlineData("project.assets.json", true)]
    [InlineData("current.json", true)]
    [InlineData("artifact.json", true)]
    [InlineData("foo.csproj", true)]
    [InlineData("Program.cs", true)]
    [InlineData("README.md", true)]
    [InlineData("entry.dll", false)]
    [InlineData("runtimes/win-x64/native/e_sqlite3.dll", false)]
    [InlineData("dep.dll", false)]
    public void ExclusionRules_AreStable(string path, bool excluded)
    {
        var actual = PluginRuntimeLayout.IsExcluded(path);
        Assert.True(actual == excluded, $"IsExcluded(\"{path}\") = {actual}, expected {excluded}");
    }

    [Fact]
    public void HostInstanceId_IsUniquePerCall_WithStableBuildPrefix()
    {
        var a = PluginRuntimeLayout.GetHostInstanceId();
        var b = PluginRuntimeLayout.GetHostInstanceId();
        Assert.NotEqual(a, b); // fresh guid suffix per call
        // The deterministic build prefix (12 hex) is identical for the same host build.
        Assert.Equal(a.Split('-')[0], b.Split('-')[0]);
        Assert.Matches("^[0-9a-f]{12}-[0-9a-f]{8}$", a);
    }
}
