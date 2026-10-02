using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using NetPI.Host.Events;
using NetPI.Host.Logging;
using NetPI.Host.Models;
using NetPI.Host.Plugins;
using NetPI.Host.Registries;
using NetPI.Host.Rpc;
using NetPI.Host.Sessions;
using NetPI.Host.Settings;
using NetPI.Host.Storage;

namespace NetPI.Host;

/// <summary>Composition root of one host instance (everything except the web server).</summary>
internal sealed class HostKernel : IAsyncDisposable
{
    private readonly List<IDisposable> _subscriptions = [];
    private bool _disposed;

    public required NetPiServerOptions Options { get; init; }
    public required NetPiPaths Paths { get; init; }
    public required string Token { get; init; }
    public required LogSink LogSink { get; init; }
    public required ILoggerFactory LoggerFactory { get; init; }
    public required SettingsStore Settings { get; init; }
    public required IStorage Storage { get; init; }
    public required EventBus Bus { get; init; }
    public required ServiceRegistry Services { get; init; }
    public required RpcRegistry Rpc { get; init; }
    public required ToolRegistry Tools { get; init; }
    public required UiRegistry Ui { get; init; }
    public required HttpRegistry Http { get; init; }
    public required SessionService Sessions { get; init; }
    public required ModelCatalog Models { get; init; }
    /// <summary>This app's hold on its home (one NetPI per home).</summary>
    internal HomeLock? HomeLock { get; init; }
    /// <summary>Logs it when the bus, or the thread pool, stops making progress (see <see cref="StallWatchdog"/>).</summary>
    internal StallWatchdog? Watchdog { get; init; }
    public PluginManager Plugins { get; private set; } = null!;
    public ILogger Log { get; private set; } = null!;

    public static HostKernel Create(NetPiServerOptions options)
    {
        var appDir = PathUtil.Normalize(AppContext.BaseDirectory);
        var home = ResolveHome(options.Home);
        var logsDir = Path.Combine(home, "logs");
        foreach (var dir in new[] { home, logsDir, Path.Combine(home, "plugins"), Path.Combine(home, "workspace") })
            Directory.CreateDirectory(dir);
        // The home holds the server token (server.json), the API keys (settings.json) and the database: on Unix it is
        // owner-only, so another local user on a shared box cannot read it or call the server with its token.
        PathUtil.OwnerOnly(home);

        // one NetPI per home: a second one stops here, before it touches the logs, the settings or the database
        var homeLock = HomeLock.Acquire(home);
        var created = new Stack<object>();
        created.Push(homeLock);
        var sink = new LogSink(logsDir, options.ConsoleLogging);
        created.Push(sink);
        var factory = Microsoft.Extensions.Logging.LoggerFactory.Create(b => LoggingSetup.Configure(b, sink));
        created.Push(factory);
        try
        {
            var settings = new SettingsStore(Path.Combine(home, "settings.json"), factory.CreateLogger("NetPI.Settings"));
            created.Push(settings);
            sink.MinLevel = LoggingSetup.ParseLevel(settings.Get<string>("logging.level"), LogLevel.Information);
            var paths = BuildPaths(options, appDir, home, logsDir, settings);
            TryCreate(paths.DefaultWorkspace);
            TryCreate(paths.TempDir);
            PendingBuild.Install(appDir, factory.CreateLogger("NetPI.Host")); // before anything loads or serves the app folder
            SharedAssemblies.PreloadContracts(appDir, factory.CreateLogger("NetPI.Host")); // the higher abstractions' contracts, shared by every plugin

            // The store is chosen once, here: a provider that cannot open stops the start with its message (never an empty fallback).
            var provider = StorageProviders.Create(options.Ephemeral ? "memory" : settings.Get<string>("storage.provider") ?? "sqlite");
            var storage = provider.Open(new StorageOpenOptions { Home = home, Logger = factory.CreateLogger("NetPI.Storage"), Settings = settings });
            created.Push(storage);
            var bus = new EventBus(factory.CreateLogger("NetPI.Events"));
            created.Push(bus);
            var watchdog = new StallWatchdog(bus, factory.CreateLogger("NetPI.Watchdog"), gate: storage.Lock);
            created.Push(watchdog);
            settings.AttachBus(bus);
            var services = new ServiceRegistry();
            var tools = new ToolRegistry(settings, bus, factory.CreateLogger("NetPI.Tools"));
            created.Push(tools);
            var ui = new UiRegistry(bus);
            created.Push(ui);
            var models = new ModelCatalog(services, settings, bus, factory.CreateLogger("NetPI.Models"));
            created.Push(models);
            var sessions = new SessionService(storage, bus, paths.DefaultWorkspace);

            var kernel = new HostKernel
            {
                Options = options,
                Paths = paths,
                Token = string.IsNullOrWhiteSpace(options.Token) ? NewToken() : options.Token.Trim(),
                LogSink = sink,
                LoggerFactory = factory,
                Settings = settings,
                Storage = storage,
                Bus = bus,
                Services = services,
                Rpc = new RpcRegistry(),
                Tools = tools,
                Ui = ui,
                Http = new HttpRegistry(),
                Sessions = sessions,
                Models = models,
                HomeLock = homeLock,
                Watchdog = watchdog,
            };
            kernel.Log = factory.CreateLogger("NetPI.Host");
            kernel.Plugins = new PluginManager(kernel, factory.CreateLogger("NetPI.Plugins"));
            kernel.Wire();
            settings.StartWatching();
            return kernel;
        }
        catch (Exception ex)
        {
            factory.CreateLogger("NetPI.Host").LogCritical(ex, "Host startup failed");
            while (created.Count > 0)
            {
                try
                {
                    switch (created.Pop())
                    {
                        case IAsyncDisposable ad: ad.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5)); break;
                        case IDisposable d: d.Dispose(); break;
                    }
                }
                catch { }
            }
            throw;
        }
    }

    private void Wire()
    {
        _subscriptions.Add(Bus.Subscribe(EventTypes.SettingsChanged, e =>
        {
            Tools.OnSettingsChanged();
            LogSink.MinLevel = LoggingSetup.ParseLevel(Settings.Get<string>("logging.level"), LogLevel.Information);
            Plugins.OnSettingsChanged(e);
        }));
        _subscriptions.Add(Bus.Subscribe(EventTypes.ModelsChanged, Models.OnModelsChanged));
        Services.Changed += Models.OnServiceChanged;
        Services.Changed += type => Bus.Publish("services.changed", new { contract = type.FullName });
        Rpc.Changed += method => Bus.Publish("rpc.changed", new { method });

        // Host services, also reachable through IServiceRegistry (e.g. from a ToolContext).
        _subscriptions.Add(Services.Register<IPluginManager>(Plugins));
        _subscriptions.Add(Services.Register<ISessionStore>(Sessions));
        _subscriptions.Add(Services.Register<IModelCatalog>(Models));
        _subscriptions.Add(Services.Register<ISettings>(Settings));
        _subscriptions.Add(Services.Register<IStorageAccess>(new StorageAccess(Storage)));
        _subscriptions.Add(Services.Register<IEventBus>(Bus));
        _subscriptions.Add(Services.Register<IToolRegistry>(Tools));
        _subscriptions.Add(Services.Register<IRpcRegistry>(Rpc));
        _subscriptions.Add(Services.Register<IUiRegistry>(Ui));

        CoreRpc.Register(this, _subscriptions);
    }

    /// <summary>
    /// Where the data lives: the --home option, else the NETPI_HOME environment variable, else ~/.netpi. ~ and
    /// environment references are expanded. Shared with the desktop shell, which resolves the SAME home for its
    /// window.json and WebView2 profile (a home read without expansion splits them from the server's).
    /// </summary>
    internal static string ResolveHome(string? option)
    {
        var raw = !string.IsNullOrWhiteSpace(option) ? option
            : Environment.GetEnvironmentVariable("NETPI_HOME") is { Length: > 0 } env ? env
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".netpi");
        return PathUtil.Expand(raw);
    }

    private static NetPiPaths BuildPaths(NetPiServerOptions options, string appDir, string home, string logsDir, SettingsStore settings)
    {
        var dirs = new List<string> { Path.Combine(appDir, "plugins"), Path.Combine(home, "plugins") };
        foreach (var d in settings.Get<List<string>>("plugins.dirs") ?? [])
            if (!string.IsNullOrWhiteSpace(d)) dirs.Add(PathUtil.Expand(d, home));
        foreach (var d in options.ExtraPluginDirs ?? [])
            if (!string.IsNullOrWhiteSpace(d)) dirs.Add(PathUtil.Expand(d));

        var workspace = settings.Get<string>("workspace.default");
        return new NetPiPaths
        {
            AppDir = appDir,
            Home = home,
            LogsDir = logsDir,
            WebRoot = string.IsNullOrWhiteSpace(options.WebRoot) ? Path.Combine(appDir, "wwwroot") : PathUtil.Expand(options.WebRoot),
            SettingsFile = settings.FilePath,
            TempDir = Path.Combine(Path.GetTempPath(), "netpi"),
            PluginDirs = dirs.Select(PathUtil.Normalize).Distinct(PathUtil.Comparer).ToList(),
            DefaultWorkspace = string.IsNullOrWhiteSpace(workspace) ? Path.Combine(home, "workspace") : PathUtil.Expand(workspace, home),
        };
    }

    private static void TryCreate(string dir)
    {
        try { Directory.CreateDirectory(dir); }
        catch { /* reported when used */ }
    }

    private static string NewToken()
    {
        Span<byte> bytes = stackalloc byte[24];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        try { await Plugins.DisposeAsync().ConfigureAwait(false); }
        catch (Exception ex) { Log.LogError(ex, "Stopping plugins failed"); }
        Services.Changed -= Models.OnServiceChanged;
        foreach (var s in _subscriptions) s.Dispose();
        Models.Dispose();
        Tools.Dispose();
        Ui.Dispose();
        Settings.Dispose();
        Watchdog?.Dispose();
        await Bus.DisposeAsync().ConfigureAwait(false);
        Storage.Dispose();
        LoggerFactory.Dispose();
        LogSink.Dispose();
        HomeLock?.Dispose();
    }
}
