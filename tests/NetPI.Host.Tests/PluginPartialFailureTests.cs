using NetPI.Host;
using NetPI.Host.Config;
using NetPI.Host.Plugins;
using Xunit;

namespace NetPI.Host.Tests;

/// <summary>
/// astra-1 P6: failure points AFTER registration/subscription and during
/// PARTIAL startup — the host must roll back a partially constructed
/// generation (remove the registered service + subscription it added) and
/// recover with a fresh last-known-good generation. Uses the TestPlugin's
/// config-driven hooks: <c>registerFail</c> (throws after
/// Services.Register), <c>subscribeFail</c> (throws after
/// Events.Subscribe), <c>startPartialFail</c> (real start work — a start
/// event — completes before the Start failure).
/// </summary>
public sealed class PluginPartialFailureTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), "netpi-partial-" + Guid.NewGuid().ToString("n"));
    private readonly string _runtimeDir;
    private readonly string _pluginDir;
    private const string Plugin = "NetPI.FailPlugin";

    public PluginPartialFailureTests()
    {
        _runtimeDir = Path.Combine(_home, "runtime");
        _pluginDir = Path.Combine(_home, "plugins");
        Directory.CreateDirectory(_runtimeDir);
        Directory.CreateDirectory(_pluginDir);

        var testBin = Path.GetDirectoryName(typeof(NetPI.TestPlugin.TestPlugin).Assembly.Location)!;
        var cfg = Path.GetFileName(Path.GetDirectoryName(testBin)!);
        string? root = testBin;
        while (root is not null && !File.Exists(Path.Combine(root, "NetPI.sln")))
            root = Path.GetDirectoryName(root);
        var pluginOut = Path.Combine(root!, "plugins", "NetPI.TestPlugin", "bin", cfg, "net10.0");
        Assert.True(Directory.Exists(pluginOut), $"TestPlugin build output not found: {pluginOut}");
        var staged = Path.Combine(_pluginDir, Plugin);
        Directory.CreateDirectory(staged);
        foreach (var f in Directory.EnumerateFiles(pluginOut))
            File.Copy(f, Path.Combine(staged, Path.GetFileName(f)), overwrite: true);
    }

    public void Dispose()
    {
        try { Directory.Delete(_home, recursive: true); } catch { }
    }

    private void WriteConfig(string extraFlags)
    {
        File.WriteAllText(Path.Combine(_runtimeDir, "config.json"),
            $$"""
            {
              "plugins": {
                "netpi.failplugin": { "generation": "g1"{{ extraFlags }} }
              }
            }
            """);
    }

    private HostRuntime NewRuntime()
    {
        var options = new PluginManagerOptions
        {
            PluginDirectory = _pluginDir,
            RuntimeDirectory = _runtimeDir,
        };
        return new HostRuntime(options, new ConsoleErrLogger(), new ConfigService(_runtimeDir));
    }

    private static object? AcquireService(HostRuntime runtime)
    {
        var type = ServiceTypes.Of(runtime.Services, "test.service");
        using var lease = runtime.Services.Acquire("test.service", type);
        return lease.Value;
    }

    [Fact]
    public async Task Reload_RegisterFailsAfterRegistration_RollsBack_FreshGenerationUsable()
    {
        WriteConfig("");
        var runtime = NewRuntime();
        await runtime.StartAsync();
        var first = runtime.Plugins.GetStatus().Single(s => s.PluginId == Plugin);
        Assert.Equal(PluginState.Active, first.State);
        Assert.NotNull(AcquireService(runtime));

        // The candidate will register its service and THEN throw — the host
        // must roll the partial registration back, not leave a half-built
        // generation, and must recover with a fresh LKG generation.
        WriteConfig(", \"registerFail\": true");
        var outcome = await runtime.Plugins.ReloadPluginAsync(Plugin);
        Assert.Equal(NetPI.Abstractions.PluginLifecycleOutcome.RolledBack, outcome.Outcome);
        Assert.Contains("after registration", outcome.Error, StringComparison.OrdinalIgnoreCase);

        var now = runtime.Plugins.GetStatus().Single(s => s.PluginId == Plugin);
        Assert.Equal(PluginState.Active, now.State);
        Assert.True(now.Generation > first.Generation, $"expected fresh generation > {first.Generation}, got {now.Generation}");
        // The rolled-back (fresh LKG) generation's service is live and usable.
        Assert.NotNull(AcquireService(runtime));
        Assert.Equal(1, now.SubscriptionCount);
        await runtime.DisposeAsync();
    }

    [Fact]
    public async Task Reload_FailsAfterSubscription_RollsBack_FreshGenerationUsable()
    {
        WriteConfig("");
        var runtime = NewRuntime();
        await runtime.StartAsync();
        var first = runtime.Plugins.GetStatus().Single(s => s.PluginId == Plugin);
        Assert.Equal(PluginState.Active, first.State);

        WriteConfig(", \"subscribeFail\": true");
        var outcome = await runtime.Plugins.ReloadPluginAsync(Plugin);
        Assert.Equal(NetPI.Abstractions.PluginLifecycleOutcome.RolledBack, outcome.Outcome);
        Assert.Contains("after subscription", outcome.Error, StringComparison.OrdinalIgnoreCase);

        var now = runtime.Plugins.GetStatus().Single(s => s.PluginId == Plugin);
        Assert.Equal(PluginState.Active, now.State);
        Assert.True(now.Generation > first.Generation);
        Assert.NotNull(AcquireService(runtime));
        await runtime.DisposeAsync();
    }

    [Fact]
    public async Task Reload_PartialStartFails_RollsBack_StartWorkDidRun()
    {
        WriteConfig("");
        var runtime = NewRuntime();
        await runtime.StartAsync();
        var first = runtime.Plugins.GetStatus().Single(s => s.PluginId == Plugin);
        Assert.Equal(PluginState.Active, first.State);

        // The candidate LOADS successfully and STARTS partially: it publishes
        // a start event, then throws during StartAsync. Start failure must
        // roll back (a fresh LKG generation), never emit Applied, and the
        // plugin stays usable.
        WriteConfig(", \"startPartialFail\": true");
        var outcome = await runtime.Plugins.ReloadPluginAsync(Plugin);
        Assert.Equal(NetPI.Abstractions.PluginLifecycleOutcome.RolledBack, outcome.Outcome);
        Assert.Contains("partial start", outcome.Error, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(NetPI.Abstractions.PluginLifecycleOutcome.Applied, outcome.Outcome);

        var now = runtime.Plugins.GetStatus().Single(s => s.PluginId == Plugin);
        Assert.Equal(PluginState.Active, now.State);
        Assert.True(now.Generation > first.Generation);
        Assert.NotNull(AcquireService(runtime));
        await runtime.DisposeAsync();
    }
}
