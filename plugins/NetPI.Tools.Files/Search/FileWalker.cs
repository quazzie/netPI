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
    /// <paramref name="relativeTo"/> (default: root). Each directory is materialised only as far as the walk can
    /// still emit entries, so a tree that stops at <c>MaxEntries</c> inside a huge directory never reads the rest of it.
    /// </summary>
    public static IEnumerable<WalkEntry> Walk(string root, WalkOptions? options = null, string? relativeTo = null, CancellationToken ct = default)
    {
        options ??= new WalkOptions();
        root = Path.GetFullPath(root);
        relativeTo = relativeTo is null ? root : Path.GetFullPath(relativeTo);
        var rootIgnores = options.RespectIgnore ? IgnoreStack.ForRoot(root).Enter(root) : IgnoreStack.Empty;
        var count = 0;
        var pending = new Stack<(List<WalkEntry> Entries, int Index, IgnoreStack Ignores)>();
        pending.Push((ReadDir(root, rootIgnores, options, relativeTo, options.MaxEntries), 0, rootIgnores));
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
                // Materialise only what the walk can still emit: beyond that the rest of the directory is never yielded.
                pending.Push((ReadDir(e.FullPath, child, options, relativeTo, Math.Max(0, options.MaxEntries - count)), 0, child));
            }
        }
    }

    /// <summary>
    /// List one directory (sorted by name, case-insensitive), applying ignore rules. Ignored entries are flagged, not
    /// removed. At most <paramref name="maxEntries"/> entries are materialised; <c>visible</c> and <c>ignored</c> are the
    /// directory's totals, counted in one streamed pass when the cap was reached, so a caller can say how many entries
    /// it did not list.
    /// </summary>
    public static (List<WalkEntry> Entries, int Visible, int Ignored) ListDirectory(string dir, bool respectIgnore = true, string? relativeTo = null, int maxEntries = int.MaxValue)
    {
        dir = Path.GetFullPath(dir);
        var ignores = respectIgnore ? IgnoreStack.ForRoot(dir).Enter(dir) : IgnoreStack.Empty;
        var options = new WalkOptions { RespectIgnore = respectIgnore };
        var entries = ReadDir(dir, ignores, options, relativeTo ?? dir, maxEntries);
        var visible = entries.Count(e => !e.Ignored);
        var ignored = entries.Count - visible;
        if (maxEntries < int.MaxValue && entries.Count >= maxEntries)
            (visible, ignored) = CountEntries(dir, ignores, options);
        return (entries, visible, ignored);
    }

    /// <summary>One streamed pass over a directory (nothing is materialised): the totals behind a capped listing.</summary>
    private static (int Visible, int Ignored) CountEntries(string dir, IgnoreStack ignores, WalkOptions options)
    {
        int visible = 0, ignored = 0;
        try
        {
            foreach (var fsi in new DirectoryInfo(dir).EnumerateFileSystemInfos("*", EnumOptions))
            {
                try
                {
                    var isDir = (fsi.Attributes & FileAttributes.Directory) != 0;
                    var isIgnored = options.RespectIgnore &&
                                   ((isDir && IgnoreStack.DefaultSkipDirs.Contains(fsi.Name)) || ignores.IsIgnored(fsi.FullName, isDir));
                    if (isIgnored) ignored++; else visible++;
                }
                catch { /* entry vanished or is inaccessible */ }
            }
        }
        catch { /* the directory vanished or is inaccessible */ }
        return (visible, ignored);
    }

    /// <summary>The entries of one directory, materialised up to <paramref name="maxEntries"/> and sorted by name.</summary>
    internal static List<WalkEntry> ReadDir(string dir, IgnoreStack ignores, WalkOptions options, string relativeTo, int maxEntries = int.MaxValue)
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
                    var ignored = options.RespectIgnore &&
                                  ((isDir && IgnoreStack.DefaultSkipDirs.Contains(fsi.Name)) || ignores.IsIgnored(fsi.FullName, isDir));
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
                if (result.Count >= maxEntries) break; // the rest is counted, not materialised
            }
        }
        catch { /* the directory vanished or is inaccessible */ }
        result.Sort((a, b) =>
        {
            var c = StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name);
            return c != 0 ? c : StringComparer.Ordinal.Compare(a.Name, b.Name);
        });
        return result;
    }
}
