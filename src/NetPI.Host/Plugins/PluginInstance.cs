using System.Collections.Concurrent;
using NetPI.Abstractions;

namespace NetPI.Host.Plugins;

/// <summary>
/// Host-side record for one plugin generation: identity, state machine,
/// ALC, plugin instance, ownership maps (registrations/subscriptions),
/// active lease count, and reload policy (PLAN §6/§7).
/// </summary>
public sealed class PluginInstance
{
    private readonly object _stateGate = new();

    public string PluginId { get; }
    public int Generation { get; }
    public string? SourceDirectory { get; init; }
    public string? CacheDirectory { get; init; }
    public PluginInfo? Info { get; set; }
    public PluginLoadContext? LoadContext { get; set; }
    public INetPiPlugin? Plugin { get; set; }
    public ReloadPolicy Policy { get; set; } = ReloadPolicy.PluginIdle;

    /// <summary>Registration handles owned by this generation (removed on unload even if the plugin forgot).</summary>
    public ConcurrentDictionary<string, IDisposable> Registrations { get; } = new();

    /// <summary>Event subscription handles owned by this generation.</summary>
    public ConcurrentDictionary<string, IDisposable> Subscriptions { get; } = new();

    /// <summary>Lease handles currently held on this plugin's services (drained before unload).</summary>
    public ConcurrentDictionary<Guid, IDisposable> LiveLeases { get; } = new();

    private string? _lastError;
    private readonly object _errorGate = new();
    private PluginState _state = PluginState.Unloaded;

    /// <summary>Current live lease count (drain target: zero).</summary>
    public int LeasesHeld => LiveLeases.Count;

    public PluginState State
    {
        get { lock (_stateGate) { return _state; } }
        set { lock (_stateGate) { _state = value; } }
    }

    /// <summary>Most recent load/start/reload failure (PLAN §46 plugin-management surface); null when healthy.</summary>
    public string? LastError
    {
        get { lock (_errorGate) return _lastError; }
        set { lock (_errorGate) _lastError = value; }
    }

    /// <summary>Clears the recorded error (after a successful reload/fix).</summary>
    public void ClearLastError() => LastError = null;

    public PluginInstance(string pluginId, int generation)
    {
        PluginId = pluginId;
        Generation = generation;
    }

    /// <summary>
    /// PLAN §44: a lease on this plugin generation itself (not on one of its
    /// services). Counts toward <see cref="LeasesHeld"/> so a reload's drain
    /// waits for it to be released.
    /// </summary>
    public IValueLease<object> AcquireSelfLease()
    {
        var lease = new PluginSelfLease(this);
        LiveLeases[lease.Id] = lease;
        return lease;
    }

    private sealed class PluginSelfLease(PluginInstance owner) : IValueLease<object>
    {
        private int _released;
        public Guid Id { get; } = Guid.NewGuid();
        public object Value => owner;
        private void Release()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0) return;
            owner.LiveLeases.TryRemove(Id, out _);
        }
        void IDisposable.Dispose() => Release();
        public ValueTask DisposeAsync() { Release(); return ValueTask.CompletedTask; }
    }

    public void AddLease(Guid leaseId, IDisposable lease) => LiveLeases[leaseId] = lease;

    public void RemoveLease(Guid leaseId, out IDisposable? lease)
    {
        lease = null;
        LiveLeases.TryRemove(leaseId, out lease);
    }



}
