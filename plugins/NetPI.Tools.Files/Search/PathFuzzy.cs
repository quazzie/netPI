namespace NetPI.Tools.Files;

/// <summary>Fuzzy file-name scoring for <c>@</c> mentions (substring &gt; subsequence, file name &gt; directories, shorter paths first).</summary>
public static class PathFuzzy
{
    /// <summary>Score <paramref name="relPath"/> ('/'-separated) against <paramref name="query"/>. Null when it does not match.</summary>
    public static double? Score(string query, string relPath)
    {
        if (string.IsNullOrWhiteSpace(query)) return 0;
        var q = query.Trim().Replace('\\', '/').ToLowerInvariant();
        var path = relPath.ToLowerInvariant();
        var slash = path.LastIndexOf('/', path.Length > 1 && path[^1] == '/' ? path.Length - 2 : path.Length - 1);
        var name = path[(slash + 1)..].TrimEnd('/');
        var depth = path.Count(c => c == '/');
        var lengthPenalty = path.Length * 0.5 + depth * 2;

        // Queries with a '/' are matched against the full path.
        if (!q.Contains('/'))
        {
            var idx = name.IndexOf(q, StringComparison.Ordinal);
            if (idx >= 0)
            {
                var s = 1000.0 - idx * 5 - lengthPenalty;
                if (idx == 0) s += 200;
                var stem = Path.GetFileNameWithoutExtension(name);
                if (name == q || stem == q) s += 400;
                return s;
            }
        }
        var pidx = path.IndexOf(q, StringComparison.Ordinal);
        if (pidx >= 0)
        {
            var s = 700.0 - lengthPenalty;
            if (pidx == 0 || path[pidx - 1] == '/') s += 100;
            if (pidx + q.Length >= slash + 1) s += 100; // reaches into the file name
            return s;
        }

        // Subsequence match, preferring characters in the file name, word boundaries and runs.
        var sub = Subsequence(q, path, slash + 1);
        return sub is null ? null : 300.0 + sub.Value - lengthPenalty;
    }

    private static double? Subsequence(string q, string path, int nameStart)
    {
        // Greedy from the end so the file name is preferred, then a forward pass as fallback.
        var score = 0.0;
        var pi = path.Length - 1;
        var prevMatched = -2;
        for (var qi = q.Length - 1; qi >= 0; qi--)
        {
            var c = q[qi];
            while (pi >= 0 && path[pi] != c) pi--;
            if (pi < 0) return null;
            score += 1;
            if (pi >= nameStart) score += 2;
            if (pi == 0 || path[pi - 1] is '/' or '_' or '-' or '.' or ' ') score += 4;
            if (prevMatched == pi + 1) score += 5;
            prevMatched = pi;
            pi--;
        }
        return score * 4;
    }

    public static List<T> Rank<T>(IEnumerable<T> items, Func<T, string> relPath, string query, int limit)
    {
        var scored = new List<(T Item, double Score, string Rel)>();
        foreach (var item in items)
        {
            var rel = relPath(item);
            var s = Score(query, rel);
            if (s is not null) scored.Add((item, s.Value, rel));
        }
        return scored
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Rel.Length)
            .ThenBy(x => x.Rel, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .Select(x => x.Item)
            .ToList();
    }
}
