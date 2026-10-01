using System.Text;
using System.Text.Json.Nodes;

namespace NetPI.Mcp;

/// <summary>
/// Turns a <c>resources/read</c> result into model-facing text. Text becomes the content, bounded; binary stays out of
/// it entirely, the same promise a tool result makes. An oversized resource is written whole to a temp file and the
/// model is told to read that, rather than being handed a truncated document.
/// </summary>
internal static class ResourceReader
{
    public static ToolResult Convert(JsonObject result, RemoteResource resource, int maxChars, int maxBinary, string tempDir)
    {
        if (result["contents"] is not JsonArray contents)
            throw new McpException("MCP resources/read response is missing its contents array.");
        var text = new StringBuilder();
        var entries = new JsonArray();
        var binary = new JsonArray();
        var empty = true;
        foreach (var node in contents.OfType<JsonObject>())
        {
            var uri = node["uri"]?.GetValue<string>() ?? resource.Uri;
            var mime = node["mimeType"]?.GetValue<string>();
            if (node["text"] is { } value)
            {
                var entry = value.GetValue<string>();
                text.AppendLine(entry);
                entries.Add(new JsonObject { ["uri"] = uri, ["mimeType"] = mime, ["chars"] = entry.Length });
                empty = false;
            }
            else if (node["blob"] is { } blob)
            {
                // The payload is never decoded into the conversation and never stored: report the shape, not the bytes.
                var length = blob.GetValue<string>().Length;
                binary.Add(new JsonObject
                {
                    ["uri"] = uri, ["mimeType"] = mime,
                    ["base64Chars"] = length, ["approxBytes"] = length * 3 / 4,
                    ["note"] = "Binary resource; its content was not read into the model context.",
                });
                text.AppendLine($"[Binary resource {uri}{(mime is null ? "" : " (" + mime + ")")} was not read into text. Use the server's own tool if you need its contents.]");
                empty = false;
            }
            else entries.Add(new JsonObject { ["uri"] = uri, ["mimeType"] = mime, ["empty"] = true });
        }
        if (empty) return ToolResult.Error($"MCP resource {resource.Uri} returned no content.", new JsonObject { ["kind"] = "mcp-resource", ["serverId"] = resource.ServerId, ["uri"] = resource.Uri });

        var whole = text.ToString().TrimEnd();
        JsonObject details = new()
        {
            ["kind"] = "mcp-resource", ["serverId"] = resource.ServerId, ["uri"] = resource.Uri,
            ["name"] = resource.Name, ["contents"] = entries, ["binary"] = binary,
        };
        if (whole.Length <= maxChars)
        {
            details["chars"] = whole.Length;
            return new ToolResult { Content = whole, Details = details };
        }
        // Too big for the context: keep it whole on disk, the way an oversized schema is kept.
        var directory = Path.Combine(tempDir, "mcp-resources");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, RemoteTool.Hash(resource.ServerId + "\0" + resource.Uri)[..24] + ".txt");
        File.WriteAllText(path, whole, new UTF8Encoding(false));
        details["chars"] = whole.Length; details["file"] = path;
        return new ToolResult
        {
            Content = whole[..Math.Min(2000, maxChars)] + $"\n\n[Truncated: {whole.Length} characters. The complete resource is at {path} — read that file instead.]",
            Details = details,
        };
    }
}
