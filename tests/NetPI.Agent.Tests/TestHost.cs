using System.Reflection;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace NetPI.Agent.Tests;

/// <summary>A plugin context over the shared fake host services that tracks registrations (disposed on stop, like the host).</summary>
public sealed class TestPluginContext : IPluginContext
{
    private readonly TestHost _host;
    private readonly List<IDisposable> _owned = [];
    private readonly CancellationTokenSource _stopping = new();

    public TestPluginContext(TestHost host, string pluginId)
    {
        _host = host;
        PluginId = pluginId;
        Logger = new ConsoleLogger(pluginId, TestHost.Verbose);
        Services = new TrackedServices(host.Services, this);
        Rpc = new TrackedRpc(host.Rpc, this);
        Tools = new TrackedTools(host.Tools, this);
        Events = new TrackedBus(host.Bus, this);
    }

    public string PluginId { get; }
    public string PluginDirectory => _host.Workspace;
    public NetPiPaths Paths => _host.Paths;
    public ILogger Logger { get; }
    public IEventBus Events { get; }
    public IServiceRegistry Services { get; }
    public IRpcRegistry Rpc { get; }
    public IToolRegistry Tools { get; }
    public IUiRegistry Ui => throw new NotSupportedException();
    public IHttpRegistry Http => throw new NotSupportedException();
    public ISettings Settings => _host.Settings;
    public IDatabase Db => _host.Db;
    public ISessionStore Sessions => _host.Sessions;
    public IModelCatalog Models => _host.Catalog;
    public CancellationToken Stopping => _stopping.Token;

    public T Track<T>(T disposable) where T : IDisposable
    {
        lock (_owned) _owned.Add(disposable);
        return disposable;
    }

    public void Unload()
    {
        _stopping.Cancel();
        List<IDisposable> owned;
        lock (_owned)
        {
            owned = [.. _owned];
            _owned.Clear();
        }
        owned.Reverse();
        foreach (var d in owned)
        {
            try { d.Dispose(); } catch { }
        }
    }

    private sealed class TrackedServices(FakeServices inner, TestPluginContext ctx) : IServiceRegistry
    {
        public IDisposable Register<T>(T instance, int priority = 0) where T : class => ctx.Track(inner.Register(instance, priority));
        public T? Get<T>() where T : class => inner.Get<T>();
        public IReadOnlyList<T> GetAll<T>() where T : class => inner.GetAll<T>();
    }

    private sealed class TrackedRpc(FakeRpc inner, TestPluginContext ctx) : IRpcRegistry
    {
        public IDisposable Register(string method, RpcHandler handler, string? description = null) => ctx.Track(inner.RegisterFor(method, handler, ctx.PluginId));
        public IDisposable Register(string method, RpcHandler handler, string? description, bool readOnly) =>
            ctx.Track(inner.RegisterFor(method, handler, ctx.PluginId, readOnly));
        public Task<object?> InvokeAsync(string method, object? parameters = null, CancellationToken ct = default) => inner.InvokeAsync(method, parameters, ct);
        public IReadOnlyList<RpcMethodInfo> List() => inner.List();
        public bool Exists(string method) => inner.Exists(method);
    }

    private sealed class TrackedTools(FakeTools inner, TestPluginContext ctx) : IToolRegistry
    {
        public IDisposable Register(IAgentTool tool, int priority = 0) => ctx.Track(inner.Register(tool, priority, ctx.PluginId));
        public IReadOnlyList<IAgentTool> All => inner.All;
        public IAgentTool? Get(string name) => inner.Get(name);
        public IReadOnlyList<ToolRegistration> Registrations => inner.Registrations;
    }

    private sealed class TrackedBus(FakeBus inner, TestPluginContext ctx) : IEventBus
    {
        public void Publish(BusEvent evt) => inner.Publish(evt);
        public IDisposable Subscribe(string pattern, Action<BusEvent> handler) => ctx.Track(inner.Subscribe(pattern, handler));
        public IDisposable SubscribeAsync(string pattern, Func<BusEvent, ValueTask> handler) => ctx.Track(inner.SubscribeAsync(pattern, handler));
        public IReadOnlyList<BusEvent> Recent(int max = 200) => inner.Recent(max);
    }
}

public sealed class TestHost : IAsyncDisposable
{
    public static bool Verbose { get; set; }

    public static ModelInfo LocalModel() => new()
    {
        Provider = "fake", Id = "local", IsLocal = true, Concurrency = 2, ContextWindow = 100_000, MaxOutputTokens = 4096, Status = "loaded",
    };

    public static ModelInfo SoloModel() => new()
    {
        Provider = "fake", Id = "solo", IsLocal = true, Concurrency = 1, ContextWindow = 50_000, Status = "loaded",
    };

    public static ModelInfo CloudModel() => new()
    {
        Provider = "cloud", Id = "big", IsLocal = false, ContextWindow = 200_000,
    };

    private readonly List<(string Id, INetPiPlugin Plugin, TestPluginContext Ctx)> _plugins = [];

    public string Root { get; }
    public string Workspace { get; }
    public string Home { get; }
    public NetPiPaths Paths { get; }
    public FakeBus Bus { get; } = new();
    public FakeServices Services { get; } = new();
    public FakeSettings Settings { get; }
    public FakeTools Tools { get; }
    public FakeRpc Rpc { get; } = new();
    public FakeSessionStore Sessions { get; }
    public FakeCatalog Catalog { get; }
    public IDatabase Db { get; set; }

    private TestHost(IDatabase? db)
    {
        Root = Path.Combine(Environment.GetEnvironmentVariable("NETPI_TEST_ROOT") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Path.GetTempPath(), "netpi-agent-tests"), Ids.Short(10));
        Workspace = Path.Combine(Root, "workspace");
        Home = Path.Combine(Root, "home");
        Directory.CreateDirectory(Workspace);
        Directory.CreateDirectory(Home);
        Paths = new NetPiPaths
        {
            AppDir = Root, Home = Home, LogsDir = Root, WebRoot = Root, SettingsFile = Path.Combine(Home, "settings.json"),
            DatabaseFile = Path.Combine(Home, "netpi.db"), TempDir = Root, PluginDirs = [], DefaultWorkspace = Workspace,
        };
        Settings = new FakeSettings(Bus);
        Tools = new FakeTools(Settings);
        Sessions = new FakeSessionStore(Bus, Workspace);
        Catalog = new FakeCatalog(Services);
        Db = db ?? new NullDatabase();
        Services.Changed = type => ((IEventBus)Bus).Publish("services.changed", new { contract = type.FullName });
        // the host registers its core services too
        Services.Register<ISessionStore>(Sessions);
        Services.Register<IModelCatalog>(Catalog);
        Services.Register<IResourceLeases>(new NetPI.Host.Registries.ResourceLeases(Bus));
    }

    /// <summary>Which plugins <see cref="StartAsync"/> loads.</summary>
    [Flags]
    public enum Plugins { None = 0, Agents = 1, Context = 2, AgentsMd = 4, Runtime = 8, AgentTools = 16, All = 31 }

    public static async Task<TestHost> StartAsync(Action<TestHost>? setup = null, Plugins plugins = Plugins.All, IDatabase? db = null)
    {
        var h = new TestHost(db);
        h.Catalog.AddModel(LocalModel());
        h.Catalog.AddModel(SoloModel());
        h.Catalog.AddModel(CloudModel());
        h.Catalog.DefaultModelRef = "fake/local";
        setup?.Invoke(h);
        if (plugins.HasFlag(Plugins.Agents)) await h.StartPluginAsync(new NetPI.Agents.AgentsPlugin());
        if (plugins.HasFlag(Plugins.Context)) await h.StartPluginAsync(new NetPI.Context.ContextPlugin());
        if (plugins.HasFlag(Plugins.AgentsMd)) await h.StartPluginAsync(new NetPI.AgentsMd.AgentsMdPlugin());
        if (plugins.HasFlag(Plugins.Runtime)) await h.StartPluginAsync(new RuntimePlugin());
        if (plugins.HasFlag(Plugins.AgentTools)) await h.StartPluginAsync(new NetPI.Tools.Agents.AgentToolsPlugin());
        return h;
    }

    public async Task<TestPluginContext> StartPluginAsync(INetPiPlugin plugin)
    {
        var id = plugin.GetType().GetCustomAttribute<NetPiPluginAttribute>()?.Id ?? plugin.GetType().Name;
        var ctx = new TestPluginContext(this, id);
        await plugin.StartAsync(ctx, CancellationToken.None);
        lock (_plugins) _plugins.Add((id, plugin, ctx));
        return ctx;
    }

    public async Task StopPluginAsync(string id)
    {
        (string Id, INetPiPlugin Plugin, TestPluginContext Ctx) entry;
        lock (_plugins)
        {
            entry = _plugins.First(p => p.Id == id);
            _plugins.Remove(entry);
        }
        await entry.Plugin.StopAsync(CancellationToken.None);
        entry.Ctx.Unload();
    }

    public T Plugin<T>() where T : INetPiPlugin
    {
        lock (_plugins) return (T)_plugins.First(p => p.Plugin is T).Plugin;
    }

    public IAgentRuntime Runtime => Services.Get<IAgentRuntime>() ?? throw new InvalidOperationException("no agent runtime");
    public IAgentScheduler? Scheduler => Services.Get<IAgentScheduler>();

    public SessionInfo NewSession(string? model = null, string? projectId = null, string title = "test") =>
        Sessions.CreateSession(new SessionInfo { Title = title, Model = model, ProjectId = projectId });

    public void SetModel(string sessionId, string model) => Sessions.UpdateSession(sessionId, s => s.Model = model);

    public void AddTool(IAgentTool tool) => Tools.Register(tool);

    public Task<AgentInfo> SendAsync(string sessionId, string text, DeliveryMode mode = DeliveryMode.Auto) =>
        Runtime.SendAsync(sessionId, new UserInput { Text = text }, mode);

    /// <summary>Wait until the session's agent is not busy (Running/Queued/Yielded).</summary>
    public async Task<AgentInfo> IdleAsync(string sessionId, int timeoutMs = 10_000)
    {
        await Wait.Until(() => Runtime.GetBySession(sessionId) is { } a && a.Status is not (AgentStatus.Running or AgentStatus.Queued or AgentStatus.Yielded),
            $"agent of {sessionId} idle", timeoutMs);
        await Bus.DrainAsync();
        return Runtime.GetBySession(sessionId)!;
    }

    public async Task<AgentInfo> StatusAsync(string agentId, AgentStatus status, int timeoutMs = 10_000)
    {
        await Wait.Until(() => Runtime.Get(agentId)?.Status == status, $"agent {agentId} {status} (now {Runtime.Get(agentId)?.Status})", timeoutMs);
        return Runtime.Get(agentId)!;
    }

    public List<ChatMessage> Messages(string sessionId) => Sessions.Messages(sessionId);

    public static JsonObject Json(object? o) => (NetPiJson.ToNode(o) as JsonObject) ?? new JsonObject();

    public async ValueTask DisposeAsync()
    {
        List<string> ids;
        lock (_plugins) ids = _plugins.Select(p => p.Id).Reverse().ToList();
        foreach (var id in ids)
        {
            try { await StopPluginAsync(id); } catch (Exception ex) { Console.WriteLine($"    stop {id} failed: {ex.Message}"); }
        }
        Bus.Dispose();
        try { Directory.Delete(Root, recursive: true); } catch { }
    }
}
