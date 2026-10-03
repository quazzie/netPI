using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NetPI.Embeddings;

/// <summary>
/// Text embeddings for the other plugins (<see cref="IEmbeddingService"/>): an OpenAI-compatible <c>/v1/embeddings</c>
/// server, the nuc's <c>embed-tasks</c> (bge-base-en-v1.5) in the owner's setup. Off until <c>embed.model</c> is set.
/// Every failure is fast and visible: a short timeout, then a back-off during which calls fail at once, so a consumer
/// falls back to what it did without embeddings instead of waiting on a server that is not there.
/// </summary>
[NetPiPlugin("netpi.embeddings", Name = "Embeddings", Description = "Text embeddings (an OpenAI-compatible /v1/embeddings server) for meaning search in other plugins", Order = 26)]
public sealed class EmbeddingsPlugin : INetPiPlugin
{
    public Task StartAsync(IPluginContext context, CancellationToken ct)
    {
        context.Services.Register(new SettingsSection
        {
            Id = "embed", Title = "Embeddings", Group = "Tools", Order = 46,
            Settings =
            [
                SettingInfo.Str("embed.baseUrl", "Server URL", "", "An OpenAI-compatible /v1/embeddings server. Empty: the AiProxy/AiGateway server (providers.aiproxy.baseUrl)."),
                SettingInfo.Str("embed.model", "Embedding model", "", "Empty switches embeddings off. bge-base-en-v1.5 on the nuc was measured (decisions-lab nuc-plan.md)."),
                SettingInfo.Str("embed.queryPrefix", "Query prefix", "", "Put in front of search texts only (bge: \"Represent this sentence for searching relevant passages: \"); stored documents get none."),
                SettingInfo.Int("embed.timeoutMs", "Timeout (ms)", EmbeddingClient.DefaultTimeoutMs, "How long an interactive call waits. Indexing in the background waits up to 30 s per batch.", 100, 60000),
                SettingInfo.Int("embed.batchSize", "Texts per request", EmbeddingClient.DefaultBatchSize, null, 1, 256),
            ],
        });
        var http = context.Track(new HttpClient { Timeout = Timeout.InfiniteTimeSpan });
        var client = new EmbeddingClient(context.Settings, http);
        context.Services.Register<IEmbeddingService>(client);
        context.Rpc.Register("embed.texts", async (r, rct) =>
        {
            var texts = r.Prop("texts") is { ValueKind: JsonValueKind.Array } arr
                ? arr.EnumerateArray().Select(e => e.GetString() ?? "").ToList()
                : throw new RpcException("bad_request", "Missing array parameter 'texts'");
            var kind = string.Equals(r.Str("kind"), "query", StringComparison.OrdinalIgnoreCase) ? EmbeddingKind.Query : EmbeddingKind.Document;
            try
            {
                var result = await client.EmbedAsync(new EmbeddingRequest { Texts = texts, Kind = kind }, rct).ConfigureAwait(false);
                return new JsonObject
                {
                    ["model"] = result.Model,
                    ["dim"] = result.Dimensions,
                    ["vectors"] = new JsonArray(result.Vectors.Select(v => (JsonNode)new JsonArray(v.Select(x => (JsonNode)JsonValue.Create(x)).ToArray())).ToArray()),
                };
            }
            catch (EmbeddingException ex) { throw new RpcException(ex.Code, ex.Message); }
        }, "Embed texts with the configured model: { texts: string[], kind?: \"document\" | \"query\" } → { model, dim, vectors } (normalized; a query gets embed.queryPrefix)");
        context.Rpc.RegisterReadOnly("embed.status", (_, _) => Task.FromResult<object?>(client.Status()),
            "The embedding client: { model, baseUrl, available, downUntil?, lastError?, calls, failures, p50Ms }");
        return Task.CompletedTask;
    }
}

/// <summary>POST /v1/embeddings in batches; a timeout or failure starts a back-off (5 s, doubling, at most 5 min).</summary>
internal sealed class EmbeddingClient(ISettings settings, HttpClient http, Func<DateTimeOffset>? clock = null) : IEmbeddingService
{
    public const int DefaultTimeoutMs = 1500;
    public const int DefaultBatchSize = 64;
    public static readonly TimeSpan BackgroundTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan FirstBackoff = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(5);

    private readonly Func<DateTimeOffset> _now = clock ?? (() => DateTimeOffset.UtcNow);
    private readonly Lock _gate = new();
    private readonly Queue<double> _ms = new();
    private DateTimeOffset _downUntil = DateTimeOffset.MinValue;
    private int _failuresInRow;
    private long _calls, _failures;
    private string? _lastError;

    public string? Model => settings.Get("embed.model", "") is { Length: > 0 } m && m.Trim().Length > 0 ? m.Trim() : null;

    public bool Available
    {
        get { lock (_gate) return Model is not null && _now() >= _downUntil; }
    }

    public string Root()
    {
        var url = settings.Get("embed.baseUrl", "");
        if (string.IsNullOrWhiteSpace(url)) url = settings.Get("providers.aiproxy.baseUrl", "http://127.0.0.1:8090");
        url = (url ?? "http://127.0.0.1:8090").Trim().TrimEnd('/');
        return url.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ? url[..^3] : url;
    }

    public async Task<EmbeddingResult> EmbedAsync(EmbeddingRequest request, CancellationToken ct)
    {
        var model = Model ?? throw new EmbeddingException("off", "Embeddings are off (embed.model is empty).");
        lock (_gate)
            if (_now() < _downUntil)
                throw new EmbeddingException("unavailable", $"The embedding server is backing off until {_downUntil:HH:mm:ss} after: {_lastError}");
        if (request.Texts.Count == 0) return new EmbeddingResult(model, 0, []);

        var prefix = request.Kind == EmbeddingKind.Query ? settings.Get("embed.queryPrefix", "") ?? "" : "";
        var batch = Math.Clamp(settings.Get("embed.batchSize", DefaultBatchSize), 1, 256);
        var timeout = request.Background ? BackgroundTimeout : TimeSpan.FromMilliseconds(Math.Clamp(settings.Get("embed.timeoutMs", DefaultTimeoutMs), 100, 60000));
        var vectors = new List<float[]>(request.Texts.Count);
        var sw = Stopwatch.StartNew();
        try
        {
            for (var at = 0; at < request.Texts.Count; at += batch)
            {
                var texts = request.Texts.Skip(at).Take(batch).Select(t => prefix + t).ToList();
                vectors.AddRange(await PostAsync(model, texts, timeout, ct).ConfigureAwait(false));
            }
        }
        catch (EmbeddingException ex) when (!ct.IsCancellationRequested)
        {
            Failed(ex.Message);
            throw;
        }
        Succeeded(sw.Elapsed.TotalMilliseconds);
        var dim = vectors[0].Length;
        if (vectors.Any(v => v.Length != dim)) throw new EmbeddingException("bad_response", $"{model} returned vectors of different lengths.");
        return new EmbeddingResult(model, dim, vectors);
    }

    private async Task<List<float[]>> PostAsync(string model, List<string> texts, TimeSpan timeout, CancellationToken ct)
    {
        var body = new JsonObject { ["model"] = model, ["input"] = new JsonArray(texts.Select(t => (JsonNode)t).ToArray()) };
        using var req = new HttpRequestMessage(HttpMethod.Post, Root() + "/v1/embeddings")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, new MediaTypeHeaderValue("application/json")),
        };
        req.Headers.TryAddWithoutValidation("User-Agent", "NetPI-embeddings");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        HttpResponseMessage res;
        try { res = await http.SendAsync(req, cts.Token).ConfigureAwait(false); }
        catch (HttpRequestException ex) { throw new EmbeddingException("unreachable", $"Cannot reach {Root()}: {ex.Message}"); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new EmbeddingException("timeout", $"{model} did not answer within {timeout.TotalMilliseconds:0} ms"); }
        using (res)
        {
            string text;
            try { text = await res.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new EmbeddingException("timeout", $"{model} did not answer within {timeout.TotalMilliseconds:0} ms"); }
            JsonNode? json = null;
            try { json = JsonNode.Parse(text); } catch (JsonException) { }
            if (!res.IsSuccessStatusCode)
            {
                var err = json?["error"] ?? json?["detail"]?["error"];
                var code = (err as JsonObject)?["type"]?.GetValue<string>() ?? (err as JsonObject)?["code"]?.ToString() ?? $"http_{(int)res.StatusCode}";
                var msg = (err as JsonObject)?["message"]?.GetValue<string>() ?? text;
                if (msg.Length > 300) msg = msg[..300] + "…";
                var rid = res.Headers.TryGetValues("X-Request-Id", out var ids) ? ids.FirstOrDefault() : null;
                throw new EmbeddingException(code, $"{model}: HTTP {(int)res.StatusCode} {code}: {msg}{(rid is null ? "" : $" (request {rid})")}");
            }
            if (json?["data"] is not JsonArray data || data.Count != texts.Count)
                throw new EmbeddingException("bad_response", $"{model} returned {(json?["data"] as JsonArray)?.Count ?? 0} vectors for {texts.Count} texts.");
            var vectors = new float[texts.Count][];
            for (var k = 0; k < data.Count; k++)
            {
                var item = data[k] as JsonObject;
                var index = item?["index"] is JsonValue iv && iv.TryGetValue<int>(out var n) ? n : k;
                if (index < 0 || index >= texts.Count || item?["embedding"] is not JsonArray values || values.Count == 0)
                    throw new EmbeddingException("bad_response", $"{model} returned an unreadable vector at {k}.");
                vectors[index] = VectorMath.Normalize(values.Select(x => x!.GetValue<float>()).ToArray());
            }
            return vectors.Any(v => v is null) ? throw new EmbeddingException("bad_response", $"{model} skipped an index.") : vectors.ToList();
        }
    }

    private void Succeeded(double ms)
    {
        lock (_gate)
        {
            _calls++;
            _failuresInRow = 0;
            _ms.Enqueue(ms);
            while (_ms.Count > 50) _ms.Dequeue();
        }
    }

    private void Failed(string error)
    {
        lock (_gate)
        {
            _calls++;
            _failures++;
            _failuresInRow++;
            _lastError = error;
            var wait = TimeSpan.FromTicks(Math.Min(MaxBackoff.Ticks, FirstBackoff.Ticks << Math.Min(_failuresInRow - 1, 16)));
            _downUntil = _now() + wait;
        }
    }

    public JsonObject Status()
    {
        lock (_gate)
        {
            var sorted = _ms.OrderBy(x => x).ToList();
            return new JsonObject
            {
                ["model"] = Model,
                ["baseUrl"] = Root(),
                ["available"] = Model is not null && _now() >= _downUntil,
                ["downUntil"] = _now() < _downUntil ? _downUntil.ToString("O") : null,
                ["lastError"] = _lastError,
                ["calls"] = _calls,
                ["failures"] = _failures,
                ["p50Ms"] = sorted.Count == 0 ? null : Math.Round(sorted[sorted.Count / 2], 1),
            };
        }
    }
}
