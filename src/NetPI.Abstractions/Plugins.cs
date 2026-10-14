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
    /// <summary>
    /// Ask for a reload. It goes through the <see cref="IReloadGate"/> like every other: this returns once the reload
    /// was applied, or once it was deferred (something holds the gate, or <c>plugins.quiet</c> is on) - then
    /// <see cref="Deferred"/> lists it and it is applied when nothing holds the gate any more.
    /// </summary>
    Task ReloadAsync(string pluginId, CancellationToken ct = default);
    Task SetEnabledAsync(string pluginId, bool enabled, CancellationToken ct = default);
    Task RescanAsync(CancellationToken ct = default);
    /// <summary>
    /// Plugins whose reload is waiting, oldest first (empty otherwise): for the gate (<see cref="IReloadGate"/>: an
    /// agent is busy) or for <c>plugins.quiet</c> to be switched off. The running version keeps serving meanwhile.
    /// </summary>
    IReadOnlyList<string> Deferred() => [];
}

/// <summary>
/// The one door every plugin reload goes through (implemented by the host: <c>ctx.Services.Get&lt;IReloadGate&gt;()</c>).
/// A plugin whose work a reload would disturb holds it for as long as that work lasts - the runtime holds it while any
/// agent is busy, because reloading a plugin under a run can end the run or take a tool away from it. A reload asked
/// for meanwhile (a build replaced the files, <c>/reload</c>) is deferred, and the host applies it when the last hold
/// is released. The gate never forces one: while holds overlap, the reloads wait.
/// </summary>
public interface IReloadGate
{
    /// <summary>Keep reloads from being applied until the returned handle is disposed. Never waits; disposing twice is harmless.</summary>
    IDisposable Hold(string owner, string reason);

    /// <summary>
    /// <see cref="Hold"/>, but first wait for the reloads being applied right now, if any: work that starts from outside -
    /// a new run - then starts after that swap, not on the version it retires. With <paramref name="yieldToReload"/> it
    /// also waits while a reload has been deferred longer than <c>plugins.reloadDrainMinutes</c>, until that reload is
    /// applied (it is, once the holds already taken are released): so reloads cannot be put off for ever by runs that
    /// keep overlapping. Only for work nothing running waits for (a user's new run) - a run that waited for itself would
    /// never end.
    /// </summary>
    ValueTask<IDisposable> EnterAsync(string owner, string reason, bool yieldToReload = false, CancellationToken ct = default);

    /// <summary>What holds the gate now, oldest first: <c>"owner: reason (since …)"</c>.</summary>
    IReadOnlyList<string> Holders();
}
