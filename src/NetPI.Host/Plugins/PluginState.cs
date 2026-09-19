namespace NetPI.Host.Plugins;

/// <summary>
/// Reload policy for a plugin (PLAN §6). Normal plugins drain when idle
/// (no live leases); agent/web plugins additionally wait for the agent run
/// to finish (AgentIdle) — wired through a pluggable gate, since the agent
/// runtime does not exist in Phase 1.
/// </summary>
public enum ReloadPolicy
{
    /// <summary>Reload as soon as all service leases are released.</summary>
    PluginIdle = 0,

    /// <summary>Reload only when the agent is idle (no active run).</summary>
    AgentIdle = 1,
}

/// <summary>
/// The plugin lifecycle state machine (PLAN §6):
/// Loading → Active ⇄ Draining → Unloading → Unloaded, or Failed.
/// </summary>
public enum PluginState
{
    Loading = 0,
    Active = 1,
    /// <summary>New leases are denied; existing leases drain before unload.</summary>
    Draining = 2,
    Unloading = 3,
    Unloaded = 4,
    Failed = 5,
}

/// <summary>
/// Gate asked during a reload: "may this plugin proceed with the unload?".
/// Return false to keep waiting (the host re-asks on a short interval).
/// </summary>
public delegate Task<bool> ReloadGate(Plugins.PluginInstance instance);
