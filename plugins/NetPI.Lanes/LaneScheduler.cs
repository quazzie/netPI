using NetPI.Abstractions;

namespace NetPI.Lanes;

/// <summary>
/// astra-2: the concrete lane scheduler — one serialized state machine
/// (astra-2 §5.1). Admission is never round-robin, never time-sliced and
/// never evicts an owner: a pool admits while
/// <c>owned &lt; effective capacity</c>, holds everyone else behind a FIFO
/// durable queue, and drains (disable / capacity reduction) without
/// preemption (astra-2 §3.1, §5.2). Ownership is counted, not current HTTP
/// requests — an owner keeps its lane through tool batches, retry backoff
/// and in-run maintenance.
/// </summary>
public sealed class LaneScheduler : ILaneScheduler, ILaneAdmissionSink
{
    private sealed class PoolState
    {
        public required string Id;
        public bool Enabled = true;
        public bool Draining;
        public LaneCapacityMode Mode = LaneCapacityMode.Provider;
        public int? MaxAgents;
        public LaneUnknownPolicy UnknownPolicy = LaneUnknownPolicy.HoldNew;
        public required string DeploymentId;
        public required string ModelId;
        public int NextEpoch = 1;
        public int NextReadySequence = 1;
        public Dictionary<string, LaneOwnershipToken> Owners = new();
        public SortedDictionary<int, LaneQueueEntry> Queue = new();
        public ProviderCapacityObservation? LastObservation;
        public string? PinnedDeployment;
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, PoolState> _pools = new();
    private readonly string _hostGeneration;
    private readonly IPluginLogger _log;
    private int _nextEpoch;

    /// <summary>
    /// astra-2 §3.1/§4: fired (after the admission lock is released) when a
    /// queued assignment is admitted from the FIFO queue. The runner uses this
    /// to START the segment of a stored queued record — the record never held a
    /// live blocking task; admission is the single trigger that starts it.
    /// Immediate admissions from a fresh <see cref="AcquireAsync"/> do NOT fire
    /// this (the caller already holds the token).
    /// </summary>
    public event Action<LaneOwnershipToken>? AdmittedFromQueue;

    /// <inheritdoc/>
    public void OnAdmittedFromQueue(Action<LaneOwnershipToken> handler)
    {
        AdmittedFromQueue += handler;
    }

    /// <summary>
    /// Build a scheduler from the plugin's parsed pool/deployment registry.
    /// <paramref name="hostGeneration"/> fences ownership tokens: a
    /// generation that dies can never own a lane in a live generation
    /// (astra-2 §3.3, §7 — "fence all previous-host tokens").
    /// </summary>
    public LaneScheduler(string hostGeneration, IPluginLogger log)
    {
        _hostGeneration = hostGeneration;
        _log = log;
        _nextEpoch = ++GlobalEpochCounter;
    }

    private static int GlobalEpochCounter = 0;

    /// <summary>The deployment binding a pool is registered with (null when the pool is unknown).</summary>
    public string? DeploymentOf(string poolId)
    {
        lock (_gate)
            return _pools.TryGetValue(poolId, out var p) ? p.DeploymentId : null;
    }

    /// <summary>
    /// The model id a pool is bound to — the key the <see cref="IProviderCapacitySource"/>
    /// is observed with (null when the pool is unknown).
    /// </summary>
    public string? ModelOf(string poolId)
    {
        lock (_gate)
            return _pools.TryGetValue(poolId, out var p) ? p.ModelId : null;
    }

    /// <summary>Register one pool binding (idempotent on re-registration of the same config).</summary>
    public void RegisterPool(string poolId, string deploymentId, string modelId, LaneCapacityMode mode, int? maxAgents, bool enabled = true)
    {
        lock (_gate)
        {
            _pools[poolId] = new PoolState
            {
                Id = poolId,
                DeploymentId = deploymentId,
                ModelId = modelId,
                Mode = mode,
                MaxAgents = maxAgents,
                Enabled = enabled,
            };
        }
    }

    /// <summary>
    /// Reconcile durable queued work once after a restart (astra-2 §7).
    /// Entries with an in-memory ready sequence win ties deterministically.
    /// </summary>
    public void AdoptQueue(IEnumerable<LaneQueueEntry> entries)
    {
        lock (_gate)
        {
            foreach (var e in entries)
            {
                if (!_pools.TryGetValue(e.PoolId, out var p)) continue;
                var seq = p.Queue.Keys.Contains(e.ReadySequence) ? p.NextReadySequence++ : e.ReadySequence;
                p.Queue[seq] = e with { ReadySequence = seq };
            }
        }
    }

    /// <summary>
    /// Update a pool's capacity metadata from a provider observation
    /// (astra-2 §5.3 — metadata only, no inference). Stale/unknown metadata
    /// holds NEW admission; it never releases or steals an owned lane.
    /// </summary>
    public void UpdateProviderObservation(ProviderCapacityObservation observation)
    {
        var admitted = new List<LaneOwnershipToken>();
        try
        {
            lock (_gate)
            {
                // The observation is keyed by the pool's bound MODEL (the key the
                // capacity source uses); every pool bound to that model receives it.
                foreach (var p in _pools.Values.Where(x => string.Equals(x.ModelId, observation.DeploymentId, StringComparison.Ordinal)).ToList())
                {
                    p.LastObservation = observation;
                    admitted.AddRange(TryAdmitLocked(p, "capacity observation"));
                }
            }
        }
        finally
        {
            FireAdmittedFromQueue(admitted);
        }
    }

    public async ValueTask<LaneAcquireResult> AcquireAsync(LaneQueueEntry queue)
    {
        await ValueTask.CompletedTask;
        lock (_gate)
        {
            if (!_pools.TryGetValue(queue.PoolId, out var p))
                return new LaneAcquireResult(null, 0, $"unknown pool {queue.PoolId}");

            if (TryAdmitOneLocked(p, queue, out var token, out var blocked))
                return new LaneAcquireResult(token, 0, null);

            var seq = p.Queue.Keys.Contains(queue.ReadySequence) ? p.NextReadySequence++ : queue.ReadySequence;
            p.Queue[seq] = queue with { ReadySequence = seq };
            _log.Information($"lanes: queued {queue.AssignmentId} in {p.Id} (position ~{p.Queue.Count}) — {blocked}");
            return new LaneAcquireResult(null, p.Queue.Count, blocked);
        }
    }

    public async ValueTask ReleaseAsync(LaneOwnershipToken token)
    {
        await ValueTask.CompletedTask;
        var admitted = new List<LaneOwnershipToken>();
        try
        {
            lock (_gate)
            {
                if (token is null) return;
                if (!_pools.TryGetValue(token.PoolId, out var p)) return;
                if (!p.Owners.TryGetValue(token.AssignmentId, out var owned) || !ReferenceEquals(owned, token))
                    return; // stale/double release — idempotent no-op (astra-2 §5.1)

                p.Owners.Remove(token.AssignmentId);
                if (p.Owners.Count == 0) p.PinnedDeployment = null;
                _log.Information($"lanes: released {token.PoolId} lane for {token.AssignmentId} (epoch {token.Epoch})");
                admitted.AddRange(TryAdmitLocked(p, "release"));
            }
        }
        finally
        {
            FireAdmittedFromQueue(admitted);
        }
    }

    public async ValueTask<LaneOwnershipToken?> HandoffAsync(LaneOwnershipToken fromToken, LaneQueueEntry toEntry)
    {
        await ValueTask.CompletedTask;
        lock (_gate)
        {
            if (fromToken is null || toEntry is null) return null;
            if (!string.Equals(fromToken.PoolId, toEntry.PoolId, StringComparison.Ordinal)) return null;
            if (!_pools.TryGetValue(fromToken.PoolId, out var p)) return null;
            if (!p.Owners.TryGetValue(fromToken.AssignmentId, out var owned) || !ReferenceEquals(owned, fromToken))
                return null; // not a current owner — handoff of a stale token changes nothing

            if (p.Owners.ContainsKey(toEntry.AssignmentId))
                return null; // the target already owns a lane — no double ownership

            // Single-residency check (astra-2 §5.4): a second owner must use the
            // pool's pinned binding; a different deployment waits for full drain.
            if (p.PinnedDeployment is { } pinned && !string.Equals(pinned, toEntry.DeploymentId, StringComparison.Ordinal))
            {
                _log.Information($"lanes: handoff held — pool {p.Id} is pinned to {pinned}, {toEntry.DeploymentId} waits for drain");
                return null;
            }

            p.Owners.Remove(fromToken.AssignmentId);
            var newToken = fromToken with
            {
                AssignmentId = toEntry.AssignmentId,
                RunId = toEntry.RunId ?? toEntry.AssignmentId,
                Epoch = fromToken.Epoch + 1,
                LaneId = p.Owners.Count == 1 ? fromToken.LaneId : $"lane-{p.Owners.Count + 1}",
                AcquiredAt = DateTimeOffset.UtcNow,
            };
            p.Owners[toEntry.AssignmentId] = newToken;
            _log.Information($"lanes: handoff {fromToken.PoolId}: {fromToken.AssignmentId} → {toEntry.AssignmentId} (epoch {newToken.Epoch})");
            return newToken;
        }
    }

    public bool TryValidatePermit(LaneOwnershipToken? token, string poolId, string assignmentId)
    {
        if (token is null) return false;
        lock (_gate)
        {
            if (!string.Equals(token.HostGeneration, _hostGeneration, StringComparison.Ordinal)) return false;
            if (!string.Equals(token.PoolId, poolId, StringComparison.Ordinal)) return false;
            if (!_pools.TryGetValue(poolId, out var p)) return false;
            if (!p.Owners.TryGetValue(assignmentId, out var owned) || !ReferenceEquals(owned, token)) return false;
            return true;
        }
    }

    public async ValueTask<bool> CancelQueuedAsync(string assignmentId)
    {
        await ValueTask.CompletedTask;
        lock (_gate)
        {
            foreach (var p in _pools.Values)
            {
                foreach (var (seq, entry) in p.Queue)
                {
                    if (entry.AssignmentId == assignmentId)
                    {
                        p.Queue.Remove(seq);
                        _log.Information($"lanes: cancelled queued {assignmentId} in {p.Id}");
                        return true;
                    }
                }
            }
            return false;
        }
    }

    public IReadOnlyList<LanePoolSnapshot> Snapshots()
    {
        lock (_gate)
            return _pools.Values.Select(SnapshotLocked).OrderBy(s => s.PoolId).ToList();
    }

    public async ValueTask SetPoolEnabledAsync(string poolId, bool enabled)
    {
        await ValueTask.CompletedTask;
        var admitted = new List<LaneOwnershipToken>();
        try
        {
            lock (_gate)
            {
                if (!_pools.TryGetValue(poolId, out var p)) return;
                p.Enabled = enabled;
                p.Draining = !enabled;
                _log.Information($"lanes: pool {poolId} {(enabled ? "enabled" : "disabled — draining, no new admission")}");
                if (enabled) admitted.AddRange(TryAdmitLocked(p, "re-enabled")); // re-enable drains the FIFO queue
            }
        }
        finally
        {
            FireAdmittedFromQueue(admitted);
        }
    }

    // ---- internals (lock held) ------------------------------------------------

    /// <summary>One admission attempt for a SPECIFIC entry (lock held).</summary>
    private bool TryAdmitOneLocked(PoolState p, LaneQueueEntry entry, out LaneOwnershipToken? token, out string? blocked, List<LaneOwnershipToken>? admittedFromQueue = null)
    {
        token = null; blocked = null;
        if (!p.Enabled) { blocked = "pool disabled (draining)"; return false; }

        var residency = ResidencyBlock(p, entry);
        if (residency is not null) { blocked = residency; return false; }

        var capacity = EffectiveCapacity(p, out var capBlocked);
        if (capacity is null) { blocked = capBlocked ?? "capacity unknown"; return false; }
        if (p.Owners.Count >= capacity.Value) { blocked = $"at capacity ({p.Owners.Count}/{capacity.Value})"; return false; }

        token = new LaneOwnershipToken(
            p.Id,
            $"lane-{p.Owners.Count + 1}",
            entry.AssignmentId,
            entry.RunId ?? entry.AssignmentId,
            _hostGeneration,
            p.NextEpoch++,
            DateTimeOffset.UtcNow);
        p.Owners[entry.AssignmentId] = token!;
        p.PinnedDeployment ??= entry.DeploymentId;
        _log.Information($"lanes: admitted {entry.AssignmentId} on {p.Id}/{token!.LaneId} (epoch {token!.Epoch}, {p.Owners.Count}/{capacity.Value})");
        if (admittedFromQueue is not null) admittedFromQueue.Add(token!);
        return true;
    }

    /// <summary>Admit from the FIFO queue as far as capacity allows (lock held).</summary>
    private List<LaneOwnershipToken> TryAdmitLocked(PoolState p, string reason)
    {
        var admitted = new List<LaneOwnershipToken>();
        while (p.Queue.Count > 0)
        {
            var (seq, entry) = p.Queue.MinBy(x => x.Key);
            if (!TryAdmitOneLocked(p, entry, out _, out var blocked, admitted))
            {
                // "at capacity" is fill-only (stop, queue intact); every other
                // block (disabled, capacity unknown, residency) stops admission
                // for the same reason — a later release/observation will retry.
                _log.Debug($"lanes: {p.Id} queue head held: {blocked}");
                return admitted;
            }
            p.Queue.Remove(seq);
        }
        _ = reason;
        return admitted;
    }

    private static string? ResidencyBlock(PoolState p, LaneQueueEntry entry)
    {
        if (p.Owners.Count == 0) return null;
        if (p.PinnedDeployment is not { } pinned) return null;
        if (string.Equals(pinned, entry.DeploymentId, StringComparison.Ordinal)) return null;
        return $"single-residency: pool pinned to {pinned}, {entry.DeploymentId} waits for drain";
    }

    private static int? EffectiveCapacity(PoolState p, out string? blocked)
    {
        blocked = null;
        if (p.Mode == LaneCapacityMode.Manual)
        {
            if (p.MaxAgents is > 0) return p.MaxAgents;
            blocked = "manual mode without a validated agent count";
            return null;
        }

        var obs = p.LastObservation;
        if (obs is null || !obs.Usable)
        {
            blocked = obs is null
                ? "provider capacity unknown (hold-new)"
                : $"provider capacity {obs.Status} (hold-new until a fresh observation)";
            return null;
        }
        var n = obs.TotalConcurrency!.Value;
        if (p.MaxAgents is > 0) n = Math.Min(n, p.MaxAgents.Value);
        return n;
    }

    private LanePoolSnapshot SnapshotLocked(PoolState p)
    {
        string? blocked = null;
        string? capBlocked = null;
        var capacity = p.Enabled ? EffectiveCapacity(p, out capBlocked) : null;
        if (!p.Enabled) blocked = "disabled — draining";
        else if (capacity is null) blocked = capBlocked;
        else if (p.Owners.Count >= capacity) blocked = "full";
        return new LanePoolSnapshot(
            p.Id,
            p.Enabled,
            p.Draining,
            p.Owners.Count,
            capacity,
            p.Mode,
            p.LastObservation?.TotalConcurrency,
            p.MaxAgents,
            p.LastObservation?.Status ?? ProviderCapacityStatus.Unknown,
            p.LastObservation?.ObservedAt,
            p.Queue.Count,
            blocked);
    }

    /// <summary>
    /// Deliver the queue-admission event OUTSIDE the gate (callers pass the
    /// list captured inside the lock). Handlers start queued segments; a
    /// faulting handler is logged, never propagated into admission.
    /// </summary>
    private void FireAdmittedFromQueue(IReadOnlyList<LaneOwnershipToken> admitted)
    {
        if (admitted.Count == 0) return;
        var handler = AdmittedFromQueue;
        if (handler is null) return;
        foreach (var t in admitted)
        {
            try { handler(t); }
            catch (Exception ex) { _log.Error($"lane admission handler faulted: {ex.Message}"); }
        }
    }
}
