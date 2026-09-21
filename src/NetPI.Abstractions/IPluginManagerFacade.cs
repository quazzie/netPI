using System.Text.Json;

namespace NetPI.Abstractions;


/// <summary>Immutable snapshot of a plugin's load/reload state (PLAN §36, §45).</summary>
public sealed record PluginStatusSnapshot(
    string Id,
    string Name,
    string? Version,
    string State,
    int Generation,
    string? BuildId,
    string? Policy,
    int ActiveLeases,
    string? LastError,
    // astra-1 P5: the update picture (discovery pointer vs. active snapshot) and
    // the last lifecycle operation (with the RestartRequired flag).
    PluginStatusUpdate? Update = null,
    PluginOperationRecord? LastOperation = null);

/// <summary>
/// astra-1 P5: the plugin update picture as seen by the diagnostics view — what
/// the discovery pointer currently names (AvailableBuildId, "legacy" for legacy
/// folders, null when there is no valid source) vs. the snapshot dir the active
/// generation loaded from (LoadedPath), the blocking-lease count, and whether the
/// previous generation's ALC has been collected yet (null when none exists).
/// "Active new build" vs. "old ALC still awaiting collection" reads off
/// AvailableBuildId != BuildId + PrevAlocCollected.
/// </summary>
public sealed record PluginStatusUpdate(
    string? AvailableBuildId,
    string? LoadedPath,
    int BlockingLeases,
    bool? PrevAlocCollected);

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

    /// <summary>
    /// astra-1 P2: structured reload outcome (host-owned lifecycle queue). The
    /// default returns Deferred — the legacy bool API stays authoritative for
    /// host-less builds.
    /// </summary>
    ValueTask<PluginOperationOutcome> ReloadPluginOutcomeAsync(string pluginId, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(new PluginOperationOutcome(
            Guid.NewGuid().ToString("n"), pluginId, null, null,
            PluginLifecyclePhase.Admission, PluginLifecycleOutcome.Deferred,
            "plugin manager unavailable", false));

    /// <summary>astra-1 P2: reload every known plugin; one structured outcome per plugin.</summary>
    ValueTask<IReadOnlyList<PluginOperationOutcome>> ReloadAllOutcomeAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult<IReadOnlyList<PluginOperationOutcome>>([]);

    /// <summary>
    /// astra-1 P5: retired-ALC collection state ("gc" view) — one entry per
    /// unloaded generation, newest last. The default returns an empty list
    /// (host-less builds never load collectible ALCs).
    /// </summary>
    IReadOnlyList<UnloadedAlocInfo> UnloadedAlocs()
        => Array.Empty<UnloadedAlocInfo>();

    // ------------------------------------------------------------------
    // astra-1 P5: queue-backed update operations (astral-1 P5 / PLAN §36 §41).
    // The Web handlers ack with the returned operation id IMMEDIATELY; the
    // work runs on the host's lifecycle queue (the same serial queue reloads
    // already use), and the client learns the outcome from the §41 events
    // (plugin.reloaded / plugin.reloadFailed / plugin.scanned /
    // plugins.state) emitted by the queue's completion path — NOT from the
    // request. After a reconnect the outcome is still queryable through
    // <see cref="GetOperation"/> (bounded, per host, evicts oldest).
    // Host-less builds return synthetic ids and report the operation as
    // Done/Deferred, exactly like the synchronous defaults above.
    // ------------------------------------------------------------------

    /// <summary>astra-1 P5: enqueue a reload; returns the operation id immediately.</summary>
    string EnqueueReload(string pluginId, string? requestedBuildId, CancellationToken cancellationToken = default)
        => Guid.NewGuid().ToString("n");

    /// <summary>astra-1 P5: enqueue a reload-all; returns the operation id immediately.</summary>
    string EnqueueReloadAll(CancellationToken cancellationToken = default)
        => Guid.NewGuid().ToString("n");

    /// <summary>astra-1 P5: enqueue a scan; returns the operation id immediately.</summary>
    string EnqueueScan(CancellationToken cancellationToken = default)
        => Guid.NewGuid().ToString("n");

    /// <summary>
    /// astra-1 P5: query the status of an Enqueue* operation (works after the
    /// initiating connection has died and reconnected). Unknown ids report
    /// Done with a Deferred outcome naming the missing id.
    /// </summary>
    PluginOperationStatus GetOperation(string operationId)
        => new(operationId, PluginOperationKind.Reload, true,
            PluginLifecycleOutcome.Deferred, PluginLifecyclePhase.Admission,
            "plugin manager unavailable", null, Array.Empty<string>());

}

/// <summary>Minimal config surface the Web plugin needs for config.update (PLAN §36).</summary>
public interface IHostConfigUpdate
{
    /// <summary>Merge <c>plugins:&lt;pluginId&gt;</c> properties into ~/.netpi/config.json (deep merge).</summary>
    ValueTask MergePluginAsync(string pluginId, JsonElement properties, CancellationToken cancellationToken = default);
}
