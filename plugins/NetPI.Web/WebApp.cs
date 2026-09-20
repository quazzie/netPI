using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebSockets;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;



using NetPI.Abstractions;

namespace NetPI.Web;

/// <summary>
/// Owns the Kestrel app for the netPI web surface (PLAN §36, §41): a single
/// WebSocket hub at /ws (all clients receive the same broadcast), a minimal
/// REST bootstrap, and static serving of the compiled frontend. Subscribes to
/// agent bus events and re-emits them as §41 WebSocket events.
/// </summary>
internal sealed class WebApp : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOpts =
        new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly IPluginContext _ctx;
    private readonly int _port;
    private readonly string _staticRoot;
    private readonly IPluginLogger _log;
    private WebApplication? _app;

    // ---- services resolved at load (reload-safe; may be null) -------------
    private IAgentRunner? _runner;
    private IAgentRuntime? _agent;
    private IModelCatalog? _catalog;
    private ISessionStore? _store;
    private IPluginManagerFacade? _facade;
    private IHostConfigUpdate? _config;
    private ISteeringQueue? _steering;
    private ICompaction? _compaction;
    private NetPI.Abstractions.ICommandRegistry? _commands;

    private readonly List<IDisposable> _subs = [];
    // PLAN §41: tool.started → tool.output → tool.completed, with real duration.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> _toolStarts = new();
    // A model turn stays open through its tool batch so live tool calls/results
    // render inside the same assistant message instead of losing their parent.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _assistantOpen = new();

    // PLAN §39: per-session delta batcher (coalesce ~20ms windows).
    private sealed class DeltaBatcher
    {
        public readonly object Gate = new();
        public readonly System.Collections.Generic.Dictionary<string, System.Text.StringBuilder> Buffers = new();
        public Task? Pending;
    }
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DeltaBatcher> _batchers = new();

    /// <summary>Per-session accumulated assistant text since the last
    /// model-completed (PLAN §41: text.completed must carry the full final text,
    /// but the deltas were already flushed by the 120 ms timer).</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _completedText = new();

    // ---- hub ---------------------------------------------------------------
    private readonly HashSet<Client> _clients = [];
    private readonly object _clientsLock = new();

    private int _promptTokens, _completionTokens, _totalTokens;

    public WebApp(IPluginContext ctx, int port, string staticRoot, IPluginLogger log)
    {
        _ctx = ctx;
        _port = port;
        _staticRoot = staticRoot;
        _log = log;
    }

    public async ValueTask StartAsync(CancellationToken ct)
    {
        _runner = Resolve<IAgentRunner>("runner");
        _agent = Resolve<IAgentRuntime>("agent");
        _catalog = Resolve<IModelCatalog>("catalog");
        _store = Resolve<ISessionStore>("sessions");
        _facade = Resolve<IPluginManagerFacade>("plugins");
        _config = Resolve<IHostConfigUpdate>("host-config");
        _steering = Resolve<ISteeringQueue>("steering");
        _compaction = Resolve<ICompaction>("compaction");
        _commands = Resolve<NetPI.Abstractions.ICommandRegistry>("commands");

        _subs.Add(_ctx.Events.Subscribe<AgentEvent>(OnAgentEvent));

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = new[] { $"--urls=http://127.0.0.1:{_port}" },
        });
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        var app = builder.Build();

        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });
        app.UseDefaultFiles();

        app.UseStaticFiles();
        if (RootsTheFrontend())
        {
            app.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = new PhysicalFileProvider(Path.GetFullPath(_staticRoot)),
                ServeUnknownFileTypes = true,
            });
        }
        else
        {
            app.MapGet("/", () => "netPI is running. WebSocket at /ws");
            _log.Warning("staticRoot not found; serving /ws only");
        }
        app.MapGet("/bootstrap", Bootstrap);
        app.Map("/ws", HandleWsAsync);
        // Localhost-only file view for workspace/absolute references in chat.
        // Registered as a statement-bodied async lambda. A sync delegate that merely
        // returns OpenFileAsync's Task<IResult> (method group, or pass-through /
        // expression-bodied async lambda) silently degrades to HTTP 200 with an empty
        // body when the handler lives in a collectible plugin ALC; the statement-bodied
        // form (await into a local, return the value) serves the IResult correctly.
        // Verified empirically against the framework's RequestDelegateFactory, 2026-09-20.
        app.MapGet("/api/file", async (HttpContext c) =>
        {
            var result = await OpenFileAsync(c);
            return result;
        });
        // SPA routing: any request that matched no file and no explicit route
        // (including the bare "/") serves index.html from the frontend build.
        if (RootsTheFrontend())
            app.MapFallback(async context =>
            {
                context.Response.ContentType = "text/html";
                await context.Response.SendFileAsync(Path.Combine(Path.GetFullPath(_staticRoot), "index.html"));
            });

        await app.StartAsync(ct);
        _app = app;
        _ = Task.Run(BootstrapCatalogAsync);
    }

    public async ValueTask StopAsync(CancellationToken ct)
    {
        foreach (var s in _subs) s.Dispose();
        _subs.Clear();
        var app = _app;
        _app = null;
        if (app is null) return;
        try { await app.StopAsync(ct); } catch { }
        try { await app.DisposeAsync(); } catch { }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var s in _subs) s.Dispose();
        _subs.Clear();
        if (_app is not null)
        {
            var app = _app;
            _app = null;
            try { await app.StopAsync(CancellationToken.None); } catch { }
            try { await app.DisposeAsync(); } catch { }
        }
    }


    private bool RootsTheFrontend() =>
        !string.IsNullOrEmpty(_staticRoot) && Directory.Exists(Path.GetFullPath(_staticRoot));

    private T? Resolve<T>(string id) where T : notnull
    {
        try { return _ctx.Services.Resolve<T>(id); }
        catch (ServiceUnavailableException) { return default; }
    }

    // ---- agent event bridge ------------------------------------------------

    private void OnAgentEvent(AgentEvent e)
    {
        string? sid = e.SessionId;
        switch (e.Type)
        {
            case AgentEventType.AgentStarting:
                SendEvent("agent.state", new { state = "Preparing" }, sid);
                break;

            case AgentEventType.BeforeModelRequest:
                SendEvent("agent.state", new { state = "CallingModel" }, sid);
                break;

            case AgentEventType.BeforeToolBatch:
                SendEvent("agent.state", new { state = "ExecutingTools" }, sid);
                break;

            case AgentEventType.AfterToolBatch:
                // The assistant turn that requested these tools is only complete
                // after all tool results have been delivered to the UI.
                CompleteAssistantTurn(sid);
                break;

            case AgentEventType.BeforeToolCall when e.Payload is not null:
            {
                var p = e.Payload.Value;
                var id = S(p, "toolCallId");
                if (id is not null) _toolStarts[id] = DateTime.UtcNow;
                SendEvent("tool.started", new { id = S(p, "toolCallId"), name = S(p, "toolName") }, sid);
                break;
            }

            case AgentEventType.AfterToolCall when e.Payload is not null:
            {
                // PLAN §41: the agent publishes tool-call-completed WITH the
                // output (toolOutput/isError on the wire). Emit tool.output so
                // the UI's ToolCallBlock resolves live, then tool.completed
                // with the real wall-clock duration.
                var p = e.Payload.Value;
                var id = S(p, "toolCallId");
                long durMs = 0;
                if (id is not null)
                {
                    durMs = _toolStarts.TryRemove(id, out var start)
                        ? (int)(DateTime.UtcNow - start).TotalMilliseconds
                        : 0;
                }
                if (id is not null)
                {
                    SendEvent("tool.output", new { id, output = S(p, "toolOutput") ?? "", isError = B(p, "isError") }, sid);
                    SendEvent("tool.completed", new { id, durationMs = durMs }, sid);
                }
                break;
            }

            case AgentEventType.ModelStreamEvent when e.Payload is not null:
                ForwardModelWire(e.Payload.Value, sid);
                break;

            case AgentEventType.ModelRequestFailed when e.Payload is not null:
            {
                var p = e.Payload.Value;
                SendEvent("model.requestFailed", new { message = S(p, "error") ?? "model request failed" }, sid);
                // Do NOT force Idle here: a retry may follow (ModelRetrying will
                // flip to "Retrying"). Terminal Idle is emitted by
                // AgentCompleted/AgentCancelled in the agent runtime.
                break;

            }

            case AgentEventType.ModelRetrying when e.Payload is not null:
            {
                var p = e.Payload.Value;
                SendEvent("model.retrying", new
                {
                    attempt = S(p, "promptTokens") != null ? int.Parse(S(p, "promptTokens")!) : 0,
                    maxAttempts = S(p, "completionTokens") != null ? int.Parse(S(p, "completionTokens")!) : 0,
                    delayMs = S(p, "totalTokens") != null ? int.Parse(S(p, "totalTokens")!) : 0,
                    error = S(p, "error"),
                }, sid);
                SendEvent("agent.state", new { state = "Retrying" }, sid);
                break;
            }


            case AgentEventType.AgentCompleted:
            case AgentEventType.AgentCancelled:
                // Final turns have no tool batch, so close them here.
                CompleteAssistantTurn(sid);
                SendEvent("agent.state", new { state = "Idle" }, sid);
                break;
        }
    }

    private void ForwardModelWire(JsonElement w, string? sid)
    {
        if (w.ValueKind != JsonValueKind.Object) return;
        var kind = S(w, "kind") ?? "";
        switch (kind)
        {
            case "model-started":
            {
                var key = sid ?? "";
                // Defensive close in case a provider starts a new turn without a
                // normal tool/final boundary.
                if (_assistantOpen.ContainsKey(key))
                    CompleteAssistantTurn(sid);
                _assistantOpen[key] = 1;
                SendEvent("assistant.started", new { model = S(w, "modelId") }, sid);
                break;
            }
            case "thinking-started":
                SendEvent("thinking.started", new { }, sid);
                break;
            case "thinking-delta":
                EnqueueDelta(sid, "thinking", S(w, "text") ?? "");
                break;
            case "thinking-completed":
                FlushDeltas(sid);
                SendEvent("thinking.completed", new { }, sid);
                break;
            case "text-delta":
                EnqueueDelta(sid, "text", S(w, "text") ?? "");
                break;
            case "tool-call-started":
                // Surface the call as soon as the model emits it; arguments stream
                // into the block while the model is still responding.
                FlushDeltas(sid);
                SendEvent("tool.started", new { id = S(w, "toolCallId") ?? "", name = S(w, "toolName") ?? "tool" }, sid);
                break;
            case "tool-output-chunk":
                // PLAN §25: progressive shell stdout/stderr. Forward as a WS
                // tool.output with append:true; the client grows the block live.
                SendEvent("tool.output", new { id = S(w, "toolCallId") ?? "", output = S(w, "toolOutput") ?? "", append = true }, sid);
                break;
            case "tool-call-arguments-delta":
                EnqueueDelta(sid, "args:" + (S(w, "toolCallId") ?? ""), S(w, "text") ?? "");
                break;
            case "usage-updated":
                _promptTokens = I(w, "promptTokens");
                _completionTokens = I(w, "completionTokens");
                _totalTokens = I(w, "totalTokens");
                SendEvent("usage.updated", new
                {
                    promptTokens = _promptTokens,
                    completionTokens = _completionTokens,
                    totalTokens = _totalTokens,
                }, sid);
                break;
            case "model-completed":
            {
                FlushDeltas(sid);
                // This closes the model stream, not necessarily the assistant UI
                // turn: tool calls/results still belong to this same turn.
                var finalText = _completedText.TryRemove(sid ?? "", out var t) ? t : string.Empty;
                SendEvent("text.completed", new { text = finalText }, sid);
                break;
            }
        }
    }

    private static string? S(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString() : null;

    private static int I(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number
            ? p.GetInt32() : 0;

    private static bool B(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.True
            ? true : false;

    // ---- websocket ---------------------------------------------------------

    private async Task HandleWsAsync(HttpContext ctx)
    {
        if (!ctx.WebSockets.IsWebSocketRequest)
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        using var ws = await ctx.WebSockets.AcceptWebSocketAsync();


        var client = new Client(ws, ctx.RequestAborted);
        lock (_clientsLock) _clients.Add(client);
        try
        {
            await SendBootstrapAsync(client, ctx.RequestAborted);
            while (ws.State == WebSocketState.Open)
            {
                var buf = new byte[64 * 1024];
                var result = await ws.ReceiveAsync(new ArraySegment<byte>(buf), ctx.RequestAborted);
                if (result.MessageType == WebSocketMessageType.Close) break;
                var text = Encoding.UTF8.GetString(buf, 0, result.Count);
                await HandleCommandAsync(client, text, ctx.RequestAborted);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log.Warning($"ws loop: {ex.Message}");
        }
        finally
        {
            lock (_clientsLock) _clients.Remove(client);
            try
            {
                if (ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
            }
            catch { }
        }
    }

    private async Task SendBootstrapAsync(Client c, CancellationToken ct)
    {
        if (_agent is not null)
        {
            var st = _agent.State;
            await SendAsync(c, "agent.state", new { state = MapAgentState(st.State), sessionId = st.ActiveSessionId }, st.ActiveSessionId, ct);
        }

        if (_catalog is not null)
        {
            try
            {
                if (_catalog.IsStale) await _catalog.RefreshAsync(ct);
                await SendAsync(c, "models.updated", new { models = ModelJson() }, null, ct);
            }
            catch (Exception ex)
            {
                await SendAsync(c, "models.refreshFailed", new { message = ex.Message }, null, ct);
            }
        }

        if (_facade is not null)
            await SendAsync(c, "plugins.state", new { plugins = PluginJson() }, null, ct);

        await SendAsync(c, "ui.panels", new { panels = PanelJson() }, null, ct);

        if (_store is not null)
        {
            try
            {
                var sessions = await _store.ListAsync(50, ct);
                await SendAsync(c, "session.list", new { sessions = sessions.Select(ToSessionJson).ToList() }, null, ct);

                // PLAN §41: replay the active session's transcript so a client
                // (re)connecting after a host restart is not left with a blank
                // viewport. Mirrors SessionOpenAsync: latest 200 entries, more on
                // scroll-up.
                if (_agent?.State.ActiveSessionId is { } asid)
                {
                    var info = await _store.GetAsync(asid, ct);
                    if (info is not null)
                    {
                        const int pageSize = 200;
                        var total = info.EntryCount;
                        var offset = Math.Max(0, total - pageSize);
                        var entries = await _store.ReadAsync(asid, offset, pageSize, ct);
                        var beforeSeq = entries.Count > 0 ? entries[0].Sequence : 0;
                        await SendAsync(c, "session.updated", ToSessionJson(info), asid, ct);
                        await SendAsync(c, "session.entries",
                            new { entries = EntriesToJson(entries), replace = true, total = total,
                                  hasMore = total > entries.Count, beforeSequence = beforeSeq }, asid, ct);
                    }
                }
                // else: agent restarted without a session — restore the most
                // recently used one (sessions is sorted newest-first).
                else if (sessions.FirstOrDefault() is { } last)
                {
                    var info = await _store.GetAsync(last.Id, ct);
                    if (info is not null)
                    {
                        const int pageSize = 200;
                        var total = info.EntryCount;
                        var offset = Math.Max(0, total - pageSize);
                        var entries = await _store.ReadAsync(last.Id, offset, pageSize, ct);
                        var beforeSeq = entries.Count > 0 ? entries[0].Sequence : 0;
                        await SendAsync(c, "session.updated", ToSessionJson(info), last.Id, ct);
                        await SendAsync(c, "session.entries",
                            new { entries = EntriesToJson(entries), replace = true, total = total,
                                  hasMore = total > entries.Count, beforeSequence = beforeSeq }, last.Id, ct);
                    }
                }
            }
            catch (Exception ex)
            {
                _log.Warning($"bootstrap session.list failed: {ex.Message}");
            }
        }
    }

    private static string MapAgentState(AgentState s) => s switch
    {
        AgentState.Idle => "Idle",
        _ => s.ToString(), // Preparing/CallingModel/ExecutingTools/Compacting/Retrying/Cancelling

    };

    private async Task HandleCommandAsync(Client c, string raw, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(raw)) return;
        JsonElement? msg;
        try { msg = JsonDocument.Parse(raw).RootElement.Clone(); }
        catch { return; }
        if (msg is null) return;

        if (!msg.Value.TryGetProperty("type", out var tEl) || tEl.ValueKind != JsonValueKind.String) return;
        var type = tEl.GetString() ?? "";
        var requestId = msg.Value.TryGetProperty("requestId", out var r) && r.ValueKind == JsonValueKind.String
            ? r.GetString() : null;
        JsonElement p = msg.Value.TryGetProperty("payload", out var pl) && pl.ValueKind == JsonValueKind.Object
            ? pl.Clone()
            : default;

        try
        {
            await RouteCommandAsync(c, type, requestId, p, ct);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            _log.Error($"command {type} failed", ex);
            if (requestId is not null)
                await SendErrorAsync(c, requestId, ex.Message, ct);
        }
    }

    private async Task RouteCommandAsync(Client c, string type, string? requestId, JsonElement p, CancellationToken ct)
    {
        switch (type)
        {
            case "chat.send":
                await ChatSendAsync(c, requestId, p, ct);
                break;

            case "chat.steer":
                if (_steering is null) { await SendErrorAsync(c, requestId, "steering unavailable", ct); break; }
                // PLAN §12: steer targets the session (falls back to the active run).
                await _steering.EnqueueAsync(S(p, "text") ?? "", S(p, "sessionId"), ct);
                await SendAckAsync(c, requestId, ct);
                break;

            case "agent.cancel":
                if (_runner is not null) await _runner.CancelRunAsync(ct);
                if (_agent?.State.ActiveSessionId is { } csid)
                    await SendAsync(c, "agent.state", new { state = "Cancelling" }, csid, ct);
                await SendAckAsync(c, requestId, ct);
                break;

            case "session.create":
                await SessionCreateAsync(c, requestId, p, ct);
                break;

            case "session.open":
                await SessionOpenAsync(c, requestId, p, ct);
                break;

            case "session.older":
                await SessionOlderAsync(c, requestId, p, ct);
                break;

            case "session.rename":
            {
                var sid = S(p, "sessionId");
                var title = S(p, "title");
                var workspace = S(p, "workspace");
                if (sid is null || _store is null) { await SendAckAsync(c, requestId, ct); break; }
                if (!string.IsNullOrEmpty(title))
                {
                    await _store.RenameAsync(sid, title, ct);
                    await BroadcastSession(sid, ct);
                }
                else if (!string.IsNullOrEmpty(workspace))
                {
                    await _store.SetWorkspaceAsync(sid, workspace, ct);
                    await BroadcastSession(sid, ct);
                }
                await SendAckAsync(c, requestId, ct);
                break;
            }

            case "session.list":
                if (_store is not null)
                {
                    var list = await _store.ListAsync(50, ct);
                    await SendAsync(c, "session.list",
                        new { sessions = list.Select(ToSessionJson).ToList() }, null, ct);
                }
                await SendAckAsync(c, requestId, ct);
                break;

            case "session.model":
            {
                var sid = S(p, "sessionId");
                if (sid is not null && _store is not null)
                {
                    await _store.SetModelAsync(sid, S(p, "modelId"), S(p, "reasoning"), ct);
                    await BroadcastSession(sid, ct);
                }
                await SendAckAsync(c, requestId, ct);
                break;
            }

            case "session.reasoning":
            {
                var sid = S(p, "sessionId");
                if (sid is not null && _store is not null)
                {
                    var info = await _store.GetAsync(sid, ct);
                    await _store.SetModelAsync(sid, info?.ModelId, S(p, "level"), ct);
                    await BroadcastSession(sid, ct);
                }
                await SendAckAsync(c, requestId, ct);
                break;
            }

            case "session.compact":
            {
                var sid = S(p, "sessionId");
                var modelId = S(p, "model");
                var reasoning = S(p, "reasoning");
                bool ok = false; string note = "";

                if (sid is not null && _compaction is not null && _compaction.IsAvailable)
                {
                    if (string.IsNullOrEmpty(modelId) && _catalog is not null && _catalog.Models.Count > 0)
                        modelId = _catalog.Models[0].ModelId;
                    if (!string.IsNullOrEmpty(modelId))
                    {
                        try
                        {
                            var result = await _compaction.CompactAsync(new CompactionRequest
                            {
                                SessionId = sid,
                                ModelId = modelId,
                                ReasoningLevel = reasoning,
                            }, ct);
                            ok = result is { Performed: true };
                            note = ok ? "compacted" : "no-op (below threshold)";
                            await BroadcastSession(sid, ct);
                        }
                        catch (Exception ex)
                        {
                            note = ex.Message;
                            _log.Warning($"session.compact failed: {ex.Message}");
                        }
                    }
                    else note = "no model";
                }
                else if (sid is null) note = "no sessionId";
                else note = "compaction unavailable";

                await SendAckAsync(c, requestId, ct);
                await SendAsync(c, "session.compact.result", new { sessionId = sid, performed = ok, note }, sid, ct);
                break;
            }


            case "models.refresh":
                if (_catalog is null) { await SendErrorAsync(c, requestId, "no catalog", ct); break; }
                try
                {
                    await _catalog.RefreshAsync(ct);
                    await SendAsync(c, "models.updated", new { models = ModelJson() }, null, ct);
                }
                catch (Exception ex)
                {
                    await SendAsync(c, "models.refreshFailed", new { message = ex.Message }, null, ct);
                }
                await SendAckAsync(c, requestId, ct);
                break;

            case "plugin.reload":
            {
                var pid = S(p, "pluginId");
                if (_facade is null || pid is null)
                {
                    await SendErrorAsync(c, requestId, "plugin manager unavailable", ct);
                    break;
                }
                // Reload is host-owned work, NOT connection work: a reload of the
                // Web plugin itself stops the old generation, which closes this very
                // WS connection and would cancel the connection token mid-LoadAsync.
                // So run it on a bounded host token and swallow sends that race the
                // connection teardown (the browser reconnects and re-lists plugins).
                bool ok;
                using (var reloadCts = new CancellationTokenSource())
                {
                    reloadCts.CancelAfter(90_000); // generous bound; host owns its own reload
                    try { ok = await _facade.ReloadAsync(pid, reloadCts.Token); }
                    catch (Exception ex)
                    {
                        _log.Error($"plugin.reload {pid} threw: {ex.Message}", ex);

                        ok = false;
                    }
                }
                try
                {
                    await SendAsync(c, ok ? "plugin.reloaded" : "plugin.reloadFailed", new { pluginId = pid }, null, ct);
                    // PLAN §41: a per-plugin state event so the UI can update just this
                    // plugin's row (e.g. mark it "failed") rather than only the list.
                    await SendAsync(c, "plugin.state", new { pluginId = pid, state = ok ? "active" : "failed" }, null, ct);
                    await SendAsync(c, "plugins.state", new { plugins = PluginJson() }, null, ct);
                    await SendAsync(c, "ui.panels", new { panels = PanelJson() }, null, ct);
                    await SendAckAsync(c, requestId, ct);
                }
                catch { /* connection may be gone (Web reloaded itself) */ }
                break;
            }

            case "plugins.list":
            {
                if (_facade is not null)
                    await SendAsync(c, "plugins.state", new { plugins = PluginJson() }, null, ct);
                await SendAckAsync(c, requestId, ct);
                break;
            }

            case "plugin.reloadAll":
                if (_facade is not null)
                {
                    await _facade.ReloadAllAsync(ct);
                    // PLAN §41: per-plugin state events for each reloaded plugin.
                    foreach (var st in _facade.GetStatus())
                        await SendAsync(c, "plugin.state",
                            new { pluginId = st.Id, state = MapPluginState(st.State) }, null, ct);
                    await SendAsync(c, "plugins.state", new { plugins = PluginJson() }, null, ct);
                    await SendAsync(c, "ui.panels", new { panels = PanelJson() }, null, ct);
                }
                await SendAckAsync(c, requestId, ct);
                break;

            case "commands.list":
            {
                var cmds = (_commands?.All() ?? [])
                    .Select(c => new { name = c.Name, description = c.Description, requires = c.Requires })
                    .OrderBy(c => c.name)
                    .ToList();
                await c.SendSafeAsync(Envelope("ack", requestId, null, new { commands = cmds }), ct);
                break;
            }

            case "ui.panels.list":
            {
                await SendAsync(c, "ui.panels", new { panels = PanelJson() }, null, ct);
                await SendAckAsync(c, requestId, ct);
                break;
            }

            case "workspace.files":
            {
                // PLAN §37 @ picker: return a flat, bounded list of workspace files.
                var wsDir = S(p, "path");
                var query = S(p, "query") ?? "";
                var limit = Math.Min(I(p, "limit") == 0 ? 50 : I(p, "limit"), 200);
                var files = new List<object>();
                try
                {
                    if (!string.IsNullOrEmpty(wsDir) && Directory.Exists(wsDir))
                    {
                        var q = query.Trim();
                        var filtered = new DirectoryInfo(wsDir).EnumerateFiles("*", new EnumerationOptions
                        {
                            RecurseSubdirectories = true,
                            IgnoreInaccessible = true,
                            AttributesToSkip = FileAttributes.ReparsePoint,
                        })
                            .Where(f =>
                            {
                                var norm = f.FullName.Replace('\\', '/');
                                return !f.Name.StartsWith(".")
                                    && !norm.Contains("/.git/", StringComparison.OrdinalIgnoreCase)
                                    && !norm.Contains("/node_modules/", StringComparison.OrdinalIgnoreCase)
                                    && !norm.Contains("/bin/", StringComparison.OrdinalIgnoreCase)
                                    && !norm.Contains("/obj/", StringComparison.OrdinalIgnoreCase)
                                    && f.Extension.Length > 0;
                            })
                            .Where(f => q.Length == 0
                                || f.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                                || Path.GetRelativePath(wsDir, f.FullName).Contains(q, StringComparison.OrdinalIgnoreCase))
                            .OrderBy(f =>
                            {
                                var rel = Path.GetRelativePath(wsDir, f.FullName);
                                return q.Length > 0 && f.Name.StartsWith(q, StringComparison.OrdinalIgnoreCase) ? 0
                                    : q.Length > 0 && rel.StartsWith(q, StringComparison.OrdinalIgnoreCase) ? 1 : 2;
                            })
                            .ThenBy(f => Path.GetRelativePath(wsDir, f.FullName), StringComparer.OrdinalIgnoreCase)
                            .Take(limit)
                            .ToList();
                        foreach (var f in filtered)
                        {
                            var rel = Path.GetRelativePath(wsDir, f.FullName);
                            files.Add(new { path = rel, full = f.FullName, size = f.Length, mtime = f.LastWriteTimeUtc.ToString("O") });
                        }
                    }
                }
                catch (Exception ex) { _log.Warning($"workspace.files failed: {ex.Message}"); }
                await c.SendSafeAsync(Envelope("ack", requestId, null, new { files }), ct);
                break;
            }

            case "config.update":
            {
                if (_config is null || !p.TryGetProperty("plugins", out var plugins) ||
                    plugins.ValueKind != JsonValueKind.Object)
                {
                    await SendErrorAsync(c, requestId, "config update unavailable", ct);
                    break;
                }
                foreach (var (key, value) in EnumerateObject(plugins))
                    await _config.MergePluginAsync(key, value, ct);
                await SendAckAsync(c, requestId, ct);
                break;
            }

            default:
                if (requestId is not null)
                    await SendErrorAsync(c, requestId, $"unknown command {type}", ct);
                break;
        }
    }

    private async Task ChatSendAsync(Client c, string? requestId, JsonElement p, CancellationToken ct)
    {
        if (_runner is null) { await SendErrorAsync(c, requestId, "agent runner unavailable", ct); return; }
        if (_store is null) { await SendErrorAsync(c, requestId, "session store unavailable", ct); return; }

        var text = S(p, "text") ?? "";
        var model = S(p, "model");
        var reasoning = S(p, "reasoning");
        var sessionId = S(p, "sessionId");
        var payloadWorkspace = S(p, "workspace");

        // Ensure a session exists (the runner persists the initial user entry).
        string? sid = sessionId;
        SessionInfo? info = null;
        if (!string.IsNullOrEmpty(sid))
            info = await _store.GetAsync(sid, ct);
        if (info is null)
        {
            info = await _store.CreateAsync(payloadWorkspace, ct);
            sid = info.Id;
            await SendAsync(c, "session.created", ToSessionJson(info), sid, ct);
        }

        // The session is authoritative for workspace and, when the client does
        // not override them, its previously selected model/reasoning.
        var workspace = info.WorkspacePath ?? payloadWorkspace;
        model ??= info.ModelId;
        reasoning ??= info.ReasoningLevel;
        if (string.IsNullOrEmpty(model) && _catalog?.Models.FirstOrDefault() is { } firstModel)
            model = firstModel.ModelId;

        // PLAN §14/§31: remember the model/reasoning for the session so a later
        // open restores them into the composer.
        if (!string.IsNullOrEmpty(model))
            await _store.SetModelAsync(sid, model, string.IsNullOrEmpty(reasoning) ? null : reasoning, ct);

        var started = await _runner.StartRunAsync(new AgentRunRequest(sid, workspace, model, text,
            string.IsNullOrEmpty(reasoning) ? null : reasoning, null, null), ct);
        if (!string.IsNullOrEmpty(started.Error))
        {
            await SendErrorAsync(c, requestId, started.Error, ct);
            return;
        }

        // The client already shows an optimistic user row. This authoritative
        // echo arrives only after the runner accepted the turn, so a rejected
        // submission cannot look like work that silently vanished.
        await SendAsync(c, "session.entry",
            new { entry = new { type = "user_message", text } as object }, sid, ct);
        await SendAsync(c, "agent.state", new { state = "Preparing" }, sid, ct);
        await SendAckAsync(c, requestId, ct);
    }

    private async Task SessionCreateAsync(Client c, string? requestId, JsonElement p, CancellationToken ct)
    {
        if (_store is null) { await SendErrorAsync(c, requestId, "no session store", ct); return; }
        var ws = S(p, "workspace");
        var created = await _store.CreateAsync(string.IsNullOrEmpty(ws) ? null : ws, ct);
        await SendAsync(c, "session.created", ToSessionJson(created), created.Id, ct);
        await SendAsync(c, "session.entries",
            new { entries = new List<object>(), replace = true, total = 0,
                  hasMore = false, beforeSequence = 0 }, created.Id, ct);
        await SendAckAsync(c, requestId, ct);
    }

    private async Task SessionOpenAsync(Client c, string? requestId, JsonElement p, CancellationToken ct)
    {
        if (_store is null) { await SendErrorAsync(c, requestId, "no session store", ct); return; }
        var sid = S(p, "sessionId");
        if (sid is null) { await SendAckAsync(c, requestId, ct); return; }
        var info = await _store.GetAsync(sid, ct);
        if (info is null) { await SendErrorAsync(c, requestId, "session not found", ct); return; }
        await SendAsync(c, "session.updated", ToSessionJson(info), sid, ct);
        // PLAN §38: don't load/render an entire giant session — load the LATEST
        // ~200 entries; older ones are fetched on scroll-up via session.older.
        const int pageSize = 200;
        var total = info.EntryCount;
        var offset = Math.Max(0, total - pageSize);
        var entries = await _store.ReadAsync(sid, offset, pageSize, ct);
        // PLAN §38: beforeSequence is the sequence of the OLDEST entry loaded —
        // the client requests session.older { beforeSequence } on scroll-up.
        var beforeSeq = entries.Count > 0 ? entries[0].Sequence : 0;
        await SendAsync(c, "session.entries",
            new { entries = EntriesToJson(entries), replace = true, total = total,
                  hasMore = total > entries.Count, beforeSequence = beforeSeq }, sid, ct);
        await SendAckAsync(c, requestId, ct);
    }

    /// <summary>PLAN §38: scroll-up pagination — entries older than a sequence.</summary>
    private async Task SessionOlderAsync(Client c, string? requestId, JsonElement p, CancellationToken ct)
    {
        if (_store is null) { await SendErrorAsync(c, requestId, "no session store", ct); return; }
        var sid = S(p, "sessionId");
        if (sid is null) { await SendAckAsync(c, requestId, ct); return; }
        var beforeSequence = I(p, "beforeSequence");
        var count = I(p, "count");
        if (count <= 0) count = 200;
        if (beforeSequence <= 0)
        {
            await SendAsync(c, "session.older", new { entries = new List<object>(), beforeSequence = 0, hasMore = false }, sid, ct);
            await SendAckAsync(c, requestId, ct);
            return;
        }
        var entries = await _store.ReadBeforeAsync(sid, beforeSequence, count, ct);
        var newOldest = entries.Count > 0 ? entries[0].Sequence : 0;
        await SendAsync(c, "session.older",
            new { entries = EntriesToJson(entries), beforeSequence = newOldest,
                  hasMore = entries.Count == count }, sid, ct);
        await SendAckAsync(c, requestId, ct);
    }

    private async Task BroadcastSession(string sid, CancellationToken ct)
    {
        if (_store is null) return;
        var info = await _store.GetAsync(sid, ct);
        if (info is not null)
            await BroadcastAsync("session.updated", ToSessionJson(info), sid, ct);
    }

    // ---- shape helpers ------------------------------------------------------

    private async Task BootstrapCatalogAsync()
    {
        try
        {
            if (_catalog is not null && _catalog.IsStale)
                await _catalog.RefreshAsync(CancellationToken.None);
        }
        catch { /* provider may not be ready yet */ }
    }

    private object[] ModelJson()
    {
        if (_catalog is null) return [];
        // PLAN §13: fully data-driven — window, max output, modalities and
        // reasoning levels all come from the provider's parsed metadata
        // (no netPI capability table, no hard-coded lists).
        return _catalog.Models.Select(m => new
        {
            id = m.ModelId,
            contextWindow = m.ContextWindowTokens,
            maxOutputTokens = m.MaxOutputTokens,
            inputModalities = m.InputModalities,
            reasoning = m.ReasoningLevels is { } levels && levels.Count > 0
                ? new { levels = levels.ToArray(), defaultLevel = m.DefaultReasoningLevel }
                : (object?)null,
        }).ToArray();
    }

    private object[] PanelJson() =>
        _ctx.WebPanels.All().Select(p => new
        {
            id = p.Id,
            title = p.Title,
            icon = p.Icon,
            entryUrl = p.EntryUrl,
            order = p.Order,
        }).ToArray();

    private object[] PluginJson()
    {
        if (_facade is null) return [];
        return _facade.GetStatus().Select(s => new
        {
            s.Id,
            s.Name,
            version = s.Version ?? "",
            generation = s.Generation,
            state = MapPluginState(s.State),
            activeLeases = s.ActiveLeases,
            lastError = s.LastError,
        }).ToArray();
    }

    private static string MapPluginState(string state) => state.ToLowerInvariant() switch
    {
        "active" => "active",
        "loading" => "draining",
        "draining" => "draining",
        "failed" => "failed",
        "unloading" => "unloading",
        "unloaded" => "collected",
        _ => "active",
    };

    private static object ToSessionJson(SessionInfo s) => new
    {
        id = s.Id,
        title = s.Title ?? "",
        workspace = s.WorkspacePath ?? "",
        modelId = s.ModelId,
        reasoningLevel = s.ReasoningLevel,
        createdAt = s.CreatedAt.ToUnixTimeMilliseconds(),
        updatedAt = s.UpdatedAt.ToUnixTimeMilliseconds(),
    };

    /// <summary>
    /// Convert a run of stored entries to the §41 <c>session.entries</c> wire
    /// shape. The agent persists tool outputs as separate <see cref="MessageRole.Tool"/>
    /// entries; those are merged into the <c>toolResults</c> of the preceding
    /// assistant_message so the frontend can render them (PLAN §41).
    /// </summary>
    private List<object> EntriesToJson(IReadOnlyList<SessionEntry> entries)
    {
        var outEntries = new List<object>();
        WireAssistant? lastAssistant = null;
        foreach (var e in entries)
        {
            if (e.Kind == EntryKind.Message && e.Message is { } m)
            {
                switch (m.Role)
                {
                    case MessageRole.User:
                        lastAssistant = null;
                        outEntries.Add(new { type = "user_message", text = TextOf(m) } as object);
                        break;

                    case MessageRole.Assistant:
                        var (parts, toolResults) = SplitAssistant(m);
                        lastAssistant = new WireAssistant { Parts = parts, ToolResults = toolResults };
                        outEntries.Add(lastAssistant);
                        break;

                    case MessageRole.Tool:
                        // Merge tool outputs into the preceding assistant entry.
                        if (lastAssistant is not null)
                            lastAssistant.ToolResults.AddRange(ToolResultsJson(m));
                        break;
                }
            }
            else if (e.Kind == EntryKind.Compaction && e.Payload is { } pl)
            {
                lastAssistant = null;
                var root = JsonDocument.Parse(pl.ToString()).RootElement;
                outEntries.Add(new { type = "compaction", summary = S(root, "summary") } as object);
            }
        }
        return outEntries;
    }

    /// <summary>Mutable wire form of an assistant transcript entry (PLAN §41).</summary>
    private sealed class WireAssistant
    {
        public string Type => "assistant_message";
        public List<object> Parts { get; set; } = [];
        public List<object> ToolResults { get; set; } = [];
        public object? Usage { get; set; }
    }

    private static string TextOf(AgentMessage m) =>
        string.Join("\n", m.Parts.OfType<TextPart>().Select(t => t.Text));

    private static (List<object> Parts, List<object> ToolResults) SplitAssistant(AgentMessage m)
    {
        var parts = new List<object>();
        var toolResults = new List<object>();
        foreach (var part in m.Parts)
        {
            switch (part)
            {
                case ThinkingPart t:
                    parts.Add(new { type = "thinking", text = t.Text });
                    break;
                case TextPart t:
                    parts.Add(new { type = "text", text = t.Text });
                    break;
                case ToolCallPart tc:
                    parts.Add(new
                    {
                        type = "tool_call",
                        id = tc.Id,
                        name = tc.Name,
                        argumentsJson = tc.Arguments.GetRawText(),
                    });
                    break;
                case ToolResultPart tr:
                    toolResults.Add(new
                    {
                        id = tr.ToolCallId,
                        output = string.Join("\n", tr.Parts.OfType<TextPart>().Select(x => x.Text)),
                        isError = tr.IsError,
                    });
                    break;
            }
        }
        return (parts, toolResults);
    }

    private List<object> ToolResultsJson(AgentMessage toolMsg)
    {
        var list = new List<object>();
        foreach (var part in toolMsg.Parts.OfType<ToolResultPart>())
        {
            list.Add(new
            {
                id = part.ToolCallId,
                output = string.Join("\n", part.Parts.OfType<TextPart>().Select(x => x.Text)),
                isError = part.IsError,
            });
        }
        return list;
    }

    // ---- send / broadcast ---------------------------------------------------

    private async Task BroadcastAsync(string type, object payload, string? sid, CancellationToken ct)
    {
        var json = Envelope(type, null, sid, payload);
        List<Client> snapshot;
        lock (_clientsLock) snapshot = _clients.ToList();
        foreach (var c in snapshot)
            await c.SendSafeAsync(json, ct);
    }

    private const int DeltaFlushMs = 20;

    /// <summary>PLAN §39: buffer a delta; a pending flush is scheduled for the
    /// next ~20ms window (≈ one browser frame).</summary>
    private void EnqueueDelta(string? sid, string lane, string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        var b = _batchers.GetOrAdd(sid ?? "", _ => new DeltaBatcher());
        bool schedule;
        lock (b.Gate)
        {
            if (!b.Buffers.TryGetValue(lane, out var buf))
            {
                buf = new System.Text.StringBuilder();
                b.Buffers[lane] = buf;
            }
            buf.Append(text);
            schedule = b.Pending is null;
        }
        if (!schedule) return;
        b.Pending = Task.Delay(DeltaFlushMs).ContinueWith(_ => FlushDeltas(sid),
            System.Threading.Tasks.TaskScheduler.Default);
    }

    /// <summary>PLAN §39: drain all buffered lanes for a session now
    /// (window elapsed, or a boundary event forced the flush).</summary>
    /// <summary>Synchronous drain: emit every buffered lane now (no 120 ms delay),
    /// returning the drained "text" lane (the completed assistant text), or null.
    /// Boundary events (thinking.completed, model-completed, ...) force this so the
    /// wire order is deltas-then-completed.</summary>
    private string? FlushDeltas(string? sid)
    {
        if (sid is null) return null;
        var key = sid ?? "";
        if (!_batchers.TryGetValue(key, out var b)) return null;
        List<(string lane, string text)> drained;
        lock (b.Gate)
        {
            b.Pending = null; // cancel the delayed flush — we are draining now
            if (b.Buffers.Count == 0)
                return null;
            drained = b.Buffers
                .Select(kv => (kv.Key, kv.Value.ToString()))
                .Where(x => x.Item2.Length > 0)
                .ToList();
            b.Buffers.Clear();
        }
        foreach (var (lane, text) in drained)
        {
            if (lane == "thinking")
                SendEvent("thinking.delta", new { text }, sid);
            else if (lane == "text")
            {
                SendEvent("text.delta", new { text }, sid);
                _completedText.AddOrUpdate(sid ?? "", text, (_, old) => old + text);
            }
            else if (lane.StartsWith("args:"))
                SendEvent("tool.args", new { id = lane[5..], args = text }, sid);
        }
        _batchers.TryRemove(key, out _);
        var textLane = drained.FirstOrDefault(x => x.lane == "text");
        return string.IsNullOrEmpty(textLane.text) ? null : textLane.text;
    }

    
    private void CompleteAssistantTurn(string? sid)
    {
        var key = sid ?? "";
        if (!_assistantOpen.TryRemove(key, out _)) return;

        SendEvent("assistant.completed", new
        {
            usage = _totalTokens > 0 ? new
            {
                promptTokens = _promptTokens,
                completionTokens = _completionTokens,
                totalTokens = _totalTokens,
            } : (object?)null,
        }, sid);
        _promptTokens = _completionTokens = _totalTokens = 0;
    }

    private void SendEvent(string type, object payload, string? sid) =>
        _ = Task.Run(async () =>
        {
            var json = Envelope(type, null, sid, payload);
            List<Client> snapshot;
            lock (_clientsLock) snapshot = _clients.ToList();
            foreach (var c in snapshot)
                await c.SendSafeAsync(json, CancellationToken.None);
        });

    private async Task SendAsync(Client c, string type, object payload, string? sid, CancellationToken ct)
    {
        var json = Envelope(type, null, sid, payload);
        await c.SendSafeAsync(json, ct);
    }

    private static async Task SendAckAsync(Client c, string? requestId, CancellationToken ct)
    {
        if (requestId is null) return;
        var json = JsonSerializer.Serialize(new { type = "ack", requestId });
        await c.SendSafeAsync(json, ct);
    }

    private static async Task SendErrorAsync(Client c, string? requestId, string message, CancellationToken ct)
    {
        if (requestId is null) return;
        var json = Envelope("error", requestId, null, new { message });
        await c.SendSafeAsync(json, ct);
    }

    private static string Envelope(string type, string? requestId, string? sessionId, object? payload)
    {
        var dict = new Dictionary<string, object?>
        {
            ["type"] = type,
            ["payload"] = payload ?? new Dictionary<string, object?>(),
        };
        if (requestId is not null) dict["requestId"] = requestId;
        if (sessionId is not null) dict["sessionId"] = sessionId;
        return JsonSerializer.Serialize(dict, JsonOpts);
    }

    private static IEnumerable<KeyValuePair<string, JsonElement>> EnumerateObject(JsonElement obj)
    {
        foreach (var prop in obj.EnumerateObject())
            yield return new KeyValuePair<string, JsonElement>(prop.Name, prop.Value);
    }

    private sealed class Client
    {
        private readonly SemaphoreSlim _sendGate = new(1, 1);

        public Client(WebSocket ws, CancellationToken ct)
        {
            Ws = ws;
            Ct = ct;
        }

        public WebSocket Ws { get; }
        public CancellationToken Ct { get; }

        /// <summary>Serialize all writes on this socket (WebSocket is not thread-safe).</summary>
        public async Task SendSafeAsync(string json, CancellationToken ct)
        {
            try
            {
                await _sendGate.WaitAsync(ct);
                try
                {
                    if (Ws.State == WebSocketState.Open)
                        await Ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(json)),
                            WebSocketMessageType.Text, true, Ct);
                }
                finally
                {
                    _sendGate.Release();
                }
            }
            catch (OperationCanceledException) { }
            catch { /* client gone */ }
        }
    }

    private async Task<IResult> OpenFileAsync(HttpContext context)
    {
        var rawPath = context.Request.Query["path"].ToString();
        if (string.IsNullOrWhiteSpace(rawPath))
            return Results.BadRequest("path is required");

        string fullPath;
        if (Path.IsPathRooted(rawPath))
        {
            fullPath = Path.GetFullPath(rawPath);
        }
        else
        {
            var sid = context.Request.Query["sessionId"].ToString();
            if (string.IsNullOrWhiteSpace(sid) || _store is null)
                return Results.BadRequest("sessionId is required for relative paths");

            var session = await _store.GetAsync(sid, context.RequestAborted);
            if (session?.WorkspacePath is null)
                return Results.NotFound("session/workspace not found");

            fullPath = Path.GetFullPath(Path.Combine(session.WorkspacePath, rawPath));
        }

        if (!File.Exists(fullPath))
            return Results.NotFound("file not found");

        if (!TryGetContentType(fullPath, out var contentType))
            contentType = "application/octet-stream";

        var download = context.Request.Query["download"] == "1";
        return Results.File(
            fullPath,
            contentType,
            fileDownloadName: download ? Path.GetFileName(fullPath) : null,
            enableRangeProcessing: true);
    }

    private static bool TryGetContentType(string path, out string? contentType)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        contentType = ext switch
        {
            ".cs" or ".fs" or ".vb" or ".js" or ".jsx" or ".ts" or ".tsx" => "text/javascript; charset=utf-8",
            ".json" or ".jsonl" or ".yaml" or ".yml" or ".xml" => "text/plain; charset=utf-8",
            ".html" or ".htm" => "text/html; charset=utf-8",
            ".css" or ".scss" => "text/css; charset=utf-8",
            ".svelte" or ".vue" or ".py" or ".ps1" or ".sh" or ".bash" or ".cmd" or ".bat"
                or ".sql" or ".toml" or ".ini" or ".cfg" or ".props" or ".targets"
                or ".csproj" or ".sln" or ".md" or ".txt" => "text/plain; charset=utf-8",
            _ => null,
        };
        return contentType is not null;
    }


    private object Bootstrap()
    {
        return new
        {
            status = "ok",
            version = "0.1.0",
            agent = _agent is not null ? MapAgentState(_agent.State.State) : "Idle",
            models = _catalog?.Models.Count ?? 0,
        };
    }
}

