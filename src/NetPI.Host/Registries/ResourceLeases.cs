namespace NetPI.Host.Registries;

/// <summary>No delegates or plugin objects are stored here; the leases and descriptors belong to the host.</summary>
public sealed class ResourceLeases(IEventBus events) : IResourceLeases
{
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
                Holder = new SlotHolder { AgentId = request.AgentId, SessionId = request.SessionId, Label = request.Label, Priority = request.Priority, Since = DateTimeOffset.UtcNow },
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
            Holder = new SlotHolder { AgentId = x.Holder.AgentId, SessionId = x.Holder.SessionId, Label = x.Holder.Label, Priority = x.Holder.Priority, Since = x.Holder.Since },
        }).ToList();
    }

    private void Release(long id)
    {
        string resource;
        lock (_gate)
        {
            if (!_active.Remove(id, out var info)) return;
            resource = info.Resource;
        }
        events.Publish("resources.released", new { resource });
    }

    private sealed class Lease(ResourceLeases owner, long id) : IDisposable
    {
        private int _released;
        public void Dispose() { if (Interlocked.Exchange(ref _released, 1) == 0) owner.Release(id); }
    }
}
