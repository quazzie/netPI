using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace NetPI.Tools.Shell;

/// <summary>
/// What a shell command changed in the git repository it ran in. Agents change files through the shell far more than
/// through <c>edit</c> (Python and Node scripts, <c>sed -i</c>, redirects), and none of that reached the chat: no diff,
/// and a script whose <c>replace()</c> matched nothing exits 0 like one that worked. Before and after a foreground
/// command that may write, this takes <c>git status</c> plus the size and modification time of every dirty path; the
/// difference is what changed while the command ran. It sees what git sees (ignored files, other repositories and
/// changes another process made at the same time are outside it or attributed to the command), so it is a report, not
/// a guard. <c>shell.trackChanges</c> switches it off.
/// </summary>
internal static partial class ChangeTracker
{
    /// <summary>How long one snapshot may take; a slower repository is simply not tracked.</summary>
    public static TimeSpan Budget { get; set; } = TimeSpan.FromSeconds(3);
    /// <summary>Changed files named in the model-facing line; the rest are counted.</summary>
    public const int MaxListed = 20;
    /// <summary>A repository with more dirty paths than this is not fingerprinted (each one is a stat).</summary>
    public const int MaxDirty = 5000;
    private const int StatusMaxChars = 4 * 1024 * 1024;
    private const int DiffMaxChars = 60_000;

    /// <summary>A dirty path's status and fingerprint (size and modification time; -1 when the file is gone).</summary>
    public readonly record struct Entry(char Kind, long Size, long Ticks);

    public sealed record Snapshot(string Root, string? Head, Dictionary<string, Entry> Dirty);

    /// <summary>A path the command changed, relative to the repository root with forward slashes.</summary>
    public sealed record Change(string Path, string Kind, bool CleanBefore);

    /// <summary>What the command changed, ready for the result.</summary>
    public sealed record Report(string Root, List<Change> Changes, bool HeadMoved, string? Diff);

    private static readonly ConcurrentDictionary<string, string?> Roots = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    // ------------------------------------------------------------------ which commands, which directory

    private static readonly HashSet<string> ReadOnlyCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "cd", "ls", "dir", "cat", "head", "tail", "grep", "egrep", "fgrep", "rg", "wc", "pwd", "echo", "printf", "which",
        "where", "type", "file", "stat", "du", "df", "sort", "uniq", "cut", "tr", "diff", "cmp", "date", "env", "printenv",
        "true", "false", "test", "[", "basename", "dirname", "realpath", "readlink", "nl", "od", "xxd", "hexdump", "jq",
        "tree", "less", "more", "column", "seq", "sleep", "export", "set", "hostname", "whoami", "uname", "nproc", "id",
        "Get-ChildItem", "gci", "Get-Content", "gc", "Select-String", "sls", "Get-Item", "gi", "Test-Path", "Resolve-Path",
        "Get-Process", "gps", "Get-Location", "gl", "Write-Output", "Write-Host", "Measure-Object", "measure",
        "Select-Object", "select", "Where-Object", "where", "Sort-Object", "sort", "Format-Table", "ft", "Format-List", "fl",
        "Get-Date", "Get-Command", "gcm", "Get-Service", "Get-CimInstance", "Get-ItemProperty", "Get-FileHash", "Set-Location", "sl",
    };

    private static readonly HashSet<string> ReadOnlyGit = new(StringComparer.OrdinalIgnoreCase)
    {
        "status", "log", "diff", "show", "rev-parse", "ls-files", "ls-tree", "blame", "grep", "describe", "shortlog",
        "cat-file", "merge-base", "reflog", "rev-list", "for-each-ref", "name-rev", "check-ignore", "var", "version", "help",
    };

    /// <summary>
    /// Whether the command can change files: false only when every part of it is a command known to read (ls, cat, grep,
    /// git log …, Get-ChildItem …) and nothing is redirected into a file. Anything unknown may write, so a doubt costs a
    /// snapshot, never a missed report.
    /// </summary>
    public static bool MayWrite(string command)
    {
        foreach (Match m in Redirect().Matches(command))
        {
            var target = m.Groups["to"].Value.Trim('"', '\'');
            if (target is "/dev/null" or "$null" or "nul" or "NUL" || target.StartsWith('&')) continue;
            return true;
        }
        foreach (var raw in Segments().Split(command))
        {
            var words = raw.Trim().Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            var i = 0;
            while (i < words.Length && (words[i].Contains('=') && !words[i].StartsWith('-') || words[i] is "sudo" or "time" or "command" or "builtin" or "then" or "do" or "!" or "{" or "(")) i++;
            if (i >= words.Length) continue;
            var first = words[i].TrimStart('(').Trim('"', '\'');
            var name = Path.GetFileNameWithoutExtension(first.Replace('\\', '/').Split('/')[^1]);
            if (name.Equals("git", StringComparison.OrdinalIgnoreCase))
            {
                var sub = words.Skip(i + 1).FirstOrDefault(w => !w.StartsWith('-') && !w.Contains('/') && !w.Contains('\\') && !w.Contains(':'));
                if (sub is not null && ReadOnlyGit.Contains(sub)) continue;
                if (sub is "branch" or "remote" or "worktree" or "stash" or "tag" or "config" && words.Skip(i + 2).Any(w => w is "list" or "-v" or "-vv" or "--list" or "-l" or "--get" or "--show-current" || w.StartsWith("--get"))) continue;
                return true;
            }
            if (name.Equals("sed", StringComparison.OrdinalIgnoreCase))
            {
                if (words.Skip(i + 1).Any(w => w.StartsWith("--in-place") || (w.StartsWith('-') && !w.StartsWith("--") && w.Contains('i')))) return true;
                continue;
            }
            if (name.Equals("find", StringComparison.OrdinalIgnoreCase))
            {
                if (words.Any(w => w is "-delete" or "-exec" or "-execdir" or "-ok" or "-fprint" or "-fprintf" or "-fls")) return true;
                continue;
            }
            if (!ReadOnlyCommands.Contains(name)) return true;
        }
        return false;
    }

    /// <summary>
    /// Whether the command looks like it is meant to edit a file: a Python or Node script that writes one, <c>sed -i</c>,
    /// <c>perl -pi</c>, or PowerShell setting a file's content. For such a command a report of no change is worth a line:
    /// it is how a replace that matched nothing shows.
    /// </summary>
    public static bool LooksLikeEdit(string command) =>
        (ScriptHost().IsMatch(command) && ScriptWrite().IsMatch(command)) || InPlace().IsMatch(command) || PwshWrite().IsMatch(command);

    /// <summary>
    /// The directory whose repository the command works in: a leading <c>cd &lt;dir&gt; &amp;&amp;</c> (or <c>;</c>, or
    /// Set-Location) names it, as most agent commands start; otherwise the directory the shell starts in.
    /// </summary>
    public static string ProbeDir(string command, string cwd)
    {
        var m = LeadingCd().Match(command);
        if (!m.Success) return cwd;
        var dir = m.Groups["q"].Success ? m.Groups["q"].Value : m.Groups["s"].Success ? m.Groups["s"].Value : m.Groups["w"].Value;
        try
        {
            if (dir == "~" || dir.StartsWith("~/")) dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), dir.Length > 2 ? dir[2..] : "");
            // Git Bash spells C:\x as /c/x
            if (OperatingSystem.IsWindows() && GitBashDrive().Match(dir) is { Success: true } d) dir = d.Groups[1].Value.ToUpperInvariant() + ":/" + d.Groups[2].Value;
            var full = Path.GetFullPath(Path.IsPathRooted(dir) ? dir : Path.Combine(cwd, dir));
            return Directory.Exists(full) ? full : cwd;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException)
        {
            return cwd;
        }
    }

    // ------------------------------------------------------------------ snapshots

    /// <summary>The repository's dirty paths with their fingerprints, or null when there is no repository or git is too slow.</summary>
    public static async Task<Snapshot?> TakeAsync(string dir, CancellationToken ct, string? knownRoot = null)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(Budget);
        try
        {
            var root = knownRoot ?? await RootAsync(dir, budget.Token).ConfigureAwait(false);
            if (root is null) return null;
            var r = await GitRunner.RunAsync(root, budget.Token, StatusMaxChars, "status", "--porcelain=v2", "--branch", "-z", "--untracked-files=all").ConfigureAwait(false);
            if (r.StartError is not null || r.TimedOut || r.Aborted || r.Cut || r.ExitCode != 0) return null;
            var (head, kinds) = ParseStatus(r.Stdout);
            if (kinds.Count > MaxDirty) return null;
            var dirty = new Dictionary<string, Entry>(StringComparer.Ordinal);
            foreach (var (path, kind) in kinds) dirty[path] = Fingerprint(root, path, kind);
            return new Snapshot(root, head, dirty);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }

    private static async Task<string?> RootAsync(string dir, CancellationToken ct)
    {
        var key = Path.GetFullPath(dir);
        if (Roots.TryGetValue(key, out var cached)) return cached;
        var r = await GitRunner.RunAsync(key, ct, 4096, "rev-parse", "--show-toplevel").ConfigureAwait(false);
        if (r.StartError is not null || r.TimedOut || r.Aborted) return null; // not an answer: ask again next time
        var root = r.ExitCode == 0 && r.Stdout.Trim() is { Length: > 0 } top ? Path.GetFullPath(top) : null;
        if (Roots.Count > 256) Roots.Clear();
        Roots[key] = root;
        return root;
    }

    /// <summary>
    /// <c>git status --porcelain=v2 --branch -z</c>: the commit HEAD is on (null before the first one) and every dirty
    /// path with one letter: M (modified, also staged or renamed), A (added or untracked), D (deleted).
    /// </summary>
    internal static (string? Head, List<(string Path, char Kind)> Paths) ParseStatus(string status)
    {
        string? head = null;
        var paths = new List<(string, char)>();
        var fields = status.Split('\0');
        for (var i = 0; i < fields.Length; i++)
        {
            var f = fields[i];
            if (f.Length < 2) continue;
            switch (f[0])
            {
                case '#':
                    if (f.StartsWith("# branch.oid ", StringComparison.Ordinal) && f[13..] is var oid && oid != "(initial)") head = oid;
                    break;
                case '?':
                    paths.Add((f[2..], 'A'));
                    break;
                case '1':
                {
                    var parts = f.Split(' ', 9);
                    if (parts.Length == 9) paths.Add((parts[8], KindOf(parts[1])));
                    break;
                }
                case '2':
                {
                    var parts = f.Split(' ', 10);
                    if (parts.Length == 10) paths.Add((parts[9], KindOf(parts[1])));
                    i++; // the original path of a rename or copy follows as its own field
                    break;
                }
                case 'u':
                {
                    var parts = f.Split(' ', 11);
                    if (parts.Length == 11) paths.Add((parts[10], 'M'));
                    break;
                }
            }
        }
        return (head, paths);
    }

    private static char KindOf(string xy) => xy.Contains('D') ? 'D' : xy[0] == 'A' ? 'A' : 'M';

    private static Entry Fingerprint(string root, string rel, char kind)
    {
        try
        {
            var info = new FileInfo(Path.Combine(root, rel));
            return info.Exists ? new Entry(kind, info.Length, info.LastWriteTimeUtc.Ticks) : new Entry(kind, -1, -1);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return new Entry(kind, -1, -1);
        }
    }

    // ------------------------------------------------------------------ the difference

    /// <summary>
    /// The paths that changed between the snapshots: newly dirty, or dirty before with another size or time (a status
    /// letter alone, such as staging with git add, is not a change of the file). A path dirty before and clean after was
    /// put back (reverted), unless HEAD moved: a commit cleans what it took, and that is not the command undoing it.
    /// </summary>
    public static (List<Change> Changes, bool HeadMoved) Diff(Snapshot before, Snapshot after)
    {
        var changes = new List<Change>();
        foreach (var (path, now) in after.Dirty)
        {
            if (!before.Dirty.TryGetValue(path, out var was))
                changes.Add(new Change(path, KindName(now), CleanBefore: true));
            else if (was.Size != now.Size || was.Ticks != now.Ticks)
                changes.Add(new Change(path, KindName(now), CleanBefore: false));
        }
        var headMoved = before.Head != after.Head;
        if (!headMoved)
            foreach (var path in before.Dirty.Keys)
                if (!after.Dirty.ContainsKey(path)) changes.Add(new Change(path, "reverted", CleanBefore: false));
        changes.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        return (changes, headMoved);
    }

    private static string KindName(Entry e) => e.Kind switch { 'D' => "deleted", 'A' => "added", _ => "modified" };

    /// <summary>
    /// What the command did to the tracked files that were clean before it ran: their diff against the commit they were
    /// clean at, which is exactly the command's change (a file already dirty has older changes mixed in, so it is listed
    /// without a diff).
    /// </summary>
    public static async Task<string?> DiffTextAsync(Snapshot before, List<Change> changes, CancellationToken ct)
    {
        var paths = changes.Where(c => c.CleanBefore && c.Kind is "modified" or "deleted").Select(c => c.Path).Take(50).ToList();
        if (paths.Count == 0 || before.Head is null) return null;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(Budget);
        try
        {
            string[] args = ["diff", "--no-color", "--no-ext-diff", before.Head, "--", .. paths];
            var r = await GitRunner.RunAsync(before.Root, budget.Token, DiffMaxChars, args).ConfigureAwait(false);
            return r.ExitCode == 0 && r.StartError is null && !r.TimedOut && !r.Aborted && r.Stdout.Length > 0 ? r.Stdout : null;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>The model-facing line: which files changed (the first <see cref="MaxListed"/>), or none for an edit-like command.</summary>
    public static string? Line(Report report, bool editLike, bool outsideWorkspace)
    {
        var where = outsideWorkspace ? $" (in {report.Root}, outside this session's workspace)" : "";
        if (report.Changes.Count == 0)
            return editLike
                ? $"[No file in {report.Root} changed{(outsideWorkspace ? ", which is outside this session's workspace" : "")}. If this command was meant to edit one, the text it looked for may not be there: check the file, or use the edit tool, which says when oldText is not found.]"
                : null;
        var listed = report.Changes.Take(MaxListed).Select(c => $"{c.Kind switch { "added" => "A", "deleted" => "D", "reverted" => "R", _ => "M" }} {c.Path}");
        var more = report.Changes.Count > MaxListed ? $", and {report.Changes.Count - MaxListed} more" : "";
        return $"[Files changed while the command ran{where}: {string.Join(", ", listed)}{more}]";
    }

    // ------------------------------------------------------------------ patterns

    // a redirect into a file: >, >>, 1>, &>, *> (pwsh) with its target; 2>&1 and >&2 name a stream, not a file
    [GeneratedRegex(@"(?<![<>=\-])(?:\d|&|\*)?>>?(?!&)\s*(?<to>""[^""]*""|'[^']*'|[^\s;|&)]+)")]
    private static partial Regex Redirect();

    [GeneratedRegex(@"&&|\|\||;|\||\r?\n")]
    private static partial Regex Segments();

    [GeneratedRegex(@"\b(?:python[0-9.]*|py|node|deno|bun|ruby|perl)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ScriptHost();

    [GeneratedRegex(@"write_text|write_bytes|\.write\(|open\([^)]*['""](?:w|a|r\+|wb|ab)['""]|writeFileSync|writeFile\(|appendFileSync|File\.Write")]
    private static partial Regex ScriptWrite();

    [GeneratedRegex(@"\bsed\s+(?:-[a-zA-Z]*i|--in-place)|\bperl\s+-[a-zA-Z]*i")]
    private static partial Regex InPlace();

    [GeneratedRegex(@"\b(?:Set-Content|Add-Content|Out-File)\b|\[(?:System\.)?IO\.File\]::Write", RegexOptions.IgnoreCase)]
    private static partial Regex PwshWrite();

    [GeneratedRegex(@"^\s*(?:cd|Set-Location|sl|pushd|Push-Location)\s+(?:""(?<q>[^""]+)""|'(?<s>[^']+)'|(?<w>[^\s;&|]+))\s*(?:&&|;|\r?\n)", RegexOptions.IgnoreCase)]
    private static partial Regex LeadingCd();

    [GeneratedRegex(@"^/([a-zA-Z])/(.*)$")]
    private static partial Regex GitBashDrive();
}
