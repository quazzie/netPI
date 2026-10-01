using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace NetPI.Mcp;

internal sealed class ServerManager : IAsyncDisposable
{
    internal sealed class State(ServerConfig config)
    {
        public ServerConfig Config { get; } = config;
        public McpConnection? Connection;
        public string Status = config.Enabled ? "connecting" : "disabled";
        public string? Error;
        public long Generation;
        public bool Disconnected;
        public bool Retiring;
        public List<JsonObject> RawCatalog = [];
        public List<JsonObject> RawResources = [];
        public JsonArray Rejected = [];
        public Dictionary<string, (RemoteTool Tool, IDisposable Handle)> Catalog = new(StringComparer.Ordinal);
        public Dictionary<string, RemoteResource> Resources = new(StringComparer.Ordinal);
        public readonly CancellationTokenSource Stop = new();
        public readonly Channel<Command> Commands = Channel.CreateBounded<Command>(new BoundedChannelOptions(16) { FullMode = BoundedChannelFullMode.Wait });
        public Task Worker = Task.CompletedTask;
        public int Calls;
        public TaskCompletionSource Idle = Completed();
        private static TaskCompletionSource Completed() { var t = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); t.SetResult(); return t; }
    }
    internal sealed record Command(string Reason, bool Reconnect, TaskCompletionSource? Done = null);
    private readonly IPluginContext _ctx;
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _changes = new(1);
    private readonly CancellationTokenSource _stop;
    private readonly Dictionary<string, State> _states = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _invalid = new(StringComparer.Ordinal);
    private bool _disposed;
    public ServerManager(IPluginContext ctx)
    {
        _ctx = ctx; _stop = CancellationTokenSource.CreateLinkedTokenSource(ctx.Stopping);
    }
    internal int Limit(string key, int fallback, int min, int max) => Math.Clamp(_ctx.Settings.Get(key, fallback), min, max);
    public async Task StartAsync(HashSet<string> previouslyHealthy, CancellationToken ct)
    {
        var configs = ReadConfigs();
        foreach (var config in configs) _states[config.Id] = new State(config);
        try
        {
            await Task.WhenAll(_states.Values.Where(s => s.Config.Enabled).Select(async state =>
            {
                try { await ConnectAsync(state, "discovery", ct).ConfigureAwait(false); }
                catch (Exception ex)
                {
                    SetStatus(state, "failed", state.Config.Redact(ex.Message));
                    if (previouslyHealthy.Contains(state.Config.Id)) throw new McpException("MCP replacement could not prepare healthy server " + state.Config.Id + "; the previous plugin must be kept.");
                }
            })).ConfigureAwait(false);
            foreach (var state in _states.Values.Where(s => s.Config.Enabled)) state.Worker = RunAsync(state);
        }
        catch { await DisposeAsync().ConfigureAwait(false); throw; }
    }
    private List<ServerConfig> ReadConfigs()
    {
        var map = _ctx.Settings.GetNode("mcp.servers");
        if (map is not null && map is not JsonObject) throw new McpException("mcp.servers must be an object keyed by server id.");
        var result = new List<ServerConfig>();
        lock (_gate) _invalid.Clear();
        foreach (var (id, node) in map as JsonObject ?? new JsonObject())
        {
            try { result.Add(ServerConfig.Parse(id, node as JsonObject ?? throw new McpException("Server configuration must be an object."), _ctx.Paths.DefaultWorkspace)); }
            catch (Exception ex) { lock (_gate) _invalid[id] = ex is RpcException ? ex.Message : "Invalid server configuration."; }
        }
        return result;
    }
    public async Task ReconcileAsync(CancellationToken ct)
    {
        await _changes.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            var configs = ReadConfigs().ToDictionary(c => c.Id, StringComparer.Ordinal);
            List<State> remove;
            lock (_gate) remove = _states.Values.Where(s => !configs.ContainsKey(s.Config.Id)).ToList();
            foreach (var old in remove)
            {
                lock (_gate) _states.Remove(old.Config.Id);
                RemoveCatalog(old, "deleted");
                await RetireAsync(old).ConfigureAwait(false);
            }
            foreach (var config in configs.Values)
            {
                State? old; lock (_gate) _states.TryGetValue(config.Id, out old);
                if (old is not null && JsonNode.DeepEquals(old.Config.Json(), config.Json())) continue;
                var next = new State(config);
                // Keep the known catalog during an outage, including updated local exposure/pin policy.
                List<JsonObject>? known = null; List<JsonObject>? knownResources = null;
                lock (_gate) if (old is not null) { old.Retiring = true; known = old.RawCatalog; knownResources = old.RawResources; }
                if (known is not null && config.Enabled) Apply(next, known, knownResources ?? [], "configuration");
                if (config.Enabled)
                {
                    try { await ConnectAsync(next, "configuration", ct).ConfigureAwait(false); }
                    catch (Exception ex) { SetStatus(next, "failed", config.Redact(ex.Message)); }
                }
                else RemoveCatalog(next, "disabled");
                lock (_gate) _states[config.Id] = next;
                if (old is not null)
                {
                    // New registrations shadow old ones first. Unload can dispose these handles again safely.
                    string[] removed;
                    lock (_gate)
                    {
                        removed = old.Catalog.Keys.Where(id => !next.Catalog.ContainsKey(id)).ToArray();
                        foreach (var entry in old.Catalog.Values) entry.Handle.Dispose();
                        old.Catalog.Clear();
                    }
                    if (removed.Length > 0) _ctx.Events.Publish("mcp.toolsChanged", new JsonObject { ["serverId"] = config.Id,
                        ["reason"] = config.Enabled ? "configuration" : "disabled", ["added"] = new JsonArray(), ["updated"] = new JsonArray(), ["removed"] = ServerConfig.Array(removed) });
                    await RetireAsync(old).ConfigureAwait(false);
                }
                if (config.Enabled) next.Worker = RunAsync(next);
                SetStatus(next, next.Status, next.Error);
            }
        }
        finally { _changes.Release(); }
    }
    private async Task ConnectAsync(State state, string reason, CancellationToken ct)
    {
        SetStatus(state, state.Catalog.Count == 0 ? "connecting" : "reconnecting", null);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token, state.Stop.Token);
        timeout.CancelAfter(state.Config.ConnectTimeoutMs);
        var candidate = new McpConnection(state.Config, Limit("mcp.maxMessageChars", 4_000_000, 4096, 16_000_000),
            line => _ctx.Logger.LogDebug("MCP {Server}: {Line}", state.Config.Id, line));
        candidate.Transport.Notification += m =>
        {
            // Both notifications mean the same thing to us: re-read the catalogs.
            if (m["method"]?.GetValue<string>() is "notifications/tools/list_changed" or "notifications/resources/list_changed")
                state.Commands.Writer.TryWrite(new Command("catalog-refresh", false));
        };
        candidate.Transport.Closed += _ =>
        {
            if (ReferenceEquals(state.Connection, candidate))
            {
                state.Disconnected = true;
                state.Commands.Writer.TryWrite(new Command("transport-lost", true));
            }
        };
        try
        {
            await candidate.InitializeAsync(timeout.Token).ConfigureAwait(false);
            var raw = await candidate.ListAsync(Limit("mcp.maxCatalogTools", 10000, 1, 10000),
                Limit("mcp.maxCatalogChars", 8_000_000, 4096, 32_000_000), timeout.Token).ConfigureAwait(false);
            var resources = await candidate.ListResourcesAsync(Limit("mcp.maxResourceEntries", 2000, 1, 20000),
                Limit("mcp.maxResourceCatalogChars", 2_000_000, 4096, 16_000_000), timeout.Token).ConfigureAwait(false);
            var old = state.Connection;
            state.Connection = candidate; state.Disconnected = false; state.Generation++;
            Apply(state, raw, resources, reason);
            SetStatus(state, "connected", null);
            if (old is not null) await old.DisposeAsync().ConfigureAwait(false);
        }
        catch { await candidate.DisposeAsync().ConfigureAwait(false); throw; }
    }
    private void Apply(State state, List<JsonObject> raw, List<JsonObject> rawResources, string reason)
    {
        var replacements = new Dictionary<string, RemoteTool>(StringComparer.Ordinal);
        var rejected = new JsonArray();
        foreach (var value in raw)
        {
            var name = value["name"]!.GetValue<string>();
            if (!state.Config.Exposes(name)) continue;
            try
            {
                var tool = new RemoteTool(this, state.Config, value);
                if (!replacements.TryAdd(tool.Definition.Name, tool)) throw new McpException("Remote tool id collision.");
            }
            catch (Exception ex)
            {
                var error = state.Config.Redact(ex.Message);
                rejected.Add(new JsonObject { ["name"] = name, ["error"] = error });
                _ctx.Logger.LogWarning("MCP {Server}: rejected tool {Tool}: {Reason}", state.Config.Id, name, error);
            }
        }
        var added = new List<string>(); var removed = new List<string>(); var updated = new List<string>();
        lock (_gate)
        {
            if (state.Retiring) return;
            state.RawCatalog = raw; state.RawResources = rawResources; state.Rejected = rejected;
            foreach (var (id, tool) in replacements)
            {
                if (state.Catalog.TryGetValue(id, out var current) && current.Tool.Definition.Revision == tool.Definition.Revision) continue;
                var active = _ctx.Tools.Get(id);
                if (active is not null && _ctx.Tools.Registrations.Any(r => ReferenceEquals(r.Tool, active) && r.PluginId != _ctx.PluginId))
                {
                    state.Rejected.Add(new JsonObject { ["name"] = tool.RemoteName, ["error"] = "Tool id collides with a tool owned by another plugin." });
                    continue;
                }
                var handle = _ctx.Tools.Register(tool);
                if (state.Catalog.TryGetValue(id, out current)) { current.Handle.Dispose(); updated.Add(id); }
                else added.Add(id);
                state.Catalog[id] = (tool, handle);
            }
            foreach (var id in state.Catalog.Keys.Where(id => !replacements.ContainsKey(id)).ToArray())
            {
                state.Catalog[id].Handle.Dispose(); state.Catalog.Remove(id); removed.Add(id);
            }
            // Resources carry no registration handle: the catalog is what mcp_search and mcp_resource read.
            var exposed = new Dictionary<string, RemoteResource>(StringComparer.Ordinal);
            foreach (var value in rawResources)
            {
                RemoteResource resource;
                try
                {
                    var uri = value["uri"]?.GetValue<string>() ?? "";
                    if (!state.Config.ExposesResource(uri)) continue;
                    resource = new RemoteResource(state.Config, value);
                }
                catch (Exception ex)
                {
                    var error = state.Config.Redact(ex.Message);
                    rejected.Add(new JsonObject { ["name"] = value["uri"]?.ToJsonString() ?? "resource", ["error"] = error });
                    _ctx.Logger.LogWarning("MCP {Server}: rejected resource {Uri}: {Reason}", state.Config.Id, value["uri"]?.ToJsonString(), error);
                    continue;
                }
                exposed[resource.Uri] = resource;
            }
            state.Resources = exposed;
        }
        if (added.Count + removed.Count + updated.Count > 0)
            _ctx.Events.Publish("mcp.toolsChanged", new JsonObject
            {
                ["serverId"] = state.Config.Id, ["generation"] = state.Generation, ["reason"] = reason,
                ["added"] = ServerConfig.Array(added), ["removed"] = ServerConfig.Array(removed), ["updated"] = ServerConfig.Array(updated),
            });
    }
    private void RemoveCatalog(State state, string reason)
    {
        string[] removed; lock (_gate)
        {
            removed = state.Catalog.Keys.ToArray();
            foreach (var entry in state.Catalog.Values) entry.Handle.Dispose();
            state.Catalog.Clear();
            state.Resources.Clear();
        }
        if (removed.Length > 0) _ctx.Events.Publish("mcp.toolsChanged", new JsonObject { ["serverId"] = state.Config.Id,
            ["reason"] = reason, ["generation"] = state.Generation, ["added"] = new JsonArray(), ["updated"] = new JsonArray(), ["removed"] = ServerConfig.Array(removed) });
    }
    private void SetStatus(State state, string status, string? error)
    {
        lock (_gate) { state.Status = status; state.Error = error; }
        _ctx.Events.Publish("mcp.serverChanged", new JsonObject { ["serverId"] = state.Config.Id, ["status"] = status,
            ["error"] = error, ["generation"] = state.Generation });
    }
    private async Task RunAsync(State state)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token, state.Stop.Token);
        var ct = linked.Token;
        Task? listener = null;
        var backoff = 1;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (state.Connection is not null && state.Connection.ListChanged && listener is null)
                    listener = ListenAsync(state, ct);
                Command command;
                using (var wait = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    wait.CancelAfter(TimeSpan.FromSeconds(state.Status == "connected" ? Limit("mcp.refreshSeconds", 60, 5, 3600) : backoff));
                    try { command = await state.Commands.Reader.ReadAsync(wait.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested) { command = new Command("catalog-refresh", state.Status != "connected" || state.Disconnected); }
                }
                if (ct.IsCancellationRequested) break;
                try
                {
                    if (command.Reconnect || state.Connection is null || state.Disconnected)
                    {
                        await ConnectAsync(state, command.Reason, ct).ConfigureAwait(false);
                        listener = null;
                    }
                    else
                    {
                        using var refresh = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        refresh.CancelAfter(state.Config.ConnectTimeoutMs);
                        var raw = await state.Connection.ListAsync(Limit("mcp.maxCatalogTools", 10000, 1, 10000),
                            Limit("mcp.maxCatalogChars", 8_000_000, 4096, 32_000_000), refresh.Token).ConfigureAwait(false);
                        var resources = await state.Connection.ListResourcesAsync(Limit("mcp.maxResourceEntries", 2000, 1, 20000),
                            Limit("mcp.maxResourceCatalogChars", 2_000_000, 4096, 16_000_000), refresh.Token).ConfigureAwait(false);
                        Apply(state, raw, resources, command.Reason);
                    }
                    backoff = 1; command.Done?.TrySetResult();
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { command.Done?.TrySetCanceled(ct); throw; }
                catch (Exception ex)
                {
                    SetStatus(state, state.Disconnected || state.Connection is null ? "failed" : "connected", state.Config.Redact(ex.Message));
                    backoff = Math.Min(30, backoff * 2);
                    command.Done?.TrySetException(new McpException(state.Config.Redact(ex.Message)));
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally
        {
            while (state.Commands.Reader.TryRead(out var pending)) pending.Done?.TrySetException(new McpException("MCP server stopped."));
            if (listener is not null) try { await listener.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); } catch { }
        }
    }
    private async Task ListenAsync(State state, CancellationToken ct)
    {
        var connection = state.Connection!;
        try { await connection.Transport.ListenAsync(connection.Modern, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (ReferenceEquals(state.Connection, connection))
            {
                _ctx.Logger.LogDebug("MCP {Server} notification stream: {Error}", state.Config.Id, state.Config.Redact(ex.Message));
                state.Commands.Writer.TryWrite(new Command("notification-stream-lost", true));
            }
        }
    }
    public async Task CommandAsync(string id, bool reconnect, CancellationToken ct)
    {
        State state; lock (_gate) state = _states.GetValueOrDefault(id) ?? throw new RpcException("not_found", "No MCP server " + id);
        if (!state.Config.Enabled) throw new RpcException("bad_request", "Enable this MCP server first.");
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await state.Commands.Writer.WriteAsync(new Command(reconnect ? "reconnect" : "catalog-refresh", reconnect, done), ct).ConfigureAwait(false);
        await done.Task.WaitAsync(ct).ConfigureAwait(false);
    }
    public IReadOnlyList<RemoteTool> Catalog()
    {
        lock (_gate) return _states.Values.SelectMany(s => s.Catalog.Values.Select(e => e.Tool)).OrderBy(t => t.Definition.Name, StringComparer.Ordinal).ToList();
    }
    public IReadOnlyList<RemoteResource> Resources(string? server)
    {
        lock (_gate) return _states.Values.Where(s => server is null || s.Config.Id == server)
            .SelectMany(s => s.Resources.Values).OrderBy(r => r.Uri, StringComparer.Ordinal).ToList();
    }
    public IReadOnlyList<RemoteTool> Inventory(string? server)
    {
        lock (_gate)
        {
            var tools = new List<RemoteTool>();
            foreach (var state in _states.Values.Where(s => server is null || s.Config.Id == server))
                foreach (var raw in state.RawCatalog)
                    try { tools.Add(new RemoteTool(this, state.Config, raw)); } catch { }
            return tools;
        }
    }
    public bool Exposed(RemoteTool tool) { lock (_gate) return _states.GetValueOrDefault(tool.ServerId)?.Catalog.ContainsKey(tool.Definition.Name) == true; }
    public bool Available(string server) { lock (_gate) return _states.TryGetValue(server, out var s) && s.Status == "connected" && !s.Disconnected; }
    public RemoteTool? Find(string id) { lock (_gate) return _states.Values.Select(s => s.Catalog.GetValueOrDefault(id).Tool).FirstOrDefault(t => t is not null); }
    public RemoteResource? FindResource(string uri, string? server)
    {
        lock (_gate) return _states.Values.Where(s => server is null || s.Config.Id == server)
            .Select(s => s.Resources.GetValueOrDefault(uri)).FirstOrDefault(r => r is not null);
    }
    public JsonObject Snapshot()
    {
        lock (_gate) return new JsonObject { ["servers"] = new JsonArray(_states.Values.OrderBy(s => s.Config.Id, StringComparer.Ordinal).Select(s => (JsonNode)new JsonObject
        {
            ["id"] = s.Config.Id, ["status"] = s.Status, ["error"] = s.Error, ["generation"] = s.Generation,
            ["toolCount"] = s.Catalog.Count, ["resourceCount"] = s.Resources.Count, ["rejected"] = s.Rejected.DeepClone(), ["version"] = s.Connection?.Transport.Version, ["config"] = s.Config.Json(),
        }).Concat(_invalid.Select(p => (JsonNode)new JsonObject { ["id"] = p.Key, ["status"] = "invalid", ["error"] = p.Value })).ToArray()) };
    }
    public async Task<ToolResult> CallAsync(RemoteTool tool, JsonElement args, CancellationToken ct)
    {
        State state; McpConnection connection;
        lock (_gate)
        {
            state = _states.GetValueOrDefault(tool.ServerId) ?? throw new McpException("MCP server was removed.");
            if (!state.Config.Enabled || state.Status != "connected" || state.Disconnected || state.Connection is null) throw new McpException("MCP server " + tool.ServerId + " is unavailable. Reconnect before retrying.");
            if (!state.Catalog.TryGetValue(tool.Definition.Name, out var current) || current.Tool.Definition.Revision != tool.Definition.Revision)
                throw new McpException("MCP tool definition changed. Discover its current schema.");
            connection = state.Connection;
            if (state.Calls++ == 0) state.Idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token, state.Stop.Token);
        timeout.CancelAfter(state.Config.CallTimeoutMs);
        try
        {
            var result = await connection.CallAsync(tool.RemoteName, args, tool.Definition.Parameters, timeout.Token).ConfigureAwait(false);
            return ResultAdapter.Convert(result, tool.ServerId, tool.RemoteName, Limit("mcp.maxBinaryBytes", 2_000_000, 1024, 8_000_000));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { return ToolResult.Error("MCP call timed out or the server stopped. Its side effect may have happened; the call was not replayed."); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return ToolResult.Error("MCP " + tool.ServerId + "/" + tool.RemoteName + ": " + state.Config.Redact(ex.Message)); }
        finally { lock (_gate) if (--state.Calls == 0) state.Idle.TrySetResult(); }
    }
    /// <summary>
    /// Reads one advertised resource. The URI is the gate: a resource the server did not list is never requested, so
    /// a link that merely appears in a tool result is not fetched, and a per-server allow-list removes it upstream of
    /// here. Binary contents stay out of model text, exactly as in a tool result.
    /// </summary>
    public async Task<ToolResult> ReadResourceAsync(RemoteResource resource, CancellationToken ct)
    {
        State state; McpConnection connection;
        lock (_gate)
        {
            state = _states.GetValueOrDefault(resource.ServerId) ?? throw new McpException("MCP server was removed.");
            if (!state.Config.Enabled || state.Status != "connected" || state.Disconnected || state.Connection is null)
                throw new McpException("MCP server " + resource.ServerId + " is unavailable. Reconnect before retrying.");
            if (!state.Resources.ContainsKey(resource.Uri)) throw new McpException("MCP resource " + resource.Uri + " is no longer advertised. Search again.");
            connection = state.Connection;
            if (state.Calls++ == 0) state.Idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token, state.Stop.Token);
        timeout.CancelAfter(state.Config.CallTimeoutMs);
        try
        {
            var result = await connection.ReadResourceAsync(resource.Uri, timeout.Token).ConfigureAwait(false);
            return ResourceReader.Convert(result, resource, Limit("mcp.maxResourceChars", 20000, 1024, 200000),
                Limit("mcp.maxBinaryBytes", 2_000_000, 1024, 8_000_000), _ctx.Paths.TempDir);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { return ToolResult.Error("MCP resource read timed out or the server stopped."); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return ToolResult.Error("MCP " + resource.ServerId + " " + resource.Uri + ": " + state.Config.Redact(ex.Message)); }
        finally { lock (_gate) if (--state.Calls == 0) state.Idle.TrySetResult(); }
    }
    private async Task RetireAsync(State state)
    {
        try { await state.Idle.Task.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); } catch { }
        state.Stop.Cancel(); state.Commands.Writer.TryComplete();
        if (state.Connection is not null) await state.Connection.DisposeAsync().ConfigureAwait(false);
        try { await state.Worker.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); } catch { }
        state.Stop.Dispose();
    }
    public async ValueTask DisposeAsync()
    {
        await _changes.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true; _stop.Cancel();
            State[] states; lock (_gate) { states = _states.Values.ToArray(); _states.Clear(); }
            foreach (var state in states) RemoveCatalog(state, "disabled");
            await Task.WhenAll(states.Select(RetireAsync)).ConfigureAwait(false);
        }
        finally { _changes.Release(); }
        _stop.Dispose();
    }
}
