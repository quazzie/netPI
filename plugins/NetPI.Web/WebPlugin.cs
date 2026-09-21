using System.Text.Json;
using NetPI.Abstractions;

namespace NetPI.Web;

/// <summary>
/// The reloadable web plugin (PLAN §36, §41). Runs a Kestrel app on a port from
/// config, serves the compiled Svelte build, exposes /ws, and bridges agent
/// bus events → §41 WebSocket events plus handles client commands.
/// The former standalone "plugins" panel was folded into the NetPI.Diagnostics
/// plugin's "Plugins" sub-tab (docs/web-panels.md) — NetPI.Web registers no panel.
/// </summary>
public sealed class WebPlugin : INetPiPlugin
{
    private WebApp? _app;

    public PluginInfo Info { get; } = new("netPI.Web", "Web Surface", "0.1.0");

    public ValueTask LoadAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        // astra-1 P3: Load = registration only. The active Kestrel startup
        // (listener, subscriptions, service resolution) happens in
        // StartAsync per the documented lifecycle — a generation that failed
        // before Start owns no listener.
        var cfg = context.OwnConfig;
        int port = cfg.ValueKind == JsonValueKind.Object
                   && cfg.TryGetProperty("port", out var p) && p.ValueKind == JsonValueKind.Number
            ? p.GetInt32() : 5173;
        var staticRoot = cfg.ValueKind == JsonValueKind.Object
                   && cfg.TryGetProperty("staticRoot", out var s) && s.ValueKind == JsonValueKind.String
            ? s.GetString() ?? string.Empty : string.Empty;

        _app = new WebApp(context, port, staticRoot, context.Log);
        _context = context;
        _port = port;
        return ValueTask.CompletedTask;
    }

    public async ValueTask StartAsync(CancellationToken cancellationToken)
    {
        if (_app is null) return; // Load failed — nothing to start
        await _app.StartAsync(cancellationToken);
        _context?.Log.Information($"Web surface listening on http://127.0.0.1:{_port}");
    }

    private IPluginContext? _context;

    /// <summary>Retained so StartAsync can log the port (Load's owner reference).</summary>
    private int _port;

    public async ValueTask StopAsync(CancellationToken cancellationToken)
    {
        var app = _app;
        _app = null;
        if (app is not null)
            await app.StopAsync(cancellationToken);

        await ValueTask.CompletedTask;
    }

    public ValueTask UnloadAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
}
