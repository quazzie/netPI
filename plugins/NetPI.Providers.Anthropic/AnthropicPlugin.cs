using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;

[assembly: InternalsVisibleTo("NetPI.Providers.Tests")]

namespace NetPI.Providers.Anthropic;

/// <summary>Registers the <c>anthropic</c> provider (settings <c>providers.anthropic</c>).</summary>
[NetPiPlugin("netpi.providers.anthropic", Name = "Anthropic provider",
    Description = "Claude models through the Anthropic Messages API (API key, thinking, prompt caching).",
    Order = 10)]
public sealed class AnthropicPlugin : INetPiPlugin
{
    public const string SettingsPath = "providers.anthropic";

    private HttpClient? _http;
    private IDisposable? _settingsSub;

    public AnthropicProvider? Provider { get; private set; }

    public Task StartAsync(IPluginContext context, CancellationToken ct)
    {
        _http = HttpFactory.Create();
        Provider = new AnthropicProvider(_http, () => context.Settings.GetNode(SettingsPath) as JsonObject, context.Logger, context.Events);
        context.Services.Register<IModelProvider>(Provider);
        _settingsSub = context.Events.Subscribe(EventTypes.SettingsChanged, evt =>
        {
            // Key/baseUrl/enabled changed: refresh in the background (publishes models.changed when the list changes).
            if (Provider is { SettingsChangedSinceLastList: true } p) _ = RefreshQuietlyAsync(p, context.Stopping);
        });
        return Task.CompletedTask;
    }

    private static async Task RefreshQuietlyAsync(AnthropicProvider p, CancellationToken ct)
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
