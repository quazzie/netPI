using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

[assembly: InternalsVisibleTo("NetPI.Providers.Tests")]

namespace NetPI.Providers.AiProxy;

/// <summary>
/// Offers two provider types, both OpenAI-compatible: <c>aiproxy</c> (AiProxy / nInfer: Responses API, local models) and
/// <c>openai-compatible</c> (any other endpoint: Chat Completions, local only for loopback URLs), and runs one provider per
/// entry of <c>providers.instances</c> of those types. Entries are reconciled live when settings change.
/// </summary>
[NetPiPlugin("netpi.providers.aiproxy", Name = "AiProxy provider",
    Description = "OpenAI-compatible models via AiProxy (Responses or Chat Completions) and other OpenAI-compatible endpoints.",
    Order = 10)]
public sealed class AiProxyPlugin : INetPiPlugin
{
    public const string AiProxyType = "aiproxy";
    public const string OpenAiType = "openai-compatible";

    private IPluginContext? _ctx;
    private HttpClient? _http;
    private ProviderSet<OpenAiCompatibleProvider>? _aiproxy;
    private ProviderSet<OpenAiCompatibleProvider>? _openai;
    private IDisposable? _settingsSub;
    private CancellationTokenSource? _pollCts;
    private Task? _poll;

    /// <summary>All providers currently registered by this plugin (AiProxy ones first).</summary>
    public IReadOnlyList<OpenAiCompatibleProvider> Providers => [.. _aiproxy?.All ?? [], .. _openai?.All ?? []];

    public Task StartAsync(IPluginContext context, CancellationToken ct)
    {
        context.Services.Register(new SettingsSection
        {
            Id = "provider-aiproxy", Title = "AiProxy / nInfer", Group = "Models", Order = 30, ProviderType = AiProxyType,
            Help = "A local AiProxy or nInfer server: the Responses API by default, and its models are local (free, loaded on demand).",
            Settings =
            [
                SettingInfo.Str("baseUrl", "Server URL", "http://127.0.0.1:8090", "A trailing /v1 is fine."),
                SettingInfo.Choice("transport", "Transport", "responses", ["responses", "chat"], "Responses API (/v1/responses) or Chat Completions."),
                SettingInfo.Secret("apiKey", "API key", "Sent as Bearer; env:NAME reads an environment variable.", "none"),
                SettingInfo.Bool("replayReasoning", "Send previous reasoning back", true, "Default: on for Responses, off for Chat."),
                SettingInfo.Bool("parseThinkTags", "Split <think> tags into thinking", true),
                SettingInfo.Bool("dumpFailedRequests", "Save failed requests", true, "Their bodies go to ~/.netpi/logs/failed-requests, for reproducing backend bugs."),
                SettingInfo.Bool("includeEncryptedReasoning", "Request encrypted reasoning", false, "OpenAI-hosted reasoning models only; nInfer rejects it."),
                SettingInfo.Int("defaultMaxOutputTokens", "Output limit when the catalog has none", 16384, null, 256, null, "tokens"),
                SettingInfo.Int("modelsCacheSeconds", "Model list cache", 10, null, 0, 3600, "s"),
                SettingInfo.Bool("local", "Local models", true, "Their agents share the slots the catalog reports (concurrency); local models are free."),
                SettingInfo.Bool("enabled", "Enabled", true),
            ],
        });
        context.Services.Register(new SettingsSection
        {
            Id = "provider-openai-compatible", Title = "OpenAI-compatible", Group = "Models", Order = 31, ProviderType = OpenAiType,
            Help = "Any server with an OpenAI-style API: LM Studio, a remote llama.cpp, vLLM, OpenAI itself. Chat Completions by default.",
            Settings =
            [
                SettingInfo.Str("baseUrl", "Server URL", null, "A trailing /v1 is fine.", "http://127.0.0.1:1234"),
                SettingInfo.Secret("apiKey", "API key", "Sent as Bearer; env:NAME reads an environment variable.", "none"),
                SettingInfo.Choice("transport", "Transport", "chat", ["chat", "responses"], "Chat Completions (/v1/chat/completions) or the Responses API."),
                new SettingInfo { Key = "local", Type = "bool", Label = "Local models", Help = "Unset: local only for a loopback URL. Local models are free and their agents share the slots the catalog reports." },
                SettingInfo.Bool("replayReasoning", "Send previous reasoning back", false),
                SettingInfo.Bool("parseThinkTags", "Split <think> tags into thinking", true),
                SettingInfo.Int("defaultMaxOutputTokens", "Output limit when the catalog has none", 16384, null, 256, null, "tokens"),
                SettingInfo.Int("modelsCacheSeconds", "Model list cache", 10, null, 0, 3600, "s"),
                SettingInfo.Bool("enabled", "Enabled", true),
            ],
        });
        _ctx = context;
        _http = HttpFactory.Create();
        var http = _http;
        _aiproxy = new ProviderSet<OpenAiCompatibleProvider>(context, AiProxyType, (id, config, read) =>
            new OpenAiCompatibleProvider(id, ProviderSet<OpenAiCompatibleProvider>.NameOf(id, config, "AiProxy"), http, read,
                new ProviderDefaults { BaseUrl = "http://127.0.0.1:8090", IsLocal = true, Transport = OpenAiTransport.Responses },
                context.Logger, context.Events, context.Paths.LogsDir));
        _openai = new ProviderSet<OpenAiCompatibleProvider>(context, OpenAiType, (id, config, read) =>
            new OpenAiCompatibleProvider(id, ProviderSet<OpenAiCompatibleProvider>.NameOf(id, config, "OpenAI-compatible"), http, read,
                new ProviderDefaults { BaseUrl = "", IsLocal = IsLoopback(config.Str("baseUrl")), Transport = OpenAiTransport.Chat },
                context.Logger, context.Events, context.Paths.LogsDir),
            // the loopback default depends on the URL: a changed one is a new provider
            (id, config) => $"{ProviderSet<OpenAiCompatibleProvider>.NameOf(id, config, "OpenAI-compatible")}|{IsLoopback(config.Str("baseUrl"))}",
            (id, config) => string.IsNullOrWhiteSpace(config.Str("baseUrl")) ? "it has no baseUrl" : null);
        _aiproxy.Reconcile();
        _openai.Reconcile();
        _settingsSub = context.Events.Subscribe(EventTypes.SettingsChanged, _ => OnSettingsChanged());
        _pollCts = CancellationTokenSource.CreateLinkedTokenSource(context.Stopping);
        _poll = PollLocalModelsAsync(_pollCts.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct)
    {
        _pollCts?.Cancel();
        if (_poll is not null)
        {
            try { await _poll.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        _pollCts?.Dispose();
        _pollCts = null;
        _poll = null;
        _settingsSub?.Dispose();
        _settingsSub = null;
        _aiproxy?.Dispose();
        _openai?.Dispose();
        _aiproxy = _openai = null;
        _http?.Dispose();
        _http = null;
    }

    // Local backend/profile changes occur outside NetPI settings (AiHub switching a loadout). Keep the provider cache current
    // even when no UI requests the catalog. Local lists are capped at a 1 s TTL, so each tick really reads the backend.
    private async Task PollLocalModelsAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            foreach (var p in Providers.Where(p => p.IsLocal))
            {
                try { await p.ListModelsAsync(refresh: false, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                catch { /* provider logs listing failures and publishes offline status */ }
            }
    }

    private void OnSettingsChanged()
    {
        var ctx = _ctx;
        if (ctx is null || _http is null) return;
        try
        {
            var changed = (_aiproxy?.Reconcile() ?? false) | (_openai?.Reconcile() ?? false);
            if (changed) ctx.Events.Publish(EventTypes.ModelsChanged, new JsonObject { ["provider"] = AiProxyType });
            // Endpoint settings changed (baseUrl, key, enabled...): refresh in the background; publishes models.changed if needed.
            foreach (var p in Providers)
                if (p.SettingsChangedSinceLastList)
                    _ = RefreshQuietlyAsync(p, ctx.Stopping);
        }
        catch (Exception ex) { ctx.Logger.LogWarning(ex, "aiproxy: applying settings change failed"); }
    }

    private static async Task RefreshQuietlyAsync(OpenAiCompatibleProvider p, CancellationToken ct)
    {
        try { await p.ListModelsAsync(refresh: true, ct).ConfigureAwait(false); }
        catch { /* logged by the provider; cancellation on unload */ }
    }

    private static bool IsLoopback(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.IsLoopback || u.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase));
}
