using System.Collections.Concurrent;

namespace NetPI.Tools.Files;

/// <summary>Backs the <c>files.search</c> (@-mentions) and <c>files.list</c> (file tree) RPC methods.</summary>
public sealed class FileIndex
{
    public const int MaxIndexedEntries = 50_000;
    public static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(10);

    /// <summary>Distinct roots tracked at once: the cache and the per-root gates share the bound.</summary>
    private const int MaxRoots = 16;

    private sealed record CacheEntry(DateTime At, List<WalkEntry> Entries);

    /// <summary>
    /// Per-root gate: the semaphore makes concurrent searches for the same root walk once; the use count
    /// lets the pruner retire (and dispose) a gate only when nobody is inside it. Joining and retirement are
    /// both decided under this lock, so a gate is never disposed while a caller may still take the semaphore
    /// (joiners see the retirement under the lock and fetch the current gate instead), and a caller only
    /// ever takes the semaphore of a gate it successfully joined.
    /// </summary>
    private sealed class Gate
    {
        private readonly object _sync = new();
        private int _users;
        private bool _retired;

        public readonly SemaphoreSlim Semaphore = new(1, 1);

        /// <summary>Join. False when the gate was retired in the meantime: fetch the current one again.</summary>
        public bool TryEnter()
        {
            lock (_sync)
            {
                if (_retired) return false;
                _users++;
                return true;
            }
        }

        public void Exit()
        {
            lock (_sync) _users--;
        }

        /// <summary>Refused while in use; the pruner disposes only after it has removed the gate.</summary>
        public bool Retire()
        {
            lock (_sync)
            {
                if (_retired || _users > 0) return false;
                _retired = true;
                return true;
            }
        }

        public void Dispose() => Semaphore.Dispose();
    }

    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Gate> _gates = new(StringComparer.Ordinal);
    private readonly object _pruneSync = new();

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
        var gate = JoinGate(root);
        try
        {
            await gate.Semaphore.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_cache.TryGetValue(root, out c) && DateTime.UtcNow - c.At < CacheTtl) return c.Entries;
                var list = await Task.Run(() => FileWalker.Walk(root, new WalkOptions { MaxEntries = MaxIndexedEntries }, ct: ct).ToList(), ct).ConfigureAwait(false);
                _cache[root] = new CacheEntry(DateTime.UtcNow, list);
                // Keep the cache small.
                if (_cache.Count > MaxRoots)
                    foreach (var old in _cache.Where(kv => DateTime.UtcNow - kv.Value.At > CacheTtl).Select(kv => kv.Key).ToList())
                        _cache.TryRemove(old, out _);
                // Keep the gates bounded the same way.
                if (_gates.Count > MaxRoots)
                    PruneGates();
                return list;
            }
            finally
            {
                gate.Semaphore.Release();
            }
        }
        finally
        {
            gate.Exit();
        }
    }

    /// <summary>Join the root's gate; if the entry we fetched was just retired by the pruner, fetch the current one.</summary>
    private Gate JoinGate(string root)
    {
        while (true)
        {
            var gate = _gates.GetOrAdd(root, static _ => new Gate());
            if (gate.TryEnter()) return gate;
        }
    }

    /// <summary>
    /// Retire and dispose the gates nobody is using, first those whose root the cache no longer tracks.
    /// One pruner at a time, and retirement plus removal happen in one critical section: a gate is removed
    /// exactly when it is retired, so it cannot be disposed while a caller may still join or hold it, and
    /// the removal always targets the gate that was retired (a gate a caller already fetched is only
    /// retired under its own lock, with the use count checked at the same time).
    /// </summary>
    private void PruneGates()
    {
        if (_gates.Count <= MaxRoots) return;
        lock (_pruneSync)
        {
            if (_gates.Count <= MaxRoots) return;
            var roots = _gates.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray();
            for (int pass = 0; pass < 2 && _gates.Count > MaxRoots; pass++)
            {
                foreach (var root in roots)
                {
                    if (_gates.Count <= MaxRoots) break;
                    if (!_gates.TryGetValue(root, out var gate)) continue;
                    if (pass == 0 && _cache.ContainsKey(root)) continue;
                    if (!gate.Retire()) continue;
                    if (_gates.TryRemove(root, out var removed))
                    {
                        if (ReferenceEquals(removed, gate))
                            gate.Dispose();
                        else
                            _gates.TryAdd(root, removed); // unreachable under the prune lock: a gate added in the meantime is put back
                    }
                }
            }
        }
    }

    public void Invalidate() => _cache.Clear();
}
