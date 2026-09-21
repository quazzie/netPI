using NetPI.Abstractions;
using NetPI.Host;
using NetPI.Host.Config;
using NetPI.Host.Plugins;
using TestPluginClass = NetPI.TestPlugin.TestPlugin;
using Xunit;

namespace NetPI.Host.Tests;

/// <summary>
/// Regression for the plugin hot-swap problem: while a generation is active
/// its DLLs were mapped from the staged folder (<c>plugins/&lt;name&gt;/</c>),
/// which on Windows means the host held file locks on the exact folder a new
/// build stages over — "the DLL is locked" and no hot-swap.
///
/// astra-1 P1 layout: the host ALWAYS loads from an immutable per-host-instance,
/// per-attempt snapshot (<c>&lt;runtimeDir&gt;/plugin-cache/&lt;instance&gt;/&lt;plugin&gt;/&lt;attempt&gt;-</c>
/// <c>&lt;buildId&gt;/</c>); a new build publishes a NEW artifact directory and
/// flips an atomic <c>current.json</c> pointer, so the previous snapshot and the
/// old artifact are both untouched. These tests prove:
///   1. the running generation is mapped from a per-instance snapshot, not the
///      source (legacy staged folder OR published artifact);
///   2. a new build (new artifact + pointer flip) can be published while gen 1
///      is active, gen 1 keeps its original bytes, and a reload picks up the
///      new bytes as gen 2 under its pinned build id;
///   3. stale snapshot directories are pruned (ownership-aware) to the
///      configured count once their ALCs are finalized.
/// </summary>
public sealed class PluginHotSwapTests : IAsyncLifetime
{
    private string _home = null!;
    private string _pluginDir = null!;
    private string _testBin = null!;

    public async Task InitializeAsync()
    {
        _home = Path.Combine(Path.GetTempPath(), "netpi-hotswap-" + Guid.NewGuid().ToString("n"));
        _pluginDir = Path.Combine(_home, "plugins");
        Directory.CreateDirectory(_pluginDir);

        // The TestPlugin's OWN build output (abstractions + fixture only), not
        // the shared test bin — the test bin holds every dependency plugin's
        // DLL (e.g. netPI.Tools.dll) and copying it in would register foreign
        // service ids in a second copy of the fixture.
        var testAssemblyBin = Path.GetDirectoryName(typeof(TestPluginClass).Assembly.Location)!;
        var cfg = Path.GetFileName(Path.GetDirectoryName(testAssemblyBin)!); // e.g. "Debug"
        var tfm = Path.GetFileName(testAssemblyBin); // e.g. "net10.0"
        string? root = testAssemblyBin;
        while (root is not null && !File.Exists(Path.Combine(root, "NetPI.sln")))
            root = Path.GetDirectoryName(root);
        Assert.True(root is not null, "repo root with NetPI.sln not found");
        _testBin = Path.Combine(root!, "plugins", "NetPI.TestPlugin", "bin", cfg, tfm);
        Assert.True(Directory.Exists(_testBin), $"TestPlugin build output not found: {_testBin}");
    }

    public async Task DisposeAsync()
    {
        try { Directory.Delete(_home, recursive: true); } catch { }
        await Task.CompletedTask;
    }

    private HostRuntime NewRuntime()
    {
        var options = new PluginManagerOptions
        {
            PluginDirectory = _pluginDir,
            RuntimeDirectory = _home,
            MaxCachedGenerations = 2,
        };
        File.WriteAllText(Path.Combine(_home, "config.json"),
            "{ \"plugins\": { \"netpi.testplugin\": { \"generation\": \"gen\" } } }");
        return new HostRuntime(options, new ConsoleErrLogger(), new ConfigService(_home));
    }

    /// <summary>
    /// Legacy staging (pre-P1 layout): DLLs directly in <c>plugins/&lt;id&gt;/</c>
    /// with no pointer. Still loadable through the migration allowlist path.
    /// </summary>
    private void StageLegacy(string id)
    {
        var staged = Path.Combine(_pluginDir, id);
        Directory.CreateDirectory(staged);
        foreach (var f in Directory.EnumerateFiles(_testBin, "*.*")
            .Where(f => Path.GetFileName(f).StartsWith("netPI.TestPlugin", StringComparison.OrdinalIgnoreCase)))
            File.Copy(f, Path.Combine(staged, Path.GetFileName(f)), overwrite: true);
    }

    /// <summary>
    /// astra-1 P1 publication: build <paramref name="payloadDir"/> (defaults to
    /// the TestPlugin build output) into an immutable artifact directory
    /// (<c>&lt;home&gt;/.artifacts/plugins/&lt;id&gt;/&lt;buildId&gt;/</c> + artifact.json)
    /// and atomically flip <c>plugins/&lt;id&gt;/current.json</c>. Returns the
    /// artifact dir + build id.
    /// </summary>
    private (string ArtifactDir, string BuildId) PublishArtifact(string id, string? payloadDir = null)
    {
        var src = payloadDir ?? _testBin;
        // Only the TestPlugin's own assemblies — the shared test bin also holds
        // dependency plugin DLLs (e.g. NetPI.Tools.dll), whose INetPiPlugin
        // implementation would win "multiple implementations" and register
        // foreign service ids (e.g. 'tools') in a second copy.
        var dlls = new DirectoryInfo(src).EnumerateFiles()
            .Where(f => f.Extension.Equals(".dll", StringComparison.OrdinalIgnoreCase)
                        && f.Name.StartsWith("netPI.TestPlugin", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.True(dlls.Count > 0, $"no dlls in {src}");

        var entries = dlls
            .Select(f => (Path: f.Name, Sha256: PluginPublication.FileHash(f.FullName)))
            .ToList();
        var buildId = PluginPublication.ComputeBuildId(entries);

        var artifactDir = Path.Combine(_home, ".artifacts", "plugins", id, buildId);
        if (!Directory.Exists(artifactDir))
        {
            Directory.CreateDirectory(artifactDir);
            foreach (var f in dlls)
                File.Copy(f.FullName, Path.Combine(artifactDir, f.Name), overwrite: false);
        }

        var manifest = new PluginManifest
        {
            Plugin = id,
            EntryAssembly = "netPI.TestPlugin.dll",
            BuildId = buildId,
            TargetFramework = "net10.0",
            RuntimeIdentifier = "win-x64",
            Files = entries.Select(e => new PluginManifestFile
            {
                Path = e.Path,
                Sha256 = e.Sha256,
                Size = new FileInfo(Path.Combine(artifactDir, e.Path)).Length,
            }).ToArray(),
        };
        if (!File.Exists(Path.Combine(artifactDir, PluginPublication.ManifestFileName)))
        {
            File.WriteAllText(Path.Combine(artifactDir, PluginPublication.ManifestFileName),
                System.Text.Json.JsonSerializer.Serialize(manifest, new System.Text.Json.JsonSerializerOptions
                {
                    PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
                    WriteIndented = true,
                }));
        }

        // Validate before flipping the pointer (publisher-side pre-check).
        var (loadedManifest, merr) = PluginPublication.LoadManifest(artifactDir);
        Assert.Null(merr);
        Assert.Null(PluginPublication.ValidateArtifact(artifactDir, loadedManifest!));

        var discovery = Path.Combine(_pluginDir, id);
        Directory.CreateDirectory(discovery);
        var pointer = new PluginBuildPointer
        {
            Plugin = id,
            BuildId = buildId,
            ArtifactDir = artifactDir,
            PublishedAt = DateTime.UtcNow.ToString("O"),
        };
        PluginPublication.WriteAtomic(Path.Combine(discovery, PluginPublication.PointerFileName),
            System.Text.Json.JsonSerializer.Serialize(pointer, new System.Text.Json.JsonSerializerOptions
            {
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
                WriteIndented = true,
            }));
        return (artifactDir, buildId);
    }

    // ------------------------------------------------------------------

    [Fact]
    public async Task PublishedBuild_LoadsFromPerInstanceSnapshot()
    {
        var (artifactDir, buildId) = PublishArtifact("NetPI.TestPlugin");
        await using var runtime = NewRuntime();
        await runtime.StartAsync();

        var p = Assert.Single(runtime.Plugins.GetStatus());
        Assert.Equal(1, p.Generation);
        Assert.Equal(buildId, p.BuildId);

        var inst = runtime.Plugins.Get("NetPI.TestPlugin")!;
        var snap = inst.CacheDirectory!;
        // Snapshot lives under the per-host-instance cache root, named
        // netpi-&lt;gen&gt;-&lt;12hex&gt;, and is a COPY of the artifact (different
        // path, same bytes) — never the artifact dir itself, never plugins/.
        Assert.StartsWith(Path.Combine(_home, "plugin-cache"), snap);
        Assert.DoesNotContain(Path.Combine(_pluginDir, string.Empty), snap);
        Assert.DoesNotContain(artifactDir, snap);
        Assert.Equal("NetPI.TestPlugin", Path.GetFileName(Path.GetDirectoryName(snap)!));
        Assert.Matches(@"^netpi-1-[0-9a-f]{12}$", Path.GetFileName(snap));
        var dll = Path.Combine(snap, "netPI.TestPlugin.dll");
        Assert.True(File.Exists(dll));
        Assert.Equal(File.ReadAllBytes(Path.Combine(artifactDir, "netPI.TestPlugin.dll")), File.ReadAllBytes(dll));
    }

    [Fact]
    public async Task LegacyStagedFolder_StillLoads_ThroughMigrationPath()
    {
        StageLegacy("NetPI.TestPlugin");
        await using var runtime = NewRuntime();
        await runtime.StartAsync();

        var inst = runtime.Plugins.Get("NetPI.TestPlugin");
        Assert.NotNull(inst);
        Assert.Equal("legacy", inst.BuildId);
        // Migrated into the same per-instance snapshot layout.
        Assert.StartsWith(Path.Combine(_home, "plugin-cache"), inst.CacheDirectory!);
        Assert.Matches(@"^netpi-1-[0-9a-f]{12}$", Path.GetFileName(inst.CacheDirectory));
    }

    [Fact]
    public async Task NewBuildPublishedWhileActive_ReloadPicksUpNewBytes()
    {
        var (artifact1, build1) = PublishArtifact("NetPI.TestPlugin");
        await using var runtime = NewRuntime();
        await runtime.StartAsync();

        var inst1 = runtime.Plugins.Get("NetPI.TestPlugin")!;
        var snapDll1 = Path.Combine(inst1.CacheDirectory!, "netPI.TestPlugin.dll");
        var originalBytes = File.ReadAllBytes(snapDll1);
        Assert.Equal(build1, inst1.BuildId);

        // Publish a NEW build: different bytes → new artifact dir + new pointer.
        // The old artifact is untouched (immutable); only the pointer flips.
        var mutDir = Path.Combine(_home, "payload-mut");
        Directory.CreateDirectory(mutDir);
        foreach (var f in Directory.EnumerateFiles(_testBin, "*.dll"))
            File.Copy(f, Path.Combine(mutDir, Path.GetFileName(f)), overwrite: true);
        var mutDll = Path.Combine(mutDir, "netPI.TestPlugin.dll");
        var baseBytes = File.ReadAllBytes(mutDll);
        var newBytes = new byte[baseBytes.Length + 1]; // append one byte
        Array.Copy(baseBytes, newBytes, baseBytes.Length);
        File.WriteAllBytes(mutDll, newBytes);
        var (artifact2, build2) = PublishArtifact("NetPI.TestPlugin", payloadDir: mutDir);
        Assert.NotEqual(artifact1, artifact2);
        Assert.NotEqual(build1, build2);
        // The previous artifact is byte-for-byte intact.
        Assert.Equal(originalBytes, File.ReadAllBytes(Path.Combine(artifact1, "netPI.TestPlugin.dll")));

        var fresh = await runtime.Plugins.ReloadAsync("NetPI.TestPlugin");
        Assert.NotNull(fresh);
        Assert.Equal(2, fresh.Generation);
        Assert.Equal(PluginState.Active, fresh.State);
        Assert.Equal(build2, fresh!.BuildId);

        // Gen 2's snapshot carries the NEW bytes ...
        var snapDll2 = Path.Combine(fresh.CacheDirectory!, "netPI.TestPlugin.dll");
        Assert.Equal(newBytes, File.ReadAllBytes(snapDll2));
        // ...while gen 1's snapshot is untouched (immutable once snapshotted).
        Assert.Equal(originalBytes, File.ReadAllBytes(snapDll1));
    }

    [Fact]
    public async Task StaleSnapshotsArePrunedToMaxCachedGenerations()
    {
        PublishArtifact("NetPI.TestPlugin");
        await using var runtime = NewRuntime();
        await runtime.StartAsync();
        // Four reloads: generations 2..5. Pruning keeps the
        // MaxCachedGenerations=2 most recent snapshots, but a snapshot whose
        // ALC has not been finalized yet is locked and survives as a retry.
        for (var i = 0; i < 4; i++)
        {
            var fresh = await runtime.Plugins.ReloadAsync("NetPI.TestPlugin");
            Assert.NotNull(fresh);
        }

        // Force the GC to finalize every unloaded ALC (the existing no-leak
        // invariant), so the stale snapshot directories are no longer locked.
        for (int i = 0; i < 60 && runtime.Plugins.UnloadedAlocs().Any(a => !a.Collected); i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            await Task.Delay(50);
        }
        Assert.All(runtime.Plugins.UnloadedAlocs(), a => Assert.True(a.Collected, "old ALC '" + a.Label + "' should be collectible"));

        // One more load: the prune now finds every stale snapshot
        // un-locked and removes it (ownership: this host owns its instance root).
        var last = await runtime.Plugins.ReloadAsync("NetPI.TestPlugin");
        Assert.NotNull(last);
        Assert.Equal(6, last.Generation);

        var pluginRoot = Path.GetDirectoryName(last.CacheDirectory!)!;
        var present = Directory.EnumerateDirectories(pluginRoot).ToList();
        // Exactly the two most recent snapshots survive, the newest being the
        // one just loaded ...
        Assert.Equal(2, present.Count);
        Assert.Contains(last.CacheDirectory!, present);
        // ...and exactly the second-newest attempt (gen 5) is the other survivor —
        // everything older (gen 1..4) was pruned.
        var other = present.Single(d => !string.Equals(d, last.CacheDirectory!, StringComparison.Ordinal));
        Assert.Matches(@"^netpi-5-[0-9a-f]{12}$", Path.GetFileName(other));
    }

    [Fact]
    public async Task Scan_LoadsNewlyPublishedPlugins_AndLeavesLoadedOnesAlone()
    {
        // PLAN §50: a plugin published after startup (a fresh artifact +
        // pointer dropped into plugins/) must be discoverable by ScanAsync
        // without a host restart — and a scan must never touch the plugins
        // already loaded.
        PublishArtifact("NetPI.TestPlugin");
        await using var runtime = NewRuntime();
        await runtime.StartAsync();
        var before = runtime.Plugins.Get("NetPI.TestPlugin")!;
        Assert.Equal(1, before.Generation);

        // Publish a second copy of the test plugin under a NEW plugin id —
        // the directory name is the plugin id the host discovers.
        var (_, build2) = PublishArtifact("NetPI.TestPlugin2");
        var (loaded, perr) = PluginPublication.LoadPointer(Path.Combine(_pluginDir, "NetPI.TestPlugin2"));
        Assert.Null(perr);
        Assert.Equal(build2, loaded!.BuildId);

        // Give the second copy its own config section: register=false so its
        // service id does not collide with the first plugin's registration.
        File.WriteAllText(Path.Combine(_home, "config.json"),
            """{"plugins": {"netpi.testplugin": {"generation": "gen"}, "netpi.testplugin2": {"register": false}}}""");

        var scanned = await runtime.Plugins.ScanAsync();

        Assert.Equal(new[] { "NetPI.TestPlugin2" }, scanned);
        var fresh = runtime.Plugins.Get("NetPI.TestPlugin2")!;
        Assert.Equal(PluginState.Active, fresh.State);
        Assert.Equal(1, fresh.Generation);
        Assert.Equal(build2, fresh.BuildId);
        // the pre-existing plugin was not reloaded or unloaded
        Assert.Same(before, runtime.Plugins.Get("NetPI.TestPlugin"));
        Assert.Equal(PluginState.Active, before.State);

        // A second scan finds nothing new.
        Assert.Empty(await runtime.Plugins.ScanAsync());
    }

    [Fact]
    public async Task InvalidArtifact_SkipsPlugin_OthersStillLoad()
    {
        // A pointer whose artifact fails validation (missing file) must not
        // crash the host: the plugin is skipped (not loaded), and other plugins
        // still load normally.
        PublishArtifact("NetPI.TestPlugin");
        var (artifactBad, _) = PublishArtifact("NetPI.TestPlugin2");
        File.Delete(Path.Combine(artifactBad, "netPI.TestPlugin.dll")); // corrupt the second artifact

        await using var runtime = NewRuntime();
        await runtime.StartAsync();

        var statuses = runtime.Plugins.GetStatus().Select(s => s.PluginId).OrderBy(x => x).ToList();
        Assert.Contains("NetPI.TestPlugin", statuses);
        Assert.DoesNotContain("NetPI.TestPlugin2", statuses);
    }
}
