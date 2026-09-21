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

    /// <summary>
    /// astra-1 P1: the build this generation was loaded from — the build id
    /// pinned by the <c>current.json</c> pointer for published builds, or
    /// "legacy" when the generation was migrated from a legacy staged folder.
    /// </summary>
    public string? BuildId { get; init; }
    public PluginInfo? Info { get; set; }
    public PluginLoadContext? LoadContext { get; set; }
    public INetPiPlugin? Plugin { get; set; }
    public ReloadPolicy Policy { get; set; } = ReloadPolicy.PluginIdle;

    /// <summary>Registration handles owned by this generation (removed on unload even if the plugin forgot).</summary>
    public ConcurrentDictionary<string, IDisposable> Registrations { get; } = new();

    /// <summary>Command registrations owned by this generation (PLAN §37); removed on unload.</summary>
    public NetPI.Host.Services.ScopedCommands? Commands { get; set; }

    /// <summary>Web-panel registrations owned by this generation.</summary>
    public NetPI.Host.Services.ScopedWebPanels? WebPanels { get; set; }

    /// <summary>Event subscription handles owned by this generation.</summary>
    public ConcurrentDictionary<string, IDisposable> Subscriptions { get; } = new();

    /// <summary>Source bytes this generation loads from (legacy staged folder or the pinned artifact); re-resolvable for rollback.</summary>
    public string? SourceArtifactDir { get; set; }

    /// <summary>astra-1 P2: config snapshot taken when this generation was loaded (rollback retains it).</summary>
    public System.Text.Json.JsonElement SourceConfig { get; set; }

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

    /// <summary>
    /// astra-1 P2: compare-and-set on the state machine. The reload admission
    /// gate (Active/Failed → Draining) must be atomic with the state check so
    /// two racing lifecycle operations cannot both believe they own the drain.
    /// </summary>
    public bool TrySetState(PluginState expected, PluginState next)
    {
        lock (_stateGate)
        {
            if (_state != expected) return false;
            _state = next;
            return true;
        }
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
