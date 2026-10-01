using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NetPI.Mcp;

internal sealed class McpConnection : IAsyncDisposable
{
    public IMcpTransport Transport { get; }
    public bool Modern => Transport.Version == Protocol.Modern;
    public bool ListChanged { get; private set; }
    public bool HasTools { get; private set; }
    public bool HasResources { get; private set; }
    public bool ResourceListChanged { get; private set; }
    /// <summary>The server offers resource subscriptions, which need a server-&gt;client request path NetPI does not open.</summary>
    public bool ResourceSubscribe { get; private set; }
    private readonly ServerConfig _config;
    public McpConnection(ServerConfig config, int limit, Action<string> log)
    {
        _config = config;
        Transport = config.Transport == "stdio" ? new StdioTransport(config, limit, log) : new HttpTransport(config, limit);
    }
    public async Task InitializeAsync(CancellationToken ct)
    {
        JsonObject? discovery = null;
        try
        {
            using var probe = CancellationTokenSource.CreateLinkedTokenSource(ct);
            probe.CancelAfter(Math.Max(100, Math.Min(1000, _config.ConnectTimeoutMs / 3)));
            discovery = await Transport.RequestAsync("server/discover", null, probe.Token).ConfigureAwait(false);
        }
        catch (McpException ex) when (ex.Code == -32022)
        {
            // Recognized modern version errors never downgrade to initialize.
            throw new McpException("Server speaks modern MCP but does not support NetPI's modern revision.", ex.Code, ex.ErrorData);
        }
        catch (McpException ex) when (ex.Code is -32600 or -32601 or -32602) { }
        catch (McpHttpException ex) when (ex.Status == HttpStatusCode.BadRequest) { }
        // A probe that runs out of time (a slow-resolving hostname, a loaded server) is not a failure: fall through to
        // initialize. Only the caller's own cancellation stops here.
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        if (discovery is not null)
        {
            if (discovery["supportedVersions"] is not JsonArray versions || !versions.Any(v => v?.GetValue<string>() == Protocol.Modern))
                throw new McpException("No mutually supported modern MCP revision.");
            SetCapabilities(discovery);
            return;
        }
        Transport.Version = Protocol.Legacy;
        var init = await Transport.RequestAsync("initialize", new JsonObject
        {
            ["protocolVersion"] = Protocol.Legacy, ["capabilities"] = new JsonObject(),
            ["clientInfo"] = new JsonObject { ["name"] = "NetPI", ["version"] = "0.1.0" },
        }, ct).ConfigureAwait(false);
        if (init["protocolVersion"]?.GetValue<string>() != Protocol.Legacy)
            throw new McpException("Unsupported negotiated MCP revision. NetPI supports 2026-07-28 and 2025-11-25.");
        SetCapabilities(init);
        await Transport.NotifyAsync("notifications/initialized", null, ct).ConfigureAwait(false);
    }
    private void SetCapabilities(JsonObject response)
    {
        // Tools are not required: a server may publish only resources (a skills library, documents).
        var capabilities = response["capabilities"] as JsonObject ?? new JsonObject();
        var tools = capabilities["tools"] as JsonObject;
        var resources = capabilities["resources"] as JsonObject;
        HasTools = tools is not null;
        ListChanged = tools?["listChanged"]?.GetValue<bool>() == true;
        HasResources = resources is not null;
        ResourceListChanged = resources?["listChanged"]?.GetValue<bool>() == true;
        ResourceSubscribe = resources?["subscribe"]?.GetValue<bool>() == true;
        if (!HasTools && !HasResources) throw new McpException("MCP server advertises neither tools nor resources.");
    }
    public Task<List<JsonObject>> ListAsync(int maxTools, int maxChars, CancellationToken ct) =>
        HasTools ? PageAsync("tools/list", "tools", maxTools, maxChars, "tool", ct) : Task.FromResult(new List<JsonObject>());
    public Task<List<JsonObject>> ListResourcesAsync(int maxResources, int maxChars, CancellationToken ct) =>
        HasResources ? PageAsync("resources/list", "resources", maxResources, maxChars, "resource", ct) : Task.FromResult(new List<JsonObject>());
    private async Task<List<JsonObject>> PageAsync(string method, string key, int maxEntries, int maxChars, string noun, CancellationToken ct)
    {
        var list = new List<JsonObject>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        var chars = 0;
        do
        {
            var page = await Transport.RequestAsync(method, cursor is null ? null : new JsonObject { ["cursor"] = cursor }, ct).ConfigureAwait(false);
            if (page[key] is not JsonArray entries) throw new McpException("MCP " + method + " response is missing its " + key + " array.");
            foreach (var node in entries)
            {
                if (node is not JsonObject entry || entry["name"] is not JsonValue n || !n.TryGetValue<string>(out var name) || string.IsNullOrWhiteSpace(name) || name.Length > 128)
                    throw new McpException("MCP catalog contains an invalid " + noun + " name.");
                if (!names.Add(name)) throw new McpException("MCP catalog contains duplicate " + noun + " names.");
                chars += entry.ToJsonString().Length;
                if (list.Count >= maxEntries || chars > maxChars) throw new McpException("MCP catalog exceeds its configured limit.");
                list.Add(entry);
            }
            cursor = page["nextCursor"]?.GetValue<string>();
            if (cursor is not null && (!cursors.Add(cursor) || cursor.Length > 4096)) throw new McpException("MCP catalog returned a repeated or oversized cursor.");
        } while (cursor is not null);
        return list;
    }
    /// <summary>Reads one resource the server advertises. Nothing else is ever read: an unlisted URI is not fetched.</summary>
    public Task<JsonObject> ReadResourceAsync(string uri, CancellationToken ct)
    {
        if (!HasResources) throw new McpException("MCP server does not advertise resources.");
        return Transport.RequestAsync("resources/read", new JsonObject { ["uri"] = uri }, ct);
    }
    public Task<JsonObject> CallAsync(string name, JsonElement args, JsonObject schema, CancellationToken ct)
    {
        var arguments = JsonNode.Parse(args.GetRawText()) as JsonObject ?? throw new McpException("MCP arguments must be an object.");
        Schema.Validate(schema, arguments);
        var headers = Modern && _config.Transport == "http" ? Schema.Headers(schema, arguments) : null;
        return Transport.RequestAsync("tools/call", new JsonObject { ["name"] = name, ["arguments"] = arguments }, ct, headers);
    }
    public ValueTask DisposeAsync() => Transport.DisposeAsync();
}
