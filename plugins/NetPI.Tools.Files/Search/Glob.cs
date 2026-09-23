using System.Text;
using System.Text.RegularExpressions;

namespace NetPI.Tools.Files;

/// <summary>
/// Glob patterns over forward-slash relative paths: <c>*</c> (within a segment), <c>**</c> (any number of segments),
/// <c>?</c>, <c>{a,b}</c> (nestable), <c>[abc]</c> / <c>[!a-z]</c>. A pattern without '/' matches the file name at any depth.
/// A leading '!' negates. Case-insensitive on Windows and macOS.
/// </summary>
public sealed class Glob
{
    private static readonly bool DefaultIgnoreCase = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();

    private readonly Regex _regex;

    public string Pattern { get; }
    public bool Negated { get; }
    /// <summary>The pattern has no '/' and is matched against the file name only.</summary>
    public bool NameOnly { get; }

    public Glob(string pattern, bool? ignoreCase = null)
    {
        Pattern = pattern;
        var p = pattern.Trim().Replace('\\', '/');
        if (p.StartsWith('!')) { Negated = true; p = p[1..]; }
        if (p.StartsWith("./")) p = p[2..];
        NameOnly = !p.TrimEnd('/').Contains('/');
        if (p.EndsWith('/')) p += "**";
        var opts = RegexOptions.CultureInvariant | RegexOptions.Singleline;
        if (ignoreCase ?? DefaultIgnoreCase) opts |= RegexOptions.IgnoreCase;
        _regex = new Regex("^" + ToRegex(p) + "$", opts, TimeSpan.FromSeconds(1));
    }

    /// <summary>Match a relative path ('/' or '\' separated). For name-only patterns the last segment is tested.</summary>
    public bool IsMatch(string relPath)
    {
        relPath = relPath.Replace('\\', '/').TrimEnd('/');
        if (relPath.StartsWith("./")) relPath = relPath[2..];
        var subject = NameOnly ? relPath[(relPath.LastIndexOf('/') + 1)..] : relPath;
        try { return _regex.IsMatch(subject); }
        catch (RegexMatchTimeoutException) { return false; }
    }

    public static bool HasGlobChars(string s) => s.IndexOfAny(['*', '?', '[', '{']) >= 0;

    /// <summary>
    /// Split a path that contains glob characters into its literal base directory and the glob part:
    /// <c>src/**/*.cs</c> → (<c>src</c>, <c>**/*.cs</c>).
    /// </summary>
    public static (string Base, string Pattern) SplitBase(string path)
    {
        var p = path.Replace('\\', '/');
        var firstGlob = p.IndexOfAny(['*', '?', '[', '{']);
        if (firstGlob < 0) return (path, "");
        var slash = p.LastIndexOf('/', firstGlob);
        if (slash < 0) return ("", p);
        var basePart = p[..slash];
        if (basePart.Length == 0) basePart = "/";
        else if (basePart.Length == 2 && basePart[1] == ':') basePart += "/";
        return (basePart, p[(slash + 1)..]);
    }

    /// <summary>Convert a glob (already '/'-separated) to a regex body (no anchors).</summary>
    public static string ToRegex(string glob)
    {
        var sb = new StringBuilder();
        var i = 0;
        Convert(glob, ref i, sb, braceDepth: 0);
        return sb.ToString();
    }

    private static void Convert(string g, ref int i, StringBuilder sb, int braceDepth)
    {
        while (i < g.Length)
        {
            var c = g[i];
            switch (c)
            {
                case '*':
                    if (i + 1 < g.Length && g[i + 1] == '*')
                    {
                        var atSegStart = i == 0 || g[i - 1] == '/';
                        var j = i + 2;
                        while (j < g.Length && g[j] == '*') j++;
                        if (atSegStart && j < g.Length && g[j] == '/')
                        {
                            sb.Append("(?:.*/)?"); // "**/" = zero or more directories
                            i = j + 1;
                        }
                        else
                        {
                            sb.Append(".*");
                            i = j;
                        }
                    }
                    else
                    {
                        sb.Append("[^/]*");
                        i++;
                    }
                    break;
                case '?':
                    sb.Append("[^/]");
                    i++;
                    break;
                case '[':
                {
                    var close = FindClassEnd(g, i);
                    if (close < 0) { sb.Append("\\["); i++; break; }
                    var body = g[(i + 1)..close];
                    sb.Append('[');
                    if (body.StartsWith('!') || body.StartsWith('^')) { sb.Append('^'); body = body[1..]; }
                    sb.Append(body.Replace("\\", "\\\\").Replace("[", "\\["));
                    sb.Append(']');
                    i = close + 1;
                    break;
                }
                case '{':
                {
                    if (FindBraceEnd(g, i) < 0) { sb.Append("\\{"); i++; break; }
                    i++;
                    sb.Append("(?:");
                    Convert(g, ref i, sb, braceDepth + 1);
                    sb.Append(')');
                    break;
                }
                case ',' when braceDepth > 0:
                    sb.Append('|');
                    i++;
                    break;
                case '}' when braceDepth > 0:
                    i++;
                    return;
                case '\\' when i + 1 < g.Length:
                    sb.Append(Regex.Escape(g[i + 1].ToString()));
                    i += 2;
                    break;
                default:
                    sb.Append(Regex.Escape(c.ToString()));
                    i++;
                    break;
            }
        }
    }

    private static int FindClassEnd(string g, int open)
    {
        var j = open + 1;
        if (j < g.Length && (g[j] == '!' || g[j] == '^')) j++;
        if (j < g.Length && g[j] == ']') j++;
        while (j < g.Length && g[j] != ']') j++;
        return j < g.Length ? j : -1;
    }

    private static int FindBraceEnd(string g, int open)
    {
        var depth = 0;
        for (var j = open; j < g.Length; j++)
        {
            if (g[j] == '{') depth++;
            else if (g[j] == '}' && --depth == 0) return j;
        }
        return -1;
    }
}

/// <summary>A set of include/exclude globs (e.g. grep's <c>glob</c> argument: "*.cs", "!**/Generated/**").</summary>
public sealed class GlobFilter
{
    private readonly List<Glob> _include = [];
    private readonly List<Glob> _exclude = [];

    public bool IsEmpty => _include.Count == 0 && _exclude.Count == 0;

    public GlobFilter(IEnumerable<string> patterns)
    {
        foreach (var raw in patterns)
        {
            foreach (var p in SplitPatterns(raw))
            {
                var g = new Glob(p);
                (g.Negated ? _exclude : _include).Add(g);
            }
        }
    }

    /// <summary>Split "*.cs *.ts" or "*.cs;*.ts" (but keep "{a,b}" intact). Commas split only outside braces.</summary>
    public static IEnumerable<string> SplitPatterns(string s)
    {
        var sb = new StringBuilder();
        var depth = 0;
        foreach (var c in s)
        {
            if (c == '{') depth++;
            else if (c == '}') depth = Math.Max(0, depth - 1);
            if (depth == 0 && (char.IsWhiteSpace(c) || c == ';' || c == ','))
            {
                if (sb.Length > 0) { yield return sb.ToString(); sb.Clear(); }
                continue;
            }
            sb.Append(c);
        }
        if (sb.Length > 0) yield return sb.ToString();
    }

    public bool IsMatch(string relPath)
    {
        if (_include.Count > 0 && !_include.Any(g => g.IsMatch(relPath))) return false;
        return !_exclude.Any(g => g.IsMatch(relPath));
    }
}
