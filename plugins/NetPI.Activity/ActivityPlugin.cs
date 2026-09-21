using System.Text.Json;
using NetPI.Abstractions;

namespace NetPI.Activity;

/// <summary>
/// astra-1 H: the Activity plugin (PLAN §10). Registers the "Activity"
/// right-panel tab — ONE view with two sections: <b>Agents</b> (runs from every
/// session, via the run-query service) and <b>Processes</b> (background jobs +
/// foreground shell commands). It CONSUMES the other plugins' services through
/// Abstractions only (no ALC references) and never owns or terminates agent
/// runtimes when it unloads — it presents and, at most, signals a cancel.
///
/// Missing BackgroundTasks / Tools / Agent plugins do not prevent normal chat:
/// each service is resolved lazily and a null is surfaced as "unavailable" in
/// the relevant section, not an error.
/// </summary>
public sealed class ActivityPlugin : INetPiPlugin
{
    private IPluginContext? _ctx;
    private ActivityWebApp? _app;
    private IDisposable? _panel;
    private int _port;

    public PluginInfo Info { get; } = new("netPI.Activity", "Activity", "0.1.0");

    public ValueTask LoadAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        _ctx = context;
        int port = ReadPort(context.OwnConfig);
        _port = port;
        _app = new ActivityWebApp(context, port);
        // First-class panel registration (PLAN §47): the page and the
        // /api/activity endpoints live on this plugin's own Kestrel port. The
        // registration is generation-scoped, so the tab disappears with the
        // generation even if we never dispose the handle.
        _panel = context.WebPanels.Register(new WebPanelDefinition(
            "activity", "Activity", "✦", $"http://127.0.0.1:{port}/panel/activity", 7));
        return ValueTask.CompletedTask;
    }

    public async ValueTask StartAsync(CancellationToken cancellationToken)
    {
        var ctx = _ctx ?? throw new InvalidOperationException("Loaded context not available.");
        var app = _app ?? throw new InvalidOperationException("Web app not initialised.");
        await app.StartAsync(cancellationToken);
        // Port 0 (tests): re-register over our own entry with the real bound URL.
        if (_port == 0 && app.BoundUrl is { } url)
        {
            _panel?.Dispose();
            _panel = ctx.WebPanels.Register(new WebPanelDefinition(
                "activity", "Activity", "✦", url + "/panel/activity", 7));
        }
        ctx.Log.Information($"Activity surface listening on {app.BoundUrl ?? $"http://127.0.0.1:{_port}"}");
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken)
    {
        var webApp = _app;
        _app = null;
        if (webApp is null) return;
        try { await webApp.StopAsync(cancellationToken); } catch { /* best-effort */ }
    }

    public ValueTask UnloadAsync(CancellationToken cancellationToken)
    {
        _panel?.Dispose();
        _panel = null;
        return ValueTask.CompletedTask;
    }

    private static int ReadPort(JsonElement cfg) =>
        cfg.ValueKind == JsonValueKind.Object
            && cfg.TryGetProperty("port", out var p) && p.ValueKind == JsonValueKind.Number
            ? p.GetInt32() : 5276;
}
