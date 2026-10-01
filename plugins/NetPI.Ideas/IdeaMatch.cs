using System.Text;
using System.Text.Json.Nodes;

namespace NetPI.Ideas;

/// <summary>What a pick-one decision answered: the winning option, how sure it was, and how sure "none" was.</summary>
internal sealed record IdeaPick(int Index, double P, double None)
{
    public double RunnerUp { get; init; }
    public bool Clear(double threshold) => DecisionConfidence.Probability(None) && DecisionConfidence.Probability(RunnerUp) && DecisionConfidence.Clear(P, Math.Max(None, RunnerUp), threshold);
}

/// <summary>
/// The one place where "which of the user's open ideas is this about?" is asked and answered. Recall (while the first
/// message is typed), the check that runs when a tab closes and the one that reads a commit all use it, so the answer's
/// shape is validated once, the "none" option can never be mistaken for an idea, and a backlog larger than one
/// decision's worth of options still has every idea eligible.
/// </summary>
internal static class IdeaMatch
{
    /// <summary>Single-token letters for the options; the last one used is "none".</summary>
    public const string Letters = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";
    /// <summary>How many ideas one decision offers (the letters, minus the one that means "none").</summary>
    public const int MaxOptions = 51; // the letters, minus the one that means "none"
    /// <summary>How many windows of options one match may ask about, so a long backlog stays bounded in time and money.</summary>
    public const int MaxWindows = 3;

    private static readonly HashSet<string> Stop = new(StringComparer.Ordinal)
    {
        "the", "and", "for", "you", "that", "this", "with", "from", "not", "but", "all", "can", "was", "have", "are",
        "was", "were", "will", "would", "should", "could", "about", "into", "than", "then", "them", "they", "what",
        "when", "which", "your", "our", "its", "his", "her", "out", "get", "got", "just", "like", "make", "made",
    };

    /// <summary>The option list ("A) … , none) …"), the letter that means "none", and the labels to ask for.</summary>
    public static string Options(IReadOnlyList<JsonObject> candidates, string noneLabel, out string none, out JsonArray labels)
    {
        var list = new StringBuilder();
        for (var k = 0; k < candidates.Count; k++)
        {
            var idea = candidates[k];
            list.Append(Letters[k]).Append(") [").Append(IdeaOps.ProjectLabel(idea)).Append("] ").Append(IdeaOps.Str(idea["title"]));
            if (OneLine(IdeaOps.Str(idea["summary"])) is { Length: > 0 } summary) list.Append(" — ").Append(Clip(summary, 240));
            list.Append('\n');
        }
        none = Letters[candidates.Count].ToString();
        list.Append(none).Append(") ").Append(noneLabel);
        labels = new JsonArray();
        for (var k = 0; k <= candidates.Count; k++) labels.Add(Letters[k].ToString());
        return list.ToString();
    }

    public static JsonArray Labels(int count)
    {
        var labels = new JsonArray();
        for (var k = 0; k <= count; k++) labels.Add(Letters[k].ToString());
        return labels;
    }

    /// <summary>
    /// The answer, read the way it can be trusted: only the letters we asked about count, the winner is one of the
    /// options (never "none"), and an answer without probabilities for them is no answer at all.
    /// </summary>
    public static IdeaPick? Answer(JsonNode? answer, int options)
    {
        if (answer is not JsonObject o) return null;
        if (o["branches"] is not JsonArray { Count: > 0 } branches || branches[0]?["probabilities"] is not JsonObject probs) return null;
        if (options <= 0) return null;
        double P(string label) => probs[label] is JsonValue v && v.TryGetValue<double>(out var d) && double.IsFinite(d) ? d : 0;
        var best = -1;
        for (var k = 0; k < options; k++)
        {
            if (probs[Letters[k].ToString()] is null) continue; // a letter we never offered says nothing about this answer
            if (best < 0 || P(Letters[k].ToString()) > P(Letters[best].ToString())) best = k;
        }
        if (best < 0) return null;
        return new IdeaPick(best, P(Letters[best].ToString()), P(Letters[options].ToString()))
        { RunnerUp = Enumerable.Range(0, options).Where(k => k != best).Select(k => P(Letters[k].ToString())).DefaultIfEmpty(0).Max() };
    }

    /// <summary>The same reading, for an answer that has already been turned into label → probability.</summary>
    public static IdeaPick? Pick(IReadOnlyDictionary<string, double> probs, IReadOnlyList<string> labels)
    {
        if (labels.Count < 2) return null;
        var best = -1;
        for (var k = 0; k < labels.Count - 1; k++)
        {
            if (!probs.ContainsKey(labels[k])) continue;
            if (best < 0 || probs[labels[k]] > probs[labels[best]]) best = k;
        }
        if (best < 0) return null;
        return new IdeaPick(best, probs[labels[best]], probs.GetValueOrDefault(labels[^1]))
        { RunnerUp = Enumerable.Range(0, labels.Count - 1).Where(k => k != best).Select(k => probs.GetValueOrDefault(labels[k])).DefaultIfEmpty(0).Max() };
    }

    /// <summary>
    /// The candidates a decision may see, most relevant first, in windows of at most <see cref="MaxOptions"/>: with a
    /// backlog of 300 ideas one decision still cannot offer all of them, so the most relevant go first and the next
    /// window is only asked about when the first one found nothing (bounded, so a burst of checks stays cheap).
    /// Backlog order breaks ties, which keeps the result the same for the same backlog and the same text.
    /// </summary>
    public static IEnumerable<List<JsonObject>> Windows(IReadOnlyList<JsonObject> candidates, string? text, int maxWindows = MaxWindows)
    {
        if (candidates.Count <= MaxOptions)
        {
            yield return [.. candidates];
            yield break;
        }
        var ranked = Ranked(candidates, text);
        for (var w = 0; w < maxWindows && w * MaxOptions < ranked.Count; w++)
            yield return ranked.Skip(w * MaxOptions).Take(MaxOptions).ToList();
    }

    /// <summary>The candidates in the order the model should see them: shared words first, then the backlog's own order.</summary>
    public static List<JsonObject> Ranked(IReadOnlyList<JsonObject> candidates, string? text)
    {
        var words = Tokens(text);
        if (words.Count == 0) return [.. candidates];
        return candidates
            .Select((idea, at) => (idea, at, score: Score(words, idea)))
            .OrderByDescending(x => x.score)
            .ThenBy(x => x.at)
            .Select(x => x.idea)
            .ToList();
    }

    /// <summary>How much of the text the idea is about: a word of its title counts double, a tag once.</summary>
    private static double Score(HashSet<string> words, JsonObject idea)
    {
        double score = 0;
        foreach (var word in Tokens(IdeaOps.Str(idea["title"]))) if (words.Contains(word)) score += 2;
        foreach (var word in Tokens(IdeaOps.Str(idea["summary"]))) if (words.Contains(word)) score += 1;
        foreach (var tag in (idea["tags"] as JsonArray ?? []).Select(IdeaOps.Str)) if (tag is { Length: > 0 }) score += words.Contains(tag.ToLowerInvariant()) ? 1 : 0;
        return score;
    }

    private static HashSet<string> Tokens(string? text)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in (text ?? "").Split([' ', '\t', '\r', '\n', '.', ',', ';', ':', '!', '?', '"', '\'', '(', ')', '[', ']', '{', '}', '/', '\\', '`', '*', '#', '-', '_']))
        {
            var word = raw.Trim().TrimStart('`', '*', '#', '_').ToLowerInvariant();
            if (word.Length < 3 || word.Any(c => !char.IsLetterOrDigit(c)) || Stop.Contains(word)) continue;
            set.Add(word);
        }
        return set;
    }

    private static string OneLine(string? s) => string.Join(' ', (s ?? "").Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).Trim();
    private static string Clip(string s, int n) => s.Length > n ? s[..n] + "…" : s;
}
