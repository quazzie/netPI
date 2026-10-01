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
        ctx.Rpc.Register("mcp.list", (_, _) => Task.FromResult<object?>(manager.Snapshot()), "MCP server configurations and connection status", readOnly: true);
        ctx.Rpc.Register("mcp.tools", (req, _) =>
        {
            var id = req.Str("serverId");
            return Task.FromResult<object?>(new JsonObject { ["tools"] = new JsonArray(manager.Inventory(id)
                .Select(t => (JsonNode)new JsonObject { ["id"] = t.Definition.Name, ["serverId"] = t.ServerId, ["name"] = t.RemoteName,
                    ["description"] = t.Definition.Description, ["revision"] = t.Definition.Revision, ["deferred"] = t.Definition.Deferred,
                    ["readOnly"] = t.Definition.ReadOnly, ["exposed"] = manager.Exposed(t), ["schema"] = t.Definition.Parameters.DeepClone() }).ToArray()) });
        }, "MCP tools for a server (UI catalog, not model context)", readOnly: true);
        ctx.Rpc.Register("mcp.resources", (req, _) =>
        {
            var id = req.Str("serverId");
            return Task.FromResult<object?>(new JsonObject { ["resources"] = new JsonArray(manager.Resources(id)
                .Select(r => (JsonNode)r.Summary(manager.Available(r.ServerId))).ToArray()) });
        }, "MCP resources advertised by a server (what mcp_search can surface)", readOnly: true);
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
