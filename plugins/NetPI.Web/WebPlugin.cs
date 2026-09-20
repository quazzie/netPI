using System.Text.Json;
using NetPI.Abstractions;

namespace NetPI.Web;

/// <summary>
/// The reloadable web plugin (PLAN §36, §41). Runs a Kestrel app on a port from
/// config, serves the compiled Svelte build, exposes /ws, and bridges agent
/// bus events → §41 WebSocket events plus handles client commands.
/// It also self-registers the "plugins" panel
/// (docs/web-panels.md) so the Svelte shell contains no hardcoded tabs.
/// </summary>
public sealed class WebPlugin : INetPiPlugin
{
    private WebApp? _app;
    private List<IDisposable>? _panels;

    public PluginInfo Info { get; } = new("netPI.Web", "Web Surface", "0.1.0");

    public async ValueTask LoadAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        var cfg = context.OwnConfig;
        int port = cfg.ValueKind == JsonValueKind.Object
                   && cfg.TryGetProperty("port", out var p) && p.ValueKind == JsonValueKind.Number
            ? p.GetInt32() : 5173;
        var staticRoot = cfg.ValueKind == JsonValueKind.Object
                   && cfg.TryGetProperty("staticRoot", out var s) && s.ValueKind == JsonValueKind.String
            ? s.GetString() ?? string.Empty : string.Empty;

        // Self-registration: the shell renders only what the panel catalog
        // (ui.panels) contains. Panel ids are stable so persisted
        // ui.rightTab values keep working across the migration.
        // The "diagnostics" tab is registered by the NetPI.Diagnostics plugin
        // itself (first-class, PLAN §47) — it is absent until that plugin loads.
        _panels =
        [
            context.WebPanels.Register(new WebPanelDefinition("plugins", "Plugins", "◇", "/panel/plugins", 0)),
        ];

        _app = new WebApp(context, port, staticRoot, context.Log);
        await _app.StartAsync(cancellationToken);
        context.Log.Information($"Web surface listening on http://127.0.0.1:{port}");
        await ValueTask.CompletedTask;
    }

    public ValueTask StartAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

    public async ValueTask StopAsync(CancellationToken cancellationToken)
    {
        var app = _app;
        _app = null;
        if (app is not null)
            await app.StopAsync(cancellationToken);

        if (_panels is not null)
        {
            foreach (var p in _panels) p.Dispose();
            _panels = null;
        }
        await ValueTask.CompletedTask;
    }

    public ValueTask UnloadAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
}
