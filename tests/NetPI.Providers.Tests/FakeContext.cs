using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using NetPI.Host.Storage.Memory;

namespace NetPI.Providers.Tests;

/// <summary>Settings backed by a JsonObject (dotted paths).</summary>
internal sealed class FakeSettings(JsonObject root) : ISettings
{
    public JsonObject Root { get; private set; } = root;
    public string FilePath => "memory://settings.json";
    public bool InvalidOnDisk => false;
    public string? InvalidOnDiskError => null;
    public JsonObject Snapshot() => (JsonObject)Root.DeepClone();

    public JsonNode? GetNode(string path)
    {
        JsonNode? node = Root;
        foreach (var seg in path.Split('.'))
        {
            if (node is not JsonObject o || !o.TryGetPropertyValue(seg, out node)) return null;
        }
        return node;
    }

    public T? Get<T>(string path, T? defaultValue = default)
    {
        var n = GetNode(path);
        return n is null ? defaultValue : n.Deserialize<T>(NetPiJson.Options) ?? defaultValue;
    }

    public void Set(string path, JsonNode? value)
    {
        var segs = path.Split('.');
        var o = Root;
        foreach (var seg in segs[..^1])
        {
            if (o[seg] is not JsonObject child) { child = new JsonObject(); o[seg] = child; }
            o = child;
        }
        o[segs[^1]] = value;
    }

    public void Replace(JsonObject root) => Root = root;
}

/// <summary>Synchronous in-memory bus that records every event.</summary>
internal sealed class FakeBus : IEventBus
{
    private readonly List<(string Pattern, Action<BusEvent> Handler)> _subs = [];
    public List<BusEvent> Published { get; } = [];

    public void Publish(BusEvent evt)
    {
        lock (Published) Published.Add(evt);
        List<(string Pattern, Action<BusEvent> Handler)> subs;
        lock (_subs) subs = [.. _subs];
        foreach (var (p, h) in subs)
            if (p == "*" || p == evt.Type || (p.EndsWith(".*") && evt.Type.StartsWith(p[..^1], StringComparison.Ordinal))) h(evt);
    }

    public IDisposable Subscribe(string pattern, Action<BusEvent> handler)
    {
        var entry = (pattern, handler);
        lock (_subs) _subs.Add(entry);
        return new Disposable(() => { lock (_subs) _subs.Remove(entry); });
    }

    public IDisposable SubscribeAsync(string pattern, Func<BusEvent, ValueTask> handler) =>
        Subscribe(pattern, e => handler(e).AsTask().GetAwaiter().GetResult());

    public IReadOnlyList<BusEvent> Recent(int max = 200) { lock (Published) return Published.TakeLast(max).ToList(); }

    public int Count(string type) { lock (Published) return Published.Count(e => e.Type == type); }
    public int SubscriberCount { get { lock (_subs) return _subs.Count; } }
}

/// <summary>List-backed service registry.</summary>
internal sealed class FakeServices : IServiceRegistry
{
    private readonly List<(Type Type, object Instance, int Priority)> _items = [];

    public IDisposable Register<T>(T instance, int priority = 0) where T : class
    {
        var entry = (typeof(T), (object)instance, priority);
        lock (_items) _items.Add(entry);
        var disposed = false;
        return new Disposable(() => { lock (_items) { if (disposed) return; disposed = true; _items.Remove(entry); } });
    }

    public T? Get<T>() where T : class => GetAll<T>().FirstOrDefault();

    public IReadOnlyList<T> GetAll<T>() where T : class
    {
        lock (_items) return _items.Where(i => i.Type == typeof(T)).OrderByDescending(i => i.Priority).Select(i => (T)i.Instance).ToList();
    }
}

internal sealed class Disposable(Action action) : IDisposable
{
    private Action? _action = action;
    public void Dispose() => Interlocked.Exchange(ref _action, null)?.Invoke();
}

internal sealed class ListLogger : ILogger
{
    public List<string> Lines { get; } = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (Lines) Lines.Add($"{logLevel}: {formatter(state, exception)}");
    }
}

/// <summary>Just enough of IPluginContext for provider plugins: the registries are doubles, the store is the real memory one.</summary>
internal sealed class FakePluginContext : IPluginContext, IDisposable
{
    private readonly CancellationTokenSource _stopping = new();
    private readonly IStorage _storage;

    public FakePluginContext(JsonObject settings, string pluginId = "test")
    {
        PluginId = pluginId;
        Settings = SettingsImpl = new FakeSettings(settings);
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
