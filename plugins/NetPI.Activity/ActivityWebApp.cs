using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using NetPI.Abstractions;

namespace NetPI.Activity;

/// <summary>
/// astra-2 §12: the Work panel surface (presentation owner for the combined
/// Background/Activity view). Owns a small Kestrel app on a localhost-only port
/// (default 5276) that exposes:
///
///   GET  /panel/activity                         the Work panel page (embedded HTML)
///   GET  /panel/background                       compat redirect to /panel/activity
///   GET  /api/activity/agents                    lifecycle rows + pool snapshots +
///                                                monotonic revision + per-service
///                                                availability (astra-2 §12.2 data
///                                                contract; legacy `runs` kept)
///   GET  /api/activity/work                      the combined Work snapshot (agents +
///                                                lanes + processes) for the panel poll
///   POST /api/activity/agents/{id}/cancel        cancel via the orchestration contract
///                                                (RunId + explicit subtree semantics;
///                                                queued/suspended cancellable); legacy
///                                                runner fallback
///   GET  /api/activity/processes                 background jobs + foreground shell processes
///   GET  /api/activity/processes/background/{id}/output   LATEST bounded tail of a job
///   POST /api/activity/processes/background/{id}/stop    stop a background job (tree)
///
/// The page and the API share one origin, so the page needs no cross-origin
/// access. Every external service is resolved LAZILY: a missing
/// orchestration/lanes/background/foreground plugin surfaces
/// <c>available=false</c> in that section (per-service availability) — partial
/// failures never blank the other sections, and this plugin never blocks
/// normal chat. It never references BackgroundJobManager's concrete type; only
/// the Abstractions contracts are used.
/// </summary>
public sealed class ActivityWebApp
{
    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>Bounded recent-terminal history exposed to the panel (astra-2 §12.1).</summary>
    internal const int HistoryLimit = 20;

    /// <summary>Maximum ring-slice fetches when composing a tail (ring is 256k; 64k per fetch).</summary>
    private const int MaxTailFetches = 4;

    private readonly IPluginContext _ctx;
    private readonly int _port;
    private WebApplication? _app;
    private string? _boundUrl;

    /// <summary>
    /// Monotonic revision over the orchestration projection (astra-2 §12.2:
    /// "monotonic revision"). Bumped once per successful orchestrator query;
    /// stays 0 while the service is unavailable (clients fall back to polling).
    /// </summary>
    private long _revision;

    /// <summary>Actual bound URL (populated after <see cref="StartAsync"/>; honors port 0).</summary>
    public string? BoundUrl => _boundUrl;

    public ActivityWebApp(IPluginContext ctx, int port)
    {
        _ctx = ctx;
        _port = port;
    }

    public async ValueTask StartAsync(CancellationToken ct)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = new[] { $"--urls=http://127.0.0.1:{_port}" },
        });
        builder.Logging.ClearProviders();
        var app = builder.Build();
        _app = app;

        app.MapGet("/", () => "netPI work (agents + processes). Panel at /panel/activity, data under /api/activity");
        app.MapGet("/panel/activity", async (HttpContext c) =>
        {
            c.Response.ContentType = "text/html; charset=utf-8";
            await c.Response.Body.WriteAsync(Encoding.UTF8.GetBytes(PanelHtml()));
        });

        // astra-2 §12.1: /panel/background must keep resolving. The combined
        // Work view IS the registered background panel, served here; a 302 keeps
        // old bookmarks/links working without a second evolving UI.
        app.MapGet("/panel/background", (HttpContext c) =>
        {
            c.Response.StatusCode = StatusCodes.Status302Found;
            c.Response.Headers.Location = "/panel/activity";
            return ValueTask.CompletedTask;
        });
        app.MapGet("/panel/bg", (HttpContext c) =>
        {
            c.Response.StatusCode = StatusCodes.Status302Found;
            c.Response.Headers.Location = "/panel/activity";
            return ValueTask.CompletedTask;
        });

        app.MapGet("/api/activity/agents", async (HttpContext c) =>
        {
            var payload = await AgentsPayloadAsync(c.RequestAborted);
            c.Response.ContentType = "application/json; charset=utf-8";
            await c.Response.WriteAsync(JsonSerializer.Serialize(payload, Json));
        });

        // astra-2 §12.2: one combined snapshot for the panel's 2 s poll —
        // agents + lanes + processes with per-section availability. The page
        // polls this; the separate routes below stay for compatibility.
        app.MapGet("/api/activity/work", async (HttpContext c) =>
        {
            var ct = c.RequestAborted;
            var agents = await AgentsPayloadAsync(ct);
            var processes = await ProcessesPayloadAsync(ct);
            var lanes = LanesPayloadAsync();
            c.Response.ContentType = "application/json; charset=utf-8";
            await c.Response.WriteAsync(JsonSerializer.Serialize(new
            {
                agents,
                lanes,
                processes,
                ts = DateTimeOffset.UtcNow,
            }, Json));
        });

        // astra-2 §12.2: cancellation delegates through the orchestration
        // contract with RunId + explicit subtree semantics (queued/suspended
        // assignments are cancellable). Legacy runner fallback for hosts that
        // predate the orchestrator.
        app.MapPost("/api/activity/agents/{id}/cancel", async (HttpContext c) =>
        {
            var id = c.Request.RouteValues["id"] as string ?? "";
            bool subtree = true; // explicit subtree semantics (§9: cancel scope is the tree)
            if (c.Request.Query.TryGetValue("subtree", out var q) &&
                bool.TryParse(q.ToString(), out var b))
                subtree = b;
            var body = await ReadBodyAsync(c);
            if (body.TryGetProperty("subtree", out var sb) && sb.ValueKind is JsonValueKind.True or JsonValueKind.False)
                subtree = sb.GetBoolean();

            var orch = Orchestrator();
            bool ok = false;
            string outcome = "unavailable";
            if (orch is not null)
            {
                var known = await orch.GetAssignmentAsync(id, c.RequestAborted);
                if (known is not null)
                {
                    var result = await orch.CancelAsync(id, subtree, c.RequestAborted);
                    ok = true;
                    outcome = AgentAssignmentLifecycleNames.Name(result);
                }
            }
            if (outcome == "unavailable")
            {
                var runner = Runner();
                ok = runner is not null && runner.CancelRun(id);
                outcome = ok ? "cancelled" : "unavailable";
            }
            c.Response.ContentType = "application/json; charset=utf-8";
            await c.Response.WriteAsync(JsonSerializer.Serialize(
                new { ok, runId = id, subtree, outcome }, Json));
        });

        app.MapGet("/api/activity/processes", async (HttpContext c) =>
        {
            var payload = await ProcessesPayloadAsync(c.RequestAborted);
            c.Response.ContentType = "application/json; charset=utf-8";
            await c.Response.WriteAsync(JsonSerializer.Serialize(payload, Json));
        });

        app.MapGet("/api/activity/processes/background/{id}/output", async (HttpContext c) =>
        {
            var id = c.Request.RouteValues["id"] as string ?? "";
            var chars = int.TryParse(c.Request.Query["chars"], out var n) ? n : 20_000;
            chars = Math.Clamp(chars, 1, 256_000);
            var jobs = Jobs();
            if (jobs is null)
            {
                c.Response.ContentType = "application/json; charset=utf-8";
                await c.Response.WriteAsync(JsonSerializer.Serialize(new { available = false, text = "" }, Json));
                return;
            }
            var (tail, truncated, state, exitCode) = await LatestTailAsync(jobs, id, chars, c.RequestAborted);
            c.Response.ContentType = "application/json; charset=utf-8";
            await c.Response.WriteAsync(JsonSerializer.Serialize(new
            {
                available = true,
                id,
                state,
                exitCode,
                truncated,
                text = tail,
            }, Json));
        });

        app.MapPost("/api/activity/processes/background/{id}/stop", async (HttpContext c) =>
        {
            var id = c.Request.RouteValues["id"] as string ?? "";
            var jobs = Jobs();
            var ok = jobs is not null && await jobs.KillAsync(id, c.RequestAborted);
            c.Response.ContentType = "application/json; charset=utf-8";
            await c.Response.WriteAsync(JsonSerializer.Serialize(new { ok, id }, Json));
        });

        // Statement-bodied handlers only — the /api/file ALC gotcha (PLAN §41):
        // method-group / pass-through async delegates degrade to empty 200s.
        await app.StartAsync(ct);
        _boundUrl = app.Urls.FirstOrDefault();
        _ctx.Log.Information($"Activity surface listening on {_boundUrl ?? $"http://127.0.0.1:{_port}"}");
    }

    public async ValueTask StopAsync(CancellationToken ct)
    {
        if (_app is null) return;
        var app = _app;
        _app = null;
        try { await app.StopAsync(ct); await app.DisposeAsync(); }
        catch { /* shutdown is best-effort */ }
    }

    // ---- lazy service resolution (missing plugin => null, never an error) ----

    private IAgentRunner? Runner()
    {
        try { return _ctx.Services.Resolve<IAgentRunner>("runner"); }
        catch (ServiceUnavailableException) { return null; }
    }

    private IBackgroundJobManager? Jobs()
    {
        try { return _ctx.Services.Resolve<IBackgroundJobManager>("background"); }
        catch (ServiceUnavailableException) { return null; }
    }

    private IForegroundProcessTracker? Foreground()
    {
        try { return _ctx.Services.Resolve<IForegroundProcessTracker>("foreground-processes"); }
        catch (ServiceUnavailableException) { return null; }
    }

    private IAgentOrchestrator? Orchestrator()
    {
        try { return _ctx.Services.Resolve<IAgentOrchestrator>("orchestration"); }
        catch (ServiceUnavailableException) { return null; }
    }

    private ILaneScheduler? Lanes()
    {
        try { return _ctx.Services.Resolve<ILaneScheduler>("lanes"); }
        catch (ServiceUnavailableException) { return null; }
    }

    // ---- payloads -------------------------------------------------------------

    /// <summary>
    /// The astra-2 §12.2 agents payload: lifecycle rows (the canonical
    /// projection from the orchestration service — ALL nonterminal assignments
    /// across every session/team, newest createdAt first, stable id tiebreak),
    /// bounded recent-terminal history, pool snapshots, a monotonic revision,
    /// and EXPLICIT per-service availability. The legacy <c>runs</c> array is
    /// kept from the runner's run-query so older clients keep working; when the
    /// orchestrator is present it is the source of truth for the lifecycle
    /// rows and <c>runs</c> stays empty-compatible (the runner may still be
    /// absent — the two services fail independently).
    /// </summary>
    internal async ValueTask<object> AgentsPayloadAsync(CancellationToken ct)
    {
        var orch = Orchestrator();
        var agents = Array.Empty<object>();
        var history = Array.Empty<object>();
        var runs = Array.Empty<object>();
        long revision = 0;
        bool agentsAvailable = false;

        if (orch is not null)
        {
            try
            {
                var rows = await orch.ListAssignmentsAsync(ct);
                // astra-2 §12.1: the Agents section lists ALL NONTERMINAL
                // assignments (the session-busy gate's source of truth), newest
                // createdAt first with a stable id tiebreak. Terminal rows go to
                // the bounded `history` below — never into the live list (we
                // filter defensively; ListAssignmentsAsync is specified to return
                // nonterminal rows, but a producer that returns all rows must
                // not leak terminal assignments into the live section).
                agents = rows
                    .Where(r => r.IsNonTerminal)
                    .OrderByDescending(r => r.CreatedAt)
                    .ThenByDescending(r => r.AssignmentId, StringComparer.Ordinal)
                    .Select(ToAgentRow)
                    .ToArray();
                history = rows
                    .Where(r => !r.IsNonTerminal)
                    .OrderByDescending(r => r.EndedAt ?? r.CreatedAt)
                    .ThenByDescending(r => r.AssignmentId, StringComparer.Ordinal)
                    .Take(HistoryLimit)
                    .Select(ToAgentRow)
                    .ToArray();
                agentsAvailable = true;
                revision = Interlocked.Increment(ref _revision);
            }
            catch
            {
                // A failing projection degrades the section, not the view.
                agentsAvailable = false;
            }
        }

        var runner = Runner();
        if (runner is not null)
        {
            try
            {
                runs = runner.ListRuns()
                    .Select(r => new
                    {
                        runId = r.RunId,
                        sessionId = r.SessionId,
                        modelId = r.ModelId,
                        state = r.State.ToString(),
                        outcome = r.Outcome.ToString(),
                        startTime = r.StartTime,
                        endTime = r.EndTime,
                        isRunning = r.State != AgentState.Idle,
                    })
                    .ToArray();
            }
            catch
            {
                runs = Array.Empty<object>();
            }
        }

        return new
        {
            // Legacy shape (available = "can we list anything agent-side") plus
            // the astra-2 explicit per-service availability.
            available = agentsAvailable || runner is not null,
            agentsAvailable,
            lanesAvailable = Lanes() is not null,
            runsAvailable = runner is not null,
            revision,
            agents,
            history,
            runs,
            pools = LanesPayloadAsync().Pools,
        };
    }

    /// <summary>Pool snapshots from the lane scheduler (astra-2 §12.1 lane summary).</summary>
    internal LanePayload LanesPayloadAsync()
    {
        var lanes = Lanes();
        if (lanes is null)
            return new LanePayload(false, Array.Empty<LaneRowDto>());
        try
        {
            var pools = lanes.Snapshots()
                .OrderBy(p => p.PoolId, StringComparer.Ordinal)
                .Select(p => new LaneRowDto
                {
                    PoolId = p.PoolId,
                    Enabled = p.Enabled,
                    Draining = p.Draining,
                    OwnedCount = p.OwnedCount,
                    EffectiveCapacity = p.EffectiveCapacity,
                    CapacityMode = LaneCapacityModeNames.Name(p.CapacityMode),
                    ProviderReportedConcurrency = p.ProviderReportedConcurrency,
                    UserCap = p.UserCap,
                    ProviderStatus = ProviderCapacityStatusNames.Name(p.ProviderStatus),
                    ProviderObservedAt = p.ProviderObservedAt,
                    QueueCount = p.QueueCount,
                    BlockedReason = p.BlockedReason,
                    CanAdmit = p.CanAdmit,
                })
                .ToArray();
            return new LanePayload(true, pools);
        }
        catch
        {
            return new LanePayload(false, Array.Empty<LaneRowDto>());
        }
    }

    private async ValueTask<object> ProcessesPayloadAsync(CancellationToken ct)
    {
        var jobs = Jobs();
        var bg = jobs is null
            ? null
            : (await jobs.ListAsync(ct)).Select(j => new
            {
                id = j.JobId,
                shell = j.ShellId,
                command = j.Command,
                state = j.State.ToString(),
                startedAt = j.StartedAt,
                exitedAt = j.ExitedAt,
                exitCode = j.ExitCode,
                sessionId = j.SessionId,
                projectId = j.ProjectId,
                workingDirectory = j.WorkingDirectory,
            })
            // astra-2 §12.1: newest-first by startedAt, stable id tiebreak.
            // No running-first regrouping (that was the old background page).
            .OrderByDescending(j => j.startedAt)
            .ThenByDescending(j => j.id, StringComparer.Ordinal)
            .ToArray();

        var fg = Foreground();
        var fgRunning = fg is null ? null : fg.Running().Select(ToForeground).ToArray();
        var fgRecent = fg is null ? null : fg.Recent(50).Select(ToForeground).ToArray();

        return new
        {
            background = new { available = jobs is not null, jobs = bg },
            foreground = new
            {
                available = fg is not null,
                running = fgRunning,
                recent = fgRecent,
            },
        };
    }

    private static object ToForeground(ForegroundProcessInfo p) => new
    {
        toolShellId = p.ToolShellId,
        command = p.Command,
        workingDirectory = p.WorkingDirectory,
        sessionId = p.SessionId,
        processId = p.ProcessId,
        startedAt = p.StartedAt,
        exitedAt = p.ExitedAt,
        exitCode = p.ExitCode,
        isRunning = p.IsRunning,
    };

    /// <summary>
    /// The LATEST requested tail of a job's captured output (astra-2 §12.2:
    /// "returns the latest requested tail, not the tail of only the first
    /// fetched chunk"). The contract is cursor-based: <c>GetOutputAsync</c>
    /// serves at most one slice per call from the bounded ring, so we page to
    /// the end of the ring (a bounded number of slices — the ring is capped
    /// well above one slice) and keep the last <c>chars</c> characters. Only
    /// the IBackgroundJobManager contract is used — never the concrete
    /// BackgroundJobManager.
    /// </summary>
    internal async ValueTask<(string Tail, bool Truncated, string State, int? ExitCode)> LatestTailAsync(
        IBackgroundJobManager jobs, string jobId, int chars, CancellationToken ct)
    {
        var kept = new StringBuilder();
        bool droppedFromFront = false;
        bool noSuchJob = false;
        BackgroundJobState state = BackgroundJobState.Failed;
        int? exitCode = null;

        // Cursor-based paging to the END of the ring (astra-2 §12.2): each
        // GetOutputAsync call serves at most one bounded slice and reports the
        // offset of the next slice. A cursor (not a fixed count) is essential —
        // stopping at the first slice would return the tail of the first chunk,
        // not the tail of the ring. Bounded by both the ring cap and a fetch cap.
        int next = 0;
        int fetches = 0;
        while (fetches < MaxTailFetches && kept.Length < 256_000)
        {
            fetches++;
            var outp = await jobs.GetOutputAsync(jobId, next, ct);
            state = outp.State;
            exitCode = outp.ExitCode;
            if (outp.Text == "no such job")
            {
                noSuchJob = true;
                break;
            }
            droppedFromFront |= outp.Truncated;
            kept.Append(outp.Text);
            if (string.IsNullOrEmpty(outp.Text)) break;          // ring ended
            if (outp.NextOffset <= next) break;                  // no progress
            next = outp.NextOffset;                             // cursor advances
        }

        if (noSuchJob)
            return ("", false, "failed", null);

        var text = kept.ToString();
        if (text.Length > chars)
        {
            text = text[^chars..];
            droppedFromFront = true;
        }
        return (text, droppedFromFront, state.ToString(), exitCode);
    }
    private static string PanelHtml()
    {
        var asm = typeof(ActivityWebApp).Assembly;
        var resName = asm.GetManifestResourceNames()
            .Single(n => n.EndsWith(".panels.activity.html", StringComparison.OrdinalIgnoreCase));
        using var stream = asm.GetManifestResourceStream(resName)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static async ValueTask<JsonElement> ReadBodyAsync(HttpContext c)
    {
        using var ms = new MemoryStream();
        await c.Request.Body.CopyToAsync(ms, c.RequestAborted);
        if (ms.Length == 0)
            return JsonDocument.Parse("{}").RootElement.Clone();
        ms.Position = 0;
        return JsonDocument.Parse(ms).RootElement.Clone();
    }

    private static object ToAgentRow(AgentAssignmentRow r) => new
    {
        assignmentId = r.AssignmentId,
        agentId = r.AgentId,
        teamId = r.TeamId,
        sessionId = r.SessionId,
        parentAgentId = r.ParentAgentId,
        // astra-2 §12.2: documented lower-case lifecycle strings, never ToString.
        lifecycle = AgentAssignmentLifecycleNames.Name(r.Lifecycle),
        nonTerminal = r.IsNonTerminal,
        phase = r.Phase.ToString(),
        executionMode = r.ExecutionMode == DeploymentExecutionMode.DirectCloud ? "cloud-direct" : "pooled",
        poolId = r.PoolId,
        laneId = r.LaneId,
        deploymentId = r.DeploymentId,
        modelId = r.ModelId,
        title = r.Title,
        createdAt = r.CreatedAt,
        startedAt = r.StartedAt,
        endedAt = r.EndedAt,
        reason = r.Reason,
    };
}

/// <summary>Wire row for one pool snapshot (astra-2 §12.1 lane summary).</summary>
public sealed class LaneRowDto
{
    public string PoolId { get; init; } = "";
    public bool Enabled { get; init; }
    public bool Draining { get; init; }
    public int OwnedCount { get; init; }
    public int? EffectiveCapacity { get; init; }
    public string CapacityMode { get; init; } = "unknown";
    public int? ProviderReportedConcurrency { get; init; }
    public int? UserCap { get; init; }
    public string ProviderStatus { get; init; } = "unknown";
    public DateTimeOffset? ProviderObservedAt { get; init; }
    public int QueueCount { get; init; }
    public string? BlockedReason { get; init; }
    public bool CanAdmit { get; init; }
}

/// <summary>The lanes section of the Work payload (explicit availability).</summary>
public sealed record LanePayload(bool Available, LaneRowDto[] Pools);

/// <summary>
/// Lower-case wire names for the non-lifecycle enums the panel renders
/// (astra-2 §12.2: explicit, consistent string formats — no C# ToString).
/// </summary>
public static class LaneCapacityModeNames
{
    public static string Name(LaneCapacityMode m) => m switch
    {
        LaneCapacityMode.Provider => "provider",
        LaneCapacityMode.Manual => "manual",
        _ => "unknown",
    };
}

public static class ProviderCapacityStatusNames
{
    public static string Name(ProviderCapacityStatus s) => s switch
    {
        ProviderCapacityStatus.Unknown => "unknown",
        ProviderCapacityStatus.Stale => "stale",
        ProviderCapacityStatus.Fresh => "fresh",
        _ => "unknown",
    };
}
