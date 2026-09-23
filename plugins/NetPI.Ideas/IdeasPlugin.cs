using System.Text.Json;
using NetPI.Abstractions;

namespace NetPI.Ideas;

/// <summary>
/// The NetPI.Ideas plugin: a per-project "do later" memory bank. Owns the
/// ideas tools (idea.add/list/get/update/remove — the agent's "store it in
/// ideas" surface) and the "Ideas" right-panel tab (this plugin's own Kestrel
/// port; insert-into-chat via ChatPrefillEvent, "do now" via the runner
/// contract). The bank lives in the session workspace's ideas.json.
/// </summary>
public sealed class IdeasPlugin : INetPiPlugin
{
    private IPluginContext? _ctx;
    private IdeasWebApp? _app;
    private IDisposable? _panel;
    private int _port;
    private IToolRegistry? _toolsRegistry;
    private IDisposable[] _toolRegistrations = [];
    private IDisposable? _toolsWatch;
    private readonly object _gate = new();

    public PluginInfo Info { get; } = new("netPI.Ideas", "Ideas", "0.1.0");

    public ValueTask LoadAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        _ctx = context;
        _port = ReadPort(context.OwnConfig);
        _app = new IdeasWebApp(context, _port);
        _panel = context.WebPanels.Register(new WebPanelDefinition(
            "ideas", "Ideas", "★", $"http://127.0.0.1:{_port}/panel/ideas", 8));
        return ValueTask.CompletedTask;
    }

    public async ValueTask StartAsync(CancellationToken cancellationToken)
    {
        var ctx = _ctx ?? throw new InvalidOperationException("Not loaded.");
        var app = _app ?? throw new InvalidOperationException("Web app not initialised.");
        await app.StartAsync(cancellationToken);
        // Port 0 (tests): re-register over our own entry with the real bound URL.
        if (_port == 0 && app.BoundUrl is { } url)
        {
            _panel?.Dispose();
            _panel = ctx.WebPanels.Register(new WebPanelDefinition("ideas", "Ideas", "★", url + "/panel/ideas", 8));
        }
        RegisterTools();
        _toolsWatch?.Dispose();
        _toolsWatch = ctx.Services.WatchServiceReplacement("tools", _ =>
        {
            lock (_gate)
            {
                try { RegisterTools(); ctx.Log.Information("Ideas re-registered tools into reloaded tools registry"); }
                catch (Exception ex) { ctx.Log.Error($"Ideas re-registration failed: {ex.Message}", ex); }
            }
        });
        ctx.Log.Information($"Ideas ready (surface: {app.BoundUrl ?? $"http://127.0.0.1:{_port}"})");
        await ValueTask.CompletedTask;
    }

    private void RegisterTools()
    {
        var ctx = _ctx ?? throw new InvalidOperationException("Not loaded.");
        IToolRegistry? registry = null;
        try { registry = ctx.Services.Resolve<IToolRegistry>("tools"); }
        catch { /* tools not loaded — skip registration */ }
        if (registry is null) return;

        var same = _toolsRegistry is not null && ReferenceEquals(_toolsRegistry, registry);
        if (same) foreach (var r in _toolRegistrations) r.Dispose();
        else _toolRegistrations = []; // stale handles belong to a reloaded registry

        var tools = new IAgentTool[]
        {
            new IdeaAddTool(), new IdeaListTool(), new IdeaGetTool(),
            new IdeaUpdateTool(), new IdeaRemoveTool(),
        };
        _toolRegistrations = tools.Select(t => registry.Register(t)).ToArray();
        _toolsRegistry = registry;
    }

    public ValueTask StopAsync(CancellationToken cancellationToken)
    {
        _toolsWatch?.Dispose();
        _toolsWatch = null;
        foreach (var r in _toolRegistrations) r.Dispose();
        _toolRegistrations = [];
        var webApp = _app;
        _app = null;
        if (webApp is not null)
        {
            _ = webApp.StopAsync(cancellationToken);
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask UnloadAsync(CancellationToken cancellationToken)
    {
        _panel?.Dispose();
        _panel = null;
        _ctx = null;
        return ValueTask.CompletedTask;
    }

    private static int ReadPort(JsonElement cfg) =>
        cfg.ValueKind == JsonValueKind.Object
            && cfg.TryGetProperty("port", out var p) && p.ValueKind == JsonValueKind.Number
            ? p.GetInt32() : 5277;
}
