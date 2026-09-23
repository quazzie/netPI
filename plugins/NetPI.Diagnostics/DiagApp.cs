using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using NetPI.Abstractions;

namespace NetPI.Diagnostics;

/// <summary>
/// The first-class diagnostics surface (PLAN §47). Owns a small Kestrel app on a
/// localhost-only port (default 5274) that exposes:
///
///   GET /bootstrap                  health probe
///   GET /panel/diagnostics          live panel page (embedded HTML, self-refreshing)
///   GET /api/diag/overview          host / agent / plugins / models / sessions / jobs
///   GET /api/diag/logs              tail of ~/.netpi/logs (level + plugin filters)
///   GET /api/diag/events?limit=     recent host-bus events (agent/model stream)
///   GET /api/diag/model?limit=      provider wire-decision ring (ModelRequestDiagnostics)
///   GET /api/diag/sessions?limit=   session list
///   GET /api/diag/sessions/{id}     one session's entries (history)
///
/// Everything is read-only. Data flow: the host event bus (subscribed here) plus
/// host-registered services resolved lazily on request — no other plugin's
/// assembly is referenced (reload-safe, PLAN §5).
/// </summary>
public sealed class DiagApp
{
    private const int EventCap = 500;
    private const int ModelCap = 500;

    private readonly IPluginContext _ctx;
    private readonly IPluginLogger _log;
    private readonly int _port;
    private readonly string _runtimeDir;
    /// <summary>The port the Kestrel listener actually bound (honors port 0 → random).
    /// docs/plans/compaction-tool-history.md §6.3: the control endpoints' same-origin
    /// check compares against THIS, so a port-0 deployment validates correctly.</summary>
    private int _boundPort;
    private WebApplication? _app;
    /// <summary>Actual bound URL (populated after <see cref="StartAsync"/>; honors port 0).</summary>
    public string? BoundUrl => _boundUrl;
    private string? _boundUrl;
    private IDisposable? _eventSub;
    private IDisposable? _modelSub;

    private readonly object _gate = new();
    private readonly List<AgentEventRec> _events = [];
    private readonly List<DiagRec> _modelDiagnostics = [];
    private DateTimeOffset? _startedAt;

    /// <summary>One host-bus event, summarized (full payloads stay out of the buffer).</summary>
    private sealed record AgentEventRec(string At, string Type, string? SessionId, string Summary)
    {
        public object ToJson() => new { at = At, type = Type, sessionId = SessionId, summary = Summary };
    }

    /// <summary>One provider wire-decision record, ready for JSON.</summary>
    private sealed record DiagRec(
        string At, string? SessionId, string ModelId,
        string WireConfig, string WireRequested, string WireServed,
        bool Chained, bool Fallback, string? FailureReason, string Summary)
    {
        public object ToJson() => new
        {
            at = At, sessionId = SessionId, modelId = ModelId,
            wireConfig = WireConfig, wireRequested = WireRequested, wireServed = WireServed,
            chained = Chained, fallback = Fallback, failureReason = FailureReason, summary = Summary,
        };
    }

    public DiagApp(IPluginContext context, IPluginLogger log, int port, string runtimeDir, string? urls = null)
    {
        _ctx = context;
        _log = log;
        _port = port;
        _runtimeDir = runtimeDir;
        _urls = urls;
    }
    private readonly string? _urls;

    public async ValueTask StartAsync(CancellationToken ct)
    {
        // Bus subscriptions are owned by this plugin generation: the host
        // removes them on unload (PLAN §7), so the buffers never outlive the gen.
        _eventSub = _ctx.Events.Subscribe<AgentEvent>(OnAgentEvent);
        _modelSub = _ctx.Events.Subscribe<ModelRequestDiagnostics>(OnModelDiagnostics);

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = new[] { $"--urls={_urls ?? $"http://127.0.0.1:{_port}"}" },
        });
        builder.Logging.ClearProviders();
        var app = builder.Build();
        _app = app;
        _startedAt = DateTimeOffset.UtcNow;

        app.MapGet("/", () => "netPI diagnostics. Health at /bootstrap, data under /api/diag");
        app.MapGet("/bootstrap", () => Results.Json(new
        {
            ok = true,
            pluginId = "netpi.diagnostics",
            port = _port,
            startedAt = _startedAt.Value.ToString("O"),
        }));
        app.MapGet("/panel/diagnostics", async (HttpContext c) =>
        {
            c.Response.ContentType = "text/html; charset=utf-8";
            await c.Response.Body.WriteAsync(Encoding.UTF8.GetBytes(PanelHtml()));
        });
        app.MapGet("/api/diag/overview", async (HttpContext c) =>
        {
            var payload = await BuildOverviewAsync(c.RequestAborted);
            c.Response.ContentType = "application/json; charset=utf-8";
            await c.Response.WriteAsync(JsonSerializer.Serialize(payload, DiagJson));
        });
        app.MapGet("/api/diag/logs", async (HttpContext c) =>
        {
            var payload = TailLogs(
                int.TryParse(c.Request.Query["lines"], out var l) ? l : 200,
                c.Request.Query["level"],
                c.Request.Query["plugin"],
                c.Request.Query["file"]);
            c.Response.ContentType = "application/json; charset=utf-8";
            await c.Response.WriteAsync(JsonSerializer.Serialize(payload, DiagJson));
        });
        app.MapGet("/api/diag/events", async (HttpContext c) =>
        {
            var payload = SnapshotEvents(
                int.TryParse(c.Request.Query["limit"], out var lim) ? lim : 100,
                c.Request.Query["type"],
                c.Request.Query["session"]);
            c.Response.ContentType = "application/json; charset=utf-8";
            await c.Response.WriteAsync(JsonSerializer.Serialize(payload, DiagJson));
        });
        app.MapGet("/api/diag/model", async (HttpContext c) =>
        {
            var payload = SnapshotModel(
                int.TryParse(c.Request.Query["limit"], out var lim) ? lim : 100,
                c.Request.Query["session"]);
            c.Response.ContentType = "application/json; charset=utf-8";
            await c.Response.WriteAsync(JsonSerializer.Serialize(payload, DiagJson));
        });
        app.MapGet("/api/diag/sessions", async (HttpContext c) =>
        {
            var payload = await SnapshotSessionsAsync(
                int.TryParse(c.Request.Query["limit"], out var lim) ? lim : 25, ct);
            c.Response.ContentType = "application/json; charset=utf-8";
            await c.Response.WriteAsync(JsonSerializer.Serialize(payload, DiagJson));
        });
        app.MapGet("/api/diag/sessions/{id}", async (HttpContext c) =>
        {
            var id = c.Request.RouteValues["id"] as string ?? "";
            var payload = await SnapshotSessionAsync(id,
                int.TryParse(c.Request.Query["count"], out var cnt) ? cnt : 100, ct);
            c.Response.ContentType = "application/json; charset=utf-8";
            if (payload is null) { c.Response.StatusCode = 404; await c.Response.WriteAsync("{}"); }
            else await c.Response.WriteAsync(JsonSerializer.Serialize(payload, DiagJson));
        });

        // ---- control endpoints (docs/plans/compaction-tool-history.md §6) ----------
        // Narrowly scoped, same-origin, POST-only mutations. The panel is served from
        // THIS Kestrel (same origin), so it drives them directly — it never uses the
        // cross-origin host WebSocket (the host's :5173 control boundary rejects it, and
        // protocol.md forbids a cross-origin panel from connecting to it). Every mutation
        // is loopback-Host + same-origin-validated (matching the established control-
        // boundary policy), GET is rejected (405), and the operation is ENQUEUED (deferred):
        // the outcome arrives on the next overview poll, never from this request — so
        // reloading Diagnostics itself does not hold the request open while its own
        // generation drains.
        app.MapGet("/api/diag/reload", () => Results.StatusCode(405));
        app.MapPost("/api/diag/reload", async (HttpContext c) =>
        {
            if (!ControlOriginOk(c, out var why)) { await RejectOriginAsync(c, why); return; }
            var pluginId = c.Request.Query["pluginId"];
            if (string.IsNullOrEmpty(pluginId)) { c.Response.StatusCode = 400; await c.Response.WriteAsync("{\"error\":\"pluginId required\"}"); return; }
            var facade = TryResolve<IPluginManagerFacade>("plugins");
            if (facade is null) { c.Response.StatusCode = 503; await c.Response.WriteAsync("{\"error\":\"plugin manager unavailable\"}"); return; }
            var opId = facade.EnqueueReload(pluginId, null, c.RequestAborted); // deferred — no build id = reload to current pointer
            await WriteJsonAsync(c, new { operationId = opId, scheduled = true });
        });
        app.MapGet("/api/diag/reload-all", () => Results.StatusCode(405));
        app.MapPost("/api/diag/reload-all", async (HttpContext c) =>
        {
            if (!ControlOriginOk(c, out var why)) { await RejectOriginAsync(c, why); return; }
            var facade = TryResolve<IPluginManagerFacade>("plugins");
            if (facade is null) { c.Response.StatusCode = 503; await c.Response.WriteAsync("{\"error\":\"plugin manager unavailable\"}"); return; }
            var opId = facade.EnqueueReloadAll(c.RequestAborted);
            await WriteJsonAsync(c, new { operationId = opId, scheduled = true });
        });
        app.MapGet("/api/diag/scan", () => Results.StatusCode(405));
        app.MapPost("/api/diag/scan", async (HttpContext c) =>
        {
            if (!ControlOriginOk(c, out var why)) { await RejectOriginAsync(c, why); return; }
            var facade = TryResolve<IPluginManagerFacade>("plugins");
            if (facade is null) { c.Response.StatusCode = 503; await c.Response.WriteAsync("{\"error\":\"plugin manager unavailable\"}"); return; }
            var opId = facade.EnqueueScan(c.RequestAborted);
            await WriteJsonAsync(c, new { operationId = opId, scheduled = true });
        });
        app.MapGet("/api/diag/models/refresh", () => Results.StatusCode(405));
        app.MapPost("/api/diag/models/refresh", async (HttpContext c) =>
        {
            if (!ControlOriginOk(c, out var why)) { await RejectOriginAsync(c, why); return; }
            var catalog = TryResolve<IModelCatalog>("catalog");
            if (catalog is null) { c.Response.StatusCode = 503; await c.Response.WriteAsync("{\"error\":\"model catalog unavailable\"}"); return; }
            try
            {
                var models = await catalog.RefreshAsync(c.RequestAborted);
                await WriteJsonAsync(c, new { ok = true, count = models.Count });
            }
            catch (Exception ex) { c.Response.StatusCode = 502; await WriteJsonAsync(c, new { ok = false, error = ex.Message }); }
        });

        // Statement-bodied handlers only — the /api/file ALC gotcha (PLAN §41):
        // method-group / pass-through async delegates degrade to empty 200s.
        await app.StartAsync(ct);
        _boundUrl = app.Urls.FirstOrDefault();
        _boundPort = ParsePort(_boundUrl) ?? _port;
        _log.Information($"Diagnostics surface listening on {_boundUrl ?? $"http://127.0.0.1:{_port}"}");
    }

    public async ValueTask StopAsync(CancellationToken ct)
    {
        _eventSub?.Dispose();
        _modelSub?.Dispose();
        _eventSub = null;
        _modelSub = null;
        if (_app is null) return;
        var app = _app;
        _app = null;
        try { await app.StopAsync(ct); await app.DisposeAsync(); }
        catch { /* shutdown is best-effort */ }
    }

    // ---- bus handlers --------------------------------------------------------

    private void OnAgentEvent(AgentEvent e)
    {
        var rec = new AgentEventRec(e.At.ToString("O"), e.Type.ToString(), e.SessionId, Summarize(e));
        lock (_gate)
        {
            _events.Add(rec);
            if (_events.Count > EventCap) _events.RemoveAt(0);
        }
    }

    private void OnModelDiagnostics(ModelRequestDiagnostics e)
    {
        var rec = new DiagRec(
            DateTimeOffset.UtcNow.ToString("O"), e.SessionId, e.ModelId,
            e.WireConfig, e.WireRequested, e.WireServed,
            e.Chained, e.Fallback, e.FailureReason, e.Summary);
        lock (_gate)
        {
            _modelDiagnostics.Add(rec);
            if (_modelDiagnostics.Count > ModelCap) _modelDiagnostics.RemoveAt(0);
        }
    }

    private static string Summarize(AgentEvent e)
    {
        var p = e.Payload;
        if (p is not { ValueKind: JsonValueKind.Object } obj) return "";
        string? Kind = Str(obj, "kind");
        string? Error = Str(obj, "error");
        if (e.Type == AgentEventType.ModelStreamEvent)
        {
            var text = Str(obj, "text");
            return Kind is null ? ""
                : text is null ? $"stream {Kind}"
                : $"stream {Kind} +{text.Length}";
        }
        if (Error is not null) return $"error: {Error}";
        if (Kind is not null) return Kind;
        return e.Type.ToString();
    }

    private static string? Str(System.Text.Json.JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    // ---- endpoint payloads ---------------------------------------------------

    private static readonly JsonSerializerOptions DiagJson = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private async ValueTask<object> BuildOverviewAsync(CancellationToken ct)
    {
        try { return new
        {
            host = new
            {
                pid = Environment.ProcessId,
                uptimeSeconds = (long)Math.Max(0, (DateTimeOffset.UtcNow - _startedAt!).Value.TotalSeconds),
                runtimeDir = _runtimeDir,
            },
            agent = AgentStateJson(),
            plugins = PluginsJson(),
            // astra-1 P5: retired-ALC collection state (separate from the per-plugin rows).
            unloadedAlocs = UnloadedAlocsJson(),
            models = ModelsJson(),
            sessions = await SessionsJsonAsync(ct),
            backgroundJobs = await JobsJsonAsync(ct),
        }; }
        catch (Exception ex) { _log.Warning($"overview failed: {ex.Message}"); return new { error = ex.Message }; }
    }

    private object? AgentStateJson()
    {
        var runtime = TryResolve<IAgentRuntime>("agent");
        var runner = TryResolve<IAgentRunner>("runner");
        if (runtime is null && runner is null) return null;
        return new
        {
            state = runtime?.State.State.ToString() ?? (runner?.IsRunning == true ? "Running" : "Unknown"),
            running = runtime?.State.IsRunning ?? (runner?.IsRunning ?? false),
            activeSessionId = runtime?.State.ActiveSessionId,
        };
    }

    private object PluginsJson()
    {
        var facade = TryResolve<IPluginManagerFacade>("plugins");
        if (facade is null) return new object[] { };
        return facade.GetStatus().Select(p => new
        {
            id = p.Id, name = p.Name, version = p.Version, state = p.State,
            generation = p.Generation, policy = p.Policy,
            activeLeases = p.ActiveLeases, lastError = p.LastError,
            // astra-1 P5: the update picture + last lifecycle operation + old-ALC state.
            availableBuildId = p.Update?.AvailableBuildId,
            loadedPath = p.Update?.LoadedPath,
            blockingLeases = p.Update?.BlockingLeases,
            prevAlocCollected = p.Update?.PrevAlocCollected,
            lastOperation = p.LastOperation is { } o ? new
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

    // astra-1 P5: retired-ALC collection state ("gc" view) as a separate payload.
    private object UnloadedAlocsJson()
    {
        var facade = TryResolve<IPluginManagerFacade>("plugins");
        if (facade is null) return new object[0];
        return facade.UnloadedAlocs().Select(a => new
        {
            label = a.Label, collected = a.Collected, pluginId = a.PluginId,
        }).ToArray();
    }


    private object? ModelsJson()
    {
        var catalog = TryResolve<IModelCatalog>("catalog");
        if (catalog is null) return null;
        return new
        {
            stale = catalog.IsStale,
            lastRefreshedAt = catalog.LastRefreshedAt,
            models = catalog.Models.Select(m => new
            {
                id = m.ModelId, provider = m.Provider,
                supportsTools = m.SupportsTools, supportsResponses = m.SupportsResponses,
            }),
        };
    }

    private async ValueTask<object> SessionsJsonAsync(CancellationToken ct)
    {
        var store = TryResolve<ISessionStore>("sessions");
        if (store is null) return new object[0];
        try
        {
            var list = await store.ListAsync(25, 0, ct);
            return list.Select(SessionJson).ToArray();
        }
        catch { return new object[0]; }
    }

    private object SessionJson(SessionInfo s) => new
    {
        id = s.Id, title = s.Title, workspace = s.WorkspacePath,
        entryCount = s.EntryCount, modelId = s.ModelId,
        createdAt = s.CreatedAt, updatedAt = s.UpdatedAt,
    };

    private async ValueTask<object?> JobsJsonAsync(CancellationToken ct)
    {
        var jobs = TryResolve<IBackgroundJobManager>("background");
        if (jobs is null) return null;
        try
        {
            var list = await jobs.ListAsync(ct);
            return list.Select(j => new
            {
                id = j.JobId, command = j.Command, state = j.State.ToString(),
                startedAt = j.StartedAt, exitCode = j.ExitCode,
            }).ToArray();
        }
        catch { return null; }
    }

    // The host keeps the log open; FileShare.ReadWrite lets us read it live
    // (works regardless of the writer's share mode — PLAN §47).
    private static IEnumerable<string> ReadLinesShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(fs);
        string? line;
        while ((line = reader.ReadLine()) is not null) yield return line;
    }

    private object TailLogs(int lines, string? level, string? plugin, string? file)
    {
        var logDir = Path.Combine(_runtimeDir, "logs");
        List<string> linesOut = [];
        string? source = null;
        try
        {
            if (file is not null)
            {
                var named = Path.Combine(logDir, Path.GetFileName(file)); // no traversal
                if (File.Exists(named)) { source = Path.GetFileName(named); linesOut = ReadLinesShared(named).ToList(); }
            }
            else
            {
                var newest = Directory.EnumerateFiles(logDir, "netpi-*.log")
                    .OrderBy(n => n).LastOrDefault();
                if (newest is not null)
                {
                    source = Path.GetFileName(newest);
                    linesOut = ReadLinesShared(newest).ToList();
                }
            }
        }
        catch (Exception ex) { return new { error = ex.Message }; }

        if (linesOut.Count > 0)
        {
            var filtered = linesOut.AsEnumerable();
            if (level is not null)
                filtered = filtered.Where(l => ContainsToken(l, level));
            if (plugin is not null)
                filtered = filtered.Where(l => ContainsToken(l, plugin));
            linesOut = filtered.TakeLast(Math.Max(1, lines)).ToList();
        }
        return new { file = source, totalLines = linesOut.Count, lines = linesOut };
    }

    private static bool ContainsToken(string line, string token)
        => line.Contains(token, StringComparison.OrdinalIgnoreCase);

    private object SnapshotEvents(int limit, string? type, string? session)
    {
        List<AgentEventRec> snapshot;
        lock (_gate) snapshot = _events.ToList();
        var filtered = snapshot.Where(r =>
            (type is null || r.Type == type) &&
            (session is null || r.SessionId == session));
        return new
        {
            count = _events.Count,
            events = filtered.TakeLast(Math.Max(1, limit)).Select(r => r.ToJson()).ToArray(),
        };
    }

    private object SnapshotModel(int limit, string? session)
    {
        List<DiagRec> snapshot;
        lock (_gate) snapshot = _modelDiagnostics.ToList();
        var filtered = snapshot.Where(r => session is null || r.SessionId == session);
        return new
        {
            count = _modelDiagnostics.Count,
            requests = filtered.TakeLast(Math.Max(1, limit)).Select(r => r.ToJson()).ToArray(),
        };
    }

    private async ValueTask<object> SnapshotSessionsAsync(int limit, CancellationToken ct)
    {
        var store = TryResolve<ISessionStore>("sessions");
        if (store is null) return new { error = "no session store registered" };
        var list = await store.ListAsync(limit, 0, ct);
        return new { count = list.Count, sessions = list.Select(SessionJson).ToArray() };
    }

    private async ValueTask<object?> SnapshotSessionAsync(string id, int count, CancellationToken ct)
    {
        var store = TryResolve<ISessionStore>("sessions");
        if (store is null) return null;
        var info = await store.GetAsync(id, ct);
        if (info is null) return null;
        count = Math.Clamp(count, 1, 500);
        var entries = await store.ReadAsync(id, 0, count, ct);
        return new
        {
            session = SessionJson(info),
            entries = entries.Select(EntryJson).ToArray(),
        };
    }

    private static object EntryJson(SessionEntry e)
    {
        string? text = e.Message?.Parts
            .Where(p => p is TextPart or ThinkingPart)
            .Select(p => p switch
            {
                TextPart t => t.Text,
                ThinkingPart t => $"[thinking] {t.Text}",
                _ => null,
            })
            .Where(t => t is not null)
            .Aggregate((a, b) => $"{a}\n{b}");
        return new
        {
            id = e.Id, sequence = e.Sequence, kind = e.Kind.ToString(),
            role = e.Message?.Role.ToString().ToLowerInvariant(),
            text = Truncate(text), createdAt = e.CreatedAt,
        };
    }

    private static string? Truncate(string? s) => s is null ? null : s.Length > 4000 ? s[..4000] + "…" : s;

    private static string PanelHtml()
    {
        var asm = typeof(DiagApp).Assembly;
        var resName = asm.GetManifestResourceNames()
            .Single(n => n.EndsWith(".panels.diagnostics.html", StringComparison.OrdinalIgnoreCase));
        using var stream = asm.GetManifestResourceStream(resName)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    // ---- service resolution (never reference another plugin's ALC) -----------

    private T? TryResolve<T>(string id) where T : notnull
    {
        try { return _ctx.Services.Resolve<T>(id); }
        catch { return default; }
    }

    // ---- control boundary (docs/plans/compaction-tool-history.md §6.3) --------

    /// <summary>
    /// docs/plans/compaction-tool-history.md §6.3: the same local-control-boundary
    /// predicate the host uses for its mutating routes — the loopback listener is NOT
    /// browser-origin validation, so a malicious page in ANY browser can still open a
    /// request to 127.0.0.1:port. The Host must be a loopback name AND, when an Origin
    /// is present, it must be the same http:// loopback host:port (the local app, i.e.
    /// THIS surface). An absent Origin is allowed ONLY on a loopback Host (the
    /// originless CLI path); a present-but-wrong Origin is rejected. No permissive CORS
    /// and no origin allowlisting — the panel is same-origin, so its own Origin passes.
    /// </summary>
    private bool ControlOriginOk(HttpContext c, out string reason)
    {
        var host = c.Request.Host.Host;
        if (!IsLoopbackHost(host)) { reason = $"host is not loopback ({host})"; return false; }
        var origin = c.Request.Headers.Origin.ToString();
        if (string.IsNullOrWhiteSpace(origin)) { reason = ""; return true; } // originless CLI, loopback host
        Uri u;
        try { u = new Uri(origin, UriKind.Absolute); }
        catch { reason = "unparseable Origin"; return false; }
        if (u.Scheme != Uri.UriSchemeHttp) { reason = "Origin scheme is not http"; return false; }
        if (u.Port != _boundPort) { reason = $"Origin port {u.Port} != bound port {_boundPort}"; return false; }
        if (!IsLoopbackHost(u.Host)) { reason = $"Origin host is not loopback ({u.Host})"; return false; }
        reason = "";
        return true;
    }

    private static bool IsLoopbackHost(string host) =>
        host is "127.0.0.1" or "localhost" or "::1" or "[::1]";

    private static async Task RejectOriginAsync(HttpContext c, string reason)
    {
        c.Response.StatusCode = 403;
        c.Response.ContentType = "application/json; charset=utf-8";
        await c.Response.WriteAsync($"{{\"error\":\"control origin rejected: {reason}\"}}");
    }

    private static async Task WriteJsonAsync(HttpContext c, object payload)
    {
        c.Response.ContentType = "application/json; charset=utf-8";
        await c.Response.WriteAsync(JsonSerializer.Serialize(payload, DiagJson));
    }

    private static int? ParsePort(string? url)
    {
        if (string.IsNullOrEmpty(url)) return null;
        try { return new Uri(url, UriKind.Absolute).Port; }
        catch { return null; }
    }
}
