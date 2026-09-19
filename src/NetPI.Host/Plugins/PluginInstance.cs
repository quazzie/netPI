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

    private PluginState _state = PluginState.Unloaded;

    public PluginState State
    {
        get { lock (_stateGate) { return _state; } }
        set { lock (_stateGate) { _state = value; } }
    }

    public PluginInstance(string pluginId, int generation)
    {
        PluginId = pluginId;
        Generation = generation;
    }

    public void AddLease(Guid leaseId, IDisposable lease) => LiveLeases[leaseId] = lease;

    public void RemoveLease(Guid leaseId, out IDisposable? lease)
    {
        lease = null;
        LiveLeases.TryRemove(leaseId, out lease);
    }

    /// <summary>Current live lease count (drain target: zero).</summary>
    public int LeasesHeld => LiveLeases.Count;
}
