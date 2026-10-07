using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace NetPI.Providers.ClaudeCli;

/// <summary>
/// NetPI's own tools for a Claude CLI run, served to the CLI as an MCP server on NetPI's loopback port (idea-0qo9xb). Each
/// agent turn of the CLI that is offered NetPI tools gets a run here, with a token of its own that the CLI receives through
/// a temporary <c>--mcp-config</c> file (never its arguments or environment), and only that run's tools. A call the CLI
/// makes comes back through <see cref="IToolRelay"/>, so the runtime runs it like one of its own: the guard and its
/// approvals, plan mode, the workspace guard, live output under the id the CLI streams the call with. The CLI reports the
/// call in its stream as <c>mcp__netpi__&lt;tool&gt;</c>; <see cref="ClaudeCliStream"/> records it under the NetPI tool's
/// own name, with the details the tool returned (mcp_search's disclosure is what a later mcp_call checks).
/// </summary>
internal sealed class NetpiMcp(Func<IToolRelay?> relay, ILogger log)
{
    /// <summary>The server's name in the CLI's MCP config: its tools are <c>mcp__netpi__&lt;name&gt;</c> there.</summary>
    public const string ServerName = "netpi";
    public const string ToolPrefix = "mcp__" + ServerName + "__";
    /// <summary>The path under the plugin's HTTP root: <c>/api/p/netpi.providers.claudecli/mcp</c>.</summary>
    public const string Path = "mcp";
    private static readonly string[] Versions = ["2024-11-05", "2025-03-26", "2025-06-18", "2025-11-25"];

    /// <summary>One CLI run's view: whose chat it is, the tools it was offered, and the stream that reports its calls.</summary>
    internal sealed class Run
    {
        public required string SessionId { get; init; }
        public required IReadOnlyList<ToolDefinition> Tools { get; init; }
        public required ClaudeCliStream Stream { get; init; }
    }

    private readonly ConcurrentDictionary<string, Run> _runs = new(StringComparer.Ordinal);

    /// <summary>Open a run: its token (for the CLI's config), and the registration that ends it with the CLI call.</summary>
    public (string Token, IDisposable Registration) Open(Run run)
    {
        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));
        _runs[token] = run;
        return (token, new Closer(() => _runs.TryRemove(token, out _)));
    }

    internal int OpenRuns => _runs.Count;

    /// <summary>
    /// The CLI's MCP config for a run: one HTTP server, its URL on NetPI's loopback port, its token in a header. With
    /// <c>--strict-mcp-config</c> it is the only server the CLI loads, so no upstream server's URL or secret reaches it.
    /// </summary>
    public static JsonObject Config(string serverUrl, string token) => new()
    {
        ["mcpServers"] = new JsonObject
        {
            [ServerName] = new JsonObject
            {
                ["type"] = "http",
                ["url"] = serverUrl.TrimEnd('/') + "/api/p/" + ClaudeCliPlugin.PluginId + "/" + Path,
                ["headers"] = new JsonObject { ["Authorization"] = "Bearer " + token },
            },
        },
    };

    /// <summary>The HTTP endpoint (Streamable HTTP, JSON answers only: nothing here pushes to the CLI on its own).</summary>
    public async Task HandleAsync(HttpContext http)
    {
        if (HttpMethods.IsDelete(http.Request.Method)) { http.Response.StatusCode = StatusCodes.Status200OK; return; }
        if (!HttpMethods.IsPost(http.Request.Method))
        {
            http.Response.Headers.Allow = "POST, DELETE";
            http.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            return;
        }
        var auth = http.Request.Headers.Authorization.ToString();
        if (!auth.StartsWith("Bearer ", StringComparison.Ordinal) || !_runs.TryGetValue(auth["Bearer ".Length..].Trim(), out var run))
        {
            http.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }
        JsonNode? body;
        try { body = await JsonNode.ParseAsync(http.Request.Body, cancellationToken: http.RequestAborted).ConfigureAwait(false); }
        catch (JsonException) { await WriteAsync(http, Error(null, -32700, "Parse error")).ConfigureAwait(false); return; }

        JsonNode? answer;
        if (body is JsonArray batch)
        {
            var answers = new JsonArray();
            foreach (var item in batch)
                if (await HandleMessageAsync(run, item as JsonObject, http.RequestAborted).ConfigureAwait(false) is { } one) answers.Add(one);
            answer = answers.Count > 0 ? answers : null;
        }
        else answer = await HandleMessageAsync(run, body as JsonObject, http.RequestAborted).ConfigureAwait(false);

        if (answer is null) { http.Response.StatusCode = StatusCodes.Status202Accepted; return; }
        await WriteAsync(http, answer).ConfigureAwait(false);
    }

    private static async Task WriteAsync(HttpContext http, JsonNode answer)
    {
        http.Response.StatusCode = StatusCodes.Status200OK;
        http.Response.ContentType = "application/json";
        await http.Response.WriteAsync(answer.ToJsonString(), http.RequestAborted).ConfigureAwait(false);
    }

    /// <summary>One JSON-RPC message: the answer to a request, or null for a notification or a response.</summary>
    internal async Task<JsonObject?> HandleMessageAsync(Run run, JsonObject? message, CancellationToken ct)
    {
        if (message is null) return Error(null, -32600, "Invalid request");
        var id = message["id"]?.DeepClone();
        var method = message["method"] is JsonValue m && m.TryGetValue<string>(out var name) ? name : null;
        if (method is null) return id is null ? null : Error(id, -32600, "Invalid request");
        if (id is null) return null; // notifications/initialized and the like
        var p = message["params"] as JsonObject;
        switch (method)
        {
            case "initialize":
            {
                var asked = p?["protocolVersion"] is JsonValue v && v.TryGetValue<string>(out var ver) ? ver : null;
                return Result(id, new JsonObject
                {
                    ["protocolVersion"] = asked is not null && Versions.Contains(asked) ? asked : "2025-06-18",
                    ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
                    ["serverInfo"] = new JsonObject { ["name"] = ServerName, ["version"] = "1" },
                    ["instructions"] = "NetPI's own tools for this chat. They run inside NetPI: its guard can block a call or ask the user first, and plan mode lets them only read.",
                });
            }
            case "ping":
                return Result(id, new JsonObject());
            case "tools/list":
                return Result(id, new JsonObject
                {
                    ["tools"] = new JsonArray([.. run.Tools.Select(t => (JsonNode)new JsonObject
                    {
                        ["name"] = t.Name,
                        ["description"] = string.IsNullOrWhiteSpace(t.Description) ? t.Label ?? t.Name : t.Description,
                        ["inputSchema"] = t.Parameters.DeepClone(),
                    })]),
                });
            case "tools/call":
                return Result(id, await CallAsync(run, p, ct).ConfigureAwait(false));
            default:
                return Error(id, -32601, "Method not found: " + method);
        }
    }

    private async Task<JsonObject> CallAsync(Run run, JsonObject? p, CancellationToken ct)
    {
        var name = p?["name"] is JsonValue n && n.TryGetValue<string>(out var s) ? s : null;
        var tool = name is null ? null : run.Tools.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.Ordinal));
        if (tool is null) return Text($"NetPI does not offer the tool \"{name}\" to this run.", isError: true);
        var arguments = p?["arguments"] is JsonObject a ? a.ToJsonString() : "{}";
        // the id the CLI streams this call with, so its live output and its record land on the row the stream opened
        var callId = await run.Stream.TakeRelayIdAsync(tool.Name, arguments, ct).ConfigureAwait(false) ?? "netpi_" + Guid.NewGuid().ToString("N")[..16];
        if (relay() is not { } runtime) return Text("NetPI's runtime is not available: the call was not run.", isError: true);
        ToolResult? result;
        try { result = await runtime.RunAsync(run.SessionId, new ToolCallPart { Id = callId, Name = tool.Name, Arguments = arguments }, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return Text("The call was stopped.", isError: true); }
        catch (Exception ex)
        {
            log.LogWarning(ex, "claude-cli: the relayed call to {Tool} failed", tool.Name);
            return Text($"Tool '{tool.Name}' failed: {ex.Message}", isError: true);
        }
        if (result is null) return Text("This chat has no model call in flight any more: the call was not run.", isError: true);
        if (result.Details is { } details && NetPiJson.ToNode(details) is JsonObject kept) run.Stream.KeepRelayDetails(callId, kept);
        var content = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = result.Content ?? "" } };
        foreach (var image in result.Images ?? [])
            content.Add(new JsonObject { ["type"] = "image", ["data"] = image.Data, ["mimeType"] = image.MediaType });
        return new JsonObject { ["content"] = content, ["isError"] = result.IsError };
    }

    private static JsonObject Text(string text, bool isError) =>
        new() { ["content"] = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = text } }, ["isError"] = isError };

    private static JsonObject Result(JsonNode id, JsonObject result) => new() { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };

    private static JsonObject Error(JsonNode? id, int code, string message) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = code, ["message"] = message } };

    private sealed class Closer(Action close) : IDisposable
    {
        private int _done;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0) close();
        }
    }
}
