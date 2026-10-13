using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace NetPI;

/// <summary>
/// Finding tools by what they do: <c>tool_search</c> (the runtime) and a codemode script's <c>searchTools()</c> rank the same
/// way, so one query finds the same tools in both. Okapi BM25 over a tool's name, label, category, description, help and
/// parameters, after an exact name.
/// </summary>
public static class ToolRanking
{
    /// <summary>
    /// The definitions that match <paramref name="query"/>, best first: a query that is a tool's exact name comes first, the
    /// rest by Okapi BM25 (k1 1.2, b 0.75) over each tool's <see cref="Document"/>. Ties keep name order.
    /// <paramref name="minRelative"/> (0..1) drops the tools that score below that share of the best score, so a tool that
    /// shares one word with the query does not ride along with the real matches (an exact name is always kept).
    /// </summary>
    public static List<ToolDefinition> Rank(IReadOnlyList<ToolDefinition> tools, string query, double minRelative = 0)
    {
        var trimmed = query.Trim();
        var exact = tools.Where(t => string.Equals(t.Name, trimmed, StringComparison.OrdinalIgnoreCase)).ToList();
        var terms = Tokenize(query).Distinct(StringComparer.Ordinal).ToList();
        if (terms.Count == 0) return exact;

        var docs = tools.Select(t => Tokenize(Document(t)).GroupBy(w => w, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal)).ToList();
        var lengths = docs.Select(d => d.Values.Sum()).ToList();
        var average = Math.Max(1.0, lengths.Count == 0 ? 1 : lengths.Average());
        const double k1 = 1.2, b = 0.75;
        var idf = terms.ToDictionary(term => term, term =>
        {
            var df = docs.Count(d => d.ContainsKey(term));
            return Math.Log(1 + (docs.Count - df + 0.5) / (df + 0.5));
        }, StringComparer.Ordinal);

        var scored = new List<(ToolDefinition Tool, double Score)>();
        for (var i = 0; i < tools.Count; i++)
        {
            double score = 0;
            foreach (var term in terms)
            {
                if (!docs[i].TryGetValue(term, out var count)) continue;
                var norm = k1 * (1 - b + b * lengths[i] / average);
                score += idf[term] * count * (k1 + 1) / (count + norm);
            }
            if (score > 0) scored.Add((tools[i], score));
        }
        var best = scored.Count == 0 ? 0 : scored.Max(s => s.Score);
        var ranked = scored.Where(s => s.Score >= best * minRelative)
            .OrderByDescending(s => s.Score).ThenBy(s => s.Tool.Name, StringComparer.Ordinal).Select(s => s.Tool);
        return [.. exact, .. ranked.Where(t => !exact.Contains(t))];
    }

    /// <summary>
    /// What a tool is found by: its name (and with <c>_</c> as spaces), label, category, description and the longer help,
    /// and its parameters' names and descriptions.
    /// </summary>
    public static string Document(ToolDefinition tool)
    {
        var parts = new List<string> { tool.Name, tool.Name.Replace('_', ' '), tool.Label ?? "", tool.Category, tool.Description, tool.Help ?? "" };
        SchemaText(tool.Parameters, parts, 0);
        return string.Join(' ', parts.Where(p => p.Length > 0));
    }

    private static void SchemaText(JsonNode? schema, List<string> parts, int depth)
    {
        if (schema is not JsonObject o || depth > 6) return;
        if (o["description"] is JsonValue d && d.TryGetValue<string>(out var text)) parts.Add(text);
        if (o["properties"] is JsonObject properties)
            foreach (var (name, property) in properties)
            {
                parts.Add(name);
                SchemaText(property, parts, depth + 1);
            }
        SchemaText(o["items"], parts, depth + 1);
        foreach (var key in new[] { "anyOf", "oneOf", "allOf" })
            if (o[key] is JsonArray variants)
                foreach (var variant in variants) SchemaText(variant, parts, depth + 1);
    }

    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "a", "an", "and", "are", "as", "at", "be", "by", "for", "from", "in", "is", "it", "of", "on", "or", "that", "the",
        "this", "to", "with", "tool", "tools",
    };

    /// <summary>Lowercase terms split at camelCase and non-alphanumerics, without stop words, in a naive singular form.</summary>
    public static IEnumerable<string> Tokenize(string text)
    {
        var split = Regex.Replace(text, "([a-z0-9])([A-Z])", "$1 $2");
        split = Regex.Replace(split, "([A-Z]+)([A-Z][a-z])", "$1 $2");
        foreach (Match m in Regex.Matches(split.ToLowerInvariant(), "[a-z0-9]+", RegexOptions.CultureInvariant))
            if (!StopWords.Contains(m.Value)) yield return Stem(m.Value);
    }

    /// <summary>So <c>issues</c> finds <c>issue</c> and <c>searches</c> finds <c>search</c>.</summary>
    private static string Stem(string term)
    {
        if (term.Length > 4 && term.EndsWith("ies", StringComparison.Ordinal)) return term[..^3] + "y";
        if (term.Length > 4 && Regex.IsMatch(term, "(ches|shes|sses|xes|zes)$")) return term[..^2];
        if (term.Length > 3 && term.EndsWith('s') && !term.EndsWith("ss", StringComparison.Ordinal)) return term[..^1];
        return term;
    }
}
