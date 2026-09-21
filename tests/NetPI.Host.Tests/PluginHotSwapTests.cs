using NetPI.Abstractions;
using NetPI.Host;
using NetPI.Host.Config;
using NetPI.Host.Plugins;
using TestPluginClass = NetPI.TestPlugin.TestPlugin;
using Xunit;

namespace NetPI.Host.Tests;

/// <summary>
/// Regression for the plugin hot-swap problem: while a generation is active its
/// DLLs were mapped from the staged folder (<c>plugins/&lt;name&gt;/</c>), which
/// on Windows means the host held file locks on the exact folder a new build
/// stages over — "the DLL is locked" and no hot-swap.
///
/// The host must instead always load from an immutable per-generation snapshot
/// (<c>~/.netpi/plugin-cache/&lt;plugin&gt;/&lt;gen&gt;/</c>); the staged folder is
/// then free to be rewritten at any time, and a reload snapshots the new
/// bytes. These tests prove:
///   1. the running generation is mapped from a cache snapshot, not the source;
///   2. the staged folder can be overwritten while gen 1 is active, gen 1 keeps
///      its original bytes, and a reload picks up the new bytes as gen 2;
///   3. stale snapshot directories are pruned to the configured count once
///      their ALCs are finalized.
/// </summary>
public sealed class PluginHotSwapTests : IAsyncLifetime
{
    private string _home = null!;
    private string _pluginDir = null!;
    private string _stagedDll = null!;

    public async Task InitializeAsync()
    {
        _home = Path.Combine(Path.GetTempPath(), "netpi-hotswap-" + Guid.NewGuid().ToString("n"));
        _pluginDir = Path.Combine(_home, "plugins");
        Directory.CreateDirectory(_pluginDir);

        // Stage the TestPlugin build output under the plugin directory, the
        // same way the publish script stages real plugins.
        var testBin = Path.GetDirectoryName(typeof(TestPluginClass).Assembly.Location)!;
        var cfg = Path.GetFileName(Path.GetDirectoryName(testBin)!);
        string? root = testBin;
        while (root is not null && !File.Exists(Path.Combine(root, "NetPI.sln")))
            root = Path.GetDirectoryName(root);
        Assert.True(root is not null, "repo root with NetPI.sln not found");
        var pluginOut = Path.Combine(root, "plugins", "NetPI.TestPlugin", "bin", cfg, "net10.0");
        Assert.True(Directory.Exists(pluginOut), $"plugin build output not found: {pluginOut}");

        var staged = Path.Combine(_pluginDir, "NetPI.TestPlugin");
        Directory.CreateDirectory(staged);
        foreach (var f in Directory.EnumerateFiles(pluginOut))
            File.Copy(f, Path.Combine(staged, Path.GetFileName(f)), overwrite: true);
        _stagedDll = Path.Combine(staged, "netPI.TestPlugin.dll");
        return;
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

    [Fact]
    public async Task LoadsFromSnapshot_NotFromStagedFolder()
    {
        await using var runtime = NewRuntime();
        await runtime.StartAsync();

        var p = Assert.Single(runtime.Plugins.GetStatus());
        Assert.Equal(1, p.Generation);

        var inst = runtime.Plugins.Get("NetPI.TestPlugin")!;
        Assert.NotNull(inst.CacheDirectory);
        Assert.EndsWith(Path.Combine("plugin-cache", "NetPI.TestPlugin", "1"), inst.CacheDirectory!.TrimEnd(Path.DirectorySeparatorChar));

        // The snapshot must contain the loaded assembly and be a COPY of the
        // staged file (different paths, same bytes).
        var snapDll = Path.Combine(inst.CacheDirectory, "netPI.TestPlugin.dll");
        Assert.True(File.Exists(snapDll));
        Assert.Equal(File.ReadAllBytes(_stagedDll), File.ReadAllBytes(snapDll));
    }

    [Fact]
    public async Task StagedFolderCanBeRewrittenWhileGenerationIsActive_ReloadPicksUpNewBytes()
    {
        await using var runtime = NewRuntime();
        await runtime.StartAsync();

        var inst1 = runtime.Plugins.Get("NetPI.TestPlugin")!;
        var snapDll1 = Path.Combine(inst1.CacheDirectory!, "netPI.TestPlugin.dll");
        var originalBytes = File.ReadAllBytes(_stagedDll);
        Assert.Equal(originalBytes, File.ReadAllBytes(snapDll1));

        // Rewrite the staged folder — exactly what a new build + publish does.
        // (This used to fail while the generation was live, because the host
        // had gen 1's ALC mapped from these very files.)
        var newBytes = new byte[originalBytes.Length + 1]; // append one byte: same valid PE, different bytes
        Array.Copy(originalBytes, newBytes, originalBytes.Length);
        File.WriteAllBytes(_stagedDll, newBytes);

        var fresh = await runtime.Plugins.ReloadAsync("NetPI.TestPlugin");
        Assert.NotNull(fresh);
        Assert.Equal(2, fresh.Generation);
        Assert.Equal(PluginState.Active, fresh.State);

        // Gen 2's snapshot must carry the NEW bytes...
        var snapDll2 = Path.Combine(fresh.CacheDirectory!, "netPI.TestPlugin.dll");
        Assert.Equal(newBytes, File.ReadAllBytes(snapDll2));
        // ...while gen 1's snapshot is untouched (immutable once staged).
        Assert.Equal(originalBytes, File.ReadAllBytes(snapDll1));
    }

    [Fact]
    public async Task StaleSnapshotsArePrunedToMaxCachedGenerations()
    {
        await using var runtime = NewRuntime();
        await runtime.StartAsync();
        // Four reloads: generations 2..5; each staging sweep keeps the
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

        // One more staging: the sweep now finds every stale snapshot
        // un-locked and deletes it.
        var last = await runtime.Plugins.ReloadAsync("NetPI.TestPlugin");
        Assert.NotNull(last);
        Assert.Equal(6, last.Generation);

        var cacheRoot = Path.Combine(_home, "plugin-cache", "NetPI.TestPlugin");
        var present = Directory.EnumerateDirectories(cacheRoot)
            .Select(d => int.Parse(Path.GetFileName(d)))
            .ToList();
        // The two most recent snapshots survive ...
        Assert.Contains(5, present);
        Assert.Contains(6, present);
        // ...and everything older is pruned.
        Assert.DoesNotContain(1, present);
        Assert.DoesNotContain(2, present);
        Assert.DoesNotContain(3, present);
        Assert.DoesNotContain(4, present);

    }

    [Fact]
    public async Task Scan_LoadsNewlyStagedPlugins_AndLeavesLoadedOnesAlone()
    {
        // PLAN §50: a plugin folder staged after startup (a fresh folder
        // dropped into plugins/) must be discoverable by ScanAsync without a
        // host restart — and a scan must never touch the plugins already loaded.
        await using var runtime = NewRuntime();
        await runtime.StartAsync();
        var before = runtime.Plugins.Get("NetPI.TestPlugin")!;
        Assert.Equal(1, before.Generation);

        // Stage a second copy of the test plugin under a NEW directory name —
        // the directory name is the plugin id the host discovers.
        var staged2 = Path.Combine(_pluginDir, "NetPI.TestPlugin2");
        Directory.CreateDirectory(staged2);
        foreach (var f in Directory.EnumerateFiles(Path.GetDirectoryName(_stagedDll)!))
            File.Copy(f, Path.Combine(staged2, Path.GetFileName(f)), overwrite: true);

        // Give the second copy its own config section: register=false so its
        // service id does not collide with the first plugin's registration.
        File.WriteAllText(Path.Combine(_home, "config.json"),
            @"{""plugins"": { ""netpi.testplugin"": { ""generation"": ""gen"" },
              ""netpi.testplugin2"": { ""register"": false } }}");

        var scanned = await runtime.Plugins.ScanAsync();

        Assert.Equal(new[] { "NetPI.TestPlugin2" }, scanned);
        var fresh = runtime.Plugins.Get("NetPI.TestPlugin2")!;
        Assert.Equal(PluginState.Active, fresh.State);
        Assert.Equal(1, fresh.Generation);
        // the pre-existing plugin was not reloaded or unloaded
        Assert.Same(before, runtime.Plugins.Get("NetPI.TestPlugin"));
        Assert.Equal(PluginState.Active, before.State);

        // A second scan finds nothing new.
        Assert.Empty(await runtime.Plugins.ScanAsync());
    }
}
