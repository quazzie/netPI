namespace NetPI;

/// <summary>
/// The registry of what is physically running on shared model resources (a local model's slots). A plain class in the shared contract
/// assembly, so one instance can outlive a reload of the plugin that registered it: a new generation of that plugin adopts the registered
/// instance and <see cref="Rebind"/>s it to its own event bus, and the count carries across the swap. No delegates or plugin objects are
/// stored here; the leases and descriptors are plain data.
/// </summary>
public sealed class ResourceLeases(IEventBus events) : IResourceLeases
{
    private IEventBus _events = events;

    /// <summary>Publish from now on through <paramref name="events"/> (the adopting plugin generation's bus: the previous one is gone).</summary>
    public void Rebind(IEventBus events) => Volatile.Write(ref _events, events);

    private readonly Lock _gate = new();
    private readonly Dictionary<long, ResourceLeaseInfo> _active = [];
    private long _sequence;

    public bool TryAcquire(string resource, int capacity, AgentSlotRequest request, int instances, out IDisposable? lease)
    {
        lock (_gate)
        {
            lease = null;
            if (_active.Values.Count(x => string.Equals(x.Resource, resource, StringComparison.OrdinalIgnoreCase)) >= Math.Max(1, capacity)
                || _active.Values.Count(x => string.Equals(x.Key, request.Key, StringComparison.OrdinalIgnoreCase)) >= Math.Max(1, instances))
                return false;
            var id = ++_sequence;
            _active[id] = new ResourceLeaseInfo
            {
                Resource = resource, Key = request.Key,
                Holder = new SlotHolder { LeaseId = id.ToString(System.Globalization.CultureInfo.InvariantCulture), ExecutorGeneration = request.ExecutorGeneration, AgentId = request.AgentId, SessionId = request.SessionId, Label = request.Label, Priority = request.Priority, Since = DateTimeOffset.UtcNow },
            };
            lease = new Lease(this, id);
            return true;
        }
    }

    public IReadOnlyList<ResourceLeaseInfo> Snapshot()
    {
        lock (_gate) return _active.Values.Select(x => new ResourceLeaseInfo
        {
            Resource = x.Resource, Key = x.Key,
            Holder = new SlotHolder { LeaseId = x.Holder.LeaseId, ExecutorGeneration = x.Holder.ExecutorGeneration, CorrelationId = x.Holder.CorrelationId, Purpose = x.Holder.Purpose, Retiring = x.Holder.Retiring, CancellationRequestedAt = x.Holder.CancellationRequestedAt, ProviderReturnedAt = x.Holder.ProviderReturnedAt, AgentId = x.Holder.AgentId, SessionId = x.Holder.SessionId, Label = x.Holder.Label, Priority = x.Holder.Priority, Since = x.Holder.Since },
        }).ToList();
    }

    public void Update(string leaseId, ResourceLeaseUpdate update)
    {
        if (!long.TryParse(leaseId, out var id)) return;
        lock (_gate)
        {
            if (!_active.TryGetValue(id, out var info)) return;
            var h = info.Holder;
            if (update.BeginCall)
            {
                h.CorrelationId = update.CorrelationId; h.Purpose = update.Purpose;
                h.ProviderReturnedAt = null; h.CancellationRequestedAt = null;
            }
            if (update.CancellationRequested) h.CancellationRequestedAt ??= DateTimeOffset.UtcNow;
            if (update.ProviderReturned) h.ProviderReturnedAt = DateTimeOffset.UtcNow;
            h.Retiring |= update.Retiring;
        }
        Volatile.Read(ref _events).Publish("resources.changed", new { leaseId });
    }

    private void Release(long id)
    {
        string resource;
        lock (_gate)
        {
            if (!_active.Remove(id, out var info)) return;
            resource = info.Resource;
        }
        Volatile.Read(ref _events).Publish("resources.released", new { resource });
    }

    private sealed class Lease(ResourceLeases owner, long id) : IResourceLease
    {
        public string Id => id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        private int _released;
        public void Dispose() { if (Interlocked.Exchange(ref _released, 1) == 0) owner.Release(id); }
    }
}
