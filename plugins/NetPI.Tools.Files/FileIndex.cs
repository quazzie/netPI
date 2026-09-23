using System.Collections.Concurrent;

namespace NetPI.Tools.Files;

/// <summary>Backs the <c>files.search</c> (@-mentions) and <c>files.list</c> (file tree) RPC methods.</summary>
public sealed class FileIndex
{
    public const int MaxIndexedEntries = 50_000;
    public static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(10);

    private sealed record CacheEntry(DateTime At, List<WalkEntry> Entries);

    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);

    public sealed record SearchHit(string Path, string Rel, bool IsDir);

    public sealed record ListEntry(string Name, string Rel, bool IsDir, long? Size, DateTimeOffset? Mtime, bool? Ignored);

    public sealed record ListResult(string Root, string Dir, List<ListEntry> Entries);

    public async Task<List<SearchHit>> SearchAsync(string root, string? query, int limit, CancellationToken ct = default)
    {
        root = Path.GetFullPath(root);
        if (!Directory.Exists(root)) return [];
        limit = Math.Clamp(limit, 1, 500);
        var entries = await GetEntriesAsync(root, ct).ConfigureAwait(false);
        var q = (query ?? "").Trim().TrimStart('@');
        IEnumerable<WalkEntry> ranked;
        if (q.Length == 0)
            ranked = entries.OrderBy(e => e.RelPath.Count(c => c == '/')).ThenBy(e => e.IsDir ? 0 : 1).ThenBy(e => e.RelPath, StringComparer.OrdinalIgnoreCase).Take(limit);
        else
            ranked = PathFuzzy.Rank(entries, e => e.IsDir ? e.RelPath + "/" : e.RelPath, q, limit);
        return ranked.Select(e => new SearchHit(e.FullPath, e.RelPath, e.IsDir)).ToList();
    }

    public ListResult List(string root, string? dir)
    {
        root = Path.GetFullPath(root);
        var target = string.IsNullOrWhiteSpace(dir) ? root : Path.GetFullPath(Path.IsPathRooted(dir) ? dir : Path.Combine(root, dir));
        if (!Directory.Exists(target)) throw new RpcException("not_found", $"Directory not found: {target}");
        var entries = FileWalker.ListDirectory(target, respectIgnore: true, relativeTo: root)
            .Where(e => !(e.IsDir && e.Name == ".git"))
            .OrderBy(e => e.IsDir ? 0 : 1)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .Select(e => new ListEntry(e.Name, Rel(root, e.FullPath), e.IsDir, e.IsDir ? null : e.Size,
                e.MTimeUtc == default ? null : new DateTimeOffset(e.MTimeUtc, TimeSpan.Zero), e.Ignored ? true : null))
            .ToList();
        var relDir = Rel(root, target);
        return new ListResult(root, relDir == "." ? "" : relDir, entries);
    }

    private static string Rel(string root, string full)
    {
        var rel = Path.GetRelativePath(root, full).Replace('\\', '/');
        return rel;
    }

    private async Task<List<WalkEntry>> GetEntriesAsync(string root, CancellationToken ct)
    {
        if (_cache.TryGetValue(root, out var c) && DateTime.UtcNow - c.At < CacheTtl) return c.Entries;
        var gate = _locks.GetOrAdd(root, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_cache.TryGetValue(root, out c) && DateTime.UtcNow - c.At < CacheTtl) return c.Entries;
            var list = await Task.Run(() => FileWalker.Walk(root, new WalkOptions { MaxEntries = MaxIndexedEntries }, ct: ct).ToList(), ct).ConfigureAwait(false);
            _cache[root] = new CacheEntry(DateTime.UtcNow, list);
            // Keep the cache small.
            if (_cache.Count > 16)
                foreach (var old in _cache.Where(kv => DateTime.UtcNow - kv.Value.At > CacheTtl).Select(kv => kv.Key).ToList())
                    _cache.TryRemove(old, out _);
            return list;
        }
        finally
        {
            gate.Release();
        }
    }

    public void Invalidate() => _cache.Clear();
}
