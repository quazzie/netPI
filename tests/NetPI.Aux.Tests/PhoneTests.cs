using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using NetPI.Phone;

namespace NetPI.Aux.Tests;

/// <summary>
/// The phone listener: pairing (a code is good once, for five minutes, and a wrong one is throttled), device tokens (hashed at rest,
/// revoked at once), the RPC allow-list with its per-method parameters, the allow list of addresses, the origin check, the proxy
/// headers, the event stream, and the real listener over a socket.
/// </summary>
public static class PhoneTests
{
    private sealed class Env : IAsyncDisposable
    {
        public SchedulesTests.ManualTime Time { get; } = new(new DateTimeOffset(2026, 10, 17, 8, 0, 0, TimeSpan.Zero));
        public FakePluginContext Ctx { get; }
        public PhonePlugin Plugin { get; }
        public HttpClient Http { get; private set; } = null!;
        public int Port { get; } = FreePort();
        public List<(string Method, JsonObject Args)> Calls { get; } = [];

        public Env(string? allowFrom = null, bool trustProxy = false)
        {
            var home = T.TempDir("phone-home");
            Directory.CreateDirectory(Path.Combine(home, "wwwroot", "phone"));
            File.WriteAllText(Path.Combine(home, "wwwroot", "phone", "index.html"), "<!doctype html><title>phone</title>");
            Ctx = new FakePluginContext(home, "netpi.phone");
            Ctx.SettingsFake.SetQuiet("phone.enabled", true);
            Ctx.SettingsFake.SetQuiet("phone.port", Port);
            if (allowFrom is not null) Ctx.SettingsFake.SetQuiet("phone.allowFrom", new JsonArray(allowFrom));
            if (trustProxy) Ctx.SettingsFake.SetQuiet("phone.trustProxy", true);
            Plugin = new PhonePlugin(Time, listen: true);
            foreach (var m in new[] { "sessions.list", "agent.send", "sessions.create", "guard.answer" })
                Ctx.Rpc.Register(m, (req, _) => { Calls.Add((req.Method, JsonNode.Parse(req.Params.GetRawText())!.AsObject())); return Task.FromResult<object?>(new JsonObject { ["called"] = req.Method }); });
        }

        public async Task<Env> StartAsync()
        {
            await Plugin.StartAsync(Ctx, CancellationToken.None);
            for (var i = 0; i < 100 && !Plugin.Status()["listening"]!.GetValue<bool>() && Plugin.Status()["error"] is null; i++) await Task.Delay(50);
            Check.True(Plugin.Status()["listening"]!.GetValue<bool>(), "listening: " + Plugin.Status()["error"]);
            Http = new HttpClient(new HttpClientHandler { UseCookies = false }) { BaseAddress = new Uri($"http://127.0.0.1:{Port}") };
            return this;
        }

        public async Task<string> NewCode() => ((await Ctx.RpcFake.CallAsync("phone.pair", new { }))!["code"]!.GetValue<string>());

        public Task<HttpResponseMessage> Post(string path, object? body, string? cookie = null, Action<HttpRequestMessage>? tweak = null)
        {
            var req = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(body is null ? "" : System.Text.Json.JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
            if (cookie is not null) req.Headers.Add("Cookie", $"{PhoneApi.Cookie}={cookie}");
            tweak?.Invoke(req);
            return Http.SendAsync(req);
        }

        /// <summary>Pair a device and return its token (the cookie the phone would keep).</summary>
        public async Task<string> Pair(string name = "Test phone")
        {
            var res = await Post("/api/pair", new { code = await NewCode(), name });
            Check.Equal(HttpStatusCode.OK, res.StatusCode, "pairs");
            var setCookie = res.Headers.GetValues("Set-Cookie").Single();
            Check.Contains(setCookie, "httponly");
            Check.Contains(setCookie, "samesite=strict");
            return setCookie.Split(';')[0].Split('=', 2)[1];
        }

        public async ValueTask DisposeAsync()
        {
            Http?.Dispose();
            await Plugin.StopAsync(CancellationToken.None);
        }
    }

    private static string S(JsonNode? n) => n?.GetValue<string>() ?? "";

    public static void Register(TestRunner r)
    {
        r.Add("phone: a code pairs once; a device token is stored hashed and revoking it ends it at once", async () =>
        {
            await using var env = await new Env().StartAsync();
            var res = await env.Post("/api/pair", new { code = "WRONGWRONG12" });
            Check.Equal(HttpStatusCode.Unauthorized, res.StatusCode, "a wrong code");
            Check.Equal(HttpStatusCode.Unauthorized, (await env.Http.GetAsync("/api/me")).StatusCode, "no cookie, no entry");

            var code = await env.NewCode();
            var ok = await env.Post("/api/pair", new { code, name = "Pixel" });
            Check.Equal(HttpStatusCode.OK, ok.StatusCode);
            var token = ok.Headers.GetValues("Set-Cookie").Single().Split(';')[0].Split('=', 2)[1];
            Check.Equal(HttpStatusCode.Unauthorized, (await env.Post("/api/pair", new { code })).StatusCode, "the code is spent");

            var me = await env.Http.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/api/me") { Headers = { { "Cookie", $"{PhoneApi.Cookie}={token}" } } });
            Check.Equal(HttpStatusCode.OK, me.StatusCode, "the token works");
            Check.Equal("Pixel", S(JsonNode.Parse(await me.Content.ReadAsStringAsync())!["device"]!["name"]));

            var devices = (await env.Ctx.RpcFake.CallAsync("phone.devices", new { }))!.AsArray();
            Check.Equal(1, devices.Count);
            Check.True(devices[0]!["tokenHash"] is null, "the hash is not shown");
            Check.False((await env.Ctx.RpcFake.CallAsync("phone.devices", new { }))!.ToJsonString().Contains(token), "the token itself is never stored or shown");

            await env.Ctx.RpcFake.CallAsync("phone.revoke", new { id = S(devices[0]!["id"]) });
            var after = await env.Http.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/api/me") { Headers = { { "Cookie", $"{PhoneApi.Cookie}={token}" } } });
            Check.Equal(HttpStatusCode.Unauthorized, after.StatusCode, "revoked");
        });

        r.Add("phone: a code expires after five minutes", async () =>
        {
            await using var env = await new Env().StartAsync();
            var code = await env.NewCode();
            env.Time.Advance(PhoneAuth.CodeLife + TimeSpan.FromSeconds(1));
            Check.Equal(HttpStatusCode.Unauthorized, (await env.Post("/api/pair", new { code })).StatusCode);
        });

        r.Add("phone: wrong codes are throttled per address, and the proxy's X-Forwarded-For is believed only with phone.trustProxy", async () =>
        {
            await using var env = await new Env().StartAsync();
            for (var i = 0; i < FailureThrottle.Limit; i++) await env.Post("/api/pair", new { code = "NOPE" + i });
            Check.Equal(HttpStatusCode.TooManyRequests, (await env.Post("/api/pair", new { code = "NOPE" })).StatusCode, "blocked after the limit");
            Check.Equal(HttpStatusCode.TooManyRequests, (await env.Post("/api/pair", new { code = "NOPE" }, tweak: q => q.Headers.Add("X-Forwarded-For", "9.9.9.9"))).StatusCode, "the header is ignored without trustProxy");

            await using var proxied = await new Env(trustProxy: true).StartAsync();
            for (var i = 0; i < FailureThrottle.Limit; i++) await proxied.Post("/api/pair", new { code = "NOPE" + i }, tweak: q => q.Headers.Add("X-Forwarded-For", "evil, 203.0.113.7"));
            Check.Equal(HttpStatusCode.TooManyRequests, (await proxied.Post("/api/pair", new { code = "NOPE" }, tweak: q => q.Headers.Add("X-Forwarded-For", "x, 203.0.113.7"))).StatusCode, "that client is blocked (the proxy's last hop counts)");
            Check.Equal(HttpStatusCode.Unauthorized, (await proxied.Post("/api/pair", new { code = "NOPE" }, tweak: q => q.Headers.Add("X-Forwarded-For", "198.51.100.2"))).StatusCode, "another client is not");
        });

        r.Add("phone: the RPC proxy passes only the allow-listed methods and parameters", async () =>
        {
            await using var env = await new Env().StartAsync();
            var token = await env.Pair();
            var ok = await env.Post("/api/rpc/sessions.list", new { limit = 5 }, token);
            Check.Equal(HttpStatusCode.OK, ok.StatusCode);
            Check.Equal("sessions.list", S(JsonNode.Parse(await ok.Content.ReadAsStringAsync())!["result"]!["called"]));
            Check.Equal(5, env.Calls.Single(c => c.Method == "sessions.list").Args["limit"]!.GetValue<int>());

            Check.Equal(HttpStatusCode.Forbidden, (await env.Post("/api/rpc/settings.get", new { }, token)).StatusCode, "settings are not reachable");
            Check.Equal(HttpStatusCode.Forbidden, (await env.Post("/api/rpc/sessions.delete", new { id = "x" }, token)).StatusCode, "nor is delete");
            Check.Equal(HttpStatusCode.Forbidden, (await env.Post("/api/rpc/phone.pair", new { }, token)).StatusCode, "a phone cannot pair another phone");
            Check.Equal(HttpStatusCode.BadRequest, (await env.Post("/api/rpc/sessions.create", new { title = "t", meta = new { cwd = "C:/" } }, token)).StatusCode, "meta is not a parameter from here");
            Check.Equal(HttpStatusCode.BadRequest, (await env.Post("/api/rpc/guard.answer", new { approvalId = "a", allow = true, evil = 1 }, token)).StatusCode);
            Check.Equal(HttpStatusCode.Unauthorized, (await env.Post("/api/rpc/sessions.list", new { })).StatusCode, "no cookie");
            Check.False(env.Calls.Any(c => c.Method is "settings.get" or "sessions.delete" or "phone.pair"), "none of the refused calls reached the host");
        });

        r.Add("phone: only allowed addresses connect, and a cross-origin POST is refused", async () =>
        {
            await using var closed = await new Env(allowFrom: "10.99.0.0/16").StartAsync();
            Check.Equal(HttpStatusCode.Forbidden, (await closed.Http.GetAsync("/")).StatusCode, "127.0.0.1 is not on that list");

            await using var env = await new Env().StartAsync();
            var page = await env.Http.GetAsync("/");
            Check.Equal(HttpStatusCode.OK, page.StatusCode);
            Check.Contains(await page.Content.ReadAsStringAsync(), "<title>phone</title>");
            Check.True(page.Headers.Contains("X-Frame-Options"), "the page cannot be framed");
            Check.Equal(HttpStatusCode.NotFound, (await env.Http.GetAsync("/app.js")).StatusCode, "a file that is not installed");
            Check.Equal(HttpStatusCode.NotFound, (await env.Http.GetAsync("/../settings.json")).StatusCode, "no path outside the page");

            var token = await env.Pair();
            var foreign = await env.Post("/api/rpc/sessions.list", new { }, token, q => q.Headers.Add("Origin", "https://evil.example"));
            Check.Equal(HttpStatusCode.Forbidden, foreign.StatusCode, "another origin");
            var own = await env.Post("/api/rpc/sessions.list", new { }, token, q => q.Headers.Add("Origin", $"http://127.0.0.1:{env.Port}"));
            Check.Equal(HttpStatusCode.OK, own.StatusCode, "its own origin");
        });

        r.Add("phone: the event stream carries a chat's events, announces a stored message without its body, and needs a paired device", async () =>
        {
            await using var env = await new Env().StartAsync();
            Check.Equal(HttpStatusCode.Unauthorized, (await env.Http.GetAsync("/api/events")).StatusCode);
            var token = await env.Pair();
            var req = new HttpRequestMessage(HttpMethod.Get, "/api/events") { Headers = { { "Cookie", $"{PhoneApi.Cookie}={token}" } } };
            using var res = await env.Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
            Check.Equal("text/event-stream", res.Content.Headers.ContentType?.MediaType);
            await using var stream = await res.Content.ReadAsStreamAsync();
            using var reader = new StreamReader(stream);
            for (var i = 0; i < 100 && env.Plugin.Api!.Connections == 0; i++) await Task.Delay(20);

            env.Ctx.Events.Publish("settings.changed", new { path = "x" });   // not relayed
            env.Ctx.Events.Publish("stream.delta", new { sessionId = "s1", kind = "text", text = "hej" }, sessionId: "s1");
            env.Ctx.Events.Publish("message.added", new { sessionId = "s1", message = new { seq = 7, role = "user", parts = new object[] { new { type = "image", data = "AAAA" } } } }, sessionId: "s1");
            var lines = new List<string>();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (lines.Count < 2)
            {
                var l = await reader.ReadLineAsync(cts.Token);
                if (l is not null && l.StartsWith("data: ")) lines.Add(l[6..]);
            }
            var first = JsonNode.Parse(lines[0])!;
            Check.Equal("stream.delta", S(first["type"]), "the unrelated settings event was not relayed");
            Check.Equal("hej", S(first["data"]!["text"]));
            var second = JsonNode.Parse(lines[1])!;
            Check.Equal("message.added", S(second["type"]));
            Check.Equal(7, second["data"]!["seq"]!.GetValue<int>());
            Check.False(lines[1].Contains("AAAA"), "the image does not ride the stream");
        });

        r.Add("phone: the Agents view's snapshot is allowed, and the events that make it re-read carry no body", () =>
        {
            Check.True(PhoneApi.Allowed.ContainsKey("work.snapshot"), "work.snapshot is allowed");
            Check.True(PhoneApi.Allowed["work.snapshot"].Length == 0, "and takes no parameters");
            var frame = PhoneApi.Frame(new BusEvent { Type = "agents.changed", Data = new { agents = new[] { new { key = "x" } } } });
            Check.Equal("agents.changed", S(frame["type"]));
            Check.True(frame["data"] is null, "no agent list rides the stream");
            return Task.CompletedTask;
        });

        r.Add("phone: off until enabled, and the pairing call names the page's address", async () =>
        {
            var ctx = new FakePluginContext(T.TempDir("phone-off"), "netpi.phone");
            var plugin = new PhonePlugin(TimeProvider.System, listen: true);
            await plugin.StartAsync(ctx, CancellationToken.None);
            Check.False(plugin.Status()["listening"]!.GetValue<bool>(), "off by default");
            await plugin.StopAsync(CancellationToken.None);

            await using var env = await new Env().StartAsync();
            env.Ctx.SettingsFake.SetQuiet("phone.publicUrl", "https://phone.example.com/");
            var pair = (await env.Ctx.RpcFake.CallAsync("phone.pair", new { }))!;
            Check.Equal($"https://phone.example.com/#pair={S(pair["code"])}", S(pair["links"]![0]));
        });
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}
