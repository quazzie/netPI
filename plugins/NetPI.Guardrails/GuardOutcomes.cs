using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace NetPI.Guardrails;

/// <summary>
/// What became of every guard question: the <c>outcomes</c> collection. A row per ask rule that fired: the command (or
/// path), the rule, the second opinion's probabilities when one was asked, and the result — <c>cleared</c> (the second
/// opinion let it run), <c>session</c> (allowed earlier for the chat), <c>allowed</c> / <c>allowed-session</c> /
/// <c>denied</c> / <c>steered</c> / <c>cancelled</c> (the user's answer or what ended the wait), <c>subagent</c> (refused:
/// nobody to ask). The user's answers to commands the model did not clear are the labels the second opinion was never
/// measured on; until now they were gone when the card closed. Kept to the newest <see cref="Keep"/> rows.
/// </summary>
internal sealed class GuardOutcomes(IPluginData data, ILogger log)
{
    public const int Keep = 5000;
    private readonly IDataCollection _rows = data.Collection("outcomes", new CollectionSpec().Text("at").Text("result"));
    private int _writes;

    public void Record(string result, string sessionId, string tool, Verdict verdict, Opinion? opinion)
    {
        try
        {
            var row = new JsonObject
            {
                ["at"] = DateTimeOffset.UtcNow.ToString("O"), ["result"] = result, ["sessionId"] = sessionId, ["tool"] = tool,
                ["kind"] = verdict.Kind, ["rule"] = verdict.Rule,
                ["subject"] = verdict.Subject is { Length: > 600 } s ? s[..600] + "…" : verdict.Subject,
            };
            if (opinion is not null) row["opinion"] = opinion.ToJson();
            _rows.Put($"{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid().ToString("N")[..6]}", row);
            if (Interlocked.Increment(ref _writes) % 200 == 0) Prune();
        }
        catch (Exception ex) { log.LogDebug("Guardrails: an outcome was not recorded: {Message}", ex.Message); }
    }

    public JsonObject Read(string? result, int limit)
    {
        var q = new DataQuery().Order("at", true).Take(Math.Clamp(limit, 1, 1000));
        if (result is { Length: > 0 }) q.Eq("result", result);
        var summary = new JsonObject();
        foreach (var doc in _rows.Find())
            if (doc.Doc["result"]?.GetValue<string>() is { } r) summary[r] = (summary[r]?.GetValue<int>() ?? 0) + 1;
        return new JsonObject { ["summary"] = summary, ["rows"] = new JsonArray(_rows.Find(q).Select(d => (JsonNode)d.Doc).ToArray()) };
    }

    private void Prune()
    {
        var count = _rows.Count();
        if (count <= Keep) return;
        foreach (var old in _rows.Find(new DataQuery().Order("at").Take((int)(count - Keep)))) _rows.Delete(old.Key);
    }
}
