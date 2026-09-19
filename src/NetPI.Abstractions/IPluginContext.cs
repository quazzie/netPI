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
}

/// <summary>Minimal logging surface handed to plugins.</summary>
public interface IPluginLogger
{
    void Debug(string message);
    void Information(string message);
    void Warning(string message);
    void Error(string message, Exception? exception = null);
}
