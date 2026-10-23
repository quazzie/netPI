using System.Diagnostics;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
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

    private readonly HttpClient _http;
    private readonly Func<JsonObject?> _config;
    private readonly ILogger _log;
    private readonly string? _dumpDir;
    private readonly ModelListCache _models;
    /// <summary>Models whose chats met a thinking block bound to another prefix: their calls drop such blocks from then on.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _dropUnboundThinking = new(StringComparer.OrdinalIgnoreCase);

    public AnthropicProvider(HttpClient http, Func<JsonObject?> config, ILogger? logger = null, IEventBus? events = null,
        string id = "anthropic", string? logsDir = null)
    {
        Id = id;
        _http = http;
        _config = config;
        _log = logger ?? NullLogger.Instance;
        _dumpDir = logsDir is null ? null : Path.Combine(logsDir, "failed-requests");
        _models = new ModelListCache(id, _log, events);
    }

    public string Id { get; }
    public string DisplayName { get; init; } = "Anthropic";
    public bool IsLocal => false;

    internal AnthropicOptions Options()
    {
        try { return AnthropicOptions.Parse(_config()); }
        catch (Exception ex) { _log.LogWarning(ex, "anthropic: could not read settings"); return AnthropicOptions.Parse(null); }
    }

    private static string CacheKey(AnthropicOptions o) => !o.Enabled ? "disabled" : o.ApiKey is null ? "nokey" : o.Fingerprint;

    /// <summary>True when the model list was fetched before and the settings changed since.</summary>
    public bool SettingsChangedSinceLastList => _models.SettingsChanged(CacheKey(Options()));

    public void InvalidateModels() => _models.Invalidate();

    // ================================================================ models

    public async Task<IReadOnlyList<ModelInfo>> ListModelsAsync(bool refresh, CancellationToken ct)
    {
        var o = Options();
        var policy = new ModelListCache.Policy
        {
            Fingerprint = CacheKey(o),
            CanFetch = o.Enabled && o.ApiKey is not null,
            Ttl = TimeSpan.FromSeconds(o.ModelsCacheSeconds),
            FailureTtl = TimeSpan.FromSeconds(Math.Min(60, o.ModelsCacheSeconds)),
            Url = o.Root,
            LogOnce = false, // every failed attempt is a line: the fallback table is what answers, and the log is where it says why
            // A failed call falls back to the static capability table, not to the last list: the models API returns
            // ids alone, and it is that table which says what a Claude id can do.
            OnFailure = () => (o.FallbackModels.Count > 0 ? o.FallbackModels : [.. ClaudeCapabilities.FallbackModels])
                .Select(id => ClaudeCapabilities.ToModelInfo(Id, id, null)).ToList(),
        };
        return await _models.ListAsync(refresh, policy, c => FetchModelsAsync(o, c), ct).ConfigureAwait(false);
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

    // ================================================================ streaming

    public IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken ct) =>
        HttpTransport.StreamAsync(StreamCoreAsync, request, new CallInfo { Transport = "messages" }, Id, _dumpDir, _log, ct);

    private async IAsyncEnumerable<ModelStreamEvent> StreamCoreAsync(ModelRequest request, CallInfo call, [EnumeratorCancellation] CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        var o = Options();
        if (!o.Enabled) throw new ModelException("Anthropic: provider is disabled in settings.", false, null, "provider_disabled");
        if (o.ApiKey is null) throw new ModelException(NoKeyMessage, false, 401, "authentication_error");

        if (!Uri.TryCreate(o.Root, UriKind.Absolute, out _))
            throw new ModelException($"Anthropic: no valid baseUrl configured ('{o.BaseUrl}').", false, null, "invalid_config");

        var binding = _dropUnboundThinking.ContainsKey(request.Model.Id);
        HttpResponseMessage resp;
        for (var attempt = 0; ; attempt++)
        {
            var body = AnthropicRequest.BuildBody(request, o, Id);
            if (binding) AnthropicRequest.DropUnboundThinking(body);
            using var httpReq = new HttpRequestMessage(HttpMethod.Post, o.Root + "/v1/messages");
            httpReq.Content = new ByteArrayContent(J.Utf8(body));
            httpReq.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            httpReq.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            ApplyHeaders(httpReq, o, binding && AnthropicRequest.HasBlockBinding(body) ? AnthropicRequest.BindingBeta : null);
            call.Url = httpReq.RequestUri?.ToString();
            call.Body = body;
            call.Dump = o.DumpFailedRequests;

            try { resp = await _http.SendAsync(httpReq, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not ModelException) { throw HttpTransport.Rethrow(ex, "Anthropic", ct); }

            // The id Anthropic puts on every response and every error page: the only way to find the failure in their logs.
            if (resp.Headers.TryGetValues("request-id", out var rid)) call.RequestId = rid.FirstOrDefault();
            if (resp.IsSuccessStatusCode) break;
            using var failed = resp;
            var errBody = await ProviderErrors.ReadBodySafeAsync(resp, ct).ConfigureAwait(false);
            call.ResponseBody = errBody;
            ct.ThrowIfCancellationRequested();
            // Claude 5.5-era models bind a replayed thinking block to the system prompt, tools and messages before it:
            // a publish that changed a tool description, or a reload that changed the tool list, invalidates every
            // thinking block of a chat (HTTP 400 "bound to a different conversation"), and retrying the same body never
            // clears it. The call goes again with the blocks that no longer verify dropped (block_binding drop_block,
            // or stripped where the request has no adaptive thinking to carry the setting), and the model keeps that
            // from then on: the stored history still holds the old signatures.
            if (attempt == 0 && !binding && (int)resp.StatusCode == 400 && AnthropicRequest.IsBindingMismatch(errBody))
            {
                _log.LogInformation("anthropic: thinking blocks bound to an earlier prefix (session {Session}, {Model}); retrying with them dropped",
                    request.SessionId, request.Model.Id);
                _dropUnboundThinking[request.Model.Id] = true;
                binding = true;
                continue;
            }
            throw ProviderErrors.FromHttp("Anthropic", resp, errBody);
        }

        using (resp)
        {
            var asm = new MessageAssembler();
            var parser = new AnthropicStreamParser(asm, "Anthropic");

            // A gateway that ignores stream:true answers with the plain Messages JSON (or an error page with a 200).
            if (resp.Content.Headers.ContentType?.MediaType is "application/json")
            {
                string text;
                try { text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false); }
                catch (Exception ex) when (ex is not ModelException) { throw HttpTransport.Rethrow(ex, "Anthropic", ct); }
                call.ResponseBody = ProviderErrors.Cap(text); // same cap as the non-2xx path
                try { parser.HandleJsonBody(text); }
                finally { call.RequestId ??= parser.RequestId; }
            }
            else
            {
                Stream stream;
                try { stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false); }
                catch (Exception ex) when (ex is not ModelException) { throw HttpTransport.Rethrow(ex, "Anthropic", ct); }

                await foreach (var sse in ProviderErrors.Guard(SseReader.ReadAsync(stream, ct, "Anthropic"), "Anthropic", ct).ConfigureAwait(false))
                {
                    // An error frame carries its own request id, for a gateway that sends no header.
                    try { parser.Handle(sse); }
                    finally { call.RequestId ??= parser.RequestId; }
                    foreach (var ev in asm.Drain()) yield return ev;
                    if (parser.Finished) break;
                }
            }
            parser.Finish();
            foreach (var ev in asm.Drain()) yield return ev;
            var ms = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            yield return new StreamCompleted(asm.Build(Id, request.Model.Id, request.SessionId, ms));
        }
    }

    private static void ApplyHeaders(HttpRequestMessage req, AnthropicOptions o, string? extraBeta = null)
    {
        if (o.ApiKey is not null) req.Headers.TryAddWithoutValidation("x-api-key", o.ApiKey);
        req.Headers.TryAddWithoutValidation("anthropic-version", ApiVersion);
        var betas = extraBeta is null || o.Betas.Contains(extraBeta) ? o.Betas : [.. o.Betas, extraBeta];
        if (betas.Count > 0) req.Headers.TryAddWithoutValidation("anthropic-beta", string.Join(",", betas));
        HttpTransport.ApplyCustomHeaders(req, o.Headers);
    }
}
