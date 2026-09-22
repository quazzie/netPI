using System.Text.Json;

namespace NetPI.Abstractions;

/// <summary>
/// astra-2 §13: assignment lifecycle events. Published by the orchestration
/// plugin AFTER a durable <see cref="IOrchestrationStore"/> transition commits
/// (never from a live in-memory queue that a reload would forget). The Web
/// surface forwards these to the shell as <c>agent.updated</c> (one row) and
/// <c>agents.state</c> (a bounded batch); the Work panel still polls its own
/// Kestrel surface for authoritative projections — these events are the SHELL
/// badge/capacity feed, not the panel's source of truth.
/// </summary>
public enum AgentLifecycleEventKind
{
    /// <summary>A single assignment transitioned (lifecycle/phase/pool changed).</summary>
    Updated = 0,

    /// <summary>A batch of assignments changed (spawn, subtree cancel, reconcile).</summary>
    Batch = 1
}

public sealed record AgentLifecycleEvent(
    AgentLifecycleEventKind Kind,
    IReadOnlyList<AgentAssignmentRow> Rows,
    string? TeamId = null,
    DateTimeOffset? At = null)
{
    public DateTimeOffset Timestamp => At ?? DateTimeOffset.UtcNow;
}

/// <summary>
/// astra-2 §13: lane/pool capacity snapshot, emitted when a pool's live
/// ownership, queue length, or target capacity changes. The Web surface
/// forwards this as <c>lanes.state</c> (the shell's capacity badge). The Work
/// panel still polls its own surface for the full per-pool detail.
/// </summary>
public sealed record LanesStateEvent(
    IReadOnlyList<AgentPoolSnapshot> Pools,
    DateTimeOffset? At = null)
{
    public DateTimeOffset Timestamp => At ?? DateTimeOffset.UtcNow;
}

/// <summary>One pool row in a <see cref="LanesStateEvent"/> snapshot.</summary>
public sealed record AgentPoolSnapshot(
    string PoolId,
    string DeploymentId,
    string ModelId,
    int OwnedCount,
    int QueueCount,
    int? TargetCapacity,
    bool Enabled,
    string? BlockReason = null);
