namespace NetPI.Providers.Kit;

/// <summary>The reasoning-effort scale none…max and the nearest-effort mapping the providers share.</summary>
internal static class EffortScale
{
    private static readonly string[] Scale = ["none", "minimal", "low", "medium", "high", "xhigh", "max"];

    /// <summary>True when the value is one of the scale's names.</summary>
    public static bool IsKnown(string effort) => Array.IndexOf(Scale, effort.ToLowerInvariant()) >= 0;

    /// <summary>
    /// The listed effort closest to <paramref name="requested"/> (exact match first; ties prefer the higher).
    /// Null when the requested value is not on the scale or no listed effort is.
    /// </summary>
    public static string? Nearest(string requested, IEnumerable<string> efforts)
    {
        var list = efforts as IReadOnlyList<string> ?? efforts.ToList();
        foreach (var e in list)
            if (string.Equals(e, requested, StringComparison.OrdinalIgnoreCase)) return e;
        var ri = Array.IndexOf(Scale, requested.ToLowerInvariant());
        if (ri < 0) return null;
        string? best = null; var bestDist = int.MaxValue; var bestRank = -1;
        foreach (var e in list)
        {
            var ei = Array.IndexOf(Scale, e.ToLowerInvariant());
            if (ei < 0) continue;
            var dist = Math.Abs(ei - ri);
            if (dist < bestDist || (dist == bestDist && ei > bestRank)) { best = e; bestDist = dist; bestRank = ei; }
        }
        return best;
    }
}
