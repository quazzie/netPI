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
/// payload → prompt template, incidents (a conversation key starts its chat at once, later alerts are appended to the open chat,
/// recoveries reach the chat that has the incident, repeats are duplicates), a chat that cannot start waiting with its alerts
/// instead of vanishing, the agent choices, proposals that wait for the user, the tool's limits, and the real listener over a socket.
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

        /// <summary>A grouped webhook, the way Checkmk's is set up: a Before command names the conversation (the host), writes each item and lets a recovery only join an open one.</summary>
        public Task<JsonObject> AddGrouped(object? extra = null, string prompt = "Incident {{key}}, {{count}} alert(s):\n{{alerts}}") =>
            Add(MergeInto(new { pre = WebhookHooksTests.Adapter() }, extra), prompt);

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

    internal static string? S(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

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
            Check.Equal("/hook/checkmk", S(added["path"]), "the URL is the name, lower-cased");
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

        r.Add("webhooks: a webhook has a readable URL name next to its id; both work, names are unique and checked", async () =>
        {
            var env = await new Env().StartAsync();
            var added = await env.Add(extra: new { name = "Checkmk alerts" });
            var id = S(added["id"])!; var token = S(added["token"])!;
            Check.Equal("checkmk-alerts", S(added["slug"]), "derived from the name");
            Check.Equal("/hook/checkmk-alerts", S(added["path"]));
            Check.Equal(202, (await env.Plugin.ReceiveAsync("/hook/checkmk-alerts", "POST", IPAddress.Loopback, "application/json", _ => Task.FromResult<string?>(JsonSerializer.Serialize(Alert())), token, CancellationToken.None)).Status, "the readable URL works");
            Check.Equal(202, (await env.Plugin.ReceiveAsync("/hook/CHECKMK-alerts", "POST", IPAddress.Loopback, "application/json", _ => Task.FromResult<string?>(JsonSerializer.Serialize(Alert("b"))), token, CancellationToken.None)).Status, "case does not matter");
            Check.Equal(202, (await env.Post(id, token, Alert("c"))).Status, "and so does the id URL");
            Check.Equal(401, (await env.Plugin.ReceiveAsync("/hook/checkmk-alerts", "POST", IPAddress.Loopback, "application/json", _ => Task.FromResult<string?>("{}"), "whk_wrong", CancellationToken.None)).Status, "the token is still needed");

            var twin = await env.Add(extra: new { name = "Checkmk alerts" });
            Check.Equal("checkmk-alerts-2", S(twin["slug"]), "a taken derived name gets a number");
            await Check.ThrowsAsync<RpcException>(() => env.Add(extra: new { name = "other", slug = "checkmk-alerts" }));
            await Check.ThrowsAsync<RpcException>(() => env.Add(extra: new { name = "other", slug = "has space" }));
            await Check.ThrowsAsync<RpcException>(() => env.Add(extra: new { name = "other", slug = "whk_abc" }));
            Check.Equal("my_hook-1", S((await env.Add(extra: new { name = "third", slug = "My_Hook-1" }))["slug"]), "lower-cased");

            var renamed = (await env.Ctx.RpcFake.CallAsync("webhooks.update", new { id, slug = "cmk" }))!;
            Check.Equal("/hook/cmk", S(renamed["path"]));
            Check.Equal(401, (await env.Plugin.ReceiveAsync("/hook/checkmk-alerts", "POST", IPAddress.Loopback, "application/json", _ => Task.FromResult<string?>("{}"), token, CancellationToken.None)).Status, "the old name no longer names it (unknown, like a wrong token)");
            var cleared = (await env.Ctx.RpcFake.CallAsync("webhooks.update", new { id, slug = "" }))!;
            Check.Equal("/hook/" + id, S(cleared["path"]), "no name: the id path");
            await Check.ThrowsAsync<RpcException>(() => env.Ctx.RpcFake.InvokeAsync("webhooks.update", new { id, slug = "my_hook-1" }));
            var proposed = (await env.Ctx.RpcFake.CallAsync("webhooks.update", new { id, slug = "sneaky", proposedBy = "session:ses_1" }))!;
            Check.Equal(false, proposed["enabled"]!.GetValue<bool>(), "an agent renaming the URL pauses it");
        });

        r.Add("webhooks: priority is low, normal, high or critical; it is stamped on the chats, and an agent raising it pauses the webhook", async () =>
        {
            var env = await new Env().StartAsync();
            var added = await env.Add(extra: new { priority = "Critical" });
            var id = S(added["id"])!;
            Check.Equal("critical", S(added["priority"]), "checked and normalized");
            await Check.ThrowsAsync<RpcException>(() => env.Add(extra: new { priority = "urgent" }));
            await Check.ThrowsAsync<RpcException>(() => env.Ctx.RpcFake.InvokeAsync("webhooks.update", new { id, priority = "asap" }));
            Check.Equal(202, (await env.Post(id, S(added["token"])!, Alert())).Status);
            Check.Equal("critical", env.Ctx.SessionsFake.Sessions.Single().Meta?[RunPriority.MetaKey]?.GetValue<string>());
            var lowered = (await env.Ctx.RpcFake.CallAsync("webhooks.update", new { id, priority = "low" }))!;
            Check.Equal("low", S(lowered["priority"]));
            var raised = (await env.Ctx.RpcFake.CallAsync("webhooks.update", new { id, priority = "critical", proposedBy = "session:ses_1" }))!;
            Check.Equal(false, raised["enabled"]!.GetValue<bool>(), "an agent changing its priority pauses it as a proposal");
        });

        r.Add("webhooks: openInTab marks the chats a webhook starts; the option is stored on add and set or cleared on update", async () =>
        {
            var env = await new Env().StartAsync();
            var added = await env.Add(extra: new { openInTab = true });
            Check.Equal(true, added["openInTab"]!.GetValue<bool>());
            var id = S(added["id"])!;
            Check.Equal(202, (await env.Post(id, S(added["token"])!, Alert())).Status);
            Check.Equal(true, env.Ctx.SessionsFake.Sessions.Single().Meta?["openInTab"]?.GetValue<bool>());

            // a webhook without the option: its doc stores false, and the chat it starts carries no such key
            var plain = await env.Add(extra: new { name = "plain" });
            Check.Equal(false, plain["openInTab"]!.GetValue<bool>(), "default off");
            await env.Post(S(plain["id"])!, S(plain["token"])!, Alert());
            Check.True(env.Ctx.SessionsFake.Sessions[^1].Meta?["openInTab"] is null, "no key: the app windows open no tab");
            Check.True(env.Ctx.SessionsFake.Sessions[^1].Meta?["webhook"] is not null, "(it is still a webhook chat)");

            // an update sets the flag (and the next chat carries it), clears it again, and an omitted one is never cleared by accident
            var set = (await env.Ctx.RpcFake.CallAsync("webhooks.update", new { id = S(plain["id"]), openInTab = true }))!;
            Check.Equal(true, set["openInTab"]!.GetValue<bool>());
            await env.Post(S(plain["id"])!, S(plain["token"])!, Alert("after"));
            Check.Equal(true, env.Ctx.SessionsFake.Sessions[^1].Meta?["openInTab"]?.GetValue<bool>());
            var cleared = (await env.Ctx.RpcFake.CallAsync("webhooks.update", new { id = S(plain["id"]), openInTab = false }))!;
            Check.Equal(false, cleared["openInTab"]!.GetValue<bool>(), "an explicit false clears it");
            var untouched = (await env.Ctx.RpcFake.CallAsync("webhooks.update", new { id = S(plain["id"]), prompt = "same {{host}}" }))!;
            Check.Equal(false, untouched["openInTab"]!.GetValue<bool>(), "an update without it leaves it alone");
        });

        r.Add("webhooks: an ungrouped payload becomes a chat at once, on the project, sent as the system", async () =>
        {
            var env = await new Env().StartAsync();
            var added = await env.Add(extra: new { pre = WebhookHooksTests.Hook(new WebhookHooksTests.Sink(), 0, "--adapt:title") });
            var (status, body) = await env.Post(S(added["id"])!, S(added["token"])!, Alert(output: "disk \"/\" is 91 %"));
            Check.Equal(202, status);
            Check.Equal("started", S(body["status"]));
            var session = env.Ctx.SessionsFake.Sessions.Single();
            Check.Equal(env.Project.Id, session.ProjectId);
            Check.Equal("Webhook: nuc / Kernel errors is CRIT", session.Title);
            Check.Equal(S(added["id"]), session.Meta?["webhook"]?.GetValue<string>());
            Check.Equal("normal", S(added["priority"]), "a webhook's chats run at normal unless it says otherwise");
            Check.Equal("normal", session.Meta?[RunPriority.MetaKey]?.GetValue<string>(), "the chat carries its level: one started for the user");
            var sent = env.Runtime.Sent.Single();
            Check.Equal("system", sent.Input.Source);
            Check.Equal("Alert: Kernel errors on nuc is CRIT. disk \"/\" is 91 %", sent.Input.Text);
            var runs = new JsonArray([.. (await env.History(S(added["id"])!)).Where(h => S(h!["status"]) != "hook").Select(h => h!.DeepClone())]);   // (the Before command has its own rows)
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

        r.Add("webhooks: the Before command can skip a payload; no command, a failing one or silence skips nothing", async () =>
        {
            var env = await new Env().StartAsync();
            var sink = new WebhookHooksTests.Sink();
            var added = await env.Add(extra: new { pre = WebhookHooksTests.Hook(sink, 0, "--adapt:skipok") });
            var id = S(added["id"])!; var token = S(added["token"])!;

            Check.Equal("started", S((await env.Post(id, token, Alert())).Body["status"]));
            var recovered = await env.Post(id, token, Alert(state: "OK"));
            Check.Equal(200, recovered.Status);
            Check.Equal("skipped", S(recovered.Body["status"]));
            Check.Contains(S(recovered.Body["reason"]), "Before command skipped it");
            Check.Equal(1, env.Ctx.SessionsFake.Sessions.Count, "the skip left no chat");
            Check.Equal(2, sink.Count, "the command ran for both payloads");

            // null Before command = skip false: it runs; a command that fails or says nothing useful does not swallow the payload either
            var none = await env.Add(extra: new { name = "none" });
            Check.Equal("started", S((await env.Post(S(none["id"])!, S(none["token"])!, Alert(state: "OK"))).Body["status"]), "no command: every payload runs");
            var failing = await env.Add(extra: new { name = "failing", pre = WebhookHooksTests.Hook(sink, 3, "--adapt:skipok") });
            Check.Equal("started", S((await env.Post(S(failing["id"])!, S(failing["token"])!, Alert(state: "OK"))).Body["status"]), "a failing command: fail open");
            var garbage = await env.Add(extra: new { name = "garbage", pre = WebhookHooksTests.Hook(sink, 0, "--say=this is not JSON") });
            Check.Equal("started", S((await env.Post(S(garbage["id"])!, S(garbage["token"])!, Alert(state: "OK"))).Body["status"]), "output that is not JSON: the defaults");
            var broken = await env.Add(extra: new { name = "broken", pre = WebhookHooksTests.Hook(sink, 0, "--say={\"skip\": tru") });
            Check.Equal("started", S((await env.Post(S(broken["id"])!, S(broken["token"])!, Alert(state: "OK"))).Body["status"]), "broken JSON: the defaults");
            var wrongTypes = await env.Add(extra: new { name = "types", pre = WebhookHooksTests.Hook(sink, 0, "--say={\"skip\": \"yes\", \"key\": 7, \"start\": 1}") });
            var typed = await env.Post(S(wrongTypes["id"])!, S(wrongTypes["token"])!, Alert(state: "OK"));
            Check.Equal("started", S(typed.Body["status"]), "a flag that is not a boolean is ignored; a number is a fine key");
        });

        r.Add("webhooks: a storm on one host is one chat — the first alert starts it at once and the rest are appended, nothing is held back", async () =>
        {
            var env = await new Env().StartAsync();
            var added = await env.AddGrouped();
            var id = S(added["id"])!; var token = S(added["token"])!;

            var first = await env.Post(id, token, Alert("Kernel errors", "nuc"));
            Check.Equal(202, first.Status);
            Check.Equal("started", S(first.Body["status"]), "a new conversation's chat starts at once: nothing is held back to collect more");
            Check.Equal(1, env.Ctx.SessionsFake.Sessions.Count);
            Check.Contains(env.Runtime.Sent.Single().Input.Text, "Incident nuc, 1 alert(s)");
            env.Time.Advance(TimeSpan.FromSeconds(30));
            Check.Equal("appended", S((await env.Post(id, token, Alert("Container radio", "nuc", output: "Exited (1)"))).Body["status"]), "same host: appended to its chat");
            Check.Equal("appended", S((await env.Post(id, token, Alert("Container yue2", "nuc", output: "Exited (1)"))).Body["status"]));
            Check.Equal("started", S((await env.Post(id, token, Alert("Disk", "server"))).Body["status"]), "another host: a conversation of its own, started at once");
            Check.Equal("duplicate", S((await env.Post(id, token, Alert("Container yue2", "nuc", output: "Exited (1)"))).Body["status"]), "the very same alert again is a duplicate");
            Check.Equal(2, env.Ctx.SessionsFake.Sessions.Count, "one chat per host");
            Check.Equal(0, await env.Tick(), "nothing waits, so a tick starts nothing");
            Check.True((await env.Incidents(id)).All(i => S(i!["state"]) == "open"), "no conversation is waiting");

            var nuc = env.Ctx.SessionsFake.Sessions.First();
            Check.Equal("Webhook: nuc / Kernel errors is CRIT", nuc.Title);
            Check.Contains(env.Runtime.Sent[1].Input.Text, "More arrived for this conversation (nuc)", "the second alert went to the same chat");
            var history = (await env.History(id)).Select(h => S(h!["status"])).Where(s => s != "hook").ToList();   // (the Before command has its own rows)
            Check.Equal("appended:2,duplicate:1,started:2", string.Join(",", history.GroupBy(x => x).OrderBy(g => g.Key).Select(g => $"{g.Key}:{g.Count()}")), "no waiting or held rows");
        });

        r.Add("webhooks: the loop's watch list is a query, not a scan — a backlog of open, unwatched incidents cannot push the awaited ones out of the window", async () =>
        {
            var env = await new Env().StartAsync();
            var added = await env.AddGrouped();
            var id = S(added["id"])!;
            var store = env.Plugin.Store!;
            // 501 open incidents whose chats are not watched (finished long ago), then two that are
            for (var i = 0; i < 501; i++)
                store.PutIncident(new JsonObject
                {
                    ["webhookId"] = id, ["key"] = $"host-{i}", ["state"] = "open", ["awaiting"] = false,
                    ["lastAtMs"] = env.Time.Now.AddMinutes(i).ToUnixTimeMilliseconds(),
                });
            for (var i = 0; i < 2; i++)
                store.PutIncident(new JsonObject
                {
                    ["webhookId"] = id, ["key"] = $"watched-{i}", ["state"] = "open", ["awaiting"] = true,
                    ["lastAtMs"] = env.Time.Now.AddMinutes(600 + i).ToUnixTimeMilliseconds(),
                });
            var awaiting = store.AwaitingIncidents();
            Check.Equal(2, awaiting.Count, "only the ones whose chats are being watched, past the 500 open ones");
            Check.True(awaiting.All(d => S(d["key"])!.StartsWith("watched-")));
            Check.True(store.AnyAwaiting());
            // an open incident an older version wrote without the flag is not watched either
            store.PutIncident(new JsonObject { ["webhookId"] = id, ["key"] = "old", ["state"] = "open", ["lastAtMs"] = 1 });
            Check.Equal(2, store.AwaitingIncidents().Count);
        });

        r.Add("webhooks: a webhook document an older version wrote with a debounce group still starts its chat at once", async () =>
        {
            var env = await new Env().StartAsync();
            var added = await env.AddGrouped();
            var id = S(added["id"])!; var token = S(added["token"])!;
            var doc = env.Plugin.Store!.Get(id)!;
            doc["group"] = new JsonObject { ["key"] = "host", ["debounceSeconds"] = 600, ["reopenMinutes"] = 240 };
            env.Plugin.Store!.Put(doc);

            Check.Equal("started", S((await env.Post(id, token, Alert())).Body["status"]), "the stored debounce is ignored");
            Check.Equal(1, env.Ctx.SessionsFake.Sessions.Count);
            Check.Equal(0, await env.Tick(), "nothing waits for a window");
        });

        r.Add("webhooks: later alerts of an open incident are appended to its chat; recoveries reach it; repeats are duplicates", async () =>
        {
            var env = await new Env().StartAsync();
            var added = await env.AddGrouped();
            var id = S(added["id"])!; var token = S(added["token"])!;
            await env.Post(id, token, Alert("Kernel errors"));
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
            Check.Contains(env.Runtime.Sent[1].Input.Text, "More arrived for this conversation (nuc)");
            Check.Contains(env.Runtime.Sent[1].Input.Text, "Container radio");
            Check.Equal("system", env.Runtime.Sent[1].Input.Source);

            var repeat = await env.Post(id, token, Alert("Container radio", output: "Exited (1)"));
            Check.Equal(200, repeat.Status);
            Check.Equal("duplicate", S(repeat.Body["status"]), "the same alert again, just now");
            Check.Equal(2, env.Runtime.Sent.Count, "a repeat does not wake the chat");

            var recovery = await env.Post(id, token, Alert("Container radio", state: "OK", output: "Up 2 minutes"));
            Check.Equal("appended", S(recovery.Body["status"]), "a recovery may not start a chat, but the conversation is open: it joins");
            Check.Contains(env.Runtime.Sent[2].Input.Text, "radio is OK");

            var stranger = await env.Post(id, token, Alert("Disk", host: "server", state: "OK"));
            Check.Equal("skipped", S(stranger.Body["status"]), "a recovery with no open incident starts nothing");
            Check.Contains(S(stranger.Body["reason"]), "no open conversation");
            Check.Equal(1, env.Ctx.SessionsFake.Sessions.Count);

            var other = await env.Post(id, token, Alert("Disk", host: "server"));
            Check.Equal("started", S(other.Body["status"]), "another host opens its own incident");
            // (rows made in the same millisecond have no defined order: count them)
            var rows = (await env.History(id)).Select(h => S(h!["status"])).Where(s => s != "hook").ToList();
            Check.Equal("appended:2,duplicate:1,skipped:1,started:2", string.Join(",", rows.GroupBy(x => x).OrderBy(g => g.Key).Select(g => $"{g.Key}:{g.Count()}")));
        });

        r.Add("webhooks: a finished chat is reopened for four hours; after that, or when the chat is gone, the alert opens a new incident", async () =>
        {
            var env = await new Env().StartAsync();
            var added = await env.AddGrouped();
            var id = S(added["id"])!; var token = S(added["token"])!;
            await env.Post(id, token, Alert("Kernel errors"));
            env.FinishChats();

            env.Time.Advance(TimeSpan.FromMinutes(30));
            Check.Equal("appended", S((await env.Post(id, token, Alert("Container radio"))).Body["status"]), "finished 30 min ago: the same chat wakes up");
            Check.Equal(1, env.Ctx.SessionsFake.Sessions.Count);
            env.FinishChats();   // the woken chat finishes again

            env.Time.Advance(TimeSpan.FromMinutes(241));
            var late = await env.Post(id, token, Alert("Container yue2"));
            Check.Equal("started", S(late.Body["status"]), "four hours after the last activity it is a new conversation, and its chat starts at once");
            Check.Equal(2, env.Ctx.SessionsFake.Sessions.Count);

            env.Ctx.SessionsFake.Sessions.Clear();   // the user deleted the chat
            var orphan = await env.Post(id, token, Alert("Disk"));
            Check.Equal("started", S(orphan.Body["status"]), "the chat is gone: a new conversation, not a lost alert");
        });

        r.Add("webhooks: without a Before command every payload is its own chat at once, and nothing waits", async () =>
        {
            var env = await new Env().StartAsync();
            var added = await env.Add();
            var first = await env.Post(S(added["id"])!, S(added["token"])!, Alert());
            Check.Equal("started", S(first.Body["status"]));
            var second = await env.Post(S(added["id"])!, S(added["token"])!, Alert());
            Check.Equal("started", S(second.Body["status"]), "no key, so no conversation to join: even the same payload is a new chat");
            Check.Equal(2, env.Ctx.SessionsFake.Sessions.Count);
        });

        r.Add("webhooks: a chat that cannot start waits with its alerts and is tried again for 30 minutes instead of being dropped — then each alert is told it failed", async () =>
        {
            var env = await new Env().StartAsync();
            env.Agents(("local", false, true));   // the only free agent is not loaded
            var added = await env.AddGrouped(extra: new { agent = "local" });
            var id = S(added["id"])!; var token = S(added["token"])!;
            var waiting = await env.Post(id, token, Alert("Kernel errors"));
            Check.Equal(202, waiting.Status, "accepted: it waits for its chat");
            Check.Equal("waiting", S(waiting.Body["status"]));
            Check.Contains(S(waiting.Body["reason"]), "unavailable");
            var second = await env.Post(id, token, Alert("Container radio"));
            Check.Equal("waiting", S(second.Body["status"]), "the same conversation: the payload waits with the first");
            var dry = (await env.Ctx.RpcFake.CallAsync("webhooks.test", new { id, payload = Alert("Disk"), adapt = new { key = "nuc" } }))!;
            Check.Equal("join", S(dry["action"]), "a dry run says it would wait with them too");
            Check.Equal(0, env.Ctx.SessionsFake.Sessions.Count);
            var inc = (await env.Incidents(id)).Single();
            Check.Equal("waiting", S(inc!["state"]));
            Check.Contains(S(inc["waiting"]), "unavailable");
            Check.Equal(2, ((JsonArray)(await env.History(id))).Count(h => S(h!["status"]) == "waiting"), "nothing is lost meanwhile");

            env.Time.Advance(TimeSpan.FromMinutes(5));
            Check.Equal(0, await env.Tick(), "still unavailable: tried again, nothing starts");
            env.Agents(("local", true, true));   // the model loaded
            env.Time.Advance(TimeSpan.FromSeconds(31));   // the next try is 30 s after the last
            Check.Equal(1, await env.Tick(), "the next try starts it");
            Check.Equal(1, env.Runtime.Sent.Count);
            Check.Contains(env.Runtime.Sent.Single().Input.Text, "Container radio", "with both alerts");

            // a webhook whose agent never comes back
            var env2 = await new Env().StartAsync();
            env2.Agents(("local", false, true));
            var added2 = await env2.AddGrouped(extra: new { agent = "local" });
            Check.Equal("waiting", S((await env2.Post(S(added2["id"])!, S(added2["token"])!, Alert("Kernel errors"))).Body["status"]));
            for (var i = 0; i < 70; i++) { env2.Time.Advance(TimeSpan.FromSeconds(30)); await env2.Tick(); }
            var rows = (await env2.History(S(added2["id"])!)).Select(h => S(h!["status"])).ToList();
            Check.True(rows.Contains("failed"), "after 30 minutes the alert is reported failed");
            Check.Equal(0, (await env2.Incidents(S(added2["id"])!)).Count, "and the incident is closed");
        });

        r.Add("webhooks: the history prune keeps every start inside the fuse's hour, so a fuse above the history size can still fire", async () =>
        {
            var env = await new Env().StartAsync();
            var store = env.Plugin.Store!;
            var now = DateTimeOffset.UtcNow;
            var n = WebhookStore.HistoryPerWebhook + 50;
            for (var i = 0; i < n; i++) store.Record("w1", now.AddSeconds(-i * 0.01), WebhookStore.Started, null, "s" + i, null, null, null);
            Check.Equal(n, store.StartedSince("w1", now.AddHours(-1)), "all starts of the hour are still counted");
            store.Record("w1", now.AddHours(-3), WebhookStore.Started, null, "old", null, null, null);
            store.Record("w1", now, WebhookStore.Skipped, null, null, null, null, null);
            Check.Equal(n, store.StartedSince("w1", now.AddHours(-4)), "a start older than the hour is pruned past the history size");
        });

        r.Add("webhooks: the safety fuse holds new chats back until the hour has room; it does not drop them", async () =>
        {
            var env = await new Env().StartAsync();
            var added = await env.AddGrouped(extra: new { maxChatsPerHour = 1 });
            var id = S(added["id"])!; var token = S(added["token"])!;
            Check.Equal("started", S((await env.Post(id, token, Alert("a", host: "nuc"))).Body["status"]), "one chat fits the fuse");
            var b = await env.Post(id, token, Alert("b", host: "server"));
            Check.Equal("waiting", S(b.Body["status"]), "the other conversation waits: the fuse is full");
            Check.Equal(1, env.Ctx.SessionsFake.Sessions.Count);
            env.Time.Advance(TimeSpan.FromMinutes(30));
            Check.Equal(0, await env.Tick(), "still inside the hour");
            Check.Equal(1, (await env.Incidents(id)).Count(i => S(i!["state"]) == "waiting"), "the other waits — the fuse paces, it does not expire");
            Check.Contains(S((await env.Incidents(id)).First(i => S(i!["state"]) == "waiting")!["waiting"]), "safety fuse");
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

        r.Add("webhooks: a model that works with its own tools (the Claude CLI) is refused at once, not retried: nobody is there to hold what it runs", async () =>
        {
            var env = await new Env().StartAsync();
            var added = await env.Add();
            env.Ctx.ModelsFake.Models.Add(new ModelInfo { Provider = "fake", Id = "cli", RunsOwnTools = true });
            env.Ctx.ModelsFake.DefaultModelRef = "fake/cli";
            var refused = await env.Post(S(added["id"])!, S(added["token"])!, Alert());
            Check.Equal("failed", S(refused.Body["status"]));
            Check.Contains(S(refused.Body["reason"]), "works with its own tools");
            Check.Equal(0, env.Runtime.Sent.Count, "nothing was sent to the runtime");
            Check.Equal(0, env.Ctx.SessionsFake.Sessions.Count, "and the chat is gone");
        });

        r.Add("webhooks: a start cancelled by the caller is reported, not swallowed, and leaves no empty chat behind; a chat that already runs is kept and counts as started", async () =>
        {
            var env = await new Env().StartAsync();
            var added = await env.Add();
            using var cts = new CancellationTokenSource();
            env.Ctx.Rpc.Register("profiles.applyAuto", (_, _) => { cts.Cancel(); throw new OperationCanceledException(cts.Token); });
            var (status, body) = await env.Plugin.ReceiveAsync("/hook/" + S(added["id"]), "POST", IPAddress.Parse("127.0.0.1"), "application/json",
                max => Task.FromResult<string?>(JsonSerializer.Serialize(Alert())), S(added["token"])!, cts.Token);
            Check.Equal("failed", S(body["status"]), "an ungrouped alert has no conversation to wait under: it is told at once");
            Check.Contains(S(body["reason"]), "cancelled");
            Check.Equal(0, env.Ctx.SessionsFake.Sessions.Count, "the half-made chat is deleted, or every retry would add another empty one");
            Check.Equal(0, env.Runtime.Sent.Count, "and nothing was sent");

            // the run exists by the time the cancel lands: the chat stays and the alert counts as started
            var running = await env.Add(extra: new { name = "running" });
            using var cts2 = new CancellationTokenSource();
            env.Ctx.Rpc.Register("profiles.applyAuto", (req, _) =>
            {
                env.Runtime.Agents.Add(new AgentInfo { Id = "agt_x", SessionId = req.Str("sessionId")!, Status = AgentStatus.Running });
                cts2.Cancel();
                throw new OperationCanceledException(cts2.Token);
            });
            var (_, body2) = await env.Plugin.ReceiveAsync("/hook/" + S(running["id"]), "POST", IPAddress.Parse("127.0.0.1"), "application/json",
                max => Task.FromResult<string?>(JsonSerializer.Serialize(Alert("b"))), S(running["token"])!, cts2.Token);
            Check.Equal("started", S(body2["status"]), "a chat that runs is the incident's chat");
            Check.Equal(1, env.Ctx.SessionsFake.Sessions.Count, "and is not deleted");
        });

        r.Add("webhooks: a webhook's own profile is applied to its chats (the automatic one has no ssh); unknown ones are refused; a deleted one fails clearly", async () =>
        {
            var env = await new Env().StartAsync();
            var profiles = new JsonArray(new JsonObject { ["id"] = "coder" }, new JsonObject { ["id"] = "sysadmin" });
            var applied = new List<string>();
            env.Ctx.Rpc.Register("profiles.list", (_, _) => Task.FromResult<object?>(new JsonObject { ["defaultProfile"] = "coder", ["profiles"] = profiles.DeepClone() }));
            env.Ctx.Rpc.Register("profiles.apply", (req, _) => { applied.Add("apply:" + req.Str("profile")); return Task.FromResult<object?>(null); });
            env.Ctx.Rpc.Register("profiles.applyAuto", (_, _) => { applied.Add("auto"); return Task.FromResult<object?>(null); });

            var plain = await env.Add(extra: new { name = "plain" });
            var named = await env.Add(extra: new { name = "named", profile = "SysAdmin" });
            Check.Equal("sysadmin", S(named["profile"]), "stored with the id's own spelling");
            await env.Post(S(plain["id"])!, S(plain["token"])!, Alert());
            await env.Post(S(named["id"])!, S(named["token"])!, Alert());
            Check.Equal("auto,apply:sysadmin", string.Join(",", applied), "no profile: the automatic one; a named one: exactly that");

            var ex = await Check.ThrowsAsync<RpcException>(() => env.Add(extra: new { profile = "nonexistent" }));
            Check.Contains(ex.Message, "Profile nonexistent not found");
            var cleared = (await env.Ctx.RpcFake.CallAsync("webhooks.update", new { id = S(named["id"]), profile = "" }))!;
            Check.True(cleared["profile"] is null, "an empty profile goes back to the automatic one");

            await env.Ctx.RpcFake.CallAsync("webhooks.update", new { id = S(named["id"]), profile = "sysadmin" });
            profiles.Clear();
            profiles.Add(new JsonObject { ["id"] = "coder" });   // the user deleted the profile
            var gone = await env.Post(S(named["id"])!, S(named["token"])!, Alert("later"));
            Check.Equal("failed", S(gone.Body["status"]));
            Check.Contains(S(gone.Body["reason"]), "profile sysadmin no longer exists");
            Check.Equal(2, env.Ctx.SessionsFake.Sessions.Count, "and no chat was started with the wrong tools");
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

            for (var i = 0; i < WebhooksPlugin.FailureLimit; i++) await env.Post(id, "whk_guess" + i, Alert(), from: "10.1.2.3");
            Check.Equal(429, (await env.Post(id, token, Alert("d"), from: "10.1.2.3")).Status, "a guessing address is refused even with the right token");
            Check.Equal(202, (await env.Post(id, token, Alert("e"), from: "192.168.1.9")).Status, "another address is not affected");
            env.Time.Advance(WebhooksPlugin.FailureWindow + TimeSpan.FromSeconds(1));
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

            // a webhook disabled while its conversation's chat waits to start: the waiting alerts are reported, not lost
            env.Agents(("local", false, true));
            var g = await env.AddGrouped(extra: new { agent = "local" });
            Check.Equal("waiting", S((await env.Post(S(g["id"])!, S(g["token"])!, Alert())).Body["status"]));
            await env.Ctx.RpcFake.CallAsync("webhooks.update", new { id = S(g["id"]), enabled = false });
            env.Time.Advance(TimeSpan.FromSeconds(31));
            var chatsBefore = env.Ctx.SessionsFake.Sessions.Count;
            await env.Tick();
            Check.Equal(chatsBefore, env.Ctx.SessionsFake.Sessions.Count, "no chat started for it");
            Check.True((await env.History(S(g["id"])!)).Any(h => S(h!["reason"])?.Contains("disabled before its chat started") == true), "its waiting alert is reported, not lost");
        });

        r.Add("webhooks: update changes the prompt, timeout and fuse; bad values are refused", async () =>
        {
            var env = await new Env().StartAsync();
            var added = await env.Add();
            var id = S(added["id"])!;
            Check.Equal(10, added["timeoutMinutes"]!.GetValue<int>(), "the default timeout");
            var changed = (await env.Ctx.RpcFake.CallAsync("webhooks.update", new { id, timeoutMinutes = 25, maxChatsPerHour = 5, prompt = "new {{host}}" }))!;
            Check.Equal(25, changed["timeoutMinutes"]!.GetValue<int>());
            Check.Equal(5, changed["maxChatsPerHour"]!.GetValue<int>());
            Check.Equal("new {{host}}", S(changed["prompt"]));
            Check.Equal(0, (await env.Ctx.RpcFake.CallAsync("webhooks.update", new { id, timeoutMinutes = 0 }))!["timeoutMinutes"]!.GetValue<int>(), "0 = never");
            await Check.ThrowsAsync<RpcException>(() => env.Ctx.RpcFake.InvokeAsync("webhooks.update", new { id, timeoutMinutes = 99999 }));
            await Check.ThrowsAsync<RpcException>(() => env.Ctx.RpcFake.InvokeAsync("webhooks.update", new { id, maxChatsPerHour = -1 }));
            await Check.ThrowsAsync<RpcException>(() => env.Ctx.RpcFake.InvokeAsync("webhooks.update", new { id, prompt = "  " }));
        });

        r.Add("webhooks: test shows what a payload would do - start, join, append or skip - without storing anything or running the Before command", async () =>
        {
            var env = await new Env().StartAsync();
            var added = await env.AddGrouped();
            var id = S(added["id"])!; var token = S(added["token"])!;
            async Task<JsonNode> Test(object payload, object? adapt = null) => (await env.Ctx.RpcFake.CallAsync("webhooks.test", new { id, payload, adapt }))!;
            var nuc = new { key = "nuc", text = "Kernel errors is CRIT (1 in last 30 min)" };

            Check.Equal("start", S((await Test(Alert()))["action"]), "without the Before command's answer: no key, so a chat of its own");
            var start = await Test(Alert(), nuc);
            Check.Equal("start", S(start["action"]), "a new conversation's chat would start at once");
            Check.Contains(S(start["note"]), "start the conversation “nuc” now");
            Check.Contains(S(start["prompt"]), "Incident nuc, 1 alert(s)");
            Check.Contains(S(start["prompt"]), "Kernel errors is CRIT (1 in last 30 min)");
            Check.Equal("skip", S((await Test(Alert(state: "OK"), new { key = "nuc", skip = true }))["action"]));
            Check.Equal("skip", S((await Test(Alert(state: "OK"), new { key = "nuc", start = false }))["action"]), "a recovery with nothing open");
            Check.Equal(0, (await env.Incidents(id)).Count, "a test stores no incident");
            Check.Equal(0, ((JsonArray)(await env.History(id))).Count, "no delivery, and the Before command did not run");

            await env.Post(id, token, Alert("a"));
            var append = await Test(Alert("c"), nuc);
            Check.Equal("append", S(append["action"]));
            Check.Contains(S(append["prompt"]), "More arrived for this conversation");
            Check.Equal("append", S((await Test(Alert("d", state: "OK"), new { key = "nuc", start = false }))["action"]), "a recovery would be appended to the open conversation");

            var ran = (await env.Ctx.RpcFake.CallAsync("webhooks.test", new { id, payload = Alert("e", host: "server"), run = true }))!;
            Check.Equal("started", S(ran["status"]));
            Check.True((await env.History(id)).Any(h => S(h!["from"]) == "test"), "a test run is marked as such");
            await env.Ctx.RpcFake.CallAsync("webhooks.delete", new { id });
            Check.Equal(0, ((JsonArray)(await env.History(id))).Count);
            Check.Equal(0, (await env.Incidents(id)).Count, "delete removes the incidents too");
            await Check.ThrowsAsync<RpcException>(() => env.Ctx.RpcFake.InvokeAsync("webhooks.get", new { id }));
        });

        r.Add("webhooks: hooks, runs and waiting incidents survive a restart; the waiting alerts start their chat afterwards", async () =>
        {
            var env = await new Env().StartAsync();
            env.Agents(("local", false, true));
            var added = await env.AddGrouped(extra: new { agent = "local" });
            await env.Post(S(added["id"])!, S(added["token"])!, Alert("Kernel errors"));
            await env.Post(S(added["id"])!, S(added["token"])!, Alert("Container radio"));
            env.Ctx.Unload();
            var plugin2 = new WebhooksPlugin(env.Time, listen: false, loop: false);
            var ctx2 = new FakePluginContext(env.Ctx.Paths.Home, "netpi.webhooks");
            ctx2.ServicesFake.Register<IAgentRuntime>(env.Runtime);
            ctx2.SessionsFake.Projects.AddRange(env.Ctx.SessionsFake.Projects);
            await plugin2.StartAsync(ctx2, CancellationToken.None);
            Check.Equal(1, ((JsonArray)(await ctx2.RpcFake.CallAsync("webhooks.list", new { }))!).Count);
            Check.Equal(1, ((JsonArray)(await ctx2.RpcFake.CallAsync("webhooks.incidents", new { }))!).Count, "the waiting incident is still there");
            env.Time.Advance(TimeSpan.FromSeconds(31));
            Check.Equal(1, await plugin2.Runner!.TickAsync(CancellationToken.None));
            Check.Contains(env.Runtime.Sent.Single().Input.Text, "Container radio", "both alerts, waiting before the restart");
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
            Check.Contains(test.Content, "start a chat");
            Check.Equal(0, env.Ctx.SessionsFake.Sessions.Count);
            await env.Post(id, S(added["token"])!, Alert());
            Check.Contains((await Call(new { action = "history", id })).Content, "started");
            foreach (var refused in new[] { "add", "update", "enable", "delete", "rotate" })
                Check.True((await Call(new { action = refused, id })).IsError, refused + " is the user's");
            var paused = await Call(new { action = "pause", id });
            Check.False(paused.IsError);
            Check.Equal(false, (await env.Get(id))["enabled"]!.GetValue<bool>());
        });

        r.Add("webhooks: a receiver that cannot bind is logged, and a stop waits for the reconciles it started", async () =>
        {
            var env = await new Env(listen: true).StartAsync();
            env.Ctx.SettingsFake.SetQuiet("webhooks.bind", "999.999.999.999");
            env.Ctx.SettingsFake.SetQuiet("webhooks.enabled", true);
            env.Ctx.Events.Publish("settings.changed", new JsonObject { ["path"] = "webhooks.bind" });
            await WaitFor(() => env.Plugin.Status()["error"] is not null);
            Check.Contains(env.Plugin.Status()["error"]!.GetValue<string>(), "not an IP");
            Check.True(env.Ctx.Log.Lines.Any(l => l.Contains("not an IP address")), "the failed bind is logged");

            // a settings change at the moment of the stop starts one more reconcile: the stop waits for it, bounded
            env.Ctx.Events.Publish("settings.changed", new JsonObject { ["path"] = "webhooks.port" });
            var stopping = System.Diagnostics.Stopwatch.StartNew();
            await env.Plugin.StopAsync(CancellationToken.None);
            Check.True(stopping.Elapsed < TimeSpan.FromSeconds(10), "the stop did not hang on a reconcile: " + stopping.Elapsed);
            Check.Equal(false, env.Plugin.Status()["listening"]!.GetValue<bool>());
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
