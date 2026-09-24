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
        context.Services.Register(new SettingsSection
        {
            Id = "anthropic", Title = "Anthropic", Group = "Models", Order = 50,
            Settings =
            [
                SettingInfo.Secret("providers.anthropic.apiKey", "API key", "env:NAME reads another environment variable.", "env ANTHROPIC_API_KEY"),
                SettingInfo.Choice("providers.anthropic.thinking", "Thinking", "budget", ["budget", "adaptive", "off"], "budget: budget_tokens from the effort; adaptive: the model decides."),
                SettingInfo.Bool("providers.anthropic.adaptiveEffort", "Send the effort with adaptive thinking", true),
                SettingInfo.Bool("providers.anthropic.promptCaching", "Prompt caching", true),
                SettingInfo.Int("providers.anthropic.defaultMaxOutputTokens", "Output limit", 32000, null, 256, null, "tokens"),
                SettingInfo.List("providers.anthropic.betas", "anthropic-beta headers", []),
                SettingInfo.Str("providers.anthropic.baseUrl", "Server URL", "https://api.anthropic.com"),
                SettingInfo.Int("providers.anthropic.modelsCacheSeconds", "Model list cache", 600, null, 0, 86400, "s"),
            ],
        });
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
