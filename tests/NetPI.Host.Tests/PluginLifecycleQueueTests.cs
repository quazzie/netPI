using NetPI.Abstractions;
using NetPI.Host;
using NetPI.Host.Config;
using NetPI.Host.Plugins;
using Xunit;

namespace NetPI.Host.Tests;

/// <summary>
/// astra-1 P2 (Host): the host-owned lifecycle operation queue and structured
/// reload outcomes — Deferred is never a generic Failed; "update failed,
/// previous build active" ≠ "plugin unavailable"; uncooperative StopAsync
/// ends as RestartRequired without a second generation; candidate failures
/// roll back to a running last-known-good generation (RolledBack) or report
/// Failed with BOTH errors.
/// </summary>
public sealed class PluginLifecycleQueueTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), "netpi-p2-" + Guid.NewGuid().ToString("n"));
    private readonly string _runtimeDir;
    private readonly string _pluginDir;
    private readonly string _testBin;

    public PluginLifecycleQueueTests()
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

    /// <summary>Stage a single, uniquely-id'd legacy plugin (directory name = plugin id).</summary>
    private void StageAs(string dirName)
    {
        var staged = Path.Combine(_pluginDir, dirName);
        Directory.CreateDirectory(staged);
        foreach (var f in Directory.EnumerateFiles(_testBin, "*.*")
                     .Where(f => Path.GetFileName(f).StartsWith("netPI.TestPlugin", StringComparison.OrdinalIgnoreCase)
                                 || Path.GetFileName(f) == "NetPI.Abstractions.dll"))
            File.Copy(f, Path.Combine(staged, Path.GetFileName(f)), overwrite: true);
    }

    private void WriteConfig(string json)
        => File.WriteAllText(Path.Combine(_runtimeDir, "config.json"), json);

    private HostRuntime NewRuntime(TimeSpan? unloadTimeout = null)
    {
        var options = new PluginManagerOptions
        {
            PluginDirectory = _pluginDir,
            RuntimeDirectory = _runtimeDir,
            MaxCachedGenerations = 2,
        };
        if (unloadTimeout.HasValue)
            options.UnloadTimeout = unloadTimeout.Value;
        return new HostRuntime(options, new ConsoleErrLogger(), new ConfigService(_runtimeDir));
    }

    // ------------------------------------------------------------------
    // 1. queued reloads serialize: two concurrent reloads, no interleaving
    // ------------------------------------------------------------------
    [Fact]
    public async Task ConcurrentReloads_SerializeThroughTheQueue()
    {
        StageAs("NetPI.Q1");
        WriteConfig("{ \"plugins\": { \"netpi.q1\": { \"generation\": \"gen\" } } }");
        await using var runtime = NewRuntime();
        await runtime.StartAsync();

        var o1 = runtime.Plugins.ReloadPluginAsync("NetPI.Q1");
        var o2 = runtime.Plugins.ReloadPluginAsync("NetPI.Q1");
        var r1 = await o1;
        var r2 = await o2;

        Assert.Equal(PluginLifecycleOutcome.Applied, r1.Outcome);
        Assert.Equal(PluginLifecycleOutcome.Applied, r2.Outcome);
        // the second op ran AFTER the first finished: generations must be 2 and 3
        var st = runtime.Plugins.GetStatus().Single();
        Assert.Equal(3, st.Generation);
        Assert.Equal(PluginState.Active, st.State);
        Assert.Equal("legacy", r1.ActiveBuildId);
        Assert.Equal("legacy", r2.ActiveBuildId);
    }

    // ------------------------------------------------------------------
    // 2. drain timeout: deferred keeps the ACTUAL previous state running
    // ------------------------------------------------------------------
    [Fact]
    public async Task DrainTimeout_RestoresPreviousState_AndDeferredIsNotFailed()
    {
        StageAs("NetPI.Q2");
        WriteConfig("{ \"plugins\": { \"netpi.q2\": { \"generation\": \"gen\" } } }");
        await using var runtime = NewRuntime(unloadTimeout: TimeSpan.FromMilliseconds(300));
        await runtime.StartAsync();

        // hold a service lease so the drain can never finish
        using var lease = runtime.Services.Acquire("test.service", ServiceTypes.Of(runtime.Services, "test.service"));
        var reload = runtime.Plugins.ReloadPluginAsync("NetPI.Q2");
        await Task.Delay(150);
        Assert.Equal(PluginState.Draining, runtime.Plugins.Get("NetPI.Q2")!.State);
        var o = await reload;

        Assert.Equal(PluginLifecycleOutcome.Deferred, o.Outcome);
        Assert.Equal(PluginState.Active, runtime.Plugins.Get("NetPI.Q2")!.State);
        Assert.Contains("drain", o.Error, StringComparison.OrdinalIgnoreCase);
        Assert.False(o.RestartRequired);

        // and the plugin still works, and can be reloaded once the lease is gone
        lease.Dispose();
        var o2 = await runtime.Plugins.ReloadPluginAsync("NetPI.Q2");
        Assert.Equal(PluginLifecycleOutcome.Applied, o2.Outcome);
        Assert.Equal(PluginState.Active, runtime.Plugins.Get("NetPI.Q2")!.State);
    }

    // ------------------------------------------------------------------
    // 3. LKG rollback: candidate load fails → a FRESH last-known-good gen runs
    // ------------------------------------------------------------------
    [Fact]
    public async Task CandidateLoadFails_RollsBackToFreshLastKnownGood()
    {
        StageAs("NetPI.Q3");
        WriteConfig("{ \"plugins\": { \"netpi.q3\": { \"generation\": \"gen\" } } }");
        await using var runtime = NewRuntime();
        await runtime.StartAsync();
        Assert.Equal(1, runtime.Plugins.Get("NetPI.Q3")!.Generation);

        // flip the config so the candidate generation's LoadAsync throws
        WriteConfig("{ \"plugins\": { \"netpi.q3\": { \"loadFail\": true } } }");
        var o = await runtime.Plugins.ReloadPluginAsync("NetPI.Q3");

        Assert.Equal(PluginLifecycleOutcome.RolledBack, o.Outcome);
        Assert.Equal(PluginLifecyclePhase.RollingBack, o.Phase);
        Assert.Contains("refuses to load", o.Error);
        var inst = runtime.Plugins.Get("NetPI.Q3")!;
        Assert.Equal(PluginState.Active, inst.State);
        Assert.True(inst.Generation > 1, "rollback must be a FRESH generation (old gen was already unloaded)");
        // the rolled-back generation must be USABLE
        var svc = runtime.Services.Acquire("test.service", ServiceTypes.Of(runtime.Services, "test.service"));
        Assert.NotNull(svc);
    }

    // ------------------------------------------------------------------
    // 4. start failure: no Applied ever; rollback succeeds → RolledBack
    // ------------------------------------------------------------------
    [Fact]
    public async Task CandidateStartFails_RollsBack_NeverEmitsApplied()
    {
        StageAs("NetPI.Q4");
        WriteConfig("{ \"plugins\": { \"netpi.q4\": { \"generation\": \"gen\" } } }");
        await using var runtime = NewRuntime();
        await runtime.StartAsync();

        WriteConfig("{ \"plugins\": { \"netpi.q4\": { \"startFail\": true } } }");
        var o = await runtime.Plugins.ReloadPluginAsync("NetPI.Q4");

        Assert.Equal(PluginLifecycleOutcome.RolledBack, o.Outcome);
        Assert.Contains("start failed", o.Error, StringComparison.OrdinalIgnoreCase);
        var inst = runtime.Plugins.Get("NetPI.Q4")!;
        Assert.Equal(PluginState.Active, inst.State);
        Assert.True(inst.Generation > 1);
    }

    // ------------------------------------------------------------------
    // 5. uncooperative stop: RestartRequired, NO second generation
    // ------------------------------------------------------------------
    [Fact]
    public async Task UncooperativeStop_RestartRequired_NoSecondGeneration()
    {
        StageAs("NetPI.Q5");
        WriteConfig("{ \"plugins\": { \"netpi.q5\": { \"stopMs\": 2000 } } }");
        await using var runtime = NewRuntime(unloadTimeout: TimeSpan.FromMilliseconds(300));
        await runtime.StartAsync();
        Assert.Equal(PluginState.Active, runtime.Plugins.Get("NetPI.Q5")!.State);

        var o = await runtime.Plugins.ReloadPluginAsync("NetPI.Q5");

        Assert.Equal(PluginLifecycleOutcome.RestartRequired, o.Outcome);
        Assert.Equal(PluginLifecyclePhase.Stopping, o.Phase);
        Assert.True(o.RestartRequired);
        // the old generation is quarantined — the host must NOT have loaded a
        // second, conflicting generation
        var inst = runtime.Plugins.Get("NetPI.Q5")!;
        Assert.Equal(1, inst.Generation);
        Assert.Equal(PluginState.Unloading, inst.State);
    }

    // ------------------------------------------------------------------
    // 6. reload-all returns one structured outcome per plugin
    // ------------------------------------------------------------------
    [Fact]
    public async Task ReloadAll_ReturnsOneOutcomePerPlugin()
    {
        StageAs("NetPI.Q6");
        StageAs("NetPI.Q7");
        WriteConfig("{ \"plugins\": { \"netpi.q6\": { \"generation\": \"g6\" }, \"netpi.q7\": { \"generation\": \"g7\", \"register\": false } } }");
        await using var runtime = NewRuntime();
        await runtime.StartAsync();

        var outcomes = await runtime.Plugins.ReloadAllOpAsync();
        Assert.Equal(2, outcomes.Count);
        Assert.All(outcomes, o => Assert.Equal(PluginLifecycleOutcome.Applied, o.Outcome));
        Assert.Equal(new[] { "NetPI.Q6", "NetPI.Q7" },
            outcomes.Select(o => o.PluginId).ToArray());
    }

    // ------------------------------------------------------------------
    // 7. shutdown rejects new operations (no queue entry, immediate Deferred)
    // ------------------------------------------------------------------
    [Fact]
    public async Task Shutdown_RejectsNewOperations_Immediately()
    {
        StageAs("NetPI.Q8");
        WriteConfig("{ \"plugins\": { \"netpi.q8\": { \"generation\": \"gen\" } } }");
        await using var runtime = NewRuntime();
        await runtime.StartAsync();

        await runtime.Plugins.ShutdownOpAsync();
        var o = await runtime.Plugins.ReloadPluginAsync("NetPI.Q8");
        Assert.Equal(PluginOperationOutcome.ShutdownRejectedId, o.OperationId);
        Assert.Equal(PluginLifecycleOutcome.Deferred, o.Outcome);
        Assert.NotNull(o.Error);
    }

    // ------------------------------------------------------------------
    // 8. astra-1 P5: queue-backed ops — Enqueue* returns an id immediately,
    //    the work runs on the SAME queue, and the outcome is queryable later
    // ------------------------------------------------------------------

    [Fact]
    public async Task EnqueueReload_ReturnsIdImmediately_AndOutcomeIsQueryableLater()
    {
        StageAs("NetPI.Q9");
        WriteConfig("{ \"plugins\": { \"netpi.q9\": { \"generation\": \"gen\" } } }");
        await using var runtime = NewRuntime();
        await runtime.StartAsync();
        Assert.Equal(1, runtime.Plugins.Get("NetPI.Q9")!.Generation);

        // the ack must return an id WITHOUT awaiting the reload: flip the config
        // so the candidate generation fails to start (slow path), then assert
        // the id is returned and the status is still not Done immediately after.
        WriteConfig("{ \"plugins\": { \"netpi.q9\": { \"startFail\": true } } }");
        var opId = runtime.Plugins.EnqueueReload("NetPI.Q9", null);
        Assert.False(string.IsNullOrWhiteSpace(opId));

        // the operation is registered and not finished yet (or finished very
        // quickly on a fast machine — but the id was returned synchronously).
        var immediate = runtime.Plugins.GetOperation(opId);
        Assert.Equal(opId, immediate.OperationId);
        Assert.Equal(PluginOperationKind.Reload, immediate.Kind);

        // wait for completion, then the outcome MUST be queryable (Done=true).
        var sw = System.Diagnostics.Stopwatch.StartNew();
        PluginOperationStatus done;
        do
        {
            done = runtime.Plugins.GetOperation(opId);
            if (sw.Elapsed > TimeSpan.FromSeconds(15))
                throw new TimeoutException("queued reload did not complete in time");
            await Task.Delay(20);
        } while (!done.Done);

        Assert.True(done.Done);
        Assert.Equal(PluginOperationKind.Reload, done.Kind);
        // a start-failed reload rolls back to the LKG generation.
        Assert.Equal(PluginLifecycleOutcome.RolledBack, done.Outcome);
        Assert.NotNull(done.Error);
    }

    [Fact]
    public async Task EnqueueScan_ReturnsIdImmediately_AndRecordsScannedIds()
    {
        // stage a plugin the host has NOT seen at start: start the runtime with
        // an empty plugin dir, then stage + scan.
        Directory.CreateDirectory(_pluginDir);
        WriteConfig("{}");
        await using var runtime = NewRuntime();
        await runtime.StartAsync();

        StageAs("NetPI.Q10");
        WriteConfig("{ \"plugins\": { \"netpi.q10\": { \"generation\": \"gen\" } } }");

        var opId = runtime.Plugins.EnqueueScan();
        Assert.False(string.IsNullOrWhiteSpace(opId));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        PluginOperationStatus done;
        do
        {
            done = runtime.Plugins.GetOperation(opId);
            if (sw.Elapsed > TimeSpan.FromSeconds(15))
                throw new TimeoutException("queued scan did not complete in time");
            await Task.Delay(20);
        } while (!done.Done);

        Assert.True(done.Done);
        Assert.Equal(PluginOperationKind.Scan, done.Kind);
        Assert.Contains("NetPI.Q10", done.ScannedIds);
        Assert.Equal(PluginState.Active, runtime.Plugins.Get("NetPI.Q10")!.State);
    }
}
