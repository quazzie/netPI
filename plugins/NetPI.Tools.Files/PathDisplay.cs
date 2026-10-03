namespace NetPI.Tools.Files;

/// <summary>Path formatting helpers shared by the tools.</summary>
public static class PathDisplay
{
    public static readonly StringComparison PathComparison = WorkspacePaths.Comparison;

    /// <summary>Path relative to <paramref name="baseDir"/> with forward slashes, or the absolute path when outside of it.</summary>
    public static string Relative(string baseDir, string fullPath)
    {
        try
        {
            var rel = Path.GetRelativePath(baseDir, fullPath);
            if (rel == ".") return ".";
            if (rel.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(rel)) return fullPath.Replace('\\', '/');
            return rel.Replace('\\', '/');
        }
        catch
        {
            return fullPath.Replace('\\', '/');
        }
    }

    public static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB".Replace(',', '.'),
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB".Replace(',', '.'),
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.##} GB".Replace(',', '.'),
    };

    /// <summary>A short "did you mean" hint for a missing path (same directory, similar names).</summary>
    public static string? Suggest(string missingPath)
    {
        try
        {
            var dir = Path.GetDirectoryName(missingPath);
            var name = Path.GetFileName(missingPath);
            if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(name)) return null;
            if (!Directory.Exists(dir))
                return $"The directory {dir} does not exist either.";
            var candidates = new DirectoryInfo(dir).EnumerateFileSystemInfos("*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 })
                .Take(5000)
                .Select(f => (f.Name, Score: Similarity(name, f.Name)))
                .Where(x => x.Score >= 0.5)
                .OrderByDescending(x => x.Score)
                .Take(3)
                .Select(x => x.Name)
                .ToList();
            return candidates.Count > 0 ? "Did you mean: " + string.Join(", ", candidates.Select(c => Path.Combine(dir, c))) + "?" : null;
        }
        catch
        {
            return null;
        }
    }

    private static double Similarity(string a, string b)
    {
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return 1;
        a = a.ToLowerInvariant();
        b = b.ToLowerInvariant();
        if (Path.GetFileNameWithoutExtension(a) == Path.GetFileNameWithoutExtension(b)) return 0.9;
        var d = Levenshtein(a, b);
        return 1.0 - (double)d / Math.Max(a.Length, b.Length);
    }

    private static int Levenshtein(string a, string b)
    {
        if (a.Length > 200 || b.Length > 200) return Math.Max(a.Length, b.Length);
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) prev[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (var j = 1; j <= b.Length; j++)
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }
}
