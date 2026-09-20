using NetPI.Host;
using NetPI.Host.Config;
using NetPI.Host.Plugins;
using Xunit;

namespace NetPI.Host.Tests;

/// <summary>
/// PLAN §50 (Host): "plugin throws during Load → old version remains usable or
/// clear Failed state". Uses the TestPlugin's config-driven <c>loadFail</c>
/// hook, staged under a second plugin directory, so one host instance hosts a
/// healthy plugin and a plugin whose LoadAsync throws.
/// </summary>
public sealed class PluginFailureTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), "netpi-fail-" + Guid.NewGuid().ToString("n"));
    private readonly string _runtimeDir;
    private readonly string _pluginDir;

    public PluginFailureTests()
    {
        _runtimeDir = Path.Combine(_home, "runtime");
        _pluginDir = Path.Combine(_home, "plugins");
        Directory.CreateDirectory(_runtimeDir);
        Directory.CreateDirectory(_pluginDir);

        // Stage the TestPlugin build output under TWO plugin directories:
        // the healthy one and the one whose config flips loadFail. The host
        // identifies plugins by directory name, so both coexist.
        var testBin = Path.GetDirectoryName(typeof(NetPI.TestPlugin.TestPlugin).Assembly.Location)!;
        var cfg = Path.GetFileName(Path.GetDirectoryName(testBin)!);
        string? root = testBin;
        while (root is not null && !File.Exists(Path.Combine(root, "NetPI.sln")))
            root = Path.GetDirectoryName(root);
        var pluginOut = Path.Combine(root!, "plugins", "NetPI.TestPlugin", "bin", cfg, "net10.0");
        Stage(pluginOut, "NetPI.TestPlugin");
        Stage(pluginOut, "NetPI.FailPlugin");
    }

    private void Stage(string source, string dirName)
    {
        var staged = Path.Combine(_pluginDir, dirName);
        Directory.CreateDirectory(staged);
        foreach (var f in Directory.EnumerateFiles(source))
            File.Copy(f, Path.Combine(staged, Path.GetFileName(f)), overwrite: true);
    }

    public void Dispose()
    {
        try { Directory.Delete(_home, recursive: true); } catch { }
    }

    private void WriteConfig(bool loadFail)
    {
        File.WriteAllText(Path.Combine(_runtimeDir, "config.json"),
            $$"""
            {
              "plugins": {
                "netpi.testplugin": { "generation": "gen1" },
                "netpi.failplugin": { "loadFail": {{ loadFail.ToString().ToLowerInvariant() }}, "register": false }
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

    [Fact]
    public async Task Startup_PluginThrowsDuringLoad_MarksFailed_KeepsOthersActive()
    {
        WriteConfig(loadFail: true);
        var runtime = NewRuntime();
        await runtime.StartAsync(); // must not throw

        var status = runtime.Plugins.GetStatus();
        Assert.Equal(PluginState.Active,
            status.Single(s => s.PluginId == "NetPI.TestPlugin").State);
        var failed = status.Single(s => s.PluginId == "NetPI.FailPlugin");
        Assert.Equal(PluginState.Failed, failed.State);
        Assert.Contains("refuses to load", failed.LastError);
        await runtime.DisposeAsync();
    }

    [Fact]
    public async Task Reload_PluginThrowsDuringLoad_MarksFailed_NoCrash()
    {
        WriteConfig(loadFail: false);
        var runtime = NewRuntime();
        await runtime.StartAsync();
        Assert.Equal(PluginState.Active,
            runtime.Plugins.GetStatus().Single(s => s.PluginId == "NetPI.FailPlugin").State);

        // Flip the failing flag and reload: the old generation was already
        // drained, so the host must surface a clear Failed state — not crash.
        WriteConfig(loadFail: true);
        var fresh = await runtime.Plugins.ReloadAsync("NetPI.FailPlugin");
        Assert.Null(fresh);
        var failed = runtime.Plugins.GetStatus().Single(s => s.PluginId == "NetPI.FailPlugin");
        Assert.Equal(PluginState.Failed, failed.State);
        Assert.Contains("refuses to load", failed.LastError);
        // The host and the other plugin are still fully operational.
        Assert.Equal(PluginState.Active,
            runtime.Plugins.GetStatus().Single(s => s.PluginId == "NetPI.TestPlugin").State);
        await runtime.DisposeAsync();
    }
}
