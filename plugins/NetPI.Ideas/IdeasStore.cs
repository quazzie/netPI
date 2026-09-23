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
/// Reads and writes ideas files: per-file lock, atomic writes (temp file + rename), original line endings and BOM kept,
/// unknown fields preserved, and a FileSystemWatcher per file that publishes a debounced <c>ideas.changed { file }</c>.
/// </summary>
public sealed class IdeasStore : IDisposable
{
    public const string ChangedEvent = "ideas.changed";
    private const int MaxWatchers = 32;

    private static readonly JsonDocumentOptions ReadOptions = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };
    private static readonly StringComparer PathComparer = OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

    private readonly IEventBus _events;
    private readonly ILogger? _logger;
    private readonly TimeSpan _debounce;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(PathComparer);
    private readonly Dictionary<string, FileSystemWatcher> _watchers = new(PathComparer);
    private readonly List<string> _watchOrder = [];
    private readonly Dictionary<string, Timer> _timers = new(PathComparer);
    private readonly object _gate = new();
    private bool _disposed;

    public IdeasStore(IEventBus events, ILogger? logger = null, TimeSpan? debounce = null)
    {
        _events = events;
        _logger = logger;
        _debounce = debounce ?? TimeSpan.FromMilliseconds(250);
    }

    public static string Normalize(string path) => System.IO.Path.GetFullPath(path);

    public async Task<T> ReadAsync<T>(string path, Func<IdeasFile, T> read, CancellationToken ct = default)
    {
        path = Normalize(path);
        Watch(path);
        var gate = _locks.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try { return read(await LoadAsync(path, ct).ConfigureAwait(false)); }
        finally { gate.Release(); }
    }

    /// <summary>Load, mutate and save atomically (under the file's lock). Throw from <paramref name="mutate"/> to abort without saving.</summary>
    public async Task<T> UpdateAsync<T>(string path, Func<IdeasFile, T> mutate, CancellationToken ct = default)
    {
        path = Normalize(path);
        Watch(path);
        var gate = _locks.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var file = await LoadAsync(path, ct).ConfigureAwait(false);
            var result = mutate(file);
            await SaveAsync(file, ct).ConfigureAwait(false);
            Changed(path);
            return result;
        }
        finally { gate.Release(); }
    }

    // ------------------------------------------------------------------ load/save

    private static async Task<IdeasFile> LoadAsync(string path, CancellationToken ct)
    {
        byte[]? bytes = null;
        for (var attempt = 0; ; attempt++)
        {
            try { bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false); break; }
            catch (FileNotFoundException) { break; }
            catch (DirectoryNotFoundException) { break; }
            catch (IOException) when (attempt < 5) { await Task.Delay(40, ct).ConfigureAwait(false); } // sharing violation
        }
        return Parse(path, bytes);
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

        var dir = System.IO.Path.GetDirectoryName(file.Path)!;
        Directory.CreateDirectory(dir);
        var tmp = System.IO.Path.Combine(dir, $".{System.IO.Path.GetFileName(file.Path)}.{Ids.Short(6)}.tmp");
        try
        {
            await File.WriteAllBytesAsync(tmp, bytes, ct).ConfigureAwait(false);
            try { File.Move(tmp, file.Path, overwrite: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Locked by an editor / hidden file on Windows: fall back to an in-place write.
                await File.WriteAllBytesAsync(file.Path, bytes, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
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

    /// <summary>Publish <c>ideas.changed</c> after the file has been quiet for the debounce interval.</summary>
    private void Changed(string path)
    {
        lock (_gate)
        {
            if (_disposed) return;
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
        }
    }
}
