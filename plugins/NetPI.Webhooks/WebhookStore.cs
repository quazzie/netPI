using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace NetPI.Webhooks;

/// <summary>
/// The webhooks and their run history in the plugin's own collections. A webhook is one document:
/// <c>{ id, name, enabled, tokenHash, tokenHint, projectId?, agent?, prompt, title?, match?, dedupe?, maxPerHour, maxConcurrent,
/// proposedBy?, createdAt, updatedAt, lastAt?, lastStatus?, lastReason?, lastSessionId? }</c>; a run is
/// <c>{ webhookId, atMs, at, status, reason?, sessionId?, key?, summary?, from? }</c>. The bearer token itself is shown once, when it
/// is made; only its SHA-256 is stored, so a leaked settings database does not leak a working token.
/// </summary>
internal sealed class WebhookStore(IPluginData data)
{
    public const int HistoryPerWebhook = 100;
    public const string Started = "started", Skipped = "skipped", Failed = "failed";

    private readonly IDataCollection _hooks = data.Collection("webhooks", new CollectionSpec().Text("projectId"));
    private readonly IDataCollection _runs = data.Collection("runs", new CollectionSpec().Text("webhookId").Integer("atMs").Text("key"));

    public JsonObject? Get(string id) => _hooks.Get(id);

    public IReadOnlyList<JsonObject> List(string? projectId = null)
    {
        var q = new DataQuery();
        if (!string.IsNullOrEmpty(projectId)) q.Eq("projectId", projectId);
        return [.. _hooks.Find(q).Select(d => d.Doc).OrderBy(d => d["createdAt"]?.GetValue<string>(), StringComparer.Ordinal)];
    }

    public void Put(JsonObject doc) => _hooks.Put(doc["id"]!.GetValue<string>(), doc);

    public JsonObject Change(string id, Action<JsonObject> change) => data.Transaction(() =>
    {
        var doc = _hooks.Get(id) ?? throw new RpcException("not_found", $"Webhook {id} not found");
        change(doc);
        _hooks.Put(id, doc);
        return doc;
    });

    public bool Delete(string id) => data.Transaction(() =>
    {
        _runs.DeleteWhere(new DataQuery().Eq("webhookId", id));
        return _hooks.Delete(id);
    });

    public void Record(string id, DateTimeOffset at, string status, string? reason, string? sessionId, string? key, string? summary, string? from)
    {
        data.Transaction(() =>
        {
            if (_hooks.Get(id) is { } doc)
            {
                doc["lastAt"] = Iso(at);
                doc["lastStatus"] = status;
                doc["lastReason"] = reason;
                if (sessionId is not null) doc["lastSessionId"] = sessionId;
                _hooks.Put(id, doc);
            }
            var ms = at.ToUnixTimeMilliseconds();
            _runs.Put($"{id}:{ms:D13}:{Ids.New("r")}", new JsonObject
            {
                ["webhookId"] = id, ["atMs"] = ms, ["at"] = Iso(at), ["status"] = status, ["reason"] = reason,
                ["sessionId"] = sessionId, ["key"] = key, ["summary"] = summary, ["from"] = from,
            });
            foreach (var old in _runs.Find(new DataQuery { Offset = HistoryPerWebhook }.Eq("webhookId", id).Order("atMs", descending: true)))
                _runs.Delete(old.Key);
        });
    }

    public IReadOnlyList<JsonObject> History(string? id, int limit)
    {
        var q = new DataQuery().Order("atMs", descending: true).Take(limit);
        if (!string.IsNullOrEmpty(id)) q.Eq("webhookId", id);
        return [.. _runs.Find(q).Select(d => d.Doc)];
    }

    /// <summary>The runs of a webhook that started a chat since <paramref name="since"/>.</summary>
    public IReadOnlyList<JsonObject> StartedSince(string id, DateTimeOffset since) =>
        [.. _runs.Find(new DataQuery().Eq("webhookId", id).Ge("atMs", since.ToUnixTimeMilliseconds()))
            .Select(d => d.Doc).Where(d => d["status"]?.GetValue<string>() == Started)];

    /// <summary>The newest started run with this dedupe key, or null.</summary>
    public JsonObject? LastStartedWithKey(string id, string key) =>
        _runs.Find(new DataQuery().Eq("webhookId", id).Eq("key", key).Order("atMs", descending: true).Take(20))
            .Select(d => d.Doc).FirstOrDefault(d => d["status"]?.GetValue<string>() == Started);

    // ---------------------------------------------------------------- tokens

    /// <summary>A new bearer token: <c>whk_</c> + 32 random bytes, base64url.</summary>
    public static string NewToken() => "whk_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    public static bool TokenMatches(JsonObject doc, string presented)
    {
        var stored = doc["tokenHash"]?.GetValue<string>();
        if (string.IsNullOrEmpty(stored) || string.IsNullOrEmpty(presented)) return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(stored), Encoding.ASCII.GetBytes(Hash(presented)));
    }

    public static bool Enabled(JsonObject doc) => doc["enabled"] is JsonValue v && v.TryGetValue<bool>(out var b) && b;

    public static string Iso(DateTimeOffset t) => t.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}
