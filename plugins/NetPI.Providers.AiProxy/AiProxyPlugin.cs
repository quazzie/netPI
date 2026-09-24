using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

[assembly: InternalsVisibleTo("NetPI.Providers.Tests")]

namespace NetPI.Providers.AiProxy;

/// <summary>
/// Registers the <c>aiproxy</c> provider (settings <c>providers.aiproxy</c>) plus one provider per entry of
/// <c>providers.openaiCompatible</c> (<c>[{ id, name, baseUrl, apiKey?, transport?, local?, headers?, ... }]</c>).
/// Extra endpoints are reconciled live when settings change.
/// </summary>
[NetPiPlugin("netpi.providers.aiproxy", Name = "AiProxy provider",
    Description = "OpenAI-compatible models via AiProxy (Responses or Chat Completions) and extra OpenAI-compatible endpoints.",
    Order = 10)]
public sealed class AiProxyPlugin : INetPiPlugin
{
    public const string ProviderId = "aiproxy";
    public const string SettingsPath = "providers.aiproxy";
    public const string ExtraSettingsPath = "providers.openaiCompatible";

    private sealed record Extra(OpenAiCompatibleProvider Provider, IDisposable Registration, string Key);

    private readonly Lock _gate = new();
    private readonly Dictionary<string, Extra> _extras = new(StringComparer.OrdinalIgnoreCase);
    private IPluginContext? _ctx;
    private HttpClient? _http;
    private OpenAiCompatibleProvider? _aiproxy;
    private IDisposable? _settingsSub;

    /// <summary>All providers currently registered by this plugin (aiproxy first).</summary>
    public IReadOnlyList<OpenAiCompatibleProvider> Providers
    {
        get
        {
            lock (_gate)
            {
                var list = new List<OpenAiCompatibleProvider>();
                if (_aiproxy is not null) list.Add(_aiproxy);
                list.AddRange(_extras.Values.Select(e => e.Provider));
                return list;
            }
        }
    }

    public Task StartAsync(IPluginContext context, CancellationToken ct)
    {
        context.Services.Register(new SettingsSection
        {
            Id = "aiproxy", Title = "AiProxy / nInfer", Group = "Models", Order = 30,
            Settings =
            [
                SettingInfo.Str("providers.aiproxy.baseUrl", "Server URL", "http://127.0.0.1:8090", "A trailing /v1 is fine."),
                SettingInfo.Choice("providers.aiproxy.transport", "Transport", "responses", ["responses", "chat"], "Responses API (/v1/responses) or Chat Completions."),
                SettingInfo.Secret("providers.aiproxy.apiKey", "API key", "Sent as Bearer; env:NAME reads an environment variable.", "none"),
                SettingInfo.Bool("providers.aiproxy.replayReasoning", "Send previous reasoning back", true, "Default: on for Responses, off for Chat."),
                SettingInfo.Bool("providers.aiproxy.parseThinkTags", "Split <think> tags into thinking", true),
                SettingInfo.Bool("providers.aiproxy.dumpFailedRequests", "Save failed requests", true, "Their bodies go to ~/.netpi/logs/failed-requests, for reproducing backend bugs."),
                SettingInfo.Bool("providers.aiproxy.includeEncryptedReasoning", "Request encrypted reasoning", false, "OpenAI-hosted reasoning models only; nInfer rejects it."),
                SettingInfo.Int("providers.aiproxy.defaultMaxOutputTokens", "Output limit when the catalog has none", 16384, null, 256, null, "tokens"),
                SettingInfo.Int("providers.aiproxy.modelsCacheSeconds", "Model list cache", 10, null, 0, 3600, "s"),
                SettingInfo.Bool("providers.aiproxy.local", "Local models", true, "Lanes take their size from the catalog's concurrency; local models are free."),
                SettingInfo.Bool("providers.aiproxy.enabled", "Enabled", true),
            ],
        });
        _ctx = context;
        _http = HttpFactory.Create();
        _aiproxy = OpenAiCompatibleProvider.FromSettingsPath(context, _http, ProviderId, "AiProxy", SettingsPath,
            new ProviderDefaults { BaseUrl = "http://127.0.0.1:8090", IsLocal = true, Transport = OpenAiTransport.Responses });
        context.Services.Register<IModelProvider>(_aiproxy);
        ReconcileExtras();
        _settingsSub = context.Events.Subscribe(EventTypes.SettingsChanged, _ => OnSettingsChanged());
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        _settingsSub?.Dispose();
        _settingsSub = null;
        lock (_gate)
        {
            foreach (var e in _extras.Values) SafeDispose(e.Registration);
            _extras.Clear();
        }
        _http?.Dispose();
        _http = null;
        return Task.CompletedTask;
    }

    private void OnSettingsChanged()
    {
        var ctx = _ctx;
        if (ctx is null || _http is null) return;
        try
        {
            if (ReconcileExtras()) ctx.Events.Publish(EventTypes.ModelsChanged, new JsonObject { ["provider"] = ProviderId });
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

    /// <summary>Register/unregister extra endpoints to match settings. Returns true when the set changed.</summary>
    internal bool ReconcileExtras()
    {
        var ctx = _ctx;
        var http = _http;
        if (ctx is null || http is null) return false;

        var wanted = new Dictionary<string, (string Name, string Key)>(StringComparer.OrdinalIgnoreCase);
        if (ctx.Settings.GetNode(ExtraSettingsPath) is JsonArray arr)
        {
            foreach (var node in arr)
            {
                if (node is not JsonObject o) continue;
                var id = o.Str("id")?.Trim();
                if (string.IsNullOrEmpty(id) || id.Contains('/') || string.Equals(id, ProviderId, StringComparison.OrdinalIgnoreCase))
                {
                    ctx.Logger.LogWarning("{Path}: ignoring entry with invalid id '{Id}'", ExtraSettingsPath, id);
                    continue;
                }
                if (string.IsNullOrWhiteSpace(o.Str("baseUrl")))
                {
                    ctx.Logger.LogWarning("{Path}: entry '{Id}' has no baseUrl", ExtraSettingsPath, id);
                    continue;
                }
                if (wanted.ContainsKey(id)) continue; // first entry wins
                var name = o.Str("name") is { Length: > 0 } n ? n : id;
                wanted[id] = (name, $"{name}|{IsLoopback(o.Str("baseUrl"))}");
            }
        }

        var changed = false;
        lock (_gate)
        {
            foreach (var (id, extra) in _extras.ToList())
            {
                if (wanted.TryGetValue(id, out var w) && w.Key == extra.Key) continue;
                SafeDispose(extra.Registration);
                _extras.Remove(id);
                changed = true;
            }
            foreach (var (id, w) in wanted)
            {
                if (_extras.ContainsKey(id)) continue;
                var entryId = id;
                var provider = new OpenAiCompatibleProvider(entryId, w.Name, http, () => FindEntry(ctx, entryId),
                    new ProviderDefaults { BaseUrl = "", IsLocal = w.Key.EndsWith("|True", StringComparison.Ordinal), Transport = OpenAiTransport.Chat },
                    ctx.Logger, ctx.Events, ctx.Paths.LogsDir);
                var reg = ctx.Services.Register<IModelProvider>(provider);
                _extras[id] = new Extra(provider, reg, w.Key);
                changed = true;
            }
        }
        return changed;
    }

    private static JsonObject? FindEntry(IPluginContext ctx, string id)
    {
        if (ctx.Settings.GetNode(ExtraSettingsPath) is not JsonArray arr) return null;
        foreach (var node in arr)
            if (node is JsonObject o && string.Equals(o.Str("id")?.Trim(), id, StringComparison.OrdinalIgnoreCase)) return o;
        return null;
    }

    private static bool IsLoopback(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.IsLoopback || u.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase));

    private static void SafeDispose(IDisposable d)
    {
        try { d.Dispose(); } catch { /* registry already disposed */ }
    }
}
