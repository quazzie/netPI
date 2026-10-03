using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace NetPI.Mcp;

/// <summary>
/// Reads one resource the server advertises. The URI comes from <c>mcp_search</c> and is the whole gate: a URI the
/// server never listed is not requested, so a link that merely appears in a tool result is never fetched.
/// </summary>
internal sealed class McpResourceTool(IPluginContext ctx, ServerManager manager) : IAgentTool
{
    public ToolDefinition Definition { get; } = new()
    {
        Name = "mcp_resource", Label = "MCP resource", Category = "mcp", ReadOnly = true, SummaryArg = "uri",
        Description = "Read one remote resource listed by mcp_search (a document, a skill, a reference file), by its exact uri.",
        Parameters = JsonNode.Parse(
            """
            {"type":"object","properties":{"uri":{"type":"string","description":"Exact uri from mcp_search."},
             "server":{"type":"string","description":"Server id, when the same uri is advertised by more than one."}},
             "required":["uri"],"additionalProperties":false}
            """)!.AsObject(),
    };

    public Task<ToolResult> ExecuteAsync(ToolContext context, JsonElement args, CancellationToken ct)
    {
        var a = new ToolArgs(args);
        var uri = a.Str("uri")?.Trim();
        if (string.IsNullOrWhiteSpace(uri) || uri.Length > 2048) return Task.FromResult(ToolResult.Error("uri must be an exact resource uri from mcp_search (1–2048 characters)."));
        var server = a.Str("server");
        var resource = manager.FindResource(uri, server)
            ?? throw new McpException("MCP resource " + uri + " is not advertised by " + (server ?? "any connected server")
                + ". Find it with mcp_search first; only listed resources can be read.");
        ctx.Logger.LogDebug("MCP {Server}: reading resource {Uri}", resource.ServerId, resource.Uri);
        return manager.ReadResourceAsync(resource, ct);
    }
}
