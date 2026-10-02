using System.Text.Json;
using System.Text.Json.Nodes;
using NetPI;

namespace NetPI.Migration001;

/// <summary>
/// The abort of a run before anything has been written: the database at the home does not look like the old shape,
/// or it already looks like the new one. Carries the reason for the exit message.
/// </summary>
public sealed class MigrationAbortedException(string reason) : Exception(reason);

/// <summary>
/// Read-only <see cref="ISettings"/> for the storage provider: the sqlite provider asks only for <c>database.sqlitePath</c>
/// (a custom native library), and that is worth keeping identical to the running app's, so the tool reads the home's
/// <c>settings.json</c> if it can parse it. It never writes it.
/// </summary>
internal sealed class MigrationSettings : ISettings
{
    private readonly JsonObject _root;

    private MigrationSettings(JsonObject root) => _root = root;

    public static MigrationSettings For(string home)
    {
        try
        {
            var text = File.ReadAllText(Path.Combine(home, "settings.json"));
            var doc = System.Text.Json.JsonDocument.Parse(text, new System.Text.Json.JsonDocumentOptions
            {
                CommentHandling = System.Text.Json.JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
            return new MigrationSettings(doc.RootElement.Clone().Deserialize<JsonObject>() ?? new JsonObject());
        }
        catch { return new MigrationSettings(new JsonObject()); }
    }

    public string FilePath => "migration (read-only)";

    public bool InvalidOnDisk => false;

    public string? InvalidOnDiskError => null;

    public JsonObject Snapshot() => _root.DeepClone() as JsonObject ?? new JsonObject();

    public JsonNode? GetNode(string path)
    {
        JsonNode? node = _root;
        foreach (var part in path.Split('.'))
            node = node is JsonObject { } o ? o[part] : null;
        return node;
    }

    public T? Get<T>(string path, T? defaultValue = default)
    {
        try { return GetNode(path) is JsonValue v ? v.GetValue<T>() : defaultValue; }
        catch { return defaultValue; }
    }

    public void Set(string path, JsonNode? value) => throw new NotSupportedException("The migration settings are read-only");

    public void Replace(JsonObject root) => throw new NotSupportedException("The migration settings are read-only");
}

/// <summary>Small JSON helpers shared by the copy steps (a corrupt value is carried as-is, never guessed at).</summary>
internal static class Jsonx
{
    public static JsonObject? Object(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        try { return JsonNode.Parse(text) as JsonObject; }
        catch (System.Text.Json.JsonException) { return null; }
    }

    /// <summary>The tools column is a JSON array string; a value that does not parse is kept raw (as a string node).</summary>
    public static JsonNode? ArrayOrRaw(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        try { return JsonNode.Parse(text); }
        catch (System.Text.Json.JsonException) { return JsonValue.Create(text!); }
    }
}

