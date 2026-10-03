using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NetPI.Host.Logging;

namespace NetPI.Host.Web;

/// <summary>
/// Kestrel on 127.0.0.1 with a single hand-written router (no MVC/routing overhead):
/// <c>/api/health</c> (open), <c>/api/rpc/{method}</c>, <c>/api/p/{pluginId}/…</c>, <c>/ws</c>,
/// <c>/plugins/{pluginId}/…</c> (plugin wwwroot) and the SPA from WebRoot with index.html fallback.
/// </summary>
internal sealed class WebServer : IAsyncDisposable
{
    public const string CookieName = "netpi_token";
    public const string TokenHeader = "X-NetPI-Token";
    private const string ImmutableCache = "public, max-age=31536000, immutable";

    /// <summary>
    /// The UI is model-written markdown in a WebView, so a page that reaches it can be one the model wrote: no script
    /// and no frame may come from anywhere but this origin, no style is loaded from anywhere, and nothing may be framed
    /// or used as a base. Images and styles are the exceptions the app itself needs (data:/blob: pictures, the inline
    /// styles svelte components set), and a plain <c>ws:</c> for the socket.
    /// </summary>
    public const string ContentSecurityPolicy =
        "default-src 'self'; img-src 'self' data: blob:; style-src 'self' 'unsafe-inline'; " +
        "connect-src 'self' ws: wss:; object-src 'none'; base-uri 'none'; frame-ancestors 'none'";

    private static readonly JsonDocumentOptions BodyOptions = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };
    private static readonly FileExtensionContentTypeProvider ContentTypes = CreateContentTypes();

    private readonly HostKernel _k;
    private readonly ILogger _log;
    private readonly WsHub _hub;
    private readonly CancellationTokenSource _stopping = new();
    private readonly byte[] _token;
    private WebApplication? _app;
    private int _disposed;

    private WebServer(HostKernel kernel, WsHub hub)
    {
        _k = kernel;
        _hub = hub;
        _log = kernel.LoggerFactory.CreateLogger("NetPI.Web");
        _token = Encoding.UTF8.GetBytes(kernel.Token);
    }

    public int Port { get; private set; }
    public string BaseUrl => $"http://127.0.0.1:{Port}";

    public static async Task<WebServer> StartAsync(HostKernel kernel, CancellationToken ct)
    {
        var desired = kernel.Options.Port ?? kernel.Settings.Get<int?>("server.port") ?? 7431;
        if (desired is < 0 or > 65535) desired = 7431;
        var server = new WebServer(kernel, new WsHub(kernel));
        try
        {
            try
            {
                await server.ListenAsync(desired, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (desired != 0 && ex is IOException or SocketException)
            {
                kernel.Log.LogWarning("Port {Port} is not available ({Error}); using a random free port", desired, ex.Message);
                await server.ListenAsync(0, ct).ConfigureAwait(false);
            }
            return server;
        }
        catch
        {
            await server.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task ListenAsync(int port, CancellationToken ct)
    {
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions
        {
            ApplicationName = "NetPI",
            ContentRootPath = _k.Paths.AppDir,
            EnvironmentName = Environments.Production,
        });
        builder.Services.AddLogging(b => LoggingSetup.Configure(b, _k.LogSink));
        builder.WebHost.UseKestrelCore().ConfigureKestrel(o =>
        {
            o.Listen(IPAddress.Loopback, port);
            o.AddServerHeader = false;
            o.Limits.MaxRequestBodySize = 64L * 1024 * 1024;
        });
        var app = builder.Build();
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });
        app.Run(HandleAsync);
        try
        {
            await app.StartAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            await app.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses.FirstOrDefault();
        Port = address is not null && Uri.TryCreate(address, UriKind.Absolute, out var uri) ? uri.Port : port;
        _app = app;
    }

    // ------------------------------------------------------------------ routing

    private async Task HandleAsync(HttpContext ctx)
    {
        var path = ctx.Request.Path.Value ?? "/";
        ctx.Response.Headers.XContentTypeOptions = "nosniff";
        ctx.Response.Headers.ContentSecurityPolicy = ContentSecurityPolicy;
        ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
        try
        {
            if (path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase)) await ApiAsync(ctx, path).ConfigureAwait(false);
            else if (path.Equals("/ws", StringComparison.OrdinalIgnoreCase)) await WebSocketAsync(ctx).ConfigureAwait(false);
            else if (path.StartsWith("/plugins/", StringComparison.OrdinalIgnoreCase)) await PluginFileAsync(ctx, path).ConfigureAwait(false);
            else if ((path == "/" || path.Equals("/index.html", StringComparison.OrdinalIgnoreCase)) && ctx.Request.Query.ContainsKey("token")) await LoginAsync(ctx).ConfigureAwait(false);
            else await StaticAsync(ctx, path).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (!ctx.Response.HasStarted)
        {
            _log.LogError(ex, "{Method} {Path} failed", ctx.Request.Method, path);
            await WriteErrorAsync(ctx, 500, "internal", ex.Message).ConfigureAwait(false);
        }
    }

    private async Task ApiAsync(HttpContext ctx, string path)
    {
        if (ApplyCors(ctx)) return; // preflight answered
        if (path.Equals("/api/health", StringComparison.OrdinalIgnoreCase))
        {
            await WriteBytesAsync(ctx, 200, Wire.SerializeValue(new { ok = true, name = "netpi", version = HostInfo.Version })).ConfigureAwait(false);
            return;
        }
        // a plugin route mapped open authenticates its requests itself (IHttpRegistry.Map): an extension cannot hold
        // this run's token and comes from its own origin
        if (path.StartsWith("/api/p/", StringComparison.OrdinalIgnoreCase) && IsOpenPluginRoute(path["/api/p/".Length..]))
        {
            await PluginHttpAsync(ctx, path["/api/p/".Length..]).ConfigureAwait(false);
            return;
        }
        if (!IsAuthorized(ctx))
        {
            await WriteErrorAsync(ctx, 401, "unauthorized", "Missing or invalid token").ConfigureAwait(false);
            return;
        }
        if (!IsOriginAllowed(ctx))
        {
            await WriteErrorAsync(ctx, 403, "forbidden", "Origin not allowed").ConfigureAwait(false);
            return;
        }
        if (path.StartsWith("/api/rpc/", StringComparison.OrdinalIgnoreCase))
            await RpcAsync(ctx, path["/api/rpc/".Length..]).ConfigureAwait(false);
        else if (path.StartsWith("/api/p/", StringComparison.OrdinalIgnoreCase))
            await PluginHttpAsync(ctx, path["/api/p/".Length..]).ConfigureAwait(false);
        else
            await WriteErrorAsync(ctx, 404, "not_found", $"No API endpoint {path}").ConfigureAwait(false);
    }

    private async Task RpcAsync(HttpContext ctx, string method)
    {
        if (!HttpMethods.IsPost(ctx.Request.Method))
        {
            ctx.Response.Headers.Allow = "POST";
            await WriteErrorAsync(ctx, 405, "bad_request", "Use POST").ConfigureAwait(false);
            return;
        }
        if (method.Length == 0)
        {
            await WriteErrorAsync(ctx, 404, "not_found", "Missing method").ConfigureAwait(false);
            return;
        }
        JsonElement parameters = default;
        using (var body = new MemoryStream())
        {
            await ctx.Request.Body.CopyToAsync(body, ctx.RequestAborted).ConfigureAwait(false);
            var bytes = body.GetBuffer().AsMemory(0, (int)body.Length);
            if (!bytes.Span.Trim(" \t\r\n"u8).IsEmpty)
            {
                try
                {
                    using var doc = JsonDocument.Parse(bytes, BodyOptions);
                    parameters = doc.RootElement.Clone();
                }
                catch (JsonException ex)
                {
                    await WriteErrorAsync(ctx, 400, "bad_request", "Invalid JSON body: " + ex.Message).ConfigureAwait(false);
                    return;
                }
            }
        }
        byte[] response;
        try
        {
            var result = await _k.Rpc.InvokeAsync(method, parameters, null, ctx.RequestAborted).ConfigureAwait(false);
            response = Wire.SerializeValue(result);
        }
        catch (Exception ex)
        {
            var (code, message, status) = Wire.MapError(ex, _log, method);
            await WriteErrorAsync(ctx, status, code, message).ConfigureAwait(false);
            return;
        }
        ctx.Response.Headers.CacheControl = "no-store";
        await WriteBytesAsync(ctx, 200, response).ConfigureAwait(false);
    }

    private bool IsOpenPluginRoute(string rest)
    {
        var slash = rest.IndexOf('/');
        var pluginId = slash < 0 ? rest : rest[..slash];
        return pluginId.Length > 0 && _k.Http.IsOpen(pluginId, slash < 0 ? "" : rest[(slash + 1)..]);
    }

    private async Task PluginHttpAsync(HttpContext ctx, string rest)
    {
        var slash = rest.IndexOf('/');
        var pluginId = slash < 0 ? rest : rest[..slash];
        var sub = slash < 0 ? "" : rest[(slash + 1)..];
        var handler = pluginId.Length == 0 ? null : _k.Http.Match(pluginId, sub);
        if (handler is null)
        {
            await WriteErrorAsync(ctx, 404, "not_found", $"No handler for /api/p/{rest}").ConfigureAwait(false);
            return;
        }
        var originalBase = ctx.Request.PathBase;
        var originalPath = ctx.Request.Path;
        ctx.Request.PathBase = originalBase.Add(new PathString("/api/p/" + pluginId));
        ctx.Request.Path = new PathString("/" + sub);
        try
        {
            await handler(ctx).ConfigureAwait(false);
        }
        catch (Exception ex) when (!ctx.Response.HasStarted && ex is not OperationCanceledException)
        {
            _log.LogError(ex, "Plugin endpoint /api/p/{Path} failed", rest);
            await WriteErrorAsync(ctx, 500, "internal", ex.Message).ConfigureAwait(false);
        }
        finally
        {
            ctx.Request.PathBase = originalBase;
            ctx.Request.Path = originalPath;
        }
    }

    private async Task WebSocketAsync(HttpContext ctx)
    {
        if (!ctx.WebSockets.IsWebSocketRequest)
        {
            await WriteErrorAsync(ctx, 400, "bad_request", "WebSocket upgrade expected").ConfigureAwait(false);
            return;
        }
        if (!IsAuthorized(ctx))
        {
            await WriteErrorAsync(ctx, 401, "unauthorized", "Missing or invalid token").ConfigureAwait(false);
            return;
        }
        if (!IsOriginAllowed(ctx))
        {
            _log.LogWarning("Rejected WebSocket from origin {Origin}", ctx.Request.Headers.Origin.ToString());
            await WriteErrorAsync(ctx, 403, "forbidden", "Origin not allowed").ConfigureAwait(false);
            return;
        }
        // A desktop app has a handful of clients (the window, a devtools page, a second window). Each one parks two
        // tasks and a queue, so an unbounded number of them is a way to run the machine out of memory rather than a
        // feature: past the cap the upgrade is refused with an error the client can show.
        if (_hub.ClientCount >= WsHub.MaxClients)
        {
            _log.LogWarning("Refused a WebSocket: {Count} clients are already connected", _hub.ClientCount);
            await WriteErrorAsync(ctx, 503, "too_many_clients", $"Too many WebSocket clients (max {WsHub.MaxClients})").ConfigureAwait(false);
            return;
        }
        using var ws = await ctx.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted, _stopping.Token);
        await _hub.RunClientAsync(ws, lifetime.Token).ConfigureAwait(false);
    }

    private async Task LoginAsync(HttpContext ctx)
    {
        if (!Matches(ctx.Request.Query["token"].ToString()))
        {
            if (IsAuthorized(ctx))
            {
                // A stale launch URL (old token) while the browser already holds a valid cookie.
                ctx.Response.Redirect("/");
                return;
            }
            ctx.Response.StatusCode = 401;
            ctx.Response.ContentType = "text/plain; charset=utf-8";
            await ctx.Response.WriteAsync("Invalid NetPI token. Use the launch URL printed by the server.").ConfigureAwait(false);
            return;
        }
        var cookie = new CookieOptions { HttpOnly = true, SameSite = SameSiteMode.Strict, Path = "/", IsEssential = true };
        ctx.Response.Cookies.Append(CookieName, _k.Token, cookie);
        // Cookies are not port-scoped: a per-port copy keeps two NetPI instances on one host from logging each other out.
        ctx.Response.Cookies.Append(CookieName + "_" + ctx.Connection.LocalPort, _k.Token, cookie);
        ctx.Response.Headers.CacheControl = "no-store";
        ctx.Response.Redirect("/");
    }

    // ------------------------------------------------------------------ static files

    private async Task StaticAsync(HttpContext ctx, string path)
    {
        if (!HttpMethods.IsGet(ctx.Request.Method) && !HttpMethods.IsHead(ctx.Request.Method))
        {
            await WriteErrorAsync(ctx, 405, "bad_request", "Method not allowed").ConfigureAwait(false);
            return;
        }
        var root = _k.Paths.WebRoot;
        var rel = path.TrimStart('/');
        if (rel.Length > 0)
        {
            var file = PathUtil.SafeCombine(root, rel);
            if (file is not null && File.Exists(file))
            {
                var immutable = rel.StartsWith("assets/", StringComparison.OrdinalIgnoreCase);
                await ServeFileAsync(ctx, file, immutable ? ImmutableCache : "no-cache").ConfigureAwait(false);
                return;
            }
            if (Path.HasExtension(rel))
            {
                ctx.Response.StatusCode = 404;
                return;
            }
        }
        var index = Path.Combine(root, "index.html");
        if (File.Exists(index))
        {
            await ServeFileAsync(ctx, index, "no-cache").ConfigureAwait(false);
            return;
        }
        ctx.Response.ContentType = "text/html; charset=utf-8";
        ctx.Response.Headers.CacheControl = "no-cache";
        await ctx.Response.WriteAsync(
            "<!doctype html><meta charset=utf-8><title>NetPI</title><body style=\"font-family:sans-serif\">" +
            $"<h1>NetPI {HostInfo.Version}</h1><p>The server is running, but the web UI is not installed (no index.html in <code>{WebUtility.HtmlEncode(root)}</code>).</p>")
            .ConfigureAwait(false);
    }

    private async Task PluginFileAsync(HttpContext ctx, string path)
    {
        var rest = path["/plugins/".Length..];
        var slash = rest.IndexOf('/');
        if (slash <= 0 || (!HttpMethods.IsGet(ctx.Request.Method) && !HttpMethods.IsHead(ctx.Request.Method)))
        {
            ctx.Response.StatusCode = 404;
            return;
        }
        var root = _k.Plugins.GetWebRoot(rest[..slash]);
        var file = root is null ? null : PathUtil.SafeCombine(root, rest[(slash + 1)..]);
        if (file is null || !File.Exists(file))
        {
            ctx.Response.StatusCode = 404;
            return;
        }
        await ServeFileAsync(ctx, file, "no-cache").ConfigureAwait(false);
    }

    private static async Task ServeFileAsync(HttpContext ctx, string file, string cacheControl)
    {
        var info = new FileInfo(file);
        var etag = $"\"{info.Length:x}-{info.LastWriteTimeUtc.Ticks:x}\"";
        var headers = ctx.Response.Headers;
        headers.ETag = etag;
        headers.CacheControl = cacheControl;
        if (Matches(ctx.Request.Headers.IfNoneMatch.ToString(), etag))
        {
            ctx.Response.StatusCode = 304;
            return;
        }
        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = ContentTypes.TryGetContentType(file, out var type) ? type : "application/octet-stream";
        ctx.Response.ContentLength = info.Length;
        if (HttpMethods.IsHead(ctx.Request.Method)) return;
        await ctx.Response.SendFileAsync(file, ctx.RequestAborted).ConfigureAwait(false);
    }

    /// <summary>
    /// Whether an <c>If-None-Match</c> header names this etag. The header is a list of etags, each optionally
    /// <c>W/</c>-prefixed, so the entries are compared whole: a substring match would answer 304 for an etag that
    /// merely contains ours.
    /// </summary>
    private static bool Matches(string header, string etag)
    {
        if (header.Length == 0) return false;
        foreach (var entry in header.Split(','))
        {
            var candidate = entry.Trim();
            if (candidate.StartsWith("W/", StringComparison.Ordinal)) candidate = candidate[2..];
            if (candidate == "*" || string.Equals(candidate, etag, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    private static FileExtensionContentTypeProvider CreateContentTypes()
    {
        var p = new FileExtensionContentTypeProvider();
        p.Mappings[".js"] = "text/javascript; charset=utf-8";
        p.Mappings[".mjs"] = "text/javascript; charset=utf-8";
        p.Mappings[".css"] = "text/css; charset=utf-8";
        p.Mappings[".html"] = "text/html; charset=utf-8";
        p.Mappings[".json"] = "application/json; charset=utf-8";
        p.Mappings[".map"] = "application/json; charset=utf-8";
        p.Mappings[".svg"] = "image/svg+xml";
        p.Mappings[".wasm"] = "application/wasm";
        p.Mappings[".webmanifest"] = "application/manifest+json";
        p.Mappings[".woff2"] = "font/woff2";
        return p;
    }

    // ------------------------------------------------------------------ security

    private bool IsAuthorized(HttpContext ctx)
    {
        var req = ctx.Request;
        return Matches(req.Headers[TokenHeader].ToString())
               || Matches(req.Cookies[CookieName + "_" + ctx.Connection.LocalPort])
               || Matches(req.Cookies[CookieName])
               || Matches(req.Query["token"].ToString());
    }

    private bool Matches(string? candidate)
    {
        if (string.IsNullOrEmpty(candidate)) return false;
        var bytes = Encoding.UTF8.GetBytes(candidate);
        return CryptographicOperations.FixedTimeEquals(bytes, _token);
    }

    /// <summary>No Origin, our own origin (127.0.0.1/localhost on this port) or one of <c>server.devOrigins</c>.</summary>
    private bool IsOriginAllowed(HttpContext ctx)
    {
        var origin = ctx.Request.Headers.Origin.ToString();
        if (string.IsNullOrEmpty(origin)) return true;
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme == Uri.UriSchemeHttp && uri.Port == ctx.Connection.LocalPort &&
            (uri.Host == "127.0.0.1" || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || uri.Host == "[::1]"))
            return true;
        return IsDevOrigin(uri);
    }

    /// <summary>
    /// CORS for <c>server.devOrigins</c> only (e.g. a Vite dev server calling the API directly). Returns true when the
    /// request was a preflight and has been answered.
    /// </summary>
    private bool ApplyCors(HttpContext ctx)
    {
        var origin = ctx.Request.Headers.Origin.ToString();
        if (origin.Length == 0 || !Uri.TryCreate(origin, UriKind.Absolute, out var uri) || !IsDevOrigin(uri)) return false;
        var h = ctx.Response.Headers;
        h.AccessControlAllowOrigin = origin;
        h.AccessControlAllowCredentials = "true";
        h.Vary = "Origin";
        if (!HttpMethods.IsOptions(ctx.Request.Method)) return false;
        h.AccessControlAllowMethods = "GET, POST, PUT, PATCH, DELETE, OPTIONS";
        h.AccessControlAllowHeaders = "content-type, " + TokenHeader.ToLowerInvariant();
        h.AccessControlMaxAge = "600";
        ctx.Response.StatusCode = 204;
        return true;
    }

    private bool IsDevOrigin(Uri uri)
    {
        foreach (var allowed in _k.Settings.Get<List<string>>("server.devOrigins") ?? [])
        {
            if (string.IsNullOrWhiteSpace(allowed)) continue;
            if (allowed.Trim() == "*") return true;
            if (Uri.TryCreate(allowed.Trim(), UriKind.Absolute, out var a) &&
                a.Scheme.Equals(uri.Scheme, StringComparison.OrdinalIgnoreCase) &&
                a.Host.Equals(uri.Host, StringComparison.OrdinalIgnoreCase) && a.Port == uri.Port)
                return true;
        }
        return false;
    }

    // ------------------------------------------------------------------ helpers

    private static Task WriteErrorAsync(HttpContext ctx, int status, string code, string message) =>
        WriteBytesAsync(ctx, status, Wire.ErrorBody(code, message));

    private static async Task WriteBytesAsync(HttpContext ctx, int status, byte[] json)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        ctx.Response.ContentLength = json.Length;
        if (HttpMethods.IsHead(ctx.Request.Method)) return;
        await ctx.Response.Body.WriteAsync(json, ctx.RequestAborted).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stopping.Cancel();
        _hub.Dispose();
        if (_app is not null)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await _app.StopAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) { _log.LogDebug(ex, "Web server stop"); }
            await _app.DisposeAsync().ConfigureAwait(false);
        }
    }
}
