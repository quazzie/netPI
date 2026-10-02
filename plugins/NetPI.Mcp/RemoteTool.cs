using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace NetPI.Mcp;

internal sealed class RemoteTool : IAgentTool
{
    private readonly ServerManager _manager;
    public string ServerId { get; }
    public string RemoteName { get; }
    public string SearchText { get; }
    public JsonObject Raw { get; }
    public ToolDefinition Definition { get; }
    public RemoteTool(ServerManager manager, ServerConfig config, JsonObject raw)
    {
        _manager = manager; ServerId = config.Id; Raw = raw.DeepClone().AsObject();
        RemoteName = raw["name"]!.GetValue<string>();
        var schema = raw["inputSchema"] as JsonObject ?? throw new McpException("Tool inputSchema must be a JSON object.");
        Schema.Check(schema, config.Transport == "http");
        var description = raw["description"]?.GetValue<string>() ?? RemoteName;
        var title = raw["title"]?.GetValue<string>() ?? RemoteName;
        var revisionInput = new JsonObject { ["tool"] = raw.DeepClone(), ["pinned"] = config.Pinned.Contains(RemoteName),
            ["readOnly"] = config.ReadOnly.Contains(RemoteName) };
        Definition = new ToolDefinition
        {
            Name = Name(config.Id, RemoteName), Category = "mcp", Label = config.Id + ": " + title,
            Description = "[" + config.Id + "] " + (description.Length > 240 ? description[..240] + "…" : description),
            Help = description, Parameters = schema.DeepClone().AsObject(),
            Deferred = !config.Pinned.Contains(RemoteName, StringComparer.Ordinal),
            ReadOnly = config.ReadOnly.Contains(RemoteName, StringComparer.Ordinal) || DeclaredReadOnly(raw),
            Revision = Hash(Canonical(revisionInput)),
        };
        SearchText = config.Id + " " + RemoteName + " " + title + " " + description + " " + string.Join(' ', config.Synonyms) + " " + schema.ToJsonString();
    }
    /// <summary>The server's own claim that the tool only reads (<c>annotations.readOnlyHint</c>); a tool that says nothing is not read-only.</summary>
    internal static bool DeclaredReadOnly(JsonObject raw) =>
        raw["annotations"]?["readOnlyHint"] is JsonValue hint && hint.TryGetValue<bool>(out var readOnly) && readOnly;
    internal static string Name(string server, string tool) => "mcp_" + Slug(server, 12) + "_" + Slug(tool, 24) + "_" + Hash(server + "\0" + tool)[..16];
    private static string Slug(string text, int length)
    {
        var slug = Regex.Replace(text.ToLowerInvariant(), "[^a-z0-9]+", "_").Trim('_');
        if (slug.Length == 0) slug = "tool";
        return slug[..Math.Min(length, slug.Length)];
    }
    internal static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static string Canonical(JsonNode? node) => node switch
    {
        JsonObject o => "{" + string.Join(",", o.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => JsonSerializer.Serialize(p.Key) + ":" + Canonical(p.Value))) + "}",
        JsonArray a => "[" + string.Join(",", a.Select(Canonical)) + "]",
        _ => node?.ToJsonString() ?? "null",
    };
    public Task<ToolResult> ExecuteAsync(ToolContext context, JsonElement args, CancellationToken ct) =>
        _manager.CallAsync(this, args, ct);
    public JsonObject Summary(bool available) => new()
    {
        ["id"] = Definition.Name, ["server"] = ServerId, ["name"] = RemoteName,
        ["description"] = Definition.Description, ["available"] = available,
    };
}
