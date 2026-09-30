using System.Text.Json.Nodes;
namespace NetPI.Mcp;

/// <summary>A modern stream is bound to one listen request and begins with an acknowledged subscription.</summary>
internal sealed class Subscription
{
    private bool _acknowledged;
    private long _window = Environment.TickCount64;
    private int _count;
    public bool Accept(JsonObject message, string id)
    {
        if (Environment.TickCount64 - _window >= 1000) { _window = Environment.TickCount64; _count = 0; }
        if (++_count > 1024) throw new McpException("MCP notification traffic exceeds 1024 messages per second.");
        if (message["jsonrpc"]?.GetValue<string>() != "2.0") throw new McpException("Invalid MCP notification JSON-RPC version.");
        var method = message["method"]?.GetValue<string>();
        if (method == "notifications/cancelled" && Protocol.Id(message["params"]?["requestId"]) == id)
            throw new McpException("MCP subscription was cancelled by the server.");
        if (Protocol.Id(message["params"]?["_meta"]?[Protocol.MetaPrefix + "subscriptionId"]) != id) return false;
        if (method == "notifications/subscriptions/acknowledged")
        {
            if (_acknowledged || message["params"]?["notifications"]?["toolsListChanged"]?.GetValue<bool>() != true)
                throw new McpException("Invalid MCP subscription acknowledgement.");
            _acknowledged = true; return false;
        }
        if (!_acknowledged) throw new McpException("MCP subscription notification arrived before acknowledgement.");
        return method == "notifications/tools/list_changed";
    }
}
