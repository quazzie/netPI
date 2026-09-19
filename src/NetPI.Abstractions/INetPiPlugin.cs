
namespace NetPI.Abstractions;

/// <summary>
/// The single contract every netPI plugin implements. The host discovers
/// implementers by looking for classes that implement this interface in each
/// plugin directory (see PLAN §5).
///
/// Lifecycle order per generation:
///   LoadAsync -> StartAsync ... StopAsync -> UnloadAsync
/// </summary>
public interface INetPiPlugin
{
    /// <summary>Stable identity and version of this plugin.</summary>
    PluginInfo Info { get; }

    /// <summary>
    /// Register services, tools, and event subscriptions.
    /// Everything a plugin owns is registered here so the host can
    /// deterministically remove it on unload.
    /// </summary>
    ValueTask LoadAsync(IPluginContext context, CancellationToken cancellationToken);

    /// <summary>
    /// Start active resources (servers, watchers, timers).
    /// May be a no-op. Runs once per generation after LoadAsync.
    /// </summary>
    ValueTask StartAsync(CancellationToken cancellationToken);

    /// <summary>Drain/stop active resources. Runs before UnloadAsync.</summary>
    ValueTask StopAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Release anything left (disposal, final cleanup). After this the
    /// plugin generation must be fully unloadable.
    /// </summary>
    ValueTask UnloadAsync(CancellationToken cancellationToken);
}
