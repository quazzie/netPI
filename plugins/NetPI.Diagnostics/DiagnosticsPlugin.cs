using System.Text.Json;
using NetPI.Abstractions;

namespace NetPI.Diagnostics;

/// <summary>
/// The first-class diagnostics plugin (PLAN §47). Self-registers the
/// "diagnostics" panel tab (replacing the Web plugin's placeholder) and serves
/// read-only /api/diag endpoints plus the live /panel/diagnostics page on its
/// own localhost Kestrel port. Data comes from the host event bus and host-
/// registered services — no cross-plugin assembly references.
/// </summary>
public sealed class DiagnosticsPlugin : INetPiPlugin
{
    private DiagApp? _app;
    private IDisposable? _panel;

    public PluginInfo Info { get; } = new("netPI.Diagnostics", "Diagnostics", "0.1.0");

    public ValueTask LoadAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        var cfg = context.OwnConfig;
        int port = cfg.ValueKind == JsonValueKind.Object
                   && cfg.TryGetProperty("port", out var p) && p.ValueKind == JsonValueKind.Number
            ? p.GetInt32() : 5274;

        var runtimeDir = Environment.GetEnvironmentVariable("NETPI_HOME")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".netpi");

        _app = new DiagApp(context, context.Log, port, runtimeDir);

        // First-class panel registration (PLAN §47): the Svelte shell renders
        // whatever the panel catalog (ui.panels) contains. The entry URL is
        // absolute because the panel page lives on this plugin's own port
        // (iframe src accepts it); the host removes the registration on unload.
        _panel = context.WebPanels.Register(new WebPanelDefinition(
            "diagnostics", "Diagnostics", "◌", $"http://127.0.0.1:{port}/panel/diagnostics", 10));
        return ValueTask.CompletedTask;
    }

    public async ValueTask StartAsync(CancellationToken cancellationToken)
    {
        if (_app is null) return;
        await _app.StartAsync(cancellationToken);
        await ValueTask.CompletedTask;
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken)
    {
        if (_app is null) return;
        var app = _app;
        _app = null;
        await app.StopAsync(cancellationToken);
        await ValueTask.CompletedTask;
    }

    public async ValueTask UnloadAsync(CancellationToken cancellationToken)
    {
        _panel?.Dispose();
        _panel = null;
        await ValueTask.CompletedTask;
    }
}
