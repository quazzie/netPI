using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace NetPI.Host.Plugins;

/// <summary>Optional <c>plugin.json</c>: <c>{ id, name, description, assembly, enabled, order }</c>.</summary>
internal sealed class PluginManifest
{
    public string? Id { get; init; }
    public string? Name { get; init; }
    public string? Description { get; init; }
    public string? Assembly { get; init; }
    public bool Enabled { get; init; } = true;
    public int? Order { get; init; }

    public static PluginManifest? TryLoad(string file, ILogger log)
    {
        try
        {
            var text = File.ReadAllText(file);
            if (JsonNode.Parse(text, null, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })
                is not JsonObject o)
                return null;
            return new PluginManifest
            {
                Id = Str(o, "id"),
                Name = Str(o, "name"),
                Description = Str(o, "description"),
                Assembly = Str(o, "assembly"),
                Enabled = o["enabled"] is JsonValue ev && ev.TryGetValue<bool>(out var en) ? en : true,
                Order = o["order"] is JsonValue ov && ov.TryGetValue<int>(out var order) ? order : null,
            };
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            log.LogWarning("Cannot read {File}: {Error}", file, ex.Message);
            return null;
        }
    }

    private static string? Str(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;
}
