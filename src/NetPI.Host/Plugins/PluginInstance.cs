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

    /// <summary>astra-1 P3: true once StartAsync succeeded (Stop is only safe after it).</summary>
    public bool StartRan { get; set; }

    /// <summary>astra-1 P3: idempotency guard for the unified generation cleanup.</summary>
    public bool CleanupStarted { get; set; }

    /// <summary>astra-1 P4: native library files this generation loaded (process mappings).</summary>
    public IReadOnlyList<string> NativeFiles { get; set; } = [];

    /// <summary>astra-1 P4: native build content hashes of this generation (RestartRequired comparison on reload).</summary>
    public IReadOnlyCollection<string> NativeBuildHashes { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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

    /// <summary>
    /// astra-1 P3: begin draining atomically with the state check. Succeeds
    /// only from Active or Failed (the two reloadable states); on failure
    /// <c>prev</c> is the current state and nothing changed.
    ///
    /// Lock order (documented, one direction only): _stateGate first, never
    /// holding any other lock; <see cref="LiveLeases"/> is a lock-free
    /// ConcurrentDictionary, so storing into it under _stateGate cannot invert
    /// the order with any other lock.
    /// </summary>
    public bool TryBeginDrain(out PluginState prev)
    {
        lock (_stateGate)
        {
            prev = _state;
            if (_state is not (PluginState.Active or PluginState.Failed))
                return false;
            _state = PluginState.Draining;
            return true;
        }
    }

    /// <summary>
    /// astra-1 P3: atomically admit a NEW lease: the state check and the
    /// lease recording happen under the same lock, so a lease can never be
    /// admitted after <see cref="TryBeginDrain"/> succeeded. External work
    /// (registry Acquire) is admitted only from Loading/Active — a Failed or
    /// Draining plugin takes no new external work.
    /// </summary>
    public bool TryAcquireLease(Guid leaseId, IDisposable lease)
    {
        lock (_stateGate)
        {
            if (_state is not (PluginState.Loading or PluginState.Active))
                return false;
            LiveLeases[leaseId] = lease;
            return true;
        }
    }

    /// <summary>
    /// astra-1 P3: atomic admission for the plugin's OWN lease (it may hold
    /// work on itself while Failed — but never once draining has started).
    /// </summary>
    public bool TryAcquireSelfLease(Guid leaseId, IDisposable lease)
    {
        lock (_stateGate)
        {
            if (_state is PluginState.Draining or PluginState.Unloading)
                return false;
            LiveLeases[leaseId] = lease;
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
        // astra-1 P3: atomic admission — a lease is recorded only if the state
        // machine admits it (never after draining started). An unadmitted
        // self-lease is returned untracked (the plugin may still touch its own
        // already-live services; the drain target only counts admitted ones).
        var lease = new PluginSelfLease(this);
        if (!TryAcquireSelfLease(lease.Id, lease))
        {
            lease.Untrack();
            return lease;
        }
        return lease;
    }

    private sealed class PluginSelfLease(PluginInstance owner) : IValueLease<object>
    {
        private int _released;
        private bool _tracked = true;
        public Guid Id { get; } = Guid.NewGuid();
        public object Value => owner;
        public void Untrack() => _tracked = false;
        private void Release()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0) return;
            if (_tracked)
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
