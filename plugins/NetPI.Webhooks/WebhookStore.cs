using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace NetPI.Webhooks;

/// <summary>
/// The webhooks, their delivery history and their incidents in the plugin's own collections.
/// <para>A webhook: <c>{ id, name, enabled, tokenHash, tokenHint, projectId?, agent?, prompt, alert?, followUp?, title?, match?,
/// group? { key, debounceSeconds, reopenMinutes }, maxChatsPerHour, proposedBy?, createdAt, updatedAt, lastAt?, lastStatus?,
/// lastReason?, lastSessionId? }</c>. A delivery (a row of the history): <c>{ webhookId, atMs, at, status, reason?, sessionId?, key?,
/// summary?, from? }</c>. The bearer token itself is shown once, when it is made; only its SHA-256 is stored, so a leaked database does
/// not leak a working token.</para>
/// <para>An incident is the unit a group key makes: <c>{ webhookId, key, state: collecting|open, startAtMs?, pending: [{ payload, at, from }],
/// retryUntilMs?, sessionId?, openedAt?, lastAtMs, alertCount, lastFingerprint? }</c>. <c>collecting</c> holds the alerts that arrived
/// inside the debounce window (persisted, so a restart does not lose them); <c>open</c> names the chat that handles it, which later alerts
/// with the same key are appended to.</para>
/// </summary>
internal sealed class WebhookStore(IPluginData data)
{
    public const int HistoryPerWebhook = 200;
    /// <summary>Delivery statuses: held (collecting inside the debounce window), started (a chat began), appended (added to the open chat),
    /// duplicate (the same alert was just seen), skipped (filtered, disabled, over the fuse), failed, hook (an action of the webhook's hooks ran, or failed).</summary>
    public const string Held = "held", Started = "started", Appended = "appended", Duplicate = "duplicate", Skipped = "skipped", Failed = "failed", Hook = "hook";
    public const string Collecting = "collecting", Open = "open";

    private readonly IDataCollection _hooks = data.Collection("webhooks", new CollectionSpec().Text("projectId"));
    private readonly IDataCollection _runs = data.Collection("runs", new CollectionSpec().Text("webhookId").Integer("atMs").Text("key"));
    private readonly IDataCollection _incidents = data.Collection("incidents", new CollectionSpec().Text("webhookId").Text("state").Integer("startAtMs").Integer("lastAtMs"));

    public JsonObject? Get(string id) => _hooks.Get(id);

    /// <summary>The webhook a request path names: <c>/hook/&lt;segment&gt;</c> is its id or its slug (the readable name the user chose).</summary>
    public JsonObject? Resolve(string segment) =>
        _hooks.Get(segment) ?? List().FirstOrDefault(h => h["slug"]?.GetValue<string>() is { } s && string.Equals(s, segment, StringComparison.OrdinalIgnoreCase));

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
        _incidents.DeleteWhere(new DataQuery().Eq("webhookId", id));
        return _hooks.Delete(id);
    });

    // ---------------------------------------------------------------- deliveries

    /// <param name="touch">Whether the row also becomes the webhook's last delivery (a hook's action does not: it is not a delivery).</param>
    public void Record(string id, DateTimeOffset at, string status, string? reason, string? sessionId, string? key, string? summary, string? from, bool touch = true)
    {
        data.Transaction(() =>
        {
            if (touch && _hooks.Get(id) is { } doc)
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

    /// <summary>How many chats a webhook started since <paramref name="since"/> (the safety fuse counts these).</summary>
    public int StartedSince(string id, DateTimeOffset since) =>
        _runs.Find(new DataQuery().Eq("webhookId", id).Ge("atMs", since.ToUnixTimeMilliseconds()))
            .Count(d => d.Doc["status"]?.GetValue<string>() == Started);

    // ---------------------------------------------------------------- incidents

    public static string IncidentKey(string webhookId, string groupKey) =>
        webhookId + ":" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(groupKey)))[..16].ToLowerInvariant();

    public JsonObject? GetIncident(string webhookId, string groupKey) => _incidents.Get(IncidentKey(webhookId, groupKey));

    public void PutIncident(JsonObject incident) => _incidents.Put(IncidentKey(incident["webhookId"]!.GetValue<string>(), incident["key"]!.GetValue<string>()), incident);

    public void DeleteIncident(string webhookId, string groupKey) => _incidents.Delete(IncidentKey(webhookId, groupKey));

    /// <summary>Incidents whose debounce window has ended, oldest first.</summary>
    public IReadOnlyList<JsonObject> DueIncidents(DateTimeOffset now) =>
        [.. _incidents.Find(new DataQuery().Eq("state", Collecting).Le("startAtMs", now.ToUnixTimeMilliseconds()).Order("startAtMs"))
            .Select(d => d.Doc)];

    /// <summary>The soonest moment a collecting incident is due, for the loop's sleep.</summary>
    public DateTimeOffset? NextDue() =>
        _incidents.Find(new DataQuery().Eq("state", Collecting).NotNull("startAtMs").Order("startAtMs").Take(1))
            .Select(d => (DateTimeOffset?)DateTimeOffset.FromUnixTimeMilliseconds(d.Doc["startAtMs"]!.GetValue<long>())).FirstOrDefault();

    public IReadOnlyList<JsonObject> Incidents(string? webhookId)
    {
        var q = new DataQuery().Order("lastAtMs", descending: true).Take(200);
        if (!string.IsNullOrEmpty(webhookId)) q.Eq("webhookId", webhookId);
        return [.. _incidents.Find(q).Select(d => d.Doc)];
    }

    /// <summary>Open incidents whose chat is running or has not been judged finished yet: the loop watches these for the finished / asking / stalled hooks.</summary>
    public IReadOnlyList<JsonObject> AwaitingIncidents() =>
        [.. _incidents.Find(new DataQuery().Eq("state", Open).Take(500)).Select(d => d.Doc).Where(d => d["awaiting"] is JsonValue v && v.TryGetValue<bool>(out var b) && b)];

    public bool AnyAwaiting() => AwaitingIncidents().Count > 0;

    /// <summary>Incidents that ended long ago are forgotten, so the collection does not grow without bound.</summary>
    public void Prune(DateTimeOffset olderThan) =>
        _incidents.DeleteWhere(new DataQuery().Eq("state", Open).Lt("lastAtMs", olderThan.ToUnixTimeMilliseconds()));

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
