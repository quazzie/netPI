using System.Text.Json;
using System.Text.Json.Nodes;

namespace NetPI.Mcp;

internal sealed class McpException(string message, int? code = null, JsonObject? data = null) : Exception(message)
{
    public int? Code { get; } = code;
    public JsonObject? ErrorData { get; } = data;
}

internal static class Protocol
{
    public const string Modern = "2026-07-28";
    public const string Legacy = "2025-11-25";
    public const string MetaPrefix = "io.modelcontextprotocol/";
    public static JsonObject Message(string method, JsonObject? parameters, long? id, string version)
    {
        var p = parameters?.DeepClone().AsObject() ?? new JsonObject();
        if (version == Modern)
        {
            var meta = p["_meta"] as JsonObject ?? new JsonObject();
            meta[MetaPrefix + "protocolVersion"] = version;
            meta[MetaPrefix + "clientInfo"] = new JsonObject { ["name"] = "NetPI", ["version"] = "0.1.0" };
            meta[MetaPrefix + "clientCapabilities"] = new JsonObject();
            p["_meta"] = meta;
        }
        var message = new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method, ["params"] = p };
        if (id is not null) message["id"] = id.Value;
        return message;
    }
    public static JsonObject Parse(string text, int maxChars) =>
        text.Length > maxChars ? throw new McpException("MCP message exceeds the configured size limit.") :
        JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { MaxDepth = 64 }) as JsonObject
        ?? throw new McpException("MCP message must be a JSON object.");
    public static JsonObject Result(JsonObject message)
    {
        if (message["jsonrpc"]?.GetValue<string>() != "2.0") throw new McpException("Invalid MCP JSON-RPC version.");
        if (message["error"] is JsonObject error)
            throw new McpException(error["message"]?.GetValue<string>() ?? "MCP protocol error.",
                error["code"]?.GetValue<int>(), error["data"] as JsonObject);
        if (message["result"] is not JsonObject result) throw new McpException("Missing MCP response result.");
        if (result["resultType"]?.GetValue<string>() == "input_required")
            throw new McpException("This server requires a client interaction (sampling, elicitation or roots) that NetPI has not enabled. The tool was not replayed.");
        return result;
    }
    public static string Id(JsonNode? id) => id?.ToJsonString() ?? "";
    /// <summary>The reply to a server's <c>ping</c> request: an empty result, as the protocol requires.</summary>
    public static JsonObject Pong(JsonObject request) => new() { ["jsonrpc"] = "2.0", ["id"] = request["id"]!.DeepClone(), ["result"] = new JsonObject() };
    public static async Task<string?> ReadLineAsync(TextReader reader, int limit, CancellationToken ct)
    {
        var text = new System.Text.StringBuilder();
        var buffer = new char[1];
        while (await reader.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false) != 0)
        {
            if (buffer[0] == '\n') return text.ToString().TrimEnd('\r');
            if (text.Length >= limit) throw new McpException("MCP line exceeds the configured size limit.");
            text.Append(buffer[0]);
        }
        return text.Length == 0 ? null : text.ToString();
    }
}

internal interface IMcpTransport : IAsyncDisposable
{
    event Action<JsonObject>? Notification;
    event Action<Exception>? Closed;
    string Version { get; set; }
    Task<JsonObject> RequestAsync(string method, JsonObject? parameters, CancellationToken ct, JsonObject? headers = null);
    Task NotifyAsync(string method, JsonObject? parameters, CancellationToken ct);
    Task ListenAsync(bool modern, CancellationToken ct);
}
