using NetPI.Abstractions;
using NetPI.Host.Config;
using NetPI.Host.Events;
using NetPI.Host.Plugins;
using NetPI.Host.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace NetPI.Host;

/// <summary>
/// The boring host (PLAN §3): process lifetime, plugin discovery/loading,
/// collectible ALCs, service registry, event bus, reload coordination,
/// shutdown. Everything else is a plugin.
/// </summary>
public sealed class HostRuntime : IAsyncDisposable
{
    private readonly ILogger _logger;
    private bool _shutdown;

    public PluginManagerOptions Options { get; }
    public ConfigService Config { get; }
    public EventBus Events { get; }
    public ServiceRegistry Services { get; }
    public PluginManager Plugins { get; }
    public CommandRegistry Commands { get; }
    public WebPanelRegistry WebPanels { get; }
    public IConfigService ConfigService => Config;

    public CancellationToken Lifetime { get; private set; }
    private CancellationTokenSource _cts = null!;

    public HostRuntime(PluginManagerOptions? options = null, ILogger? logger = null,
        ConfigService? config = null)
    {
        Options = options ?? new PluginManagerOptions();
        _logger = logger ?? NullLogger.Instance;
        Config = config ?? new ConfigService(Options.RuntimeDirectory);
        Events = new EventBus();
        Services = new ServiceRegistry();
        Commands = new CommandRegistry();
        WebPanels = new WebPanelRegistry();
        // PLAN §37: expose the command registry to plugins (the Web plugin
        // resolves it under id "commands" to answer commands.list).
        Services.Register<ICommandRegistry>("commands", Commands);
        Plugins = new PluginManager(Options, Events, Services, Commands, WebPanels, Config, _logger);
    }

    /// <summary>
    /// Startup sequence (PLAN §3):
    ///   1. ensure runtime directory + config
    ///   2. discover + load plugins (LoadAllAsync)
    ///   3. start them (StartAllAsync)
    /// The host returns immediately after starting; shutdown is driven by the
    /// caller (CLI loop / Ctrl+C).
    /// </summary>
    public async Task StartAsync()
    {
        _logger.LogInformation("netPI host starting; plugin directory '{Dir}'",
            Options.PluginDirectory);

        Directory.CreateDirectory(Options.RuntimeDirectory);
        Directory.CreateDirectory(Path.Combine(Options.RuntimeDirectory, "logs"));

        await Plugins.LoadAllAsync();
        await Plugins.StartAllAsync();

        foreach (var p in Plugins.CurrentSnapshots())
            _logger.LogInformation("Loaded {Plugin} gen {Gen} ({State})",
                p.PluginId, p.Generation, p.State);

        _logger.LogInformation("netPI host ready");
    }

    /// <summary>
    /// Wait until the shutdown signal (Ctrl+C / process exit). The returned
    /// token is cancelled when the process should shut down.
    /// </summary>
    public async Task WaitForShutdownAsync(CancellationToken externalToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(externalToken);
        _cts = linked;
        Lifetime = linked.Token;

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        linked.Token.Register(() => tcs.TrySetResult());
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            _logger.LogInformation("Ctrl+C received — shutting down netPI");
            linked.Cancel();
        };
        // best-effort OS exit fallback (PLAN §48)
        AppDomain.CurrentDomain.ProcessExit += (_, _) => linked.Cancel();

        await tcs.Task;
    }


    /// <summary>
    /// Shutdown sequence (PLAN §48): stop + unload plugins in reverse order,
    /// then flush.
    /// </summary>
    public async Task ShutdownAsync(CancellationToken externalToken = default)
    {
        if (_shutdown) return;
        _shutdown = true;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(externalToken);
        cts.CancelAfter(TimeSpan.FromSeconds(30));
        // astra-1 P2: shutdown goes through the lifecycle queue, so any
        // in-flight reload/scan completes first and no new op can sneak in.
        await Plugins.ShutdownOpAsync(cts.Token);
        _logger.LogInformation("netPI host stopped");
    }

    public async ValueTask DisposeAsync()
    {
        if (!_shutdown)
            await ShutdownAsync();
        Config.Dispose();
    }
}
