namespace NetPI.E2E;

public sealed record SelectionResult(List<TestCase> Selected, List<string> Errors);

/// <summary>
/// Which tests a command line asks for, decided before any server or browser starts. A positional filter is an exact test id,
/// else a substring of ids, else (only when no id matches) a substring of names, so the old name filters keep working while
/// <c>errors</c> means the errors.* tests and not every test whose sentence mentions errors; <c>--tag</c> adds every test with a tag;
/// <c>--skip-tag</c> removes. A filter or tag that matches nothing is an error, so a typo can never become a green empty run.
/// </summary>
public static class Selection
{
    public static SelectionResult Resolve(IReadOnlyList<TestCase> all, IReadOnlyList<string> filters, IReadOnlyList<string> tags, IReadOnlyList<string> skipTags)
    {
        var errors = new List<string>();
        var picked = new HashSet<string>(StringComparer.Ordinal);
        var known = all.SelectMany(c => c.Tags).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();

        foreach (var f in filters)
        {
            var hits = all.Where(c => string.Equals(c.Id, f, StringComparison.OrdinalIgnoreCase)).ToList();
            if (hits.Count == 0) hits = all.Where(c => c.Id.Contains(f, StringComparison.OrdinalIgnoreCase)).ToList();
            if (hits.Count == 0) hits = all.Where(c => c.Name.Contains(f, StringComparison.OrdinalIgnoreCase)).ToList();
            if (hits.Count == 0) errors.Add($"'{f}' matches no test (by id or name){Suggest(all, f)}");
            foreach (var h in hits) picked.Add(h.Id);
        }

        foreach (var t in tags)
        {
            var hits = all.Where(c => c.Tags.Contains(t, StringComparer.OrdinalIgnoreCase)).ToList();
            if (hits.Count == 0) errors.Add($"tag '{t}' matches no test. Tags: {string.Join(", ", known)}");
            foreach (var h in hits) picked.Add(h.Id);
        }

        foreach (var t in skipTags)
            if (!known.Contains(t, StringComparer.OrdinalIgnoreCase)) errors.Add($"--skip-tag '{t}' is not a tag. Tags: {string.Join(", ", known)}");

        var selected = filters.Count + tags.Count > 0 ? all.Where(c => picked.Contains(c.Id)).ToList() : all.ToList();
        var before = selected.Count;
        selected = selected.Where(c => !c.Tags.Intersect(skipTags, StringComparer.OrdinalIgnoreCase).Any()).ToList();
        if (errors.Count == 0 && selected.Count == 0)
            errors.Add(before == 0 ? "no tests are registered" : $"every selected test is skipped by --skip-tag {string.Join(",", skipTags)}");
        return new SelectionResult(selected, errors);
    }

    private static string Suggest(IReadOnlyList<TestCase> all, string f)
    {
        var near = all.Select(c => (c.Id, Distance: Distance(f.ToLowerInvariant(), c.Id.ToLowerInvariant()))).OrderBy(x => x.Distance).Take(3).ToList();
        return near.Count == 0 || near[0].Distance > Math.Max(4, f.Length / 2) ? "" : ". Did you mean: " + string.Join(", ", near.Select(x => x.Id));
    }

    private static int Distance(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (var j = 0; j <= b.Length; j++) d[0, j] = j;
        for (var i = 1; i <= a.Length; i++)
            for (var j = 1; j <= b.Length; j++)
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
        return d[a.Length, b.Length];
    }
}
