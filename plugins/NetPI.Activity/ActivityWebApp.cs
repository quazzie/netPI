using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using NetPI.Abstractions;

namespace NetPI.Activity;

/// <summary>
/// astra-1 H: the Activity web surface. Owns a small Kestrel app on a
/// localhost-only port (default 5276) that exposes:
///
///   GET  /panel/activity                        the panel page (embedded HTML, self-refreshing)
///   GET  /api/activity/agents                   all runs from every session (run-query)
///   POST /api/activity/agents/{runId}/cancel    signal cancel on one specific run
///   GET  /api/activity/processes                background jobs + foreground shell processes
///   GET  /api/activity/processes/background/{id}/output   tail of a background job's output
///   POST /api/activity/processes/background/{id}/stop    stop a background job (process tree)
///
/// The page and the API share one origin, so the page needs no cross-origin
/// access (same shape as BackgroundTasks / Diagnostics, PLAN §47). Every external
/// service is resolved LAZILY: a missing plugin surfaces "unavailable", never an
/// error, so this plugin never blocks normal chat.
/// </summary>
public sealed class ActivityWebApp
{
    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly IPluginContext _ctx;
    private readonly int _port;
    private WebApplication? _app;
    private string? _boundUrl;

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

        app.MapGet("/", () => "netPI activity. Panel at /panel/activity, data under /api/activity");
        app.MapGet("/panel/activity", async (HttpContext c) =>
        {
            c.Response.ContentType = "text/html; charset=utf-8";
            await c.Response.Body.WriteAsync(Encoding.UTF8.GetBytes(PanelHtml()));
        });

        app.MapGet("/api/activity/agents", async (HttpContext c) =>
        {
            var payload = await AgentsPayloadAsync(c.RequestAborted);
            c.Response.ContentType = "application/json; charset=utf-8";
            await c.Response.WriteAsync(JsonSerializer.Serialize(payload, Json));
        });

        app.MapPost("/api/activity/agents/{runId}/cancel", async (HttpContext c) =>
        {
            var runId = c.Request.RouteValues["runId"] as string ?? "";
            var runner = Runner();
            var cancelled = runner is not null && runner.CancelRun(runId);
            c.Response.ContentType = "application/json; charset=utf-8";
            await c.Response.WriteAsync(JsonSerializer.Serialize(new { ok = cancelled, runId }, Json));
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
            var outp = await jobs.GetOutputAsync(id, 0, c.RequestAborted);
            var tail = outp.Text.Length > chars ? outp.Text[^chars..] : outp.Text;
            c.Response.ContentType = "application/json; charset=utf-8";
            await c.Response.WriteAsync(JsonSerializer.Serialize(new
            {
                available = true,
                id = outp.JobId,
                state = outp.State.ToString(),
                exitCode = outp.ExitCode,
                truncated = outp.Truncated,
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

    // ---- payloads ---------------------------------------------------------

    private async ValueTask<object> AgentsPayloadAsync(CancellationToken ct)
    {
        var runner = Runner();
        if (runner is null)
            return new { available = false, runs = Array.Empty<object>() };

        var runs = runner.ListRuns()
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
        return new { available = true, runs };
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
            }).ToArray();

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

    private static string PanelHtml()
    {
        var asm = typeof(ActivityWebApp).Assembly;
        var resName = asm.GetManifestResourceNames()
            .Single(n => n.EndsWith(".panels.activity.html", StringComparison.OrdinalIgnoreCase));
        using var stream = asm.GetManifestResourceStream(resName)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
