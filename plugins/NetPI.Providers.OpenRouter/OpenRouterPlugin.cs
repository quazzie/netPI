using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;

[assembly: InternalsVisibleTo("NetPI.Providers.Tests")]

namespace NetPI.Providers.OpenRouter;

/// <summary>Registers the <c>openrouter</c> provider providers (settings <c>providers.instances</c>).</summary>
[NetPiPlugin("netpi.providers.openrouter", Name = "OpenRouter provider",
    Description = "Models through OpenRouter (API key): tool calls, unified reasoning with replayed reasoning_details, prompt caching.",
    Order = 10)]
public sealed class OpenRouterPlugin : INetPiPlugin
{
    public const string Type = "openrouter";

    private HttpClient? _http;
    private IDisposable? _settingsSub;
    private ProviderSet<OpenRouterProvider>? _set;

    /// <summary>The first OpenRouter provider the owner added, if any.</summary>
    public OpenRouterProvider? Provider => _set?.All.FirstOrDefault();

    public Task StartAsync(IPluginContext context, CancellationToken ct)
    {
        context.Services.Register(new SettingsSection
        {
            Id = "provider-openrouter", Title = "OpenRouter", Group = "Models", Order = 40, ProviderType = Type,
            Help = "Hundreds of hosted models through one API key.",
            Settings =
            [
                SettingInfo.Secret("apiKey", "API key", "env:NAME reads another environment variable.", "env OPENROUTER_API_KEY"),
                SettingInfo.List("include", "Models to offer", null, "Model ids or globs, e.g. stealth/*, anthropic/claude-*, *:free.", "all models with tool calls"),
                SettingInfo.Int("maxOutputTokens", "Output limit", 32768, "Reasoning counts against it.", 256, null, "tokens"),
                SettingInfo.Bool("promptCaching", "Prompt caching for anthropic/* models", true),
                SettingInfo.Bool("replayReasoning", "Send reasoning_details back", true),
                SettingInfo.Bool("parseThinkTags", "Split <think> tags into thinking", true),
                SettingInfo.Bool("dumpFailedRequests", "Save failed requests", true),
                SettingInfo.Str("baseUrl", "Server URL", "https://openrouter.ai/api"),
                SettingInfo.Int("modelsCacheSeconds", "Model list cache", 600, null, 0, 86400, "s"),
                SettingInfo.Bool("enabled", "Enabled", true),
            ],
        });
        _http = HttpFactory.Create();
        var http = _http;
        _set = new ProviderSet<OpenRouterProvider>(context, Type, (id, config, read) =>
            new OpenRouterProvider(http, read, context.Logger, context.Events, context.Paths.LogsDir, id) { DisplayName = ProviderSet<OpenRouterProvider>.NameOf(id, config, "OpenRouter") });
        _set.Reconcile();
        _settingsSub = context.Events.Subscribe(EventTypes.SettingsChanged, evt =>
        {
            if (_set is not { } set) return;
            if (set.Reconcile()) context.Events.Publish(EventTypes.ModelsChanged, new JsonObject { ["provider"] = Type });
            // Key/baseUrl/include changed: refresh in the background (publishes models.changed when the list changes).
            foreach (var p in set.All)
                if (p.SettingsChangedSinceLastList) _ = RefreshQuietlyAsync(p, context.Stopping);
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
        _set?.Dispose();
        _set = null;
        _http?.Dispose();
        _http = null;
        return Task.CompletedTask;
    }
}
