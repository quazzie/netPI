namespace NetPI.Abstractions;

// astra-2 (docs/plans/astra-2.md §3–§6): assignment-held lanes. netPI owns
// admitted agent count itself; the inference engine (AiProxy) only supplies
// capacity metadata. A pool owns N lanes; an assignment (a logical run) owns
// AT MOST one lane and keeps it across model turns, tool batches, retry
// backoff, in-run maintenance — released only when the segment drains, is
// cancelled and unwinds, or an explicit handoff transfers it.

/// <summary>How a deployment's execution is authorized (astra-2 §5.2).</summary>
public enum DeploymentExecutionMode
{
    /// <summary>A pooled deployment: every segment requires lane admission.</summary>
    Pooled = 0,

    /// <summary>
    /// A direct cloud deployment: no lane/pool membership; execution requires
    /// explicit cloud authorization instead (astra-2 §11).
    /// </summary>
    DirectCloud = 1,
}

/// <summary>
/// How a pool's effective capacity is determined (astra-2 §5.2–§5.3).
/// </summary>
public enum LaneCapacityMode
{
    /// <summary>
    /// Follow the provider: N comes from a fresh observation of the backend's
    /// TOTAL configured concurrency (optionally capped by <c>maxAgents</c>),
    /// never from instantaneous free slots.
    /// </summary>
    Provider = 0,

    /// <summary>A user-configured fixed agent count (override/fallback).</summary>
    Manual = 1,
}

/// <summary>
/// Policy applied when provider capacity metadata is missing, stale, or
/// reports <see cref="ProviderCapacityStatus.Unknown"/> (astra-2 §5.3):
/// existing owners finish on their unchanged deployment; NO new admission or
/// handoff occurs until a fresh valid observation arrives.
/// </summary>
public enum LaneUnknownPolicy
{
    /// <summary>Hold new admission (the delivery default — fail closed).</summary>
    HoldNew = 0,
}

/// <summary>Whether a provider capacity observation is usable right now.</summary>
public enum ProviderCapacityStatus
{
    /// <summary>
    /// No observation (never refreshed / source unavailable). Never a basis
    /// for admission — a guessed number is a bug.
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// A total is known but the observation is older than the source's
    /// configured freshness bound (or the source has stopped reporting).
    /// </summary>
    Stale = 1,

    /// <summary>A fresh total-concurrency observation.</summary>
    Fresh = 2,
}

/// <summary>
/// One observation of a deployment's backend capacity metadata (astra-2 §5.3).
/// Immutably produced by an <see cref="IProviderCapacitySource"/>; the lane
/// scheduler is the only consumer. The total is the backend's CONFIGURED
/// concurrency for that deployment — never an instantaneous free-slot count,
/// never derived from model size or the model-list length.
/// </summary>
public sealed record ProviderCapacityObservation(
    string DeploymentId,
    int? TotalConcurrency,
    ProviderCapacityStatus Status,
    DateTimeOffset ObservedAt,
    string? Source,
    string? Detail = null)
{
    /// <summary>True when <see cref="TotalConcurrency"/> is a positive, fresh value.</summary>
    public bool Usable => Status == ProviderCapacityStatus.Fresh && TotalConcurrency is > 0;
}

/// <summary>
/// Supplies validated capacity metadata for a deployment (astra-2 §5.3).
/// Metadata polling only — a capacity source must NEVER make an inference
/// request (no capability probes for admission). Implemented by the provider
/// plugin (service <c>provider-capacity</c>); the lane scheduler polls it at a
/// bounded interval and on binding changes.
/// </summary>
public interface IProviderCapacitySource
{
    /// <summary>
    /// Refresh (or return a recent) observation for one model. The provider is
    /// keyed by its own model ids (netPI deployments bind to a model); report a
    /// FRESH, positive <see cref="ProviderCapacityObservation.TotalConcurrency"/>
    /// ONLY for a configured/declared backend concurrency for that model —
    /// never an instantaneous active-request count, never a guess. When no such
    /// total is exposed, report <see cref="ProviderCapacityStatus.Unknown"/>
    /// (the scheduler holds new admission — it does not substitute a number).
    /// Implementors must be safe to call concurrently and bound their waits.
    /// This is metadata-only: it must not make an inference request.
    /// </summary>
    ValueTask<ProviderCapacityObservation> ObserveAsync(string modelId, CancellationToken cancellationToken = default);
}

/// <summary>
/// The trusted execution policy for a model (astra-2 §3.3, §4 — the
/// "trusted execution-policy resolver"). netPI resolves this from CONFIG,
/// never from the model or a tool argument:
/// <c>Pooled</c> requires lane admission before any segment executes; a
/// deployment that resolves to no policy (this returns null) runs as legacy
/// direct execution — no lane, governed by the runner's own capacity.
/// </summary>
public sealed record DeploymentPolicy(
    string ModelId,
    DeploymentExecutionMode Mode,
    string? PoolId,
    string DeploymentId,
    bool Enabled = true)
{
    /// <summary>True when this model runs through a lane pool (requires admission).</summary>
    public bool RequiresLane => Mode == DeploymentExecutionMode.Pooled && !string.IsNullOrEmpty(PoolId);
}

/// <summary>
/// Resolves a model's execution policy from configuration (astra-2 §4).
/// Registered by the lane plugin (service <c>deployments</c>); the runner
/// consults it to decide lane admission vs. direct execution. Returns null
/// for a model with no pool binding (legacy/direct path).
/// </summary>
public interface IDeploymentPolicySource
{
    DeploymentPolicy? PolicyFor(string modelId);
}

/// <summary>
/// Fenced authority for one assignment to use one lane (astra-2 §3.3).
/// Internal to the host: it is never a model-visible argument and never
/// trusted from tool input. <see cref="HostGeneration"/> is the host process
/// generation that minted it — a stale generation's tokens die with the fence
/// on restart/reload (astra-2 §7: "fence all previous-host tokens").
/// </summary>
public sealed record LaneOwnershipToken(
    string PoolId,
    string? LaneId,
    string AssignmentId,
    string RunId,
    string HostGeneration,
    int Epoch,
    DateTimeOffset AcquiredAt)
{
    /// <summary>
    /// One-lane invariant: a token always carries exactly the one pool it
    /// covers (LaneId is display-level; PoolId is the authority).
    /// </summary>
    public bool CoversPool(string poolId) =>
        string.Equals(PoolId, poolId, StringComparison.Ordinal);
}

/// <summary>
/// The durable queue record (astra-2 §3.1): stored WORK, not a live task. A
/// queued assignment executes neither model calls nor tool batches; it may be
/// reconstructed once from storage on restart.
/// </summary>
public sealed record LaneQueueEntry(
    string AssignmentId,
    string PoolId,
    string DeploymentId,
    int ReadySequence,
    string? AgentId,
    string? RunId,
    string? SessionId,
    string Title,
    DateTimeOffset EnqueuedAt);

/// <summary>
/// A point-in-time snapshot of one pool for panels/diagnostics (astra-2 §13).
/// Read-only; publishing a snapshot never reserves capacity.
/// </summary>
public sealed record LanePoolSnapshot(
    string PoolId,
    bool Enabled,
    bool Draining,
    int OwnedCount,
    int? EffectiveCapacity,
    LaneCapacityMode CapacityMode,
    int? ProviderReportedConcurrency,
    int? UserCap,
    ProviderCapacityStatus ProviderStatus,
    DateTimeOffset? ProviderObservedAt,
    int QueueCount,
    string? BlockedReason)
{
    /// <summary>
    /// True while the pool is admitting (enabled, non-draining, capacity known
    /// and above current ownership).
    /// </summary>
    public bool CanAdmit =>
        Enabled && !Draining &&
        EffectiveCapacity is int eff && eff > OwnedCount &&
        BlockedReason is null;
}

/// <summary>
/// Admission result: either a fresh ownership token or a stable, explainable
/// queued status (position is an estimate, not a guarantee — astra-2 §3.1).
/// </summary>
public sealed record LaneAcquireResult(
    LaneOwnershipToken? Token,
    int QueuePosition,
    string? BlockedReason);

/// <summary>
/// The lane scheduler (astra-2 §5.1, service <c>lanes</c>): pool registry,
/// atomic admission, ownership tokens, ordered queue, explicit handoff,
/// permit validation, pool draining. A single serialized state machine —
/// callers get monotonic, deterministic answers; there is no distributed
/// scheduler and no round-robin.
/// </summary>
public interface ILaneScheduler
{
    /// <summary>
    /// Try to admit <paramref name="queue"/> now: returns a token when the
    /// pool admits and ownership is free, otherwise a queued result with the
    /// (estimated) position and the blocking reason. Never blocks.
    /// </summary>
    ValueTask<LaneAcquireResult> AcquireAsync(LaneQueueEntry queue);

    /// <summary>
    /// Release one token after the segment drained (model unwound, tool batch
    /// finished or cancelled). Idempotent per (assignment, pool, epoch): a
    /// double release or a stale epoch is a no-op, not an error.
    /// </summary>
    ValueTask ReleaseAsync(LaneOwnershipToken token);

    /// <summary>
    /// Explicit handoff (astra-2 §6.2): the parent's token transfers to
    /// <paramref name="toEntry"/>, in the same pool, atomically with the parent's
    /// release. Valid only while the from-token is still owned and both are on
    /// the same pool; a mismatched/stale token yields null and changes
    /// nothing. The caller is responsible for the suspension transaction
    /// (astra-2 §6.3). Returns the child's NEW ownership token — the runtime
    /// must hold that exact reference as its authority (permits are
    /// reference-checked, so a reconstructed copy never validates).
    /// </summary>
    ValueTask<LaneOwnershipToken?> HandoffAsync(LaneOwnershipToken fromToken, LaneQueueEntry toEntry);

    /// <summary>
    /// Validate a token as the ownership authority for this (pool,
    /// assignment) pair (astra-2 §3.3). Re-validated before every model call
    /// and every lane-scoped operation; a stale epoch or foreign generation
    /// fails closed.
    /// </summary>
    bool TryValidatePermit(LaneOwnershipToken? token, string poolId, string assignmentId);

    /// <summary>
    /// Cancel a queued assignment (astra-2 §9/§13): removes it from the queue.
    /// Returns true when a queued record was removed. Terminal/owned work is
    /// not touched here — cancellation of live segments goes through the
    /// runner/orchestrator, which releases the token after drain.
    /// </summary>
    ValueTask<bool> CancelQueuedAsync(string assignmentId);

    /// <summary>One snapshot per pool, including queue counts (astra-2 §13).</summary>
    IReadOnlyList<LanePoolSnapshot> Snapshots();

    /// <summary>
    /// Stop admitting for a pool (astra-2 §5.2: disable means drain). Current
    /// owners finish; the queue stays visible with a blocked reason.
    /// </summary>
    ValueTask SetPoolEnabledAsync(string poolId, bool enabled);
}

/// <summary>
/// astra-2 §3.1/§4: a narrow, SEPARATE interface for "a queued assignment was
/// admitted from the FIFO queue — start its segment." Kept off
/// <see cref="ILaneScheduler"/> on purpose: the runner subscribes to it, and
/// test fakes of the scheduler need not implement it (an unrelated concern —
/// admission is the trigger that starts stored queued work; the scheduler's
/// own tests exercise it through the concrete type).
/// The handler must be non-blocking and fast: it is invoked AFTER the
/// admission lock is released, once per newly admitted token.
/// </summary>
public interface ILaneAdmissionSink
{
    /// <summary>Subscribe to receive queue-admission notifications.</summary>
    void OnAdmittedFromQueue(Action<LaneOwnershipToken> handler);
}
