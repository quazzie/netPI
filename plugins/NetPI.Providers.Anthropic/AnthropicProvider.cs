using System.Diagnostics;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace NetPI.Providers.Anthropic;

/// <summary>Claude through the Anthropic Messages API (API key auth). Settings are read on every call.</summary>
public sealed class AnthropicProvider : IModelProvider
{
    public const string ApiVersion = "2023-06-01";
    public const string NoKeyMessage =
        "Anthropic API key is not configured. Set providers.anthropic.apiKey in settings (or the ANTHROPIC_API_KEY environment variable).";

    private sealed record CacheEntry(IReadOnlyList<ModelInfo> Models, string Fingerprint, DateTimeOffset Expires);

    private readonly HttpClient _http;
    private readonly Func<JsonObject?> _config;
    private readonly ILogger _log;
    private readonly IEventBus? _events;
    private readonly SemaphoreSlim _listLock = new(1, 1);
    private volatile CacheEntry? _cache;
    private string? _signature;

    public AnthropicProvider(HttpClient http, Func<JsonObject?> config, ILogger? logger = null, IEventBus? events = null, string id = "anthropic")
    {
        Id = id;
        _http = http;
        _config = config;
        _log = logger ?? NullLogger.Instance;
        _events = events;
    }

    public string Id { get; }
    public string DisplayName => "Anthropic";
    public bool IsLocal => false;

    internal AnthropicOptions Options()
    {
        try { return AnthropicOptions.Parse(_config()); }
        catch (Exception ex) { _log.LogWarning(ex, "anthropic: could not read settings"); return AnthropicOptions.Parse(null); }
    }

    private static string CacheKey(AnthropicOptions o) => !o.Enabled ? "disabled" : o.ApiKey is null ? "nokey" : o.Fingerprint;

    /// <summary>True when the model list was fetched before and the settings changed since.</summary>
    public bool SettingsChangedSinceLastList => _cache is { } c && c.Fingerprint != CacheKey(Options());

    public void InvalidateModels() => _cache = null;

    // ================================================================ models

    public async Task<IReadOnlyList<ModelInfo>> ListModelsAsync(bool refresh, CancellationToken ct)
    {
        var o = Options();
        var key = CacheKey(o);
        var cache = _cache;
        if (!refresh && cache is not null && cache.Fingerprint == key && cache.Expires > DateTimeOffset.UtcNow) return cache.Models;

        await _listLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            cache = _cache;
            if (!refresh && cache is not null && cache.Fingerprint == key && cache.Expires > DateTimeOffset.UtcNow) return cache.Models;

            IReadOnlyList<ModelInfo> models;
            var ttl = TimeSpan.FromSeconds(o.ModelsCacheSeconds);
            if (!o.Enabled || o.ApiKey is null) models = [];
            else
            {
                try { models = await FetchModelsAsync(o, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    _log.LogWarning("anthropic: cannot list models ({Error}); using the static model list", ex.Message);
                    var ids = o.FallbackModels.Count > 0 ? o.FallbackModels : [.. ClaudeCapabilities.FallbackModels];
                    models = ids.Select(id => ClaudeCapabilities.ToModelInfo(Id, id, null)).ToList();
                    ttl = TimeSpan.FromSeconds(Math.Min(60, o.ModelsCacheSeconds));
                }
            }
            _cache = new CacheEntry(models, key, DateTimeOffset.UtcNow + ttl);
            PublishIfChanged(models);
            return models;
        }
        finally { _listLock.Release(); }
    }

    private async Task<IReadOnlyList<ModelInfo>> FetchModelsAsync(AnthropicOptions o, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(15));
        using var req = new HttpRequestMessage(HttpMethod.Get, o.Root + "/v1/models?limit=100");
        ApplyHeaders(req, o);
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, cts.Token).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw ProviderErrors.FromHttp("Anthropic", resp, await ProviderErrors.ReadBodySafeAsync(resp, cts.Token).ConfigureAwait(false));
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false));
        var list = new List<ModelInfo>();
        foreach (var e in doc.RootElement.Prop("data").Items())
        {
            var id = e.Str("id");
            if (string.IsNullOrWhiteSpace(id)) continue;
            var info = ClaudeCapabilities.ToModelInfo(Id, id, e.Str("display_name"),
                e.IntOrNull("max_input_tokens") ?? e.IntOrNull("context_window"), e.IntOrNull("max_tokens") ?? e.IntOrNull("max_output_tokens"));
            if (e.Str("created_at") is { } created) info.Extra = new JsonObject { ["created_at"] = created };
            list.Add(info);
        }
        return list;
    }

    private void PublishIfChanged(IReadOnlyList<ModelInfo> models)
    {
        var sig = string.Join("\n", models.Select(m => $"{m.Id}|{m.DisplayName}|{m.Status}|{m.ContextWindow}|{m.MaxOutputTokens}"));
        if (sig == _signature) return;
        var first = _signature is null;
        _signature = sig;
        if (first && models.Count == 0) return;
        try { _events?.Publish(EventTypes.ModelsChanged, new JsonObject { ["provider"] = Id }); }
        catch (Exception ex) { _log.LogDebug(ex, "anthropic: publishing models.changed failed"); }
    }

    // ================================================================ streaming

    public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        var o = Options();
        if (!o.Enabled) throw new ModelException("Anthropic: provider is disabled in settings.", false, null, "provider_disabled");
        if (o.ApiKey is null) throw new ModelException(NoKeyMessage, false, 401, "authentication_error");

        var body = AnthropicRequest.BuildBody(request, o, Id);
        using var httpReq = new HttpRequestMessage(HttpMethod.Post, o.Root + "/v1/messages");
        httpReq.Content = new ByteArrayContent(J.Utf8(body));
        httpReq.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        httpReq.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        ApplyHeaders(httpReq, o);

        HttpResponseMessage resp;
        try { resp = await _http.SendAsync(httpReq, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not ModelException) { throw Rethrow(ex, ct); }

        using (resp)
        {
            if (!resp.IsSuccessStatusCode)
            {
                var errBody = await ProviderErrors.ReadBodySafeAsync(resp, ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                throw ProviderErrors.FromHttp("Anthropic", resp, errBody);
            }

            Stream stream;
            try { stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not ModelException) { throw Rethrow(ex, ct); }

            var asm = new MessageAssembler();
            var parser = new AnthropicStreamParser(asm, "Anthropic");
            await foreach (var sse in ProviderErrors.Guard(SseReader.ReadAsync(stream, ct), "Anthropic", ct).ConfigureAwait(false))
            {
                parser.Handle(sse);
                foreach (var ev in asm.Drain()) yield return ev;
                if (parser.Finished) break;
            }
            parser.Finish();
            foreach (var ev in asm.Drain()) yield return ev;
            var ms = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            yield return new StreamCompleted(asm.Build(Id, request.Model.Id, request.SessionId, ms));
        }
    }

    private static Exception Rethrow(Exception ex, CancellationToken ct)
    {
        var t = ProviderErrors.Translate(ex, "Anthropic", ct);
        if (ReferenceEquals(t, ex)) ExceptionDispatchInfo.Capture(ex).Throw();
        return t;
    }

    private static void ApplyHeaders(HttpRequestMessage req, AnthropicOptions o)
    {
        if (o.ApiKey is not null) req.Headers.TryAddWithoutValidation("x-api-key", o.ApiKey);
        req.Headers.TryAddWithoutValidation("anthropic-version", ApiVersion);
        if (o.Betas.Count > 0) req.Headers.TryAddWithoutValidation("anthropic-beta", string.Join(",", o.Betas));
        foreach (var (k, v) in o.Headers) req.Headers.TryAddWithoutValidation(k, AnthropicOptions.ResolveSecret(v) ?? v);
    }
}
