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
    /// <paramref name="relativeTo"/> (default: root).
    /// </summary>
    public static IEnumerable<WalkEntry> Walk(string root, WalkOptions? options = null, string? relativeTo = null, CancellationToken ct = default)
    {
        options ??= new WalkOptions();
        root = Path.GetFullPath(root);
        relativeTo = relativeTo is null ? root : Path.GetFullPath(relativeTo);
        var rootIgnores = options.RespectIgnore ? IgnoreStack.ForRoot(root).Enter(root) : IgnoreStack.Empty;
        var count = 0;
        var pending = new Stack<(List<WalkEntry> Entries, int Index, IgnoreStack Ignores)>();
        pending.Push((ReadDir(root, rootIgnores, options, relativeTo), 0, rootIgnores));
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
                pending.Push((ReadDir(e.FullPath, child, options, relativeTo), 0, child));
            }
        }
    }

    /// <summary>List one directory (sorted by name, case-insensitive), applying ignore rules. Ignored entries are flagged, not removed.</summary>
    public static List<WalkEntry> ListDirectory(string dir, bool respectIgnore = true, string? relativeTo = null)
    {
        dir = Path.GetFullPath(dir);
        var ignores = respectIgnore ? IgnoreStack.ForRoot(dir).Enter(dir) : IgnoreStack.Empty;
        return ReadDir(dir, ignores, new WalkOptions { RespectIgnore = respectIgnore }, relativeTo ?? dir);
    }

    private static List<WalkEntry> ReadDir(string dir, IgnoreStack ignores, WalkOptions options, string relativeTo)
    {
        var result = new List<WalkEntry>();
        List<FileSystemInfo> infos;
        try
        {
            infos = new DirectoryInfo(dir).EnumerateFileSystemInfos("*", EnumOptions).ToList();
        }
        catch
        {
            return result;
        }
        infos.Sort((a, b) =>
        {
            var c = StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name);
            return c != 0 ? c : StringComparer.Ordinal.Compare(a.Name, b.Name);
        });
        foreach (var fsi in infos)
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
        }
        return result;
    }
}
