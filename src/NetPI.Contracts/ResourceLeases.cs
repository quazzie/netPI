namespace NetPI;

/// <summary>Fallback admission while a scheduler capability is absent. Physical counts still span plugin reloads.</summary>
public sealed class ResourceLeaseSlot(IDisposable held, AgentSlotRequest request) : IAgentSlot
{
    private int _released;
    public string? LeaseId => (held as IResourceLease)?.Id;
    public string Key => request.Key;
    public string AgentId => request.AgentId;
    public DateTimeOffset AcquiredAt { get; } = DateTimeOffset.UtcNow;
    public bool IsReleased => Volatile.Read(ref _released) != 0;
    public void Dispose() { if (Interlocked.Exchange(ref _released, 1) == 0) held.Dispose(); }
    public static async ValueTask<IAgentSlot?> AcquireAsync(IServiceRegistry services, ISettings settings, ModelInfo model, AgentSlotRequest request, CancellationToken ct)
    {
        if (!model.IsLocal || services.Get<IResourceLeases>() is not { } physical) return null;
        var capacity = ModelCapacity.Local(settings, model);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (physical.TryAcquire("local:" + model.Ref, capacity, request, capacity, out var lease)) return new ResourceLeaseSlot(lease!, request);
            await Task.Delay(50, ct).ConfigureAwait(false);
        }
    }
}

/// <summary>Host-owned admission state. Only call disposal, never scheduler replacement, frees physical capacity.</summary>
public interface IResourceLeases
{
    bool TryAcquire(string resource, int capacity, AgentSlotRequest request, int instances, out IDisposable? lease);
    IReadOnlyList<ResourceLeaseInfo> Snapshot();
    void Update(string leaseId, ResourceLeaseUpdate update) { }
}

public interface IResourceLease : IDisposable { string Id { get; } }

/// <summary>Lifecycle updates contain only primitive data, never callbacks or collectible plugin objects.</summary>
public sealed class ResourceLeaseUpdate
{
    public string? CorrelationId { get; init; }
    public string? Purpose { get; init; }
    public bool BeginCall { get; init; }
    public bool CancellationRequested { get; init; }
    public bool ProviderReturned { get; init; }
    public bool Retiring { get; init; }
}

public sealed class ResourceLeaseInfo
{
    public string Resource { get; init; } = "";
    public string Key { get; init; } = "";
    public SlotHolder Holder { get; init; } = new();
}

public sealed class ModelResourceSlots
{
    public string Key { get; set; } = "";
    public string? Model { get; set; }
    public int Capacity { get; set; }
    public int Busy { get; set; }
    public int Queued { get; set; }
    public bool Available { get; set; } = true;
    public string? Unavailable { get; set; }
    public List<SlotHolder> Owners { get; set; } = [];
}
