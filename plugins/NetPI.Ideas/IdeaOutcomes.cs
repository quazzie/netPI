using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace NetPI.Ideas;

/// <summary>
/// What every idea check decided, and what the user did with it: the <c>decisions</c> collection. Until now an answer
/// lived only in the moment (a card, a link, a notice) and the probabilities were thrown away, so whether the measured
/// accuracy held in daily use could not be told. Each row is one decision: the site (<c>attach</c>,
/// <c>link</c>, <c>done</c>, <c>notice</c>, <c>save</c>), its result, the idea it chose (or none), the probabilities
/// or similarity behind it, and a short clip of what was judged. The user's side of the cards lives in
/// <c>resolutions</c>. Kept to the newest <see cref="Keep"/> rows.
/// </summary>
public sealed class IdeaOutcomes(IPluginData data, ILogger? log = null)
{
    public const int Keep = 5000;
    private readonly IDataCollection _rows = data.Collection("decisions", new CollectionSpec().Text("site").Text("at").Text("result"));
    private int _writes;

    /// <summary>Record one decision. Never throws: a record that cannot be written must not fail the check it describes.</summary>
    public void Record(string site, string result, JsonObject? fields = null)
    {
        try
        {
            var row = fields is null ? new JsonObject() : (JsonObject)fields.DeepClone();
            row["site"] = site;
            row["result"] = result;
            row["at"] = DateTimeOffset.UtcNow.ToString("O");
            _rows.Put($"{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid().ToString("N")[..6]}", row);
            if (Interlocked.Increment(ref _writes) % 200 == 0) Prune();
        }
        catch (Exception ex) { log?.LogDebug("Ideas: a decision record was not written: {Message}", ex.Message); }
    }

    /// <summary>The newest rows first, optionally of one site and one result.</summary>
    public List<JsonObject> List(string? site, string? result, int limit)
    {
        var q = new DataQuery().Order("at", true).Take(Math.Clamp(limit, 1, 1000));
        if (site is { Length: > 0 }) q.Eq("site", site);
        if (result is { Length: > 0 }) q.Eq("result", result);
        return _rows.Find(q).Select(d => d.Doc).ToList();
    }

    /// <summary>Counts per site and result: the quick view of how often each check speaks, and what came of it.</summary>
    public JsonObject Summary()
    {
        var counts = new JsonObject();
        foreach (var doc in _rows.Find())
        {
            var site = IdeaOps.Str(doc.Doc["site"]) ?? "?";
            var result = IdeaOps.Str(doc.Doc["result"]) ?? "?";
            if (counts[site] is not JsonObject bySite) counts[site] = bySite = new JsonObject();
            bySite[result] = (bySite[result]?.GetValue<int>() ?? 0) + 1;
        }
        return counts;
    }

    private void Prune()
    {
        var count = _rows.Count();
        if (count <= Keep) return;
        foreach (var old in _rows.Find(new DataQuery().Order("at").Take((int)(count - Keep))))
            _rows.Delete(old.Key);
    }

    /// <summary>The fields of a pick-one answer worth keeping: the winner's probability, the runner-up, "none".</summary>
    internal static JsonObject Pick(IdeaPick? pick, string? ideaId, double threshold, string? text = null)
    {
        var o = new JsonObject { ["ideaId"] = ideaId, ["threshold"] = threshold };
        if (pick is not null)
        {
            o["p"] = Math.Round(pick.P, 4);
            o["runnerUp"] = Math.Round(pick.RunnerUp, 4);
            o["none"] = Math.Round(pick.None, 4);
        }
        if (text is { Length: > 0 }) o["text"] = IdeaOps.Clip(text, 300);
        return o;
    }
}

internal static class IdeaJsonExtensions
{
    /// <summary>Set one more field and hand the object back, so a record reads as one expression.</summary>
    public static JsonObject Also(this JsonObject o, string key, JsonNode? value)
    {
        o[key] = value;
        return o;
    }
}
