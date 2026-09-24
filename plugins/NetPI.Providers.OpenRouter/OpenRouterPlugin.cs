using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;

[assembly: InternalsVisibleTo("NetPI.Providers.Tests")]

namespace NetPI.Providers.OpenRouter;

/// <summary>Registers the <c>openrouter</c> provider (settings <c>providers.openrouter</c>).</summary>
[NetPiPlugin("netpi.providers.openrouter", Name = "OpenRouter provider",
    Description = "Models through OpenRouter (API key): tool calls, unified reasoning with replayed reasoning_details, prompt caching.",
    Order = 10)]
public sealed class OpenRouterPlugin : INetPiPlugin
{
    public const string SettingsPath = "providers.openrouter";

    private HttpClient? _http;
    private IDisposable? _settingsSub;

    public OpenRouterProvider? Provider { get; private set; }

    public Task StartAsync(IPluginContext context, CancellationToken ct)
    {
        _http = HttpFactory.Create();
        Provider = new OpenRouterProvider(_http, () => context.Settings.GetNode(SettingsPath) as JsonObject, context.Logger, context.Events, context.Paths.LogsDir);
        context.Services.Register<IModelProvider>(Provider);
        _settingsSub = context.Events.Subscribe(EventTypes.SettingsChanged, evt =>
        {
            // Key/baseUrl/include changed: refresh in the background (publishes models.changed when the list changes).
            if (Provider is { SettingsChangedSinceLastList: true } p) _ = RefreshQuietlyAsync(p, context.Stopping);
        });
        return Task.CompletedTask;
    }

    private static async Task RefreshQuietlyAsync(OpenRouterProvider p, CancellationToken ct)
    {
        try { await p.ListModelsAsync(refresh: true, ct).ConfigureAwait(false); }
        catch { /* logged by the provider; cancellation on unload */ }
    }

    public Task StopAsync(CancellationToken ct)
    {
        _settingsSub?.Dispose();
        _settingsSub = null;
        _http?.Dispose();
        _http = null;
        return Task.CompletedTask;
    }
}
