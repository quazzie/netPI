using System.Text.Json;

namespace NetPI.Abstractions;

/// <summary>
/// The host capabilities handed to a plugin during <see cref="INetPiPlugin.LoadAsync"/>.
/// This is the only surface through which a plugin registers anything into the host.
/// </summary>
public interface IPluginContext
{
    /// <summary>The plugin's own identity (as loaded by the host).</summary>
    PluginInfo Info { get; }

    /// <summary>Host-side service registry. Registrations are owned by this plugin generation.</summary>
    IServiceRegistry Services { get; }

    /// <summary>
    /// Command registry (PLAN §37). Plugins register slash commands here; they
    /// are removed automatically when this plugin generation is unloaded, so
    /// commands are hot-reloadable.
    /// </summary>
    ICommandRegistry Commands { get; }

    /// <summary>
    /// Right-panel Web UI registry. Plugins may contribute isolated tabs; each
    /// registration is removed automatically with the plugin generation.
    /// </summary>
    IWebPanelRegistry WebPanels { get; }

    /// <summary>Host event bus. Subscriptions are owned by this plugin generation.</summary>
    IEventBus Events { get; }

    /// <summary>
    /// This plugin's raw configuration section from ~/.netpi/config.json, or
    /// <see cref="JsonElement.ValueKind.None"/> when the plugin has no section.
    /// Unknown sibling plugins must not be visible: only this plugin's section.
    /// </summary>
    JsonElement OwnConfig { get; }

    /// <summary>Logger scoped to this plugin (writes to host console + file).</summary>
    IPluginLogger Log { get; }

    /// <summary>
    /// PLAN §44: a lease on this plugin generation itself. Hold it for the
    /// duration of long-running work (an agent run) so the host's reload
    /// cannot unload the plugin mid-work; its lease drain blocks until
    /// released. Disposing releases the generation for reload.
    /// </summary>
    IValueLease<object> LeaseSelf();
}

/// <summary>Minimal logging surface handed to plugins.</summary>
public interface IPluginLogger
{
    void Debug(string message);
    void Information(string message);
    void Warning(string message);
    void Error(string message, Exception? exception = null);
}
