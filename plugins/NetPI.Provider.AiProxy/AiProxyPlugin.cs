using System.Text.Json;
using NetPI.Abstractions;

namespace NetPI.Provider.AiProxy;

/// <summary>
/// The reloadable provider plugin (PLAN §13). Creates an <see cref="HttpClient"/>
/// from <c>~/.netpi/config.json</c> (baseUrl + apiKey) and registers
/// <see cref="IModelProvider"/> + <see cref="IModelCatalog"/>.
/// </summary>
public sealed class AiProxyPlugin : INetPiPlugin
{
    private AiProxyProvider? _provider;
    private HttpClient? _http;

    public PluginInfo Info { get; } = new("netPI.Provider.AiProxy", "AiProxy Provider", "0.1.0");

    public async ValueTask LoadAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        var baseUrl = Str(context.OwnConfig, "baseUrl");
        if (string.IsNullOrEmpty(baseUrl))
            throw new InvalidOperationException("netPI.Provider.AiProxy requires 'baseUrl' in config");

        var apiKey = Str(context.OwnConfig, "apiKey");
        _http = new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) });
        _http.BaseAddress = new Uri(baseUrl);
        if (!string.IsNullOrEmpty(apiKey))
            _http.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");

        var wire = Str(context.OwnConfig, "wire");
        _provider = new AiProxyProvider(_http, baseUrl, context.Log, string.IsNullOrEmpty(wire) ? "auto" : wire, context.Events);
        _provider = new AiProxyProvider(_http, baseUrl, context.Log, string.IsNullOrEmpty(wire) ? "auto" : wire, context.Events);
        context.Services.Register<IModelProvider>("provider", _provider);
        context.Services.Register<IModelCatalog>("catalog", _provider);
        // astra-2: the Follow-AiProxy capacity source (service "provider-capacity")
        // — metadata polling only; the NetPI.Lanes scheduler consumes it and
        // enforces admission in netPI itself (astra-2 §5.3).
        context.Services.Register<IProviderCapacitySource>("provider-capacity",
            new AiProxyCapacitySource(_http, baseUrl, context.Log));
        context.Log.Information($"AiProxy provider configured (baseUrl={baseUrl})");

        // Best-effort initial catalog refresh; failures are non-fatal (the UI
        // can trigger models.refresh later).
        try { await _provider.RefreshAsync(cancellationToken); }
        catch (Exception ex) { context.Log.Warning($"Initial catalog refresh failed: {ex.Message}"); }
    }

    public ValueTask StartAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

    public async ValueTask StopAsync(CancellationToken cancellationToken)
    {
        _http?.Dispose();
        _http = null;
        _provider = null;
        await ValueTask.CompletedTask;
    }

    public ValueTask UnloadAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

    private static string? Str(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString()
            : null;
}
