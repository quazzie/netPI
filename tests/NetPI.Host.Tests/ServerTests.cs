using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
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
                    r = await _ws.ReceiveAsync(buffer.AsMemory(count), CancellationToken.None);
                    count += r.Count;
                } while (!r.EndOfMessage);
                if (r.MessageType == WebSocketMessageType.Close) break;
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

            var session = (await Call("sessions.create", new { title = "Chat", projectId = pid, model = "aiproxy/x", reasoning = "low" }))!;
            var sid = session["id"]!.GetValue<string>();
            Check.Equal("chat", session["kind"]!.GetValue<string>());
            Check.Equal(sid, (await Call("sessions.get", new { id = sid }))!["id"]!.GetValue<string>());
            var upd = (await Call("sessions.update", new { id = sid, title = "Chat 2", model = (string?)null, archived = false }))!;
            Check.Equal("Chat 2", upd["title"]!.GetValue<string>());
            Check.True(upd["model"] is null, "null clears the model");
            Check.Equal("low", upd["reasoning"]!.GetValue<string>());
            Check.Equal(1, (await Call("sessions.list", new { projectId = pid }))!.AsArray().Count);
            Check.Equal(1, (await Call("sessions.list", new { search = "chat 2" }))!.AsArray().Count);

            for (var i = 1; i <= 70; i++) server.Sessions.AppendMessage(sid, ChatMessage.UserText("m" + i));
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
            Check.Equal("aiproxy/qwen3.8-27b", models["defaultModel"]!.GetValue<string>());

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
                         "settings.get", "settings.set", "settings.replace", "fs.dirs", "tools.list", "rpc.list", "events.recent", "logs.recent" })
                Check.True(rpcs.Contains(m), "rpc " + m);

            var recent = (await Call("events.recent", new { max = 500 }))!.AsArray();
            Check.True(recent.Any(e => e!["type"]!.GetValue<string>() == "session.created"));
            Check.True((await Call("logs.recent", new { max = 20 }))!.AsArray().Count > 0);

            Check.True((await Call("sessions.delete", new { id = sid }))!.GetValue<bool>());
            Check.True((await Call("projects.delete", new { id = pid }))!.GetValue<bool>());
            Check.Equal(1, (await Call("projects.list"))!.AsArray().Count);
        });

        r.Add("server: a busy port falls back to a random free port; stop is idempotent", async () =>
        {
            using var blocker = new TcpListener(IPAddress.Loopback, 0);
            blocker.Start();
            var busy = ((IPEndPoint)blocker.LocalEndpoint).Port;
            var server = await NetPiServer.StartAsync(new NetPiServerOptions
            {
                Port = busy, Home = T.TempDir("home"), WebRoot = T.TempDir("web"), ConsoleLogging = false, Token = "fixed-token",
            });
            Check.True(server.Port != busy && server.Port > 0, $"port {server.Port}");
            Check.Equal("fixed-token", server.Token);
            using var http = NewHttp();
            Check.Equal(HttpStatusCode.OK, (await SendAsync(http, HttpMethod.Get, server.BaseUrl + "/api/health")).Status);
            await Task.WhenAll(server.StopAsync(), server.StopAsync());
            await server.DisposeAsync();
            await Check.ThrowsAsync<HttpRequestException>(() => http.GetAsync(server.BaseUrl + "/api/health"));
        });
    }
}
