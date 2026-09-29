using System.Collections.Concurrent;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace NetPI.Ideas;

/// <summary>A loaded ideas file. <see cref="Root"/> keeps every field it had on disk.</summary>
public sealed class IdeasFile
{
    public required string Path { get; init; }
    public required JsonObject Root { get; init; }
    public required JsonArray Ideas { get; init; }
    /// <summary>"\n" or "\r\n" (the file's existing style; new files use LF).</summary>
    public string Eol { get; init; } = "\n";
    public bool Bom { get; init; }
    public bool Exists { get; init; }
    /// <summary>Indentation detected in the existing file (default two spaces).</summary>
    public char IndentChar { get; init; } = ' ';
    public int IndentSize { get; init; } = 2;
}

/// <summary>The ideas file exists but cannot be parsed. It is never overwritten in that state.</summary>
public sealed class IdeasFileException(string message) : Exception(message);

/// <summary>
/// Reads and writes ideas files: an OS lock per file (<see cref="FileGate"/>, shared with a second plugin instance),
/// atomic writes (temp file + rename), original line endings and BOM kept, unknown fields preserved, a
/// FileSystemWatcher per file that publishes a debounced <c>ideas.changed { file }</c>, and a short-lived cache of the
/// parsed file so a run of reads does not re-parse the whole (append-only, growing) backlog.
/// </summary>
public sealed class IdeasStore : IDisposable
{
    public const string ChangedEvent = "ideas.changed";
    private const int MaxWatchers = 32;
    /// <summary>The parsed file is kept this long (short: it is a backstop, not the mechanism — see <see cref="LoadAsync"/>).</summary>
    private static readonly TimeSpan ParseTtl = TimeSpan.FromSeconds(5);
    /// <summary>How many parsed files the cache keeps (paths are not bounded by anything else: tools pass their own).</summary>
    private const int MaxCached = 64;

    private static readonly JsonDocumentOptions ReadOptions = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };
    private static readonly StringComparer PathComparer = OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

    private readonly IEventBus _events;
    private readonly ILogger? _logger;
    private readonly TimeSpan _debounce;
    /// <summary>The lock the OS holds for us, so a reload swap's second store cannot lose an update to the first.</summary>
    private readonly FileGate _files = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(PathComparer);
    private readonly Dictionary<string, FileSystemWatcher> _watchers = new(PathComparer);
    private readonly List<string> _watchOrder = [];
    private readonly Dictionary<string, Timer> _timers = new(PathComparer);
    /// <summary>The parsed files, by path (under <see cref="_gate"/>).</summary>
    private readonly Dictionary<string, (IdeasFile File, DateTime At)> _parsed = new(PathComparer);
    private readonly object _gate = new();
    private bool _disposed;

    public IdeasStore(IEventBus events, ILogger? logger = null, TimeSpan? debounce = null)
    {
        _events = events;
        _logger = logger;
        _debounce = debounce ?? TimeSpan.FromMilliseconds(250);
    }

    public static string Normalize(string path) => System.IO.Path.GetFullPath(path);

    /// <summary>
    /// Read under the file's lock (both this instance's semaphore and the OS lock every store shares), so a read never
    /// sees a half-applied write from another plugin instance.
    /// </summary>
    public async Task<T> ReadAsync<T>(string path, Func<IdeasFile, T> read, CancellationToken ct = default)
    {
        path = Normalize(path);
        Watch(path);
        var gate = _locks.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try { return await _files.WithFileAsync(path, async token => read(await LoadAsync(path, token).ConfigureAwait(false)), ct).ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    /// <summary>
    /// Load, mutate and save atomically: the read, the change and the rename all happen while the file's lock is held,
    /// so two stores (or a store and a plugin swap) cannot both read the old file and write their own version. Throw from
    /// <paramref name="mutate"/> to abort without saving. A write that cannot complete throws and leaves the file as it
    /// was — the last valid backlog is never replaced by half of a new one.
    /// </summary>
    public async Task<T> UpdateAsync<T>(string path, Func<IdeasFile, T> mutate, CancellationToken ct = default)
    {
        path = Normalize(path);
        Watch(path);
        var gate = _locks.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await _files.WithFileAsync(path, async token =>
            {
                // A write always reads the file from disk. The parse cache exists for bursts of *reads* (a commit sweep,
                // recall while the user types); a write that reused it could (a) mutate a tree that is also in the cache,
                // so a patch that throws half way leaves its partial change there for the next writer to commit, and
                // (b) write a tree that was parsed before somebody else's committed change, because our watcher missed
                // their write. The lock says "one at a time"; it cannot make a stale tree current.
                var file = await LoadAsync(path, token, fresh: true).ConfigureAwait(false);
                var result = mutate(file);
                // Belt and braces: the tree we just changed must not be the one a reader gets from the cache.
                Invalidate(path);
                await SaveAsync(file, token).ConfigureAwait(false);
                Changed(path);
                return result;
            }, ct).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    // ------------------------------------------------------------------ load/save

    /// <summary>
    /// The parsed file, or the disk. The recall check asks once a second while a new chat is typed (and a commit sweep
    /// reads per commit), so a burst of reads must not re-parse the whole backlog each time: a hit returns the tree that
    /// is on disk. The TTL is only a backstop — it bounds staleness when a change event is missed (a watcher is one of
    /// at most MaxWatchers files, and the OS drops events under load); in the normal case the watcher's change events and
    /// the store's own writes remove the entry in <see cref="Changed"/> before the next read, which is what keeps it safe.
    /// A file that cannot be parsed throws as before and is not cached, so the next read retries. Callers treat the
    /// file as read-only: mutations go through <see cref="UpdateAsync"/>, which saves the same tree (and invalidates).
    /// </summary>
    private async Task<IdeasFile> LoadAsync(string path, CancellationToken ct, bool fresh = false)
    {
        if (!fresh)
        {
            lock (_gate)
            {
                if (_parsed.TryGetValue(path, out var hit) && DateTime.UtcNow - hit.At < ParseTtl)
                    return hit.File;
            }
        }
        byte[]? bytes = null;
        for (var attempt = 0; ; attempt++)
        {
            try { bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false); break; }
            catch (FileNotFoundException) { break; }
            catch (DirectoryNotFoundException) { break; }
            catch (IOException) when (attempt < 5) { await Task.Delay(40, ct).ConfigureAwait(false); } // sharing violation
        }
        var file = Parse(path, bytes);
        lock (_gate)
        {
            if (_parsed.Count >= MaxCached)
                _parsed.Remove(_parsed.OrderBy(p => p.Value.At).First().Key); // the least recently cached
            _parsed[path] = (file, DateTime.UtcNow);
        }
        return file;
    }

    public static IdeasFile Parse(string path, byte[]? bytes)
    {
        if (bytes is null || bytes.Length == 0 || (bytes.Length == 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF))
            return New(path, exists: bytes is not null);

        var bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        var text = Encoding.UTF8.GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0));
        if (text.Trim().Length == 0) return New(path, exists: true);

        JsonNode? node;
        try { node = JsonNode.Parse(text, null, ReadOptions); }
        catch (JsonException ex)
        {
            throw new IdeasFileException($"{path} is not valid JSON ({ex.Message}). Fix or delete the file; it will not be overwritten.");
        }

        JsonObject root;
        switch (node)
        {
            case JsonObject o: root = o; break;
            case JsonArray a: root = new JsonObject { ["version"] = 1, ["ideas"] = a }; break; // bare array: wrap
            default: throw new IdeasFileException($"{path} must contain a JSON object {{ \"version\": 1, \"ideas\": [...] }}.");
        }
        if (root["ideas"] is not JsonArray ideas)
        {
            if (root["ideas"] is not null)
                throw new IdeasFileException($"{path}: \"ideas\" must be an array. Fix the file; it will not be overwritten.");
            ideas = [];
            root["ideas"] = ideas;
        }
        var (indentChar, indentSize) = DetectIndent(text);
        return new IdeasFile
        {
            Path = path, Root = root, Ideas = ideas, Eol = DetectEol(text), Bom = bom, Exists = true,
            IndentChar = indentChar, IndentSize = indentSize,
        };
    }

    private static IdeasFile New(string path, bool exists)
    {
        var ideas = new JsonArray();
        return new IdeasFile { Path = path, Root = new JsonObject { ["version"] = 1, ["ideas"] = ideas }, Ideas = ideas, Exists = exists };
    }

    /// <summary>CRLF when most line breaks are CRLF, else LF.</summary>
    public static string DetectEol(string text)
    {
        int crlf = 0, lf = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n') continue;
            if (i > 0 && text[i - 1] == '\r') crlf++; else lf++;
        }
        return crlf > lf ? "\r\n" : "\n";
    }

    /// <summary>Indentation of the first indented line (tabs or 1-8 spaces), default two spaces.</summary>
    public static (char Char, int Size) DetectIndent(string text)
    {
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var n = 0;
            while (n < line.Length && (line[n] == ' ' || line[n] == '\t')) n++;
            if (n == 0 || n == line.Length) continue;
            if (line[0] == '\t') return ('\t', 1);
            return (' ', Math.Clamp(n, 1, 8));
        }
        return (' ', 2);
    }

    public static string Render(IdeasFile file)
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            IndentCharacter = file.IndentChar,
            IndentSize = file.IndentSize,
            NewLine = "\n",
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        var json = file.Root.ToJsonString(options) + "\n";
        return file.Eol == "\n" ? json : json.Replace("\n", file.Eol);
    }

    private static async Task SaveAsync(IdeasFile file, CancellationToken ct)
    {
        var text = Render(file);
        var body = Encoding.UTF8.GetBytes(text);
        var bytes = file.Bom ? [0xEF, 0xBB, 0xBF, .. body] : body;
        // Through the temporary + rename, or not at all: there is no in-place fallback, because an interrupted one
        // truncates the last valid backlog (an editor holding the file makes the rename fail, and it fails again later).
        await FileGate.WriteAtomicAsync(file.Path, bytes, ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ change notification

    /// <summary>Watch a file for external edits (idempotent; at most <see cref="MaxWatchers"/> files are watched).</summary>
    public void Watch(string path)
    {
        path = Normalize(path);
        lock (_gate)
        {
            if (_disposed || _watchers.ContainsKey(path)) return;
            var dir = System.IO.Path.GetDirectoryName(path);
            if (dir is null || !Directory.Exists(dir)) return;
            try
            {
                var w = new FileSystemWatcher(dir, System.IO.Path.GetFileName(path))
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.CreationTime,
                    IncludeSubdirectories = false,
                };
                FileSystemEventHandler onChange = (_, _) => Changed(path);
                w.Changed += onChange;
                w.Created += onChange;
                w.Deleted += onChange;
                w.Renamed += (_, _) => Changed(path);
                w.Error += (_, e) => _logger?.LogDebug(e.GetException(), "Ideas watcher error for {Path}", path);
                w.EnableRaisingEvents = true;
                _watchers[path] = w;
                _watchOrder.Add(path);
                if (_watchOrder.Count > MaxWatchers)
                {
                    var oldest = _watchOrder[0];
                    _watchOrder.RemoveAt(0);
                    if (_watchers.Remove(oldest, out var old)) old.Dispose();
                }
            }
            catch (Exception ex) { _logger?.LogDebug(ex, "Cannot watch {Path}", path); }
        }
    }

    /// <summary>Drop the parsed file, so the next read comes from disk (the tree may have been changed, or the disk is newer).</summary>
    private void Invalidate(string path)
    {
        lock (_gate) _parsed.Remove(path);
    }

    /// <summary>Publish <c>ideas.changed</c> after the file has been quiet for the debounce interval.</summary>
    private void Changed(string path)
    {
        lock (_gate)
        {
            if (_disposed) return;
            // The watcher's event or our own save: the next read re-parses, so a write is visible to a read at once.
            _parsed.Remove(path);
            if (!_timers.TryGetValue(path, out var timer))
            {
                timer = new Timer(_ => Fire(path), null, Timeout.Infinite, Timeout.Infinite);
                _timers[path] = timer;
            }
            timer.Change(_debounce, Timeout.InfiniteTimeSpan);
        }
    }

    private void Fire(string path)
    {
        lock (_gate) { if (_disposed) return; }
        try { _events.Publish(ChangedEvent, new JsonObject { ["file"] = path }); }
        catch (Exception ex) { _logger?.LogDebug(ex, "Publishing ideas.changed failed"); }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var w in _watchers.Values) { try { w.Dispose(); } catch { } }
            _watchers.Clear();
            foreach (var t in _timers.Values) t.Dispose();
            _timers.Clear();
            _parsed.Clear();
        }
    }
}
