using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using NetPI.Abstractions;

namespace NetPI.BackgroundTasks;

/// <summary>
/// The background-tasks web surface (docs/web-panels.md). Owns a small Kestrel
/// app on a localhost-only port (default 5275) that exposes:
///
///   GET  /panel/background       live panel page (embedded HTML, self-refreshing)
///   GET  /api/bg/jobs            all jobs with state, exit code, and timing
///   GET  /api/bg/{id}/output     tail of a job's captured output (?chars=)
///   POST /api/bg/{id}/kill       stop a job and its process tree
///
/// The panel page and the API share this one origin, so the page needs no
/// cross-origin access (same shape as the Diagnostics surface, PLAN §47).
/// </summary>
public sealed class BgWebApp
{
    private static readonly JsonSerializerOptions BgJson = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly BackgroundJobManager _mgr;
    private readonly IPluginLogger _log;
    private readonly int _port;
    private readonly string? _urls;
    private WebApplication? _app;

    /// <summary>Actual bound URL (populated after <see cref="StartAsync"/>; honors port 0).</summary>
    public string? BoundUrl => _boundUrl;
    private string? _boundUrl;

    public BgWebApp(BackgroundJobManager mgr, IPluginLogger log, int port, string? urls = null)
    {
        _mgr = mgr;
        _log = log;
        _port = port;
        _urls = urls;
    }

    public async ValueTask StartAsync(CancellationToken ct)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = new[] { $"--urls={_urls ?? $"http://127.0.0.1:{_port}"}" },
        });
        builder.Logging.ClearProviders();
        var app = builder.Build();
        _app = app;

        app.MapGet("/", () => "netPI background tasks. Panel at /panel/background, data under /api/bg");
        app.MapGet("/panel/background", async (HttpContext c) =>
        {
            c.Response.ContentType = "text/html; charset=utf-8";
            await c.Response.Body.WriteAsync(Encoding.UTF8.GetBytes(PanelHtml()));
        });
        app.MapGet("/api/bg/jobs", async (HttpContext c) =>
        {
            var list = await _mgr.ListAsync(c.RequestAborted);
            var payload = new
            {
                count = list.Count,
                running = list.Count(j => j.State == BackgroundJobState.Running),
                jobs = list
                    .OrderByDescending(j => j.StartedAt)
                    .Select(j => new
                    {
                        id = j.JobId,
                        shell = j.ShellId,
                        command = j.Command,
                        state = j.State.ToString(),
                        startedAt = j.StartedAt,
                        exitedAt = j.ExitedAt,
                        exitCode = j.ExitCode,
                    })
                    .ToArray(),
            };
            c.Response.ContentType = "application/json; charset=utf-8";
            await c.Response.WriteAsync(JsonSerializer.Serialize(payload, BgJson));
        });
        app.MapGet("/api/bg/{id}/output", async (HttpContext c) =>
        {
            var id = c.Request.RouteValues["id"] as string ?? "";
            var chars = int.TryParse(c.Request.Query["chars"], out var n) ? n : 20_000;
            chars = Math.Clamp(chars, 1, 256_000);
            var outp = await _mgr.GetTailAsync(id, chars, c.RequestAborted);
            c.Response.ContentType = "application/json; charset=utf-8";
            await c.Response.WriteAsync(JsonSerializer.Serialize(new
            {
                id = outp.JobId,
                state = outp.State.ToString(),
                exitCode = outp.ExitCode,
                truncated = outp.Truncated,
                text = outp.Text,
            }, BgJson));
        });
        app.MapPost("/api/bg/{id}/kill", async (HttpContext c) =>
        {
            var id = c.Request.RouteValues["id"] as string ?? "";
            var ok = await _mgr.KillAsync(id, c.RequestAborted);
            c.Response.ContentType = "application/json; charset=utf-8";
            await c.Response.WriteAsync(JsonSerializer.Serialize(new { ok, id }, BgJson));
        });

        // Statement-bodied handlers only — the /api/file ALC gotcha (PLAN §41):
        // method-group / pass-through async delegates degrade to empty 200s.
        await app.StartAsync(ct);
        _boundUrl = app.Urls.FirstOrDefault();
        _log.Information($"Background tasks surface listening on {_boundUrl ?? $"http://127.0.0.1:{_port}"}");
    }

    public async ValueTask StopAsync(CancellationToken ct)
    {
        if (_app is null) return;
        var app = _app;
        _app = null;
        try { await app.StopAsync(ct); await app.DisposeAsync(); }
        catch { /* shutdown is best-effort */ }
    }

    private static string PanelHtml()
    {
        var asm = typeof(BgWebApp).Assembly;
        var resName = asm.GetManifestResourceNames()
            .Single(n => n.EndsWith(".panels.background.html", StringComparison.OrdinalIgnoreCase));
        using var stream = asm.GetManifestResourceStream(resName)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
