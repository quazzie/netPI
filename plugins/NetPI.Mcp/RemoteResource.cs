using System.Text.Json.Nodes;

namespace NetPI.Mcp;

/// <summary>
/// One resource a server advertises. Only the metadata travels: a resource is read on demand, through
/// <c>resources/read</c>, and only for a URI the server itself listed.
/// </summary>
internal sealed class RemoteResource
{
    public string ServerId { get; }
    public string Uri { get; }
    public string Name { get; }
    public string? MimeType { get; }
    public string Description { get; }
    public string SearchText { get; }

    public RemoteResource(ServerConfig config, JsonObject raw)
    {
        ServerId = config.Id;
        if (raw["uri"] is not JsonValue u || !u.TryGetValue<string>(out var uri) || string.IsNullOrWhiteSpace(uri) || uri.Length > 2048
            || uri.Any(char.IsControl))
            throw new McpException("MCP resource entry has an invalid uri.");
        Uri = uri;
        // A server that omits the name still gets a usable label: the last segment of the URI.
        Name = raw["name"] is JsonValue n && n.TryGetValue<string>(out var name) && !string.IsNullOrWhiteSpace(name) && name.Length <= 256
            ? name : Tail(uri);
        MimeType = raw["mimeType"] is JsonValue m && m.TryGetValue<string>(out var mime) && mime.Length <= 256 ? mime : null;
        Description = raw["description"]?.GetValue<string>() ?? raw["title"]?.GetValue<string>() ?? Name;
        if (Description.Length > 400) Description = Description[..400] + "…";
        SearchText = string.Join(' ', config.Id, Uri, Name, Description, MimeType, string.Join(' ', config.Synonyms));
    }

    /// <summary>The last path segment of a URI, for a server that named its resource nothing.</summary>
    private static string Tail(string uri)
    {
        var end = uri.LastIndexOfAny(['/', '?', '#']);
        var tail = end >= 0 ? uri[(end + 1)..] : uri;
        return tail.Length == 0 ? uri : tail.Length <= 40 ? tail : tail[^40..];
    }

    public JsonObject Summary(bool available) => new()
    {
        ["kind"] = "resource", ["uri"] = Uri, ["server"] = ServerId, ["name"] = Name,
        ["description"] = Description, ["mimeType"] = MimeType, ["available"] = available,
    };
}
