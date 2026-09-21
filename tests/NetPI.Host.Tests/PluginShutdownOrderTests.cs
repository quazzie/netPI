using NetPI.Host;
using NetPI.Host.Config;
using NetPI.Host.Plugins;
using Xunit;

namespace NetPI.Host.Tests;

/// <summary>
/// astra-1 P5: shutdown order must reflect RECORDED START order (consumers
/// before providers), never the per-plugin generation number — and partially
/// started / Failed generations are cleaned up first. A Failed plugin whose
/// Start never ran is retired WITHOUT StopAsync (nothing to stop), while an
/// active plugin is stopped.
/// </summary>
public sealed class PluginShutdownOrderTests : IDisposable
{
    private readonly string _home;
    private readonly string _runtimeDir;
    private readonly string _pluginDir;
    private readonly string _testBin;

    public PluginShutdownOrderTests()
    {
        _home = Path.Combine(Path.GetTempPath(), "netpi-p5-shutdown-" + Guid.NewGuid().ToString("n"));
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

    private CapturingLogger _logger = new();

    private HostRuntime NewRuntime() => new(
        new PluginManagerOptions
        {
            PluginDirectory = _pluginDir,
            RuntimeDirectory = _runtimeDir,
            MaxCachedGenerations = 2,
        },
        _logger,
        new ConfigService(_runtimeDir));

    /// <summary>Records host log lines so the test can assert the stop order.</summary>
    private sealed class CapturingLogger : Microsoft.Extensions.Logging.ILogger
    {
        private readonly List<string> _lines = new();
        public IReadOnlyList<string> Lines => _lines;
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => new NullScope();
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => _lines.Add(formatter(state, exception));
        private sealed class NullScope : IDisposable { public void Dispose() { } }
    }

    [Fact]
    public async Task Shutdown_StopsInReverseRecordedStartOrder()
    {
        // Two plugins both start successfully. Discovery is ordinal, so
        // A00 is discovered+started FIRST, Z99 SECOND (higher StartSequence).
        // Both stage the same dummy plugin; the second opts out of service
        // registration (register:false) so the loads do not collide on the
        // registry id. Reverse-start stop order must stop Z99 BEFORE A00.
        StageAs("NetPI.A00");
        StageAs("NetPI.Z99");
        WriteConfig("{\"plugins\":{\"netpi.a00\":{\"generation\":\"first\"},\"netpi.z99\":{\"generation\":\"second\",\"register\":false}}}");

        await using var runtime = NewRuntime();
        await runtime.StartAsync();

        var a = runtime.Plugins.Get("NetPI.A00");
        var z = runtime.Plugins.Get("NetPI.Z99");
        Assert.Equal(PluginState.Active, a!.State);
        Assert.Equal(PluginState.Active, z!.State);
        Assert.True(a.StartSequence < z.StartSequence, $"start order not recorded ({a.StartSequence} !< {z.StartSequence})");

        await runtime.ShutdownAsync();

        // The host's unified-stop marker fires once per stopped generation,
        // in shutdown order. Z99 (started last) is stopped first.
        var stopFired = _logger.Lines
            .Where(l => l.EndsWith("generation stop fired (shutdown)", StringComparison.Ordinal))
            .Select(l => l.Split(' ')[0])
            .ToList();
        Assert.Equal(new[] { "NetPI.Z99", "NetPI.A00" }, stopFired);

        // Both plugins are gone from the current map (shutdown completed).
        Assert.Null(runtime.Plugins.Get("NetPI.A00"));
        Assert.Null(runtime.Plugins.Get("NetPI.Z99"));
    }
}
