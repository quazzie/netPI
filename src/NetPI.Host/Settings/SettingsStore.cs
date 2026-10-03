using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace NetPI.Host.Settings;

/// <summary>
/// <see cref="ISettings"/> backed by <c>settings.json</c> (comments and trailing commas allowed). Values are read and
/// written by dotted path. Writes are serialized and atomic (a writer gate, then a unique temp file + move): a failed
/// write changes nothing, so a value is either on disk and live or neither. External edits are picked up by a debounced
/// FileSystemWatcher; an invalid file is logged and the last good document is kept, and while the file does not parse no
/// write is saved over it (the store would replace the user's broken file with defaults plus one change, losing providers,
/// API keys, agents and MCP servers). Every change publishes <c>settings.changed</c> (<c>{ path }</c> for single-value
/// writes, <c>{ source: "file" }</c> for external edits).
/// </summary>
internal sealed class SettingsStore : ISettings, IDisposable
{
    private static readonly JsonDocumentOptions DocOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    private readonly Lock _gate = new();
    /// <summary>One writer at a time, so the file ends up in the order the document was changed in. Readers never take it.</summary>
    private readonly Lock _writeGate = new();
    private readonly ILogger _log;
    private JsonObject _root;
    private string? _lastText;
    private IEventBus? _bus;
    private FileSystemWatcher? _watcher;
    private Debouncer? _reload;
    private bool _disposed;
    /// <summary>Set (with the parse error) while the file on disk does not parse, so a write cannot replace it; null when it parses.</summary>
    private string? _invalidError;

    public SettingsStore(string filePath, ILogger log)
    {
        FilePath = filePath;
        _log = log;
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        if (File.Exists(filePath))
        {
            var text = ReadText();
            _lastText = text;
            if (TryParse(text, out var root, out var error)) _root = root;
            else
            {
                _invalidError = error;
                _log.LogError("Settings file {File} is not valid JSON ({Error}); using defaults until it is fixed — no settings write is saved over it", filePath, error);
                _root = DefaultSettings.Create();
            }
        }
        else
        {
            _root = DefaultSettings.Create();
            Save();
            _log.LogInformation("Created default settings at {File}", filePath);
        }
    }

    public string FilePath { get; }

    public bool InvalidOnDisk { get { lock (_gate) return _invalidError is not null; } }
    public string? InvalidOnDiskError { get { lock (_gate) return _invalidError; } }

    /// <summary>Raised after any change (path, or null for whole-document changes). Runs on the writer's thread.</summary>
    public event Action<string?>? Changed;

    public void AttachBus(IEventBus bus) => _bus = bus;

    public void StartWatching()
    {
        lock (_gate)
        {
            if (_watcher is not null || _disposed) return;
            _reload = new Debouncer(TimeSpan.FromMilliseconds(250), ReloadFromDisk);
            var w = new FileSystemWatcher(Path.GetDirectoryName(FilePath)!, Path.GetFileName(FilePath))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.CreationTime,
                IncludeSubdirectories = false,
            };
            FileSystemEventHandler onChange = (_, _) => _reload.Trigger();
            w.Changed += onChange;
            w.Created += onChange;
            w.Renamed += (_, _) => _reload.Trigger();
            w.Error += (_, e) => { _log.LogWarning(e.GetException(), "Settings watcher error"); _reload.Trigger(); };
            w.EnableRaisingEvents = true;
            _watcher = w;
        }
    }

    // ------------------------------------------------------------------ ISettings

    public JsonObject Snapshot()
    {
        lock (_gate) return (JsonObject)_root.DeepClone();
    }

    public JsonNode? GetNode(string path)
    {
        lock (_gate) return Walk(_root, path)?.DeepClone();
    }

    public T? Get<T>(string path, T? defaultValue = default)
    {
        lock (_gate)
        {
            var node = Walk(_root, path);
            if (node is null || node.GetValueKind() == JsonValueKind.Null) return defaultValue;
            if (typeof(JsonNode).IsAssignableFrom(typeof(T))) return node.DeepClone() is T clone ? clone : defaultValue;
            try
            {
                var value = node.Deserialize<T>(NetPiJson.For(typeof(T)));
                return value is null ? defaultValue : value;
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or NotSupportedException or FormatException)
            {
                _log.LogWarning("Setting '{Path}' cannot be read as {Type}: {Error}", path, typeof(T).Name, ex.Message);
                return defaultValue;
            }
        }
    }

    public void Set(string path, JsonNode? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var segments = path.Split('.');
        if (segments.Any(string.IsNullOrWhiteSpace)) throw new ArgumentException($"Invalid settings path '{path}'");
        lock (_writeGate)
        {
            JsonObject previous, changed;
            string? previousText;
            string text;
            lock (_gate)
            {
                ThrowIfFileBroken();
                var parent = _root;
                for (var i = 0; i < segments.Length - 1; i++)
                {
                    if (parent[segments[i]] is JsonObject child)
                    {
                        parent = child;
                        continue;
                    }
                    if (value is null) return; // removing below a missing branch: nothing to do
                    var created = new JsonObject();
                    parent[segments[i]] = created;
                    parent = created;
                }
                var key = segments[^1];
                var existing = parent.TryGetPropertyValue(key, out var old) ? old : null;
                if (value is null)
                {
                    if (!parent.ContainsKey(key)) return;
                }
                else if (existing is not null && JsonNode.DeepEquals(existing, value)) return;

                previous = (JsonObject)_root.DeepClone();
                previousText = _lastText;
                if (value is null) parent.Remove(key);
                else parent[key] = value.DeepClone();
                text = SnapshotLocked();
                changed = _root;
            }
            try { WriteFile(text); }   // outside _gate: see WriteFile
            catch
            {
                Rollback(changed, previous, previousText);
                throw;
            }
        }
        OnChanged(path);
    }

    public void Replace(JsonObject root)
    {
        ArgumentNullException.ThrowIfNull(root);
        var clone = (JsonObject)root.DeepClone();
        lock (_writeGate)
        {
            JsonObject previous, changed;
            string? previousText;
            string text;
            lock (_gate)
            {
                ThrowIfFileBroken();
                if (JsonNode.DeepEquals(_root, clone)) return;
                previous = _root;
                previousText = _lastText;
                _root = clone;
                text = SnapshotLocked();
                changed = _root;
            }
            try { WriteFile(text); }
            catch
            {
                Rollback(changed, previous, previousText);
                throw;
            }
        }
        OnChanged(null);
    }

    // ------------------------------------------------------------------ internals

    /// <summary>
    /// A write over a broken file would save defaults plus one change and lose everything the file still holds (providers,
    /// keys, agents), so the file is fixed first. Caller holds <c>_gate</c>.
    /// </summary>
    private void ThrowIfFileBroken()
    {
        if (_invalidError is { } error)
            throw new InvalidOperationException($"the settings file {FilePath} is not valid JSON ({error}); fix it first — no settings change is saved until it parses");
    }

    private static JsonNode? Walk(JsonNode? node, string path)
    {
        if (string.IsNullOrEmpty(path)) return node;
        foreach (var segment in path.Split('.'))
        {
            node = node switch
            {
                JsonObject o => o.TryGetPropertyValue(segment, out var child) ? child : null,
                JsonArray a when int.TryParse(segment, out var index) && index >= 0 && index < a.Count => a[index],
                _ => null,
            };
            if (node is null) return null;
        }
        return node;
    }

    private static bool TryParse(string text, out JsonObject root, out string? error)
    {
        try
        {
            if (JsonNode.Parse(text, null, DocOptions) is JsonObject obj)
            {
                root = obj;
                error = null;
                return true;
            }
            error = "the root must be a JSON object";
        }
        catch (JsonException ex)
        {
            error = ex.Message;
        }
        root = null!;
        return false;
    }

    /// <summary>Writes the current document (the file does not exist yet).</summary>
    private void Save()
    {
        lock (_writeGate)
        {
            string text;
            lock (_gate) text = SnapshotLocked();
            WriteFile(text);
        }
    }

    /// <summary>
    /// Puts the document back after a failed write: a value that never reached the disk is not live, so the identical
    /// retry is a real write instead of a no-op, and no change event is owed. A reload that replaced the document in
    /// the meantime keeps its own state.
    /// </summary>
    private void Rollback(JsonObject changed, JsonObject previous, string? previousText)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_root, changed)) return;
            _root = previous;
            _lastText = previousText;
        }
    }

    /// <summary>The document as it belongs on disk. Cheap enough to run under the lock; the write is not.</summary>
    private string SnapshotLocked()
    {
        var text = _root.ToJsonString(NetPiJson.Indented) + Environment.NewLine;
        _lastText = text;
        return text;
    }

    /// <summary>
    /// Writes the document to disk, WITHOUT the lock. A write can block on an editor or antivirus for well over a
    /// second (the retries below), and every reader waits on that lock — including the origin check on every HTTP
    /// request and the plugin settings a reload reads.
    /// </summary>
    private void WriteFile(string text)
    {
        // A unique name per write: two writers never share a temp file, and a leftover cannot be picked up by the
        // next write (which a fixed settings.json.tmp could be, if a write was killed between the two calls).
        var tmp = FilePath + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    // The document holds API keys: on Unix the tmp is created owner-only, and the move keeps the mode.
                    using (var fs = PathUtil.CreateOwnerOnly(tmp))
                    {
                        fs.Write(Utf8NoBom.GetBytes(text));
                        fs.Flush(flushToDisk: true);   // the move must never rename a file the disk has not received
                    }
                    File.Move(tmp, FilePath, overwrite: true);
                    return;
                }
                catch (IOException) when (attempt < 5)
                {
                    Thread.Sleep(40 * (attempt + 1)); // editor or antivirus holding the file
                }
                catch (UnauthorizedAccessException) when (attempt < 5)
                {
                    Thread.Sleep(40 * (attempt + 1));
                }
            }
        }
        finally
        {
            try { File.Delete(tmp); }   // nothing to do after the move
            catch (Exception ex) { _log.LogDebug(ex, "Could not remove the temporary settings file {Tmp}", tmp); }
        }
    }

    private string ReadText()
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var fs = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                return reader.ReadToEnd();
            }
            catch (IOException) when (attempt < 5)
            {
                Thread.Sleep(50);
            }
        }
    }

    internal void ReloadFromDisk()
    {
        if (_disposed || !File.Exists(FilePath)) return;
        // Under the writer gate, so a reload and a write never interleave. Reading the file outside it and then replacing the
        // live document with what was read undid a Set that landed in between: the older file content became the live
        // settings until the next reload (a setting just changed, gone; seen as `agents.setEnabled` answering "not disabled").
        // Held, a reload sees either the file before a write (and the write then goes on top of it) or the file after it
        // (the text is our own, and nothing changes).
        lock (_writeGate)
        {
            string text;
            try { text = ReadText(); }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Cannot read {File}", FilePath);
                return;
            }
            lock (_gate)
            {
                if (text == _lastText) return; // our own write, or nothing changed
                _lastText = text;
                if (!TryParse(text, out var root, out var error))
                {
                    _invalidError = error;
                    _log.LogWarning("Ignoring invalid settings file {File}: {Error} (keeping the last valid settings; no write is saved over it until it parses)", FilePath, error);
                    return;
                }
                _invalidError = null;
                if (JsonNode.DeepEquals(_root, root)) return;
                _root = root;
            }
        }
        _log.LogInformation("Settings reloaded from {File}", FilePath);
        OnChanged(null, fromFile: true);
    }

    private void OnChanged(string? path, bool fromFile = false)
    {
        try { Changed?.Invoke(path); }
        catch (Exception ex) { _log.LogError(ex, "Settings change handler failed"); }
        var data = new JsonObject();
        if (path is not null) data["path"] = path;
        if (fromFile) data["source"] = "file";
        _bus?.Publish(new BusEvent { Type = EventTypes.SettingsChanged, Data = data, Source = "host" });
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _watcher?.Dispose();
            _watcher = null;
            _reload?.Dispose();
        }
    }
}

/// <summary>
/// The settings document written on first run: the core's own keys only. Nothing about one machine (no default model, no
/// agents; they are set up in the app) and nothing a plugin owns: each plugin declares its defaults in its settings schema.
/// </summary>
internal static class DefaultSettings
{
    public static JsonObject Create() => new()
    {
        ["tools"] = new JsonObject { ["disabled"] = new JsonArray() },
        ["plugins"] = new JsonObject { ["disabled"] = new JsonArray(), ["dirs"] = new JsonArray(), ["quiet"] = Rpc.CoreSettings.QuietDefault },
        ["server"] = new JsonObject { ["port"] = 7431, ["devOrigins"] = new JsonArray() },
        ["ui"] = new JsonObject(),
    };
}
