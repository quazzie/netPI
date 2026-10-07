using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace NetPI.Mcp;

[NetPiPlugin("netpi.mcp", Name = "MCP", Description = "External tools with deferred discovery", Order = 80)]
public sealed class McpPlugin : INetPiPlugin
{
    private ServerManager? _manager;
    private readonly SemaphoreSlim _settings = new(1);
    public async Task StartAsync(IPluginContext ctx, CancellationToken ct)
    {
        var healthy = new HashSet<string>(StringComparer.Ordinal);
        if (ctx.Rpc.Exists("mcp.list"))
        {
            // Snapshot JSON immediately; never retain an old plugin-defined object.
            var snapshot = NetPiJson.ToNode(await ctx.Rpc.InvokeAsync("mcp.list", null, ct).ConfigureAwait(false));
            if (snapshot?["servers"] is JsonArray servers)
                foreach (var server in servers.OfType<JsonObject>())
                    if (server["status"]?.GetValue<string>() == "connected") healthy.Add(server["id"]!.GetValue<string>());
        }
        var manager = new ServerManager(ctx); _manager = manager;
        await manager.StartAsync(healthy, ct).ConfigureAwait(false);
        ctx.Tools.Register(new McpSearchTool(ctx, manager));
        ctx.Tools.Register(new McpCallTool(ctx, manager));
        ctx.Tools.Register(new McpResourceTool(ctx, manager));
        ctx.Services.Register(new SettingsSection
        {
            Id = "mcp", Title = "MCP", Group = "Tools", Order = 80,
            Settings =
            [
                SettingInfo.Int("mcp.discoveryChars", "Discovery result limit", 4000, "Characters per search/schema result; larger schemas are saved as files.", 1024, 20000, "chars"),
                SettingInfo.Int("mcp.maxMessageChars", "Largest protocol message", 4_000_000, "Reject oversized frames and SSE events.", 4096, 16_000_000, "chars"),
                SettingInfo.Int("mcp.maxCatalogTools", "Largest tool catalog", 10000, "Maximum tools discovered per server.", 1, 10000, "tools"),
                SettingInfo.Int("mcp.maxCatalogChars", "Largest tool catalog data", 8_000_000, "Bound discovery memory per server.", 4096, 32_000_000, "chars"),
                SettingInfo.Int("mcp.maxResourceEntries", "Largest resource catalog", 2000, "Maximum resources discovered per server.", 1, 20000, "resources"),
                SettingInfo.Int("mcp.maxResourceCatalogChars", "Largest resource catalog data", 2_000_000, "Bound resource discovery memory per server.", 4096, 16_000_000, "chars"),
                SettingInfo.Int("mcp.maxResourceChars", "Largest resource read", 20000, "Characters per resource read; a larger one is written to a file.", 1024, 200000, "chars"),
                SettingInfo.Int("mcp.maxBinaryBytes", "Largest image", 2_000_000, "Maximum decoded image size.", 1024, 8_000_000, "bytes"),
                SettingInfo.Int("mcp.refreshSeconds", "Catalog refresh", 60, "Refresh even when a server does not support change notifications.", 5, 3600, "seconds"),
            ],
        });
        ctx.Rpc.Register("mcp.list", (_, _) => Task.FromResult<object?>(manager.Snapshot()),
            "MCP server configurations and connection status; a secret in a URL or an argument is masked (secretsMasked), the editor reads mcp.config", readOnly: true);
        ctx.Rpc.Register("mcp.config", (req, _) =>
        {
            var id = req.Required("id");
            var config = manager.RawConfig(id) ?? throw new RpcException("not_found", "No MCP server " + id);
            return Task.FromResult<object?>(new JsonObject { ["id"] = id, ["config"] = config });
        }, "One server's configuration whole, secrets included, for the editor: { id } → { id, config }. It changes nothing, but it is not read-only: it returns what mcp.list masks (idea-3q0uzn)");
        ctx.Rpc.Register("mcp.read", (req, token) => CallAsync(req, writes: false, token),
            "Call an MCP tool that only reads (its readOnlyHint, or the server's readOnly list) through NetPI's connection: { id (from mcp.tools, or \"server/tool\") | serverId + name, arguments? ({}), revision? } → { id, serverId, name, isError, content, structuredContent, metadata, images }. A tool that may change something is refused: that is mcp.call",
            readOnly: true);
        ctx.Rpc.Register("mcp.call", (req, token) => CallAsync(req, writes: true, token),
            "Call any exposed MCP tool through NetPI's connection: { id | serverId + name, arguments?, revision? } → as mcp.read. Not read-only (the tool may change things); mcp.read is the read-only way");
        ctx.Rpc.Register("mcp.tools", (req, _) =>
        {
            var id = req.Str("serverId");
            return Task.FromResult<object?>(new JsonObject { ["tools"] = new JsonArray(manager.Inventory(id)
                .Select(t => (JsonNode)new JsonObject { ["id"] = t.Definition.Name, ["serverId"] = t.ServerId, ["name"] = t.RemoteName,
                    ["description"] = t.Definition.Description, ["revision"] = t.Definition.Revision, ["deferred"] = t.Definition.Deferred,
                    ["readOnly"] = t.Definition.ReadOnly, ["exposed"] = manager.Exposed(t), ["schema"] = t.Definition.Parameters.DeepClone() }).ToArray()) });
        }, "MCP tools for a server (UI catalog, not model context)", readOnly: true);
        ctx.Rpc.Register("mcp.tool", (req, _) =>
        {
            var id = req.Required("id");
            var tool = manager.Find(id) ?? throw new RpcException("not_found", "No MCP tool " + id);
            return Task.FromResult<object?>(new JsonObject { ["id"] = tool.Definition.Name, ["serverId"] = tool.ServerId, ["name"] = tool.RemoteName,
                ["readOnly"] = tool.Definition.ReadOnly, ["annotations"] = tool.Raw["annotations"]?.DeepClone() });
        }, "One MCP tool by its id: { id } → { id, serverId, name, readOnly (the server's readOnlyHint, or the server's readOnly list), annotations }", readOnly: true);
        ctx.Rpc.Register("mcp.resources", (req, _) =>
        {
            var id = req.Str("serverId");
            return Task.FromResult<object?>(new JsonObject { ["resources"] = new JsonArray(manager.Resources(id)
                .Select(r => (JsonNode)r.Summary(manager.Available(r.ServerId))).ToArray()) });
        }, "MCP resources advertised by a server (what mcp_search can surface)", readOnly: true);
        // mcp.read / mcp.call: what a script or an outside agent used to do itself, with the URL (secret path included)
        // taken from mcp.list and a fresh MCP session per call (idea-fhtryf). The tool is found among the exposed ones,
        // its arguments go through the model's own path (schema check, the one repair), and the call is recorded as a
        // tool call without a chat (tool.start / tool.end, source "rpc"), so diag.tools lists it.
        RemoteTool Resolve(RpcRequest req)
        {
            var id = req.Str("id");
            var serverId = req.Str("serverId");
            var name = req.Str("name");
            if (id is not null && serverId is null && name is null && !id.StartsWith("mcp_", StringComparison.Ordinal) && id.IndexOf('/') is var slash and > 0)
                (serverId, name) = (id[..slash], id[(slash + 1)..]);
            var key = serverId is not null && name is not null ? RemoteTool.Name(serverId, name)
                : id ?? throw new RpcException("bad_request", "Name the tool: id (from mcp.tools, or \"server/tool\"), or serverId and name.");
            if (manager.Find(key) is { } tool) return tool;
            var shown = serverId is not null && name is not null ? serverId + "/" + name : key;
            throw manager.Inventory(serverId).Any(t => t.Definition.Name == key)
                ? new RpcException("forbidden", $"The MCP tool {shown} is not exposed (the MCP tab's \"Expose tool\", or the server's tools list).")
                : new RpcException("not_found", $"No MCP tool {shown} (mcp.tools lists them, with their ids).");
        }
        async Task<object?> CallAsync(RpcRequest req, bool writes, CancellationToken token)
        {
            var tool = Resolve(req);
            if (!writes && !tool.Definition.ReadOnly)
                throw new RpcException("forbidden", $"{tool.ServerId}/{tool.RemoteName} is not marked read-only (no readOnlyHint, not in the server's readOnly list): mcp.call runs it (netpi.mjs: --write).");
            if (req.Str("revision") is { Length: > 0 } revision && revision != tool.Definition.Revision)
                throw new RpcException("conflict", $"The schema of {tool.ServerId}/{tool.RemoteName} changed (revision {tool.Definition.Revision} now): read it again with mcp.tools.");
            JsonObject args;
            if (!req.Params.TryGetProperty("arguments", out var given) || given.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) args = [];
            else if (given.ValueKind == JsonValueKind.Object) args = JsonNode.Parse(given.GetRawText())!.AsObject();
            else throw new RpcException("bad_request", "arguments must be an object ({} for none).");
            JsonElement effective;
            try { effective = McpCallTool.Prepare(ctx, tool, args); }
            catch (McpException ex) { throw new RpcException("bad_request", ex.Message); }

            var callId = "rpc_" + Guid.NewGuid().ToString("N")[..16];
            ctx.Events.Publish(EventTypes.ToolStart, new JsonObject
            {
                ["callId"] = callId, ["name"] = "mcp_call", ["label"] = tool.Definition.Label, ["resolvedTool"] = tool.Definition.Name,
                ["serverId"] = tool.ServerId, ["source"] = "rpc",
                ["arguments"] = new JsonObject { ["id"] = tool.Definition.Name, ["arguments"] = args.DeepClone() }.ToJsonString(),
            }, ui: false);
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            ToolResult? result = null;
            try
            {
                result = await manager.CallAsync(tool, effective, token).ConfigureAwait(false);
            }
            catch (McpException ex) { throw new RpcException("unavailable", ex.Message); }
            finally
            {
                var end = new JsonObject
                {
                    ["callId"] = callId, ["name"] = "mcp_call", ["isError"] = result?.IsError ?? true,
                    ["durationMs"] = (long)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                    ["resolvedTool"] = tool.Definition.Name, ["serverId"] = tool.ServerId, ["source"] = "rpc",
                };
                if (result is null) end["interrupted"] = token.IsCancellationRequested ? "aborted" : "failed";
                ctx.Events.Publish(EventTypes.ToolEnd, end, ui: false);
            }
            var done = result ?? throw new InvalidOperationException("The MCP call returned no result.");
            var details = done.Details as JsonObject;
            return new JsonObject
            {
                ["id"] = tool.Definition.Name, ["serverId"] = tool.ServerId, ["name"] = tool.RemoteName,
                ["isError"] = done.IsError, ["content"] = done.Content,
                ["structuredContent"] = details?["structuredContent"]?.DeepClone(),
                ["metadata"] = details?["content"]?.DeepClone(),
                ["images"] = done.Images is { Count: > 0 } images
                    ? new JsonArray(images.Select(i => (JsonNode)new JsonObject { ["mediaType"] = i.MediaType, ["data"] = i.Data }).ToArray())
                    : null,
            };
        }
        async Task<object?> Write(RpcRequest req, Action<JsonObject> edit, CancellationToken token)
        {
            await _settings.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var map = ctx.Settings.GetNode("mcp.servers") as JsonObject ?? new JsonObject();
                edit(map);
                ctx.Settings.Set("mcp.servers", map);
            }
            finally { _settings.Release(); }
            await manager.ReconcileAsync(token).ConfigureAwait(false);
            return manager.Snapshot();
        }
        ctx.Rpc.Register("mcp.save", (req, token) => Write(req, map =>
        {
            var id = req.Required("id");
            var config = req.Params.TryGetProperty("config", out var node) && node.ValueKind == JsonValueKind.Object
                ? JsonNode.Parse(node.GetRawText())!.AsObject() : throw new RpcException("bad_request", "config must be an object.");
            map[id] = ServerConfig.Parse(id, config, ctx.Paths.DefaultWorkspace).Json();
        }, token), "Save one server configuration: {id,config}");
        ctx.Rpc.Register("mcp.remove", (req, token) => Write(req, map => map.Remove(req.Required("id")), token), "Remove an MCP server: {id}");
        ctx.Rpc.Register("mcp.setEnabled", (req, token) => Write(req, map =>
        {
            var id = req.Required("id");
            if (map[id] is not JsonObject config) throw new RpcException("not_found", "No MCP server " + id);
            config["enabled"] = req.Bool("enabled") ?? throw new RpcException("bad_request", "enabled is required.");
        }, token), "Enable or disable an MCP server: {id,enabled}");
        ctx.Rpc.Register("mcp.reconnect", async (req, token) => { await manager.CommandAsync(req.Required("id"), true, token).ConfigureAwait(false); return manager.Snapshot(); }, "Reconnect an MCP server: {id}");
        ctx.Rpc.Register("mcp.refresh", async (req, token) => { await manager.CommandAsync(req.Required("id"), false, token).ConfigureAwait(false); return manager.Snapshot(); }, "Refresh an MCP server catalog: {id}");
        ctx.Events.SubscribeAsync("settings.changed", async _ =>
        {
            try { await manager.ReconcileAsync(ctx.Stopping).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ctx.Stopping.IsCancellationRequested) { }
            catch (Exception ex) { ctx.Logger.LogWarning("MCP configuration update failed: {Error}", ex.Message); }
        });
        ctx.Ui.AddTab(new UiTabInfo { Id = "mcp", Title = "MCP", Panel = UiPanel.Right, Icon = "plug", Order = 65 });
    }
    public async Task StopAsync(CancellationToken ct)
    {
        if (_manager is not null) await _manager.DisposeAsync().ConfigureAwait(false);
        _manager = null;
    }
}
