using System.Diagnostics;
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
    private readonly int _maxWsMessageBytes;
    private readonly IPluginLogger _log;
    // astra-1 §11a (F/A): idempotency table for chat.send operationIds.
    private readonly SendIdempotency _sends = new();
    private WebApplication? _app;

    /// <summary>Host build identity (set by the launcher via NETPI_HOST_BUILD_ID, or
    /// the host dll's SHA-256 when started manually).</summary>
    private static string BuildId { get; } = ResolveBuildId();

    private static string ResolveBuildId()
    {
        var fromEnv = Environment.GetEnvironmentVariable("NETPI_HOST_BUILD_ID");
        if (!string.IsNullOrEmpty(fromEnv)) return fromEnv;
        try
        {
            var dll = Path.Combine(AppContext.BaseDirectory, "netPI.Host.dll");
            if (File.Exists(dll))
            {
                using var sha = System.Security.Cryptography.SHA256.Create();
                return Convert.ToHexString(sha.ComputeHash(File.ReadAllBytes(dll))).ToLowerInvariant();
            }
        }
        catch { }
        return "unknown";
    }

    // ---- services resolved at load (reload-safe; may be null) -------------
    private IAgentRunner? _runner;
    private IAgentRuntime? _agent;
    private IModelCatalog? _catalog;
    /// <summary>
    /// astra-1 P3: the session store is resolved per access, NEVER cached.
    /// The old <c>_store ??=</c> cache pinned an unleased instance forever —
    /// a <c>plugin.reload</c> of netpi.storage.sqlite would leave this surface
    /// talking to a dead generation. Each access acquires a short-lived lease
    /// on the CURRENT store (released before the next statement), so a reload
    /// is picked up automatically. Multi-step operations that await several
    /// times capture <see cref="StoreLocal"/> once and hold it for the whole
    /// operation.
    /// </summary>
    private ISessionStore? Store
    {
        get
        {
            try { using var lease = _ctx.Services.Acquire<ISessionStore>("sessions"); return lease.Value; }
            catch (ServiceUnavailableException) { return null; }
        }
    }

    /// <summary>
    /// astra-1 P3: acquire the CURRENT session store once and hold the lease
    /// across a multi-await operation (released by the caller in finally).
    /// </summary>
    private (ISessionStore Store, IValueLease<ISessionStore> Lease)? StoreLocal()
    {
        try
        {
            var lease = _ctx.Services.Acquire<ISessionStore>("sessions");
            return (lease.Value, lease);
        }
        catch (ServiceUnavailableException) { return null; }
    }

    /// <summary>
    /// astra-1 P3: acquire a store lease the caller holds with
    /// <c>using var</c> and shadows over a local <c>Store</c> for the whole
    /// multi-await operation — ONE store instance for the operation, the
    /// lease released at scope exit (early returns included).
    /// </summary>
    private IValueLease<ISessionStore>? AcquireStoreLease()
    {
        try { return _ctx.Services.Acquire<ISessionStore>("sessions"); }
        catch (ServiceUnavailableException) { return null; }
    }
    private IPluginManagerFacade? _facade;
    private IHostConfigUpdate? _config;
    private ISteeringQueue? _steering;
    private ICompaction? _compaction;
    /// <summary>astra-1 D2 (slice 2): the last operation id applied per
    /// session — a repeated <c>session.project.applied</c> for the SAME operation
    /// id is a no-op (duplicate boundary/idle events never re-broadcast).</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _lastProjectApplied =
        new(System.StringComparer.Ordinal);
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

    /// <summary>astra-1 F/#11: the run id that last opened the assistant turn per session.
    /// With more than one concurrent run, a delayed wire event from an OLDER run must not
    /// pollute the (single) per-session batcher; <c>ForwardModelWire</c> drops any
    /// <see cref="AgentEventType.ModelStreamEvent"/> whose <c>RunId</c> differs from the
    /// session's active run. Null-RunId events (e.g. the test bus) pass through unchanged.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _activeRun = new();

    /// <summary>Per-session accumulated assistant text since the last
    /// model-completed (PLAN §41: text.completed must carry the full final text,
    /// but the deltas were already flushed by the 120 ms timer).</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _completedText = new();

    // ---- hub ---------------------------------------------------------------
    private readonly HashSet<Client> _clients = [];
    private readonly object _clientsLock = new();

    private int _promptTokens, _completionTokens, _totalTokens;

    public WebApp(IPluginContext ctx, int port, string staticRoot, int maxWsMessageBytes, IPluginLogger log)
    {
        _ctx = ctx;
        _port = port;
        _staticRoot = staticRoot;
        _maxWsMessageBytes = maxWsMessageBytes <= 0 ? 1024 * 1024 : maxWsMessageBytes;
        _log = log;
    }

    public async ValueTask StartAsync(CancellationToken ct)
    {
        _runner = Resolve<IAgentRunner>("runner");
        _agent = Resolve<IAgentRuntime>("agent");
        _catalog = Resolve<IModelCatalog>("catalog");
        _facade = Resolve<IPluginManagerFacade>("plugins");
        _config = Resolve<IHostConfigUpdate>("host-config");
        _steering = Resolve<ISteeringQueue>("steering");
        _compaction = Resolve<ICompaction>("compaction");
        _commands = Resolve<NetPI.Abstractions.ICommandRegistry>("commands");

        _subs.Add(_ctx.Events.Subscribe<AgentEvent>(OnAgentEvent));
        _subs.Add(_ctx.Events.Subscribe<ModelRequestDiagnostics>(OnModelDiagnostics));
        _subs.Add(_ctx.Events.Subscribe<PluginUpdateCompletedEvent>(OnPluginUpdateCompleted));
        // astra-2 section 13: assignment/lane lifecycle (NetPI.Orchestration + lanes publish these).
        _subs.Add(_ctx.Events.Subscribe<AgentLifecycleEvent>(OnAgentLifecycleEvent));
        _subs.Add(_ctx.Events.Subscribe<LanesStateEvent>(OnLanesStateEvent));

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

        // Localhost-only identity probe for launchers: proves the listener is a
        // netPI host and reports its build id / runtime dirs, so "port open" is
        // never mistaken for "our host is up". (PLAN §49 / astra-1 P0.3)
        app.MapGet("/identity", (HttpContext c) =>
        {
            c.Response.ContentType = "application/json";
            return c.Response.WriteAsJsonAsync(new
            {
                name = "netPI",
                buildId = BuildId,
                hostDir = AppContext.BaseDirectory,
                netpiHome = Environment.GetEnvironmentVariable("NETPI_HOME"),
                pluginDir = Environment.GetEnvironmentVariable("NETPI_PLUGINS"),
                projectRoot = Environment.GetEnvironmentVariable("NETPI_PROJECT_ROOT"),
                port = _port,
            });
        });

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
        // Localhost-only "open with the OS default application" (Windows:
        // Explorer / shell). Same path resolution as /api/file; the request
        // never leaves the machine, so the spawned process is safe.
        //
        // astra-1 §11a (F/P5): this is a MUTATING shell action, so it is a POST
        // (never GET — a link/iframe can trigger a GET without user intent) and
        // is Origin/Host-validated before it is allowed to run. The web UI POSTs.
        app.MapPost("/api/open", async (HttpContext c) =>
        {
            if (!ControlOriginIsAllowed(c.Request.Host.Host, c.Request.Headers.Origin.ToString(), _port))
            {
                _log.Warning($"api/open: rejected (origin={c.Request.Headers.Origin.ToString() ?? "(none)"} host={c.Request.Host.Host})");
                return Results.Forbid();
            }
            var result = await OpenInShellAsync(c);
            return result;
        });
        // A GET (or any non-POST) to /api/open is a mutation by a non-POST route:
        // reject it so a stray link/iframe cannot trigger a shell-open.
        app.MapGet("/api/open", (HttpContext c) => Results.StatusCode(StatusCodes.Status405MethodNotAllowed));
        // SPA routing: any request that matched no file and no explicit route
        // (including the bare "/") serves index.html from the frontend build.
        if (RootsTheFrontend())
            app.MapFallback(async context =>
            {
                context.Response.ContentType = "text/html";
                await context.Response.SendFileAsync(Path.Combine(Path.GetFullPath(_staticRoot), "index.html"));
            });

        try
        {
            await app.StartAsync(ct);
        }
        catch
        {
            // astra-1 P3: a partial start must release what it acquired —
            // Kestrel listener (if bound) plus every subscription.
            foreach (var sub in _subs) { try { sub.Dispose(); } catch { } }
            _subs.Clear();
            try { await app.DisposeAsync(); } catch { }
            throw;
        }
        _app = app;
        _ = Task.Run(BootstrapCatalogAsync);
        // One-shot backfill: existing untitled sessions get their title from
        // the first user message (new sessions are auto-titled in ChatSendAsync).
        _ = Task.Run(() => BackfillSessionTitlesAsync(CancellationToken.None));
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

    /// <summary>
    /// astra-1 §F: re-resolve the five plugin-owned services from the CURRENT
    /// registry. The host's registry removes the old generation's entries on
    /// reload (RemoveAllFor) before the new generation re-registers, so a plain
    /// re-resolve picks up the new generation (a failure that keeps the old
    /// generation active resolves to the SAME instances). Called from
    /// <see cref="OnPluginUpdateCompleted"/> after any plugin update; without it
    /// a <c>plugin.reload</c> of netpi.agent / netpi.autocompact /
    /// netpi.provider.aiproxy would leave this surface calling the RETIRED
    /// generation's runner/agent/steering/compaction/catalog (chat.send,
    /// agent.cancel, runs.list, compaction, models.refresh).
    /// </summary>
    private void RefreshReloadableServices()
    {
        _runner = Resolve<IAgentRunner>("runner");
        _agent = Resolve<IAgentRuntime>("agent");
        _catalog = Resolve<IModelCatalog>("catalog");
        _steering = Resolve<ISteeringQueue>("steering");
        _compaction = Resolve<ICompaction>("compaction");
    }

    // ---- agent event bridge ------------------------------------------------

    // ---- provider wire-decision bridge (PLAN §47) ---------------------------
    // The provider publishes one record per model request: which wire served it
    // (responses or chat), whether the session chain was used, and whether the
    // chat wire served as a TRANSPARENT FALLBACK after a responses-wire failure.
    // The chat UI surfaces the fallback case; netpi.diagnostics records the rest.
    private void OnModelDiagnostics(ModelRequestDiagnostics d)
    {
        SendEvent("model.wire", new
        {
            modelId = d.ModelId,
            wire = d.WireServed,
            requested = d.WireRequested,
            chained = d.Chained,
            fallback = d.Fallback,
            reason = d.FailureReason,
        }, d.SessionId);
    }

    /// <summary>
    /// astra-1 P5: the host lifecycle queue finished a queued update operation.
    /// Emit the §41 events to EVERY live connection from here — the runner is
    /// the only completion path (the request already ack'd with the operation
    /// id). Every event carries operationId so clients can match, and reconcile
    /// after a reconnect via the facade's GetOperation.
    /// </summary>
    private void OnPluginUpdateCompleted(PluginUpdateCompletedEvent ev)
    {
        // astra-1 §F: a Reload/ReloadAll swapped (or attempted to swap) one or
        // more plugin generations — re-resolve the five plugin-owned services
        // NOW so every subsequent operation uses the current registry entries.
        if (ev.Kind is PluginOperationKind.Reload or PluginOperationKind.ReloadAll)
            RefreshReloadableServices();
        switch (ev.Kind)
        {
            case PluginOperationKind.Reload:
                foreach (var r in ev.Results)
                {
                    if (r.PluginId is null) continue;
                    if (r.Outcome is PluginLifecycleOutcome.Applied or PluginLifecycleOutcome.RolledBack)
                    {
                        // a start failure must NEVER announce a swap.
                        SendEvent("plugin.reloaded", new { operationId = ev.OperationId, pluginId = r.PluginId, buildId = r.BuildId }, null);
                    }
                    else if (r.Outcome is PluginLifecycleOutcome.Failed or PluginLifecycleOutcome.RestartRequired)
                    {
                        SendEvent("plugin.reloadFailed", new { operationId = ev.OperationId, pluginId = r.PluginId, error = r.Error }, null);
                    }
                    // Deferred / Unchanged left the old build active — not a failure.
                }
                if (ev.PluginId is { } opPluginId)
                    SendEvent("plugin.state", new { operationId = ev.OperationId, pluginId = opPluginId, state = PluginStateNow(opPluginId) }, null);
                break;
            case PluginOperationKind.ReloadAll:
                foreach (var r in ev.Results)
                {
                    if (r.PluginId is null) continue;
                    SendEvent("plugin.state", new { operationId = ev.OperationId, pluginId = r.PluginId, state = PluginStateNow(r.PluginId) }, null);
                    if (r.Outcome is PluginLifecycleOutcome.Failed or PluginLifecycleOutcome.RestartRequired)
                        SendEvent("plugin.reloadFailed", new { operationId = ev.OperationId, pluginId = r.PluginId, error = r.Error }, null);
                }
                break;
            case PluginOperationKind.Scan:
                foreach (var scanId in ev.ScannedIds)
                    SendEvent("plugin.state", new { operationId = ev.OperationId, pluginId = scanId, state = PluginStateNow(scanId) }, null);
                SendEvent("plugin.scanned", new { operationId = ev.OperationId, loaded = ev.ScannedIds }, null);
                break;
        }
        SendEvent("plugins.state", new { operationId = ev.OperationId, plugins = PluginJson() }, null);
        SendEvent("ui.panels", new { panels = PanelJson() }, null);
    }

    /// <summary>astra-1 P5: the ACTUAL host state of one plugin ("failed" when unknown).</summary>
    private string PluginStateNow(string pluginId)
    {
        var st = _facade?.GetStatus().FirstOrDefault(x => string.Equals(x.Id, pluginId, StringComparison.OrdinalIgnoreCase));
        return st is null ? "failed" : MapPluginState(st.State);
    }

    /// <summary>
    /// astra-2 section 13: forward assignment lifecycle changes to the shell.
    /// </summary>
    private void OnAgentLifecycleEvent(AgentLifecycleEvent ev)
    {
        if (ev.Rows.Count == 0) return;
        foreach (var row in ev.Rows)
        {
            var json = JsonSerializer.Serialize(AgentRowDto(row));
            SendEvent("agent.updated", new { row = JsonSerializer.Deserialize<JsonElement>(json) }, row.SessionId);
        }
        // One grouped snapshot per event (the shell keeps the whole board current).
        SendEvent("agents.state", new { agents = ev.Rows.Select(AgentRowDto).ToList() }, null);
    }

    /// <summary>astra-2 section 13: forward the pool/lane capacity snapshot to the shell.</summary>
    private void OnLanesStateEvent(LanesStateEvent ev)
    {
        SendEvent("lanes.state", new { pools = ev.Pools.Select(p => new
        {
            poolId = p.PoolId, deploymentId = p.DeploymentId, modelId = p.ModelId,
            ownedCount = p.OwnedCount, queueCount = p.QueueCount, targetCapacity = p.TargetCapacity,
            enabled = p.Enabled, blockReason = p.BlockReason,
        }).ToList() }, null);
    }

    /// <summary>astra-2 section 12.2: camelCase web projection of an assignment row.</summary>
    private static object AgentRowDto(AgentAssignmentRow r) => new
    {
        assignmentId = r.AssignmentId,
        agentId = r.AgentId,
        teamId = r.TeamId,
        sessionId = r.SessionId,
        parentAgentId = r.ParentAgentId,
        lifecycle = AgentAssignmentLifecycleNames.Name(r.Lifecycle),
        nonTerminal = r.IsNonTerminal,
        phase = r.Phase.ToString(),
        executionMode = r.ExecutionMode == DeploymentExecutionMode.DirectCloud ? "cloud-direct" : "pooled",
        poolId = r.PoolId,
        laneId = r.LaneId,
        modelId = r.ModelId,
        title = r.Title,
        createdAt = r.CreatedAt,
        startedAt = r.StartedAt,
        endedAt = r.EndedAt,
        reason = r.Reason,
    };

    /// <summary>
    /// astra-2 §13/§16: persist a QUEUED send as a durable assignment so a full
    /// local pool is an accepted queue, not a rejection. The run id (the store's
    /// run_id == the runner's run identity) keys the row; <c>EnsureRootAgent</c>
    /// mints the session's root agent lazily and <c>CreateAssignmentAsync</c>
    /// persists a <c>Queued/Idle</c> row (the runner's terminal event reconciles
    /// against it via <c>GetByRunIdAsync</c>). Returns null on success, or an
    /// error message string. A missing orchestration-store plugin degrades to
    /// ack-only (null) — the runner still owns the in-memory queue — so a queued
    /// send never fails just because the orchestration stack is absent.
    /// </summary>
    private async Task<string?> PersistQueuedAssignmentAsync(string? sid, string? model, string? runId, CancellationToken ct)
    {
        var store = Resolve<IOrchestrationStore>("orchestration-store");
        if (store is null) return null; // no orchestration stack: ack-only
        try
        {
            var root = await store.EnsureRootAgentAsync(sid!, null, string.IsNullOrEmpty(model) ? "agent" : model!, ct);
            await store.CreateAssignmentAsync(
                runId ?? Guid.NewGuid().ToString("n"), root.AgentId, sid!, null, null,
                model, null, null, "assignment", null, ct);
            return null;
        }
        catch (Exception ex)
        {
            // Best-effort durability: the runner still owns the in-memory queue, so
            // a persistence failure is reported, not fatal to the send.
            return $"queued run could not be persisted: {ex.Message}";
        }
    }
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
                // astra-1 F/#11: a wire event from an OLDER run must not pollute
                // this session's (single) batcher. The session's STREAMING OWNER is
                // the run that currently owns its open assistant turn:
                //  - model-started opens a NEW turn: it DISCARDS any previous
                //    run's stale buffered/accumulated text (a cancelled or
                //    dropped run may never have delivered its boundary flush)
                //    and claims ownership. In normal operation the buffer is
                //    already empty here (each boundary flushes it), so the
                //    discard only bites for runs that did not complete.
                //  - any other stream event from a non-owning run (a delayed
                //    delta or terminal from an older run) is dropped.
                // Null-RunId events (the test bus) pass through unchanged.
                if (e.RunId is not null && sid is not null)
                {
                    var key = sid;
                    var kind = S(e.Payload.Value, "kind");
                    if (kind == "model-started")
                    {
                        _batchers.TryRemove(key, out _);
                        _completedText.TryRemove(key, out _);
                        _activeRun[key] = e.RunId;
                    }
                    else if (_activeRun.TryGetValue(key, out var owner) && owner != e.RunId)
                        break;
                }
                ForwardModelWire(e.Payload.Value, sid, e.RunId);
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


            case AgentEventType.TurnEmpty:
                // Cut-off guard: a turn ended with no answer and no tool calls.
                // Persist a notice for session replay + broadcast it live; the
                // nudge plugin (if active) is already steering a continuation.
                _ = EmitTurnEmptyNoticeAsync(sid);
                break;

            case AgentEventType.ProjectApplied when e.Payload is not null:
            {
                // astra-1 D2: the run's safe boundary applied a pending project
                // change (the runner held the session gate through the commit).
                // Bridge it to the client: refresh session metadata + clear the
                // "pending" indicator. Fire-and-forget (event path must not block).
                var p = e.Payload.Value;
                var opId = S(p, "operationId") ?? "";
                var pid = S(p, "projectId") ?? "";
                var pname = S(p, "projectName") ?? "";
                _ = ProjectAppliedBridgeAsync(sid, opId, pid, pname);
                break;
            }

            case AgentEventType.AgentCompleted:
            case AgentEventType.AgentCancelled:
            case AgentEventType.AgentFailed:
                // Final turns have no tool batch, so close them here.
                CompleteAssistantTurn(sid);
                SendEvent("agent.state", new { state = "Idle" }, sid);
                break;

        }
    }

    /// <summary>
    /// Cut-off notice (empty turn): persist a <see cref="EntryKind.Metadata"/>
    /// entry (wire "system_note") so the chat log shows the dead-end on session
    /// replay, and broadcast the same notice live as a <c>session.entry</c>
    /// event. Fire-and-forget (the hot model path must not block); failures are
    /// logged. The nudge plugin separately persists the continuation user message.
    /// </summary>
    private async Task EmitTurnEmptyNoticeAsync(string? sid)
    {
        if (sid is null) return;

        // The nudge plugin (priority 100) handles TurnEmpty BEFORE this surface
        // (priority 0) and enqueues its nudge synchronously; give the bus a beat
        // so the pending count reflects a real nudge when one is in flight.
        await Task.Delay(30);
        bool nudging = false;
        var steering = Resolve<ISteeringQueue>("steering");
        if (steering is not null)
            nudging = steering.PendingCount(sid) > 0;
        var text = "⚠ Model turn cut off (no answer, no tool call)" +
            (nudging ? " — nudge sent, continuing the run"
                     : " — no continuation, run ends here");

        using var _storeLease = AcquireStoreLease();
        var store = _storeLease?.Value;
        if (store is not null)
        {
            try
            {
                var entry = new SessionEntry(
                    Guid.NewGuid().ToString("n"), sid, EntryKind.Metadata, null,
                    System.Text.Json.JsonSerializer.SerializeToElement(
                        new { kind = "system_note", text }, new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase }),
                    DateTimeOffset.UtcNow);
                await store.AppendAsync(entry);
            }
            catch (Exception ex) { _log.Error($"EmitTurnEmptyNotice persist: {ex.Message}"); }
        }
        SendEvent("session.entry", new { entry = new { type = "system_note", text } as object }, sid);
    }

    private void ForwardModelWire(JsonElement w, string? sid, string? runId = null)
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
                // astra-1 F/#11: this run is now the session's active streaming run.
                // A delayed event from an older run (a different RunId) is dropped below.
                _activeRun[key] = runId;
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

        // astra-1 §11a (F/P5): the WS is the browser CONTROL surface — validate the
        // Host/Origin BEFORE accepting the upgrade (loopback binding is not origin
        // validation; a malicious page in any browser can cross-request 127.0.0.1).
        if (!ControlOriginIsAllowed(
                ctx.Request.Host.Host,
                ctx.Request.Headers.Origin.ToString(),
                _port))
        {
            _log.Warning($"ws: rejected upgrade from origin={ctx.Request.Headers.Origin.ToString() ?? "(none)"} host={ctx.Request.Host.Host}");
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
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
                // astra-1 §11a (F): assemble a COMPLETE message before decoding. A single
                // ReceiveAsync call may return only part of a message (a long pasted
                // prompt, a fragmented UTF-8 sequence, or split frames) and does not
                // equal one JSON command — keep receiving until EndOfMessage, enforce a
                // configurable total-size bound, and decode UTF-8 only once the full
                // byte sequence is present.
                var message = await ReceiveCompleteMessageAsync(ws, _maxWsMessageBytes, ctx.RequestAborted);
                if (message is null) break; // clean close / end
                await HandleCommandAsync(client, message, ctx.RequestAborted);
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException ex)
        {
            // astra-1 §11a (F): the message exceeded the bound — reject the connection.
            _log.Warning($"ws: oversized message ({ex.Message}); closing");
            try
            {
                await ws.CloseAsync(WebSocketCloseStatus.PolicyViolation, ex.Message, CancellationToken.None);
            }
            catch { }
        }
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

    /// <summary>
    /// astra-1 §11a (F): reads exactly one COMPLETE WebSocket message — accumulating
    /// frame bytes until <c>EndOfMessage</c> — bounded to <paramref name="maxBytes"/>
    /// total. Returns the UTF-8 text, or <c>null</c> when the socket closed, ended
    /// cleanly, or the message exceeded the bound (a <c>TooBigMessage</c> close is
    /// sent in the latter case). Cancellation propagates (caught by the ws loop).
    /// </summary>
    private static async Task<string?> ReceiveCompleteMessageAsync(
        WebSocket ws, int maxBytes, CancellationToken ct)
    {
        var frame = new byte[64 * 1024];
        return await AccumulateMessageAsync(
            (ct2) => FrameNextAsync(ws, frame, ct2), maxBytes, ct);
    }

    private static async Task<WsFrame?> FrameNextAsync(WebSocket ws, byte[] frame, CancellationToken ct)
    {
        var r = await ws.ReceiveAsync(new ArraySegment<byte>(frame), ct);
        if (r.MessageType == WebSocketMessageType.Close) return null;
        var bytes = new byte[r.Count];
        Array.Copy(frame, 0, bytes, 0, r.Count);
        return new WsFrame(bytes, r.EndOfMessage);
    }

    /// <summary>One frame of a WebSocket message: the bytes received in this ReceiveAsync
    /// call and whether they end the message. A complete message spans one or more frames.</summary>
    public readonly record struct WsFrame(byte[] Bytes, bool EndOfMessage);

    /// <summary>
    /// astra-1 §11a (F): accumulate the frames of ONE complete message until
    /// <c>EndOfMessage</c>, bounded to <paramref name="maxBytes"/> total, then decode
    /// the complete UTF-8 byte sequence exactly once. <paramref name="receiveNext"/>
    /// supplies the next frame (null = a close frame). A clean close returns null;
    /// an oversized message throws <see cref="WebSocketException"/> (caught by the ws
    /// loop, which closes the connection).
    /// </summary>
    public static async Task<string?> AccumulateMessageAsync(
        Func<CancellationToken, Task<WsFrame?>> receiveNext, int maxBytes, CancellationToken ct)
    {
        var acc = new List<byte>(Math.Min(maxBytes, 64 * 1024));
        while (true)
        {
            var frame = await receiveNext(ct);
            if (frame is null) return null; // clean close
            acc.AddRange(frame.Value.Bytes);
            if (acc.Count > maxBytes)
                throw new WebSocketException($"message exceeds {maxBytes} bytes");
            if (frame.Value.EndOfMessage)
                return Encoding.UTF8.GetString(acc.ToArray());
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

        if (StoreLocal() is { } bootStore)
        {
            try
            {
                var sessions = await bootStore.Store.ListAsync(50, 0, ct);
                var totalSessions = await bootStore.Store.CountAsync(ct);
                await SendAsync(c, "session.list",
                    new { sessions = sessions.Select(ToSessionJson).ToList(), offset = 0,
                          total = totalSessions, hasMore = sessions.Count < totalSessions }, null, ct);

                // PLAN §41: replay the active session's transcript so a client
                // (re)connecting after a host restart is not left with a blank
                // viewport. Mirrors SessionOpenAsync: latest 200 entries, more on
                // scroll-up.
                if (_agent?.State.ActiveSessionId is { } asid)
                {
                    var info = await bootStore.Store.GetAsync(asid, ct);
                    if (info is not null)
                    {
                        const int pageSize = 200;
                        var total = info.EntryCount;
                        var offset = Math.Max(0, total - pageSize);
                        var entries = await bootStore.Store.ReadAsync(asid, offset, pageSize, ct);
                        var beforeSeq = entries.Count > 0 ? entries[0].Sequence : 0;
                        await SendAsync(c, "session.updated", await ToVisibleSessionJsonAsync(info, ct), asid, ct);
                        object? bootStreaming = null;
                        var bInFlight = InFlightText(asid);
                        if (bInFlight is not null)
                            bootStreaming = new { text = bInFlight, maxSequence = entries.Count > 0 ? entries[^1].Sequence : beforeSeq };
                        await SendAsync(c, "session.entries",
                            new { entries = EntriesToJson(entries), replace = true, total = total,
                                  hasMore = total > entries.Count, beforeSequence = beforeSeq, streaming = bootStreaming }, asid, ct);
                    }
                }
                // else: agent restarted without a session — restore the most
                // recently used one (sessions is sorted newest-first).
                else if (sessions.FirstOrDefault() is { } last)
                {
                    var info = await bootStore.Store.GetAsync(last.Id, ct);
                    if (info is not null)
                    {
                        const int pageSize = 200;
                        var total = info.EntryCount;
                        var offset = Math.Max(0, total - pageSize);
                        var entries = await bootStore.Store.ReadAsync(last.Id, offset, pageSize, ct);
                        var beforeSeq = entries.Count > 0 ? entries[0].Sequence : 0;
                        await SendAsync(c, "session.updated", await ToVisibleSessionJsonAsync(info, ct), last.Id, ct);
                        object? bootStreaming = null;
                        var bInFlight = InFlightText(last.Id);
                        if (bInFlight is not null)
                            bootStreaming = new { text = bInFlight, maxSequence = entries.Count > 0 ? entries[^1].Sequence : beforeSeq };
                        await SendAsync(c, "session.entries",
                            new { entries = EntriesToJson(entries), replace = true, total = total,
                                  hasMore = total > entries.Count, beforeSequence = beforeSeq, streaming = bootStreaming }, last.Id, ct);
                    }
                }
            }
            catch (Exception ex)
            {
                _log.Warning($"bootstrap session.list failed: {ex.Message}");
            }
            finally
            {
                try { bootStore.Lease.Dispose(); } catch { }
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
                // astra-1 E: with multiple runs possible (maxConcurrentRuns > 1)
                // a steer without a session ID has no unambiguous target —
                // reject it rather than fall back to a global "active run".
                // With the default single-run capacity the legacy fallback
                // ("held until the next run") is unambiguous and preserved.
                var steerSid = S(p, "sessionId");
                if (steerSid is null && (_runner?.ListRuns().Count(r => r.Outcome == RunState.Running) ?? 0) > 1)
                {
                    await SendErrorAsync(c, requestId, "steer needs a sessionId while multiple runs are active", ct); break;
                }
                await _steering.EnqueueAsync(S(p, "text") ?? "", steerSid, ct);
                await SendAckAsync(c, requestId, ct);
                break;

            case "agent.cancel":
            {
                // astra-1 F: an optional runId cancels ONE specific run (the
                // Activity panel's Cancel); without it the legacy cancel of
                // the active run is preserved.
                var cancelRunId = S(p, "runId");
                var cancelSessionId = S(p, "sessionId");
                if (_runner is not null)
                {
                    if (cancelRunId is not null)
                    {
                        // astra-1 F + §13: a specific run. CancelRun reaches a live
                        // (or a shared-token) record; a QUEUED record is reached via
                        // CancelQueuedRun, which also purges the lane queue entry.
                        _ = _runner.CancelRun(cancelRunId);
                        try { await _runner.CancelQueuedRun(cancelRunId); }
                        catch { /* best-effort */ }
                    }
                    else if (cancelSessionId is not null)
                    {
                        // astra-2 §13: stop the session's run — a queued record holds
                        // no live task, so the legacy all-live cancel cannot reach
                        // it; cancel the queued/suspended records of THIS session.
                        var sessionRunIds = _runner.ListRuns()
                            .Where(r => r.SessionId == cancelSessionId && r.Outcome == RunState.Running)
                            .Select(r => r.RunId).ToList();
                        foreach (var rid in sessionRunIds)
                        {
                            _ = _runner.CancelRun(rid);
                            try { await _runner.CancelQueuedRun(rid); }
                            catch { /* best-effort */ }
                        }
                    }
                    else
                    {
                        // Legacy global stop: every live run, plus every queued run
                        // (a queued record is the durable form of "active" astra-2
                        // §13 — a full pool is an accepted queue, and stopping must
                        // stop it too). CancelRunAsync cancels the live records;
                        // the queued ones are reached by their run id.
                        await _runner.CancelRunAsync(ct);
                        var queuedRunIds = _runner.ListRuns()
                            .Where(r => r.Outcome == RunState.Running)
                            .Select(r => r.RunId).ToList();
                        foreach (var rid in queuedRunIds)
                        {
                            try { await _runner.CancelQueuedRun(rid); }
                            catch { /* best-effort */ }
                        }
                    }
                }
                if (_agent?.State.ActiveSessionId is { } csid)
                    await SendAsync(c, "agent.state", new { state = "Cancelling" }, csid, ct);
                await SendAckAsync(c, requestId, ct);
                break;
            }

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
                if (sid is null) { await SendAckAsync(c, requestId, ct); break; }
                if (StoreLocal() is { } rStore)
                {
                    try
                    {
                        if (!string.IsNullOrEmpty(title))
                            await rStore.Store.RenameAsync(sid, title, ct);
                        else if (!string.IsNullOrEmpty(workspace))
                            await rStore.Store.SetWorkspaceAsync(sid, workspace, ct);
                        await BroadcastSession(sid, ct);
                    }
                    finally { try { rStore.Lease.Dispose(); } catch { } }
                }
                await SendAckAsync(c, requestId, ct);
                break;
            }

            case "session.delete":
            {
                var sid = S(p, "sessionId");
                if (sid is null) { await SendAckAsync(c, requestId, ct); break; }
                // Deleting the transcript out from under an in-flight run would
                // orphan its entries; force the user to cancel first.
                if (_runner?.IsRunning == true && _agent?.State.ActiveSessionId == sid)
                {
                    await SendErrorAsync(c, requestId, "cannot delete a session with a run in progress", ct);
                    break;
                }
                if (StoreLocal() is { } dStore)
                {
                    try
                    {
                        await dStore.Store.DeleteAsync(sid, ct);
                        await BroadcastAsync("session.deleted", new { sessionId = sid }, sid, ct);
                    }
                    finally { try { dStore.Lease.Dispose(); } catch { } }
                }
                await SendAckAsync(c, requestId, ct);
                break;
            }

            case "session.list":
                if (StoreLocal() is { } lStore)
                {
                    try
                    {
                        const int pageSize = 50;
                        var offset = Math.Max(0, I(p, "offset"));
                        var query = S(p, "query");
                        IReadOnlyList<SessionInfo> list;
                        int total;
                        if (!string.IsNullOrWhiteSpace(query))
                        {
                            // astra-1 G1: server-side search over ALL stored sessions
                            // (the picker's search must not be bounded to loaded pages).
                            list = await lStore.Store.SearchAsync(query, pageSize, offset, ct);
                            total = await lStore.Store.SearchCountAsync(query, ct);
                        }
                        else
                        {
                            list = await lStore.Store.ListAsync(pageSize, offset, ct);
                            total = await lStore.Store.CountAsync(ct);
                        }
                        await SendAsync(c, "session.list",
                            new { sessions = list.Select(ToSessionJson).ToList(), query,
                                  offset, total, hasMore = offset + list.Count < total }, null, ct);
                    }
                    finally { try { lStore.Lease.Dispose(); } catch { } }
                }
                await SendAckAsync(c, requestId, ct);
                break;

            case "session.model":
            {
                var sid = S(p, "sessionId");
                if (sid is not null && StoreLocal() is { } mStore)
                {
                    try
                    {
                        await mStore.Store.SetModelAsync(sid, S(p, "modelId"), S(p, "reasoning"), ct);
                        await BroadcastSession(sid, ct);
                    }
                    finally { try { mStore.Lease.Dispose(); } catch { } }
                }
                await SendAckAsync(c, requestId, ct);
                break;
            }

            case "session.reasoning":
            {
                var sid = S(p, "sessionId");
                if (sid is not null && StoreLocal() is { } rgStore)
                {
                    try
                    {
                        var info = await rgStore.Store.GetAsync(sid, ct);
                        await rgStore.Store.SetModelAsync(sid, info?.ModelId, S(p, "level"), ct);
                        await BroadcastSession(sid, ct);
                    }
                    finally { try { rgStore.Lease.Dispose(); } catch { } }
                }
                await SendAckAsync(c, requestId, ct);
                break;
            }

            // astra-2 §10 (package F): persisted orchestration mode per session
            // (the user's explicit choice; a blank/null payload clears it). The
            // runner reads this to decide orchestrate-vs-direct for the run.
            case "session.mode":
            {
                var sid = S(p, "sessionId");
                if (sid is not null && StoreLocal() is { } modeStore)
                {
                    try
                    {
                        await modeStore.Store.SetModeAsync(sid, S(p, "mode"), ct);
                        await BroadcastSession(sid, ct);
                    }
                    finally { try { modeStore.Lease.Dispose(); } catch { } }
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

            case "session.project":
            {
                // astra-1 D2: switch a session's active project. Idle → apply
                // immediately (same gate the run path takes) and broadcast
                // session.updated + session.project.applied. Running → ENQUEUE
                // as pending (the run's in-flight batch keeps its workspace)
                // and the runner applies it at the safe boundary, bridging
                // ProjectApplied back to session.project.applied + session.updated.
                var sid = S(p, "sessionId");
                var projectId = S(p, "projectId");
                if (sid is null || projectId is null)
                {
                    await SendErrorAsync(c, requestId, "sessionId and projectId are required", ct); break;
                }
                var projectStore = Resolve<IProjectStore>("projects");
                var sessionStore = Resolve<ISessionStore>("sessions");
                if (projectStore is null || sessionStore is null)
                {
                    await SendErrorAsync(c, requestId, "project or session store unavailable", ct); break;
                }
                var proj = await projectStore.GetAsync(projectId, ct);
                if (proj is null)
                {
                    await SendErrorAsync(c, requestId, "unknown project", ct); break;
                }
                var opId = S(p, "operationId") ?? Guid.NewGuid().ToString("n");
                // Resolve the EXACT instruction snapshot now (the authoritative
                // context for the change); the boundary apply never re-resolves.
                var resolver = Resolve<IInstructionContextResolver>("instruction-context");
                var snapshot = resolver is null
                    ? new ProjectContextSnapshot(projectId, proj.Name, proj.WorkspacePath,
                        Array.Empty<string>(), string.Empty, string.Empty, DateTimeOffset.UtcNow)
                    : await resolver.ResolveAsync(projectId, proj.Name, proj.WorkspacePath, ct);

                // astra-1 D2 (slice 2): RUNNING if THIS session has an active run
                // (the per-session discriminator — not the aggregate IsRunning).
                var running = _runner?.GetSessionRun(sid) is not null;
                if (!running)
                {
                    // IDLE: apply directly, under the SAME per-session gate the runner's
                    // send path and safe-boundary apply use (send / project-change /
                    // compaction serialized per session). If a send owns the boundary
                    // right now, go PENDING instead — the run's boundary applies it.
                    var gate = _runner?.SessionGate(sid);
                    var hold = gate is null || gate.Wait(TimeSpan.FromSeconds(5), ct);
                    if (!hold)
                    {
                        var busyPending = Resolve<IPendingProjectChangeStore>("pending-projects");
                        if (busyPending is not null)
                        {
                            await busyPending.EnqueueAsync(new ProjectChangeRequest(opId, sid, projectId, snapshot), ct);
                            await SendAckAsync(c, requestId, ct);
                            await BroadcastAsync("session.project.pending",
                                new { sessionId = sid, operationId = opId, projectId, projectName = proj.Name }, sid, ct);
                            break;
                        }
                    }
                    try
                    {
                        await sessionStore.SetProjectAsync(new ProjectChangeRequest(opId, sid, projectId, snapshot), ct);
                        await BroadcastSession(sid, ct);
                        await SendAckAsync(c, requestId, ct);
                        await BroadcastAsync("session.project.applied",
                            new { sessionId = sid, operationId = opId, projectId, projectName = proj.Name }, sid, ct);
                    }
                    catch (Exception ex)
                    {
                        _log.Warning($"session.project apply failed: {ex.Message}");
                        await SendErrorAsync(c, requestId, ex.Message, ct);
                    }
                    finally
                    {
                        if (hold && gate is not null) { try { gate.Release(); } catch { } }
                    }
                    break;
                }

                // RUNNING: enqueue as pending; the runner applies it at the safe
                // boundary (a later selection supersedes an earlier unapplied one).
                var pending = Resolve<IPendingProjectChangeStore>("pending-projects");
                if (pending is null)
                {
                    await SendErrorAsync(c, requestId, "pending project store unavailable", ct); break;
                }
                await pending.EnqueueAsync(new ProjectChangeRequest(opId, sid, projectId, snapshot), ct);
                await SendAckAsync(c, requestId, ct);
                await BroadcastAsync("session.project.pending",
                    new { sessionId = sid, operationId = opId, projectId, projectName = proj.Name }, sid, ct);
                break;
            }

            case "runs.list":
            {
                // astra-1 F: active/recent runs for the Activity surface's WS
                // consumers (the Activity panel itself polls /api/activity).
                var runs = _runner?.ListRuns()
                    ?? System.Array.Empty<RunInfo>();
                await SendAsync(c, "runs.list",
                    new { runs = runs.Select(r => new
                    {
                        runId = r.RunId, sessionId = r.SessionId, modelId = r.ModelId,
                        state = r.State.ToString(), outcome = r.Outcome.ToString(),
                        startTime = r.StartTime, endTime = r.EndTime,
                    }).ToArray() }, null, ct);
                await SendAckAsync(c, requestId, ct);
                break;
            }

            case "project.list":
            {
                var plStore = Resolve<IProjectStore>("projects");
                if (plStore is null) { await SendErrorAsync(c, requestId, "project store unavailable", ct); break; }
                var plList = await plStore.ListAsync(ct);
                await SendAsync(c, "project.list", new { projects = plList.Select(ToProjectJson).ToArray() }, null, ct);
                await SendAckAsync(c, requestId, ct);
                break;
            }

            case "project.create":
            {
                var pcStore = Resolve<IProjectStore>("projects");
                if (pcStore is null) { await SendErrorAsync(c, requestId, "project store unavailable", ct); break; }
                var pcName = S(p, "name");
                var pcPath = S(p, "workspacePath");
                if (string.IsNullOrEmpty(pcPath)) { await SendErrorAsync(c, requestId, "workspacePath is required", ct); break; }
                if (string.IsNullOrEmpty(pcName)) pcName = System.IO.Path.GetFileName(pcPath.TrimEnd(System.IO.Path.DirectorySeparatorChar));
                try
                {
                    var created = await pcStore.CreateAsync(pcName, pcPath, ct);
                    await SendAsync(c, "project.created", new { project = ToProjectJson(created) }, null, ct);
                    await SendAckAsync(c, requestId, ct);
                }
                catch (Exception ex) { await SendErrorAsync(c, requestId, ex.Message, ct); }
                break;
            }

            case "project.update":
            {
                var puStore = Resolve<IProjectStore>("projects");
                if (puStore is null) { await SendErrorAsync(c, requestId, "project store unavailable", ct); break; }
                var puId = S(p, "id");
                if (puId is null) { await SendErrorAsync(c, requestId, "id is required", ct); break; }
                var newName = S(p, "name");
                if (!string.IsNullOrEmpty(newName))
                    await puStore.RenameAsync(puId, newName, ct);
                await SendAckAsync(c, requestId, ct);
                break;
            }

            case "session.project.refresh":
            {
                // astra-1 F: re-snapshot the session's active project
                // instructions (the D2 command resolves at selection; this
                // lets a later instruction change be captured without a new
                // switch). Re-apply through the same gate the switch uses so
                // send / project-change / compaction stay serialized.
                var srSid = S(p, "sessionId");
                if (srSid is null) { await SendErrorAsync(c, requestId, "sessionId is required", ct); break; }
                var srStore = Resolve<ISessionStore>("sessions");
                var srProjStore = Resolve<IProjectStore>("projects");
                if (srStore is null || srProjStore is null) { await SendErrorAsync(c, requestId, "session or project store unavailable", ct); break; }
                var srInfo = await srStore.GetAsync(srSid, ct);
                if (srInfo?.ProjectId is null) { await SendErrorAsync(c, requestId, "session has no project", ct); break; }
                var srProj = await srProjStore.GetAsync(srInfo.ProjectId, ct);
                if (srProj is null) { await SendErrorAsync(c, requestId, "project no longer exists", ct); break; }
                var srResolver = Resolve<IInstructionContextResolver>("instruction-context");
                if (srResolver is null) { await SendErrorAsync(c, requestId, "instruction resolver unavailable", ct); break; }
                var srGate = _runner?.SessionGate(srSid);
                var srHold = srGate is null || srGate.Wait(TimeSpan.FromSeconds(5), ct);
                if (!srHold) { await SendErrorAsync(c, requestId, "session is busy with a boundary operation; retry", ct); break; }
                try
                {
                    var srSnap = await srResolver.ResolveAsync(srProj.Id, srProj.Name, srProj.WorkspacePath, ct);
                    var srOpId = S(p, "operationId") ?? Guid.NewGuid().ToString("n");
                    // Same atomic path as a fresh switch (revision bump is a no-op
                    // re-apply; the snapshot entry is deduped by operation id).
                    await srStore.SetProjectAsync(new ProjectChangeRequest(srOpId, srSid, srProj.Id, srSnap), ct);
                    await BroadcastSession(srSid, ct);
                    await SendAsync(c, "session.project.applied",
                        new { sessionId = srSid, operationId = srOpId, projectId = srProj.Id, projectName = srProj.Name }, srSid, ct);
                    await SendAckAsync(c, requestId, ct);
                }
                finally { if (srHold) { try { srGate.Release(); } catch { } } }
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
                // astra-1 P5: the reload must NOT block the client on the host
                // queue. Ack IMMEDIATELY with an operation id; the work runs on
                // the host lifecycle queue (a reload of netpi.web itself would
                // otherwise kill this connection before the ack ever lands). The
                // outcome is announced later by the RUNNER (OnPluginUpdateCompleted)
                // to EVERY live connection, and stays queryable (facade
                // GetOperation) after a reconnect.
                var reloadOpId = _facade.EnqueueReload(pid, S(p, "buildId"), CancellationToken.None);
                await c.SendSafeAsync(
                    Envelope("ack", requestId, null,
                        new { operationId = reloadOpId, kind = "reload", pluginId = pid }), ct);
                break;
            }

            // astra-1 P5: query an Enqueue* operation's status by id (the
            // typed outcome after the connection that issued it has died —
            // used by tools/publish-plugins.ps1 and by the frontend on
            // reconnect).
            case "plugin.operation":
            {
                if (_facade is not null && S(p, "operationId") is { } opId)
                {
                    var st = _facade.GetOperation(opId);
                    await c.SendSafeAsync(
                        Envelope("plugin.operation", requestId, null, new
                        {
                            operationId = st.OperationId,
                            kind = st.Kind.ToString(),
                            done = st.Done,
                            outcome = st.Outcome.ToString(),
                            phase = st.Phase.ToString(),
                            error = st.Error,
                            appliedBuildId = st.AppliedBuildId,
                            scannedIds = st.ScannedIds,
                        }), ct);
                }
                else
                {
                    await SendErrorAsync(c, requestId, "operationId required", ct);
                }
                await SendAckAsync(c, requestId, ct);
                break;
            }

            case "plugins.list":
            {
                if (_facade is not null)
                    await SendAsync(c, "plugins.state", new { plugins = PluginJson() }, null, ct);
                await SendAckAsync(c, requestId, ct);
                break;
            }

            // astra-1 P5: ack with an operation id immediately; per-plugin
            // state/failed events arrive from the runner (OnPluginUpdateCompleted)
            // to every live connection.
            case "plugin.reloadAll":
                if (_facade is not null)
                {
                    var allOpId = _facade.EnqueueReloadAll(CancellationToken.None);
                    await c.SendSafeAsync(
                        Envelope("ack", requestId, null,
                            new { operationId = allOpId, kind = "reloadAll" }), ct);
                }
                else
                    await SendAckAsync(c, requestId, ct);
                break;

            // PLAN §50: re-scan the plugin directory for folders staged after
            // startup and load+start any the host has never seen. Existing
            // plugins are untouched (reload swaps their bytes). astra-1 P5:
            // ack with an operation id immediately — the loaded list arrives
            // from the runner (OnPluginUpdateCompleted) to every live client.
            case "plugin.scan":
            {
                if (_facade is null)
                {
                    await SendErrorAsync(c, requestId, "plugin manager unavailable", ct);
                    break;
                }
                var scanOpId = _facade.EnqueueScan(CancellationToken.None);
                await c.SendSafeAsync(
                    Envelope("ack", requestId, null,
                        new { operationId = scanOpId, kind = "scan" }), ct);
                break;
            }

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
                try
                {
                    foreach (var (key, value) in EnumerateObject(plugins))
                        await _config.MergePluginAsync(key, value, ct);
                }
                catch (InvalidOperationException ex)
                {
                    // astra-1 §11a (P/B): a malformed config or a failed atomic write
                    // rejects the update (original bytes retained). Surface the
                    // credential-free reason rather than an empty ack.
                    await SendErrorAsync(c, requestId, ex.Message, ct);
                    break;
                }
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
        // astra-1 P3: ONE store instance for the whole send (session lookup,
        // auto-title rename, model persistence) — the lease is released at
        // scope exit.
        using var _storeLease = AcquireStoreLease();
        ISessionStore? Store = _storeLease?.Value;
        if (Store is null) { await SendErrorAsync(c, requestId, "session store unavailable", ct); return; }

        var text = S(p, "text") ?? "";
        var model = S(p, "model");
        var reasoning = S(p, "reasoning");
        var sessionId = S(p, "sessionId");
        var payloadWorkspace = S(p, "workspace");
        // astra-1 §11a (F/A): a stable client operationId makes retries idempotent.
        // Absent/blank operationId is a NON-idempotent legacy send (behaviour unchanged).
        var operationId = S(p, "operationId");

        if (!string.IsNullOrEmpty(operationId))
        {
            // Replay a send that was already accepted: do NOT append another message
            // or execute another run — just re-send the original outcome + ack.
            if (_sends.TryGet(operationId) is { SessionId: { } priorSid } prior)
            {
                await SendAsync(c, "session.entry",
                    new { entry = new { type = "user_message", text = prior.Text } as object },
                    priorSid, ct);
                await SendAckAsync(c, requestId, ct);
                return;
            }
        }

        // Ensure a session exists (the runner persists the initial user entry).
        string? sid = sessionId;
        SessionInfo? info = null;
        if (!string.IsNullOrEmpty(sid))
            info = await Store.GetAsync(sid, ct);
        if (info is null)
        {
            info = await Store.CreateAsync(payloadWorkspace, ct);
            sid = info.Id;
            await SendAsync(c, "session.created", ToSessionJson(info), sid, ct);
        }

        // First send on a session with no title: name it after the message
        // (drawer "untitled" -> first few words of what the user asked).
        if (string.IsNullOrEmpty(info.Title) && !string.IsNullOrWhiteSpace(text))
        {
            var autoTitle = DeriveSessionTitle(text);
            if (autoTitle is not null)
            {
                await Store.RenameAsync(sid!, autoTitle, ct);
                info = (await Store.GetAsync(sid!, ct)) ?? info with { Title = autoTitle };
                await BroadcastSession(sid!, ct);
            }
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
            await Store.SetModelAsync(info.Id, model, string.IsNullOrEmpty(reasoning) ? null : reasoning, ct);

        // astra-2 §7/§13: thread the operation id into the runner as the run
        // identity (the store's run_id == operation id, so the runner's terminal
        // event reconciles against the durable assignment row). A non-idempotent
        // legacy send (no operation id) keeps the runner minting its own id.
        var started = await _runner.StartRunAsync(new AgentRunRequest(sid, workspace, model, text,
            RunId: string.IsNullOrEmpty(operationId) ? null : operationId), ct);
        if (started.Disposition == RunDisposition.Queued)
        {
            // astra-2 §13/§16: a full local pool is an ACCEPTED queue, never a
            // rejection. Persist a durable Queued assignment (survives reload,
            // shows a tab badge, and is cancellable), then ACK as queued. A
            // missing orchestration-store plugin degrades to an ack-only queued
            // send (the runner still owns the in-memory queue).
            var queuedError = await PersistQueuedAssignmentAsync(sid, model, started.RunId, ct);
            if (queuedError is { } qe)
            {
                await SendErrorAsync(c, requestId, qe, ct); return;
            }
            await SendAsync(c, "session.entry",
                new { entry = new { type = "user_message", text } as object }, sid, ct);
            if (!string.IsNullOrEmpty(operationId))
                _sends.Accept(operationId, new SendIdempotency.Accepted(sid, text, "queued", DateTimeOffset.UtcNow));
            await SendAckAsync(c, requestId, ct);
            return;
        }
        if (!string.IsNullOrEmpty(started.Note))
        {
            await SendErrorAsync(c, requestId, started.Note, ct);
            return;
        }

        // The client already shows an optimistic user row. This authoritative
        // echo arrives only after the runner accepted the turn, so a rejected
        // submission cannot look like work that silently vanished.
        await SendAsync(c, "session.entry",
            new { entry = new { type = "user_message", text } as object }, sid, ct);
        await SendAsync(c, "agent.state", new { state = "Preparing" }, sid, ct);
        // astra-1 §11a (F/A): stamp acceptance BEFORE the ack is delivered, so a
        // disconnect between acceptance and acknowledgement still lets a retry
        // replay the existing result instead of starting duplicate work.
        if (!string.IsNullOrEmpty(operationId))
            _sends.Accept(operationId, new SendIdempotency.Accepted(
                sid, text, "accepted", DateTimeOffset.UtcNow));
        await SendAckAsync(c, requestId, ct);
    }

    private async Task SessionCreateAsync(Client c, string? requestId, JsonElement p, CancellationToken ct)
    {
        using var _storeLease = AcquireStoreLease();
        ISessionStore? Store = _storeLease?.Value;
        if (Store is null) { await SendErrorAsync(c, requestId, "no session store", ct); return; }
        var ws = S(p, "workspace");
        var created = await Store.CreateAsync(string.IsNullOrEmpty(ws) ? null : ws, ct);
        await SendAsync(c, "session.created", ToSessionJson(created), created.Id, ct);
        await SendAsync(c, "session.entries",
            new { entries = new List<object>(), replace = true, total = 0,
                  hasMore = false, beforeSequence = 0 }, created.Id, ct);
        await SendAckAsync(c, requestId, ct);
    }

    private async Task SessionOpenAsync(Client c, string? requestId, JsonElement p, CancellationToken ct)
    {
        using var _storeLease = AcquireStoreLease();
        ISessionStore? Store = _storeLease?.Value;
        if (Store is null) { await SendErrorAsync(c, requestId, "no session store", ct); return; }
        var sid = S(p, "sessionId");
        if (sid is null) { await SendAckAsync(c, requestId, ct); return; }
        var info = await Store.GetAsync(sid, ct);
        if (info is null) { await SendErrorAsync(c, requestId, "session not found", ct); return; }
        await SendAsync(c, "session.updated", ToSessionJson(info), sid, ct);
        // PLAN §38: don't load/render an entire giant session — load the LATEST
        // ~200 entries; older ones are fetched on scroll-up via session.older.
        const int pageSize = 200;
        var total = info.EntryCount;
        var offset = Math.Max(0, total - pageSize);
        var entries = await Store.ReadAsync(sid, offset, pageSize, ct);
        // PLAN §38: beforeSequence is the sequence of the OLDEST entry loaded —
        // the client requests session.older { beforeSequence } on scroll-up.
        var beforeSeq = entries.Count > 0 ? entries[0].Sequence : 0;
        // astra-1 F: if an assistant turn is in flight RIGHT NOW, return a
        // snapshot of the currently streaming text in the SAME response —
        // persistence alone cannot reconstruct an unfinished streamed message.
        // The maxSequence cursor is the reconcile point: any live event that
        // lands for this session after the open is replayed against it (a
        // client-side; the in-flight snapshot belongs to the CURRENT run only).
        // The cursor is the HIGHEST persisted sequence: any live event that
        // carries a lower sequence is a stale replay and the client must skip
        // it; deltas for the in-flight turn are appended to the snapshot's
        // assistant block (no duplication — the snapshot already includes the
        // text that any pre-snapshot deltas would have delivered).
        // astra-1 F: reconstruct the in-flight assistant message. If a turn is
        // streaming, InFlightText() merges the flushed portion with the unflushed
        // batcher buffer (and folds that buffer into the accumulator so the next
        // flush doesn't re-emit it). The maxSequence cursor is the highest
        // persisted entry — the reconcile point for any live event arriving
        // during replay.
        var key = sid ?? "";
        var inFlight = InFlightText(sid);
        object? streaming = null;
        if (inFlight is not null)
        {
            streaming = new
            {
                text = inFlight,
                maxSequence = entries.Count > 0 ? entries[^1].Sequence : beforeSeq,
            };
        }
        await SendAsync(c, "session.entries",
            new { entries = EntriesToJson(entries), replace = true, total = total,
                  hasMore = total > entries.Count, beforeSequence = beforeSeq, streaming }, sid, ct);
        await SendAckAsync(c, requestId, ct);
    }

    /// <summary>PLAN §38: scroll-up pagination — entries older than a sequence.</summary>
    private async Task SessionOlderAsync(Client c, string? requestId, JsonElement p, CancellationToken ct)
    {
        using var _storeLease = AcquireStoreLease();
        ISessionStore? Store = _storeLease?.Value;
        if (Store is null) { await SendErrorAsync(c, requestId, "no session store", ct); return; }
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
        var entries = await Store.ReadBeforeAsync(sid, beforeSequence, count, ct);
        var newOldest = entries.Count > 0 ? entries[0].Sequence : 0;
        await SendAsync(c, "session.older",
            new { entries = EntriesToJson(entries), beforeSequence = newOldest,
                  hasMore = entries.Count == count }, sid, ct);
        await SendAckAsync(c, requestId, ct);
    }

    private async Task BroadcastSession(string sid, CancellationToken ct)
    {
        if (Store is null) return;
        var info = await Store.GetAsync(sid, ct);
        if (info is not null)
            await BroadcastAsync("session.updated", await ToVisibleSessionJsonAsync(info, ct), sid, ct);
    }
    /// <summary>
    /// astra-1 D2: the visible session's metadata INCLUDING its active project
    /// (id/name/rootPath) so the header project chip renders. Only the
    /// single-session <c>session.updated</c> path uses this — the paginated
    /// <c>session.list</c> keeps the lean <see cref="ToSessionJson"/> (no per-row
    /// project lookup = no N+1). <c>project</c> is null when the session has
    /// no project.
    /// </summary>
    private async Task<object> ToVisibleSessionJsonAsync(SessionInfo s, CancellationToken ct) => new
    {
        id = s.Id,
        title = s.Title ?? "",
        workspace = s.WorkspacePath ?? "",
        modelId = s.ModelId,
        reasoningLevel = s.ReasoningLevel,
        // astra-2 §10 (package F): the persisted orchestration mode (null =
        // direct; "orchestrate" = coordinator + delegation for the run).
        mode = s.Mode,
        createdAt = s.CreatedAt.ToUnixTimeMilliseconds(),
        updatedAt = s.UpdatedAt.ToUnixTimeMilliseconds(),
        project = await ProjectJsonAsync(s.ProjectId, ct),
        // astra-1 G2 (F contract): the compaction policy the context meter
        // surfaces. It is a STATIC snapshot (enabled + configured reserve —
        // neither varies per session or per live event), so the
        // context-revision/late-event guard does not apply; the client caches
        // it and re-derives the threshold from the selected model's window.
        compaction = CompactionPolicyJson(),
    };

    /// <summary>astra-1 G2 (F contract): the compaction policy as the client shape,
    /// or null when no compaction plugin is loaded / it publishes none (the UI
    /// then shows the threshold as "not reported", never a fabricated zero).</summary>
    private object? CompactionPolicyJson()
    {
        var policy = _compaction?.ContextPolicy;
        if (policy is null) return null;
        return new { available = policy.Available, reserveTokens = policy.ReserveTokens };
    }

    /// <summary>astra-1 D2: the session's active project as the client <c>project</c>
    /// shape, or null when the session has no project / the store is gone.</summary>
    private async Task<object?> ProjectJsonAsync(string? projectId, CancellationToken ct)
    {
        if (projectId is null) return null;
        var projStore = Resolve<IProjectStore>("projects");
        if (projStore is null) return null;
        var proj = await projStore.GetAsync(projectId, ct);
        return proj is null ? null : new
        {
            id = proj.Id,
            name = proj.Name,
            rootPath = proj.WorkspacePath,
            updatedAt = proj.UpdatedAt.ToUnixTimeMilliseconds(),
        };
    }

    /// <summary>astra-1 F: a project row as the client shape (the project.list /
    /// project.created payloads).</summary>
    private static object ToProjectJson(ProjectInfo proj) => new
    {
        id = proj.Id,
        name = proj.Name,
        workspacePath = proj.WorkspacePath,
        updatedAt = proj.UpdatedAt.ToUnixTimeMilliseconds(),
    };

    // ---- shape helpers ------------------------------------------------------
    /// <summary>
    /// astra-1 D2: bridge the runner's ProjectApplied boundary event to the
    /// client. Refreshes session metadata (session.updated) and clears the
    /// pending indicator (session.project.applied). Fire-and-forget — the event
    /// path must not block; failures are logged.
    /// </summary>
    private async Task ProjectAppliedBridgeAsync(string? sid, string operationId, string projectId, string projectName)
    {
        if (sid is null) return;
        await EmitProjectAppliedAsync(sid, operationId, projectId, projectName);
    }

    /// <summary>
    /// astra-1 D2 (slice 2): broadcast <c>session.project.applied</c> (payload
    /// operationId + projectId, the client contract) + <c>session.updated</c>
    /// (the metadata refresh). Deduped per session+operation id — a repeated
    /// event for the same operation is a no-op.
    /// </summary>
    private async Task EmitProjectAppliedAsync(string sid, string operationId, string? projectId, string? projectName)
    {
        if (string.IsNullOrEmpty(operationId)) return;
        // last-applied op id per session: a duplicate (same op id) is skipped.
        var last = _lastProjectApplied;
        if (last.TryGetValue(sid, out var prev) && prev == operationId) return;
        last[sid] = operationId;
        try
        {
            await BroadcastSession(sid, CancellationToken.None);
            await BroadcastAsync("session.project.applied",
                new { sessionId = sid, operationId, projectId, projectName }, sid, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _log.Warning($"session.project.applied bridge failed: {ex.Message}");
        }
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
            buildId = s.BuildId,
            state = MapPluginState(s.State),
            activeLeases = s.ActiveLeases,
            lastError = s.LastError,
            // astra-1 P5: the update picture + last lifecycle operation (so the
            // diagnostics plugin's WS refresh carries the same fields as its seed).
            availableBuildId = s.Update?.AvailableBuildId,
            loadedPath = s.Update?.LoadedPath,
            blockingLeases = s.Update?.BlockingLeases,
            prevAlocCollected = s.Update?.PrevAlocCollected,
            lastOperation = s.LastOperation is { } o ? new
            {
                operationId = o.OperationId,
                requestedBuildId = o.RequestedBuildId,
                activeBuildId = o.ActiveBuildId,
                phase = o.Phase.ToString(),
                outcome = o.Outcome.ToString(),
                restartRequired = o.RestartRequired,
                error = o.Error,
            } : null,
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
        // astra-2 §10 (package F): the persisted orchestration mode (null = direct).
        mode = s.Mode,
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
        // PLAN §46: a run that died mid-batch (host restart/crash) leaves an
        // assistant tool call with no stored result; the frontend would render
        // that replayed call as "running" forever. Precompute which call ids
        // actually have a result and flag the orphans as interrupted.
        var resolvedCallIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in entries)
            if (e.Kind == EntryKind.Message && e.Message is { } tm && tm.Role == MessageRole.Tool)
                foreach (var part in tm.Parts.OfType<ToolResultPart>())
                    if (!string.IsNullOrEmpty(part.ToolCallId)) resolvedCallIds.Add(part.ToolCallId);

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
                        var (parts, toolResults) = SplitAssistant(m, resolvedCallIds);
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
            else if (e.Kind == EntryKind.ProjectContext)
            {
                // astra-1 D: project-context events project into ONE model-facing
                // user message; the UI renders them as a DISTINCT block (their
                // provenance is the application, not the model) carrying the
                // exact project name / workspace / instructions.
                lastAssistant = null;
                var proj = ProjectContextProjection.Project(e);
                if (proj is not null)
                {
                    var snap = ProjectContextProjection.Snapshot(e);
                    outEntries.Add(new
                    {
                        type = "project_context",
                        projectName = snap?.ProjectName ?? string.Empty,
                        workspace = snap?.WorkspacePath ?? string.Empty,
                        contentHash = snap?.ContentHash ?? string.Empty,
                        text = ((TextPart?)proj.Parts[0])?.Text ?? string.Empty,
                    } as object);
                }
            }
            else if (e.Kind == EntryKind.Compaction && e.Payload is { } pl)
            {
                lastAssistant = null;
                var root = JsonDocument.Parse(pl.ToString()).RootElement;
                outEntries.Add(new { type = "compaction", summary = S(root, "summary") } as object);
            }
            else if (e.Kind == EntryKind.Metadata && e.Payload is { } mdp)
            {
                lastAssistant = null;
                var mroot = JsonDocument.Parse(mdp.ToString()).RootElement;
                if (S(mroot, "kind") == "system_note")
                    outEntries.Add(new { type = "system_note", text = S(mroot, "text") } as object);
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

    private static (List<object> Parts, List<object> ToolResults) SplitAssistant(
        AgentMessage m, HashSet<string> resolvedCallIds)
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
                        interrupted = !resolvedCallIds.Contains(tc.Id),
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
        // §11a (F): fan out concurrently — one non-reading client must not stall the
        // others (each SendSafeAsync is internally bounded). Per-client order is
        // preserved by the per-socket gate; durable events are replayed on resync.
        await ConcurrentDeliverAsync(snapshot.Select(c => (Func<CancellationToken, Task>)(t => c.SendSafeAsync(json, t))), ct);
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

    /// <summary>asta-1 F: the currently streaming assistant text for <paramref name="sid"/>,
    /// merging the already-flushed portion (<c>_completedText</c>) with the deltas still
    /// sitting in the unflushed batcher buffer. Only an OPEN turn is in flight — a
    /// completed turn's text already went out as <c>text.completed</c> and was removed
    /// from <c>_completedText</c>, so a stale entry can never re-emit here. Under the
    /// batcher's gate so the merge is consistent with an in-flight flush; the pending
    /// delayed-flush task is cancelled so the consumed text is never emitted twice.</summary>
    private string? InFlightText(string? sid)
    {
        if (sid is null || !_assistantOpen.ContainsKey(sid)) return null;
        var key = sid;
        string flushed = _completedText.TryGetValue(key, out var t) ? t : string.Empty;
        if (_batchers.TryGetValue(key, out var b))
        {
            lock (b.Gate)
            {
                if (b.Buffers.TryGetValue("text", out var buf) && buf.Length > 0)
                {
                    // Consume: the snapshot carries this text, so fold it into the
                    // running accumulator now and clear the buffer.
                    flushed += buf.ToString();
                    _completedText[key] = flushed;
                    buf.Clear();
                }
                // The pending delayed flush (if any) is harmless now: the text lane
                // is already consumed, so a late flush finds nothing to emit. Null it
                // (don't dispose — a Task.Delay still in the waiting state cannot be
                // disposed); this mirrors FlushDeltas' own "we drained" cancellation.
                b.Pending = null;
            }
        }
        return string.IsNullOrEmpty(flushed) ? null : flushed;
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
        // astra-1 F/#11: the owner of this turn is gone — a delayed stream event
        // from it (or an even older run) must not open a new owner claim.
        _activeRun.TryRemove(key, out _);
    }

    private void SendEvent(string type, object payload, string? sid) =>
        _ = Task.Run(async () =>
        {
            var json = Envelope(type, null, sid, payload);
            List<Client> snapshot;
            lock (_clientsLock) snapshot = _clients.ToList();
            // §11a (F): concurrent + bounded fan-out (the ~20 ms delta path is the
            // hot one); a single non-reading client can no longer stall the others.
            await ConcurrentDeliverAsync(snapshot.Select(c => (Func<CancellationToken, Task>)(t => c.SendSafeAsync(json, t))), CancellationToken.None);
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

        /// <summary>
        /// Serialize all writes on this socket (WebSocket is not thread-safe) and
        /// never let a dead / non-reading client stall the caller: a send is gated
        /// by BOTH the client-lifetime token (a disconnect aborts it) and a bounded
        /// delivery timeout, linked with the caller's operation token — PLAN §11a (F)
        /// outbound backpressure: "One connected non-reading client should not stall
        /// updates for every other client ... bounded sends with explicit resync".
        /// Ordering per client is preserved by the gate; a timed-out / cancelled
        /// delivery is dropped (the client resyncs on its next full broadcast).
        /// </summary>
        public async Task SendSafeAsync(string json, CancellationToken operation)
        {
            using var delivery = WebApp.CreateDeliveryCancellationToken(Ct, operation, DeliveryTimeoutMs);
            try
            {
                await _sendGate.WaitAsync(delivery.Token);
                try
                {
                    if (Ws.State == WebSocketState.Open)
                        await Ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(json)),
                            WebSocketMessageType.Text, true, delivery.Token);
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
        var (fullPath, error) = await ResolvePathAsync(context);
        if (error is not null)
            return error;

        if (!File.Exists(fullPath))
            return Results.NotFound("file not found");

        if (!TryGetContentType(fullPath, out var contentType))
            contentType = "application/octet-stream";

        // astra-1 §11a (F/P5): active content (HTML/SVG) served from a user-supplied
        // path must never be RENDERED in the app origin — it could execute script.
        // Serve it as a plain-text download instead (the in-app viewer shows inert
        // text; the desktop shell opens it in the OS default app).
        var download = context.Request.Query["download"] == "1" || IsActiveContent(fullPath);
        if (IsActiveContent(fullPath))
            contentType = "text/plain; charset=utf-8";
        return Results.File(
            fullPath,
            contentType,
            fileDownloadName: download ? Path.GetFileName(fullPath) : null,
            enableRangeProcessing: true);
    }

    /// <summary>
    /// <summary>
    /// astra-1 §11a (F) outbound backpressure: the client-lifetime token and the
    /// operation token are linked into ONE cancellation (a disconnect OR an
    /// aborted operation aborts the delivery). Pure + testable.
    /// </summary>
    public static CancellationTokenSource CreateDeliveryCancellationToken(
        CancellationToken clientLifetime, CancellationToken operation, int timeoutMs)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(clientLifetime, operation);
        source.CancelAfter(timeoutMs);
        return source;
    }

    /// <summary>
    /// astra-1 §11a (F) outbound backpressure: fan a message out to every client
    /// CONCURRENTLY, so one non-reading / dead client cannot stall delivery to the
    /// others (each <paramref name="send"/> is bounded internally). Pure + testable
    /// (the core of <c>BroadcastAsync</c> / <c>SendEvent</c> fan-out).
    /// </summary>
    public static Task ConcurrentDeliverAsync(
        IEnumerable<Func<CancellationToken, Task>> sends, CancellationToken operation)
    {
        var tasks = sends.Select(t => t(operation)).ToArray();
        if (tasks.Length == 0) return Task.CompletedTask;
        return Task.WhenAll(tasks);
    }

    /// <summary>Bounded per-client delivery window for <see cref="Client.SendSafeAsync"/>.</summary>
    public const int DeliveryTimeoutMs = 5000;

    /// <summary>
    /// astra-1 §11a (F/P5): validate the local browser CONTROL boundary. The listener
    /// binds loopback, but loopback binding is NOT browser-origin validation — a
    /// malicious page in ANY browser can open a cross-origin request to
    /// 127.0.0.1:port. So the Host must be a loopback name AND, when an Origin is
    /// present, it must be the same http:// loopback host:port (the local app). An
    /// absent Origin is allowed ONLY on a loopback Host (the explicit originless CLI
    /// path); a present-but-wrong Origin is rejected (an absent/spoofed Origin is not
    /// proof of trust). Testable pure predicate.
    ///
    /// astra-1 §11a (F/P5) also asked for a "same-origin anti-forgery/session proof" for
    /// mutations. For a loopback-only control surface the SAME-ORIGIN CHECK IS that
    /// proof: a malicious cross-origin page cannot present the local app's Origin
    /// (browsers set it from the page's own URL, not from the request), so only a
    /// document genuinely served by THIS host (or the explicit originless CLI path)
    /// can drive mutating routes. Adding a separate session/CSRF token on top would
    /// be a login-flow for a single local user, which §11a explicitly rules out
    /// ("do not add a login flow for the local user"). Origin/Host gate + POST-only
    /// <c>/api/open</c> + active-content-as-download (never rendered on the origin)
    /// together form the complete local-control boundary; no stateful proof is
    /// necessary because there is no second client to impersonate.
    /// </summary>
    public static bool ControlOriginIsAllowed(string? host, string? origin, int boundPort)
    {
        var hostOk = host is null or "" ? false :
            host == "127.0.0.1" || host == "127.0.0.1:" + boundPort ||
            host == "localhost" || host == "localhost:" + boundPort ||
            host == "::1" || host == "[::1]" || host == "[::1]:" + boundPort;
        if (!hostOk) return false;

        if (string.IsNullOrWhiteSpace(origin)) return true; // originless CLI, loopback host

        Uri u;
        try { u = new Uri(origin, UriKind.Absolute); }
        catch { return false; }
        if (u.Scheme != Uri.UriSchemeHttp) return false;
        if (u.Port != boundPort) return false;
        return u.Host is "127.0.0.1" or "localhost" or "::1" or "[::1]";
    }

    /// Shared path resolution for /api/file and /api/open: an absolute path is
    /// used as-is; a relative path is resolved against the workspace of the
    /// session named by <c>sessionId</c>. Returns a pre-built error
    /// <see cref="IResult"/> in the Error slot when the request cannot be
    /// resolved (Path is then empty).
    /// </summary>
    private async Task<(string Path, IResult? Error)> ResolvePathAsync(HttpContext context)
    {
        var rawPath = context.Request.Query["path"].ToString();
        if (string.IsNullOrWhiteSpace(rawPath))
            return ("", Results.BadRequest("path is required"));

        if (Path.IsPathRooted(rawPath))
            return (Path.GetFullPath(rawPath), null);

        var sid = context.Request.Query["sessionId"].ToString();
        if (!string.IsNullOrWhiteSpace(sid) && Store is not null)
        {
            var session = await Store.GetAsync(sid, context.RequestAborted);
            if (session?.WorkspacePath is { Length: > 0 })
                return (Path.GetFullPath(Path.Combine(session.WorkspacePath!, rawPath)), null);
        }

        // No usable workspace (workspace-less session, missing session id, or
        // unknown session id): fall back to the host process working directory.
        // The desktop shell starts the host with the project root as CWD, as
        // does tools/keep-alive-host.ps1, so repo-relative paths resolve there.
        var cwd = Environment.CurrentDirectory;
        if (!string.IsNullOrEmpty(cwd))
            return (Path.GetFullPath(Path.Combine(cwd, rawPath)), null);

        return ("", Results.BadRequest("path is relative and no workspace is available"));
    }

    /// <summary>
    /// Opens the resolved path with the OS default application (Windows: the
    /// Explorer shell — files launch their registered handler, folders open in
    /// Explorer). Localhost-only like /api/file; the host spawns a short-lived
    /// shell process on behalf of the web UI.
    /// </summary>
    private async Task<IResult> OpenInShellAsync(HttpContext context)
    {
        var (fullPath, error) = await ResolvePathAsync(context);
        if (error is not null)
            return error;

        if (!File.Exists(fullPath) && !Directory.Exists(fullPath))
            return Results.NotFound("file not found");

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = fullPath,
                UseShellExecute = true, // required: hands the path to the shell
                WindowStyle = ProcessWindowStyle.Hidden,
            });
        }
        catch (Exception ex)
        {
            _log.Warning($"failed to open '{fullPath}' in shell: {ex.Message}");
            return Results.Problem(detail: $"could not open in shell: {ex.Message}", statusCode: 500);
        }

        return Results.NoContent();
    }

    /// <summary>astra-1 §11a (F/P5): active content (HTML/SVG) that, if rendered in
    /// the app origin, could execute script — /api/file serves these as a download.</summary>
    public static bool IsActiveContent(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".html" or ".htm" or ".svg" or ".mhtml" or ".mht";
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


    /// <summary>
    /// Drawer title from a message: first line, markdown stripped, folded
    /// whitespace, capped at ~a couple of words (48 chars + ellipsis).
    /// </summary>
    private static string? DeriveSessionTitle(string text)
    {
        var line = text.Split((char)10)[0].Trim().TrimEnd((char)13);
        foreach (var m in new[] { "*", "`", "_", "#", "> ", "- ", "• " })
            line = line.Replace(m, "");
        line = System.Text.RegularExpressions.Regex.Replace(line, "\\s+", " ").Trim();
        if (line.Length == 0) return null;
        return line.Length <= 48 ? line : line[..47].TrimEnd() + "…";
    }


    /// <summary>
    /// One-shot: rename untitled sessions after their first user message so
    /// pre-existing transcripts show meaningful drawer titles. Runs on a
    /// background thread at Web surface start.
    /// </summary>
    private async Task BackfillSessionTitlesAsync(CancellationToken ct)
    {
        using var _storeLease = AcquireStoreLease();
        ISessionStore? Store = _storeLease?.Value;
        if (Store is null) return;
        try
        {
            var total = await Store.CountAsync(ct);
            for (var offset = 0; offset < total; offset += 50)
            {
                if (ct.IsCancellationRequested) return;
                var page = await Store.ListAsync(50, offset, ct);
                foreach (var sess in page)
                {
                    if (ct.IsCancellationRequested) return;
                    if (!string.IsNullOrEmpty(sess.Title)) continue;

                    string? firstUser = null;
                    for (var e = 0; e < 200; e += 50)
                    {
                        var entries = await Store.ReadAsync(sess.Id, e, 50, ct);
                                                var msg = entries
                            .Where(x => x.Kind == EntryKind.Message && x.Message?.Role == MessageRole.User)
                            .Select(x => x.Message!.Parts.OfType<TextPart>()
                                .Select(p => p.Text).Aggregate((a, b) => a + b))
                            .FirstOrDefault(t => !string.IsNullOrWhiteSpace(t));
                        if (msg is not null) { firstUser = msg; break; }
                        if (firstUser is not null || entries.Count < 50) break;
                    }
                    var title = firstUser is null ? null : DeriveSessionTitle(firstUser);
                    if (title is null) continue;

                    await Store.RenameAsync(sess.Id, title, ct);
                    await BroadcastSession(sess.Id, ct);
                    _log.Information($"Backfilled session title: {sess.Id} -> {title}");
                }
            }
        }
        catch (Exception ex)
        {
            _log.Warning($"session title backfill failed: {ex.Message}");
        }
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

