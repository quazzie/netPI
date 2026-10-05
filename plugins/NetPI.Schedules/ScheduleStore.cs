using System.Globalization;
using System.Text.Json.Nodes;

namespace NetPI.Schedules;

/// <summary>
/// The schedules and their run history in the plugin's own collections. A schedule is one document:
/// <c>{ id, name, prompt, projectId?, agent?, cadence, enabled, nextRunMs?, nextRunAt?, createdAt, updatedAt, lastRunAt?,
/// lastStatus?, lastReason?, lastSessionId?, finishedAt? }</c>; a run is <c>{ scheduleId, atMs, at, status, reason?, sessionId? }</c>.
/// <c>nextRunMs</c> is the index the timer asks, and it is advanced inside the transaction that claims a due run, so
/// two generations of the plugin (a hot reload) or two ticks never start the same run twice.
/// </summary>
internal sealed class ScheduleStore(IPluginData data)
{
    /// <summary>Runs kept per schedule; older ones are dropped as new ones are written.</summary>
    public const int HistoryPerSchedule = 50;

    private readonly IDataCollection _schedules = data.Collection("schedules", new CollectionSpec().Integer("nextRunMs").Text("projectId"));
    private readonly IDataCollection _runs = data.Collection("runs", new CollectionSpec().Text("scheduleId").Integer("atMs"));

    public JsonObject? Get(string id) => _schedules.Get(id);

    public IReadOnlyList<JsonObject> List(string? projectId)
    {
        var q = new DataQuery();
        if (!string.IsNullOrEmpty(projectId)) q.Eq("projectId", projectId);
        return [.. _schedules.Find(q).Select(d => d.Doc).OrderBy(d => d["createdAt"]?.GetValue<string>(), StringComparer.Ordinal)];
    }

    public void Put(JsonObject doc) => _schedules.Put(doc["id"]!.GetValue<string>(), doc);

    /// <summary>
    /// Read, change and write a schedule as one unit (the compare-and-set of the port): a tick's <see cref="ClaimDue"/>
    /// cannot slip between the read and the write, so its claim is never overwritten. <paramref name="change"/> runs
    /// inside the transaction and must not await or call into other plugins.
    /// </summary>
    public JsonObject Change(string id, Action<JsonObject> change) => data.Transaction(() =>
    {
        var doc = _schedules.Get(id) ?? throw new RpcException("not_found", $"Schedule {id} not found");
        change(doc);
        _schedules.Put(id, doc);
        return doc;
    });

    public bool Delete(string id) => data.Transaction(() =>
    {
        _runs.DeleteWhere(new DataQuery().Eq("scheduleId", id));
        return _schedules.Delete(id);
    });

    /// <summary>The soonest run of an enabled schedule, for the timer.</summary>
    public DateTimeOffset? NextDue() =>
        _schedules.Find(new DataQuery().NotNull("nextRunMs").Order("nextRunMs"))
            .Where(d => Enabled(d.Doc)).Select(d => (DateTimeOffset?)DateTimeOffset.FromUnixTimeMilliseconds(d.Doc["nextRunMs"]!.GetValue<long>()))
            .FirstOrDefault();

    /// <summary>
    /// Claim every enabled schedule that is due at <paramref name="now"/>: its next run moves on (a once is finished) in the same
    /// transaction that reads it, and what is returned is the caller's to start or skip. <c>Missed</c>: it was due longer ago than
    /// <paramref name="grace"/> (the host was down), so the caller records it instead of running it.
    /// </summary>
    public List<Claim> ClaimDue(DateTimeOffset now, TimeSpan grace) => data.Transaction(() =>
    {
        var claimed = new List<Claim>();
        foreach (var d in _schedules.Find(new DataQuery().Le("nextRunMs", now.ToUnixTimeMilliseconds())))
        {
            var doc = d.Doc;
            if (!Enabled(doc)) continue;
            var due = DateTimeOffset.FromUnixTimeMilliseconds(doc["nextRunMs"]!.GetValue<long>());
            var cadence = CadenceOf(doc);
            SetNext(doc, cadence.After(now));
            if (cadence.Kind == Cadence.Once)
            {
                doc["enabled"] = false;
                doc["finishedAt"] = Iso(now);
            }
            _schedules.Put(d.Key, doc);
            claimed.Add(new Claim((JsonObject)doc.DeepClone(), due, now - due > grace));
        }
        return claimed;
    });

    /// <summary>What happened to a run: on the schedule (its last*) and as a row of its history.</summary>
    public void Record(string scheduleId, DateTimeOffset at, string status, string? reason, string? sessionId) => data.Transaction(() =>
    {
        if (_schedules.Get(scheduleId) is { } doc)
        {
            doc["lastRunAt"] = Iso(at);
            doc["lastStatus"] = status;
            doc["lastReason"] = reason;
            if (sessionId is not null) doc["lastSessionId"] = sessionId;
            _schedules.Put(scheduleId, doc);
        }
        var ms = at.ToUnixTimeMilliseconds();
        _runs.Put($"{scheduleId}:{ms:D13}:{Ids.New("r")}", new JsonObject
        {
            ["scheduleId"] = scheduleId, ["atMs"] = ms, ["at"] = Iso(at), ["status"] = status, ["reason"] = reason, ["sessionId"] = sessionId,
        });
        foreach (var old in _runs.Find(new DataQuery { Offset = HistoryPerSchedule }.Eq("scheduleId", scheduleId).Order("atMs", descending: true)))
            _runs.Delete(old.Key);
    });

    /// <summary>A schedule's runs (or every schedule's), newest first.</summary>
    public IReadOnlyList<JsonObject> History(string? scheduleId, int limit)
    {
        var q = new DataQuery().Order("atMs", descending: true).Take(limit);
        if (!string.IsNullOrEmpty(scheduleId)) q.Eq("scheduleId", scheduleId);
        return [.. _runs.Find(q).Select(d => d.Doc)];
    }

    /// <summary>The runs a schedule started (status ran) since <paramref name="since"/>: the daily cap counts these.</summary>
    public int StartedSince(string scheduleId, DateTimeOffset since) =>
        _runs.Find(new DataQuery().Eq("scheduleId", scheduleId).Ge("atMs", since.ToUnixTimeMilliseconds()))
            .Count(d => d.Doc["status"]?.GetValue<string>() == ScheduleRunner.Ran);

    public static bool Enabled(JsonObject doc) => doc["enabled"] is JsonValue v && v.TryGetValue<bool>(out var b) && b;

    public static Cadence CadenceOf(JsonObject doc) => Cadence.Parse(doc["cadence"], 1);

    public static void SetNext(JsonObject doc, DateTimeOffset? next)
    {
        doc["nextRunMs"] = next?.ToUnixTimeMilliseconds();
        doc["nextRunAt"] = next is { } n ? Iso(n) : null;
    }

    public static string Iso(DateTimeOffset t) => t.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    internal sealed record Claim(JsonObject Schedule, DateTimeOffset Due, bool Missed);
}
