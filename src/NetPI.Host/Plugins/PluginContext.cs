using Microsoft.Extensions.Logging;

namespace NetPI.Host.Plugins;

/// <summary>The <see cref="IPluginContext"/> handed to one plugin load: shared services plus scoped registries.</summary>
internal sealed class PluginContext : IPluginContext
{
    private readonly PluginScope _scope;

    public PluginContext(HostKernel kernel, string pluginId, string directory, PluginScope scope, CancellationToken stopping, Func<string> uiVersion)
    {
        _scope = scope;
        PluginId = pluginId;
        PluginDirectory = directory;
        Paths = kernel.Paths;
        Logger = kernel.LoggerFactory.CreateLogger("plugin:" + pluginId);
        Events = new ScopedEventBus(kernel.Bus, scope);
        Services = new ScopedServiceRegistry(kernel.Services, scope);
        Rpc = new ScopedRpcRegistry(kernel.Rpc, scope);
        Tools = new ScopedToolRegistry(kernel.Tools, scope);
        Ui = new ScopedUiRegistry(kernel.Ui, scope, uiVersion);
        Http = new ScopedHttpRegistry(kernel.Http, scope);
        Settings = kernel.Settings;
        Db = kernel.Db;
        Data = new Unwired();   // replaced by the storage provider's per-plugin data when the kernel is moved onto the port
        Sessions = kernel.Sessions;
        Models = kernel.Models;
        Stopping = stopping;
    }

    public string PluginId { get; }
    public string PluginDirectory { get; }
    public NetPiPaths Paths { get; }
    public ILogger Logger { get; }
    public IEventBus Events { get; }
    public IServiceRegistry Services { get; }
    public IRpcRegistry Rpc { get; }
    public IToolRegistry Tools { get; }
    public IUiRegistry Ui { get; }
    public IHttpRegistry Http { get; }
    public ISettings Settings { get; }
    public IDatabase Db { get; }
    public IPluginData Data { get; }
    public ISessionStore Sessions { get; }
    public IModelCatalog Models { get; }
    public CancellationToken Stopping { get; }

    public T Track<T>(T disposable) where T : IDisposable => _scope.Track(disposable);

    private sealed class Unwired : IPluginData
    {
        public IDataCollection Collection(string name, CollectionSpec spec) => throw new NotSupportedException("The storage port is not wired into the host yet.");
        public T Transaction<T>(Func<T> work) => throw new NotSupportedException("The storage port is not wired into the host yet.");
        public void Transaction(Action work) => throw new NotSupportedException("The storage port is not wired into the host yet.");
    }
}
