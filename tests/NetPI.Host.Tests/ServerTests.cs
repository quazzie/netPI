using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using NetPI.Host.Web;

namespace NetPI.Host.Tests;

/// <summary>Minimal protocol client for the tests.</summary>
public sealed class WsTestClient : IAsyncDisposable
{
    private readonly ClientWebSocket _ws;
    private readonly Channel<JsonNode> _incoming = Channel.CreateUnbounded<JsonNode>();
    private readonly Task _loop;
    private int _nextId;

    public List<JsonNode> Received { get; } = [];
    public string ClientId { get; private set; } = "";
    /// <summary>How the host closed the socket, when it did (a message over the limit arrives as 1009).</summary>
    public bool Closed { get; private set; }
    public WebSocketCloseStatus? ClosedWith { get; private set; }
    public string CloseDescription { get; private set; } = "";

    private WsTestClient(ClientWebSocket ws)
    {
        _ws = ws;
        _loop = Task.Run(ReceiveLoopAsync);
    }

    public static async Task<WsTestClient> ConnectAsync(NetPiServer server, string? origin = null, bool token = true)
    {
        var ws = new ClientWebSocket();
        if (origin is not null) ws.Options.SetRequestHeader("Origin", origin);
        var url = server.BaseUrl.Replace("http://", "ws://") + "/ws" + (token ? "?token=" + Uri.EscapeDataString(server.Token) : "");
        await ws.ConnectAsync(new Uri(url), CancellationToken.None);
        var client = new WsTestClient(ws);
        var hello = await client.NextAsync(n => n["t"]?.GetValue<string>() == "hello");
        client.ClientId = hello["clientId"]!.GetValue<string>();
        return client;
    }

    private async Task ReceiveLoopAsync()
    {
        var buffer = new byte[1 << 20];
        try
        {
            while (_ws.State == WebSocketState.Open)
            {
                var count = 0;
                ValueWebSocketReceiveResult r;
                do
                {
                    if (count == buffer.Length)
                    {
                        // Grow (like the server's own receive loop): responses can be several megabytes
                        // (a sessions.messages page of 8 MiB is normal).
                        var bigger = new byte[Math.Min(buffer.Length * 2, 64 * 1024 * 1024)];
                        buffer.AsSpan(0, count).CopyTo(bigger);
                        buffer = bigger;
                    }
                    r = await _ws.ReceiveAsync(buffer.AsMemory(count), CancellationToken.None);
                    count += r.Count;
                } while (!r.EndOfMessage);
                if (r.MessageType == WebSocketMessageType.Close)
                {
                    // Answer the close so the handshake completes — that is when the reason the peer sent is readable.
                    try { await _ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, new CancellationTokenSource(2000).Token); }
                    catch { }
                    Closed = true;
                    ClosedWith = _ws.CloseStatus;
                    CloseDescription = _ws.CloseStatusDescription ?? "";
                    break;
                }
                var node = JsonNode.Parse(Encoding.UTF8.GetString(buffer, 0, count))!;
                lock (Received) Received.Add(node);
                _incoming.Writer.TryWrite(node);
            }
        }
        catch { }
        _incoming.Writer.TryComplete();
    }

    public Task SendAsync(object message) =>
        _ws.SendAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message)), WebSocketMessageType.Text, true, CancellationToken.None);

    public async Task<JsonNode> NextAsync(Func<JsonNode, bool> match, int timeoutMs = 5000)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        try
        {
            while (true)
            {
                var n = await _incoming.Reader.ReadAsync(cts.Token);
                if (match(n)) return n;
            }
        }
        catch (OperationCanceledException)
        {
            throw new AssertException("timed out waiting for a websocket message");
        }
    }

    public async Task<JsonNode> RpcAsync(string method, object? p = null)
    {
        var id = Interlocked.Increment(ref _nextId);
        await SendAsync(new { t = "rpc", id, m = method, p });
        return await NextAsync(n => n["t"]?.GetValue<string>() == "res" && n["id"]?.GetValue<int>() == id);
    }

    /// <summary>Round-trip a ping: everything the server queued before it has been received.</summary>
    public async Task SyncAsync()
    {
        await SendAsync(new { t = "ping" });
        await NextAsync(n => n["t"]?.GetValue<string>() == "pong");
    }

    public bool Saw(string type, string? sid = null)
    {
        lock (Received)
            return Received.Any(n => n["t"]?.GetValue<string>() == "ev" && n["type"]?.GetValue<string>() == type &&
                                     (sid is null || n["sid"]?.GetValue<string>() == sid));
    }

    /// <summary>Wait for the host to close the socket; false if it stayed open.</summary>
    public async Task<bool> WaitForCloseAsync(int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (Closed) return true;
            await Task.Delay(25);
        }
        return Closed;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_ws.State == WebSocketState.Open)
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", new CancellationTokenSource(2000).Token);
        }
        catch { }
        _ws.Dispose();
        try { await _loop.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
    }
}

public static class ServerTests
{
    private static string CreateWebRoot()
    {
        var web = T.TempDir("web");
        File.WriteAllText(Path.Combine(web, "index.html"), "<!doctype html><title>NetPI test</title><div id=app>INDEX</div>");
        Directory.CreateDirectory(Path.Combine(web, "assets"));
        File.WriteAllText(Path.Combine(web, "assets", "app-Bx12cD34.js"), "console.log('app')");
        File.WriteAllText(Path.Combine(web, "favicon.svg"), "<svg xmlns='http://www.w3.org/2000/svg'/>");
        return web;
    }

    private static HttpClient NewHttp() => new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });

    private static async Task<(HttpStatusCode Status, string Body, HttpResponseMessage Response)> SendAsync(
        HttpClient http, HttpMethod method, string url, string? body = null, Action<HttpRequestMessage>? configure = null)
    {
        var req = new HttpRequestMessage(method, url);
        if (body is not null) req.Content = new StringContent(body, Encoding.UTF8, "application/json");
        configure?.Invoke(req);
        var res = await http.SendAsync(req);
        return (res.StatusCode, await res.Content.ReadAsStringAsync(), res);
    }

    public static void Register(TestRunner r)
    {
        r.Add("server: /api/health is open; RPC needs the token; login cookie; errors map to codes", async () =>
        {
            await using var server = await PluginTests.StartAsync(T.TempDir("noplugins"), CreateWebRoot());
            Check.True(server.BaseUrl.StartsWith("http://127.0.0.1:"));
            Check.Equal(server.BaseUrl + "/?token=" + Uri.EscapeDataString(server.Token), server.LaunchUrl);
            using var http = NewHttp();
            var url = server.BaseUrl;

            var health = await SendAsync(http, HttpMethod.Get, url + "/api/health");
            Check.Equal(HttpStatusCode.OK, health.Status);
            Check.Contains(health.Body, "\"ok\":true");

            var anon = await SendAsync(http, HttpMethod.Post, url + "/api/rpc/app.info", "{}");
            Check.Equal(HttpStatusCode.Unauthorized, anon.Status);
            Check.Contains(anon.Body, "\"code\":\"unauthorized\"");
            var wrong = await SendAsync(http, HttpMethod.Post, url + "/api/rpc/app.info", "{}", q => q.Headers.Add(WebServer.TokenHeader, "nope"));
            Check.Equal(HttpStatusCode.Unauthorized, wrong.Status);

            var info = await SendAsync(http, HttpMethod.Post, url + "/api/rpc/app.info", "", q => q.Headers.Add(WebServer.TokenHeader, server.Token));
            Check.Equal(HttpStatusCode.OK, info.Status);
            var infoJson = JsonNode.Parse(info.Body)!;
            Check.Equal("0.1.0", infoJson["version"]!.GetValue<string>());
            Check.Equal(server.Paths.Home, infoJson["home"]!.GetValue<string>());
            Check.False(infoJson["desktop"]!.GetValue<bool>());

            var byQuery = await SendAsync(http, HttpMethod.Post, url + "/api/rpc/rpc.list?token=" + Uri.EscapeDataString(server.Token), "{}");
            Check.Equal(HttpStatusCode.OK, byQuery.Status);

            var badLogin = await SendAsync(http, HttpMethod.Get, url + "/?token=wrong");
            Check.Equal(HttpStatusCode.Unauthorized, badLogin.Status);
            var login = await SendAsync(http, HttpMethod.Get, server.LaunchUrl);
            Check.Equal(HttpStatusCode.Redirect, login.Status);
            Check.Equal("/", login.Response.Headers.Location!.OriginalString);
            var cookies = string.Join("\n", login.Response.Headers.GetValues("Set-Cookie"));
            Check.Contains(cookies, "netpi_token=" + server.Token);
            Check.Contains(cookies.ToLowerInvariant(), "httponly");
            Check.Contains(cookies.ToLowerInvariant(), "samesite=strict");
            var withCookie = await SendAsync(http, HttpMethod.Post, url + "/api/rpc/app.info", "{}", q => q.Headers.Add("Cookie", "netpi_token=" + server.Token));
            Check.Equal(HttpStatusCode.OK, withCookie.Status);
            // A stale cookie from another instance does not hide a valid per-port cookie.
            var twoCookies = await SendAsync(http, HttpMethod.Post, url + "/api/rpc/app.info", "{}",
                q => q.Headers.Add("Cookie", $"netpi_token=stale; netpi_token_{server.Port}={server.Token}"));
            Check.Equal(HttpStatusCode.OK, twoCookies.Status);

            void Auth(HttpRequestMessage q) => q.Headers.Add(WebServer.TokenHeader, server.Token);
            var notFound = await SendAsync(http, HttpMethod.Post, url + "/api/rpc/sessions.get", """{"id":"ses_missing"}""", Auth);
            Check.Equal(HttpStatusCode.NotFound, notFound.Status);
            Check.Contains(notFound.Body, "\"code\":\"not_found\"");
            var unknown = await SendAsync(http, HttpMethod.Post, url + "/api/rpc/no.such.method", "{}", Auth);
            Check.Equal(HttpStatusCode.NotFound, unknown.Status);
            var badJson = await SendAsync(http, HttpMethod.Post, url + "/api/rpc/app.info", "{nope", Auth);
            Check.Equal(HttpStatusCode.BadRequest, badJson.Status);
            var missingParam = await SendAsync(http, HttpMethod.Post, url + "/api/rpc/sessions.get", "{}", Auth);
            Check.Equal(HttpStatusCode.BadRequest, missingParam.Status);
            Check.Contains(missingParam.Body, "\"code\":\"bad_request\"");
            var badDir = await SendAsync(http, HttpMethod.Post, url + "/api/rpc/projects.create", JsonSerializer.Serialize(new { name = "x", path = "/definitely/missing/dir" }), Auth);
            Check.Equal(HttpStatusCode.BadRequest, badDir.Status);
            var get = await SendAsync(http, HttpMethod.Get, url + "/api/rpc/app.info", null, Auth);
            Check.Equal(HttpStatusCode.MethodNotAllowed, get.Status);
            var foreign = await SendAsync(http, HttpMethod.Post, url + "/api/rpc/app.info", "{}", q => { Auth(q); q.Headers.Add("Origin", "http://evil.example"); });
            Check.Equal(HttpStatusCode.Forbidden, foreign.Status);
            var own = await SendAsync(http, HttpMethod.Post, url + "/api/rpc/app.info", "{}", q => { Auth(q); q.Headers.Add("Origin", $"http://localhost:{server.Port}"); });
            Check.Equal(HttpStatusCode.OK, own.Status);
        });

        r.Add("server: a plugin route mapped open skips the token and origin checks (its handler checks), a plain one keeps them", async () =>
        {
            await using var server = await PluginTests.StartAsync(T.TempDir("noplugins"), CreateWebRoot());
            using var open = server.Kernel.Http.Map("test.open", "hook", ctx => Microsoft.AspNetCore.Http.HttpResponseWritingExtensions.WriteAsync(ctx.Response, "open " + ctx.Request.Query["key"]), open: true);
            using var plain = server.Kernel.Http.Map("test.open", "private", ctx => Microsoft.AspNetCore.Http.HttpResponseWritingExtensions.WriteAsync(ctx.Response, "private"));
            using var http = NewHttp();
            var url = server.BaseUrl + "/api/p/test.open/";
            // an extension: no token, its own origin
            var hook = await SendAsync(http, HttpMethod.Get, url + "hook?key=k1", null, q => q.Headers.Add("Origin", "chrome-extension://abcdefghijklmnop"));
            Check.Equal(HttpStatusCode.OK, hook.Status);
            Check.Equal("open k1", hook.Body);
            Check.Equal(HttpStatusCode.Unauthorized, (await SendAsync(http, HttpMethod.Get, url + "private")).Status);
            var foreign = await SendAsync(http, HttpMethod.Get, url + "private", null, q => { q.Headers.Add(WebServer.TokenHeader, server.Token); q.Headers.Add("Origin", "chrome-extension://abcdefghijklmnop"); });
            Check.Equal(HttpStatusCode.Forbidden, foreign.Status);
            var own = await SendAsync(http, HttpMethod.Get, url + "private", null, q => q.Headers.Add(WebServer.TokenHeader, server.Token));
            Check.Equal(HttpStatusCode.OK, own.Status);
            Check.Equal("private", own.Body);
            open.Dispose();
            Check.Equal(HttpStatusCode.Unauthorized, (await SendAsync(http, HttpMethod.Get, url + "hook")).Status, "gone with its registration: the plain rules again");
        });

        r.Add("server: static files, SPA fallback, cache headers, ETag", async () =>
        {
            await using var server = await PluginTests.StartAsync(T.TempDir("noplugins"), CreateWebRoot());
            using var http = NewHttp();
            var url = server.BaseUrl;
            var index = await SendAsync(http, HttpMethod.Get, url + "/");
            Check.Equal(HttpStatusCode.OK, index.Status);
            Check.Contains(index.Body, "INDEX");
            Check.Equal("no-cache", index.Response.Headers.CacheControl!.ToString());
            Check.Contains(index.Response.Content.Headers.ContentType!.ToString(), "text/html");

            var spa = await SendAsync(http, HttpMethod.Get, url + "/sessions/ses_123");
            Check.Equal(HttpStatusCode.OK, spa.Status);
            Check.Contains(spa.Body, "INDEX");

            var asset = await SendAsync(http, HttpMethod.Get, url + "/assets/app-Bx12cD34.js");
            Check.Equal(HttpStatusCode.OK, asset.Status);
            Check.Contains(asset.Body, "console.log");
            Check.Contains(asset.Response.Headers.CacheControl!.ToString(), "immutable");
            Check.Contains(asset.Response.Content.Headers.ContentType!.ToString(), "javascript");

            var etag = asset.Response.Headers.ETag!.ToString();
            var cached = await SendAsync(http, HttpMethod.Get, url + "/assets/app-Bx12cD34.js", null, q => q.Headers.TryAddWithoutValidation("If-None-Match", etag));
            Check.Equal(HttpStatusCode.NotModified, cached.Status);

            var svg = await SendAsync(http, HttpMethod.Get, url + "/favicon.svg");
            Check.Equal("image/svg+xml", svg.Response.Content.Headers.ContentType!.MediaType);
            Check.Equal(HttpStatusCode.NotFound, (await SendAsync(http, HttpMethod.Get, url + "/missing.js")).Status);
            Check.Equal(HttpStatusCode.NotFound, (await SendAsync(http, HttpMethod.Get, url + "/api/nothing?token=" + server.Token)).Status);
            Check.Equal(HttpStatusCode.NotFound, (await SendAsync(http, HttpMethod.Get, url + "/plugins/none/ui.js")).Status);
            var traversal = await SendAsync(http, HttpMethod.Get, url + "/assets/..%2F..%2Fsettings.json");
            Check.NotContains(traversal.Body, "defaultModel");
        });

        r.Add("server: every response carries the content security policy, so model-written markdown cannot load or frame anything", async () =>
        {
            await using var server = await PluginTests.StartAsync(T.TempDir("noplugins"), CreateWebRoot());
            using var http = NewHttp();
            void Auth(HttpRequestMessage q) => q.Headers.Add(WebServer.TokenHeader, server.Token);
            var csp = WebServer.ContentSecurityPolicy;
            var calls = new (HttpMethod Method, string Path, Action<HttpRequestMessage>? Configure)[]
            {
                (HttpMethod.Get, "/", null),                          // the page itself
                (HttpMethod.Get, "/assets/app-Bx12cD34.js", null),     // a bundle
                (HttpMethod.Get, "/api/health", null),                 // the open health check
                (HttpMethod.Post, "/api/rpc/app.info", Auth),           // an rpc call
            };
            foreach (var (method, path, configure) in calls)
            {
                var res = await SendAsync(http, method, server.BaseUrl + path, method == HttpMethod.Post ? "{}" : null, configure);
                var headers = res.Response.Headers;
                Check.Equal(csp, headers.GetValues("Content-Security-Policy").Single(), path);
                Check.Equal("no-referrer", headers.GetValues("Referrer-Policy").Single(), path);
                Check.Equal("nosniff", headers.GetValues("X-Content-Type-Options").Single(), path);
            }
            // The parts that keep the model out of the page, not just the string's presence.
            Check.Contains(csp, "default-src 'self'");
            Check.Contains(csp, "object-src 'none'");
            Check.Contains(csp, "base-uri 'none'");
            Check.Contains(csp, "frame-ancestors 'none'");
            Check.NotContains(csp, "script-src 'unsafe-inline'");
        });

        r.Add("server: websocket hello/rpc, session-scoped events reach only subscribed clients, origin check", async () =>
        {
            await using var server = await PluginTests.StartAsync(T.TempDir("noplugins"), CreateWebRoot());
            await using var a = await WsTestClient.ConnectAsync(server);
            await using var b = await WsTestClient.ConnectAsync(server, origin: server.BaseUrl);
            Check.True(a.ClientId.StartsWith("c_") && a.ClientId != b.ClientId);

            var created = await a.RpcAsync("sessions.create", new { title = "WS test" });
            var sid = created["r"]!["id"]!.GetValue<string>();
            var err = await a.RpcAsync("sessions.get", new { id = "ses_nope" });
            Check.Equal("not_found", err["e"]!["code"]!.GetValue<string>());
            Check.True(err["r"] is null);

            await a.SendAsync(new { t = "sub", sessions = new[] { sid } });
            await b.SendAsync(new { t = "sub", sessions = new[] { "ses_other" } });
            await a.SyncAsync();
            await b.SyncAsync();
            Check.False(b.Saw("session.created"), "an empty session is not announced");
            var emptyList = await a.RpcAsync("sessions.list", new { });
            Check.True(emptyList["r"]!.AsArray().All(n => n?["id"]?.GetValue<string>() != sid), "an empty session is not listed");

            server.Sessions.AppendMessage(sid, ChatMessage.UserText("hello over the wire"));
            var ev = await a.NextAsync(n => n["t"]?.GetValue<string>() == "ev" && n["type"]?.GetValue<string>() == "message.added");
            Check.Equal(sid, ev["sid"]!.GetValue<string>());
            Check.Equal("hello over the wire", ev["d"]!["message"]!["parts"]![0]!["text"]!.GetValue<string>());
            Check.Equal("user", ev["d"]!["message"]!["role"]!.GetValue<string>());
            Check.True(ev["seq"]!.GetValue<long>() > 0 && ev["ts"]!.GetValue<long>() > 0);
            // The broadcast session.updated follows message.added on the bus, so once B has it, B would have the scoped one too.
            await b.NextAsync(n => n["type"]?.GetValue<string>() == "session.updated" && n["d"]?["session"]?["id"]?.GetValue<string>() == sid);
            await b.SyncAsync();
            Check.False(b.Saw("message.added"), "session-scoped event must not reach an unsubscribed client");
            Check.True(b.Saw("session.created"), "broadcast events reach everyone");
            var listed = await a.RpcAsync("sessions.list", new { });
            Check.True(listed["r"]!.AsArray().Any(n => n?["id"]?.GetValue<string>() == sid), "listed once its first message materialized it");

            await b.SendAsync(new { t = "sub", sessions = "*" });
            await b.SyncAsync();
            server.Sessions.AppendMessage(sid, ChatMessage.UserText("second"));
            await b.NextAsync(n => n["type"]?.GetValue<string>() == "message.added");

            // Ui = false events are not forwarded.
            server.Events.Publish("internal.only", new { x = 1 }, ui: false);
            server.Events.Publish("visible.event", new { x = 2 });
            await a.NextAsync(n => n["type"]?.GetValue<string>() == "visible.event");
            Check.False(a.Saw("internal.only"));

            await Check.ThrowsAsync<WebSocketException>(() => WsTestClient.ConnectAsync(server, origin: "http://evil.example"));
            await Check.ThrowsAsync<WebSocketException>(() => WsTestClient.ConnectAsync(server, token: false));
            await Check.ThrowsAsync<WebSocketException>(() => WsTestClient.ConnectAsync(server, origin: "http://localhost:5173"));
            server.Settings.Set("server.devOrigins", new JsonArray("http://localhost:5173"));
            await using var dev = await WsTestClient.ConnectAsync(server, origin: "http://localhost:5173");
            Check.True(dev.ClientId.Length > 0);

            // CORS for dev origins only.
            using var http = NewHttp();
            var preflight = await SendAsync(http, HttpMethod.Options, server.BaseUrl + "/api/rpc/app.info", null, q =>
            {
                q.Headers.Add("Origin", "http://localhost:5173");
                q.Headers.Add("Access-Control-Request-Method", "POST");
            });
            Check.Equal(HttpStatusCode.NoContent, preflight.Status);
            Check.Equal("http://localhost:5173", preflight.Response.Headers.GetValues("Access-Control-Allow-Origin").Single());
            Check.Contains(preflight.Response.Headers.GetValues("Access-Control-Allow-Headers").Single(), "x-netpi-token");
            var cors = await SendAsync(http, HttpMethod.Post, server.BaseUrl + "/api/rpc/app.info", "{}", q =>
            {
                q.Headers.Add("Origin", "http://localhost:5173");
                q.Headers.Add(WebServer.TokenHeader, server.Token);
            });
            Check.Equal(HttpStatusCode.OK, cors.Status);
            Check.True(cors.Response.Headers.Contains("Access-Control-Allow-Origin"));
            var foreignPreflight = await SendAsync(http, HttpMethod.Options, server.BaseUrl + "/api/rpc/app.info", null, q => q.Headers.Add("Origin", "http://evil.example"));
            Check.False(foreignPreflight.Response.Headers.Contains("Access-Control-Allow-Origin"));
        });

        r.Add("server: a message over the websocket limit closes the socket and says what the limit is", async () =>
        {
            await using var server = await PluginTests.StartAsync(T.TempDir("noplugins"), CreateWebRoot());
            // The UI reads this and fits its payloads into it (base64 images), so it has to be the real limit.
            var info = await SendAsync(NewHttp(), HttpMethod.Post, server.BaseUrl + "/api/rpc/app.info", "", q => q.Headers.Add(WebServer.TokenHeader, server.Token));
            Check.Equal(WsHub.MaxMessageBytes, JsonNode.Parse(info.Body)!["maxMessageBytes"]!.GetValue<int>());

            await using var ws = await WsTestClient.ConnectAsync(server);
            await ws.SyncAsync();
            // What a 2.5 MB screenshot becomes once base64 is inside the JSON envelope: past the 2 MB limit.
            var huge = new string('x', 2_600_000);
            await ws.SendAsync(new { t = "rpc", id = 99, m = "agent.send", p = new { sessionId = "s", text = "look", images = new[] { new { mediaType = "image/png", data = huge } } } });
            var closed = await ws.WaitForCloseAsync();
            Check.True(closed, "the host closed the socket instead of reading the message");
            Check.Equal(WebSocketCloseStatus.MessageTooBig, ws.ClosedWith);
            // The reason reaches the user as "Connection lost: ..." — it has to name the limit.
            Check.Contains(ws.CloseDescription, "2 MB");
            Check.Contains(ws.CloseDescription, "app.info.maxMessageBytes");
        });

        r.Add("server: core RPC methods (PROTOCOL.md table)", async () =>
        {
            await using var server = await PluginTests.StartAsync(T.TempDir("noplugins"), CreateWebRoot());
            await using var ws = await WsTestClient.ConnectAsync(server);
            async Task<JsonNode?> Call(string m, object? p = null)
            {
                var res = await ws.RpcAsync(m, p);
                if (res["e"] is { } e) throw new AssertException($"{m} failed: {e.ToJsonString()}");
                return res["r"];
            }

            var info = (await Call("app.info"))!;
            foreach (var key in new[] { "version", "os", "home", "appDir", "defaultWorkspace", "desktop" }) Check.True(info[key] is not null, key);

            var dir = T.TempDir("project");
            var project = (await Call("projects.create", new { name = "Proj", path = dir }))!;
            var pid = project["id"]!.GetValue<string>();
            Check.Equal(dir, project["path"]!.GetValue<string>());
            var created = (await Call("projects.create", new { name = "", path = Path.Combine(dir, "sub", "new"), create = true }))!;
            Check.Equal("new", created["name"]!.GetValue<string>());
            Check.True(Directory.Exists(Path.Combine(dir, "sub", "new")));
            Check.Equal("Renamed", (await Call("projects.update", new { id = pid, name = "Renamed" }))!["name"]!.GetValue<string>());
            Check.Equal(2, (await Call("projects.list"))!.AsArray().Count);
            // project meta is merged key by key; null removes a key, and the last one the object
            var withMeta = (await Call("projects.update", new { id = pid, meta = new { profile = "admin", other = 1 } }))!;
            Check.Equal("admin", withMeta["meta"]?["profile"]?.GetValue<string>());
            Check.Equal("Renamed", withMeta["name"]!.GetValue<string>(), "name kept");
            var merged = (await Call("projects.update", new { id = pid, meta = new Dictionary<string, object?> { ["other"] = null } }))!;
            Check.Equal("{\"profile\":\"admin\"}", merged["meta"]!.ToJsonString());
            Check.Equal("admin", (await Call("projects.list"))!.AsArray().First(p => p!["id"]!.GetValue<string>() == pid)!["meta"]?["profile"]?.GetValue<string>());
            var cleared = (await Call("projects.update", new { id = pid, meta = new Dictionary<string, object?> { ["profile"] = null } }))!;
            Check.True(cleared["meta"] is null, "no keys, no meta");

            var session = (await Call("sessions.create", new { title = "Chat", projectId = pid, model = "aiproxy/x", reasoning = "low" }))!;
            var sid = session["id"]!.GetValue<string>();
            Check.Equal("chat", session["kind"]!.GetValue<string>());
            Check.Equal(sid, (await Call("sessions.get", new { id = sid }))!["id"]!.GetValue<string>());
            var upd = (await Call("sessions.update", new { id = sid, title = "Chat 2", model = (string?)null, archived = false }))!;
            Check.Equal("Chat 2", upd["title"]!.GetValue<string>());
            Check.True(upd["model"] is null, "null clears the model");
            Check.Equal("low", upd["reasoning"]!.GetValue<string>());
            // meta merges key by key (a plugin keeps its keys in it), and a null value removes a key
            server.Sessions.UpdateSession(sid, s => s.Meta = new JsonObject { ["plugin"] = "keeps this" });
            var metaSet = (await Call("sessions.update", new { id = sid, meta = new { mine = 1 } }))!;
            Check.Equal("keeps this", metaSet["meta"]?["plugin"]?.GetValue<string>(), "a key the caller did not send survives");
            Check.Equal(1, metaSet["meta"]?["mine"]?.GetValue<int>() ?? 0);
            var metaCut = (await Call("sessions.update", new { id = sid, meta = new Dictionary<string, object?> { ["mine"] = null, ["plugin"] = null } }))!;
            Check.True(metaCut["meta"] is null, "no keys, no meta");
            var refused = false;
            try { await Call("sessions.update", new { id = sid, meta = "wipe" }); }
            catch (AssertException ex) { refused = ex.Message.Contains("bad_request"); }   // Call turns an error reply into an AssertException
            Check.True(refused, "a meta that is not an object is refused (bad_request)");
            Check.Equal(0, (await Call("sessions.list", new { projectId = pid }))!.AsArray().Count, "an empty session is transient: not listed");
            server.Sessions.AppendMessage(sid, ChatMessage.UserText("first"));
            Check.Equal(1, (await Call("sessions.list", new { projectId = pid }))!.AsArray().Count, "its first message materializes it");
            Check.Equal(1, (await Call("sessions.list", new { search = "chat 2" }))!.AsArray().Count);

            for (var i = 2; i <= 70; i++) server.Sessions.AppendMessage(sid, ChatMessage.UserText("m" + i));
            var page = (await Call("sessions.messages", new { id = sid }))!;
            Check.Equal(60, page["messages"]!.AsArray().Count);
            Check.True(page["hasMore"]!.GetValue<bool>());
            Check.Equal(11L, page["messages"]![0]!["seq"]!.GetValue<long>());
            var older = (await Call("sessions.messages", new { id = sid, beforeSeq = 11, limit = 60 }))!;
            Check.Equal(10, older["messages"]!.AsArray().Count);
            Check.False(older["hasMore"]!.GetValue<bool>());

            var lastBefore = (await Call("sessions.messages", new { id = sid, limit = 1 }))!["messages"]![0]!["seq"]!.GetValue<long>();
            var moved = (await Call("sessions.setProject", new { id = sid, projectId = (string?)null }))!;
            Check.True(moved["projectId"] is null);
            var lastAfter = (await Call("sessions.messages", new { id = sid, limit = 1 }))!["messages"]![0]!["seq"]!.GetValue<long>();
            Check.Equal(lastBefore, lastAfter, "the host appends nothing (plugins announce the switch)");

            var models = (await Call("models.list", new { refresh = true }))!;
            Check.True(models["models"] is JsonArray);
            Check.True(models["defaultModel"] is null, "no default model: none is set and no provider lists one");

            Check.True((await Call("ui.tabs")) is JsonArray);
            Check.True((await Call("ui.commands")) is JsonArray);
            Check.True((await Call("ui.state.get", new { key = "layout" })) is null);
            Check.True((await Call("ui.state.set", new { key = "layout", value = new { left = 240, open = new[] { "a" } } }))!.GetValue<bool>());
            Check.Equal(240, (await Call("ui.state.get", new { key = "layout" }))!["left"]!.GetValue<int>());

            Check.True((await Call("plugins.list")) is JsonArray);
            Check.True((await Call("plugins.rescan"))!.GetValue<bool>());
            var reload = await ws.RpcAsync("plugins.reload", new { id = "missing.plugin" });
            Check.Equal("not_found", reload["e"]!["code"]!.GetValue<string>());

            var settings = (await Call("settings.get"))!;
            Check.Equal(server.Paths.SettingsFile, settings["path"]!.GetValue<string>());
            Check.Equal(7431, settings["settings"]!["server"]!["port"]!.GetValue<int>());
            await Call("settings.set", new { path = "ui.theme", value = "dark" });
            Check.Equal("dark", server.Settings.Get<string>("ui.theme"));
            var doc = settings["settings"]!.AsObject();
            doc["ui"] = new JsonObject { ["theme"] = "light" };
            await Call("settings.replace", new { settings = doc });
            Check.Equal("light", server.Settings.Get<string>("ui.theme"));
            await ws.NextAsync(n => n["type"]?.GetValue<string>() == "settings.changed");

            var fs = (await Call("fs.dirs", new { path = dir }))!;
            Check.Equal(dir, fs["path"]!.GetValue<string>());
            Check.Equal("sub", fs["dirs"]![0]!["name"]!.GetValue<string>());
            Check.True(fs["roots"]!.AsArray().Count >= 1);
            Check.True(fs["parent"] is not null);
            Directory.CreateDirectory(Path.Combine(dir, ".hidden"));
            Check.Equal(1, (await Call("fs.dirs", new { path = dir }))!["dirs"]!.AsArray().Count, "hidden folders are skipped");
            var home = (await Call("fs.dirs"))!;
            Check.Equal(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), home["path"]!.GetValue<string>());

            Check.True((await Call("tools.list")) is JsonArray);
            var rpcs = (await Call("rpc.list"))!.AsArray().Select(n => n!["method"]!.GetValue<string>()).ToHashSet();
            foreach (var m in new[] { "app.info", "projects.list", "projects.create", "projects.update", "projects.delete", "sessions.list", "sessions.create",
                         "sessions.get", "sessions.update", "sessions.delete", "sessions.setProject", "sessions.messages", "models.list", "ui.tabs",
                         "ui.commands", "ui.state.get", "ui.state.set", "plugins.list", "plugins.reload", "plugins.setEnabled", "plugins.rescan",
                         "settings.get", "settings.set", "settings.replace", "settings.schema", "fs.dirs", "tools.list", "rpc.list", "events.recent", "events.flush", "logs.recent" })
                Check.True(rpcs.Contains(m), "rpc " + m);

            // settings.schema: the host's section first, plugins' sections while they are registered
            static bool Has(JsonArray schema, string id) => schema.Any(s => s!["id"]!.GetValue<string>() == id);
            var schema = (await Call("settings.schema"))!.AsArray();
            Check.Equal("core", schema[0]!["id"]!.GetValue<string>());
            var model = schema[0]!["settings"]!.AsArray().Single(s => s!["key"]!.GetValue<string>() == "defaultModel")!;
            Check.Equal("model", model["type"]!.GetValue<string>());
            using (server.Services.Register(new SettingsSection
                   {
                       Id = "sample", Title = "Sample", Group = "Tools", Settings = [SettingInfo.Int("sample.count", "Count", 3, null, 1, 9, "items")],
                   }))
            {
                schema = (await Call("settings.schema"))!.AsArray();
                var sample = schema.Single(s => s!["id"]!.GetValue<string>() == "sample")!["settings"]![0]!;
                Check.Equal("int", sample["type"]!.GetValue<string>());
                Check.Equal(3, sample["default"]!.GetValue<int>());
                Check.Equal("items", sample["unit"]!.GetValue<string>());
            }
            Check.False(Has((await Call("settings.schema"))!.AsArray(), "sample"), "gone with its registration");

            var recent = (await Call("events.recent", new { max = 500 }))!.AsArray();
            Check.True(recent.Any(e => e!["type"]!.GetValue<string>() == "session.created"));
            Check.True((await Call("logs.recent", new { max = 20 }))!.AsArray().Count > 0);

            Check.True((await Call("sessions.delete", new { id = sid }))!.GetValue<bool>());
            Check.True((await Call("projects.delete", new { id = pid }))!.GetValue<bool>());
            Check.Equal(1, (await Call("projects.list"))!.AsArray().Count);
        });

        r.Add("server: events.flush answers only after every earlier event was delivered, ahead of it on the same socket", async () =>
        {
            await using var server = await PluginTests.StartAsync(T.TempDir("noplugins"), CreateWebRoot());
            await using var ws = await WsTestClient.ConnectAsync(server);
            // a window that listens to everything, and a burst of events published just before the call
            await ws.SendAsync(new { t = "sub", sessions = "*" });
            // a slow subscriber keeps the bus busy for about half a second, so an answer that did not wait for it would come first
            using var slow = server.Events.Subscribe("e2e.burst", _ => Thread.Sleep(5));
            const int burst = 100;
            for (var i = 0; i < burst; i++) server.Events.Publish("e2e.burst", new { i });
            var res = await ws.RpcAsync("events.flush");
            Check.True(res["e"] is null && res["r"]!.GetValue<bool>(), "flush answered");
            int seen;
            lock (ws.Received) seen = ws.Received.Count(n => n["t"]?.GetValue<string>() == "ev" && n["type"]?.GetValue<string>() == "e2e.burst");
            Check.Equal(burst, seen, "every event published before the call had reached the client when the answer did");
        });

        r.Add("server: a busy port falls back to a random free port; server.json says where; stop is idempotent", async () =>
        {
            using var blocker = new TcpListener(IPAddress.Loopback, 0);
            blocker.Start();
            var busy = ((IPEndPoint)blocker.LocalEndpoint).Port;
            var home = T.TempDir("home");
            var server = await NetPiServer.StartAsync(new NetPiServerOptions
            {
                Port = busy, Home = home, WebRoot = T.TempDir("web"), ConsoleLogging = false, Token = "fixed-token",
            });
            Check.True(server.Port != busy && server.Port > 0, $"port {server.Port}");
            Check.Equal("fixed-token", server.Token);
            // tools outside find this run through <home>/server.json
            var file = Path.Combine(home, "server.json");
            var info = JsonNode.Parse(File.ReadAllText(file))!;
            Check.Equal(server.BaseUrl, (string?)info["url"]);
            Check.Equal("fixed-token", (string?)info["token"]);
            Check.Equal(Environment.ProcessId, (int)info["pid"]!);
            using var http = NewHttp();
            Check.Equal(HttpStatusCode.OK, (await SendAsync(http, HttpMethod.Get, server.BaseUrl + "/api/health")).Status);
            await Task.WhenAll(server.StopAsync(), server.StopAsync());
            await server.DisposeAsync();
            await Check.ThrowsAsync<HttpRequestException>(() => http.GetAsync(server.BaseUrl + "/api/health"));
            Check.False(File.Exists(file), "server.json is removed when the server stops");
        });

        r.Add("server: one NetPI per home: a second one on the same home refuses to start", async () =>
        {
            var home = T.TempDir("home");
            NetPiServerOptions Options() => new() { Port = 0, Home = home, WebRoot = T.TempDir("web"), ConsoleLogging = false };
            var first = await NetPiServer.StartAsync(Options());
            var refused = await Check.ThrowsAsync<InvalidOperationException>(() => NetPiServer.StartAsync(Options()));
            Check.Contains(refused.Message, $"NetPI is already running with the home {home} (pid {Environment.ProcessId}, {first.BaseUrl})");
            Check.Contains(refused.Message, "--home <dir> or NETPI_HOME");
            Check.Equal(first.BaseUrl, (string?)JsonNode.Parse(File.ReadAllText(Path.Combine(home, "server.json")))!["url"], "the running one keeps its server.json");
            await first.StopAsync();
            // the home is free again
            var second = await NetPiServer.StartAsync(Options());
            await second.StopAsync();
        });

        r.Add("ws: a response over the client backlog cap is answered too_large, not a dead socket", async () =>
        {
            await using var server = await PluginTests.StartAsync(T.TempDir("bigmsg"), CreateWebRoot());
            var s = server.Sessions.CreateSession(new SessionInfo());
            // One message by itself over the 32 MiB a client can take in a single WebSocket message.
            server.Sessions.AppendMessage(s.Id, ChatMessage.UserText(new string('x', 33 * 1024 * 1024)));

            await using var ws = await WsTestClient.ConnectAsync(server);
            var res = await ws.RpcAsync("sessions.messages", new { id = s.Id });
            Check.Equal("too_large", res["e"]?["code"]?.GetValue<string>(), "the oversize answer is an error envelope: " + res.ToJsonString());

            // The socket is still with us: a normal call round-trips.
            var info = await ws.RpcAsync("app.info", null);
            Check.True(info["r"] is not null, "the socket survived the too_large answer");
        });

        r.Add("sessions.messages: a page over its byte budget comes back shorter with hasMore, and the walk covers it all", async () =>
        {
            await using var server = await PluginTests.StartAsync(T.TempDir("pagebytes"), CreateWebRoot());
            var s = server.Sessions.CreateSession(new SessionInfo());
            const int mb = 1024 * 1024;
            for (var i = 0; i < 10; i++)
                server.Sessions.AppendMessage(s.Id, ChatMessage.UserText(new string('a', mb)));

            await using var ws = await WsTestClient.ConnectAsync(server);
            var res = await ws.RpcAsync("sessions.messages", new { id = s.Id, limit = 2000 });
            var messages = (JsonArray)res["r"]!["messages"]!;
            Check.True(messages.Count is > 0 and < 10, $"the byte budget splits the page ({messages.Count} messages)");
            var hasMore = (bool)res["r"]!["hasMore"]!;
            Check.True(hasMore, "hasMore while the page is split");

            var seen = new List<long>();
            var guard = 0;
            while (true)
            {
                Check.True(++guard < 20, "the walk must end");
                foreach (var m in messages) seen.Add((long)m!["seq"]!.GetValue<long>());
                if (!hasMore) break;
                // The page keeps the newest; the next call pages back from its oldest seq, like the UI's load-earlier.
                var beforeSeq = (long)messages[0]!["seq"]!.GetValue<long>();
                res = await ws.RpcAsync("sessions.messages", new { id = s.Id, limit = 2000, beforeSeq });
                messages = (JsonArray)res["r"]!["messages"]!;
                hasMore = (bool)res["r"]!["hasMore"]!;
            }
            Check.Equal(10, seen.Count, "the walk covers every message exactly once");
            Check.True(seen.OrderBy(x => x).SequenceEqual([1, 2, 3, 4, 5, 6, 7, 8, 9, 10]), "the walk covers every message exactly once: " + string.Join(",", seen.OrderBy(x => x)));
        });

        r.Add("settings.replace: a stale base is a conflict, not a lost update", async () =>
        {
            await using var server = await PluginTests.StartAsync(T.TempDir("settingsconflict"), CreateWebRoot());
            using var http = NewHttp();
            Action<HttpRequestMessage> Auth() => m => m.Headers.Add(WebServer.TokenHeader, server.Token);
            string Url(string method) => server.BaseUrl + "/api/rpc/" + method;
            string ReplaceBody(JsonNode settings, JsonNode? baseDoc)
            {
                // Cloned: a node keeps one parent, and the loaded document already has one.
                var o = new JsonObject { ["settings"] = settings.DeepClone() };
                if (baseDoc is not null) o["base"] = baseDoc.DeepClone();
                return o.ToJsonString();
            }

            var getRes = await SendAsync(http, HttpMethod.Post, Url("settings.get"), "{}", Auth());
            Check.Equal(HttpStatusCode.OK, getRes.Status);
            var baseDoc = JsonNode.Parse(getRes.Body)!["settings"]!.DeepClone();

            // Someone else changes a key (another tab, a plugin, settings.set).
            var setRes = await SendAsync(http, HttpMethod.Post, Url("settings.set"), """{"path":"server.port","value":7432}""", Auth());
            Check.Equal(HttpStatusCode.OK, setRes.Status);

            // The raw editor saves the document it loaded, with that base: a conflict, and the other change survives.
            var stale = await SendAsync(http, HttpMethod.Post, Url("settings.replace"), ReplaceBody(baseDoc, baseDoc), Auth());
            Check.Equal(HttpStatusCode.Conflict, stale.Status);
            Check.Contains(stale.Body, "conflict");
            var get2 = await SendAsync(http, HttpMethod.Post, Url("settings.get"), "{}", Auth());
            Check.Equal(7432L, JsonNode.Parse(get2.Body)!["settings"]!["server"]!["port"]!.GetValue<long>(), "the other change is not clobbered");

            // A fresh base goes through.
            var fresh = JsonNode.Parse(get2.Body)!["settings"]!.DeepClone();
            var ok = await SendAsync(http, HttpMethod.Post, Url("settings.replace"), ReplaceBody(fresh, fresh), Auth());
            Check.Equal(HttpStatusCode.OK, ok.Status);

            // Without base the method stays permissive (other callers have not learned the protocol yet).
            var noBase = await SendAsync(http, HttpMethod.Post, Url("settings.replace"), ReplaceBody(fresh, null), Auth());
            Check.Equal(HttpStatusCode.OK, noBase.Status);
        });

        r.Add("ws: one oversized message on an empty queue is not an abort; the cap sheds a backlog", () =>
        {
            var client = new WsClient(new ClientWebSocket(), null!, NullLogger.Instance);
            var big = new byte[25 * 1024 * 1024];
            client.Enqueue(big);   // under the 32 MiB backlog cap, and the queue was empty
            Check.False(client.Aborted, "an empty queue takes one message however big");
            Check.Equal((1, (long)big.Length), client.Backlog);
            client.Enqueue(new byte[1024]);
            Check.False(client.Aborted);
            client.Enqueue(big);   // now something is already queued, and the backlog goes over the cap
            Check.True(client.Aborted, "a backlog over the cap cuts the client off");
        });

        r.Add("wire: a data error is an internal error; the 4xx mapping is unchanged but logged", () =>
        {
            var (c1, _, s1) = Wire.MapError(new InvalidOperationException("SQL has parameters but no arguments were given"), NullLogger.Instance, "sessions.messages");
            Check.Equal(("internal", 500), (c1, s1), "a programmer error is a host failure, not the caller's fault");
            var (c2, m2, s2) = Wire.MapError(new InvalidDataException("Stored message 7 is corrupted and cannot be read"), NullLogger.Instance, "sessions.messages");
            Check.Equal(("internal", 500), (c2, s2), "a corrupted stored row is a data error");
            Check.Contains(m2, "corrupted");
            Check.Contains(m2, "7");
            var (c3, _, s3) = Wire.MapError(new ArgumentException("bad"), NullLogger.Instance, "x");
            Check.Equal(("bad_request", 400), (c3, s3), "the mapping itself is unchanged");
            var (c4, m4, s4) = Wire.MapError(new JsonException("bad"), NullLogger.Instance, "x");
            Check.Equal(("bad_request", 400), (c4, s4));
            Check.Contains(m4, "Invalid parameters");
        });
    }
}
