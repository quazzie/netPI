using System.Text.Json.Nodes;

namespace NetPI;

/// <summary>
/// The model providers the owner has added: <c>providers.instances</c> is an object keyed by provider id (the part before the
/// slash of a model reference), each entry <c>{ "type": "claude-cli", "name"?, … }</c> plus the keys its type documents.
/// It starts empty: no provider exists until one is added in the settings dialog. Each provider plugin offers its types
/// (<see cref="SettingsSection.ProviderType"/>) and runs one provider per entry of its type, live.
/// </summary>
public static class ProviderInstances
{
    public const string Path = "providers.instances";

    /// <summary>The settings path of one instance (or of one of its keys).</summary>
    public static string PathOf(string id, string? key = null) => key is null ? $"{Path}.{id}" : $"{Path}.{id}.{key}";

    /// <summary>An id is the provider part of a model reference, and a settings path segment.</summary>
    public static bool ValidId(string? id) =>
        !string.IsNullOrWhiteSpace(id) && id.All(c => char.IsLetterOrDigit(c) || c is '-' or '_');

    /// <summary>The ids and live entries of this type, in the order they were added.</summary>
    public static IReadOnlyList<(string Id, JsonObject Config)> Of(ISettings settings, string type, bool includeInvalidIds = false)
    {
        var list = new List<(string, JsonObject)>();
        if (settings.GetNode(Path) is JsonObject all)
            foreach (var (id, node) in all)
                if (node is JsonObject o && (includeInvalidIds || ValidId(id)) && string.Equals((o["type"] as JsonValue)?.GetValue<string>(), type, StringComparison.OrdinalIgnoreCase))
                    list.Add((id, o));
        return list;
    }

    /// <summary>One string key of the first instance of a type (what another plugin falls back to), or null.</summary>
    public static string? FirstString(ISettings settings, string type, string key)
    {
        foreach (var (_, config) in Of(settings, type))
            if (config[key] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s)) return s;
        return null;
    }
}
