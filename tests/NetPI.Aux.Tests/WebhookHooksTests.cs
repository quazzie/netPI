using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using NetPI.Webhooks;

namespace NetPI.Aux.Tests;

/// <summary>
/// Webhook hooks: actions NetPI runs itself at fixed points (received, started, finished, asking, stalled, failed), gated by conditions over the
/// alert, the agent's verdict and its answer, fired once per incident when asked, and failing open: an agent that says nothing, dies or stalls does
/// not suppress the notification. A fake MQTT broker and a fake HTTP server show exactly what was sent.
/// </summary>
public static class WebhookHooksTests
{
    /// <summary>A broker that accepts one connection at a time, answers CONNECT and records every PUBLISH.</summary>
    internal sealed class FakeBroker : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
        public ConcurrentQueue<(string Topic, string Payload, bool Retain)> Published { get; } = new();
        public ConcurrentQueue<string> Users { get; } = new();
        public byte ConnackCode { get; set; }

        public FakeBroker()
        {
            _listener.Start();
            _ = Task.Run(async () =>
            {
                while (true)
                {
                    TcpClient client;
                    try { client = await _listener.AcceptTcpClientAsync(); } catch { return; }
                    _ = Task.Run(() => Serve(client));
                }
            });
        }

        private async Task Serve(TcpClient client)
        {
            using var _ = client;
            var stream = client.GetStream();
            try
            {
                while (true)
                {
                    var header = new byte[1];
                    if (await stream.ReadAsync(header) == 0) return;
                    var length = 0; var shift = 0;
                    while (true)
                    {
                        var b = new byte[1];
                        await stream.ReadExactlyAsync(b);
                        length |= (b[0] & 0x7F) << shift;
                        if ((b[0] & 0x80) == 0) break;
                        shift += 7;
                    }
                    var body = new byte[length];
                    await stream.ReadExactlyAsync(body);
                    switch (header[0] >> 4)
                    {
                        case 1:   // CONNECT: name(2+4) level flags keepalive(2), client id, user, password
                        {
                            var flags = body[7];
                            var i = 10;
                            var idLen = (body[i] << 8) | body[i + 1]; i += 2 + idLen;
                            if ((flags & 0x80) != 0) { var ul = (body[i] << 8) | body[i + 1]; Users.Enqueue(Encoding.UTF8.GetString(body, i + 2, ul)); }
                            await stream.WriteAsync(new byte[] { 0x20, 0x02, 0x00, ConnackCode });
                            break;
                        }
                        case 3:   // PUBLISH (QoS 0)
                        {
                            var tl = (body[0] << 8) | body[1];
                            Published.Enqueue((Encoding.UTF8.GetString(body, 2, tl), Encoding.UTF8.GetString(body, 2 + tl, body.Length - 2 - tl), (header[0] & 1) != 0));
                            break;
                        }
                        case 14: return;
                    }
                }
            }
            catch { /* the client went away */ }
        }

        public void Dispose() => _listener.Stop();
    }

    /// <summary>A server that answers every request with a status and records method, path, headers and body.</summary>
    internal sealed class FakeHttp : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
        public int Status { get; set; } = 200;
        public ConcurrentQueue<(string Method, string Path, Dictionary<string, string> Headers, string Body)> Requests { get; } = new();

        public FakeHttp()
        {
            _listener.Start();
            _ = Task.Run(async () =>
            {
                while (true)
                {
                    TcpClient client;
                    try { client = await _listener.AcceptTcpClientAsync(); } catch { return; }
                    _ = Task.Run(async () =>
                    {
                        using var c = client;
                        var stream = c.GetStream();
                        var buffer = new MemoryStream();
                        var one = new byte[1];
                        while (true)
                        {
                            if (await stream.ReadAsync(one) == 0) return;
                            buffer.WriteByte(one[0]);
                            var t = Encoding.ASCII.GetString(buffer.ToArray());
                            if (t.EndsWith("\r\n\r\n")) break;
                        }
                        var head = Encoding.ASCII.GetString(buffer.ToArray()).Split("\r\n");
                        var first = head[0].Split(' ');
                        var headers = head.Skip(1).Where(l => l.Contains(':')).ToDictionary(l => l[..l.IndexOf(':')].Trim().ToLowerInvariant(), l => l[(l.IndexOf(':') + 1)..].Trim());
                        var len = headers.TryGetValue("content-length", out var cl) ? int.Parse(cl) : 0;
                        var body = new byte[len];
                        if (len > 0) await stream.ReadExactlyAsync(body);
                        Requests.Enqueue((first[0], first[1], headers, Encoding.UTF8.GetString(body)));
                        await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {Status} X\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok"));
                    });
                }
            });
        }

        public void Dispose() => _listener.Stop();
    }

    private static string? S(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static object Alert(string service = "Kernel errors", string host = "nuc", string state = "CRIT", string output = "1 in last 30 min") =>
        new { host, service, state, output };

    private static object Mqtt(string name, string on, object? when, object json, string? once = null, int? afterMinutes = null)
    {
        var h = new JsonObject
        {
            ["name"] = name, ["on"] = on,
            ["do"] = new JsonObject { ["type"] = "mqtt", ["topic"] = "homeassistant/alert", ["json"] = NetPiJson.ToNode(json) },
        };
        if (when is not null) h["when"] = NetPiJson.ToNode(when);
        if (once is not null) h["once"] = once;
        if (afterMinutes is not null) h["afterMinutes"] = afterMinutes;
        return h;
    }

    private static async Task<(WebhooksTests.Env Env, FakeBroker Broker)> Setup()
    {
        var env = await new WebhooksTests.Env().StartAsync();
        var broker = new FakeBroker();
        env.Ctx.SettingsFake.SetQuiet("webhooks.mqtt.host", "127.0.0.1");
        env.Ctx.SettingsFake.SetQuiet("webhooks.mqtt.port", broker.Port);
        env.Ctx.SettingsFake.SetQuiet("webhooks.mqtt.user", "iot");
        env.Ctx.SettingsFake.SetQuiet("webhooks.mqtt.password", "secret");
        return (env, broker);
    }

    private static void AssistantSays(WebhooksTests.Env env, string sessionId, string text) =>
        env.Ctx.SessionsFake.AppendMessage(sessionId, new ChatMessage { Role = MessageRole.Assistant, Parts = [new TextPart { Text = text }], CreatedAt = env.Time.Now });

    private static async Task<JsonObject> Published(FakeBroker broker, int count)
    {
        for (var i = 0; i < 100 && broker.Published.Count < count; i++) await Task.Delay(20);
        Check.True(broker.Published.Count >= count, $"the broker got {broker.Published.Count} message(s), expected {count}");
        return JsonNode.Parse(broker.Published.ToArray()[count - 1].Payload)!.AsObject();
    }

    public static void Register(TestRunner r)
    {
        r.Add("hooks: a received hook runs for every payload before filters and grouping; json templates cannot be broken by the alert's text", async () =>
        {
            var (env, broker) = await Setup();
            using var _ = broker;
            var hooks = new[]
            {
                Mqtt("clear", "received", new[] { new { path = "state", @in = new[] { "OK", "UP" } } }, new { clear = true, id = "checkmk_{{host}}_{{service}}" }),
                Mqtt("page", "received", new[] { new { path = "state", @in = new[] { "DOWN" } } }, new { title = "{{host}} is DOWN", message = "{{output}}", id = "checkmk_{{host}}" }),
            };
            var added = await env.AddGrouped(extra: new { hooks });
            var id = S(added["id"])!; var token = S(added["token"])!;

            await env.Post(id, token, Alert());   // CRIT: no hook wants it
            await env.Post(id, token, Alert("Host", state: "DOWN", output: "say \"hi\"\nline two \\ backslash"));
            var page = await Published(broker, 1);
            Check.Equal("nuc is DOWN", S(page["title"]));
            Check.Equal("say \"hi\"\nline two \\ backslash", S(page["message"]), "quotes, newlines and backslashes survive: the payload is built as JSON, not pasted into it");
            Check.Equal("homeassistant/alert", broker.Published.ToArray()[0].Topic);
            Check.Equal("iot", broker.Users.Single(), "the broker credentials are used");

            // a recovery fails the match and has no open incident (skipped), and still clears the notification
            var recovery = await env.Post(id, token, Alert("Container radio", host: "server", state: "OK"));
            Check.Equal("skipped", S(recovery.Body["status"]));
            var clear = await Published(broker, 2);
            Check.Equal(true, clear["clear"]!.GetValue<bool>());
            Check.Equal("checkmk_server_Container radio", S(clear["id"]));
            Check.Equal(2, broker.Published.Count, "exactly the two hooks that matched");
            var rows = (await env.History(id)).Select(h => S(h!["status"])).Count(s => s == "hook");
            Check.Equal(2, rows, "each action is in the history");
            Check.Equal("skipped", S(((JsonObject)(await env.Get(id)))["lastStatus"]), "a hook is not the webhook's last delivery");
        });

        r.Add("hooks: the finished hook gates on the agent's verdict and fails open — silence, an error or an unknown verdict still notifies, and once means once", async () =>
        {
            var (env, broker) = await Setup();
            using var _ = broker;
            var hooks = new[]
            {
                Mqtt("notify", "finished", new[] { new { path = "verdict", notIn = new[] { "noise", "resolved" } } },
                    new { title = "Checkmk: {{title}}", message = "{{verdict}}: {{summary}}", id = "checkmk_{{key}}" }, once: "notify"),
            };
            var added = await env.AddGrouped(extra: new { hooks, title = "{{host}}: {{service}} is {{state}}" });
            var id = S(added["id"])!; var token = S(added["token"])!;
            await env.Post(id, token, Alert("Kernel errors"));
            env.Time.Advance(TimeSpan.FromSeconds(91));
            await env.Tick();
            var sid = env.Ctx.SessionsFake.Sessions.Single().Id;

            env.Time.Advance(TimeSpan.FromSeconds(30));
            await env.Tick();
            Check.Equal(0, broker.Published.Count, "the chat is still running: not finished");

            env.FinishChats();
            AssistantSays(env, sid, "Benign: a stale service.\nVERDICT: noise");
            env.Time.Advance(TimeSpan.FromSeconds(10));
            await env.Tick();
            await Task.Delay(100);
            Check.Equal(0, broker.Published.Count, "the agent said noise: nothing is sent");
            Check.Equal("noise", S((await env.Incidents(id)).Single()!["verdict"]));

            // a new failure wakes the same chat; this time the agent says nothing useful
            env.Time.Advance(TimeSpan.FromMinutes(2));
            Check.Equal("appended", S((await env.Post(id, token, Alert("Container radio", output: "Exited (1)"))).Body["status"]));
            env.FinishChats();
            AssistantSays(env, sid, "The radio container crashed (OOM). I restarted it with docker restart radio and it is healthy.");
            env.Time.Advance(TimeSpan.FromSeconds(10));
            await env.Tick();
            var notice = await Published(broker, 1);
            Check.Equal("unknown: The radio container crashed (OOM). I restarted it with docker restart radio and it is healthy.", S(notice["message"]), "no VERDICT line: unknown, so it is sent");
            Check.Contains(S(notice["title"]), "Checkmk: nuc: Kernel errors is CRIT");
            Check.Equal("checkmk_nuc", S(notice["id"]));

            // once: a third finish does not notify again
            env.Time.Advance(TimeSpan.FromMinutes(2));
            await env.Post(id, token, Alert("Disk"));
            env.FinishChats();
            AssistantSays(env, sid, "Real problem.\nVERDICT: real");
            env.Time.Advance(TimeSpan.FromSeconds(10));
            await env.Tick();
            await Task.Delay(100);
            Check.Equal(1, broker.Published.Count, "the once key already fired for this incident");

            // an errored run is "error", not silence
            var added2 = await env.AddGrouped(extra: new { name = "second", hooks = new[] { Mqtt("err", "finished", new[] { new { path = "status", equals = "error" } }, new { message = "agent failed: {{reason}}" }) } });
            await env.Post(S(added2["id"])!, S(added2["token"])!, Alert("Disk", host: "server"));
            env.Time.Advance(TimeSpan.FromSeconds(91));
            await env.Tick();
            var failedAgent = env.Runtime.Agents.Last();
            failedAgent.Status = AgentStatus.Failed;
            failedAgent.Error = "the model is not loaded";
            env.Time.Advance(TimeSpan.FromSeconds(10));
            await env.Tick();
            var err = await Published(broker, 2);
            Check.Equal("agent failed: the model is not loaded", S(err["message"]));
        });

        r.Add("hooks: stalled fires once after its minutes, asking carries the question, failed carries the reason", async () =>
        {
            var (env, broker) = await Setup();
            using var _ = broker;
            var hooks = new object[]
            {
                Mqtt("slow", "stalled", null, new { message = "no answer after {{elapsedMinutes}} min: {{title}}" }, afterMinutes: 5),
                Mqtt("ask", "asking", null, new { message = "needs you: {{question}}" }),
            };
            var added = await env.AddGrouped(extra: new { hooks, title = "{{host}}: {{service}} is {{state}}" });
            var id = S(added["id"])!; var token = S(added["token"])!;
            await env.Post(id, token, Alert("Kernel errors"));
            env.Time.Advance(TimeSpan.FromSeconds(91));
            await env.Tick();

            env.Time.Advance(TimeSpan.FromMinutes(4));
            await env.Tick();
            await Task.Delay(100);
            Check.Equal(0, broker.Published.Count, "4 min: not stalled yet");
            env.Time.Advance(TimeSpan.FromMinutes(2));
            await env.Tick();
            var slow = await Published(broker, 1);
            Check.Contains(S(slow["message"]), "no answer after 6 min: nuc: Kernel errors is CRIT");
            env.Time.Advance(TimeSpan.FromMinutes(2));
            await env.Tick();
            await Task.Delay(100);
            Check.Equal(1, broker.Published.Count, "stalled fires once per wait");

            // the chat asks the user
            env.Ctx.Rpc.Register("ask.pending", (_, _) => Task.FromResult<object?>(new JsonArray(new JsonObject { ["questions"] = new JsonArray(new JsonObject { ["question"] = "Restart the radio container?" }) })));
            env.Runtime.Agents.Last().Status = AgentStatus.Yielded;
            env.Time.Advance(TimeSpan.FromSeconds(10));
            await env.Tick();
            var ask = await Published(broker, 2);
            Check.Equal("needs you: Restart the radio container?", S(ask["message"]));
            await env.Tick();
            await Task.Delay(100);
            Check.Equal(2, broker.Published.Count, "asking fires once per wait too");

            // batches that cannot start become a failed hook, with the reason
            var env2Pair = await Setup();
            using var broker2 = env2Pair.Broker;
            var env2 = env2Pair.Env;
            env2.Agents(("local", false, true));
            var failedHook = await env2.AddGrouped(extra: new { agent = "local", title = "{{host}}: {{service}} is {{state}}", hooks = new[] { Mqtt("lost", "failed", null, new { title = "unreviewed: {{title}}", message = "{{reason}}" }) } });
            await env2.Post(S(failedHook["id"])!, S(failedHook["token"])!, Alert("Kernel errors"));
            for (var i = 0; i < 70; i++) { env2.Time.Advance(TimeSpan.FromSeconds(30)); await env2.Tick(); }
            var lost = await Published(broker2, 1);
            Check.Equal("unreviewed: nuc: Kernel errors is CRIT", S(lost["title"]));
            Check.Contains(S(lost["message"]), "unavailable");
        });

        r.Add("hooks: an ungrouped webhook is watched too, a failing action is recorded and does not stop the chat, and the http action sends what it is told", async () =>
        {
            var (env, broker) = await Setup();
            using var _ = broker;
            using var http = new FakeHttp();
            var hooks = new object[]
            {
                new JsonObject
                {
                    ["on"] = "finished", ["name"] = "ping",
                    ["do"] = new JsonObject
                    {
                        ["type"] = "http", ["url"] = $"http://127.0.0.1:{http.Port}/notify/{{{{host}}}}", ["method"] = "POST",
                        ["headers"] = new JsonObject { ["X-Source"] = "netpi-{{host}}" },
                        ["json"] = new JsonObject { ["verdict"] = "{{verdict}}", ["text"] = "{{summary}}" },
                    },
                },
            };
            var added = await env.Add(extra: new { hooks });   // no group key: every alert its own chat
            var id = S(added["id"])!; var token = S(added["token"])!;
            var started = await env.Post(id, token, Alert());
            Check.Equal("started", S(started.Body["status"]));
            var sid = S(started.Body["sessionId"])!;
            env.FinishChats();
            AssistantSays(env, sid, "All good.\nVERDICT: resolved");
            env.Time.Advance(TimeSpan.FromSeconds(10));
            await env.Tick();
            for (var i = 0; i < 100 && http.Requests.IsEmpty; i++) await Task.Delay(20);
            var req = http.Requests.Single();
            Check.Equal("POST", req.Method);
            Check.Equal("/notify/nuc", req.Path);
            Check.Equal("netpi-nuc", req.Headers["x-source"]);
            Check.Contains(req.Headers["content-type"], "application/json");
            var body = JsonNode.Parse(req.Body)!;
            Check.Equal("resolved", S(body["verdict"]));
            Check.Equal("All good.", S(body["text"]), "the summary has no VERDICT line");

            // a broker that refuses, and a server that errors: recorded, not thrown
            broker.ConnackCode = 5;
            http.Status = 500;
            var added2 = await env.Add(extra: new { name = "bad", hooks = new object[] { Mqtt("m", "received", null, new { x = "1" }), hooks[0] } });
            var again = await env.Post(S(added2["id"])!, S(added2["token"])!, Alert("Disk"));
            Check.Equal("started", S(again.Body["status"]), "a failing hook does not stop the delivery");
            var reasons = (await env.History(S(added2["id"])!)).Where(h => S(h!["status"]) == "hook").Select(h => S(h!["reason"])).ToList();
            Check.True(reasons.Any(x => x!.Contains("m: FAILED") && x.Contains("refused the credentials")), "the broker refusal is in the history: " + string.Join(" | ", reasons));
            env.FinishChats();
            AssistantSays(env, S(again.Body["sessionId"])!, "done");
            env.Time.Advance(TimeSpan.FromSeconds(10));
            await env.Tick();
            reasons = (await env.History(S(added2["id"])!)).Where(h => S(h!["status"]) == "hook").Select(h => S(h!["reason"])).ToList();
            Check.True(reasons.Any(x => x!.Contains("ping: FAILED") && x.Contains("HTTP 500")), "the http error is in the history: " + string.Join(" | ", reasons));

            // no broker configured
            env.Ctx.SettingsFake.SetQuiet("webhooks.mqtt.host", "");
            var added3 = await env.Add(extra: new { name = "nobroker", hooks = new[] { Mqtt("m", "received", null, new { x = "1" }) } });
            await env.Post(S(added3["id"])!, S(added3["token"])!, Alert("Disk"));
            Check.True((await env.History(S(added3["id"])!)).Any(h => S(h!["reason"])?.Contains("webhooks.mqtt.host is not set") == true), "an unset broker says so");
        });

        r.Add("hooks: testHook runs one action with a sample context; bad hook definitions are refused when saved", async () =>
        {
            var (env, broker) = await Setup();
            using var _ = broker;
            var added = await env.Add(extra: new { hooks = new[] { Mqtt("page", "finished", null, new { title = "{{host}}: {{summary}}" }) } });
            var id = S(added["id"])!;
            var ok = (await env.Ctx.RpcFake.CallAsync("webhooks.testHook", new { id, index = 1, context = new { host = "nuc", summary = "it works" } }))!;
            Check.Equal(true, ok["ok"]!.GetValue<bool>());
            var sent = await Published(broker, 1);
            Check.Equal("nuc: it works", S(sent["title"]));
            await Check.ThrowsAsync<RpcException>(() => env.Ctx.RpcFake.InvokeAsync("webhooks.testHook", new { id, index = 5 }));

            async Task<string> Refused(object hook) => (await Check.ThrowsAsync<RpcException>(() => env.Add(extra: new { name = "x", hooks = new[] { hook } }))).Message;
            Check.Contains(await Refused(new { on = "sometimes", @do = new { type = "mqtt", topic = "t", text = "x" } }), "on must be one of");
            Check.Contains(await Refused(new { on = "received", @do = new { type = "mqtt", text = "x" } }), "needs a topic");
            Check.Contains(await Refused(new { on = "received", @do = new { type = "mqtt", topic = "t" } }), "json");
            Check.Contains(await Refused(new { on = "received", @do = new { type = "carrier-pigeon" } }), "mqtt or http");
            Check.Contains(await Refused(new { on = "received", @do = new { type = "http", url = "ftp://x/y" } }), "http(s) url");
            Check.Contains(await Refused(new { on = "received", afterMinutes = 5, @do = new { type = "mqtt", topic = "t", text = "x" } }), "afterMinutes only belongs to a stalled hook");
            Check.Contains(await Refused(new { on = "stalled", afterMinutes = 0, @do = new { type = "mqtt", topic = "t", text = "x" } }), "1–1440");
            Check.Contains(await Refused(new { on = "received", colour = "red", @do = new { type = "mqtt", topic = "t", text = "x" } }), "unknown field");
            Check.Contains(await Refused(new { on = "received", when = new[] { new { path = "x", matches = "(" } }, @do = new { type = "mqtt", topic = "t", text = "x" } }), "condition on x");
        });
    }
}
