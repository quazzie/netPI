namespace NetPI.Abstractions;

/// <summary>
/// astra-2 §7: a durable Queued assignment's re-entry facts (one-time adoption
/// after a restart). <see cref="OperationId"/> is the store's <c>run_id</c> —
/// the runner re-enters it under that id so the terminal event reconciles back
/// to this assignment; <see cref="Brief"/> is the run's prompt text.
/// </summary>
public sealed record QueuedAdoptionInfo(
    string AssignmentId,
    string OperationId,
    string SessionId,
    string? ModelId,
    string? PoolId,
    string? DeploymentId,
    string? Brief);

// astra-2 (docs/plans/astra-2.md §2, §6, §7, §9): the logical assignment
// lifecycle, team/agent identity, mailboxes and the orchestrator contracts.
// Logical lifecycle is SEPARATE from the execution phase (AgentState): a
// session can be queued, waiting, or suspended while every execution-phase
// field of the runner still reads Idle.

/// <summary>
/// Logical assignment lifecycle (astra-2 §6.1). Stable serialized values:
/// the wire/panel always uses these lower-case strings, never C# enum
/// <c>ToString()</c> output.
/// </summary>
public enum AgentAssignmentLifecycle
{
    /// <summary>Accepted, awaiting capacity or an enabled pool. No lane, no execution.</summary>
    Queued = 0,

    /// <summary>One live execution segment (pooled: owns a lane; direct cloud: no lane).</summary>
    Running = 1,

    /// <summary>
    /// Durable checkpoint, awaiting a child/message/dependency condition.
    /// Entered only by an explicit wait/delegate action; a foreground tool
    /// waiting on process output is still Running.
    /// </summary>
    Waiting = 2,

    /// <summary>Durable checkpoint, explicitly paused or awaiting user/recovery action.</summary>
    Suspended = 3,

    /// <summary>Cancel signalled; no new model calls; awaiting drain.</summary>
    Cancelling = 4,

    /// <summary>Terminal, clean.</summary>
    Completed = 5,

    /// <summary>Terminal, error. <see cref="AgentAssignmentRow.Reason"/> explains.</summary>
    Failed = 6,

    /// <summary>Terminal, explicitly cancelled.</summary>
    Cancelled = 7,
}

/// <summary>
/// The serialized form of <see cref="AgentAssignmentLifecycle"/> — the value
/// every panel, event payload and store row carries (astra-2 §12.2:
/// documented lower-case strings; never compare against enum ToString).
/// </summary>
public static class AgentAssignmentLifecycleNames
{
    public static string Name(AgentAssignmentLifecycle l) => l switch
    {
        AgentAssignmentLifecycle.Queued => "queued",
        AgentAssignmentLifecycle.Running => "running",
        AgentAssignmentLifecycle.Waiting => "waiting",
        AgentAssignmentLifecycle.Suspended => "suspended",
        AgentAssignmentLifecycle.Cancelling => "cancelling",
        AgentAssignmentLifecycle.Completed => "completed",
        AgentAssignmentLifecycle.Failed => "failed",
        AgentAssignmentLifecycle.Cancelled => "cancelled",
        _ => "unknown",
    };

        public static bool TryParse(string? name, out AgentAssignmentLifecycle l)
    {
        switch (name)
        {
            case "queued": l = AgentAssignmentLifecycle.Queued; return true;
            case "running": l = AgentAssignmentLifecycle.Running; return true;
            case "waiting": l = AgentAssignmentLifecycle.Waiting; return true;
            case "suspended": l = AgentAssignmentLifecycle.Suspended; return true;
            case "cancelling": l = AgentAssignmentLifecycle.Cancelling; return true;
            case "completed": l = AgentAssignmentLifecycle.Completed; return true;
            case "failed": l = AgentAssignmentLifecycle.Failed; return true;
            case "cancelled": l = AgentAssignmentLifecycle.Cancelled; return true;
            default: l = AgentAssignmentLifecycle.Queued; return false;
        }
    }
}

/// <summary>
/// Stable lower-case names for the execution-phase <see cref="AgentState"/> — the value
/// the store persists and the panels render (astra-2 §12.2: documented strings,
/// never enum ToString). Kept beside the lifecycle names so the canonical
/// projection has exactly one string vocabulary.
/// </summary>
public static class AgentStateNames
{
    public static string Name(AgentState s) => s switch
    {
        AgentState.Idle => "idle",
        AgentState.Preparing => "preparing",
        AgentState.CallingModel => "calling-model",
        AgentState.ExecutingTools => "executing-tools",
        AgentState.Compacting => "compacting",
        AgentState.Retrying => "retrying",
        AgentState.Cancelling => "cancelling",
        _ => "unknown",
    };

        public static bool TryParse(string? name, out AgentState s)
    {
        switch (name)
        {
            case "idle": s = AgentState.Idle; return true;
            case "preparing": s = AgentState.Preparing; return true;
            case "calling-model": s = AgentState.CallingModel; return true;
            case "executing-tools": s = AgentState.ExecutingTools; return true;
            case "compacting": s = AgentState.Compacting; return true;
            case "retrying": s = AgentState.Retrying; return true;
            case "cancelling": s = AgentState.Cancelling; return true;
            default: s = AgentState.Idle; return false;
        }
    }
}

/// <summary>Stable lower-case names for <see cref="DeploymentExecutionMode"/> (astra-2 §11).</summary>
public static class DeploymentExecutionModeNames
{
    public static string Name(DeploymentExecutionMode m) => m switch
    {
        DeploymentExecutionMode.Pooled => "pooled",
        DeploymentExecutionMode.DirectCloud => "direct-cloud",
        _ => "unknown",
    };

        public static bool TryParse(string? name, out DeploymentExecutionMode m)
    {
        switch (name)
        {
            case "pooled": m = DeploymentExecutionMode.Pooled; return true;
            case "direct-cloud": m = DeploymentExecutionMode.DirectCloud; return true;
            default: m = DeploymentExecutionMode.Pooled; return false;
        }
    }
}

/// <summary>
/// One assignment (logical run) as the canonical lifecycle projection —
/// the single row shape for the Work panel, <c>agents.state</c> snapshots
/// and run queries (astra-2 §12.2: one canonical query projection, no
/// second registry). A session has at most ONE nonterminal assignment
/// (enforced by the store); terminal rows are history.
/// </summary>
public sealed record AgentAssignmentRow(
    string AssignmentId,
    string AgentId,
    string? TeamId,
    string SessionId,
    string? ParentAgentId,
    AgentAssignmentLifecycle Lifecycle,
    AgentState Phase,
    DeploymentExecutionMode ExecutionMode,
    string? PoolId,
    string? LaneId,
    string? DeploymentId,
    string? ModelId,
    string Title,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? EndedAt,
    string? Reason)
{
    /// <summary>
    /// Monotonic CAS version: the store increments it on every successful
    /// <c>TransitionAsync</c>. A reader uses it to compare-and-swap back; a stale
    /// version loses (astra-2 §7: no silent overwrite of a newer transition).
    /// </summary>
    public int Version { get; init; } = 0;

    /// <summary>
    /// True while the assignment still holds a logical slot (queued, running,
    /// waiting, cancelling, suspended). The panel and the session-busy gate
    /// both key off this — NOT off a lane, so a queued/suspended record is
    /// never mistaken for free.
    /// </summary>
    public bool IsNonTerminal =>
        Lifecycle is AgentAssignmentLifecycle.Queued
        or AgentAssignmentLifecycle.Running
        or AgentAssignmentLifecycle.Waiting
        or AgentAssignmentLifecycle.Cancelling
        or AgentAssignmentLifecycle.Suspended;
}

/// <summary>
/// One agent: stable participant identity backed by exactly one session
/// (astra-2 §2). Roots are user-created; children are spawned/delegated.
/// </summary>
public sealed record AgentIdentity(
    string AgentId,
    string? TeamId,
    string? ParentAgentId,
    string SessionId,
    string Title,
    bool IsChild,
    DateTimeOffset CreatedAt);

/// <summary>Bounded result of a finished child, delivered to the parent (astra-2 §8).</summary>
public sealed record AgentChildResult(
    string ChildAgentId,
    string ChildSessionId,
    string RunId,
    AgentAssignmentLifecycle Outcome,
    string Summary,
    IReadOnlyList<string>? ArtifactPaths = null,
    IReadOnlyList<string>? UnresolvedIssues = null);

/// <summary>
/// astra-2 §6.2/§6.3: the acceptance receipt for a delegation — the child's
/// identity + assignment plus the parent's assignment and post-suspension
/// lifecycle. The child's actual result is delivered LATER, as a separate
/// bounded mailbox event when the child's wait is satisfied (the tool never
/// blocks waiting for the child to finish).
/// </summary>
public sealed record AgentDelegateResult(
    AgentIdentity ChildAgent,
    string ChildAssignmentId,
    string ChildSessionId,
    string ParentAssignmentId,
    AgentAssignmentLifecycle ParentStatus,
    string? Reason);

/// <summary>
/// astra-2 §6.3: one satisfied wait ready for resume — the wait's identity,
/// the awaited targets, and the parent's durable run id (resume = RequeueRunAsync
/// with the same run id).
/// </summary>
public sealed record AgentSatisfiedWait(
    string WaitId,
    IReadOnlyList<AgentWaitTarget> Targets,
    string? AssignmentId,
    string? RunId);

/// <summary>
/// A durable, addressed message between agents (astra-2 §9). Ordered
/// per-recipient via <see cref="RecipientSequence"/>; bounded body; sender
/// and kind metadata survive compaction. Consumption is cursor-based and
/// idempotent (delivery + consumption are transactional — no re-delivery,
/// no double consumption).
/// </summary>
public sealed record AgentMailboxMessage(
    string MessageId,
    string FromAgentId,
    string ToAgentId,
    string? TeamId,
    string Kind,
    string Body,
    int RecipientSequence,
    DateTimeOffset SentAt,
    string? IdempotencyKey = null);

/// <summary>
/// One task-board record with explicit dependencies (astra-2 §9).
/// Dependencies must form a DAG (cycles rejected at create time).
/// </summary>
public sealed record AgentTaskRecord(
    string TaskId,
    string TeamId,
    string Title,
    string? OwnerAgentId,
    string Status,
    IReadOnlyList<string> DependsOnTaskIds,
    int Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>
/// The orchestrator (astra-2 §5.1, service <c>orchestration</c>): team/agent
/// lifecycle, mailboxes, waits, task board, budgets. Agent TOOLs are thin
/// wrappers over these operations with trusted runtime identity — tools
/// may NAME a target but cannot impersonate it (astra-2 §3.3).
/// </summary>
public interface IAgentOrchestrator
{
    /// <summary>
    /// Create a child agent + independent session + queued assignment,
    /// atomically (astra-2 §7). Returns the created identity plus the
    /// assignment's admission status. Idempotent per <c>operationId</c>:
    /// a repeated spawn with the same operation id returns the original
    /// records, never a second child.
    /// </summary>
    ValueTask<AgentSpawnResult> SpawnChildAsync(
        string parentAgentId,
        AgentSpawnRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Enqueue a durable message to an existing agent without triggering any
    /// inference (astra-2 §9). Returns the assigned recipient sequence.
    /// </summary>
    ValueTask<int> SendMessageAsync(
        string fromAgentId, string toAgentId, string kind, string body,
        string? idempotencyKey = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Read and advance the recipient's consumption cursor — up to
    /// <paramref name="count"/> unread messages, oldest first.
    /// </summary>
    ValueTask<IReadOnlyList<AgentMailboxMessage>> DrainMailboxAsync(
        string agentId, int count, CancellationToken cancellationToken);

    /// <summary>Register a wait condition; the scheduler wakes the agent when it is met (astra-2 §6.2).</summary>
    ValueTask RegisterWaitAsync(string agentId, AgentWaitCondition condition, CancellationToken cancellationToken);

    /// <summary>
    /// Current logical state of one assignment (null when the assignment is
    /// unknown). Panels and tools query here; never keep a second cache.
    /// </summary>
    ValueTask<AgentAssignmentRow?> GetAssignmentAsync(string assignmentId, CancellationToken cancellationToken);

    /// <summary>
    /// The nonterminal assignment for a session (null when the session is
    /// logically free). The session-busy gate's source of truth.
    /// </summary>
    ValueTask<AgentAssignmentRow?> GetNonterminalAsync(string sessionId, CancellationToken cancellationToken);

    /// <summary>All nonterminal assignments across every session/team (astra-2 §12.1: the Agents section lists ALL of them).</summary>
    ValueTask<IReadOnlyList<AgentAssignmentRow>> ListAssignmentsAsync(CancellationToken cancellationToken);

    /// <summary>
    /// astra-2 §6.2: the nonterminal assignments of <paramref name="agentId"/> and
    /// all its descendants (the wait-dependency cycle check). Empty for an
    /// unknown agent.
    /// </summary>
    ValueTask<IReadOnlyList<AgentAssignmentRow>> ListSubtreeAsync(string agentId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Cancel one assignment: terminal-ize it (and, with <c>subtree</c>, its
    /// descendants). Idempotent — cancelling an already-terminal assignment
    /// returns the recorded outcome.
    /// </summary>
    /// <summary>
    /// astra-2 §6.2/§6.3: spawn a child agent, register a durable wait of the
    /// parent on the child's assignment, then quiesce the parent's live segment
    /// (SuspendRunAsync — no further model call in this segment; the lane is
    /// released so the child can be admitted for it). When the child reaches a
    /// terminal outcome, the wait is satisfied and the parent is resumed with a
    /// bounded result in its mailbox. Idempotent by the delegation operation id.
    /// </summary>
    ValueTask<AgentDelegateResult> DelegateAsync(
        string parentAgentId, AgentSpawnRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// astra-2 §6.3 step 5: consume satisfied durable waits of <paramref name="agentId"/>
    /// and resume each (RequeueRunAsync with the SAME run id + a bounded result in the
    /// mailbox). Returns the number of parents resumed. Waking is never permission to
    /// execute without a lane — resume re-enters admission.
    /// </summary>
    ValueTask<int> ConsumeSatisfiedWaitsAsync(string agentId, CancellationToken cancellationToken);

    ValueTask<AgentAssignmentLifecycle> CancelAsync(
        string assignmentId, bool subtree, CancellationToken cancellationToken);

    /// <summary>
    /// Explicit follow-up for a terminal agent: a NEW assignment in the same
    /// session, admitted through the same policy as the original
    /// (astra-2 §9 — a follow-up is a new run, not a lane entitlement).
    /// </summary>
    ValueTask<AgentSpawnResult> ContinueAsync(
        string agentId, string text, string? operationId, CancellationToken cancellationToken);
}

/// <summary>Inputs for <see cref="IAgentOrchestrator.SpawnChildAsync"/>.</summary>
public sealed record AgentSpawnRequest
{
    /// <summary>The task brief (the child's first user message).</summary>
    public required string Brief { get; init; }

    /// <summary>Stable operation id for idempotent retries (astra-2 §7).</summary>
    public required string OperationId { get; init; }

    /// <summary>Pool to admit into (pooled deployment) — null for a direct-cloud deployment.</summary>
    public string? PoolId { get; init; }

    /// <summary>The deployment the child is pinned to (astral-2 §5.2: execution policy comes from trusted config, never the model).</summary>
    public string? DeploymentId { get; init; }

    /// <summary>
    /// Workspace mode (astra-2 §8): "shared-read" (read-only tool policy),
    /// "isolated-worktree" (independent changes from a recorded base), or
    /// "shared-write" (explicit file ownership). Advisory-only restrictions
    /// must say so.
    /// </summary>
    public string WorkspaceMode { get; init; } = "shared-read";

    /// <summary>Explicit full-parent-transcript inheritance (astra-2 §8: NEVER the default).</summary>
    public bool InheritParentTranscript { get; init; }

    /// <summary>Selected facts/file references folded into the child brief.</summary>
    public IReadOnlyList<string>? ContextRefs { get; init; }
}

/// <summary>
/// Result of a spawn/continue: the created/located agent plus the assignment
/// outcome (admitted now, queued, or rejected with a reason).
/// </summary>
public sealed record AgentSpawnResult(
    AgentIdentity Agent,
    string AssignmentId,
    string SessionId,
    AgentAssignmentLifecycle Status,
    string? Reason = null);

/// <summary>
/// A wait condition (astra-2 §6.2): the assignment enters <c>Waiting</c> and
/// holds no lane while it is open. Satisfied when any/all of the referenced
/// runs reach a terminal outcome, or a message of <see cref="MessageKind"/>
/// arrives from one of the referenced agents. Deadlines are persisted wake
/// events — the scheduler never polls with a model call.
/// </summary>
public sealed record AgentWaitCondition
{
    /// <summary>Assignment ids to await terminal outcomes for.</summary>
    public IReadOnlyList<string> AssignmentIds { get; init; } = [];

    /// <summary>Wake on a message from one of these agents.</summary>
    public IReadOnlyList<string> MessageFromAgentIds { get; init; } = [];

    /// <summary>Only wake for this message kind (null = any kind).</summary>
    public string? MessageKind { get; init; }

    /// <summary>Require all conditions vs any (default any).</summary>
    public bool RequireAll { get; init; }

    /// <summary>Optional deadline; a missed deadline is a persisted wake event, not a model loop.</summary>
    public DateTimeOffset? Deadline { get; init; }
}

// ---------------------------------------------------------------------------
// astra-2 §7 persistence contract. The storage plugin implements
// <see cref="IOrchestrationStore"/> over the same SQLite database (migration v4);
// the orchestration plugin consumes it through the service registry (id
// <c>orchestration-store</c>) so it never references the storage assembly.
// Every operation is idempotent by its durable key (operationId / message
// idempotency key / wait id) — a repeated call returns the original record,
// and a repeated key with different arguments is a conflict, not a new row.
// ---------------------------------------------------------------------------

/// <summary>
/// A wait target: either a referenced assignment (terminal-outcome condition) or
/// a message from a set of agents. Serialized as the <c>targets_json</c> blob.
/// </summary>
public sealed record AgentWaitTarget(string? AssignmentId, IReadOnlyList<string> MessageFromAgentIds, string? MessageKind)
{
    public AgentWaitTarget() : this(null, [], null) { }
}

/// <summary>Outcome of a child/continue spawn — the created or located agent plus admission status.</summary>
public sealed record AgentSpawnOutcome(AgentIdentity Agent, string AssignmentId, string SessionId, AgentAssignmentLifecycle Status, string? Reason)
{
    public AgentSpawnResult ToResult() => new(Agent, AssignmentId, SessionId, Status, Reason);
}

public interface IOrchestrationStore
{
    // ---- agents ---------------------------------------------------------
    /// <summary>
    /// Idempotently ensure a root agent for a session (astra-2 §7: created lazily
    /// on a new accepted submission; old sessions gain their AgentId here, never
    /// as fake active runs). Returns the existing agent when one is bound.
    /// </summary>
    ValueTask<AgentIdentity> EnsureRootAgentAsync(string sessionId, string? teamId, string title, CancellationToken ct = default);

    /// <summary>
    /// Atomically create a CHILD agent + its own session + a Queued assignment in
    /// ONE transaction (astra-2 §7). Idempotent by <paramref name="operationId"/>.
    /// </summary>
    ValueTask<AgentSpawnOutcome> SpawnChildAsync(
        string operationId, string? parentAgentId, string? teamId, string modelId,
        string? poolId, string? deploymentId, string brief, string title, CancellationToken ct = default);

    /// <summary>Look up an agent by id (null when unknown).</summary>
    ValueTask<AgentIdentity?> GetAgentAsync(string agentId, CancellationToken ct = default);

    /// <summary>Look up the agent bound to a session (null when the session has no agent yet).</summary>
    ValueTask<AgentIdentity?> GetAgentBySessionAsync(string sessionId, CancellationToken ct = default);

    // ---- assignments (the canonical lifecycle projection) ---------------
    /// <summary>
    /// Compare-and-swap a lifecycle/phase/pool transition on one assignment
    /// (astra-2 §7 version). Returns false when the expected version does not
    /// match (someone else transitioned first) — the caller must re-read, not retry blindly.
    /// </summary>
    ValueTask<bool> TransitionAsync(
        string assignmentId, int expectedVersion, AgentAssignmentLifecycle lifecycle, AgentState phase,
        string? poolId, string? laneId, string? deploymentId, string? reason, string? checkpointRef,
        CancellationToken ct = default);

    /// <summary>Current state of one assignment (null when unknown).</summary>
    ValueTask<AgentAssignmentRow?> GetAssignmentAsync(string assignmentId, CancellationToken ct = default);

    /// <summary>The nonterminal assignment for a session (null when the session is logically free).</summary>
    ValueTask<AgentAssignmentRow?> GetNonterminalAsync(string sessionId, CancellationToken ct = default);

    /// <summary>All nonterminal assignments across every session/team.</summary>
    ValueTask<IReadOnlyList<AgentAssignmentRow>> ListNonterminalAsync(CancellationToken ct = default);

    /// <summary>
    /// astra-2 §7: durable Queued assignments in ready order, for one-time adoption
    /// after a restart/reload (the new generation re-enters them into the lane
    /// pipeline exactly once — no old callbacks, no lost work).
    /// </summary>
    ValueTask<IReadOnlyList<QueuedAdoptionInfo>> ListQueuedForAdoptionAsync(CancellationToken ct = default);
    /// <summary>
    /// The nonterminal assignment that owns a runner run id (null when the run
    /// has no live assignment or is already terminal) — astra-2 §6.3 reconciliation:
    /// the runtime reports segment outcomes keyed by RunId, not assignmentId.
    /// </summary>
    ValueTask<AgentAssignmentRow?> GetByRunIdAsync(string runId, CancellationToken ct = default);

    /// <summary>
    /// astra-2 §6.3: the durable run/operation id for an assignment (the store's
    /// run_id). A delegated parent is suspended and later resumed by this id —
    /// resume = RequeueRunAsync with the same run id.
    /// </summary>
    ValueTask<string?> GetRunIdAsync(string assignmentId, CancellationToken ct = default);

    /// <summary>
    /// Every nonterminal assignment in an agent's descendant subtree (the agent
    /// itself and, recursively, all of its children) — astra-2 §9 subtree cancel.
    /// The walk follows <c>parent_agent_id</c> links downward.
    /// </summary>
    ValueTask<IReadOnlyList<AgentAssignmentRow>> ListSubtreeAsync(string agentId, CancellationToken ct = default);

    /// <summary>
    /// Create a fresh assignment for an EXISTING session (astra-2 §9 continue: a
    /// terminal agent's follow-up is a new assignment in its same session).
    /// Idempotent by <paramref name="operationId"/> (persisted as the run id); a
    /// repeated call with the same op id returns the original assignment. The
    /// one-nonterminal-per-session gate is enforced: if the session already has a
    /// nonterminal assignment, the new one is created Queued behind it.
    /// </summary>
    ValueTask<AgentAssignmentRow> CreateAssignmentAsync(
        string operationId, string agentId, string sessionId, string? teamId,
        string? parentAgentId, string? modelId, string? poolId, string? deploymentId,
        string title, string? briefRef, CancellationToken ct = default);

    /// <summary>Set a session's workspace path (astra-2 §8: the child inherits the parent's workspace). No-op-safe.</summary>
    ValueTask SetSessionWorkspaceAsync(string sessionId, string? workspace, CancellationToken ct = default);

    /// <summary>Bounded recent-terminal history (newest ended first) — the panel's `history` section.</summary>
    ValueTask<IReadOnlyList<AgentAssignmentRow>> ListRecentTerminalAsync(int limit, CancellationToken ct = default);

    // ---- mailboxes -------------------------------------------------------
    /// <summary>
    /// Enqueue a durable message to an agent (no inference). Idempotent by
    /// <paramref name="idempotencyKey"/> (null = fresh). Returns the assigned recipient sequence.
    /// </summary>
    ValueTask<int> SendMessageAsync(string messageId, string fromAgentId, string toAgentId, string? teamId,
        string kind, string body, IReadOnlyList<string>? artifacts, string? idempotencyKey, CancellationToken ct = default);

    /// <summary>Read + advance the recipient's consumption cursor (up to <paramref name="count"/> unread, oldest first).</summary>
    ValueTask<IReadOnlyList<AgentMailboxMessage>> DrainMailboxAsync(string agentId, int count, CancellationToken ct = default);

    /// <summary>Wake a waiter when a message of <paramref name="kind"/> from <paramref name="fromAgentId"/> arrives (true when a wait was satisfied).</summary>
    ValueTask<bool> NoteMessageForWaitsAsync(string toAgentId, string fromAgentId, string kind, CancellationToken ct = default);

    // ---- waits / wake ---------------------------------------------------
    /// <summary>Register a wait condition (astra-2 §6.2); the assignment holds no lane while open. Idempotent by wait id.</summary>
    ValueTask RegisterWaitAsync(string waitId, string agentId, string assignmentId, AgentWaitCondition condition, CancellationToken ct = default);

    /// <summary>Wake waiters of <paramref name="assignmentId"/> once it reaches a terminal outcome (true when any were satisfied).</summary>
    ValueTask<bool> NoteTerminalForWaitsAsync(string assignmentId, CancellationToken ct = default);

    /// <summary>
    /// astra-2 §6.3 step 5: the satisfied (but not yet consumed) waits of one
    /// agent — the resume consumer re-enters the parent for each. A satisfied
    /// wait persists until consumed, so a crash/kill between satisfaction and
    /// resume never loses the wake (no lost-wakeup window).
    /// </summary>
    ValueTask<IReadOnlyList<AgentSatisfiedWait>> ListSatisfiedWaitsAsync(string agentId, CancellationToken ct = default);

    /// <summary>
    /// astra-2 §6.3: mark a satisfied wait consumed AFTER the resume was re-
    /// queued — the consumption is part of the wake so a consumer crash retries
    /// exactly once per wait, never zero or twice.
    /// </summary>
    ValueTask MarkWaitConsumedAsync(string waitId, CancellationToken ct = default);
}
