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
    /// <summary>
    /// Replace the whole document (validated JSON object). With <paramref name="baseDocument"/> the document must still
    /// equal what the caller loaded before editing it; otherwise <see cref="SettingsReplace.Conflict"/> comes back
    /// and nothing changes. The compare and the write are one step, so two replacements racing on the same base
    /// cannot both win. <paramref name="baseDocument"/> may carry credentials: implementations compare it in memory only.
    /// </summary>
    SettingsReplace Replace(JsonObject root, JsonObject? baseDocument = null);
    /// <summary>
    /// True while <see cref="FilePath"/> does not parse: the store serves the last valid document (or first-run
    /// defaults) and <see cref="Set"/>/<see cref="Replace"/> throw until the file is fixed, so a broken file is never
    /// silently replaced. <see cref="InvalidOnDiskError"/> carries the parse error.
    /// </summary>
    bool InvalidOnDisk { get; }
    /// <summary>The settings file's parse error while <see cref="InvalidOnDisk"/>, else null.</summary>
    string? InvalidOnDiskError { get; }
}

/// <summary>The outcome of <see cref="ISettings.Replace"/> with a base document.</summary>
public enum SettingsReplace
{
    /// <summary>The document was saved (or was already equal: nothing was written).</summary>
    Saved,
    /// <summary>The document no longer equals the base: nothing was changed, the caller must reload and retry.</summary>
    Conflict,
}

