using Microsoft.Extensions.Logging;

namespace NetPI;

/// <summary>
/// Entry point of a NetPI plugin. A plugin assembly contains exactly one public, non-abstract
/// class implementing this interface (with a parameterless constructor). Plugins are loaded into
/// a collectible AssemblyLoadContext and can be reloaded at runtime: everything registered through
/// the <see cref="IPluginContext"/> is removed automatically when the plugin stops.
/// </summary>
public interface INetPiPlugin
{
    Task StartAsync(IPluginContext context, CancellationToken ct);

    /// <summary>Called before unload. Registrations made through the context are disposed afterwards automatically.</summary>
    Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}

/// <summary>Optional metadata for a plugin class. The id defaults to the assembly name lower-cased.</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class NetPiPluginAttribute(string id) : Attribute
{
    public string Id { get; } = id;
    public string? Name { get; init; }
    public string? Description { get; init; }
    /// <summary>Start order (ascending). Providers/infrastructure use low numbers, UI plugins high numbers.</summary>
    public int Order { get; init; } = 100;
}

/// <summary>Well-known filesystem locations.</summary>
public sealed class NetPiPaths
{
    /// <summary>Directory of the running executable.</summary>
    public required string AppDir { get; init; }
    /// <summary>User data directory (default ~/.netpi, override with NETPI_HOME).</summary>
    public required string Home { get; init; }
    public required string LogsDir { get; init; }
    public required string WebRoot { get; init; }
    public required string SettingsFile { get; init; }
    public required string TempDir { get; init; }
    public required IReadOnlyList<string> PluginDirs { get; init; }
    /// <summary>Default working directory for sessions that have no project.</summary>
    public required string DefaultWorkspace { get; init; }
}

/// <summary>
/// Everything a plugin can touch. All Register/Subscribe/Add calls made through this context are
/// owned by the plugin and disposed automatically on unload/reload.
/// </summary>
public interface IPluginContext
{
    string PluginId { get; }
    /// <summary>The plugin's real directory (where plugin.json and wwwroot live).</summary>
    string PluginDirectory { get; }
    NetPiPaths Paths { get; }
    ILogger Logger { get; }
    IEventBus Events { get; }
    IServiceRegistry Services { get; }
    IRpcRegistry Rpc { get; }
    IToolRegistry Tools { get; }
    IUiRegistry Ui { get; }
    IHttpRegistry Http { get; }
    ISettings Settings { get; }
    /// <summary>This plugin's own data: named collections of JSON documents with declared index fields (see <see cref="IPluginData"/>).</summary>
    IPluginData Data { get; }
    ISessionStore Sessions { get; }
    IModelCatalog Models { get; }
    /// <summary>Cancelled when the plugin is being stopped/unloaded.</summary>
    CancellationToken Stopping { get; }
    /// <summary>Track an arbitrary disposable so it is disposed on unload.</summary>
    T Track<T>(T disposable) where T : IDisposable;
}

public sealed class PluginInfo
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public string? Version { get; init; }
    public required string Directory { get; init; }
    public string? Assembly { get; init; }
    public string State { get; set; } = "unloaded"; // unloaded|loading|running|stopped|failed|disabled
    public string? Error { get; set; }
    public DateTimeOffset? LoadedAt { get; set; }
    public int LoadCount { get; set; }
    public long LoadMs { get; set; }
    public int Order { get; set; }
    public bool Enabled { get; set; } = true;
}

/// <summary>Plugin management service (implemented by the host).</summary>
public interface IPluginManager
{
    IReadOnlyList<PluginInfo> List();
    Task ReloadAsync(string pluginId, CancellationToken ct = default);
    Task SetEnabledAsync(string pluginId, bool enabled, CancellationToken ct = default);
    Task RescanAsync(CancellationToken ct = default);
    /// <summary>
    /// Plugins whose reload is waiting for <c>plugins.quiet</c> to be switched off, oldest first (empty otherwise).
    /// While quiet is on a reload is recorded and not applied, so the running version keeps serving.
    /// </summary>
    IReadOnlyList<string> Deferred() => [];
}
