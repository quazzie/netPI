using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace NetPI.Host.Settings;

/// <summary>
/// <see cref="ISettings"/> backed by <c>settings.json</c> (comments and trailing commas allowed). Values are read and
/// written by dotted path. Writes are atomic (temp file + move). External edits are picked up by a debounced
/// FileSystemWatcher; invalid JSON is logged and the last good document is kept. Every change publishes
/// <c>settings.changed</c> (<c>{ path }</c> for single-value writes, <c>{ source: "file" }</c> for external edits).
/// </summary>
internal sealed class SettingsStore : ISettings, IDisposable
{
    private static readonly JsonDocumentOptions DocOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    private readonly Lock _gate = new();
    private readonly ILogger _log;
    private JsonObject _root;
    private string? _lastText;
    private IEventBus? _bus;
    private FileSystemWatcher? _watcher;
    private Debouncer? _reload;
    private bool _disposed;

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
                _log.LogError("Settings file {File} is not valid JSON ({Error}); using defaults until it is fixed", filePath, error);
                _root = DefaultSettings.Create();
            }
        }
        else
        {
            _root = DefaultSettings.Create();
            lock (_gate) Save();
            _log.LogInformation("Created default settings at {File}", filePath);
        }
    }

    public string FilePath { get; }

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
        lock (_gate)
        {
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
                parent.Remove(key);
            }
            else
            {
                if (existing is not null && JsonNode.DeepEquals(existing, value)) return;
                parent[key] = value.DeepClone();
            }
            Save();
        }
        OnChanged(path);
    }

    public void Replace(JsonObject root)
    {
        ArgumentNullException.ThrowIfNull(root);
        var clone = (JsonObject)root.DeepClone();
        lock (_gate)
        {
            if (JsonNode.DeepEquals(_root, clone)) return;
            _root = clone;
            Save();
        }
        OnChanged(null);
    }

    // ------------------------------------------------------------------ internals

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

    /// <summary>Atomic write of the current document. Caller holds the lock.</summary>
    private void Save()
    {
        var text = _root.ToJsonString(NetPiJson.Indented) + Environment.NewLine;
        _lastText = text;
        var tmp = FilePath + ".tmp";
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                File.WriteAllText(tmp, text, Utf8NoBom);
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

    private void ReloadFromDisk()
    {
        if (_disposed || !File.Exists(FilePath)) return;
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
                _log.LogWarning("Ignoring invalid settings file {File}: {Error} (keeping the last valid settings)", FilePath, error);
                return;
            }
            if (JsonNode.DeepEquals(_root, root)) return;
            _root = root;
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

/// <summary>The settings document written on first run: nothing about one machine (no default model, no agents; they are set up in the app).</summary>
internal static class DefaultSettings
{
    public static JsonObject Create() => new()
    {
        ["providers"] = new JsonObject
        {
            ["aiproxy"] = new JsonObject { ["baseUrl"] = "http://127.0.0.1:8090", ["transport"] = "responses" },
            ["anthropic"] = new JsonObject { ["apiKey"] = "" },
        },
        ["compaction"] = new JsonObject { ["enabled"] = true },
        ["nudge"] = new JsonObject { ["enabled"] = true },
        ["retry"] = new JsonObject { ["maxAttempts"] = 6 },
        ["tools"] = new JsonObject { ["disabled"] = new JsonArray() },
        ["plugins"] = new JsonObject { ["disabled"] = new JsonArray(), ["dirs"] = new JsonArray(), ["quiet"] = false },
        ["server"] = new JsonObject { ["port"] = 7431, ["devOrigins"] = new JsonArray() },
        ["ui"] = new JsonObject(),
    };
}
