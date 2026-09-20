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
        IPluginLogger log)
    {
        _instance = instance;
        _services = services;
        _events = events;
        Info = instance.Info!;
        OwnConfig = ownConfig.ValueKind == JsonValueKind.Null ? JsonDocument.Parse("null").RootElement.Clone() : ownConfig;
        Log = log;
    }

    public PluginInfo Info { get; }
    public IServiceRegistry Services => _services;
    public IEventBus Events => _events;
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
        var previous = ServiceRegistry.ServiceOwner.Current;
        ServiceRegistry.ServiceOwner.Current = owner;
        try
        {
            var reg = inner.Register(id, instance);
            owner.Registrations[id] = reg;
            return reg;
        }
        finally
        {
            ServiceRegistry.ServiceOwner.Current = previous;
        }
    }

    public IValueLease<T> Acquire<T>(string id) where T : notnull => inner.Acquire<T>(id);
    public IValueLease<object> Acquire(string id, Type expectedType) => inner.Acquire(id, expectedType);

    public IValueLease<T> AcquireSelfLease<T>() where T : notnull => inner.AcquireSelfLease<T>();

    public T Resolve<T>(string id) where T : notnull => inner.Resolve<T>(id);
}

/// <summary>
/// <see cref="IEventBus"/> view scoped to one plugin generation: every
/// subscription made through it is owned by <see cref="Owner"/>.
/// </summary>
internal sealed class PluginContextEvents(
    IEventBus inner, PluginInstance owner) : IEventBus
{
    public IDisposable Subscribe<TEvent>(NetPI.Abstractions.EventHandler<TEvent> handler, EventSubscriptionOptions? options = null)
    {
        var previous = ServiceRegistry.ServiceOwner.Current;
        ServiceRegistry.ServiceOwner.Current = owner;
        try
        {
            var sub = inner.Subscribe(handler, options);
            // The bus already tracks the handle on the owning plugin (it picks
            // up the owner via ServiceRegistry.ServiceOwner.Current, set above)
            // — do not double-track here, or per-plugin counts run high.
            return sub;
        }
        finally

        {
            ServiceRegistry.ServiceOwner.Current = previous;
        }
    }

    public ValueTask PublishAsync<TEvent>(TEvent @event, CancellationToken ct = default) where TEvent : notnull =>
        inner.PublishAsync(@event, ct);
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
