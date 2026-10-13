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
    public const string Type = "anthropic";

    private HttpClient? _http;
    private IDisposable? _settingsSub;
    private ProviderSet<AnthropicProvider>? _set;

    /// <summary>The first Anthropic provider the owner added, if any.</summary>
    public AnthropicProvider? Provider => _set?.All.FirstOrDefault();

    public Task StartAsync(IPluginContext context, CancellationToken ct)
    {
        context.Services.Register(new SettingsSection
        {
            Id = "provider-anthropic", Title = "Anthropic", Group = "Models", Order = 50, ProviderType = Type,
            Help = "Claude through the Messages API with an API key: thinking, prompt caching.",
            Settings =
            [
                SettingInfo.Secret("apiKey", "API key", "env:NAME reads another environment variable.", "env ANTHROPIC_API_KEY"),
                SettingInfo.Choice("thinking", "Thinking", "auto", ["auto", "budget", "adaptive", "off"],
                    "auto: by model (budget_tokens up to Claude 4.5, adaptive from 4.6); budget: budget_tokens from the effort; adaptive: the model decides."),
                SettingInfo.Choice("thinkingDisplay", "Thinking text", "summarized", ["summarized", "omitted"],
                    "With adaptive thinking: summarized shows the model's thinking, omitted leaves it out (the API's own default from Claude 4.7 on)."),
                SettingInfo.Bool("adaptiveEffort", "Send the effort with adaptive thinking", true),
                SettingInfo.Bool("promptCaching", "Prompt caching", true),
                SettingInfo.Int("defaultMaxOutputTokens", "Output limit", 32000, null, 256, null, "tokens"),
                SettingInfo.Bool("dumpFailedRequests", "Save failed requests", true,
                    "Write the request and response body of a failed call to logs/failed-requests, for reproducing backend bugs."),
                SettingInfo.List("betas", "anthropic-beta headers", []),
                SettingInfo.Str("baseUrl", "Server URL", "https://api.anthropic.com"),
                SettingInfo.Int("modelsCacheSeconds", "Model list cache", 600, null, 0, 86400, "s"),
            ],
        });
        _http = HttpFactory.Create();
        var http = _http;
        _set = new ProviderSet<AnthropicProvider>(context, Type, (id, config, read) =>
            new AnthropicProvider(http, read, context.Logger, context.Events, id, context.Paths.LogsDir) { DisplayName = ProviderSet<AnthropicProvider>.NameOf(id, config, "Anthropic") });
        _set.Reconcile();
        _settingsSub = context.Events.Subscribe(EventTypes.SettingsChanged, evt =>
        {
            if (_set is not { } set) return;
            if (set.Reconcile()) context.Events.Publish(EventTypes.ModelsChanged, new JsonObject { ["provider"] = Type });
            // Key/baseUrl/enabled changed: refresh in the background (publishes models.changed when the list changes).
            foreach (var p in set.All)
                if (p.SettingsChangedSinceLastList) _ = RefreshQuietlyAsync(p, context.Stopping);
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
        _set?.Dispose();
        _set = null;
        _http?.Dispose();
        _http = null;
        return Task.CompletedTask;
    }
}
