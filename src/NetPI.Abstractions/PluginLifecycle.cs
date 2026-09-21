namespace NetPI.Abstractions;

/// <summary>
/// astra-1 P2: structured outcome of a host plugin lifecycle operation
/// (load/scan/reload/reload-all/shutdown). Availability, operation progress and
/// ALC collection are SEPARATE facts: "update failed, previous build active"
/// (outcome Failed + ActiveBuildId set) is different from "plugin unavailable"
/// (outcome Failed + ActiveBuildId null + RestartRequired).
/// </summary>
public enum PluginLifecycleOutcome
{
    /// <summary>The requested change is now live.</summary>
    Applied = 0,

    /// <summary>Nothing changed — the requested build is already active.</summary>
    Unchanged = 1,

    /// <summary>
    /// The operation did not (yet) mutate the plugin: a lease drain or idle
    /// gate was still active when the bounded wait expired, so the previous
    /// state was restored. This is NOT a failure of the plugin.
    /// </summary>
    Deferred = 2,

    /// <summary>
    /// The candidate could not be applied. If ActiveBuildId is set, the
    /// previous build is still serving; if it is null the plugin is
    /// unavailable (possibly RestartRequired).
    /// </summary>
    Failed = 3,

    /// <summary>
    /// The candidate failed and the host restored the last-known-good build as
    /// a fresh generation.
    /// </summary>
    RolledBack = 4,

    /// <summary>
    /// Cleanup could not be established (e.g. a timed-out stop left the old
    /// generation partially torn down). A coordinated host restart is needed.
    /// </summary>
    RestartRequired = 5,
}

/// <summary>One phase of a lifecycle operation (the phase reached before the outcome).</summary>
public enum PluginLifecyclePhase
{
    Admission = 0,
    Pinning = 1,
    Draining = 2,
    Stopping = 3,
    Loading = 4,
    Starting = 5,
    RollingBack = 6,
}

/// <summary>
/// astra-1 P2: the result record for a lifecycle operation. ReloadAll returns
/// one per plugin (not a count); Scan returns one per candidate plugin.
/// </summary>
public sealed record PluginOperationOutcome(
    string OperationId,
    string? PluginId,
    string? RequestedBuildId,
    string? ActiveBuildId,
    PluginLifecyclePhase Phase,
    PluginLifecycleOutcome Outcome,
    string? Error,
    bool RestartRequired)
{
    /// <summary>Synthetic operation ids for operations that never enter the queue.</summary>
    public static readonly string ShutdownRejectedId = "op-shutdown-rejected";

    public static PluginOperationOutcome Deferred(string pluginId, string? requestedBuildId, string error) =>
        new(Guid.NewGuid().ToString("n"), pluginId, requestedBuildId, null,
            PluginLifecyclePhase.Admission, PluginLifecycleOutcome.Deferred, error, false);

    public static PluginOperationOutcome Unchanged(string pluginId, string? activeBuildId) =>
        new(Guid.NewGuid().ToString("n"), pluginId, activeBuildId, activeBuildId,
            PluginLifecyclePhase.Pinning, PluginLifecycleOutcome.Unchanged, null, false);
}

/// <summary>
/// astra-1 P5: mirror of the host's per-plugin lifecycle-operation record
/// (what the last reload/scan left behind: requested vs. active build, phase,
/// outcome, error, and the RestartRequired flag). Carried on the
/// <see cref="PluginStatusSnapshot"/> for the diagnostics view.
/// </summary>
public sealed record PluginOperationRecord(
    string OperationId,
    string? RequestedBuildId,
    string? ActiveBuildId,
    PluginLifecyclePhase Phase,
    PluginLifecycleOutcome Outcome,
    string? Error,
    bool RestartRequired);

/// <summary>
/// astra-1 P5: retired-ALC collection state ("gc" view): whether a previously
/// loaded generation's ALC has been collected yet. The label is
/// "&lt;pluginId&gt;-gen&lt;n&gt;" or "superseded-&lt;pluginId&gt;-gen&lt;n&gt;".
/// </summary>
/// <summary>
/// astra-1 P5: the kind of a queue-backed lifecycle operation (the Enqueue*
/// surface). Scan operations carry the ids they loaded in
/// <see cref="PluginOperationStatus.ScannedIds"/>.
/// </summary>
public enum PluginOperationKind
{
    Reload = 0,
    ReloadAll = 1,
    Scan = 2,
}

/// <summary>
/// astra-1 P5: queue-backed operation status — what an Enqueue* caller can
/// query (via <c>IPluginManagerFacade.GetOperation</c>) before or AFTER a
/// reconnect: the operation is acknowledged by id, runs on the host's
/// lifecycle queue, and its status is retained in a bounded table while it is
/// queued/running and after it completes. Unknown ids are reported as not
/// found (Done true, outcome Deferred + Error) — never invented.
/// </summary>
public sealed record PluginOperationStatus(
    string OperationId,
    PluginOperationKind Kind,
    bool Done,
    PluginLifecycleOutcome Outcome,
    PluginLifecyclePhase Phase,
    string? Error,
    string? AppliedBuildId,
    IReadOnlyList<string> ScannedIds);

/// <summary>
/// astra-1 P5: the per-plugin result inside a <see cref="PluginUpdateCompletedEvent"/>
/// — one entry per plugin for Reload/ReloadAll operations (a Deferred/Failed
/// reload keeps its error; AppliedBuildId is the build the plugin runs after
/// the op, null when the plugin is unavailable).
/// </summary>
public sealed record PluginUpdateResult(string PluginId, PluginLifecycleOutcome Outcome, string? Error, string? BuildId);

/// <summary>
/// astra-1 P5: published on the host event bus by the lifecycle queue RUNNER
/// when a queue-backed operation completes (NOT by the Web request that acked
/// it). The Web plugin turns this into the §41 events (plugin.state /
/// plugin.reloaded / plugin.reloadFailed / plugin.scanned / plugins.state /
/// ui.panels) for every LIVE connection — so a reload of netpi.web itself
/// still reaches the other clients even though the requesting connection died
/// mid-reload. For Reload, Results carries one entry for the plugin; for
/// ReloadAll, one per plugin; for Scan, Results is empty and ScannedIds
/// carries the ids that started.
/// </summary>
public sealed record PluginUpdateCompletedEvent(
    string OperationId,
    PluginOperationKind Kind,
    string? PluginId,
    IReadOnlyList<PluginUpdateResult> Results,
    IReadOnlyList<string> ScannedIds,
    string? Error,
    string? AppliedBuildId);

/// <summary>
/// astra-1 P5: retired-ALC collection state ("gc" view): whether a previously
/// loaded generation's ALC has been collected yet. The label is
/// "&lt;pluginId&gt;-gen&lt;n&gt;" or "superseded-&lt;pluginId&gt;-gen&lt;n&gt;".
/// </summary>
public sealed record UnloadedAlocInfo(string Label, bool Collected, string? PluginId);

