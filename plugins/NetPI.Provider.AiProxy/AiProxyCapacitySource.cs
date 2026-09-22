using System.Text;
using System.Text.Json;
using NetPI.Abstractions;

namespace NetPI.Provider.AiProxy;

/// <summary>
/// astra-2 §5.3: the Follow-AiProxy capacity source (service
/// <c>provider-capacity</c>). METADATA ONLY — a capacity source must never
/// make an inference request. It reports a model's CONFIGURED backend
/// concurrency as a <see cref="ProviderCapacityObservation"/>:
/// <list type="bullet">
///   <item>a fresh, positive <c>TotalConcurrency</c> when a stable configured
///     total is available — from a per-model <c>concurrency</c> field in the
///     <c>/v1/models</c> metadata when present, else from the per-model
///     <c>slots</c> in AiProxy's <c>/aiswitcher/status</c> (a configured value,
///     not a live count);</item>
///   <item><see cref="ProviderCapacityStatus.Unknown"/> when no such total is
///     exposed (the lane scheduler then holds new admission — it never
///     substitutes a number).</item>
/// </list>
/// The per-backend <c>metrics.lanes</c> active-request array is NEVER read:
/// an instantaneous free-slot / active-request report cannot supply netPI's
/// capacity target (astra-2 §3.4, §5.3).
/// </summary>
public sealed class AiProxyCapacitySource : IProviderCapacitySource
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly IPluginLogger _log;

    public AiProxyCapacitySource(HttpClient http, string baseUrl, IPluginLogger log)
    {
        _http = http;
        _baseUrl = baseUrl.TrimEnd('/');
        _log = log;
    }

    public async ValueTask<ProviderCapacityObservation> ObserveAsync(string modelId, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;

        // 1) Explicit per-model configured concurrency from the catalog metadata
        //    (a provider-declared stable total; the /v1/models list — metadata,
        //    no inference). If the model is listed WITH a positive total that
        //    is authoritative; otherwise we fall through to the status route
        //    before concluding Unknown (absence is never a guess).
        try
        {
            using var ct = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            ct.CancelAfter(TimeSpan.FromSeconds(5));
            using var resp = await _http.GetAsync($"{_baseUrl}/v1/models", ct.Token);
            if (resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct.Token);
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                {
                    foreach (var m in data.EnumerateArray())
                    {
                        if (!m.TryGetProperty("id", out var id) || id.GetString() != modelId) continue;
                        var concurrency = GetInt(m, "concurrency");
                        if (concurrency is > 0)
                            return new ProviderCapacityObservation(modelId, concurrency.Value, ProviderCapacityStatus.Fresh, now,
                                "aiProxy /v1/models concurrency", null);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Debug($"capacity: /v1/models unavailable: {ex.Message}");
        }
        // 2) Per-model configured slots from /aiswitcher/status (the status
        //    route is metadata; "slots" is the adapter's configured count —
        //    null when the adapter does not declare one).
        try
        {
            using var ct = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            ct.CancelAfter(TimeSpan.FromSeconds(5));
            using var resp = await _http.GetAsync($"{_baseUrl}/aiswitcher/status", ct.Token);
            if (resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct.Token);
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("backends", out var backends) && backends.ValueKind == JsonValueKind.Array)
                {
                    foreach (var b in backends.EnumerateArray())
                    {
                        if (!b.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array) continue;
                        foreach (var m in models.EnumerateArray())
                        {
                            if (!m.TryGetProperty("id", out var id) || id.GetString() != modelId) continue;
                            var slots = GetInt(m, "slots");
                            if (slots is > 0)
                            {
                                var backend = b.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                                    ? n.GetString() : "?";
                                return new ProviderCapacityObservation(modelId, slots.Value, ProviderCapacityStatus.Fresh, now,
                                    $"aiProxy /aiswitcher/status {backend}", null);
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Debug($"capacity: /aiswitcher/status unavailable: {ex.Message}");
        }

        return new ProviderCapacityObservation(modelId, null, ProviderCapacityStatus.Unknown, now,
            "aiProxy (no configured total exposed)",
            "neither /v1/models concurrency nor a status slots total is available for this model — holding admission");
    }

    private static int? GetInt(JsonElement e, string prop)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetInt32() : null;
}
