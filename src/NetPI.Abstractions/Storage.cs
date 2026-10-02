using System.Text.Json.Nodes;

namespace NetPI;

/// <summary>
/// User settings (~/.netpi/settings.json). Paths are dotted: "providers.aiproxy.baseUrl".
/// Changes are persisted immediately and published as <c>settings.changed</c>.
/// </summary>
public interface ISettings
{
    string FilePath { get; }
    /// <summary>A deep clone of the whole settings document.</summary>
    JsonObject Snapshot();
    JsonNode? GetNode(string path);
    T? Get<T>(string path, T? defaultValue = default);
    void Set(string path, JsonNode? value);
    /// <summary>Replace the whole document (validated JSON object).</summary>
    void Replace(JsonObject root);
    /// <summary>
    /// True while <see cref="FilePath"/> does not parse: the store serves the last valid document (or first-run
    /// defaults) and <see cref="Set"/>/<see cref="Replace"/> throw until the file is fixed, so a broken file is never
    /// silently replaced. <see cref="InvalidOnDiskError"/> carries the parse error.
    /// </summary>
    bool InvalidOnDisk { get; }
    /// <summary>The settings file's parse error while <see cref="InvalidOnDisk"/>, else null.</summary>
    string? InvalidOnDiskError { get; }
}

