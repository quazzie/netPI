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
    private sealed record CacheEntry(IReadOnlyList<ModelInfo> Models, string Fingerprint, DateTimeOffset Expires);

    private readonly HttpClient _http;
    private readonly Func<JsonObject?> _config;
    private readonly ProviderDefaults _defaults;
    private readonly ILogger _log;
    private readonly IEventBus? _events;
    private readonly string? _dumpDir;
    private readonly SemaphoreSlim _listLock = new(1, 1);

    private volatile CacheEntry? _cache;
    private IReadOnlyList<ModelInfo>? _lastGood;
    private string? _lastGoodFingerprint;
    private string? _signature;
    private bool _failureLogged;
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
        _events = events;
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
    public void InvalidateModels() => _cache = null;

    /// <summary>True when the model list was fetched before and the endpoint settings changed since.</summary>
    public bool SettingsChangedSinceLastList
    {
        get { var c = _cache; var o = Options(); return c is not null && c.Fingerprint != (o.Enabled ? o.Fingerprint : "disabled"); }
    }

    // ================================================================ models

    public async Task<IReadOnlyList<ModelInfo>> ListModelsAsync(bool refresh, CancellationToken ct)
    {
        var o = Options();
        var fp = o.Enabled ? o.Fingerprint : "disabled";
        var cache = _cache;
        if (!refresh && cache is not null && cache.Fingerprint == fp && cache.Expires > DateTimeOffset.UtcNow) return cache.Models;

        await _listLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            cache = _cache;
            if (!refresh && cache is not null && cache.Fingerprint == fp && cache.Expires > DateTimeOffset.UtcNow) return cache.Models;

            IReadOnlyList<ModelInfo> models;
            if (!o.Enabled) models = [];
            else
            {
                try
                {
                    models = await FetchModelsAsync(o, ct).ConfigureAwait(false);
                    _lastGood = models;
                    _lastGoodFingerprint = fp;
                    if (_failureLogged) _log.LogInformation("{Provider}: model list available again ({Count} models)", Id, models.Count);
                    _failureLogged = false;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    if (!_failureLogged) _log.LogWarning("{Provider}: cannot list models from {Url}: {Error}", Id, o.Root, ex.Message);
                    _failureLogged = true;
                    models = _lastGood is { } last && _lastGoodFingerprint == fp
                        ? last.Select(m => { var c = CloneModel(m); c.Status = "offline"; return c; }).ToList()
                        : [];
                }
            }

            _cache = new CacheEntry(models, fp, DateTimeOffset.UtcNow.AddSeconds(Math.Max(0, o.ModelsCacheSeconds)));
            PublishIfChanged(models);
            return models;
        }
        finally { _listLock.Release(); }
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

    internal static ModelInfo CloneModel(ModelInfo m) => new()
    {
        Provider = m.Provider, Id = m.Id, DisplayName = m.DisplayName, ContextWindow = m.ContextWindow,
        MaxOutputTokens = m.MaxOutputTokens, Concurrency = m.Concurrency, InputModalities = [.. m.InputModalities],
        Reasoning = m.Reasoning is null ? null : new ReasoningInfo { Supported = m.Reasoning.Supported, Efforts = [.. m.Reasoning.Efforts], Default = m.Reasoning.Default },
        Status = m.Status, IsLocal = m.IsLocal, Extra = m.Extra?.DeepClone() as JsonObject,
    };

    private void PublishIfChanged(IReadOnlyList<ModelInfo> models)
    {
        var sb = new StringBuilder();
        foreach (var m in models)
        {
            sb.Append(m.Id).Append('|').Append(m.Status).Append('|').Append(m.ContextWindow).Append('|').Append(m.MaxOutputTokens)
              .Append('|').Append(m.Concurrency).Append('|').Append(string.Join(',', m.InputModalities)).Append('|')
              .Append(m.Reasoning is null ? "-" : $"{m.Reasoning.Supported}:{string.Join(',', m.Reasoning.Efforts)}:{m.Reasoning.Default}")
              .Append('|').Append(m.DisplayName).Append('\n');
        }
        var sig = sb.ToString();
        if (sig == _signature) return;
        var first = _signature is null;
        _signature = sig;
        if (first && models.Count == 0) return; // nothing -> nothing is not a change
        try { _events?.Publish(EventTypes.ModelsChanged, new JsonObject { ["provider"] = Id }); }
        catch (Exception ex) { _log.LogDebug(ex, "{Provider}: publishing models.changed failed", Id); }
    }

    // ================================================================ streaming

    /// <summary>What one call sent and got back, for error messages and failed-request dumps.</summary>
    private sealed class CallInfo
    {
        public string? Url;
        public string? Transport;
        public JsonObject? Body;
        public string? RequestId;
        /// <summary>The response body as received, capped (16k) by <see cref="ProviderErrors.ReadBodySafeAsync"/>. The
        /// dump used to keep only the 2,000-char excerpt the error message carries, so a cause outside
        /// <c>error.message</c> was unreproducible from the file (idea-022jh1).</summary>
        public string? ResponseBody;
        public IOpenAiStreamParser? Parser;
        public bool Dump;
        /// <summary>Both transports report the server's id for the response (Responses: <c>response.id</c>,
        /// Chat: the completion id); a backend with no <c>x-request-id</c> header offers only this.</summary>
        public string? ResponseId => Parser?.ResponseId;
    }

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
                throw Describe(ex, request, call);
            }
            yield return current;
        }
    }

    private ModelException Describe(ModelException ex, ModelRequest request, CallInfo call)
    {
        var ids = new List<string>();
        if (call.RequestId is { Length: > 0 } rq) ids.Add("request " + rq);
        if (call.ResponseId is { Length: > 0 } rs) ids.Add("response " + rs);
        string? dump = null;
        if (call.Dump && call.Body is not null && ex.ErrorType is not ("network_error" or "provider_disabled" or "invalid_config"))
            dump = DumpFailedRequest(ex, request, call);
        if (dump is not null) ids.Add("saved " + dump);
        if (ids.Count == 0) return ex;
        // The ids stay in the message (the log, diag and a bug report want them) and are handed over as Detail, so the
        // retry notice can show the reason without them (idea-qz1a5z).
        var detail = string.Join(", ", ids);
        return new ModelException($"{ex.Message} [{detail}]", ex.Transient, ex.StatusCode, ex.ErrorType, ex)
        {
            ContextOverflow = ex.ContextOverflow,
            RetryAfter = ex.RetryAfter,
            Detail = detail,
        };
    }

    private string? DumpFailedRequest(ModelException ex, ModelRequest request, CallInfo call)
    {
        if (_dumpDir is null) return null;
        try
        {
            Directory.CreateDirectory(_dumpDir);
            var name = $"{DateTime.Now:yyyyMMdd-HHmmss-fff}-{Safe(request.Model.Id)}.json";
            var path = Path.Combine(_dumpDir, name);
            var doc = new JsonObject
            {
                ["time"] = DateTimeOffset.Now.ToString("O"),
                ["provider"] = Id,
                ["model"] = request.Model.Id,
                ["sessionId"] = request.SessionId,
                ["url"] = call.Url,
                ["transport"] = call.Transport,
                ["requestId"] = call.RequestId,
                ["responseId"] = call.ResponseId,
                ["error"] = new JsonObject { ["message"] = ex.Message, ["type"] = ex.ErrorType, ["status"] = ex.StatusCode },
                ["request"] = call.Body!.DeepClone(),
            };
            if (!string.IsNullOrEmpty(call.ResponseBody)) doc["response"] = call.ResponseBody;
            File.WriteAllText(path, doc.ToJsonString(NetPiJson.Indented));
            // keep the newest 30
            foreach (var old in new DirectoryInfo(_dumpDir).GetFiles("*.json").OrderByDescending(f => f.Name).Skip(30))
                try { old.Delete(); } catch { }
            return path;
        }
        catch (Exception e)
        {
            _log.LogDebug(e, "{Provider}: could not save the failed request", Id);
            return null;
        }

        static string Safe(string s) => string.Concat(s.Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_'));
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
            call.Parser = parser;

            var mediaType = resp.Content.Headers.ContentType?.MediaType;
            if (mediaType is "application/json")
            {
                string text;
                try { text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false); }
                catch (Exception ex) when (ex is not ModelException) { throw Rethrow(ex, ct); }
                call.ResponseBody = ProviderErrors.Cap(text); // same cap as the non-2xx path
                parser.HandleJsonBody(text);
            }
            else
            {
                Stream stream;
                try { stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false); }
                catch (Exception ex) when (ex is not ModelException) { throw Rethrow(ex, ct); }

                await foreach (var sse in ProviderErrors.Guard(SseReader.ReadAsync(stream, ct), DisplayName, ct).ConfigureAwait(false))
                {
                    parser.Handle(sse);
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
        foreach (var (k, v) in o.Headers) req.Headers.TryAddWithoutValidation(k, ProviderOptions.ResolveSecret(v) ?? v);
    }
}
