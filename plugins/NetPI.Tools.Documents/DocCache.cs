namespace NetPI.Tools.Documents;

/// <summary>
/// Per-file extracted-text cache: LRU, byte-capped, keyed by the file's identity (path + mtime + size +
/// password, so a re-saved file or a different password never reads a stale extraction). In-memory and
/// per plugin generation - a hot reload drops it, like the Files plugin's index. The cap is
/// <c>documents.cacheMaxBytes</c> (default 32 MB): older entries are evicted in LRU order when it is exceeded.
/// </summary>
public sealed class DocCache(ISettings? settings = null)
{
    /// <summary>The default cap for <c>documents.cacheMaxBytes</c>: enough to hold the extracted text of a
    /// handful of long documents without pressing the machine's memory.</summary>
    public const int DefaultMaxBytes = 32 * 1024 * 1024;

    private sealed class Entry
    {
        public required string Key { get; init; }
        public required string Text { get; init; }
        public required long Bytes { get; init; }
    }

    /// <summary>Front = most recently used.</summary>
    private readonly LinkedList<Entry> _lru = new();
    private readonly Dictionary<string, LinkedListNode<Entry>> _index = new(StringComparer.Ordinal);
    private readonly object _lock = new();
    /// <summary>The cap in bytes (the setting, clamped to a sane minimum).</summary>
    private readonly long _maxBytes = Math.Max(1024, settings?.GetInt("documents.cacheMaxBytes", DefaultMaxBytes) ?? DefaultMaxBytes);
    private long _bytes;

    public DocCache() : this(null) { }

    /// <summary>The cap in bytes (the setting, clamped to a sane minimum).</summary>
    internal long MaxBytes { get { lock (_lock) return _maxBytes; } }

    /// <summary>
    /// The cached extraction for <paramref name="key"/>, or null on a miss. A hit moves the entry to the
    /// front (most recently used).
    /// </summary>
    public string? Get(string key)
    {
        lock (_lock)
        {
            if (!_index.TryGetValue(key, out var node)) return null;
            _lru.Remove(node);
            _lru.AddFirst(node);
            return node.Value.Text;
        }
    }

    /// <summary>Store an extraction, evicting least-recently-used entries until the byte cap holds.</summary>
    public void Add(string key, string text)
    {
        var bytes = System.Text.Encoding.UTF8.GetByteCount(text);
        lock (_lock)
        {
            if (_index.TryGetValue(key, out var existing))
            {
                _bytes -= existing.Value.Bytes;
                _lru.Remove(existing);
                _index.Remove(key);
            }
            var entry = new Entry { Key = key, Text = text, Bytes = bytes };
            var node = _lru.AddFirst(entry);
            _index[key] = node;
            _bytes += bytes;
            while (_bytes > _maxBytes && _lru.Last is { } last)
            {
                _bytes -= last.Value.Bytes;
                _lru.RemoveLast();
                _index.Remove(last.Value.Key);
            }
        }
    }

    /// <summary>Drop every entry (a test hook; the plugin generation's death does the same by reference).</summary>
    public void Clear()
    {
        lock (_lock)
        {
            _lru.Clear();
            _index.Clear();
            _bytes = 0;
        }
    }

    // ------------------------------------------------------------------ stats (internal, for tests)

    internal int Entries { get { lock (_lock) return _index.Count; } }
    internal long Bytes { get { lock (_lock) return _bytes; } }
}
