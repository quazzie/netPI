using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using NetPI.Host.Storage.Memory;

namespace NetPI.Providers.Tests;

// The recording fakes (FakeSettings, FakeBus, FakeServices, ListLogger, Disposer) are the shared ones,
// linked from tests/Shared/Fakes.cs.

/// <summary>Just enough of IPluginContext for provider plugins: the registries are doubles, the store is the real memory one.</summary>
internal sealed class FakePluginContext : IPluginContext, IDisposable
{
    private readonly CancellationTokenSource _stopping = new();
    private readonly IStorage _storage;

    public FakePluginContext(JsonObject settings, string pluginId = "test")
    {
        PluginId = pluginId;
        Settings = SettingsImpl = new FakeSettings(root: settings);
        Home = Path.Combine(Path.GetTempPath(), "netpi-prov-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Home);
        _storage = new MemoryStorageProvider().Open(new StorageOpenOptions
        {
            Home = Home, Logger = Log, Settings = Settings,
        });
    }

    public FakeSettings SettingsImpl { get; }
    public FakeBus Bus { get; } = new();
    public FakeServices ServicesImpl { get; } = new();
    public ListLogger Log { get; } = new();
    /// <summary>A home folder of its own, so the store never lands in the machine's own.</summary>
    public string Home { get; }

    public string PluginId { get; }
    public string PluginDirectory => AppContext.BaseDirectory;
    public NetPiPaths Paths { get; } = new()
    {
        AppDir = AppContext.BaseDirectory, Home = Path.GetTempPath(), LogsDir = Path.GetTempPath(), WebRoot = Path.GetTempPath(),
        SettingsFile = "settings.json", TempDir = Path.GetTempPath(), PluginDirs = [], DefaultWorkspace = Path.GetTempPath(),
    };
    public ILogger Logger => Log;
    public IEventBus Events => Bus;
    public IServiceRegistry Services => ServicesImpl;
    public IRpcRegistry Rpc => null!;
    public IToolRegistry Tools => null!;
    public IUiRegistry Ui => null!;
    public IHttpRegistry Http => null!;
    public ISettings Settings { get; }
    /// <summary>The plugin's own collections, over the memory provider (a provider plugin keeps nothing, but the port is there).</summary>
    public IPluginData Data => _storage.Plugins.For(PluginId);
    public ISessionStore Sessions => null!;
    public IModelCatalog Models => null!;
    public CancellationToken Stopping => _stopping.Token;
    public T Track<T>(T disposable) where T : IDisposable => disposable;
    public void Stop() => _stopping.Cancel();
    public void Dispose()
    {
        _stopping.Cancel();
        _storage.Dispose();
        try { Directory.Delete(Home, recursive: true); } catch { }
    }
}
