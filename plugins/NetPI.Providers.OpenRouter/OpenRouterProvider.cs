using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace NetPI.Providers.OpenRouter;

/// <summary>
/// Models through OpenRouter (<c>https://openrouter.ai/api/v1</c>, API key). The catalog offers the models that support
/// tool calls (narrow it with <c>include</c>); requests use Chat Completions with OpenRouter's unified <c>reasoning</c>
/// object, and each response's <c>reasoning_details</c> are replayed unmodified to the model that produced them.
/// Settings are read on every call. Errors are reported as they are, with the generation id; failed request bodies are
/// saved to <c>logs/failed-requests</c>.
/// </summary>
public sealed class OpenRouterProvider : IModelProvider
{
    public const string NoKeyMessage =
        "OpenRouter API key is not configured. Set providers.openrouter.apiKey in settings (or the OPENROUTER_API_KEY environment variable).";

    private sealed record CacheEntry(IReadOnlyList<ModelInfo> Models, string Fingerprint, DateTimeOffset Expires);

    private readonly HttpClient _http;
    private readonly Func<JsonObject?> _config;
    private readonly ILogger _log;
    private readonly IEventBus? _events;
    private readonly string? _dumpDir;
    private readonly SemaphoreSlim _listLock = new(1, 1);
    private volatile CacheEntry? _cache;
    private IReadOnlyList<ModelInfo>? _lastGood;
    private string? _lastGoodFingerprint;
    private string? _signature;
    private bool _failureLogged;

    public OpenRouterProvider(HttpClient http, Func<JsonObject?> config, ILogger? logger = null, IEventBus? events = null,
        string? logsDir = null, string id = "openrouter")
    {
        Id = id;
        _http = http;
        _config = config;
        _log = logger ?? NullLogger.Instance;
        _events = events;
        _dumpDir = logsDir is null ? null : Path.Combine(logsDir, "failed-requests");
    }

    public string Id { get; }
    public string DisplayName => "OpenRouter";
    public bool IsLocal => false;

    internal OpenRouterOptions Options()
    {
        try { return OpenRouterOptions.Parse(_config()); }
        catch (Exception ex) { _log.LogWarning(ex, "openrouter: could not read settings"); return OpenRouterOptions.Parse(null); }
    }

    private static string CacheKey(OpenRouterOptions o) => !o.Enabled ? "disabled" : o.ApiKey is null ? "nokey" : o.Fingerprint;

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
            // Without a key nothing is offered (the catalog itself is public, but every call needs the key).
            if (!o.Enabled || o.ApiKey is null) models = [];
            else
            {
                try
                {
                    models = await FetchModelsAsync(o, ct).ConfigureAwait(false);
                    _lastGood = models;
                    _lastGoodFingerprint = key;
                    if (_failureLogged) _log.LogInformation("openrouter: model list available again ({Count} models)", models.Count);
                    _failureLogged = false;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    if (!_failureLogged) _log.LogWarning("openrouter: cannot list models from {Url}: {Error}", o.Root, ex.Message);
                    _failureLogged = true;
                    models = _lastGood is { } last && _lastGoodFingerprint == key
                        ? last.Select(m => { var c = Clone(m); c.Status = "offline"; return c; }).ToList()
                        : [];
                    ttl = TimeSpan.FromSeconds(Math.Min(60, o.ModelsCacheSeconds));
                }
            }
            _cache = new CacheEntry(models, key, DateTimeOffset.UtcNow + ttl);
            PublishIfChanged(models);
            return models;
        }
        finally { _listLock.Release(); }
    }

    private async Task<IReadOnlyList<ModelInfo>> FetchModelsAsync(OpenRouterOptions o, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(20));
        using var req = new HttpRequestMessage(HttpMethod.Get, o.Root + "/v1/models");
        ApplyHeaders(req, o);
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, cts.Token).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw ProviderErrors.FromHttp(DisplayName, resp, await ProviderErrors.ReadBodySafeAsync(resp, cts.Token).ConfigureAwait(false));
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false));
        var list = new List<ModelInfo>();
        foreach (var e in doc.RootElement.Prop("data").Items())
            if (MapModel(e, o) is { } m) list.Add(m);
        return list;
    }

    /// <summary>A catalog entry, or null when it is filtered out (no tool calls, not included, hidden).</summary>
    internal ModelInfo? MapModel(JsonElement e, OpenRouterOptions o)
    {
        var id = e.Str("id");
        if (string.IsNullOrWhiteSpace(id)) return null;
        var ov = o.ModelOverride(id);
        if (ov.Bool("hidden", false) || !o.IsIncluded(id)) return null;
        var parameters = e.Prop("supported_parameters").Items().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() : null).ToHashSet();
        var tools = parameters.Contains("tools");
        if (!tools && !o.IsListedExactly(id)) return null; // an agent needs tool calls

        var modalities = e.Prop("architecture").Prop("input_modalities").Items()
            .Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() : null).OfType<string>().ToList();
        if (modalities.Count == 0) modalities.Add("text");

        var extra = new JsonObject { ["tools"] = tools };
        ReasoningInfo reasoning;
        var r = e.Prop("reasoning");
        if (r.IsObj())
        {
            var raw = JsonNode.Parse(r.GetRawText())!.AsObject();
            extra["reasoning"] = raw;
            reasoning = new ReasoningInfo { Supported = true, Default = r.Str("default_effort") };
            var mandatory = r.Prop("mandatory").ValueKind == JsonValueKind.True;
            var efforts = r.Prop("supported_efforts");
            // The picker lists efforts low → high; "none" where reasoning can be turned off.
            if (efforts.ValueKind == JsonValueKind.Array)
                reasoning.Efforts = [.. efforts.Items().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() : null).OfType<string>().OrderBy(Rank)];
            else if (efforts.ValueKind == JsonValueKind.Null)
                reasoning.Efforts = ["minimal", "low", "medium", "high", "xhigh", "max"];
            if (!mandatory && !reasoning.Efforts.Contains("none")) reasoning.Efforts.Insert(0, "none");
            if (mandatory) reasoning.Efforts.Remove("none");
        }
        else reasoning = new ReasoningInfo { Supported = parameters.Contains("reasoning") };

        var pricing = e.Prop("pricing");
        if (pricing.IsObj())
            extra["pricing"] = new JsonObject { ["prompt"] = pricing.Str("prompt"), ["completion"] = pricing.Str("completion") };
        if (e.Prop("created").ValueKind == JsonValueKind.Number) extra["created"] = e.Long("created");

        return new ModelInfo
        {
            Provider = Id,
            Id = id,
            DisplayName = ov.Str("displayName") ?? e.Str("name") ?? id,
            ContextWindow = ov.Int("contextWindow") ?? e.Prop("top_provider").IntOrNull("context_length") ?? e.IntOrNull("context_length"),
            MaxOutputTokens = ov.Int("maxOutputTokens") ?? e.Prop("top_provider").IntOrNull("max_completion_tokens"),
            InputModalities = modalities,
            Reasoning = reasoning,
            Status = "available",
            IsLocal = false,
            Extra = extra,
        };

        static int Rank(string effort) => Array.IndexOf(new[] { "none", "minimal", "low", "medium", "high", "xhigh", "max" }, effort.ToLowerInvariant()) is var i and >= 0 ? i : 99;
    }

    private static ModelInfo Clone(ModelInfo m) => new()
    {
        Provider = m.Provider, Id = m.Id, DisplayName = m.DisplayName, ContextWindow = m.ContextWindow, MaxOutputTokens = m.MaxOutputTokens,
        Concurrency = m.Concurrency, InputModalities = [.. m.InputModalities],
        Reasoning = m.Reasoning is null ? null : new ReasoningInfo { Supported = m.Reasoning.Supported, Efforts = [.. m.Reasoning.Efforts], Default = m.Reasoning.Default },
        Status = m.Status, IsLocal = m.IsLocal, Extra = m.Extra?.DeepClone() as JsonObject,
    };

    private void PublishIfChanged(IReadOnlyList<ModelInfo> models)
    {
        var sb = new StringBuilder();
        foreach (var m in models)
            sb.Append(m.Id).Append('|').Append(m.Status).Append('|').Append(m.ContextWindow).Append('|').Append(m.MaxOutputTokens).Append('|')
              .Append(string.Join(',', m.InputModalities)).Append('|').Append(m.Reasoning is null ? "-" : $"{m.Reasoning.Supported}:{string.Join(',', m.Reasoning.Efforts)}:{m.Reasoning.Default}")
              .Append('|').Append(m.DisplayName).Append('\n');
        var sig = sb.ToString();
        if (sig == _signature) return;
        var first = _signature is null;
        _signature = sig;
        if (first && models.Count == 0) return;
        try { _events?.Publish(EventTypes.ModelsChanged, new JsonObject { ["provider"] = Id }); }
        catch (Exception ex) { _log.LogDebug(ex, "openrouter: publishing models.changed failed"); }
    }

    // ================================================================ streaming

    /// <summary>What one call sent and got back, for error messages and failed-request dumps.</summary>
    private sealed class CallInfo
    {
        public string? Url;
        public JsonObject? Body;
        public string? GenerationId;
        public bool Dump;
    }

    public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        // Errors are reported as they are (no retries or workarounds here): the message is only extended with the
        // generation id, and the request body is saved so the failure can be reproduced.
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
        if (call.GenerationId is { Length: > 0 } gen) ids.Add("generation " + gen);
        if (call.Dump && call.Body is not null && ex.ErrorType is not ("network_error" or "provider_disabled" or "invalid_config" or "authentication_error")
            && DumpFailedRequest(ex, request, call) is { } dump)
            ids.Add("saved " + dump);
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
                ["generationId"] = call.GenerationId,
                ["error"] = new JsonObject { ["message"] = ex.Message, ["type"] = ex.ErrorType, ["status"] = ex.StatusCode },
                ["request"] = call.Body!.DeepClone(),
            };
            File.WriteAllText(path, doc.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            foreach (var old in new DirectoryInfo(_dumpDir).GetFiles("*.json").OrderByDescending(f => f.Name).Skip(30))
                try { old.Delete(); } catch { }
            return path;
        }
        catch (Exception e)
        {
            _log.LogDebug(e, "openrouter: could not save the failed request");
            return null;
        }

        static string Safe(string s) => string.Concat(s.Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_'));
    }

    private async IAsyncEnumerable<ModelStreamEvent> StreamCoreAsync(ModelRequest request, CallInfo call, [EnumeratorCancellation] CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        var o = Options();
        if (!o.Enabled) throw new ModelException("OpenRouter: provider is disabled in settings.", false, null, "provider_disabled");
        if (o.ApiKey is null) throw new ModelException(NoKeyMessage, false, 401, "authentication_error");
        if (!Uri.TryCreate(o.Root, UriKind.Absolute, out _))
            throw new ModelException($"OpenRouter: no valid baseUrl configured ('{o.BaseUrl}').", false, null, "invalid_config");

        var body = OpenRouterChat.BuildBody(request, o, Id);
        var url = o.Root + OpenRouterChat.Path;
        call.Url = url;
        call.Body = body;
        call.Dump = o.DumpFailedRequests;

        using var httpReq = new HttpRequestMessage(HttpMethod.Post, url);
        httpReq.Content = new ByteArrayContent(J.Utf8(body));
        httpReq.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        httpReq.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        ApplyHeaders(httpReq, o);
        _log.LogDebug("openrouter: POST {Url} model={Model} messages={Count}", url, request.Model.Id, request.Messages.Count);

        HttpResponseMessage resp;
        try { resp = await _http.SendAsync(httpReq, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not ModelException) { throw Rethrow(ex, ct); }

        using (resp)
        {
            if (resp.Headers.TryGetValues("x-generation-id", out var gen)) call.GenerationId = gen.FirstOrDefault();
            if (!resp.IsSuccessStatusCode)
            {
                var errBody = await ProviderErrors.ReadBodySafeAsync(resp, ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                if ((int)resp.StatusCode == 404) InvalidateModels();
                throw HttpError(resp, errBody);
            }

            var asm = new MessageAssembler();
            var parser = new OpenRouterStreamParser(asm, DisplayName, o.ParseThinkTags);
            if (resp.Content.Headers.ContentType?.MediaType is "application/json")
            {
                string text;
                try { text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false); }
                catch (Exception ex) when (ex is not ModelException) { throw Rethrow(ex, ct); }
                try { parser.HandleJsonBody(text); }
                finally { call.GenerationId ??= parser.GenerationId; }
            }
            else
            {
                Stream stream;
                try { stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false); }
                catch (Exception ex) when (ex is not ModelException) { throw Rethrow(ex, ct); }

                await foreach (var sse in ProviderErrors.Guard(SseReader.ReadAsync(stream, ct), DisplayName, ct).ConfigureAwait(false))
                {
                    try { parser.Handle(sse); }
                    finally { call.GenerationId ??= parser.GenerationId; }
                    foreach (var ev in asm.Drain()) yield return ev;
                    if (parser.Finished) break;
                }
            }

            parser.Finish();
            foreach (var ev in asm.Drain()) yield return ev;
            var ms = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            var message = asm.Build(Id, request.Model.Id, request.SessionId, ms);
            if (parser.ReasoningDetails.Count > 0)
            {
                var th = message.Parts.OfType<ThinkingPart>().FirstOrDefault();
                if (th is null) message.Parts.Insert(0, th = new ThinkingPart());
                th.ProviderData = new JsonObject { [OpenRouterChat.DetailsKey] = new JsonArray([.. parser.ReasoningDetails.Select(d => (JsonNode)d.DeepClone())]) };
            }
            var meta = new JsonObject();
            if ((call.GenerationId ?? parser.GenerationId) is { } gid) meta["generationId"] = gid;
            if (parser.UpstreamProvider is { } upstream) meta["provider"] = upstream;
            if (parser.Cost is { } cost) meta["cost"] = cost;
            if (meta.Count > 0) message.Meta = new JsonObject { ["openrouter"] = meta };
            yield return new StreamCompleted(message);
        }
    }

    /// <summary>An HTTP error with what OpenRouter adds: the upstream provider (and its raw error) and Retry-After.</summary>
    private ModelException HttpError(HttpResponseMessage resp, string errBody)
    {
        var ex = ProviderErrors.FromHttp(DisplayName, resp, errBody);
        var notes = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(errBody);
            var metadata = doc.RootElement.Prop("error").Prop("metadata");
            if (metadata.Str("provider_name") is { Length: > 0 } who) notes.Add("upstream provider " + who);
            if (metadata.Str("raw") is { Length: > 0 } raw) notes.Add("upstream: " + J.Truncate(raw.Trim(), 400));
        }
        catch (JsonException) { }
        if (ex.RetryAfter is { } wait) notes.Add($"retry after {wait.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)} s");
        if (notes.Count == 0) return ex;
        return new ModelException($"{ex.Message} ({string.Join("; ", notes)})", ex.Transient, ex.StatusCode, ex.ErrorType, ex.InnerException)
        {
            ContextOverflow = ex.ContextOverflow,
            RetryAfter = ex.RetryAfter,
        };
    }

    private static Exception Rethrow(Exception ex, CancellationToken ct)
    {
        var t = ProviderErrors.Translate(ex, "OpenRouter", ct);
        if (ReferenceEquals(t, ex)) ExceptionDispatchInfo.Capture(ex).Throw();
        return t;
    }

    private static void ApplyHeaders(HttpRequestMessage req, OpenRouterOptions o)
    {
        if (!string.IsNullOrEmpty(o.ApiKey)) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", o.ApiKey);
        foreach (var (k, v) in o.Headers) req.Headers.TryAddWithoutValidation(k, OpenRouterOptions.ResolveSecret(v) ?? v);
    }
}
