namespace NetPI.Host.Storage.Memory;

/// <summary>
/// ASCII-only case folding, the way SQLite's <c>NOCASE</c> and <c>LIKE</c> do it: <c>A</c>-<c>Z</c> fold to <c>a</c>-<c>z</c> and
/// nothing else does. A full Unicode fold would order and match strings differently here than in the SQL provider
/// behind the same port, and the conformance suite runs on both.
/// </summary>
internal static class AsciiCase
{
    public static readonly IComparer<string> Comparer = Comparer<string>.Create(Compare);

    private static char Fold(char c) => c is >= 'A' and <= 'Z' ? (char)(c + 32) : c;

    /// <summary>Ordinal comparison with ASCII letters folded; a null sorts before everything (as SQL does).</summary>
    public static int Compare(string? a, string? b)
    {
        if (ReferenceEquals(a, b)) return 0;
        if (a is null) return -1;
        if (b is null) return 1;
        var n = Math.Min(a.Length, b.Length);
        for (var i = 0; i < n; i++)
        {
            var x = Fold(a[i]);
            var y = Fold(b[i]);
            if (x != y) return x < y ? -1 : 1;
        }
        return a.Length.CompareTo(b.Length);
    }

    /// <summary>
    /// True when <paramref name="needle"/> occurs anywhere in <paramref name="haystack"/>, ASCII case-insensitively.
    /// The <c>%</c>, <c>_</c> and <c>\</c> the session list escapes are literal here: the caller has already turned the
    /// <c>LIKE</c> pattern into a plain substring, because a stored title is a substring match and nothing else.
    /// </summary>
    public static bool Contains(string haystack, string needle)
    {
        if (needle.Length == 0) return true;
        var last = haystack.Length - needle.Length;
        for (var i = 0; i <= last; i++)
        {
            var j = 0;
            for (; j < needle.Length && Fold(haystack[i + j]) == Fold(needle[j]); j++) { }
            if (j == needle.Length) return true;
        }
        return false;
    }
}
