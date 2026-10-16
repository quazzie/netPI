namespace NetPI.Tools.Files;

public sealed record WalkEntry(string FullPath, string RelPath, string Name, bool IsDir, long Size, DateTime MTimeUtc, bool Ignored = false, bool IsLink = false);

public sealed class WalkOptions
{
    /// <summary>Apply .gitignore/.ignore files and the built-in skip list.</summary>
    public bool RespectIgnore { get; init; } = true;
    /// <summary>Yield ignored entries (flagged <see cref="WalkEntry.Ignored"/>) instead of dropping them. Ignored directories are never descended into.</summary>
    public bool ReportIgnored { get; init; }
    /// <summary>Descend into subdirectories.</summary>
    public bool Recursive { get; init; } = true;
    public int MaxDepth { get; init; } = 64;
    /// <summary>Stop after this many yielded entries.</summary>
    public int MaxEntries { get; init; } = 200_000;
    public bool IncludeDirs { get; init; } = true;
    public bool IncludeFiles { get; init; } = true;
}

/// <summary>Depth-first, name-sorted directory walk honouring ignore rules. Directory symlinks/junctions are listed but not followed.</summary>
public static class FileWalker
{
    private static readonly EnumerationOptions EnumOptions = new()
    {
        IgnoreInaccessible = true,
        AttributesToSkip = 0,
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false,
    };

    /// <summary>
    /// Walk <paramref name="root"/> (not included in the output). Relative paths use '/' and are relative to
    /// <paramref name="relativeTo"/> (default: root). The walk stops after <c>MaxEntries</c> entries; every directory
    /// it enters is enumerated whole and sorted, so a capped listing is the same first N the full listing would show.
    /// A directory below the root that is a git repository of its own (a <c>.git</c> file or directory) is skipped,
    /// so a nested worktree is never walked (a walk rooted inside one works normally).
    /// </summary>
    public static IEnumerable<WalkEntry> Walk(string root, WalkOptions? options = null, string? relativeTo = null, CancellationToken ct = default)
    {
        options ??= new WalkOptions();
        root = Path.GetFullPath(root);
        relativeTo = relativeTo is null ? root : Path.GetFullPath(relativeTo);
        var rootIgnores = options.RespectIgnore ? IgnoreStack.ForRoot(root).Enter(root) : IgnoreStack.Empty;
        var count = 0;
        var pending = new Stack<(List<WalkEntry> Entries, int Index, IgnoreStack Ignores)>();
        pending.Push((ReadDir(root, rootIgnores, options, relativeTo, options.MaxEntries, root), 0, rootIgnores));
        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var (entries, index, ignores) = pending.Pop();
            if (index >= entries.Count) continue;
            pending.Push((entries, index + 1, ignores));
            var e = entries[index];
            var emit = (e.IsDir ? options.IncludeDirs : options.IncludeFiles) && (!e.Ignored || options.ReportIgnored);
            if (emit)
            {
                yield return e;
                if (++count >= options.MaxEntries) yield break;
            }
            if (e.IsDir && !e.Ignored && !e.IsLink && options.Recursive && pending.Count <= options.MaxDepth)
            {
                var child = options.RespectIgnore ? ignores.Enter(e.FullPath) : ignores;
                // The budget is the walk's, not the directory's: a tree that stops at MaxEntries never enters the rest of it.
                pending.Push((ReadDir(e.FullPath, child, options, relativeTo, Math.Max(0, options.MaxEntries - count), root), 0, child));
            }
        }
    }

    /// <summary>
    /// List one directory (sorted by name, case-insensitive), applying ignore rules. Ignored entries are flagged, not
    /// removed. At most <paramref name="maxEntries"/> entries are kept — the first by the same name order the full
    /// listing uses — and <c>visible</c> and <c>ignored</c> are the directory's totals, counted in one streamed pass
    /// when the cap was reached, so a caller can say how many entries it did not list.
    /// </summary>
    public static (List<WalkEntry> Entries, int Visible, int Ignored) ListDirectory(string dir, bool respectIgnore = true, string? relativeTo = null, int maxEntries = int.MaxValue)
    {
        dir = Path.GetFullPath(dir);
        var ignores = respectIgnore ? IgnoreStack.ForRoot(dir).Enter(dir) : IgnoreStack.Empty;
        var options = new WalkOptions { RespectIgnore = respectIgnore };
        var entries = ReadDir(dir, ignores, options, relativeTo ?? dir, maxEntries, dir);
        var visible = entries.Count(e => !e.Ignored);
        var ignored = entries.Count - visible;
        if (maxEntries < int.MaxValue && entries.Count >= maxEntries)
            (visible, ignored) = CountEntries(dir, ignores, options, dir);
        return (entries, visible, ignored);
    }

    /// <summary>One streamed pass over a directory (nothing is materialised): the totals behind a capped listing.</summary>
    private static (int Visible, int Ignored) CountEntries(string dir, IgnoreStack ignores, WalkOptions options, string? walkRoot)
    {
        int visible = 0, ignored = 0;
        try
        {
            foreach (var fsi in new DirectoryInfo(dir).EnumerateFileSystemInfos("*", EnumOptions))
            {
                try
                {
                    var isDir = (fsi.Attributes & FileAttributes.Directory) != 0;
                    var isLink = isDir && (fsi.Attributes & FileAttributes.ReparsePoint) != 0 && fsi.LinkTarget is not null;
                    var isIgnored = options.RespectIgnore &&
                                   ((isDir && IgnoreStack.DefaultSkipDirs.Contains(fsi.Name)) || ignores.IsIgnored(fsi.FullName, isDir) ||
                                    (isDir && !isLink && walkRoot is not null && IsNestedRepo(fsi, walkRoot)));
                    if (isIgnored) ignored++; else visible++;
                }
                catch { /* entry vanished or is inaccessible */ }
            }
        }
        catch { /* the directory vanished or is inaccessible */ }
        return (visible, ignored);
    }

    /// <summary>
    /// A directory below the walk root that is a git repository of its own — a <c>.git</c> file (a worktree) or a
    /// <c>.git</c> directory (a sub-clone): the walk skips it, so the files of a nested checkout are never searched
    /// (or edited through) the outer tree. The walk root itself is exempt: a walk rooted inside such a folder works
    /// normally. A directory link is not followed, so it is not checked.
    /// </summary>
    private static bool IsNestedRepo(FileSystemInfo fsi, string walkRoot)
    {
        if (string.Equals(fsi.FullName, walkRoot, StringComparison.OrdinalIgnoreCase)) return false;
        var git = Path.Combine(fsi.FullName, ".git");
        try { return File.Exists(git) || Directory.Exists(git); }
        catch { return false; }
    }

    /// <summary>The first <paramref name="maxEntries"/> entries of one directory by name: the directory is enumerated
    /// whole and sorted, so a capped listing is the same first N the full listing would show.</summary>
    internal static List<WalkEntry> ReadDir(string dir, IgnoreStack ignores, WalkOptions options, string relativeTo, int maxEntries = int.MaxValue, string? walkRoot = null)
    {
        var result = new List<WalkEntry>();
        try
        {
            foreach (var fsi in new DirectoryInfo(dir).EnumerateFileSystemInfos("*", EnumOptions))
            {
                try
                {
                    var isDir = (fsi.Attributes & FileAttributes.Directory) != 0;
                    // Only symlinks/junctions: other reparse points (OneDrive placeholders, dedup) are regular directories.
                    var isLink = isDir && (fsi.Attributes & FileAttributes.ReparsePoint) != 0 && fsi.LinkTarget is not null;
                    var nestedRepo = isDir && !isLink && walkRoot is not null && IsNestedRepo(fsi, walkRoot);
                    var ignored = options.RespectIgnore &&
                                  ((isDir && IgnoreStack.DefaultSkipDirs.Contains(fsi.Name)) || ignores.IsIgnored(fsi.FullName, isDir) || nestedRepo);
                    long size = 0;
                    if (!isDir && fsi is FileInfo fi)
                    {
                        try { size = fi.Length; } catch { }
                    }
                    DateTime mtime = default;
                    try { mtime = fsi.LastWriteTimeUtc; } catch { }
                    var rel = Path.GetRelativePath(relativeTo, fsi.FullName).Replace('\\', '/');
                    result.Add(new WalkEntry(fsi.FullName, rel, fsi.Name, isDir, size, mtime, ignored, isLink));
                }
                catch { /* entry vanished or is inaccessible */ }
            }
        }
        catch { /* the directory vanished or is inaccessible */ }
        result.Sort((a, b) =>
        {
            var c = StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name);
            return c != 0 ? c : StringComparer.Ordinal.Compare(a.Name, b.Name);
        });
        return maxEntries < int.MaxValue && result.Count > maxEntries ? result.Take(maxEntries).ToList() : result;
    }
}
