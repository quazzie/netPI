using System.Diagnostics;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace NetPI.Providers.AiProxy;

/// <summary>
/// A provider for one OpenAI-compatible endpoint (AiProxy, llama.cpp, vLLM, OpenAI...). Two streaming transports:
/// Responses (<c>/v1/responses</c>, default) and Chat Completions (<c>/v1/chat/completions</c>). Settings are read
/// on every call through <c>config</c>, so edits apply live.
/// </summary>
public sealed class OpenAiCompatibleProvider : IModelProvider
{
    private readonly HttpClient _http;
    private readonly Func<JsonObject?> _config;
    private readonly ProviderDefaults _defaults;
    private readonly ILogger _log;
    private readonly string? _dumpDir;
    private readonly ModelListCache _models;
    /// <summary>Models whose catalog entry reported input_modalities: id → accepts images.</summary>
    private volatile Dictionary<string, bool> _imageSupport = new(StringComparer.Ordinal);

    public OpenAiCompatibleProvider(string id, string displayName, HttpClient http, Func<JsonObject?> config,
        ProviderDefaults? defaults = null, ILogger? logger = null, IEventBus? events = null, string? logsDir = null)
    {
        _dumpDir = logsDir is null ? null : Path.Combine(logsDir, "failed-requests");
        Id = id;
        DisplayName = displayName;
        _http = http;
        _config = config;
        _defaults = defaults ?? new ProviderDefaults();
        _log = logger ?? NullLogger.Instance;
        _models = new ModelListCache(id, _log, events);
    }

    /// <summary>Provider whose settings object lives at a dotted settings path (e.g. "providers.aiproxy").</summary>
    public static OpenAiCompatibleProvider FromSettingsPath(IPluginContext ctx, HttpClient http, string id, string displayName,
        string settingsPath, ProviderDefaults? defaults = null) =>
        new(id, displayName, http, () => ctx.Settings.GetNode(settingsPath) as JsonObject, defaults, ctx.Logger, ctx.Events, ctx.Paths.LogsDir);

    public string Id { get; }
    public string DisplayName { get; }
    public bool IsLocal => Options().IsLocal;

    internal ProviderOptions Options() => ProviderOptions.Parse(SafeConfig(), _defaults);

    private JsonObject? SafeConfig()
    {
        try { return _config(); }
        catch (Exception ex) { _log.LogWarning(ex, "{Provider}: could not read settings", Id); return null; }
    }

    /// <summary>Drop the cached model list (next <see cref="ListModelsAsync"/> refetches).</summary>
    public void InvalidateModels() => _models.Invalidate();

    /// <summary>True when the model list was fetched before and the endpoint settings changed since.</summary>
    public bool SettingsChangedSinceLastList
    {
        get { var o = Options(); return _models.SettingsChanged(Key(o)); }
    }

    /// <summary>Identifies the endpoint: its settings change with the base url and the key.</summary>
    private static string Key(ProviderOptions o) => o.Enabled ? o.Fingerprint : "disabled";

    // ================================================================ models

    public async Task<IReadOnlyList<ModelInfo>> ListModelsAsync(bool refresh, CancellationToken ct)
    {
        var o = Options();
        var policy = new ModelListCache.Policy
        {
            Fingerprint = Key(o),
            CanFetch = o.Enabled,
            Ttl = TimeSpan.FromSeconds(Math.Max(0, o.ModelsCacheSeconds)),
            Url = o.Root,
        };
        return await _models.ListAsync(refresh, policy, c => FetchModelsAsync(o, c), ct).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<ModelInfo>> FetchModelsAsync(ProviderOptions o, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(10));
        using var req = new HttpRequestMessage(HttpMethod.Get, o.Root + "/v1/models");
        ApplyHeaders(req, o);
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, cts.Token).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw ProviderErrors.FromHttp(DisplayName, resp, await ProviderErrors.ReadBodySafeAsync(resp, cts.Token).ConfigureAwait(false));
        var json = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var data = root.ValueKind == JsonValueKind.Array ? root : root.Prop("data").ValueKind == JsonValueKind.Array ? root.Prop("data") : root.Prop("models");

        var list = new List<ModelInfo>();
        var images = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var e in data.Items())
            if (MapModel(e, o, images) is { } m) list.Add(m);
        _imageSupport = images;
        return list;
    }

    private ModelInfo? MapModel(JsonElement e, ProviderOptions o, Dictionary<string, bool> images)
    {
        var id = e.Str("id") ?? e.Str("model") ?? e.Str("name");
        if (string.IsNullOrWhiteSpace(id)) return null;
        var ov = o.ModelOverride(id);
        if (ov.Bool("hidden", false)) return null;
        // A model with a non-chat "api" (AiGateway: "systemone" for Kev decision models) can't serve a conversation.
        if (e.Str("api") is { Length: > 0 }) return null;

        var modalities = new List<string>();
        var mod = e.Prop("input_modalities");
        if (mod.ValueKind != JsonValueKind.Array) mod = e.Prop("architecture").Prop("input_modalities");
        foreach (var x in mod.Items())
            if (x.ValueKind == JsonValueKind.String && x.GetString() is { Length: > 0 } s) modalities.Add(s);
        if (modalities.Count > 0) images[id] = modalities.Contains("image");
        else modalities.Add("text");

        ReasoningInfo? reasoning = null;
        var r = e.Prop("reasoning");
        if (r.IsObj())
        {
            reasoning = new ReasoningInfo
            {
                Supported = r.Prop("supported").ValueKind == JsonValueKind.True,
                Default = r.Str("default"),
            };
            foreach (var x in r.Prop("efforts").Items())
                if (x.ValueKind == JsonValueKind.String && x.GetString() is { Length: > 0 } s) reasoning.Efforts.Add(s);
        }

        var status = e.Prop("status");
        var statusValue = status.ValueKind == JsonValueKind.String ? status.GetString() : status.Str("value");

        var extra = new JsonObject();
        if (e.Str("owned_by") is { } owner) extra["owned_by"] = owner;
        if (e.Prop("meta").IsObj()) extra["meta"] = JsonNode.Parse(e.Prop("meta").GetRawText());
        if (e.Prop("created").ValueKind == JsonValueKind.Number) extra["created"] = e.Long("created");

        return new ModelInfo
        {
            Provider = Id,
            Id = id,
            DisplayName = ov.Str("displayName") ?? e.Str("display_name") ?? id,
            ContextWindow = ov.Int("contextWindow") ?? e.IntOrNull("context_window") ?? e.Prop("meta").IntOrNull("n_ctx")
                            ?? e.IntOrNull("context_length") ?? e.IntOrNull("max_model_len"),
            MaxOutputTokens = ov.Int("maxOutputTokens") ?? e.IntOrNull("max_output_tokens"),
            Concurrency = ov.Int("concurrency") ?? e.IntOrNull("concurrency"),
            InputModalities = modalities,
            Reasoning = reasoning,
            Status = string.IsNullOrEmpty(statusValue) ? "available" : statusValue,
            IsLocal = o.IsLocal,
            Extra = extra.Count > 0 ? extra : null,
        };
    }

    // ================================================================ streaming

    public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        // Errors are reported as they are (no retries or workarounds here): the message is only extended with the
        // server's request/response ids, and the request body is saved so the failure can be reproduced.
        var call = new CallInfo();
        await using var e = StreamCoreAsync(request, call, ct).GetAsyncEnumerator(ct);
        while (true)
        {
            ModelStreamEvent current;
            try
            {
                if (!await e.MoveNextAsync().ConfigureAwait(false)) yield break;
                current = e.Current;
            }
            catch (ModelException ex) when (!ct.IsCancellationRequested)
            {
                throw await FailedRequests.DescribeAsync(ex, request, call, Id, _dumpDir, _log, ct).ConfigureAwait(false);
            }
            yield return current;
        }
    }

    private async IAsyncEnumerable<ModelStreamEvent> StreamCoreAsync(ModelRequest request, CallInfo call, [EnumeratorCancellation] CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        var o = Options();
        if (!o.Enabled)
            throw new ModelException($"{DisplayName}: provider is disabled in settings.", false, null, "provider_disabled");

        if (!Uri.TryCreate(o.Root, UriKind.Absolute, out _))
            throw new ModelException($"{DisplayName}: no valid baseUrl configured ('{o.BaseUrl}').", false, null, "invalid_config");

        var mo = o.ForModel(request.Model.Id);
        var allowImages = !_imageSupport.TryGetValue(request.Model.Id, out var img) || img;
        var chat = mo.Transport == OpenAiTransport.Chat;
        var body = chat ? ChatTransport.BuildBody(request, mo, allowImages) : ResponsesTransport.BuildBody(request, mo, allowImages, Id);
        if (request.CaptureDecisionContext)
        {
            var snapshot = ChatTransport.BuildBody(request, mo, allowImages);
            foreach (var key in new[] { "model", "stream", "stream_options", "max_tokens", "temperature" }) snapshot.Remove(key);
            snapshot["reasoning_effort"] = EffortMap.Resolve(request.ReasoningEffort, request.Model.Reasoning) ?? "none";
            request.DecisionContext = snapshot;
        }
        var url = o.Root + (chat ? ChatTransport.Path : ResponsesTransport.Path);
        call.Url = url;
        call.Transport = chat ? "chat" : "responses";
        call.Body = body;
        call.Dump = o.DumpFailedRequests;

        using var httpReq = new HttpRequestMessage(HttpMethod.Post, url);
        httpReq.Content = new ByteArrayContent(J.Utf8(body));
        httpReq.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        httpReq.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        ApplyHeaders(httpReq, o);

        _log.LogDebug("{Provider}: POST {Url} model={Model} messages={Count}", Id, url, request.Model.Id, request.Messages.Count);

        HttpResponseMessage resp;
        try { resp = await _http.SendAsync(httpReq, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not ModelException) { throw Rethrow(ex, ct); }

        using (resp)
        {
            if (resp.Headers.TryGetValues("x-request-id", out var rid)) call.RequestId = rid.FirstOrDefault();
            if (!resp.IsSuccessStatusCode)
            {
                var errBody = await ProviderErrors.ReadBodySafeAsync(resp, ct).ConfigureAwait(false);
                call.ResponseBody = errBody;
                ct.ThrowIfCancellationRequested();
                if ((int)resp.StatusCode == 404) InvalidateModels();
                throw ProviderErrors.FromHttp(DisplayName, resp, errBody);
            }

            var asm = new MessageAssembler();
            IOpenAiStreamParser parser = chat
                ? new ChatStreamParser(asm, DisplayName, mo.ParseThinkTags)
                : new ResponsesStreamParser(asm, DisplayName, mo.ParseThinkTags);

            var mediaType = resp.Content.Headers.ContentType?.MediaType;
            if (mediaType is "application/json")
            {
                string text;
                try { text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false); }
                catch (Exception ex) when (ex is not ModelException) { throw Rethrow(ex, ct); }
                call.ResponseBody = ProviderErrors.Cap(text); // same cap as the non-2xx path
                try { parser.HandleJsonBody(text); }
                finally { call.ResponseId ??= parser.ResponseId; }
            }
            else
            {
                Stream stream;
                try { stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false); }
                catch (Exception ex) when (ex is not ModelException) { throw Rethrow(ex, ct); }

                await foreach (var sse in ProviderErrors.Guard(SseReader.ReadAsync(stream, ct), DisplayName, ct).ConfigureAwait(false))
                {
                    // Both transports report the server's id for the response (Responses: response.id, Chat: the
                    // completion id); a backend with no x-request-id header offers only this (idea-uab5a4).
                    try { parser.Handle(sse); }
                    finally { call.ResponseId ??= parser.ResponseId; }
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

    private Exception Rethrow(Exception ex, CancellationToken ct)
    {
        var t = ProviderErrors.Translate(ex, DisplayName, ct);
        if (ReferenceEquals(t, ex)) ExceptionDispatchInfo.Capture(ex).Throw();
        return t;
    }

    private static void ApplyHeaders(HttpRequestMessage req, ProviderOptions o)
    {
        if (!string.IsNullOrEmpty(o.ApiKey)) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", o.ApiKey);
        foreach (var (k, v) in o.Headers) req.Headers.TryAddWithoutValidation(k, SettingsExtensions.ResolveSecret(v) ?? v);
    }
}
