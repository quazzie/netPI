using System.Text;
using System.Text.RegularExpressions;

namespace NetPI.CodeSearch;

/// <summary>
/// Which files of a workspace are searched: <c>git ls-files --cached --others --exclude-standard</c> (tracked files and
/// new ones git does not ignore; nested repositories and checkouts are never entered), else — not a repository, or no
/// git — a walk that respects the <c>.gitignore</c> files on the way and stops at nested repositories. Either way paths
/// are relative to the root, with <c>/</c>, and stay inside it: links are not followed. Generated, vendored and binary
/// files are left out (<see cref="Skip"/>, <see cref="IsBinary"/>).
/// </summary>
internal static class FileSource
{
    /// <summary>Folders of build output and installed dependencies: never searched, tracked or not (a committed <c>dist</c> is a bundle).</summary>
    private static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "node_modules", "bin", "obj", "dist", "__pycache__", ".venv", "venv", ".worktrees",
    };

    /// <summary>More folders the walk without git leaves out, where no <c>.gitignore</c> may say so (git's listing knows better).</summary>
    private static readonly HashSet<string> WalkSkipDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "build", "out", "target", "artifacts", "coverage", "packages",
    };

    private static readonly HashSet<string> BinaryExt = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".ico", ".webp", ".pdf", ".zip", ".gz", ".7z", ".tar", ".rar", ".dll", ".exe",
        ".pdb", ".so", ".dylib", ".bin", ".woff", ".woff2", ".ttf", ".otf", ".eot", ".mp3", ".mp4", ".wav", ".ogg", ".webm",
        ".db", ".sqlite", ".snk", ".pfx", ".nupkg", ".class", ".jar", ".pyc", ".lockb",
    };

    private static readonly HashSet<string> LockFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "package-lock.json", "yarn.lock", "pnpm-lock.yaml", "Cargo.lock", "poetry.lock", "composer.lock", "Gemfile.lock", "go.sum", "packages.lock.json",
    };

    /// <summary>The root's files, relative, at most <paramref name="max"/>; <c>git</c> says whether git listed them.</summary>
    public static async Task<(List<string> Files, bool Git, bool Capped)> ListAsync(string root, int max, CancellationToken ct)
    {
        var git = await GitRunner.RunAsync(root, ct, 0, "ls-files", "-z", "--cached", "--others", "--exclude-standard").ConfigureAwait(false);
        if (git.StartError is null && !git.TimedOut && !git.Aborted && git.ExitCode == 0)
        {
            var files = git.Stdout.Split('\0', StringSplitOptions.RemoveEmptyEntries)
                .Where(f => !f.EndsWith('/') && !Skip(f))
                .Distinct(StringComparer.Ordinal).ToList();
            return (files.Take(max).ToList(), true, files.Count > max);
        }
        ct.ThrowIfCancellationRequested();
        var walked = new List<string>();
        var capped = Walk(root, "", [], walked, max, ct);
        return (walked, false, capped);
    }

    /// <summary>A path never searched: inside a build/vendor folder, a lock file, a minified bundle, a binary by extension.</summary>
    public static bool Skip(string relative)
    {
        var parts = relative.Split('/');
        for (var i = 0; i < parts.Length - 1; i++) if (SkipDirs.Contains(parts[i])) return true;
        var name = parts[^1];
        if (LockFiles.Contains(name)) return true;
        if (name.Contains(".min.", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".map", StringComparison.OrdinalIgnoreCase)) return true;
        return BinaryExt.Contains(Path.GetExtension(name));
    }

    /// <summary>A NUL byte in the first 8 KB.</summary>
    public static bool IsBinary(ReadOnlySpan<byte> bytes) => bytes[..Math.Min(bytes.Length, 8192)].IndexOf((byte)0) >= 0;

    /// <summary>Generated text: a minified bundle (a line over 2,000 characters, or 300 on average).</summary>
    public static bool IsGenerated(string text)
    {
        var lines = 0;
        var longest = 0;
        var at = 0;
        while (at < text.Length)
        {
            var nl = text.IndexOf('\n', at);
            var end = nl < 0 ? text.Length : nl;
            longest = Math.Max(longest, end - at);
            lines++;
            at = end + 1;
        }
        return longest > 2000 || (lines > 0 && text.Length / lines > 300);
    }

    /// <returns>true when the cap was reached.</returns>
    private static bool Walk(string root, string relDir, List<Ignore> rules, List<string> into, int max, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var dir = relDir.Length == 0 ? root : Path.Combine(root, relDir);
        var ignore = Path.Combine(dir, ".gitignore");
        if (File.Exists(ignore))
        {
            rules = [.. rules];
            try { rules.AddRange(Ignore.Parse(File.ReadAllLines(ignore), relDir)); } catch (IOException) { }
        }
        IEnumerable<FileSystemInfo> entries;
        try { entries = new DirectoryInfo(dir).EnumerateFileSystemInfos().OrderBy(e => e.Name, StringComparer.Ordinal).ToList(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
        foreach (var e in entries)
        {
            if (e.LinkTarget is not null) continue; // a link may lead out of the workspace
            var rel = relDir.Length == 0 ? e.Name : relDir + "/" + e.Name;
            var isDir = e is DirectoryInfo;
            if (Ignored(rules, rel, isDir)) continue;
            if (isDir)
            {
                if (SkipDirs.Contains(e.Name) || WalkSkipDirs.Contains(e.Name) || e.Name.StartsWith('.')) continue;
                if (Directory.Exists(Path.Combine(e.FullName, ".git")) || File.Exists(Path.Combine(e.FullName, ".git"))) continue; // another repository or checkout
                if (Walk(root, rel, rules, into, max, ct)) return true;
            }
            else if (!Skip(rel))
            {
                if (into.Count >= max) return true;
                into.Add(rel);
            }
        }
        return false;
    }

    private static bool Ignored(List<Ignore> rules, string rel, bool isDir)
    {
        var ignored = false;
        foreach (var r in rules)
            if ((!r.DirOnly || isDir) && r.Matches(rel)) ignored = !r.Negate;
        return ignored;
    }

    /// <summary>One <c>.gitignore</c> line: <c>*</c>, <c>**</c>, <c>?</c>, a leading or inner <c>/</c> anchors it to its folder, a trailing <c>/</c> means folders only, <c>!</c> negates.</summary>
    internal sealed class Ignore
    {
        private readonly Regex _re;
        public bool Negate { get; }
        public bool DirOnly { get; }

        private Ignore(Regex re, bool negate, bool dirOnly) { _re = re; Negate = negate; DirOnly = dirOnly; }

        public bool Matches(string rel) => _re.IsMatch(rel);

        public static IEnumerable<Ignore> Parse(IEnumerable<string> lines, string baseDir)
        {
            foreach (var raw in lines)
            {
                var line = raw.TrimEnd();
                if (line.Length == 0 || line.StartsWith('#')) continue;
                var negate = line.StartsWith('!');
                if (negate) line = line[1..];
                var dirOnly = line.EndsWith('/');
                var leading = line.StartsWith('/');
                line = line.Trim('/');
                if (line.Length == 0) continue;
                var anchored = leading || line.Contains('/');
                var body = new StringBuilder();
                for (var i = 0; i < line.Length; i++)
                {
                    var c = line[i];
                    if (c == '*' && i + 1 < line.Length && line[i + 1] == '*')
                    {
                        // "**/" any folders (none included), "/**" everything below
                        if (i + 2 < line.Length && line[i + 2] == '/') { body.Append("(?:.*/)?"); i += 2; }
                        else { body.Append(".*"); i++; }
                    }
                    else if (c == '*') body.Append("[^/]*");
                    else if (c == '?') body.Append("[^/]");
                    else body.Append(Regex.Escape(c.ToString()));
                }
                var prefix = baseDir.Length == 0 ? "" : Regex.Escape(baseDir) + "/";
                var pattern = anchored ? $"^{prefix}{body}$" : $"^{prefix}(?:.*/)?{body}$";
                yield return new Ignore(new Regex(pattern, RegexOptions.CultureInvariant | (OperatingSystem.IsWindows() ? RegexOptions.IgnoreCase : 0)), negate, dirOnly);
            }
        }
    }
}
