using System.Text.Json;
using Microsoft.Extensions.Logging;
using NetPI.Host.Services;

using NetPI.Abstractions;

namespace NetPI.Host.Plugins;

/// <summary>
/// The <see cref="IPluginContext"/> implementation handed to
/// <c>INetPiPlugin.LoadAsync</c>. The Services view scopes ownership of
/// registrations to the calling plugin generation (PLAN §7).
/// </summary>
internal sealed class PluginContext : IPluginContext
{
    private readonly PluginInstance _instance;
    private readonly PluginContextServices _services;
    private readonly PluginContextEvents _events;

    public PluginContext(
        PluginInstance instance,
        PluginContextServices services,
        PluginContextEvents events,
        JsonElement ownConfig,
        IPluginLogger log,
        ICommandRegistry? commands = null,
        IWebPanelRegistry? webPanels = null)
    {
        _instance = instance;
        _services = services;
        _events = events;
        _commands = commands;
        _webPanels = webPanels;
        Info = instance.Info!;
        OwnConfig = ownConfig.ValueKind == JsonValueKind.Null ? JsonDocument.Parse("null").RootElement.Clone() : ownConfig;
        Log = log;
    }

    private readonly ICommandRegistry? _commands;
    private readonly IWebPanelRegistry? _webPanels;
    public PluginInfo Info { get; }
    public IServiceRegistry Services => _services;
    public IEventBus Events => _events;
    public ICommandRegistry Commands => _commands ?? NoopCommands.Instance;
    public IWebPanelRegistry WebPanels => _webPanels ?? NoopPanels.Instance;

    private sealed class NoopCommands : ICommandRegistry
    {
        public static readonly NoopCommands Instance = new();
        public IDisposable Register(CommandDefinition command) => NoopDisposable.Instance;
        public IReadOnlyList<CommandDefinition> All() => [];
        public CommandDefinition? Find(string name) => null;
    }

    private sealed class NoopPanels : IWebPanelRegistry
    {
        public static readonly NoopPanels Instance = new();
        public IDisposable Register(WebPanelDefinition panel) => NoopDisposable.Instance;
        public IReadOnlyList<WebPanelDefinition> All() => [];
    }

    private sealed class NoopDisposable : IDisposable
    {
        public static readonly NoopDisposable Instance = new();
        public void Dispose() { }
    }
    public JsonElement OwnConfig { get; }
    public IPluginLogger Log { get; }

    public IValueLease<object> LeaseSelf() => _instance.AcquireSelfLease();
}

/// <summary>
/// <see cref="IServiceRegistry"/> view scoped to one plugin generation:
/// every registration made through it is owned by <see cref="Owner"/> and
/// will be removed by the host on unload, even if the plugin forgets.
/// </summary>
internal sealed class PluginContextServices(
    ServiceRegistry inner, PluginInstance owner) : IServiceRegistry
{
    public IDisposable Register<T>(string id, T instance) where T : notnull
    {
        // astra-1 P3: the owner is the explicit parameter — no ambient
        // process-global state, so concurrent callbacks can never attribute
        // work to the wrong generation.
        var reg = inner.Register(id, instance, owner);
        owner.Registrations[id] = reg;
        return reg;
    }

    public IValueLease<T> Acquire<T>(string id) where T : notnull => inner.Acquire<T>(id);
    public IValueLease<object> Acquire(string id, Type expectedType) => inner.Acquire(id, expectedType);

    public IValueLease<T> AcquireSelfLease<T>() where T : notnull => inner.AcquireSelfLease<T>(owner);

    public T Resolve<T>(string id) where T : notnull => inner.Resolve<T>(id);

    public IDisposable WatchServiceReplacement(string id, Action<string> onReplaced)
        => inner.WatchServiceReplacement(id, onReplaced);
}

/// <summary>
/// <see cref="IEventBus"/> view scoped to one plugin generation: every
/// subscription made through it is owned by <see cref="Owner"/>.
/// </summary>
internal sealed class PluginContextEvents(
    NetPI.Host.Events.EventBus inner, PluginInstance owner) : IEventBus
{
    public IDisposable Subscribe<TEvent>(NetPI.Abstractions.EventHandler<TEvent> handler, EventSubscriptionOptions? options = null)
    {
        // astra-1 P3: explicit owner — the bus tracks the handle on the
        // owning generation (no ambient state).
        return inner.Subscribe(handler, options, owner);
    }

    public ValueTask PublishAsync<TEvent>(TEvent @event, CancellationToken ct = default) where TEvent : notnull =>
        // astra-1 P3: explicit publisher owner — the self-event filter matches
        // the actual owning generation (never a process-global ambient static).
        inner.PublishAsync(@event, owner, ct);
}

/// <summary>Console/file logger adapter for plugins.</summary>
internal sealed class PluginLogger(string pluginId, ILogger hostLogger) : IPluginLogger
{
    private readonly ILogger _logger = hostLogger;
    public void Debug(string message) => _logger.LogDebug("[{Plugin}] {Message}", pluginId, message);
    public void Information(string message) => _logger.LogInformation("[{Plugin}] {Message}", pluginId, message);
    public void Warning(string message) => _logger.LogWarning("[{Plugin}] {Message}", pluginId, message);
    public void Error(string message, Exception? exception)
    {
        if (exception is null)
            _logger.LogError("[{Plugin}] {Message}", pluginId, message);
        else
            _logger.LogError(exception, "[{Plugin}] {Message}", pluginId, message);
    }
}
