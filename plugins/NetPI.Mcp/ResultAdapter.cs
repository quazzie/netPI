using System.Text;
using System.Text.Json.Nodes;

namespace NetPI.Mcp;

internal static class ResultAdapter
{
    public static ToolResult Convert(JsonObject result, string serverId, string tool, int maxBinary)
    {
        var text = new StringBuilder();
        var images = new List<ImagePart>();
        var metadata = new JsonArray();
        if (result["content"] is JsonArray content)
            foreach (var block in content.OfType<JsonObject>())
            {
                var type = block["type"]?.GetValue<string>();
                switch (type)
                {
                    case "text": text.AppendLine(block["text"]?.GetValue<string>()); break;
                    case "image":
                        var data = block["data"]?.GetValue<string>() ?? "";
                        if (data.Length > maxBinary * 4L / 3 + 4) throw new McpException("MCP image exceeds the binary size limit.");
                        var bytes = System.Convert.FromBase64String(data);
                        if (bytes.Length > maxBinary) throw new McpException("MCP image exceeds the binary size limit.");
                        images.Add(new ImagePart { MediaType = block["mimeType"]?.GetValue<string>() ?? "image/png", Data = data });
                        metadata.Add(new JsonObject { ["type"] = "image", ["mimeType"] = images[^1].MediaType, ["bytes"] = bytes.Length });
                        break;
                    case "resource":
                        if (block["resource"] is JsonObject resource && resource["text"] is { } embedded) text.AppendLine(embedded.GetValue<string>());
                        else text.AppendLine("[Embedded binary resource is not supported by this model.]");
                        metadata.Add(RemoveData(block));
                        break;
                    case "resource_link":
                        text.AppendLine($"Resource: {block["name"]?.GetValue<string>()} {block["uri"]?.GetValue<string>()}");
                        metadata.Add(block.DeepClone()); break;
                    default:
                        text.AppendLine($"[MCP {type ?? "unknown"} content is available as metadata; it is not supported by this model.]");
                        metadata.Add(RemoveData(block)); break;
                }
            }
        if (result["structuredContent"] is { } structured)
        {
            var json = structured.ToJsonString();
            if (!text.ToString().Contains(json, StringComparison.Ordinal)) text.AppendLine(json);
        }
        return new ToolResult
        {
            Content = text.ToString().TrimEnd(), Images = images.Count == 0 ? null : images,
            IsError = result["isError"]?.GetValue<bool>() == true,
            Details = new JsonObject { ["kind"] = "mcp", ["serverId"] = serverId, ["tool"] = tool,
                ["structuredContent"] = result["structuredContent"]?.DeepClone(), ["content"] = metadata },
        };
    }
    private static JsonNode RemoveData(JsonObject block)
    {
        var copy = block.DeepClone().AsObject();
        copy.Remove("data");
        if (copy["resource"] is JsonObject resource) resource.Remove("blob");
        return copy;
    }
}
