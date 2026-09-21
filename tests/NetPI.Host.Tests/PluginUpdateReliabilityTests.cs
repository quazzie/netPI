using NetPI.Abstractions;
using NetPI.Host;
using NetPI.Host.Config;
using NetPI.Host.Plugins;
using NetPI.Host.Services;
using Xunit;

namespace NetPI.Host.Tests;

/// <summary>
/// astra-1 P6 — verification before calling plugin updates reliable. The
/// existing suites prove snapshot copying/reload (PluginHotSwapTests),
/// structured queue outcomes (PluginLifecycleQueueTests), shutdown order
/// (PluginShutdownOrderTests), partial-load rollback (PluginPartialFailureTests)
/// and store non-disposal at the unit level (StoreReloadTests). These tests
/// close the end-to-end gaps:
///
///   1. a plugin staged AFTER host startup loads+starts on a scan, a failed
///      new plugin is NOT reported as loaded and never blocks the healthy
///      one (a fresh retry afterwards works);
///   2. reload-all with MIXED plugin states returns one structured outcome
///      per plugin, each with its own honest result;
///   3. a "sessions" consumer keeps working after the storage PROVIDER
///      plugin is reloaded end-to-end through a real host ALC (lazy
///      re-resolution, stores not disposed in StopAsync);
///   4. when a generation is unloaded, its event-bus subscriptions, slash
///      commands and web-panel registrations all return to baseline;
///   5. a plugin that holds a SELF-lease defers its reload (drain) and the
///      reload completes once the lease releases.
///
/// Every test uses its own isolated temp runtime home — ~/.netpi is never
/// touched.
/// </summary>
public sealed class PluginUpdateReliabilityTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), "netpi-p6-" + Guid.NewGuid().ToString("n"));
    private readonly string _runtimeDir;
    private readonly string _pluginDir;
    private readonly string _testBin;

    public PluginUpdateReliabilityTests()
    {
        _runtimeDir = Path.Combine(_home, "runtime");
        _pluginDir = Path.Combine(_home, "plugins");
        Directory.CreateDirectory(_runtimeDir);
        Directory.CreateDirectory(_pluginDir);

        var testAssemblyBin = Path.GetDirectoryName(typeof(NetPI.TestPlugin.TestPlugin).Assembly.Location)!;
        var cfg = Path.GetFileName(Path.GetDirectoryName(testAssemblyBin)!);
        var tfm = Path.GetFileName(testAssemblyBin);
        string? root = testAssemblyBin;
        while (root is not null && !File.Exists(Path.Combine(root, "NetPI.sln")))
            root = Path.GetDirectoryName(root);
        _testBin = Path.Combine(root!, "plugins", "NetPI.TestPlugin", "bin", cfg, tfm);
        Assert.True(Directory.Exists(_testBin), $"TestPlugin build output not found: {_testBin}");
    }

    public void Dispose()
    {
        try { Directory.Delete(_home, recursive: true); } catch { }
    }

    /// <summary>Stage a copy of the TestPlugin fixture under a unique plugin id (directory name = id).</summary>
    private void StagePlugin(string id)
    {
        var staged = Path.Combine(_pluginDir, id);
        Directory.CreateDirectory(staged);
        foreach (var f in Directory.EnumerateFiles(_testBin, "*.*")
                     .Where(f => Path.GetFileName(f).StartsWith("netPI.TestPlugin", StringComparison.OrdinalIgnoreCase)
                                 || Path.GetFileName(f) == "NetPI.Abstractions.dll"
                                 || Path.GetFileName(f) == "NetPI.Abstractions.pdb"))
            File.Copy(f, Path.Combine(staged, Path.GetFileName(f)), overwrite: true);
    }

    private void WriteConfig(string json) =>
        File.WriteAllText(Path.Combine(_runtimeDir, "config.json"), json);

    private HostRuntime NewRuntime() => new(
        new PluginManagerOptions
        {
            PluginDirectory = _pluginDir,
            RuntimeDirectory = _runtimeDir,
            MaxCachedGenerations = 2,
        },
        new ConsoleErrLogger(),
        new ConfigService(_runtimeDir));

    // ------------------------------------------------------------------
    // 1. scan: a plugin staged AFTER host startup loads+starts; a failed
    //    new plugin is not reported as loaded and does not block others
    // ------------------------------------------------------------------
    [Fact]
    public async Task Scan_LoadsNewlyStagedPlugin_FailedPluginNotReported_OthersUnaffected()
    {
        // Start the host with NO plugins at all (the host "has never seen"
        // either id), then stage them afterwards — exactly the P6 scenario.
        // S02 opts out of service registration: both stages are copies of the
        // same fixture, whose "test.service" id would collide in the registry.
        WriteConfig("{\"plugins\":{\"netpi.s01\":{\"generation\":\"s01\"},\"netpi.s02\":{\"startFail\":true,\"generation\":\"s02\",\"register\":false}}}");
        await using var runtime = NewRuntime();
        await runtime.StartAsync();
        Assert.Empty(runtime.Plugins.GetStatus());

        StagePlugin("NetPI.S01"); // healthy
        StagePlugin("NetPI.S02"); // fails in StartAsync

        var scanned = await runtime.Plugins.ScanAsync();

        // The healthy plugin loads + starts from the scan (no host restart) ...
        Assert.Equal(new[] { "NetPI.S01" }, scanned);
        var s01 = runtime.Plugins.Get("NetPI.S01")!;
        Assert.Equal(PluginState.Active, s01.State);
        Assert.Equal(1, s01.Generation);

        // ...and the failed one is NOT reported as loaded: it is left
        // Failed (retryable), and the healthy one is fully functional.
        Assert.DoesNotContain("NetPI.S02", scanned);
        var s02 = runtime.Plugins.Get("NetPI.S02")!;
        Assert.Equal(PluginState.Failed, s02.State);
        var snap = runtime.Services.Snapshot().ToList();
        Assert.Single(snap, s => s.PluginId == "NetPI.S01");

        // A fresh retry after the config is fixed works (retry path).
        WriteConfig("{\"plugins\":{\"netpi.s01\":{\"generation\":\"s01\"},\"netpi.s02\":{\"generation\":\"s02\",\"register\":false}}}");
        var retry = await runtime.Plugins.ReloadPluginAsync("NetPI.S02");
        Assert.Equal(PluginLifecycleOutcome.Applied, retry.Outcome);
        Assert.Equal(PluginState.Active, runtime.Plugins.Get("NetPI.S02")!.State);
    }

    // ------------------------------------------------------------------
    // 2. reload-all with mixed states: one structured outcome per plugin
    // ------------------------------------------------------------------
    [Fact]
    public async Task ReloadAll_MixedStates_ReturnsOneStructuredOutcomePerPlugin()
    {
        // Two healthy plugins with distinct start order (A00 starts first,
        // Z99 second). At reload time one applies cleanly, the other fails
        // its candidate and rolls back to a fresh LKG generation.
        StagePlugin("NetPI.A00");
        StagePlugin("NetPI.Z99");
        // Z99 opts out of service registration ("test.service" belongs to A00).
        WriteConfig("{\"plugins\":{\"netpi.a00\":{\"generation\":\"g-a\"},\"netpi.z99\":{\"generation\":\"g-z\",\"register\":false}}}");
        await using var runtime = NewRuntime();
        await runtime.StartAsync();
        Assert.Equal(PluginState.Active, runtime.Plugins.Get("NetPI.A00")!.State);
        Assert.Equal(PluginState.Active, runtime.Plugins.Get("NetPI.Z99")!.State);

        // Flip Z99's config so ITS candidate fails to load.
        WriteConfig("{\"plugins\":{\"netpi.a00\":{\"generation\":\"g-a\"},\"netpi.z99\":{\"generation\":\"g-z\",\"register\":false,\"loadFail\":true}}}");

        var outcomes = await runtime.Plugins.ReloadAllOpAsync();

        // ONE outcome per plugin — no count, no aggregate hiding a failure.
        Assert.Equal(2, outcomes.Count);
        var a00 = Assert.Single(outcomes, o => o.PluginId == "NetPI.A00");
        var z99 = Assert.Single(outcomes, o => o.PluginId == "NetPI.Z99");

        // Each carries its own honest result ...
        Assert.Equal(PluginLifecycleOutcome.Applied, a00.Outcome);
        Assert.Null(a00.Error);
        Assert.False(a00.RestartRequired);

        Assert.Equal(PluginLifecycleOutcome.RolledBack, z99.Outcome);
        Assert.Contains("refuses to load", z99.Error, StringComparison.OrdinalIgnoreCase);
        Assert.False(z99.RestartRequired);

        // ...and both plugins end up Active: A00 on the requested build,
        // Z99 on a FRESH LKG generation (usable, not a phantom).
        var aRow = runtime.Plugins.GetStatus().Single(s => s.PluginId == "NetPI.A00");
        var zRow = runtime.Plugins.GetStatus().Single(s => s.PluginId == "NetPI.Z99");
        Assert.Equal(PluginState.Active, aRow.State);
        Assert.Equal(PluginState.Active, zRow.State);
        Assert.True(zRow.Generation > 1);
        // A00 (the Applied plugin) owns "test.service" — still live after the swap.
        using var aSvc = runtime.Services.Acquire("test.service", ServiceTypes.Of(runtime.Services, "test.service"));
        Assert.NotNull(aSvc.Value);

        // Reverse-start order is preserved even with mixed states: Z99
        // started last, so it is reloaded FIRST.
        Assert.Equal(new[] { "NetPI.Z99", "NetPI.A00" },
            outcomes.Select(o => o.PluginId).ToArray());
    }

    // ------------------------------------------------------------------
    // 3. storage reload safety: a "sessions" consumer survives a reload of
    //    the provider plugin (real host ALC end-to-end)
    // ------------------------------------------------------------------
    [Fact]
    public async Task SessionsConsumer_KeepsWorkingAfterStorageProviderReload()
    {
        // Stage the real storage plugin from the repo's plugin folder: managed
        // DLLs at the snapshot top level + the e_sqlite3 native RID assets under
        // runtimes/ (the ALC's resolver probes both). Build/source trees, the
        // current.json pointer and metadata are excluded (legacy staging path —
        // a pointer here would redirect the host at a stale .artifacts build).
        var pluginsRoot = Directory.GetParent(_testBin)!.Parent!.Parent!.Parent!; // plugins/
        var storageSrc = Path.Combine(pluginsRoot.FullName, "NetPI.Storage.Sqlite");
        Assert.True(Directory.Exists(storageSrc), $"storage plugin source not found: {storageSrc}");
        var staged = Path.Combine(_pluginDir, "NetPI.Storage.Sqlite");
        Directory.CreateDirectory(staged);
        foreach (var f in Directory.EnumerateFiles(storageSrc, "*", SearchOption.AllDirectories)
                     .Where(f => !f.StartsWith(Path.Combine(storageSrc, "bin"), StringComparison.OrdinalIgnoreCase)
                                 && !f.StartsWith(Path.Combine(storageSrc, "obj"), StringComparison.OrdinalIgnoreCase)
                                 && !f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                                 && !f.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
                                 && !f.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
                                 && !f.EndsWith(".deps.json", StringComparison.OrdinalIgnoreCase)
                                 && !f.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase)))
        {
            var name = Path.GetFileName(f);
            if (name.Equals("artifact.json", StringComparison.OrdinalIgnoreCase)
                || name.Equals("current.json", StringComparison.OrdinalIgnoreCase))
                continue;
            var target = Path.Combine(staged, Path.GetRelativePath(storageSrc, f));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(f, target, overwrite: true);
        }

        var dbPath = Path.Combine(_home, "test-netpi.db");
        WriteConfig("{\"plugins\":{\"netpi.storage.sqlite\":{\"database\":\"" + dbPath + "\"}}}");
        await using var runtime = NewRuntime();
        await runtime.StartAsync();
        Assert.Equal(PluginState.Active,
            runtime.Plugins.GetStatus().Single(s => s.PluginId == "NetPI.Storage.Sqlite").State);

        // A consumer (as Web/Diagnostics/the runner would): resolve the
        // store ONCE from the registry and keep it across the provider's
        // reload.
        var sessionsType = runtime.Services.Snapshot().Single(r => r.Id == "sessions").ServiceType;
        using var consumerStore = runtime.Services.Acquire("sessions", sessionsType);
        Assert.NotNull(consumerStore.Value);
        var created = await ((NetPI.Storage.Sqlite.SqliteSessionStore)consumerStore.Value)
            .CreateAsync("/ws", CancellationToken.None);
        Assert.False(string.IsNullOrEmpty(created.Id));
        Assert.Equal(1, await ((NetPI.Storage.Sqlite.SqliteSessionStore)consumerStore.Value).CountAsync(CancellationToken.None));

        // Reload the PROVIDER plugin. The old generation stops (and must
        // NOT dispose its store) and a new generation registers a fresh
        // store under the same "sessions" id.
        var o = await runtime.Plugins.ReloadPluginAsync("NetPI.Storage.Sqlite");
        Assert.Equal(PluginLifecycleOutcome.Applied, o.Outcome);
        Assert.Equal(2, runtime.Plugins.Get("NetPI.Storage.Sqlite")!.Generation);

        // The consumer — holding the GEN-1 store reference — still works:
        // reads and writes round-trip against a live connection.
        Assert.Equal(1, await ((NetPI.Storage.Sqlite.SqliteSessionStore)consumerStore.Value).CountAsync(CancellationToken.None));
        await ((NetPI.Storage.Sqlite.SqliteSessionStore)consumerStore.Value).CreateAsync("/ws", CancellationToken.None);
        Assert.Equal(2, await ((NetPI.Storage.Sqlite.SqliteSessionStore)consumerStore.Value).CountAsync(CancellationToken.None));

        // Lazy re-resolution (what consumers do after the swap): the id now
        // resolves to a DIFFERENT store instance owned by the new generation,
        // and it sees the same durable data.
        var freshType = runtime.Services.Snapshot().Single(r => r.Id == "sessions").ServiceType;
        using var fresh = runtime.Services.Acquire("sessions", freshType);
        Assert.NotSame(consumerStore.Value, fresh.Value);
        Assert.Equal(2, await ((NetPI.Storage.Sqlite.SqliteSessionStore)fresh.Value).CountAsync(CancellationToken.None));
    }

    // ------------------------------------------------------------------
    // 4. unload leak checks: bus subscriptions, commands and web-panel
    //    registrations all return to baseline after a generation unloads
    // ------------------------------------------------------------------
    [Fact]
    public async Task Unload_RemovesCommandsAndPanels_ReturnsBusToBaseline()
    {
        StagePlugin("NetPI.L01");
        WriteConfig("{\"plugins\":{\"netpi.l01\":{\"generation\":\"g1\",\"registerCommands\":\"true\",\"registerPanel\":\"true\"}}}");
        await using var runtime = NewRuntime();
        await runtime.StartAsync();

        // Baseline: host registers only "commands"; the plugin adds one
        // service, one bus subscription, one command and one panel.
        Assert.Equal(1, runtime.Events.SubscriptionCount());
        Assert.Contains("/testplugin", runtime.Commands.All().Select(c => c.Name));
        var panels = runtime.WebPanels.All().Select(p => p.Id).ToList();
        Assert.Contains("testpanel", panels);
        Assert.Equal(1, runtime.Services.Snapshot().Count(s => s.PluginId == "NetPI.L01"));

        var fresh = await runtime.Plugins.ReloadAsync("NetPI.L01");
        Assert.NotNull(fresh);
        Assert.Equal(PluginState.Active, fresh.State);

        // The old generation is gone: the host-side teardown removed its
        // registrations, and the new generation re-added exactly one of each.
        Assert.Equal(1, runtime.Events.SubscriptionCount());
        Assert.Single(runtime.Commands.All().Where(c => c.Name == "/testplugin"));
        Assert.Single(runtime.WebPanels.All().Where(p => p.Id == "testpanel"));
        Assert.Equal(1, runtime.Services.Snapshot().Count(s => s.PluginId == "NetPI.L01"));

        // Shutdown removes the LAST generation too — every generation
        // registration returns to the host-only baseline.
        await runtime.ShutdownAsync();
        Assert.Equal(0, runtime.Events.SubscriptionCount());
        Assert.DoesNotContain("/testplugin", runtime.Commands.All().Select(c => c.Name));
        Assert.DoesNotContain("testpanel", runtime.WebPanels.All().Select(p => p.Id));
        Assert.Equal(0, runtime.Services.Snapshot().Count(s => s.PluginId == "NetPI.L01"));
    }

    // ------------------------------------------------------------------
    // 5. deferred reload: a generation holding a self-lease defers (drain),
    //    the reload completes once the lease releases
    // ------------------------------------------------------------------
    [Fact]
    public async Task SelfLease_DefersReloadUntilReleased_ThenApplied()
    {
        StagePlugin("NetPI.D01");
        WriteConfig("{\"plugins\":{\"netpi.d01\":{\"generation\":\"gen\"}}}");
        await using var runtime = NewRuntime();
        await runtime.StartAsync();
        Assert.Equal(PluginState.Active, runtime.Plugins.Get("NetPI.D01")!.State);
        Assert.Equal(0, runtime.Plugins.Get("NetPI.D01")!.LeasesHeld);

        // A self-lease on the generation (PLAN §44: the agent holds it for
        // the run's duration). The drain must wait for it.
        using var selfLease = runtime.Plugins.Get("NetPI.D01")!.AcquireSelfLease();
        Assert.Equal(1, runtime.Plugins.Get("NetPI.D01")!.LeasesHeld);

        var reload = runtime.Plugins.ReloadPluginAsync("NetPI.D01");
        await Task.Delay(250);
        // The op is still draining: the generation is stuck in Draining
        // (lease held) and no second generation was loaded.
        Assert.False(reload.IsCompleted, "reload should still be draining while the self-lease is held");
        var mid = runtime.Plugins.Get("NetPI.D01")!;
        Assert.Equal(PluginState.Draining, mid.State, "drain must not finish while the self-lease is held");
        Assert.Equal(1, mid.Generation);

        // Release the lease -> drain unblocks -> the reload completes.
        selfLease.Dispose();
        var o = await reload;
        Assert.Equal(PluginLifecycleOutcome.Applied, o.Outcome);
        var fresh = runtime.Plugins.Get("NetPI.D01")!;
        Assert.Equal(PluginState.Active, fresh.State);
        Assert.Equal(2, fresh.Generation);
        Assert.Equal(0, fresh.LeasesHeld);
        using var svc = runtime.Services.Acquire("test.service", ServiceTypes.Of(runtime.Services, "test.service"));
        Assert.NotNull(svc.Value);
    }
}
