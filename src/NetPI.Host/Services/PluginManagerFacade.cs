using System.Text.Json;
using NetPI.Abstractions;
using NetPI.Host.Config;
using NetPI.Host.Plugins;

namespace NetPI.Host.Services;

/// <summary>
/// Host implementation of the reload-control surface exposed to plugins
/// (PLAN §36). Registered into the service registry under "plugins" so the
/// Web plugin can drive reloads without referencing the host ALC.
/// </summary>
internal sealed class PluginManagerFacade : IPluginManagerFacade
{
    private readonly PluginManager _manager;

    public PluginManagerFacade(PluginManager manager) => _manager = manager;

    public IReadOnlyList<PluginStatusSnapshot> GetStatus()
    {
        var list = new List<PluginStatusSnapshot>(_manager.GetStatus().Count);
        foreach (var s in _manager.GetStatus())
        {
            list.Add(new PluginStatusSnapshot(
                s.PluginId,
                s.Name ?? s.PluginId,
                s.Version,
                s.State.ToString(),
                s.Generation,
                s.BuildId,
                null,
                s.ActiveLeases,
                s.LastError,
                s.Update is null ? null : new PluginStatusUpdate(
                    s.Update.AvailableBuildId,
                    s.Update.LoadedPath,
                    s.Update.BlockingLeases,
                    s.Update.PrevAlocCollected),
                s.LastOperation));
        }
        return list;
    }

    // astra-1 P5: retired-ALC collection state for the diagnostics view.
    public IReadOnlyList<UnloadedAlocInfo> UnloadedAlocs()
    {
        var list = new List<UnloadedAlocInfo>();
        foreach (var (label, collected) in _manager.UnloadedAlocs())
        {
            // Labels are "<pluginId>-gen<n>" or "superseded-<pluginId>-gen<n>" —
            // the plugin id is the label up to the "-gen" suffix.
            var genIdx = label.LastIndexOf("-gen", StringComparison.Ordinal);
            var pluginId = genIdx > 0 ? label[..genIdx].Replace("superseded-", "", StringComparison.Ordinal) : null;
            list.Add(new UnloadedAlocInfo(label, collected, pluginId));
        }
        return list;
    }

    public async ValueTask<bool> ReloadAsync(string pluginId, CancellationToken cancellationToken = default)
    {
        try
        {
            var gen = await _manager.ReloadAsync(pluginId, cancellationToken);
            return gen is not null;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public async ValueTask<int> ReloadAllAsync(CancellationToken cancellationToken = default)
    {
        var outcomes = await _manager.ReloadAllOpAsync(cancellationToken);
        return outcomes.Count;
    }

    public ValueTask<PluginOperationOutcome> ReloadPluginOutcomeAsync(string pluginId, CancellationToken cancellationToken = default)
        => new(_manager.ReloadPluginAsync(pluginId, cancellationToken));

    public ValueTask<IReadOnlyList<PluginOperationOutcome>> ReloadAllOutcomeAsync(CancellationToken cancellationToken = default)
        => new(_manager.ReloadAllOpAsync(cancellationToken));

    public async ValueTask<IReadOnlyList<string>> ScanAsync(CancellationToken cancellationToken = default)
        => await _manager.ScanAsync(cancellationToken);
    // ------------------------------------------------------------------
    // astra-1 P5: queue-backed update operations (the Enqueue* surface).
    // The Web handlers ack with the operation id immediately; the work runs
    // on the host lifecycle queue; the outcome is queryable later via GetOperation.
    // ------------------------------------------------------------------

    /// <inheritdoc/>
    public string EnqueueReload(string pluginId, string? requestedBuildId, CancellationToken ct = default)
        => _manager.EnqueueReload(pluginId, requestedBuildId, ct);

    /// <inheritdoc/>
    public string EnqueueReloadAll(CancellationToken ct = default)
        => _manager.EnqueueReloadAll(ct);

    /// <inheritdoc/>
    public string EnqueueScan(CancellationToken ct = default)
        => _manager.EnqueueScan(ct);

    /// <inheritdoc/>
    public PluginOperationStatus GetOperation(string operationId)
        => _manager.GetOperation(operationId);

}

/// <summary>
/// Host implementation of <see cref="IHostConfigUpdate"/> (PLAN §36): deep-merges
/// plugin config sections into ~/.netpi/config.json so the Web settings overlay
/// can persist port/baseUrl changes without a host reference.
/// </summary>
internal sealed class HostConfigUpdater : IHostConfigUpdate
{
    private readonly ConfigService _config;

    // Friendly short names (as sent by the frontend Settings overlay) →
    // actual plugin directory ids.
    private static readonly Dictionary<string, string> AliasToPlugin = new(StringComparer.Ordinal)
    {
        ["web"] = "netpi.web",
        ["aiproxy"] = "netpi.provider.aiproxy",
        ["ai-proxy"] = "netpi.provider.aiproxy",
        ["agent"] = "netpi.agent",
        ["storage"] = "netpi.storage.sqlite",
        ["context"] = "netpi.context.pi",
        ["tools"] = "netpi.tools",
        ["autocompact"] = "netpi.autoCompact",
        ["auto-compact"] = "netpi.autoCompact",
        ["retry"] = "netpi.retry",
    };



    public HostConfigUpdater(ConfigService config) => _config = config;

    public ValueTask MergePluginAsync(string pluginId, JsonElement properties, CancellationToken cancellationToken = default)
    {
        _config.MergePluginSection(ResolveId(pluginId), properties);
        return ValueTask.CompletedTask;
    }

    private static string ResolveId(string pluginId)
    {
        var key = pluginId.ToLowerInvariant();
        return AliasToPlugin.TryGetValue(key, out var mapped) ? mapped : key;
    }
}
