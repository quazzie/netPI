using System.Text.Json;
using Microsoft.Extensions.Logging;
using NetPI.Abstractions;
using NetPI.Host;
using NetPI.Host.Config;
using NetPI.Host.Plugins;
using NetPI.Host.Services;
using TestPluginClass = NetPI.TestPlugin.TestPlugin;
using Xunit;

namespace NetPI.Host.Tests;

/// <summary>
/// Resolves the service type exactly as the host registry stored it (the
/// type loaded inside the plugin's collectible ALC — a different Type object
/// than the compile-time reference in the test assembly).
/// </summary>
internal static class ServiceTypes
{
    public static Type Of(ServiceRegistry services, string id)
    {
        var reg = services.Snapshot().Single(r => r.Id == id);
        return reg.ServiceType;
    }
}


/// <summary>
/// Host-side runtime tests (PLAN §50): loading, leasing, reload-with-drain,
/// subscription removal on unload, ALC collectibility, and config parsing.
/// </summary>
public class HostRuntimeTests : IAsyncLifetime
{
    private string _home = null!;
    private string _pluginDir = null!;

    public async Task InitializeAsync()
    {
        _home = Path.Combine(Path.GetTempPath(), "netpi-test-" + Guid.NewGuid().ToString("n"));
        _pluginDir = Path.Combine(_home, "plugins");
        Directory.CreateDirectory(_pluginDir);

        // Locate the TestPlugin project's own build output and stage it under
        // the plugin directory. The test references TestPlugin, so its bin has
        // a copy of the assembly; walk up to the repo root (NetPI.sln) and into
        // the plugin's output folder.
        var testBin = Path.GetDirectoryName(typeof(TestPluginClass).Assembly.Location)!;
        var cfg = Path.GetFileName(Path.GetDirectoryName(testBin)!); // e.g. "Debug"
        string? root = testBin;
        while (root is not null && !File.Exists(Path.Combine(root, "NetPI.sln")))
            root = Path.GetDirectoryName(root);
        Assert.True(root is not null, "repo root with NetPI.sln not found");
        var pluginOut = Path.Combine(root!, "plugins", "NetPI.TestPlugin", "bin", cfg, "net10.0");
        Assert.True(Directory.Exists(pluginOut), $"plugin build output not found: {pluginOut}");

        var staged = Path.Combine(_pluginDir, "NetPI.TestPlugin");
        Directory.CreateDirectory(staged);
        foreach (var f in Directory.EnumerateFiles(pluginOut))
            File.Copy(f, Path.Combine(staged, Path.GetFileName(f)), overwrite: true);

        await Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        try { Directory.Delete(_home, recursive: true); } catch { }
        await Task.CompletedTask;
    }

    /// <summary>Build a HostRuntime rooted in the temp home, bypassing the cache copy.</summary>
    private HostRuntime NewRuntime()
    {
        var options = new PluginManagerOptions
        {
            PluginDirectory = _pluginDir,
            RuntimeDirectory = _home,
            SkipCacheCopy = true,
        };
        var config = new ConfigService(_home);
        return new HostRuntime(options, new ConsoleErrLogger(), config);
    }

    /// <summary>Acquire the test service cross-ALC (the type lives in the plugin ALC).</summary>
    private static IValueLease<object> AcquireTestService(ServiceRegistry services) =>
        services.Acquire("test.service", ServiceTypes.Of(services, "test.service"));


    [Fact]
    public async Task LoadsTestPluginAndExposesService()
    {
        await using var runtime = NewRuntime();
        await runtime.StartAsync();

        var row = Assert.Single(runtime.Plugins.GetStatus());
        Assert.Equal("NetPI.TestPlugin", row.PluginId);
        Assert.Equal(PluginState.Active, row.State);
        Assert.Equal(1, row.RegistrationCount);

        await using var lease = AcquireTestService(runtime.Services);
        Assert.NotNull(lease.Value);
    }

    [Fact]
    public async Task ReloadWaitsForLeaseDrainThenSwapsGeneration()
    {
        await using var runtime = NewRuntime();
        await runtime.StartAsync();

        var oldGen = runtime.Plugins.GetStatus().Single().Generation;
        await using var lease = AcquireTestService(runtime.Services);
        Assert.NotNull(lease.Value);

        // Hold the lease on a background task; the reload must not complete until released.
        var cts = new CancellationTokenSource();
        var reload = Task.Run(async () =>
        {
            var fresh = await runtime.Plugins.ReloadAsync("NetPI.TestPlugin", cts.Token);
            return fresh;
        });

        // Give the reload a moment to reach the drain wait (lease still held).
        await Task.Delay(300);
        var midStatus = runtime.Plugins.GetStatus().Single();
        Assert.Equal(PluginState.Draining, midStatus.State);
        Assert.False(reload.IsCompleted, "reload should be blocked on the lease");

        // Release the lease → drain unblocks → reload completes.
        await lease.DisposeAsync();
        var fresh = await Task.WhenAny(reload, Task.Delay(TimeSpan.FromSeconds(15))) == reload
            ? await reload
            : null;

        Assert.NotNull(fresh);
        Assert.NotEqual(oldGen, fresh.Generation);
        Assert.Equal(PluginState.Active, fresh.State);
    }

    [Fact]
    public async Task ReloadSwapsGenerationWhenIdle()
    {
        await using var runtime = NewRuntime();
        await runtime.StartAsync();

        var oldGen = runtime.Plugins.GetStatus().Single().Generation;
        var fresh = await runtime.Plugins.ReloadAsync("NetPI.TestPlugin");

        Assert.NotNull(fresh);
        Assert.Equal(oldGen + 1, fresh.Generation);
        Assert.Equal(PluginState.Active, fresh.State);

        // The old ALC should be reported as a leak candidate; force GC and check.
        for (int i = 0; i < 40 && runtime.Plugins.UnloadedAlocs().Any(a => !a.Collected); i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            await Task.Delay(50);
        }
        Assert.All(runtime.Plugins.UnloadedAlocs(), a => Assert.True(a.Collected, "old ALC should be collectible"));
    }

    [Fact]
    public async Task ReloadRepeatedly_EachOldGenerationBecomesCollectible()
    {
        // PLAN §50 (Host): "reload repeatedly / verify old ALC collectible".
        await using var runtime = NewRuntime();
        await runtime.StartAsync();

        const int rounds = 3;
        var gen = runtime.Plugins.GetStatus().Single().Generation;
        for (var i = 0; i < rounds; i++)
        {
            var fresh = await runtime.Plugins.ReloadAsync("NetPI.TestPlugin");
            Assert.NotNull(fresh);
            gen++;
            Assert.Equal(gen, fresh!.Generation);
        }

        // Give the GC a chance, then every unloaded generation must be
        // collectible (no ALC leak across repeated reloads).
        for (int i = 0; i < 60 && runtime.Plugins.UnloadedAlocs().Any(a => !a.Collected); i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            await Task.Delay(50);
        }
        var unloaded = runtime.Plugins.UnloadedAlocs();
        Assert.True(unloaded.Count >= rounds,
            "expected at least " + rounds + " unloaded generations, got " + unloaded.Count);
        Assert.All(unloaded, a => Assert.True(a.Collected, "old ALC '" + a.Label + "' should be collectible"));

        // The current generation must still be fully functional after the churn.
        var current = runtime.Plugins.GetStatus().Single();
        Assert.Equal(PluginState.Active, current.State);
        Assert.Equal(gen, current.Generation);
        using var lease = AcquireTestService(runtime.Services);
        Assert.NotNull(lease.Value);
    }


    [Fact]
    public async Task UnloadRemovesHostTrackedSubscriptions()
    {
        await using var runtime = NewRuntime();
        await runtime.StartAsync();

        // The TestPlugin subscribes to TestPluginEvent; the bus must see one
        // subscription owned by the plugin generation.
        Assert.Equal(1, runtime.Events.SubscriptionCount());

        var fresh = await runtime.Plugins.ReloadAsync("NetPI.TestPlugin");
        Assert.NotNull(fresh);
        // Old generation's subscription removed; new generation re-subscribed.
        Assert.Equal(1, runtime.Events.SubscriptionCount());
    }

    [Fact]
    public void ConfigExposesPluginSection()
    {
        var config = new ConfigService(_home);
        var raw = config.GetRaw("NetPI.TestPlugin");
        Assert.Equal(JsonValueKind.Object, raw.ValueKind);
        Assert.True(raw.TryGetProperty("generation", out var g));
        Assert.Equal("gen1", g.GetString());
    }

    [Fact]
    public void SelfLease_CountsTowardLeaseDrain()
    {
        // PLAN §44: a plugin's self-lease (held by the agent for the run's
        // duration) must count toward LeasesHeld, which is what a reload's
        // drain waits on.
        var inst = new PluginInstance("netPI.Agent", 1);
        Assert.Equal(0, inst.LeasesHeld);

        var lease = inst.AcquireSelfLease();
        Assert.Equal(1, inst.LeasesHeld);
        Assert.NotNull(lease.Value); // the owning generation

        lease.Dispose();
        Assert.Equal(0, inst.LeasesHeld);
    }

    [Fact]
    public async Task NewLeaseDeniedWhileDraining()
    {
        await using var runtime = NewRuntime();
        await runtime.StartAsync();

        // Force the plugin into Draining via a reload that never drains (held lease).
        await using var held = AcquireTestService(runtime.Services);
        var cts = new CancellationTokenSource();
        var reload = runtime.Plugins.ReloadAsync("NetPI.TestPlugin", cts.Token);
        await Task.Delay(200);
        Assert.Equal(PluginState.Draining, runtime.Plugins.GetStatus().Single().State);

        await Assert.ThrowsAsync<ServiceUnavailableException>(
            async () => await Task.Run(() => AcquireTestService(runtime.Services).Value));

        cts.Cancel();
        try { await reload; } catch { }
        await held.DisposeAsync();
    }
}

/// <summary>Minimal ILogger that writes to Console.Error (visible in xunit output on failure).</summary>
internal sealed class ConsoleErrLogger : ILogger
{
    public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        var line = $"[{logLevel}] {formatter(state, exception)}" + (exception is null ? "" : $"\n{exception}");
        Console.Error.WriteLine(line);
    }
    private sealed class NullScope : IDisposable { public static readonly NullScope Instance = new(); public void Dispose() { } }
}
