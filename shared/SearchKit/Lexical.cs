// Compiled into NetPI.Memory and NetPI.CodeSearch from shared/SearchKit (plugins do not reference each other): edit it here.
namespace NetPI.SearchKit;

/// <summary>
/// The word side of a hybrid search: BM25 over pieces of text, and reciprocal-rank fusion with the meaning ranking.
/// Memory scans the chats' pieces at query time with <see cref="Rank"/> (no inverted index held in memory: a piece that
/// contains none of the query's words is skipped by a cheap substring test); CodeSearch keeps an inverted index of its own
/// and uses <see cref="CodeTokens"/>, <see cref="Bm25"/> and the fusion. Words are runs of letters, digits and
/// underscores, lower-cased; identifiers stay whole.
/// </summary>
internal static class Lexical
{
    private const double K1 = 1.2, B = 0.75;
    /// <summary>The usual reciprocal-rank-fusion constant: a rank's weight is 1 / (60 + rank).</summary>
    public const int RrfK = 60;

    private static readonly HashSet<string> Stop = new(StringComparer.Ordinal)
    {
        "a", "an", "and", "are", "as", "at", "be", "but", "by", "can", "did", "do", "does", "for", "from", "had", "has", "have",
        "how", "i", "if", "in", "into", "is", "it", "its", "me", "my", "no", "not", "of", "on", "or", "our", "so", "that", "the",
        "their", "then", "there", "these", "this", "to", "us", "was", "we", "were", "what", "when", "where", "which", "who",
        "why", "will", "with", "you", "your",
    };

    public sealed record Hit(string Id, double Score, int At, int Words);

    /// <summary>The words of a text, in order, lower-cased.</summary>
    public static IEnumerable<string> Tokens(string text)
    {
        var start = -1;
        for (var i = 0; i <= text.Length; i++)
        {
            var word = i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_');
            if (word && start < 0) start = i;
            else if (!word && start >= 0)
            {
                yield return text[start..i].ToLowerInvariant();
                start = -1;
            }
        }
    }

    public static int Count(string text) => Tokens(text).Count();

    /// <summary>
    /// The words of code: every word whole (<c>codesearchindex</c>, <c>err_quix_4471</c>) and, for a compound identifier,
    /// also its parts (camelCase, PascalCase, snake_case, digits apart: <c>code</c>, <c>search</c>, <c>index</c>), so a query
    /// in plain words finds the identifier and the identifier finds itself.
    /// </summary>
    public static IEnumerable<string> CodeTokens(string text)
    {
        var start = -1;
        for (var i = 0; i <= text.Length; i++)
        {
            var word = i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_');
            if (word && start < 0) start = i;
            else if (!word && start >= 0)
            {
                var parts = Parts(text.AsSpan(start, i - start));
                yield return text[start..i].ToLowerInvariant();
                if (parts.Count > 1)
                    foreach (var p in parts) yield return p;
                start = -1;
            }
        }
    }

    /// <summary>An identifier's parts, lower-cased: split at <c>_</c>, at a lower→upper step, before the last capital of a capital run (<c>HTMLParser</c> → html, parser) and between letters and digits; one-character parts are dropped.</summary>
    private static List<string> Parts(ReadOnlySpan<char> word)
    {
        var parts = new List<string>();
        var from = 0;
        void Cut(ReadOnlySpan<char> w, int to)
        {
            if (to - from > 1) parts.Add(w[from..to].ToString().ToLowerInvariant());
            from = to;
        }
        for (var i = 1; i <= word.Length; i++)
        {
            if (i == word.Length) { Cut(word, i); break; }
            char a = word[i - 1], b = word[i];
            if (b == '_') { Cut(word, i); from = i + 1; continue; }
            if (a == '_') { from = i; continue; }
            var step = (char.IsLower(a) && char.IsUpper(b))
                       || (char.IsUpper(a) && char.IsUpper(b) && i + 1 < word.Length && char.IsLower(word[i + 1]))
                       || (char.IsDigit(a) != char.IsDigit(b));
            if (step) Cut(word, i);
        }
        return parts;
    }

    /// <summary>The query's search words for code: <see cref="CodeTokens"/> without one-letter and common English words.</summary>
    public static List<string> CodeTerms(string query) => CodeTokens(query).Where(t => t.Length > 1 && !Stop.Contains(t)).Distinct().Take(32).ToList();

    /// <summary>One term's BM25 contribution: <paramref name="idf"/> times the saturated term frequency for a piece of <paramref name="length"/> words.</summary>
    public static double Bm25(double idf, int tf, int length, double avgLength) =>
        idf * tf * (K1 + 1) / (tf + K1 * (1 - B + B * length / avgLength));

    /// <summary>The BM25 inverse document frequency of a term that <paramref name="df"/> of <paramref name="pieces"/> pieces hold.</summary>
    public static double Idf(long pieces, int df) => Math.Log(1 + (pieces - df + 0.5) / (df + 0.5));

    /// <summary>The query's search words: distinct, without one-letter words and common English ones.</summary>
    public static List<string> Terms(string query) => Tokens(query).Where(t => t.Length > 1 && !Stop.Contains(t)).Distinct().Take(32).ToList();

    /// <summary>The chats holding any of the terms, best first: BM25 of each chat's best piece.</summary>
    public static List<Hit> Rank(List<string> terms, IEnumerable<(string Id, string[] Texts, int[] Lengths)> chats)
    {
        if (terms.Count == 0) return [];
        long pieces = 0, words = 0;
        var df = new int[terms.Count];
        var found = new List<(string Id, int At, int Length, int[] Tf)>();
        foreach (var (id, texts, lengths) in chats)
            for (var i = 0; i < texts.Length; i++)
            {
                pieces++;
                words += lengths[i];
                var text = texts[i];
                if (!terms.Any(t => text.Contains(t, StringComparison.OrdinalIgnoreCase))) continue;
                var tf = new int[terms.Count];
                foreach (var token in Tokens(text))
                {
                    var k = terms.IndexOf(token);
                    if (k >= 0) tf[k]++;
                }
                if (!tf.Any(n => n > 0)) continue;
                for (var k = 0; k < tf.Length; k++) if (tf[k] > 0) df[k]++;
                found.Add((id, i, lengths[i], tf));
            }
        if (found.Count == 0) return [];
        var avg = Math.Max(1.0, (double)words / Math.Max(1, pieces));
        var idf = df.Select(d => Idf(pieces, d)).ToArray();
        var best = new Dictionary<string, Hit>(StringComparer.Ordinal);
        foreach (var (id, at, length, tf) in found)
        {
            double score = 0;
            for (var k = 0; k < tf.Length; k++)
                if (tf[k] > 0) score += Bm25(idf[k], tf[k], length, avg);
            var hit = new Hit(id, score, at, tf.Count(n => n > 0));
            if (!best.TryGetValue(id, out var have) || score > have.Score) best[id] = hit;
        }
        return best.Values.OrderByDescending(h => h.Score).ThenBy(h => h.Id, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Reciprocal-rank fusion of rankings (each best first): every list adds weight / (60 + rank) to the ids in it. The
    /// word ranking weighs each chat by its BM25 score relative to the best one, so a rare identifier that only one chat
    /// holds outweighs common words that many chats share (plain RRF would count rank 2 of a common word almost as much).
    /// </summary>
    public static List<(string Id, double Score)> Fuse(params IEnumerable<(string Id, double Weight)>[] rankings)
    {
        var score = new Dictionary<string, double>(StringComparer.Ordinal);
        var first = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var ranking in rankings)
        {
            var rank = 0;
            foreach (var (id, weight) in ranking)
            {
                rank++;
                score[id] = score.GetValueOrDefault(id) + weight / (RrfK + rank);
                first[id] = Math.Min(first.GetValueOrDefault(id, int.MaxValue), rank);
            }
        }
        return score.OrderByDescending(x => x.Value).ThenBy(x => first[x.Key]).ThenBy(x => x.Key, StringComparer.Ordinal)
            .Select(x => (x.Key, x.Value)).ToList();
    }

    /// <summary>A meaning ranking for <see cref="Fuse"/>: every rank weighs 1.</summary>
    public static IEnumerable<(string Id, double Weight)> Ranks(IEnumerable<string> ids) => ids.Select(id => (id, 1.0));

    /// <summary>A word ranking for <see cref="Fuse"/>: each chat weighs its score relative to the best.</summary>
    public static IEnumerable<(string Id, double Weight)> Ranks(List<Hit> hits) =>
        hits.Select(h => (h.Id, hits[0].Score > 0 ? h.Score / hits[0].Score : 1.0));

    /// <summary>About <paramref name="size"/> characters of a piece around the first search word in it.</summary>
    public static string Window(string text, List<string> terms, int size)
    {
        var at = terms.Select(t => text.IndexOf(t, StringComparison.OrdinalIgnoreCase)).Where(i => i >= 0).DefaultIfEmpty(0).Min();
        var start = Math.Max(0, Math.Min(at - size / 4, text.Length - size));
        var end = Math.Min(text.Length, start + size);
        return (start > 0 ? "…" : "") + text[start..end] + (end < text.Length ? "…" : "");
    }
}
