using System.Text;
using System.Text.RegularExpressions;

namespace NetPI.Tools.Files;

/// <summary>
/// The rules of one .gitignore (or .ignore) file. Supports comments, <c>!</c> negation, trailing-slash
/// directory-only rules, anchored (<c>/foo</c>, <c>a/b</c>) vs. basename rules, <c>*</c>, <c>**</c>, <c>?</c> and <c>[...]</c>.
/// </summary>
public sealed class IgnoreFile
{
    private readonly record struct Rule(Regex Regex, bool Negate, bool DirOnly);

    private readonly List<Rule> _rules = [];

    /// <summary>Directory the patterns are relative to (full path).</summary>
    public string BaseDir { get; }
    public int Count => _rules.Count;

    public IgnoreFile(string baseDir, IEnumerable<string> lines)
    {
        BaseDir = baseDir;
        var opts = RegexOptions.CultureInvariant | RegexOptions.Singleline;
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()) opts |= RegexOptions.IgnoreCase;
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0 || line.StartsWith('#')) continue;
            // Trailing spaces are ignored unless escaped.
            var end = line.Length;
            while (end > 0 && line[end - 1] == ' ' && !(end > 1 && line[end - 2] == '\\')) end--;
            line = line[..end];
            if (line.Length == 0) continue;

            var negate = false;
            if (line.StartsWith('!')) { negate = true; line = line[1..]; }
            else if (line.StartsWith("\\!") || line.StartsWith("\\#")) line = line[1..];
            var dirOnly = line.EndsWith('/');
            line = line.TrimEnd('/');
            if (line.Length == 0) continue;
            var anchored = line.Contains('/');
            line = line.TrimStart('/');
            if (line.Length == 0) continue;

            var body = Glob.ToRegex(EscapeBraces(line));
            var pattern = anchored || line.StartsWith("**") ? "^" + body + "$" : "^(?:.*/)?" + body + "$";
            try
            {
                _rules.Add(new Rule(new Regex(pattern, opts, TimeSpan.FromMilliseconds(500)), negate, dirOnly));
            }
            catch (ArgumentException) { /* invalid pattern: skip like git does */ }
        }
    }

    // Braces are literal in gitignore.
    private static string EscapeBraces(string s) => s.Replace("{", "\\{").Replace("}", "\\}");

    public static IgnoreFile? TryLoad(string dir, string fileName)
    {
        var path = Path.Combine(dir, fileName);
        try
        {
            if (!File.Exists(path)) return null;
            var f = new IgnoreFile(dir, File.ReadAllLines(path, Encoding.UTF8));
            return f.Count > 0 ? f : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Last matching rule decides: true = ignored, false = re-included, null = no rule matched.</summary>
    public bool? Match(string fullPath, bool isDir)
    {
        var rel = Path.GetRelativePath(BaseDir, fullPath).Replace('\\', '/');
        if (rel.StartsWith("..", StringComparison.Ordinal) || rel == ".") return null;
        bool? result = null;
        foreach (var r in _rules)
        {
            if (r.DirOnly && !isDir) continue;
            bool hit;
            try { hit = r.Regex.IsMatch(rel); }
            catch (RegexMatchTimeoutException) { continue; }
            if (hit) result = !r.Negate;
        }
        return result;
    }
}

/// <summary>
/// Ignore evaluation for a directory walk: built-in skipped directories plus a stack of .gitignore/.ignore files
/// (from the enclosing git repository root down to the current directory).
/// </summary>
public sealed class IgnoreStack
{
    /// <summary>Directories that are never descended into (unless explicitly targeted).</summary>
    public static readonly HashSet<string> DefaultSkipDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "node_modules", "bin", "obj", ".vs", ".idea", "dist", "build", "artifacts", "__pycache__", ".venv",
        ".hg", ".svn",
    };

    private static readonly string[] IgnoreFileNames = [".gitignore", ".ignore"];

    private readonly List<IgnoreFile> _files;

    private IgnoreStack(List<IgnoreFile> files) => _files = files;

    public static IgnoreStack Empty => new([]);

    /// <summary>Stack for a walk rooted at <paramref name="root"/>: ignore files of the git repository root and every directory between it and root.</summary>
    public static IgnoreStack ForRoot(string root)
    {
        var files = new List<IgnoreFile>();
        try
        {
            var chain = new List<string>();
            var dir = new DirectoryInfo(Path.GetFullPath(root));
            var cur = dir;
            string? repoRoot = null;
            for (var depth = 0; cur is not null && depth < 64; depth++, cur = cur.Parent)
            {
                chain.Add(cur.FullName);
                if (Directory.Exists(Path.Combine(cur.FullName, ".git")) || File.Exists(Path.Combine(cur.FullName, ".git")))
                {
                    repoRoot = cur.FullName;
                    break;
                }
            }
            // Without a repository only the root's own ignore files apply (to the walk they are loaded on entry).
            if (repoRoot is not null)
            {
                chain.Reverse();
                foreach (var d in chain.Take(chain.Count - 1)) // root itself is loaded by Enter()
                    foreach (var name in IgnoreFileNames)
                        if (IgnoreFile.TryLoad(d, name) is { } f) files.Add(f);
            }
        }
        catch { }
        return new IgnoreStack(files);
    }

    /// <summary>A new stack including the ignore files found in <paramref name="dir"/> (the receiver is unchanged).</summary>
    public IgnoreStack Enter(string dir)
    {
        List<IgnoreFile>? added = null;
        foreach (var name in IgnoreFileNames)
        {
            if (IgnoreFile.TryLoad(dir, name) is { } f)
                (added ??= []).Add(f);
        }
        if (added is null) return this;
        var list = new List<IgnoreFile>(_files.Count + added.Count);
        list.AddRange(_files);
        list.AddRange(added);
        return new IgnoreStack(list);
    }

    public bool IsIgnored(string fullPath, bool isDir)
    {
        bool? result = null;
        foreach (var f in _files)
        {
            var m = f.Match(fullPath, isDir);
            if (m.HasValue) result = m;
        }
        return result == true;
    }
}
