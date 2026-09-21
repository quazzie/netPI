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
public sealed record UnloadedAlocInfo(string Label, bool Collected, string? PluginId);

