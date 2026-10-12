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
/// payload → prompt template and match conditions, incidents (a group key opens a debounce window, later alerts join the batch or are
/// appended to the open chat, recoveries reach the chat that has the incident, repeats are duplicates), a batch that cannot start
/// waiting instead of vanishing, the agent choices, proposals that wait for the user, the tool's limits, and the real listener over a socket.
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
            Plugin = new WebhooksPlugin(Time, _listen, loop: false);
            await Plugin.StartAsync(Ctx, CancellationToken.None);
            return this;
        }

        public async Task<JsonObject> Add(object? extra = null, string prompt = "Alert: {{service}} on {{host}} is {{state}}. {{output}}")
        {
            var p = new JsonObject { ["name"] = "checkmk", ["prompt"] = prompt, ["projectId"] = Project.Id };
            if (extra is not null) foreach (var kv in NetPiJson.ToNode(extra)!.AsObject()) p[kv.Key] = kv.Value?.DeepClone();
            return (await Ctx.RpcFake.CallAsync("webhooks.add", p))!.AsObject();
        }

        /// <summary>A grouped webhook, the way Checkmk's is set up: the incident is the host, 90 s to collect, recoveries may join an open one.</summary>
        public Task<JsonObject> AddGrouped(object? extra = null, string prompt = "Incident {{key}}, {{count}} alert(s):\n{{alerts}}") =>
            Add(MergeInto(new
            {
                alert = "{{service}} is {{state}} ({{output}})",
                group = new { key = "{{host}}", debounceSeconds = 90, reopenMinutes = 240 },
                match = new[] { new { path = "state", @in = new[] { "CRIT", "DOWN" } } },
            }, extra), prompt);

        private static object MergeInto(object baseObj, object? extra)
        {
            var o = NetPiJson.ToNode(baseObj)!.AsObject();
            if (extra is not null) foreach (var kv in NetPiJson.ToNode(extra)!.AsObject()) o[kv.Key] = kv.Value?.DeepClone();
            return o;
        }

        public async Task<JsonObject> Get(string id) => (await Ctx.RpcFake.CallAsync("webhooks.get", new { id }))!.AsObject();
        public async Task<JsonArray> History(string id) => (await Ctx.RpcFake.CallAsync("webhooks.history", new { id }))!.AsArray();
        public async Task<JsonArray> Incidents(string id) => (await Ctx.RpcFake.CallAsync("webhooks.incidents", new { id }))!.AsArray();
        public Task<int> Tick() => Plugin.Runner!.TickAsync(CancellationToken.None);

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

        public void FinishChats() { foreach (var a in Runtime.Agents) a.Status = AgentStatus.Completed; }
    }

    private static string? S(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static object Alert(string service = "Kernel errors", string host = "nuc", string state = "CRIT", string output = "1 in last 30 min") =>
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

            Check.Equal(401, (await env.Post(id, "whk_wrong", Alert())).Status);
            Check.Equal(401, (await env.Post(id, "", Alert())).Status);
            Check.Equal(401, (await env.Post("whk_nothere", token, Alert())).Status, "an unknown webhook answers like a wrong token");
            Check.Equal(0, env.Ctx.SessionsFake.Sessions.Count, "refused requests start nothing");
            Check.Equal(202, (await env.Post(id, token, Alert())).Status);

            var rotated = (await env.Ctx.RpcFake.CallAsync("webhooks.rotateToken", new { id }))!;
            Check.Differs(token, S(rotated["token"])!);
            Check.Equal(401, (await env.Post(id, token, Alert("Other"))).Status, "the old token stopped working");
            Check.Equal(202, (await env.Post(id, S(rotated["token"])!, Alert("Other"))).Status);
        });

        r.Add("webhooks: an ungrouped payload becomes a chat at once, on the project, sent as the system", async () =>
        {
            var env = await new Env().StartAsync();
            var added = await env.Add(extra: new { title = "{{host}} / {{service}} is {{state}}" });
            var (status, body) = await env.Post(S(added["id"])!, S(added["token"])!, Alert(output: "disk \"/\" is 91 %"));
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

        r.Add("webhooks: match conditions filter an ungrouped payload before anything starts", async () =>
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

            Check.Equal("started", S((await env.Post(id, token, Alert())).Body["status"]));
            var recovered = await env.Post(id, token, Alert(state: "OK"));
            Check.Equal(200, recovered.Status);
            Check.Equal("skipped", S(recovered.Body["status"]));
            Check.Contains(S(recovered.Body["reason"]), "filtered: state is “OK”");
            Check.Contains(S((await env.Post(id, token, Alert(host: "testhost"))).Body["reason"]), "excluded");
            Check.Contains(S((await env.Post(id, token, Alert(service: "Fail2ban sshd"))).Body["reason"]), "does not match");
            Check.Equal(1, env.Ctx.SessionsFake.Sessions.Count, "three skips left no chat");
            await Check.ThrowsAsync<RpcException>(() => env.Add(extra: new { match = new[] { new { path = "x", matches = "(" } } }));
            await Check.ThrowsAsync<RpcException>(() => env.Add(extra: new { match = new[] { new { path = "x" } } }));
        });

        r.Add("webhooks: a storm on one host is one chat — alerts inside the window are collected and start together", async () =>
        {
            var env = await new Env().StartAsync();
            var added = await env.AddGrouped(extra: new { title = "{{host}} is failing" });
            var id = S(added["id"])!; var token = S(added["token"])!;

            var first = await env.Post(id, token, Alert("Kernel errors", "nuc"));
            Check.Equal(202, first.Status);
            Check.Equal("held", S(first.Body["status"]));
            Check.Contains(S(first.Body["reason"]), "collecting for 90 s");
            env.Time.Advance(TimeSpan.FromSeconds(30));
            Check.Equal("held", S((await env.Post(id, token, Alert("Container radio", "nuc", output: "Exited (1)"))).Body["status"]), "same host: joins the batch");
            Check.Contains(S((await env.Post(id, token, Alert("Container yue2", "nuc", output: "Exited (1)"))).Body["reason"]), "collected with “nuc”");
            Check.Equal("held", S((await env.Post(id, token, Alert("Disk", "server"))).Body["status"]), "another host: its own incident");
            Check.Equal("duplicate", S((await env.Post(id, token, Alert("Container yue2", "nuc", output: "Exited (1)"))).Body["status"]), "the very same alert twice in a batch is one");
            Check.Equal(0, env.Ctx.SessionsFake.Sessions.Count, "nothing starts inside the window");
            Check.Equal(0, await env.Tick(), "and a tick inside the window starts nothing");
            Check.Equal(2, (await env.Incidents(id)).Count, "two incidents collecting");

            env.Time.Advance(TimeSpan.FromSeconds(61));   // 91 s after the nuc's first alert
            Check.Equal(1, await env.Tick(), "only the nuc's window has ended");
            var nuc = env.Ctx.SessionsFake.Sessions.Single();
            Check.Equal("Webhook: nuc is failing (+2 more)", nuc.Title);
            var prompt = env.Runtime.Sent.Single().Input.Text;
            Check.Contains(prompt, "Incident nuc, 3 alert(s)");
            Check.True(prompt.Contains("[1/3]") && prompt.Contains("[3/3]"), "the alerts are numbered");
            Check.True(prompt.Contains("Kernel errors is CRIT") && prompt.Contains("Container radio is CRIT (Exited (1))") && prompt.Contains("Container yue2 is CRIT"), "all three alerts are in it");

            env.Time.Advance(TimeSpan.FromSeconds(30));
            Check.Equal(1, await env.Tick());
            Check.Equal(2, env.Ctx.SessionsFake.Sessions.Count, "the server's incident became its own chat");
            var states = (await env.Incidents(id)).Select(i => S(i!["state"])).ToList();
            Check.Equal("open,open", string.Join(",", states));
            var history = (await env.History(id)).Select(h => S(h!["status"])).ToList();
            Check.Equal(4, history.Count(h => h == "held"));
            Check.Equal(2, history.Count(h => h == "started"));
            Check.Equal(1, history.Count(h => h == "duplicate"));
        });

        r.Add("webhooks: later alerts of an open incident are appended to its chat; recoveries reach it; repeats are duplicates", async () =>
        {
            var env = await new Env().StartAsync();
            var added = await env.AddGrouped();
            var id = S(added["id"])!; var token = S(added["token"])!;
            await env.Post(id, token, Alert("Kernel errors"));
            env.Time.Advance(TimeSpan.FromSeconds(91));
            await env.Tick();
            var session = env.Ctx.SessionsFake.Sessions.Single();
            Check.Equal(1, env.Runtime.Sent.Count);

            env.Time.Advance(TimeSpan.FromMinutes(5));
            var more = await env.Post(id, token, Alert("Container radio", output: "Exited (1)"));
            Check.Equal(202, more.Status);
            Check.Equal("appended", S(more.Body["status"]));
            Check.Equal(session.Id, S(more.Body["sessionId"]));
            Check.Equal(1, env.Ctx.SessionsFake.Sessions.Count, "no second chat");
            Check.Equal(2, env.Runtime.Sent.Count);
            Check.Equal(session.Id, env.Runtime.Sent[1].SessionId);
            Check.Contains(env.Runtime.Sent[1].Input.Text, "More from the same incident (nuc)");
            Check.Contains(env.Runtime.Sent[1].Input.Text, "Container radio");
            Check.Equal("system", env.Runtime.Sent[1].Input.Source);

            var repeat = await env.Post(id, token, Alert("Container radio", output: "Exited (1)"));
            Check.Equal(200, repeat.Status);
            Check.Equal("duplicate", S(repeat.Body["status"]), "the same alert again, just now");
            Check.Equal(2, env.Runtime.Sent.Count, "a repeat does not wake the chat");

            var recovery = await env.Post(id, token, Alert("Container radio", state: "OK", output: "Up 2 minutes"));
            Check.Equal("appended", S(recovery.Body["status"]), "a recovery fails the match but the incident is open: it joins");
            Check.Contains(env.Runtime.Sent[2].Input.Text, "radio is OK");

            var stranger = await env.Post(id, token, Alert("Disk", host: "server", state: "OK"));
            Check.Equal("skipped", S(stranger.Body["status"]), "a recovery with no open incident starts nothing");
            Check.Contains(S(stranger.Body["reason"]), "no open incident");
            Check.Equal(1, env.Ctx.SessionsFake.Sessions.Count);

            var other = await env.Post(id, token, Alert("Disk", host: "server"));
            Check.Equal("held", S(other.Body["status"]), "another host opens its own incident");
            // (rows made in the same millisecond have no defined order: count them)
            var rows = (await env.History(id)).Select(h => S(h!["status"])).ToList();
            Check.Equal("appended:2,duplicate:1,held:2,skipped:1,started:1", string.Join(",", rows.GroupBy(x => x).OrderBy(g => g.Key).Select(g => $"{g.Key}:{g.Count()}")));
        });

        r.Add("webhooks: a finished chat is reopened inside reopenMinutes; after that, or when the chat is gone, the alert opens a new incident", async () =>
        {
            var env = await new Env().StartAsync();
            var added = await env.AddGrouped(extra: new { group = new { key = "{{host}}", debounceSeconds = 10, reopenMinutes = 60 } });
            var id = S(added["id"])!; var token = S(added["token"])!;
            await env.Post(id, token, Alert("Kernel errors"));
            env.Time.Advance(TimeSpan.FromSeconds(11));
            await env.Tick();
            env.FinishChats();

            env.Time.Advance(TimeSpan.FromMinutes(30));
            Check.Equal("appended", S((await env.Post(id, token, Alert("Container radio"))).Body["status"]), "finished 30 min ago: the same chat wakes up");
            Check.Equal(1, env.Ctx.SessionsFake.Sessions.Count);
            env.FinishChats();   // the woken chat finishes again

            env.Time.Advance(TimeSpan.FromMinutes(61));
            var late = await env.Post(id, token, Alert("Container yue2"));
            Check.Equal("held", S(late.Body["status"]), "an hour after the last activity it is a new incident");
            env.Time.Advance(TimeSpan.FromSeconds(11));
            await env.Tick();
            Check.Equal(2, env.Ctx.SessionsFake.Sessions.Count);

            env.Ctx.SessionsFake.Sessions.Clear();   // the user deleted the chat
            var orphan = await env.Post(id, token, Alert("Disk"));
            Check.Equal("held", S(orphan.Body["status"]), "the chat is gone: a new incident, not a lost alert");
        });

        r.Add("webhooks: debounce 0 starts the chat at once, and an ungrouped webhook never waits", async () =>
        {
            var env = await new Env().StartAsync();
            var added = await env.AddGrouped(extra: new { group = new { key = "{{host}}", debounceSeconds = 0 } });
            var first = await env.Post(S(added["id"])!, S(added["token"])!, Alert());
            Check.Equal("started", S(first.Body["status"]));
            Check.Equal(1, env.Ctx.SessionsFake.Sessions.Count);
        });

        r.Add("webhooks: a batch that cannot start waits and is retried for 30 minutes instead of being dropped — then each alert is told it failed", async () =>
        {
            var env = await new Env().StartAsync();
            env.Agents(("local", false, true));   // the only free agent is not loaded
            var added = await env.AddGrouped(extra: new { agent = "local" });
            var id = S(added["id"])!; var token = S(added["token"])!;
            await env.Post(id, token, Alert("Kernel errors"));
            await env.Post(id, token, Alert("Container radio"));
            env.Time.Advance(TimeSpan.FromSeconds(91));
            Check.Equal(0, await env.Tick());
            var inc = (await env.Incidents(id)).Single();
            Check.Equal("collecting", S(inc!["state"]));
            Check.Contains(S(inc["waiting"]), "unavailable");
            Check.Equal(0, env.Ctx.SessionsFake.Sessions.Count);
            Check.Equal(2, ((JsonArray)(await env.History(id))).Count(h => S(h!["status"]) == "held"), "nothing is lost meanwhile");

            env.Time.Advance(TimeSpan.FromMinutes(5));
            env.Agents(("local", true, true));   // the model loaded
            Check.Equal(1, await env.Tick(), "the next try starts it");
            Check.Contains(env.Runtime.Sent.Single().Input.Text, "Container radio", "with both alerts");

            // a webhook whose agent never comes back
            var env2 = await new Env().StartAsync();
            env2.Agents(("local", false, true));
            var added2 = await env2.AddGrouped(extra: new { agent = "local" });
            await env2.Post(S(added2["id"])!, S(added2["token"])!, Alert("Kernel errors"));
            for (var i = 0; i < 70; i++) { env2.Time.Advance(TimeSpan.FromSeconds(30)); await env2.Tick(); }
            var rows = (await env2.History(S(added2["id"])!)).Select(h => S(h!["status"])).ToList();
            Check.True(rows.Contains("failed"), "after 30 minutes the alert is reported failed");
            Check.Equal(0, (await env2.Incidents(S(added2["id"])!)).Count, "and the incident is closed");
        });

        r.Add("webhooks: the safety fuse holds new chats back until the hour has room; it does not drop them", async () =>
        {
            var env = await new Env().StartAsync();
            var added = await env.AddGrouped(extra: new { maxChatsPerHour = 1, group = new { key = "{{host}}", debounceSeconds = 10 } });
            var id = S(added["id"])!; var token = S(added["token"])!;
            await env.Post(id, token, Alert("a", host: "nuc"));
            await env.Post(id, token, Alert("b", host: "server"));
            env.Time.Advance(TimeSpan.FromSeconds(11));
            Check.Equal(1, await env.Tick(), "one chat fits the fuse");
            Check.Equal(1, env.Ctx.SessionsFake.Sessions.Count);
            env.Time.Advance(TimeSpan.FromMinutes(30));
            Check.Equal(0, await env.Tick(), "still inside the hour");
            Check.Equal(1, (await env.Incidents(id)).Count(i => S(i!["state"]) == "collecting"), "the other waits — the fuse paces, it does not expire");
            Check.Contains(S((await env.Incidents(id)).First(i => S(i!["state"]) == "collecting")!["waiting"]), "safety fuse");
            env.Time.Advance(TimeSpan.FromMinutes(31));
            Check.Equal(1, await env.Tick(), "an hour after the first, it starts");
            Check.Equal(2, env.Ctx.SessionsFake.Sessions.Count);
        });

        r.Add("webhooks: agent choices — a key, any, free, a preference list — and nothing here decides the budget", async () =>
        {
            var env = await new Env().StartAsync();
            env.Agents(("local", false, true), ("paid", true, false), ("home", true, true));
            var used = new List<string?>();
            env.Ctx.Rpc.Register("agents.use", (req, _) => { used.Add(req.Str("agent")); return Task.FromResult<object?>(null); });
            env.Ctx.Rpc.Register("budget.status", (_, _) => Task.FromResult<object?>(new JsonObject { ["exhausted"] = true }));

            async Task<JsonObject> Fire(string agent, string service)
            {
                var h = await env.Add(extra: new { name = "w-" + agent + service, agent });
                return (await env.Post(S(h["id"])!, S(h["token"])!, Alert(service))).Body;
            }

            Check.Equal("failed", S((await Fire("local", "s1"))["status"]), "an unloaded agent: told at once for an ungrouped alert");
            Check.Equal("started", S((await Fire("paid", "s2"))["status"]), "a paid agent: the Agents plugin enforces the budget, not this plugin");
            Check.Equal("started", S((await Fire("home", "s3"))["status"]));
            Check.Equal("started", S((await Fire("any", "s4"))["status"]));
            Check.Equal("started", S((await Fire("free", "s5"))["status"]));
            Check.Equal("started", S((await Fire("local, paid ,home", "s6"))["status"]));
            Check.Equal("paid,home,any,home,paid", string.Join(",", used), "paid; home; any; the first free one; the first available of the list");

            env.Agents(("local", false, true), ("paid", true, false));
            Check.Contains(S((await Fire("free", "s7"))["reason"]), "no free agent");
            await Check.ThrowsAsync<RpcException>(() => env.Add(extra: new { agent = "nobody" }));
            await Check.ThrowsAsync<RpcException>(() => env.Add(extra: new { agent = "paid,nobody" }));
        });

        r.Add("webhooks: a failed start leaves no chat behind, and a missing project is told at once", async () =>
        {
            var env = await new Env().StartAsync();
            var added = await env.Add();
            var id = S(added["id"])!; var token = S(added["token"])!;
            env.Runtime.Fail = true;
            var failed = await env.Post(id, token, Alert());
            Check.Equal(500, failed.Status);
            Check.Equal("failed", S(failed.Body["status"]));
            Check.Contains(S(failed.Body["reason"]), "not loaded");
            Check.Equal(0, env.Ctx.SessionsFake.Sessions.Count, "the chat the failed start made is gone");
            env.Runtime.Fail = false;
            env.Ctx.SessionsFake.Projects.Clear();
            var gone = await env.Post(id, token, Alert("x"));
            Check.Equal("failed", S(gone.Body["status"]));
            Check.Contains(S(gone.Body["reason"]), "no longer exists");
            Check.Equal(0, env.Ctx.SessionsFake.Sessions.Count);
        });

        r.Add("webhooks: the receiver answers only allowed addresses, throttles wrong tokens, and refuses oversize and wrong methods", async () =>
        {
            var env = await new Env().StartAsync();
            var added = await env.Add();
            var id = S(added["id"])!; var token = S(added["token"])!;
            Check.Equal(403, (await env.Post(id, token, Alert(), from: "192.168.1.9")).Status, "not on the allow list: refused before the token is looked at");
            env.Ctx.SettingsFake.SetQuiet("webhooks.allowFrom", new JsonArray("192.168.1.0/24", "10.1.2.3"));
            await env.Plugin.ReconcileAsync(CancellationToken.None);
            Check.Equal(202, (await env.Post(id, token, Alert(), from: "192.168.1.9")).Status, "a CIDR range");
            Check.Equal(202, (await env.Post(id, token, Alert("b"), from: "::ffff:192.168.1.10")).Status, "an IPv4 address mapped into IPv6");
            Check.Equal(403, (await env.Post(id, token, Alert("c"), from: "10.1.2.4")).Status);

            var tooBig = await env.Plugin.ReceiveAsync("/hook/" + id, "POST", IPAddress.Parse("10.1.2.3"), "application/json", _ => Task.FromResult<string?>(null), token, CancellationToken.None);
            Check.Equal(413, tooBig.Status);
            Check.Equal(405, (await env.Plugin.ReceiveAsync("/hook/" + id, "GET", IPAddress.Parse("10.1.2.3"), null, _ => Task.FromResult<string?>(""), token, CancellationToken.None)).Status);
            Check.Equal(404, (await env.Plugin.ReceiveAsync("/other", "POST", IPAddress.Parse("10.1.2.3"), null, _ => Task.FromResult<string?>(""), token, CancellationToken.None)).Status);
            Check.Equal(200, (await env.Plugin.ReceiveAsync("/health", "GET", IPAddress.Parse("10.1.2.3"), null, _ => Task.FromResult<string?>(""), null, CancellationToken.None)).Status);

            for (var i = 0; i < FailureThrottle.Limit; i++) await env.Post(id, "whk_guess" + i, Alert(), from: "10.1.2.3");
            Check.Equal(429, (await env.Post(id, token, Alert("d"), from: "10.1.2.3")).Status, "a guessing address is refused even with the right token");
            Check.Equal(202, (await env.Post(id, token, Alert("e"), from: "192.168.1.9")).Status, "another address is not affected");
            env.Time.Advance(FailureThrottle.Window + TimeSpan.FromSeconds(1));
            Check.Equal(202, (await env.Post(id, token, Alert("f"), from: "10.1.2.3")).Status, "the window passed");
        });

        r.Add("webhooks: a disabled webhook skips, an agent's proposal waits for the user, and an agent cannot enable one", async () =>
        {
            var env = await new Env().StartAsync();
            var added = await env.Add(extra: new { enabled = false });
            var id = S(added["id"])!; var token = S(added["token"])!;
            var off = await env.Post(id, token, Alert());
            Check.Equal("skipped", S(off.Body["status"]));
            Check.Contains(S(off.Body["reason"]), "disabled");
            await env.Ctx.RpcFake.CallAsync("webhooks.update", new { id, enabled = true });
            Check.Equal("started", S((await env.Post(id, token, Alert())).Body["status"]));

            var proposed = await env.Add(extra: new { name = "agent's", proposedBy = "session:ses_1" });
            Check.Equal(false, proposed["enabled"]!.GetValue<bool>(), "an agent's webhook starts paused");
            Check.Contains(S((await env.Post(S(proposed["id"])!, S(proposed["token"])!, Alert("p"))).Body["reason"]), "proposal");
            var ex = await Check.ThrowsAsync<RpcException>(() => env.Ctx.RpcFake.InvokeAsync("webhooks.update", new { id = S(proposed["id"]), enabled = true, proposedBy = "session:ses_1" }));
            Check.Contains(ex.Message, "cannot enable");
            await env.Ctx.RpcFake.CallAsync("webhooks.update", new { id, prompt = "changed", proposedBy = "session:ses_1" });
            Check.Equal(false, (await env.Get(id))["enabled"]!.GetValue<bool>(), "an agent's change of what runs pauses it");
            await env.Ctx.RpcFake.CallAsync("webhooks.update", new { id, enabled = true });
            Check.Equal(true, (await env.Get(id))["enabled"]!.GetValue<bool>(), "the user resumes it");
            Check.True((await env.Get(id))["proposedBy"] is null, "and the proposal mark is gone");

            // a webhook disabled while a batch is collecting: its alerts are reported, not lost
            var g = await env.AddGrouped();
            await env.Post(S(g["id"])!, S(g["token"])!, Alert());
            await env.Ctx.RpcFake.CallAsync("webhooks.update", new { id = S(g["id"]), enabled = false });
            env.Time.Advance(TimeSpan.FromSeconds(91));
            var chatsBefore = env.Ctx.SessionsFake.Sessions.Count;
            await env.Tick();
            Check.Equal(chatsBefore, env.Ctx.SessionsFake.Sessions.Count, "no chat started for it");
            Check.True((await env.History(S(g["id"])!)).Any(h => S(h!["reason"])?.Contains("disabled before its window ended") == true), "its collected alert is reported, not lost");
        });

        r.Add("webhooks: update changes grouping and templates; group {} turns grouping off; bad values are refused", async () =>
        {
            var env = await new Env().StartAsync();
            var added = await env.Add();
            var id = S(added["id"])!;
            var changed = (await env.Ctx.RpcFake.CallAsync("webhooks.update", new { id, group = new { key = "{{host}}", debounceSeconds = 45 }, followUp = "again: {{alert}}", alert = "{{service}}" }))!;
            Check.Equal("{{host}}", S(changed["group"]!["key"]));
            Check.Equal(45, changed["group"]!["debounceSeconds"]!.GetValue<int>());
            Check.Equal(240, changed["group"]!["reopenMinutes"]!.GetValue<int>(), "the other group value takes its default");
            var off = (await env.Ctx.RpcFake.CallAsync("webhooks.update", new { id, group = new { } }))!;
            Check.True(off["group"] is null, "an empty group removes it");
            await Check.ThrowsAsync<RpcException>(() => env.Ctx.RpcFake.InvokeAsync("webhooks.update", new { id, group = new { debounceSeconds = 5 } }));
            await Check.ThrowsAsync<RpcException>(() => env.Ctx.RpcFake.InvokeAsync("webhooks.update", new { id, group = new { key = "{{host}}", debounceSeconds = 99999 } }));
            await Check.ThrowsAsync<RpcException>(() => env.Ctx.RpcFake.InvokeAsync("webhooks.update", new { id, maxChatsPerHour = -1 }));
        });

        r.Add("webhooks: test shows what a payload would do — start, collect, join, append or skip — without storing anything", async () =>
        {
            var env = await new Env().StartAsync();
            var added = await env.AddGrouped();
            var id = S(added["id"])!; var token = S(added["token"])!;
            async Task<JsonNode> Test(object payload) => (await env.Ctx.RpcFake.CallAsync("webhooks.test", new { id, payload }))!;

            var collect = await Test(Alert());
            Check.Equal("collect", S(collect["action"]));
            Check.Contains(S(collect["note"]), "wait 90 s");
            Check.Contains(S(collect["prompt"]), "Incident nuc, 1 alert(s)");
            Check.Equal("skip", S((await Test(Alert(state: "OK")))["action"]));
            Check.Equal(0, (await env.Incidents(id)).Count, "a test stores no incident");
            Check.Equal(0, ((JsonArray)(await env.History(id))).Count, "and no delivery");

            await env.Post(id, token, Alert("a"));
            Check.Equal("join", S((await Test(Alert("b")))["action"]));
            env.Time.Advance(TimeSpan.FromSeconds(91));
            await env.Tick();
            var append = await Test(Alert("c"));
            Check.Equal("append", S(append["action"]));
            Check.Contains(S(append["prompt"]), "More from the same incident");
            Check.Equal("append", S((await Test(Alert("d", state: "OK")))["action"]), "a recovery would be appended to the open incident");

            var ran = (await env.Ctx.RpcFake.CallAsync("webhooks.test", new { id, payload = Alert("e", host: "server"), run = true }))!;
            Check.Equal("held", S(ran["status"]));
            Check.True((await env.History(id)).Any(h => S(h!["from"]) == "test"), "a test run is marked as such");
            await env.Ctx.RpcFake.CallAsync("webhooks.delete", new { id });
            Check.Equal(0, ((JsonArray)(await env.History(id))).Count);
            Check.Equal(0, (await env.Incidents(id)).Count, "delete removes the incidents too");
            await Check.ThrowsAsync<RpcException>(() => env.Ctx.RpcFake.InvokeAsync("webhooks.get", new { id }));
        });

        r.Add("webhooks: hooks, runs and collecting incidents survive a restart; the collected alerts start their chat afterwards", async () =>
        {
            var env = await new Env().StartAsync();
            var added = await env.AddGrouped();
            await env.Post(S(added["id"])!, S(added["token"])!, Alert("Kernel errors"));
            await env.Post(S(added["id"])!, S(added["token"])!, Alert("Container radio"));
            env.Ctx.Unload();
            var plugin2 = new WebhooksPlugin(env.Time, listen: false, loop: false);
            var ctx2 = new FakePluginContext(env.Ctx.Paths.Home, "netpi.webhooks");
            ctx2.ServicesFake.Register<IAgentRuntime>(env.Runtime);
            ctx2.SessionsFake.Projects.AddRange(env.Ctx.SessionsFake.Projects);
            await plugin2.StartAsync(ctx2, CancellationToken.None);
            Check.Equal(1, ((JsonArray)(await ctx2.RpcFake.CallAsync("webhooks.list", new { }))!).Count);
            Check.Equal(1, ((JsonArray)(await ctx2.RpcFake.CallAsync("webhooks.incidents", new { }))!).Count, "the collecting incident is still there");
            env.Time.Advance(TimeSpan.FromSeconds(91));
            Check.Equal(1, await plugin2.Runner!.TickAsync(CancellationToken.None));
            Check.Contains(env.Runtime.Sent.Single().Input.Text, "Container radio", "both alerts, collected before the restart");
            var (status, answer) = await plugin2.ReceiveAsync("/hook/" + S(added["id"]), "POST", IPAddress.Loopback, null,
                _ => Task.FromResult<string?>(JsonSerializer.Serialize(Alert("Disk"))), S(added["token"]), CancellationToken.None);
            Check.Equal(202, status, "the stored hash still accepts the token: " + answer.ToJsonString());
            Check.Equal("appended", S(answer["status"]), "and the open incident survived too");
        });

        r.Add("webhooks tool: lists, reads, tests and pauses; creating, enabling, re-keying and deleting are refused", async () =>
        {
            var env = await new Env().StartAsync();
            var added = await env.AddGrouped();
            var id = S(added["id"])!;
            var tool = new WebhooksTool(env.Ctx);
            ToolContext Tc() => new() { SessionId = "ses_t", AgentId = "agt_1", CallId = "c", Cwd = Path.GetTempPath(), Services = env.Ctx.Services, Events = env.Ctx.Events };
            async Task<ToolResult> Call(object args) => await tool.ExecuteAsync(Tc(), T.Args(args), CancellationToken.None);

            var list = await Call(new { action = "list" });
            Check.Contains(list.Content, id);
            Check.NotContains(list.Content, S(added["token"])!, "the token never reaches a chat");
            Check.Contains((await Call(new { action = "get", id })).Content, "Incident {{key}}");
            var test = await Call(new { action = "test", id, payload = Alert() });
            Check.Contains(test.Content, "open an incident and wait");
            Check.Equal(0, env.Ctx.SessionsFake.Sessions.Count);
            await env.Post(id, S(added["token"])!, Alert());
            Check.Contains((await Call(new { action = "history", id })).Content, "held");
            foreach (var refused in new[] { "add", "update", "enable", "delete", "rotate" })
                Check.True((await Call(new { action = refused, id })).IsError, refused + " is the user's");
            var paused = await Call(new { action = "pause", id });
            Check.False(paused.IsError);
            Check.Equal(false, (await env.Get(id))["enabled"]!.GetValue<bool>());
        });

        r.Add("webhooks: a reloaded plugin starts at once and takes over the port its previous generation still holds; a stop ends a bind that is still waiting", async () =>
        {
            var port = FreePort();
            var env = await new Env(listen: true).StartAsync();
            env.Ctx.SettingsFake.SetQuiet("webhooks.port", port);
            env.Ctx.SettingsFake.SetQuiet("webhooks.enabled", true);
            env.Ctx.Events.Publish("settings.changed", new JsonObject { ["path"] = "webhooks.enabled" });
            await WaitFor(() => env.Plugin.Status()["listening"]!.GetValue<bool>());

            // the host starts the new generation BEFORE it stops the old one, which still holds the port
            WebhooksPlugin Generation(out FakePluginContext ctx2)
            {
                ctx2 = new FakePluginContext(env.Ctx.Paths.Home, "netpi.webhooks");
                ctx2.ServicesFake.Register<IAgentRuntime>(env.Runtime);
                ctx2.SettingsFake.SetQuiet("webhooks.port", port);
                ctx2.SettingsFake.SetQuiet("webhooks.enabled", true);
                return new WebhooksPlugin(env.Time, listen: true, loop: false);
            }
            var second = Generation(out var ctx2);
            var started = System.Diagnostics.Stopwatch.StartNew();
            await second.StartAsync(ctx2, CancellationToken.None);
            Check.True(started.Elapsed < TimeSpan.FromSeconds(3), "StartAsync does not wait for the port: " + started.Elapsed);
            Check.False(second.Status()["listening"]!.GetValue<bool>(), "the port is still held by the previous generation");

            await env.Plugin.StopAsync(CancellationToken.None);   // the host stops the old generation
            await WaitFor(() => second.Status()["listening"]!.GetValue<bool>());
            using var http = new HttpClient();
            Check.Equal(HttpStatusCode.OK, (await http.GetAsync($"http://127.0.0.1:{port}/health")).StatusCode, "the new generation answers");

            // a third generation waits for the port the second holds; stopping it must not hang on that wait
            var third = Generation(out var ctx3);
            await third.StartAsync(ctx3, CancellationToken.None);
            var stopping = System.Diagnostics.Stopwatch.StartNew();
            await third.StopAsync(CancellationToken.None);
            Check.True(stopping.Elapsed < TimeSpan.FromSeconds(5), "a stop ends the waiting bind: " + stopping.Elapsed);
            await second.StopAsync(CancellationToken.None);
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

            var req = new HttpRequestMessage(HttpMethod.Post, "/hook/" + id) { Content = new StringContent(JsonSerializer.Serialize(Alert("Container radio", "nuc", "CRIT", "Exited (1)")), Encoding.UTF8, "application/json") };
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
