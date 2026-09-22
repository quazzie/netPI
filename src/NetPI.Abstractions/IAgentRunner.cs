namespace NetPI.Abstractions;

/// <summary>
/// Request to start one agent run (PLAN §10, §41). Transport-neutral: the Web
/// plugin builds this from a chat.send command; the agent plugin executes it.
/// </summary>
public sealed record AgentRunRequest(
    string? SessionId,
    string? WorkspacePath,
    string? ModelId,
    string Text,
    string? ReasoningLevel = null,
    float? Temperature = null,
    IReadOnlyList<AgentMessage>? PriorMessages = null,
    string? RunId = null)
{
    /// <summary>
    /// The caller-provided run identity (astra-2 §7): when present the runner uses
    /// this id for the run record, its terminal <c>AgentEvent.RunId</c>, and any
    /// persistence mapping. This is what makes the orchestration store's
    /// run_id (= operation id) reconcile against the runner's events. Null keeps
    /// the legacy runner-minted Guid.
    /// </summary>
    public override string ToString() => $"run[{SessionId ?? "new"}] {Text.Length} chars";
}

/// <summary>Outcome of starting a run (the run itself streams via the event bus).</summary>
/// <summary>
/// How an accepted submission was admitted (astra-2 §13): full capacity is an
/// ACCEPTED queue, not a rejection.
/// </summary>
public enum RunDisposition
{
    /// <summary>The segment executes now (or the deployment needs no admission).</summary>
    Admitted = 0,

    /// <summary>Accepted and persisted; awaiting capacity/pool policy. The session stays open in the UI.</summary>
    Queued = 1,
}

/// <summary>Outcome of starting a run (the run itself streams via the event bus).</summary>
public sealed record AgentRunStart(string? SessionId, string? Note, string? RunId = null, RunDisposition Disposition = RunDisposition.Admitted);

/// <summary>
/// Starts and cancels agent runs. Implemented by the agent plugin; resolved by
/// the Web plugin through the service registry (PLAN §10, §36). Lives in
/// Abstractions so Web and Agent never reference each other's ALC (reload-safe).
/// </summary>
public interface IAgentRunner
{
    /// <summary>Kick off a run (background); returns once it has started.</summary>
    ValueTask<AgentRunStart> StartRunAsync(AgentRunRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// astra-2 §7: re-enter a durable accepted run into the admission pipeline after
    /// a restart/reload (one-time adoption of a store's Queued records). Re-runs
    /// the same admission decision: pooled → re-acquire/queue on the lane
    /// scheduler; direct → starts (or refuses at the runner's own capacity).
    /// No-op (false) when the run id is unknown or already executing.
    /// </summary>
    ValueTask<bool> RequeueRunAsync(AgentRunRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// astra-2 §6.3: quiesce the run's CURRENT segment into a suspended durable
    /// wait (the runner-side half of the suspension transaction — the
    /// orchestrator owns the wake condition and the resume). Returns true when
    /// the run was live (Running) and is now Suspended; false when it was
    /// already terminal, already suspended, or unknown.
    ///
    /// This is NOT a terminal outcome (no AgentCompleted/AgentFailed/
    /// AgentCancelled is published) and it is not a cancellation (the run is
    /// not signalled). Instead a RunSuspended lifecycle event is published so
    /// subscribers (the orchestrator) know the run entered a durable wait and
    /// can reconcile it. A suspended run holds NO lane (its token is released
    /// for normal admission or handoff) and holds no blocking claim on its
    /// session: IsRunning/GetSessionRun ignore Suspended, so a suspended run
    /// never blocks a new send. The record stays registered so the later
    /// resume — RequeueRunAsync with the SAME RunId, which re-enters admission
    /// as a fresh segment — can re-admit it.
    /// </summary>
    ValueTask<bool> SuspendRunAsync(string runId, CancellationToken cancellationToken = default);

    /// <summary>Cancel the in-flight run, if any.</summary>
    ValueTask CancelRunAsync(CancellationToken cancellationToken = default);

    /// <summary>True while ANY run is executing — aggregate (PLAN §10, Package E). Includes
    /// preparation and cleanup so the host's idle reload gate stays simple.</summary>
    bool IsRunning { get; }

    /// <summary>astra-1 E: all runs the runner owns (active + finished, newest first).</summary>
    IReadOnlyList<RunInfo> ListRuns();

    /// <summary>astra-1 E: a specific run by its id (null when unknown).</summary>
    RunInfo? GetRun(string runId);

    /// <summary>astra-1 E: the ACTIVE run for a session (null when none).</summary>
    RunInfo? GetSessionRun(string sessionId);

    /// <summary>astra-1 E: cancel one specific run. True when a live run was signalled.</summary>
    /// <summary>astra-1 E: cancel one specific run. True when a live run was signalled.</summary>
    bool CancelRun(string runId);

    /// <summary>
    /// astra-2 §13: cancel one QUEUED (admitted but not yet started) run. A queued
    /// run holds no live segment, so <see cref="CancelRun"/> cannot reach it — this
    /// removes the queued record, signals its cancellation token, and purges the
    /// lane scheduler's queue entry (so a racing admission fires into a cancelled
    /// token and unwinds as cancelled). True when a queued run was cancelled.
    /// </summary>
    ValueTask<bool> CancelQueuedRun(string runId, CancellationToken cancellationToken = default);

    /// <summary>
    /// astra-1 D2 (slice 2): the per-session gate that serializes the send
    /// critical section with the run's safe-boundary project apply. Held by the
    /// runner across StartRunAsync's critical section and its boundary apply;
    /// the Web surface's session.project command acquires it for an idle apply
    /// so the three (send / project-change / compaction) never race per session.
    /// The default no-op keeps pre-D2 implementations compatible.
    /// </summary>
    System.Threading.SemaphoreSlim SessionGate(string sessionId);
}

/// <summary>Terminal / in-flight state of a single run (Package E).</summary>
public enum RunState
{
    /// <summary>Preparation, model call, or tool execution still in flight.</summary>
    Running = 0,
    /// <summary>The run finished cleanly (assistant turn closed, no error).</summary>
    Completed = 1,
    /// <summary>The run was cancelled by an explicit cancel before it finished.</summary>
    Cancelled = 2,
    /// <summary>The run escaped the model loop with an unhandled exception.</summary>
    Failed = 3,
    /// <summary>
    /// astra-2 §6.3: the segment quiesced into a durable wait (suspension
    /// transaction). This is NOT a terminal outcome — a later resume
    /// (RequeueRunAsync with the same RunId) starts a new segment. A Suspended
    /// run holds no lane and is not "live": IsRunning/GetSessionRun ignore it,
    /// so it never blocks a new send on its session.
    /// </summary>
    Suspended = 4,
}

/// <summary>Read-only summary of one run (Package E — the run-query contract).</summary>
public sealed record RunInfo(
    string RunId,
    string? SessionId,
    string? ModelId,
    AgentState State,
    DateTimeOffset StartTime,
    DateTimeOffset? EndTime,
    RunState Outcome)
{
}
