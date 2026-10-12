using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NetPI.Webhooks;

namespace NetPI.Aux.Tests;

/// <summary>
/// Inbound webhooks: the token (shown once, stored hashed), the receiver's rules (allow list, wrong tokens, size, throttle), the
/// payload → prompt template and match conditions, dedupe and the hourly/concurrent caps, the agent choices (a key, any, free, a
/// list), skips that leave no chat behind, proposals that wait for the user, the tool's limits, and the real listener over a socket.
/// </summary>
public static class WebhooksTests
{
    internal sealed class Env
    {
        public SchedulesTests.ManualTime Time { get; } = new(new DateTimeOffset(2026, 10, 13, 8, 0, 0, TimeSpan.Zero));
        public FakePluginContext Ctx { get; }
        public SchedulesTests.RecordingRuntime Runtime { get; } = new();
        public WebhooksPlugin Plugin { get; private set; } = null!;
        public ProjectInfo Project { get; }
        private readonly bool _listen;

        public Env(bool listen = false)
        {
            _listen = listen;
            var home = T.TempDir("webhooks-home");
            Ctx = new FakePluginContext(home, "netpi.webhooks");
            Project = Ctx.SessionsFake.CreateProject("homelab", home);
            Ctx.ServicesFake.Register<IAgentRuntime>(Runtime);
        }

        public async Task<Env> StartAsync()
        {
            Plugin = new WebhooksPlugin(Time, _listen);
            await Plugin.StartAsync(Ctx, CancellationToken.None);
            return this;
        }

        public async Task<JsonObject> Add(object? extra = null, string prompt = "Alert: {{service}} on {{host}} is {{state}}. {{output}}")
        {
            var p = new JsonObject { ["name"] = "checkmk", ["prompt"] = prompt, ["projectId"] = Project.Id, ["maxPerHour"] = 500, ["maxConcurrent"] = 50 };
            if (extra is not null) foreach (var kv in NetPiJson.ToNode(extra)!.AsObject()) p[kv.Key] = kv.Value?.DeepClone();
            return (await Ctx.RpcFake.CallAsync("webhooks.add", p))!.AsObject();
        }

        public async Task<JsonObject> Get(string id) => (await Ctx.RpcFake.CallAsync("webhooks.get", new { id }))!.AsObject();
        public async Task<JsonArray> History(string id) => (await Ctx.RpcFake.CallAsync("webhooks.history", new { id }))!.AsArray();

        public void Agents(params (string Key, bool Available, bool Free)[] agents)
        {
            var list = new JsonArray([.. agents.Select(a => (JsonNode)new JsonObject { ["key"] = a.Key, ["available"] = a.Available, ["free"] = a.Free })]);
            Ctx.Rpc.Register("agents.list", (_, _) => Task.FromResult<object?>(list.DeepClone()));
            Ctx.Rpc.Register("agents.use", (_, _) => Task.FromResult<object?>(null));
        }

        /// <summary>Deliver a payload the way the receiver does, from an allowed address.</summary>
        public Task<(int Status, JsonObject Body)> Post(string id, string token, object payload, string from = "127.0.0.1") =>
            Plugin.ReceiveAsync("/hook/" + id, "POST", IPAddress.Parse(from), "application/json",
                max => Task.FromResult<string?>(JsonSerializer.Serialize(payload)), token, CancellationToken.None);
    }

    private static string? S(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static object Crit(string service = "Kernel errors", string host = "nuc", string state = "CRIT", string output = "1 in last 30 min") =>
        new { host, service, state, output };

    public static void Register(TestRunner r)
    {
        r.Add("webhooks: the token is shown once, only its hash is kept, and a wrong or missing one is refused", async () =>
        {
            var env = await new Env().StartAsync();
            var added = await env.Add();
            var id = S(added["id"])!;
            var token = S(added["token"])!;
            Check.True(token.StartsWith("whk_") && token.Length > 40, "a long random token");
            Check.Equal("/hook/" + id, S(added["path"]));
            Check.True(added["tokenHash"] is null, "the hash is not shown");
            var got = await env.Get(id);
            Check.True(got["token"] is null && got["tokenHash"] is null, "reading it back shows neither");
            Check.Equal(token[^4..], S(got["tokenHint"]));

            Check.Equal(401, (await env.Post(id, "whk_wrong", Crit())).Status);
            Check.Equal(401, (await env.Post(id, "", Crit())).Status);
            Check.Equal(401, (await env.Post("whk_nothere", token, Crit())).Status, "an unknown webhook answers like a wrong token");
            Check.Equal(0, env.Ctx.SessionsFake.Sessions.Count, "refused requests start nothing");
            Check.Equal(202, (await env.Post(id, token, Crit())).Status);

            var rotated = (await env.Ctx.RpcFake.CallAsync("webhooks.rotateToken", new { id }))!;
            Check.Differs(token, S(rotated["token"])!);
            Check.Equal(401, (await env.Post(id, token, Crit("Other"))).Status, "the old token stopped working");
            Check.Equal(202, (await env.Post(id, S(rotated["token"])!, Crit("Other"))).Status);
        });

        r.Add("webhooks: a payload becomes a chat on the project with the filled-in prompt, sent as the system", async () =>
        {
            var env = await new Env().StartAsync();
            var added = await env.Add(extra: new { title = "{{host}} / {{service}} is {{state}}" });
            var (status, body) = await env.Post(S(added["id"])!, S(added["token"])!, Crit(output: "disk \"/\" is 91 %"));
            Check.Equal(202, status);
            Check.Equal("started", S(body["status"]));
            var session = env.Ctx.SessionsFake.Sessions.Single();
            Check.Equal(env.Project.Id, session.ProjectId);
            Check.Equal("Webhook: nuc / Kernel errors is CRIT", session.Title);
            Check.Equal(S(added["id"]), session.Meta?["webhook"]?.GetValue<string>());
            var sent = env.Runtime.Sent.Single();
            Check.Equal("system", sent.Input.Source);
            Check.Equal("Alert: Kernel errors on nuc is CRIT. disk \"/\" is 91 %", sent.Input.Text);
            var runs = await env.History(S(added["id"])!);
            Check.Equal("started", S(runs[0]!["status"]));
            Check.Equal(S(body["sessionId"]), S(runs[0]!["sessionId"]));
            Check.Equal("127.0.0.1", S(runs[0]!["from"]));
        });

        r.Add("webhooks: templates read nested fields and indexes, a missing field is empty, {{payload}} is the whole JSON, plain text still works", async () =>
        {
            var env = await new Env().StartAsync();
            var added = await env.Add(prompt: "a={{svc.name}} b={{list.1.v}} c=[{{nothing.here}}] d={{Svc.Name}}\n{{payload}}");
            await env.Post(S(added["id"])!, S(added["token"])!, new { svc = new { name = "disk" }, list = new[] { new { v = 1 }, new { v = 2 } } });
            var text = env.Runtime.Sent.Single().Input.Text;
            Check.Contains(text, "a=disk b=2 c=[] d=disk", "paths, indexes, a missing value and case-insensitive names");
            Check.Contains(text, "\"list\"", "{{payload}} carries the document");

            var raw = await env.Plugin.ReceiveAsync("/hook/" + S(added["id"]), "POST", IPAddress.Loopback, "text/plain",
                _ => Task.FromResult<string?>("just some text"), S(added["token"]), CancellationToken.None);
            Check.Equal(202, raw.Status);
            Check.Contains(env.Runtime.Sent.Last().Input.Text, "just some text", "a non-JSON body arrives as { body }");
        });

        r.Add("webhooks: match conditions filter a payload before anything starts (recoveries, other hosts)", async () =>
        {
            var env = await new Env().StartAsync();
            var match = new object[]
            {
                new { path = "state", @in = new[] { "CRIT", "DOWN", "UNKNOWN" } },
                new { path = "host", notIn = new[] { "testhost" } },
                new { path = "service", matches = "^(Kernel|Container)" },
            };
            var added = await env.Add(extra: new { match });
            var id = S(added["id"])!; var token = S(added["token"])!;

            var ok = await env.Post(id, token, Crit());
            Check.Equal("started", S(ok.Body["status"]));
            var recovered = await env.Post(id, token, Crit(state: "OK"));
            Check.Equal(200, recovered.Status);
            Check.Equal("skipped", S(recovered.Body["status"]));
            Check.Contains(S(recovered.Body["reason"]), "filtered: state is “OK”");
            Check.Contains(S((await env.Post(id, token, Crit(host: "testhost"))).Body["reason"]), "excluded");
            Check.Contains(S((await env.Post(id, token, Crit(service: "Fail2ban sshd"))).Body["reason"]), "does not match");
            Check.Equal(1, env.Ctx.SessionsFake.Sessions.Count, "three skips left no chat");
            await Check.ThrowsAsync<RpcException>(() => env.Add(extra: new { match = new[] { new { path = "x", matches = "(" } } }));
            await Check.ThrowsAsync<RpcException>(() => env.Add(extra: new { match = new[] { new { path = "x" } } }));
        });

        r.Add("webhooks: dedupe skips the same alert inside its window, a different key or a later time starts again", async () =>
        {
            var env = await new Env().StartAsync();
            var added = await env.Add(extra: new { dedupe = new { key = "{{host}}/{{service}}", minutes = 30 } });
            var id = S(added["id"])!; var token = S(added["token"])!;
            Check.Equal("started", S((await env.Post(id, token, Crit())).Body["status"]));
            env.Time.Advance(TimeSpan.FromMinutes(10));
            var dup = await env.Post(id, token, Crit(output: "still happening"));
            Check.Equal("skipped", S(dup.Body["status"]));
            Check.Contains(S(dup.Body["reason"]), "duplicate of “nuc/Kernel errors”");
            Check.Equal("started", S((await env.Post(id, token, Crit(service: "Journal errors"))).Body["status"]), "another service is another alert");
            Check.Equal("started", S((await env.Post(id, token, Crit(host: "server"))).Body["status"]), "another host too");
            env.Time.Advance(TimeSpan.FromMinutes(25));
            Check.Equal("started", S((await env.Post(id, token, Crit())).Body["status"]), "the window passed (35 min)");
            Check.Equal(4, env.Ctx.SessionsFake.Sessions.Count);

            var off = await env.Add(extra: new { name = "nodedupe", dedupe = new { key = "{{host}}", minutes = 0 } });
            await env.Post(S(off["id"])!, S(off["token"])!, Crit());
            Check.Equal("started", S((await env.Post(S(off["id"])!, S(off["token"])!, Crit())).Body["status"]), "minutes 0 turns it off");
        });

        r.Add("webhooks: the hourly cap and the concurrent cap skip with the reason, and finished chats free the second", async () =>
        {
            var env = await new Env().StartAsync();
            var added = await env.Add(extra: new { maxPerHour = 3, maxConcurrent = 2 });
            var id = S(added["id"])!; var token = S(added["token"])!;
            Check.Equal("started", S((await env.Post(id, token, Crit("a"))).Body["status"]));
            Check.Equal("started", S((await env.Post(id, token, Crit("b"))).Body["status"]));
            var busy = await env.Post(id, token, Crit("c"));
            Check.Contains(S(busy.Body["reason"]), "still going");
            foreach (var a in env.Runtime.Agents) a.Status = AgentStatus.Completed;
            Check.Equal("started", S((await env.Post(id, token, Crit("c"))).Body["status"]));
            var capped = await env.Post(id, token, Crit("d"));
            Check.Contains(S(capped.Body["reason"]), "maxPerHour");
            env.Time.Advance(TimeSpan.FromMinutes(61));
            Check.Equal("started", S((await env.Post(id, token, Crit("d"))).Body["status"]), "an hour later the cap has room");
            Check.Equal(4, env.Ctx.SessionsFake.Sessions.Count);
        });

        r.Add("webhooks: agent choices — a key, any, free, a preference list — and the budget", async () =>
        {
            var env = await new Env().StartAsync();
            env.Agents(("local", false, true), ("paid", true, false), ("home", true, true));
            var budget = true;
            env.Ctx.Rpc.Register("budget.status", (_, _) => Task.FromResult<object?>(new JsonObject { ["exhausted"] = budget }));
            var used = new List<string?>();
            env.Ctx.Rpc.Register("agents.use", (req, _) => { used.Add(req.Str("agent")); return Task.FromResult<object?>(null); });

            async Task<JsonObject> Fire(string agent, string service)
            {
                var h = await env.Add(extra: new { name = "w-" + agent + service, agent });
                return (await env.Post(S(h["id"])!, S(h["token"])!, Crit(service))).Body;
            }

            Check.Contains(S((await Fire("local", "s1"))["reason"]), "unavailable");
            Check.Contains(S((await Fire("paid", "s2"))["reason"]), "budget", "a paid agent on a spent budget");
            Check.Equal("started", S((await Fire("home", "s3"))["status"]));
            Check.Equal("started", S((await Fire("any", "s4"))["status"]));
            Check.Equal("started", S((await Fire("free", "s5"))["status"]));
            Check.Equal("started", S((await Fire("local, paid ,home", "s6"))["status"]));
            Check.Equal("home,any,home,home", string.Join(",", used), "home; any; the first free one; the list skips the paid agent while the budget is spent");
            budget = false;
            Check.Equal("started", S((await Fire("paid", "s7"))["status"]), "budget back: the paid agent runs");

            env.Agents(("local", false, true), ("paid", true, false));
            Check.Contains(S((await Fire("free", "s8"))["reason"]), "no free agent");
            await Check.ThrowsAsync<RpcException>(() => env.Add(extra: new { agent = "nobody" }));
            await Check.ThrowsAsync<RpcException>(() => env.Add(extra: new { agent = "paid,nobody" }));
        });

        r.Add("webhooks: a skip or a failed start leaves no chat behind; a missing project or runtime is a skip", async () =>
        {
            var env = await new Env().StartAsync();
            var added = await env.Add();
            var id = S(added["id"])!; var token = S(added["token"])!;
            env.Runtime.Fail = true;
            var failed = await env.Post(id, token, Crit());
            Check.Equal(500, failed.Status);
            Check.Equal("failed", S(failed.Body["status"]));
            Check.Contains(S(failed.Body["reason"]), "not loaded");
            Check.Equal(0, env.Ctx.SessionsFake.Sessions.Count, "the chat the failed start made is gone");
            env.Runtime.Fail = false;
            env.Ctx.SessionsFake.Projects.Clear();
            Check.Contains(S((await env.Post(id, token, Crit("x"))).Body["reason"]), "no longer exists");
            Check.Equal(0, env.Ctx.SessionsFake.Sessions.Count);
        });

        r.Add("webhooks: the receiver answers only allowed addresses, throttles wrong tokens, and refuses oversize and wrong methods", async () =>
        {
            var env = await new Env().StartAsync();
            var added = await env.Add();
            var id = S(added["id"])!; var token = S(added["token"])!;
            Check.Equal(403, (await env.Post(id, token, Crit(), from: "192.168.1.9")).Status, "not on the allow list: refused before the token is looked at");
            env.Ctx.SettingsFake.SetQuiet("webhooks.allowFrom", new JsonArray("192.168.1.0/24", "10.1.2.3"));
            await env.Plugin.ReconcileAsync(CancellationToken.None);
            Check.Equal(202, (await env.Post(id, token, Crit(), from: "192.168.1.9")).Status, "a CIDR range");
            Check.Equal(202, (await env.Post(id, token, Crit("b"), from: "::ffff:192.168.1.10")).Status, "an IPv4 address mapped into IPv6");
            Check.Equal(403, (await env.Post(id, token, Crit("c"), from: "10.1.2.4")).Status);

            var tooBig = await env.Plugin.ReceiveAsync("/hook/" + id, "POST", IPAddress.Parse("10.1.2.3"), "application/json", _ => Task.FromResult<string?>(null), token, CancellationToken.None);
            Check.Equal(413, tooBig.Status);
            Check.Equal(405, (await env.Plugin.ReceiveAsync("/hook/" + id, "GET", IPAddress.Parse("10.1.2.3"), null, _ => Task.FromResult<string?>(""), token, CancellationToken.None)).Status);
            Check.Equal(404, (await env.Plugin.ReceiveAsync("/other", "POST", IPAddress.Parse("10.1.2.3"), null, _ => Task.FromResult<string?>(""), token, CancellationToken.None)).Status);
            Check.Equal(200, (await env.Plugin.ReceiveAsync("/health", "GET", IPAddress.Parse("10.1.2.3"), null, _ => Task.FromResult<string?>(""), null, CancellationToken.None)).Status);

            for (var i = 0; i < FailureThrottle.Limit; i++) await env.Post(id, "whk_guess" + i, Crit(), from: "10.1.2.3");
            Check.Equal(429, (await env.Post(id, token, Crit("d"), from: "10.1.2.3")).Status, "a guessing address is refused even with the right token");
            Check.Equal(202, (await env.Post(id, token, Crit("e"), from: "192.168.1.9")).Status, "another address is not affected");
            env.Time.Advance(FailureThrottle.Window + TimeSpan.FromSeconds(1));
            Check.Equal(202, (await env.Post(id, token, Crit("f"), from: "10.1.2.3")).Status, "the window passed");
        });

        r.Add("webhooks: a disabled webhook skips, an agent's proposal waits for the user, and an agent cannot enable one", async () =>
        {
            var env = await new Env().StartAsync();
            var added = await env.Add(extra: new { enabled = false });
            var id = S(added["id"])!; var token = S(added["token"])!;
            var off = await env.Post(id, token, Crit());
            Check.Equal("skipped", S(off.Body["status"]));
            Check.Contains(S(off.Body["reason"]), "disabled");
            await env.Ctx.RpcFake.CallAsync("webhooks.update", new { id, enabled = true });
            Check.Equal("started", S((await env.Post(id, token, Crit())).Body["status"]));

            var proposed = await env.Add(extra: new { name = "agent's", proposedBy = "session:ses_1" });
            Check.Equal(false, proposed["enabled"]!.GetValue<bool>(), "an agent's webhook starts paused");
            Check.Contains(S((await env.Post(S(proposed["id"])!, S(proposed["token"])!, Crit("p"))).Body["reason"]), "proposal");
            var ex = await Check.ThrowsAsync<RpcException>(() => env.Ctx.RpcFake.InvokeAsync("webhooks.update", new { id = S(proposed["id"]), enabled = true, proposedBy = "session:ses_1" }));
            Check.Contains(ex.Message, "cannot enable");
            await env.Ctx.RpcFake.CallAsync("webhooks.update", new { id, prompt = "changed", proposedBy = "session:ses_1" });
            Check.Equal(false, (await env.Get(id))["enabled"]!.GetValue<bool>(), "an agent's change of what runs pauses it");
            await env.Ctx.RpcFake.CallAsync("webhooks.update", new { id, enabled = true });
            Check.Equal(true, (await env.Get(id))["enabled"]!.GetValue<bool>(), "the user resumes it");
            Check.True((await env.Get(id))["proposedBy"] is null, "and the proposal mark is gone");
        });

        r.Add("webhooks: test shows the filled-in prompt and the verdict without starting anything; delete removes history", async () =>
        {
            var env = await new Env().StartAsync();
            var added = await env.Add(extra: new { match = new[] { new { path = "state", equals = "CRIT" } } });
            var id = S(added["id"])!;
            var plan = (await env.Ctx.RpcFake.CallAsync("webhooks.test", new { id, payload = Crit() }))!;
            Check.True(plan["skip"] is null);
            Check.Contains(S(plan["prompt"]), "Kernel errors on nuc is CRIT");
            var skip = (await env.Ctx.RpcFake.CallAsync("webhooks.test", new { id, payload = Crit(state: "OK") }))!;
            Check.Contains(S(skip["skip"]), "filtered");
            Check.Equal(0, env.Ctx.SessionsFake.Sessions.Count, "a test starts nothing");
            var ran = (await env.Ctx.RpcFake.CallAsync("webhooks.test", new { id, payload = Crit(), run = true }))!;
            Check.Equal("started", S(ran["status"]));
            Check.Equal("test", S((await env.History(id))[0]!["from"]));
            await env.Ctx.RpcFake.CallAsync("webhooks.delete", new { id });
            Check.Equal(0, (await env.History(id)).Count);
            await Check.ThrowsAsync<RpcException>(() => env.Ctx.RpcFake.InvokeAsync("webhooks.get", new { id }));
        });

        r.Add("webhooks: hooks and runs survive a restart", async () =>
        {
            var env = await new Env().StartAsync();
            var added = await env.Add();
            await env.Post(S(added["id"])!, S(added["token"])!, Crit());
            env.Ctx.Unload();
            var again = await new Env().StartAsync();
            Check.Equal(0, ((JsonArray)(await again.Ctx.RpcFake.CallAsync("webhooks.list", new { }))!).Count, "a different home: nothing there");
            // the same home, a second generation of the plugin
            var plugin2 = new WebhooksPlugin(env.Time, listen: false);
            var ctx2 = new FakePluginContext(env.Ctx.Paths.Home, "netpi.webhooks");
            ctx2.ServicesFake.Register<IAgentRuntime>(env.Runtime);
            ctx2.SessionsFake.Projects.AddRange(env.Ctx.SessionsFake.Projects);
            await plugin2.StartAsync(ctx2, CancellationToken.None);
            var list = (JsonArray)(await ctx2.RpcFake.CallAsync("webhooks.list", new { }))!;
            Check.Equal(1, list.Count);
            Check.Equal("started", S(list[0]!["lastStatus"]));
            Check.Equal(1, ((JsonArray)(await ctx2.RpcFake.CallAsync("webhooks.history", new { }))!).Count);
            var (status, answer) = await plugin2.ReceiveAsync("/hook/" + S(added["id"]), "POST", IPAddress.Loopback, null, _ => Task.FromResult<string?>("{}"), S(added["token"]), CancellationToken.None);
            Check.Equal(202, status, "the stored hash still accepts the token: " + answer.ToJsonString());
        });

        r.Add("webhooks tool: lists, reads, tests and pauses; creating, enabling, re-keying and deleting are refused", async () =>
        {
            var env = await new Env().StartAsync();
            var added = await env.Add();
            var id = S(added["id"])!;
            var tool = new WebhooksTool(env.Ctx);
            ToolContext Tc() => new() { SessionId = "ses_t", AgentId = "agt_1", CallId = "c", Cwd = Path.GetTempPath(), Services = env.Ctx.Services, Events = env.Ctx.Events };
            async Task<ToolResult> Call(object args) => await tool.ExecuteAsync(Tc(), T.Args(args), CancellationToken.None);

            var list = await Call(new { action = "list" });
            Check.Contains(list.Content, id);
            Check.NotContains(list.Content, S(added["token"])!, "the token never reaches a chat");
            Check.Contains((await Call(new { action = "get", id })).Content, "Alert: {{service}}");
            var test = await Call(new { action = "test", id, payload = Crit() });
            Check.Contains(test.Content, "would start a chat");
            Check.Equal(0, env.Ctx.SessionsFake.Sessions.Count);
            await env.Post(id, S(added["token"])!, Crit());
            Check.Contains((await Call(new { action = "history", id })).Content, "started");
            foreach (var refused in new[] { "add", "update", "enable", "delete", "rotate" })
                Check.True((await Call(new { action = refused, id })).IsError, refused + " is the user's");
            var paused = await Call(new { action = "pause", id });
            Check.False(paused.IsError);
            Check.Equal(false, (await env.Get(id))["enabled"]!.GetValue<bool>());
        });

        r.Add("webhooks: the real listener — off until enabled, answers over a socket, follows setting changes", async () =>
        {
            var port = FreePort();
            var env = await new Env(listen: true).StartAsync();
            Check.Equal(false, env.Plugin.Status()["listening"]!.GetValue<bool>(), "off by default");
            var added = await env.Add();
            var id = S(added["id"])!; var token = S(added["token"])!;

            env.Ctx.SettingsFake.SetQuiet("webhooks.port", port);
            env.Ctx.SettingsFake.SetQuiet("webhooks.enabled", true);
            env.Ctx.Events.Publish("settings.changed", new JsonObject { ["path"] = "webhooks.enabled" });
            await WaitFor(() => env.Plugin.Status()["listening"]!.GetValue<bool>() || env.Plugin.Status()["error"] is not null);
            Check.True(env.Plugin.Status()["listening"]!.GetValue<bool>(), "listening: " + env.Plugin.Status()["error"]);
            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };

            Check.Equal(HttpStatusCode.OK, (await http.GetAsync("/health")).StatusCode);
            Check.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/elsewhere")).StatusCode);
            var noAuth = await http.PostAsync("/hook/" + id, new StringContent("{}", Encoding.UTF8, "application/json"));
            Check.Equal(HttpStatusCode.Unauthorized, noAuth.StatusCode);

            var req = new HttpRequestMessage(HttpMethod.Post, "/hook/" + id) { Content = new StringContent(JsonSerializer.Serialize(Crit("Container radio", "nuc", "CRIT", "Exited (1)")), Encoding.UTF8, "application/json") };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var res = await http.SendAsync(req);
            Check.Equal(HttpStatusCode.Accepted, res.StatusCode);
            var body = JsonNode.Parse(await res.Content.ReadAsStringAsync())!;
            Check.Equal("started", S(body["status"]));
            Check.Contains(env.Runtime.Sent.Single().Input.Text, "Container radio on nuc is CRIT");

            var alt = new HttpRequestMessage(HttpMethod.Post, "/hook/" + id) { Content = new StringContent("{\"host\":\"x\",\"service\":\"y\",\"state\":\"CRIT\"}", Encoding.UTF8, "application/json") };
            alt.Headers.Add("X-Webhook-Token", token);
            Check.Equal(HttpStatusCode.Accepted, (await http.SendAsync(alt)).StatusCode, "the X-Webhook-Token header works too");

            env.Ctx.SettingsFake.SetQuiet("webhooks.enabled", false);
            env.Ctx.Events.Publish("settings.changed", new JsonObject { ["path"] = "webhooks.enabled" });
            await WaitFor(() => !env.Plugin.Status()["listening"]!.GetValue<bool>());
            using var fresh = new HttpClient();   // not the connection that was already open
            await Check.ThrowsAsync<HttpRequestException>(() => fresh.GetAsync($"http://127.0.0.1:{port}/health"));
            await env.Plugin.StopAsync(CancellationToken.None);
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

    private static async Task WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++) await Task.Delay(50);
        Check.True(condition(), "the condition came true within 5 s");
    }
}
