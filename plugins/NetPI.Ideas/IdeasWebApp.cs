using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using NetPI.Abstractions;

namespace NetPI.Ideas;

/// <summary>
/// The Ideas panel surface. Owns a small Kestrel app on a localhost-only port
/// (default 5277) that exposes the panel page and the ideas-bank API:
///
///   GET  /panel/ideas                         the Ideas panel page (embedded HTML)
///   GET  /api/ideas?workspace=…                the bank of one project (default: host CWD)
///   GET  /api/ideas/{id}/text?workspace=…      the panel-composed text (for copy/paste)
///   POST /api/ideas                            create an idea
///   POST /api/ideas/{id}/update                patch an idea
///   POST /api/ideas/{id}/remove                delete an idea
///   POST /api/ideas/{id}/prefill               "insert into chat": publish a
///                                              ChatPrefillEvent (the Web surface
///                                              forwards it as chat.prefill — the
///                                              panel cannot touch the composer
///                                              directly: cross-origin, and the
///                                              shell↔panel bridge is
///                                              navigation-only)
///   POST /api/ideas/{id}/start                 "do this idea now": start an agent
///                                              run on the project's session via
///                                              the runner contract (the run's
///                                              events flow to the UI like any
///                                              chat send; if the pool is full it
///                                              is QUEUED, not rejected)
///
/// Every external service is resolved LAZILY: a missing storage/agent plugin
/// surfaces an error on the affected action only — this plugin never blocks
/// normal chat, and it references the other plugins' assemblies nowhere (the
/// Abstractions contracts are the only coupling).
/// </summary>
public sealed class IdeasWebApp
{
    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly IPluginContext _ctx;
    private readonly int _port;
    private WebApplication? _app;
    private string? _html;

    /// <summary>Actual bound URL (populated after <see cref="StartAsync"/>; honors port 0).</summary>
    public string? BoundUrl { get; private set; }

    public IdeasWebApp(IPluginContext ctx, int port)
    {
        _ctx = ctx;
        _port = port;
    }

    public async ValueTask StartAsync(CancellationToken ct)
    {
        _html = LoadPanelHtml();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = new[] { $"--urls=http://127.0.0.1:{_port}" },
        });
        builder.Logging.ClearProviders();
        var app = builder.Build();
        _app = app;

        app.MapGet("/", () => "netPI ideas (project memory bank). Panel at /panel/ideas, data under /api/ideas");
        app.MapGet("/panel/ideas", async (HttpContext c) =>
        {
            c.Response.ContentType = "text/html; charset=utf-8";
            await c.Response.Body.WriteAsync(Encoding.UTF8.GetBytes(_html ?? "panel unavailable"));
        });

        app.MapGet("/api/ideas", async (HttpContext c) =>
        {
            var ws = EffectiveWorkspace(Q(c, "workspace"));
            try
            {
                var store = new IdeasStore(ws);
                c.Response.ContentType = "application/json; charset=utf-8";
                await c.Response.WriteAsync(JsonSerializer.Serialize(new
                {
                    available = true,
                    workspace = store.Workspace,
                    file = store.FilePath,
                    statuses = IdeasStore.KnownStatuses,
                    ideas = store.Load().OrderByDescending(i => i.UpdatedAt).ToList(),
                }, Json));
            }
            catch (IdeasStoreException ex)
            {
                c.Response.StatusCode = StatusCodes.Status400BadRequest;
                c.Response.ContentType = "application/json; charset=utf-8";
                await c.Response.WriteAsync(JsonSerializer.Serialize(new { available = false, error = ex.Message }, Json));
            }
        });

        app.MapGet("/api/ideas/{id}/text", async (HttpContext c) =>
        {
            var id = c.Request.RouteValues["id"] as string ?? "";
            var ws = EffectiveWorkspace(Q(c, "workspace"));
            try
            {
                var rec = new IdeasStore(ws).Find(id);
                if (rec is null)
                {
                    c.Response.StatusCode = StatusCodes.Status404NotFound;
                    c.Response.ContentType = "application/json; charset=utf-8";
                    await c.Response.WriteAsync(JsonSerializer.Serialize(new { ok = false, error = $"no idea matching \"{id}\"" }, Json));
                    return;
                }
                c.Response.ContentType = "application/json; charset=utf-8";
                await c.Response.WriteAsync(JsonSerializer.Serialize(new { ok = true, text = BuildPrefillText(rec) }, Json));
            }
            catch (IdeasStoreException ex)
            {
                c.Response.StatusCode = StatusCodes.Status400BadRequest;
                c.Response.ContentType = "application/json; charset=utf-8";
                await c.Response.WriteAsync(JsonSerializer.Serialize(new { ok = false, error = ex.Message }, Json));
            }
        });

        app.MapPost("/api/ideas", async (HttpContext c) =>
        {
            var body = await ReadBodyAsync(c);
            var ws = EffectiveWorkspace(S(body, "workspace"));
            try
            {
                var store = new IdeasStore(ws);
                var rec = store.Add(
                    S(body, "title") ?? "",
                    S(body, "body"), S(body, "notes"), S(body, "plan"), S(body, "status"), Tags(body));
                c.Response.ContentType = "application/json; charset=utf-8";
                await c.Response.WriteAsync(JsonSerializer.Serialize(new { ok = true, file = store.FilePath, idea = rec }, Json));
            }
            catch (IdeasStoreException ex)
            {
                c.Response.StatusCode = StatusCodes.Status400BadRequest;
                c.Response.ContentType = "application/json; charset=utf-8";
                await c.Response.WriteAsync(JsonSerializer.Serialize(new { ok = false, error = ex.Message }, Json));
            }
        });

        app.MapPost("/api/ideas/{id}/update", async (HttpContext c) =>
        {
            var id = c.Request.RouteValues["id"] as string ?? "";
            var body = await ReadBodyAsync(c);
            var ws = EffectiveWorkspace(S(body, "workspace"));
            try
            {
                var store = new IdeasStore(ws);
                var rec = store.Update(id, S(body, "title"), S(body, "body"), S(body, "notes"),
                    S(body, "plan"), S(body, "status"), Tags(body));
                if (rec is null)
                {
                    c.Response.StatusCode = StatusCodes.Status404NotFound;
                    c.Response.ContentType = "application/json; charset=utf-8";
                    await c.Response.WriteAsync(JsonSerializer.Serialize(new { ok = false, error = $"no idea matching \"{id}\"" }, Json));
                    return;
                }
                c.Response.ContentType = "application/json; charset=utf-8";
                await c.Response.WriteAsync(JsonSerializer.Serialize(new { ok = true, file = store.FilePath, idea = rec }, Json));
            }
            catch (IdeasStoreException ex)
            {
                c.Response.StatusCode = StatusCodes.Status400BadRequest;
                c.Response.ContentType = "application/json; charset=utf-8";
                await c.Response.WriteAsync(JsonSerializer.Serialize(new { ok = false, error = ex.Message }, Json));
            }
        });

        app.MapPost("/api/ideas/{id}/remove", async (HttpContext c) =>
        {
            var id = c.Request.RouteValues["id"] as string ?? "";
            var body = await ReadBodyAsync(c);
            var ws = EffectiveWorkspace(S(body, "workspace"));
            try
            {
                var store = new IdeasStore(ws);
                var ok = store.Remove(id);
                c.Response.ContentType = "application/json; charset=utf-8";
                await c.Response.WriteAsync(JsonSerializer.Serialize(new { ok, file = store.FilePath }, Json));
            }
            catch (IdeasStoreException ex)
            {
                c.Response.StatusCode = StatusCodes.Status400BadRequest;
                c.Response.ContentType = "application/json; charset=utf-8";
                await c.Response.WriteAsync(JsonSerializer.Serialize(new { ok = false, error = ex.Message }, Json));
            }
        });

        app.MapPost("/api/ideas/{id}/prefill", async (HttpContext c) =>
        {
            var id = c.Request.RouteValues["id"] as string ?? "";
            var body = await ReadBodyAsync(c);
            var ws = EffectiveWorkspace(S(body, "workspace"));
            try
            {
                var rec = new IdeasStore(ws).Find(id);
                if (rec is null)
                {
                    c.Response.StatusCode = StatusCodes.Status404NotFound;
                    c.Response.ContentType = "application/json; charset=utf-8";
                    await c.Response.WriteAsync(JsonSerializer.Serialize(new { ok = false, error = $"no idea matching \"{id}\"" }, Json));
                    return;
                }
                var text = BuildPrefillText(rec);
                await _ctx.Events.PublishAsync(new ChatPrefillEvent(text, S(body, "sessionId")), c.RequestAborted);
                c.Response.ContentType = "application/json; charset=utf-8";
                await c.Response.WriteAsync(JsonSerializer.Serialize(new { ok = true, chars = text.Length }, Json));
            }
            catch (IdeasStoreException ex)
            {
                c.Response.StatusCode = StatusCodes.Status400BadRequest;
                c.Response.ContentType = "application/json; charset=utf-8";
                await c.Response.WriteAsync(JsonSerializer.Serialize(new { ok = false, error = ex.Message }, Json));
            }
        });

        app.MapPost("/api/ideas/{id}/start", async (HttpContext c) =>
        {
            var id = c.Request.RouteValues["id"] as string ?? "";
            var body = await ReadBodyAsync(c);
            var ws = EffectiveWorkspace(S(body, "workspace"));
            var ct = c.RequestAborted;
            try
            {
                var rec = new IdeasStore(ws).Find(id);
                if (rec is null)
                {
                    c.Response.StatusCode = StatusCodes.Status404NotFound;
                    c.Response.ContentType = "application/json; charset=utf-8";
                    await c.Response.WriteAsync(JsonSerializer.Serialize(new { ok = false, error = $"no idea matching \"{id}\"" }, Json));
                    return;
                }
                var sessions = Sessions();
                var runner = Runner();
                if (sessions is null || runner is null)
                {
                    c.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                    c.Response.ContentType = "application/json; charset=utf-8";
                    await c.Response.WriteAsync(JsonSerializer.Serialize(new
                    {
                        ok = false,
                        error = "session store or agent runner unavailable — the storage/agent plugins are not loaded",
                    }, Json));
                    return;
                }
                // The project's most recent session; a project never opened in chat
                // gets a fresh one (titled after the idea, like a chat auto-title).
                var all = await sessions.ListAsync(200, 0, ct);
                var target = all.Where(s => WorkspaceMatches(s.WorkspacePath, ws))
                    .OrderByDescending(s => s.UpdatedAt).FirstOrDefault();
                var createdSession = false;
                if (target is null)
                {
                    target = await sessions.CreateAsync(ws, ct);
                    await sessions.RenameAsync(target.Id, rec.Title, ct);
                    createdSession = true;
                }
                var model = target.ModelId;
                if (string.IsNullOrEmpty(model) && Catalog()?.Models.FirstOrDefault() is { } first)
                    model = first.ModelId;
                var started = await runner.StartRunAsync(new AgentRunRequest(
                    target.Id, ws, model, BuildStartMessage(rec),
                    ReasoningLevel: target.ReasoningLevel), ct);
                c.Response.ContentType = "application/json; charset=utf-8";
                await c.Response.WriteAsync(JsonSerializer.Serialize(new
                {
                    ok = started.Note is null,
                    error = started.Note,
                    sessionId = started.SessionId ?? target.Id,
                    runId = started.RunId,
                    disposition = started.Disposition.ToString().ToLowerInvariant(),
                    createdSession,
                }, Json));
            }
            catch (IdeasStoreException ex)
            {
                c.Response.StatusCode = StatusCodes.Status400BadRequest;
                c.Response.ContentType = "application/json; charset=utf-8";
                await c.Response.WriteAsync(JsonSerializer.Serialize(new { ok = false, error = ex.Message }, Json));
            }
            catch (Exception ex)
            {
                c.Response.StatusCode = StatusCodes.Status500InternalServerError;
                c.Response.ContentType = "application/json; charset=utf-8";
                await c.Response.WriteAsync(JsonSerializer.Serialize(new { ok = false, error = ex.Message }, Json));
            }
        });

        // Statement-bodied handlers only — the /api/file ALC gotcha (PLAN §41).
        await app.StartAsync(ct);
        BoundUrl = app.Urls.FirstOrDefault();
        _ctx.Log.Information($"Ideas surface listening on {BoundUrl ?? $"http://127.0.0.1:{_port}"}");
    }

    public async ValueTask StopAsync(CancellationToken ct)
    {
        if (_app is null) return;
        var app = _app;
        _app = null;
        try { await app.StopAsync(ct); await app.DisposeAsync(); }
        catch { /* shutdown is best-effort */ }
    }

    // ---- panel text builders (pure; unit-tested) ----------------------------

    /// <summary>
    /// The message a "do this idea now" run starts with — written as the
    /// user's own instruction so the agent acts on the record in ideas.json.
    /// </summary>
    internal static string BuildStartMessage(IdeaRecord idea)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Let's work on the idea \"{idea.Title}\" from this project's ideas bank " +
                      $"(ideas.json in the project root, id \"{idea.Id}\").");
        if (!string.IsNullOrWhiteSpace(idea.Body))
        {
            sb.AppendLine();
            sb.AppendLine("Idea:");
            sb.AppendLine(idea.Body);
        }
        if (!string.IsNullOrWhiteSpace(idea.Notes))
        {
            sb.AppendLine();
            sb.AppendLine("Notes:");
            sb.AppendLine(idea.Notes);
        }
        if (!string.IsNullOrWhiteSpace(idea.Plan))
        {
            sb.AppendLine();
            sb.AppendLine("Plan:");
            sb.AppendLine(idea.Plan);
        }
        sb.AppendLine();
        sb.AppendLine("Read the full record from ideas.json, agree a brief plan with me (or start straight in " +
                      "when the plan is already concrete), then implement it in this workspace. Keep the record " +
                      "in ideas.json current while you work: status \"in-progress\" while working, notes/plan as " +
                      "things progress, and status \"done\" when finished.");
        return sb.ToString();
    }

    /// <summary>
    /// The text "insert into chat" puts in the composer (and what Copy hands
    /// to the clipboard): the idea's title plus whatever body/notes/plan exist.
    /// </summary>
    internal static string BuildPrefillText(IdeaRecord idea)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Idea: {idea.Title} (id {idea.Id} · {idea.Status})");
        if (!string.IsNullOrWhiteSpace(idea.Body)) sb.AppendLine(idea.Body);
        if (!string.IsNullOrWhiteSpace(idea.Notes))
        {
            if (sb.Length > 0) sb.AppendLine();
            sb.Append("Notes: ");
            sb.AppendLine(idea.Notes);
        }
        if (!string.IsNullOrWhiteSpace(idea.Plan))
        {
            if (sb.Length > 0) sb.AppendLine();
            sb.Append("Plan: ");
            sb.AppendLine(idea.Plan);
        }
        return sb.ToString().TrimEnd();
    }

    // ---- helpers -------------------------------------------------------------

    private static string EffectiveWorkspace(string? w)
        => string.IsNullOrWhiteSpace(w) ? Environment.CurrentDirectory : Path.GetFullPath(w);

    private static string? Q(HttpContext c, string key)
        => c.Request.Query.TryGetValue(key, out var v) ? v.ToString() : null;

    private static string? S(JsonElement e, string k)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(k, out var v)
           && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string[]? Tags(JsonElement e)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty("tags", out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToArray()
            : null;

    private static async Task<JsonElement> ReadBodyAsync(HttpContext c)
    {
        try
        {
            using var ms = new MemoryStream();
            await c.Request.Body.CopyToAsync(ms, c.RequestAborted);
            ms.Position = 0;
            if (ms.Length == 0)
                return JsonSerializer.SerializeToElement(new { });
            var raw = Encoding.UTF8.GetString(ms.ToArray());
            var doc = JsonDocument.Parse(raw);
            return doc.RootElement.Clone();
        }
        catch
        {
            return JsonSerializer.SerializeToElement(new { });
        }
    }

    private static bool WorkspaceMatches(string? a, string b)
    {
        if (string.IsNullOrWhiteSpace(a)) return false;
        try
        {
            var na = Path.TrimEndingDirectorySeparator(Path.GetFullPath(a));
            var nb = Path.TrimEndingDirectorySeparator(Path.GetFullPath(b));
            var cmp = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            return string.Equals(na, nb, cmp);
        }
        catch { return false; }
    }

    private string LoadPanelHtml()
    {
        var asm = typeof(IdeasWebApp).Assembly;
        var name = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("panels.ideas.html", StringComparison.OrdinalIgnoreCase));
        if (name is null) return "<html><body>ideas panel missing</body></html>";
        using var s = asm.GetManifestResourceStream(name)!;
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    // ---- lazy service resolution (missing plugin => null, never an error) ----

    private ISessionStore? Sessions()
    {
        try { return _ctx.Services.Resolve<ISessionStore>("sessions"); }
        catch (ServiceUnavailableException) { return null; }
    }

    private IAgentRunner? Runner()
    {
        try { return _ctx.Services.Resolve<IAgentRunner>("runner"); }
        catch (ServiceUnavailableException) { return null; }
    }

    private IModelCatalog? Catalog()
    {
        try { return _ctx.Services.Resolve<IModelCatalog>("catalog"); }
        catch (ServiceUnavailableException) { return null; }
    }
}
