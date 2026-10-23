using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace NetPI.Autonomy;

/// <summary>
/// The plugin's data, in its own collections:
/// <list type="bullet">
/// <item><c>grants</c>: <c>{ id, origin, label, rule, tool, kind, createdAt, createdMs, lastUsedMs?, uses }</c> — an ask rule allowed
/// for good for one origin; keyed by origin and rule, so granting the same rule again is the same grant.</item>
/// <item><c>refusals</c>: <c>{ id, atMs, at, origin, label, sessionId, title, tool, why (ask|tainted), kind?, rule?, subject, grantable,
/// grantedMs? }</c> — what an unattended chat was refused; an ask refusal is grantable from the tab.</item>
/// <item><c>journal</c>: <c>{ id, atMs, at, origin, label, sessionId, title, tool, subject, error, checkpoint?: { id, path, existed,
/// afterHash, undoneAt? } , note? }</c> — every change an unattended chat made.</item>
/// <item><c>checkpoints</c>: <c>{ id, atMs, path, existed, content (base64) }</c> — a file as it was before a change.</item>
/// </list>
/// </summary>
internal sealed class AutonomyStore(IPluginData data, TimeProvider time)
{
    private readonly IDataCollection _grants = data.Collection("grants", new CollectionSpec().Text("origin").Integer("lastUsedMs"));
    private readonly IDataCollection _refusals = data.Collection("refusals", new CollectionSpec().Text("origin").Integer("atMs"));
    private readonly IDataCollection _journal = data.Collection("journal", new CollectionSpec().Text("origin").Text("sessionId").Integer("atMs"));
    private readonly IDataCollection _checkpoints = data.Collection("checkpoints", new CollectionSpec().Integer("atMs"));

    private long Now => time.GetUtcNow().ToUnixTimeMilliseconds();
    private string Iso(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms).ToString("O", CultureInfo.InvariantCulture);

    /// <summary>A key that sorts by time (newest last).</summary>
    private static string NewId(string prefix, long ms) => $"{prefix}-{ms:D13}-{Guid.NewGuid().ToString("N")[..10]}";

    // ---- grants -------------------------------------------------------------------------------------------------

    private static string GrantKey(string origin, string rule) =>
        "g-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(origin + "\n" + rule)))[..20].ToLowerInvariant();

    public JsonObject? FindGrant(string origin, string rule) => _grants.Get(GrantKey(origin, rule));

    public JsonObject Grant(Origin origin, string rule, string tool, string kind) => data.Transaction(() =>
    {
        var key = GrantKey(origin.Id, rule);
        var now = Now;
        var doc = _grants.Get(key) ?? new JsonObject
        {
            ["id"] = key, ["origin"] = origin.Id, ["rule"] = rule, ["tool"] = tool, ["kind"] = kind,
            ["createdMs"] = now, ["createdAt"] = Iso(now), ["uses"] = 0,
        };
        doc["label"] = origin.Label;
        _grants.Put(key, doc);
        // the refusals it answers are granted now
        foreach (var r in _refusals.Find(new DataQuery().Eq("origin", origin.Id)))
            if ((string?)r.Doc["rule"] == rule && r.Doc["grantedMs"] is null)
            {
                r.Doc["grantedMs"] = now;
                _refusals.Put(r.Key, r.Doc);
            }
        return doc;
    });

    public void Used(string grantId) => data.Transaction(() =>
    {
        if (_grants.Get(grantId) is not { } doc) return;
        doc["lastUsedMs"] = Now;
        doc["uses"] = (doc["uses"]?.GetValue<int>() ?? 0) + 1;
        _grants.Put(grantId, doc);
    });

    public bool Revoke(string grantId) => _grants.Delete(grantId);

    public IReadOnlyList<JsonObject> Grants() =>
        [.. _grants.Find().Select(d => d.Doc).OrderByDescending(d => d["lastUsedMs"]?.GetValue<long>() ?? d["createdMs"]?.GetValue<long>() ?? 0)];

    // ---- refusals -----------------------------------------------------------------------------------------------

    public JsonObject Refused(Origin origin, SessionInfo? session, string tool, string why, string subject, string? kind = null, string? rule = null)
    {
        var now = Now;
        var doc = new JsonObject
        {
            ["id"] = NewId("r", now), ["atMs"] = now, ["at"] = Iso(now), ["origin"] = origin.Id, ["label"] = origin.Label,
            ["sessionId"] = session?.Id, ["title"] = session?.Title, ["tool"] = tool, ["why"] = why, ["kind"] = kind, ["rule"] = rule,
            ["subject"] = Clip(subject), ["grantable"] = why == "ask" && rule is not null,
        };
        _refusals.Put(doc["id"]!.GetValue<string>(), doc);
        return doc;
    }

    public JsonObject? Refusal(string id) => _refusals.Get(id);

    public IReadOnlyList<JsonObject> Refusals(int limit) =>
        [.. _refusals.Find(new DataQuery().Order("atMs", descending: true).Take(limit)).Select(d => d.Doc)];

    // ---- journal ------------------------------------------------------------------------------------------------

    public JsonObject Record(Origin origin, SessionInfo? session, string tool, string subject, bool error, JsonObject? checkpoint, string? note)
    {
        var now = Now;
        var doc = new JsonObject
        {
            ["id"] = NewId("j", now), ["atMs"] = now, ["at"] = Iso(now), ["origin"] = origin.Id, ["label"] = origin.Label,
            ["sessionId"] = session?.Id, ["title"] = session?.Title, ["tool"] = tool, ["subject"] = Clip(subject), ["error"] = error,
            ["checkpoint"] = checkpoint, ["note"] = note,
        };
        _journal.Put(doc["id"]!.GetValue<string>(), doc);
        return doc;
    }

    public JsonObject? Row(string id) => _journal.Get(id);

    public void PutRow(JsonObject row) => _journal.Put(row["id"]!.GetValue<string>(), row);

    public IReadOnlyList<JsonObject> Journal(string? origin, string? sessionId, long? beforeMs, int limit)
    {
        var q = new DataQuery().Order("atMs", descending: true).Take(limit);
        if (!string.IsNullOrEmpty(origin)) q.Eq("origin", origin);
        if (!string.IsNullOrEmpty(sessionId)) q.Eq("sessionId", sessionId);
        if (beforeMs is { } b) q.Lt("atMs", b);
        return [.. _journal.Find(q).Select(d => d.Doc)];
    }

    /// <summary>Changes the origin made since <paramref name="sinceMs"/>, for the hourly cap.</summary>
    public long Changes(string origin, long sinceMs) => _journal.Count(new DataQuery().Eq("origin", origin).Ge("atMs", sinceMs));

    public long ChangesSince(long sinceMs) => _journal.Count(new DataQuery().Ge("atMs", sinceMs));

    // ---- checkpoints --------------------------------------------------------------------------------------------

    public string Keep(string path, bool existed, byte[]? content)
    {
        var now = Now;
        var id = NewId("c", now);
        _checkpoints.Put(id, new JsonObject
        {
            ["id"] = id, ["atMs"] = now, ["path"] = path, ["existed"] = existed,
            ["content"] = content is null ? null : Convert.ToBase64String(content),
        });
        return id;
    }

    public JsonObject? Checkpoint(string id) => _checkpoints.Get(id);

    // ---- retention ----------------------------------------------------------------------------------------------

    /// <summary>Drop journal rows, refusals and file copies older than <paramref name="keepDays"/>, and grants unused for <paramref name="grantDays"/>.</summary>
    public (int Rows, int Refusals, int Checkpoints, int Grants) Prune(int keepDays, int grantDays)
    {
        var now = Now;
        var before = now - (long)TimeSpan.FromDays(Math.Max(1, keepDays)).TotalMilliseconds;
        var rows = _journal.DeleteWhere(new DataQuery().Lt("atMs", before));
        var refusals = _refusals.DeleteWhere(new DataQuery().Lt("atMs", before));
        var checkpoints = _checkpoints.DeleteWhere(new DataQuery().Lt("atMs", before));
        var grants = 0;
        if (grantDays > 0)
        {
            var unused = now - (long)TimeSpan.FromDays(grantDays).TotalMilliseconds;
            foreach (var g in _grants.Find())
                if ((g.Doc["lastUsedMs"]?.GetValue<long>() ?? g.Doc["createdMs"]?.GetValue<long>() ?? now) < unused && _grants.Delete(g.Key)) grants++;
        }
        return (rows, refusals, checkpoints, grants);
    }

    private static string Clip(string s) => s.Length <= 400 ? s : s[..400] + "…";
}
