using System.Text.Json;
namespace NetPI.Abstractions;


/// <summary>Immutable snapshot of a plugin's load/reload state (PLAN §36, §45).</summary>
public sealed record PluginStatusSnapshot(string Id, string Name, string? Version, string State, int Generation, string? Policy, int ActiveLeases, string? LastError);

/// <summary>
/// Reload control surface (PLAN §36). Implemented by the host and registered into
/// the service registry; resolved by the Web plugin to handle plugin.reload /
/// plugin.reloadAll. Optional: when absent (e.g. CLI-only build), the Web plugin
/// reports reload as unavailable. Lives in Abstractions so the Web plugin never
/// references the host ALC (reload-safe).
/// </summary>
public interface IPluginManagerFacade
{
    IReadOnlyList<PluginStatusSnapshot> GetStatus();
    ValueTask<bool> ReloadAsync(string pluginId, CancellationToken cancellationToken = default);
    ValueTask<int> ReloadAllAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// PLAN §50: re-scan the plugin directory and load+start any plugin the host
    /// has never seen (a folder staged after startup). Existing ids are left
    /// alone (reload swaps their bytes). The default implementation returns an
    /// empty list — host-less builds (tests) simply have no rescannable dir.
    /// </summary>
    ValueTask<IReadOnlyList<string>> ScanAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult<IReadOnlyList<string>>([]);
}

/// <summary>Minimal config surface the Web plugin needs for config.update (PLAN §36).</summary>
public interface IHostConfigUpdate
{
    /// <summary>Merge <c>plugins:&lt;pluginId&gt;</c> properties into ~/.netpi/config.json (deep merge).</summary>
    ValueTask MergePluginAsync(string pluginId, JsonElement properties, CancellationToken cancellationToken = default);
}
