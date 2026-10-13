using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NetPI.Webhooks;

namespace NetPI.Aux.Tests;

/// <summary>
/// Webhook hooks: actions NetPI runs itself at fixed points (received, started, finished, asking, stalled, failed), gated by conditions over the
/// alert, the agent's verdict and its answer, fired once per incident when asked, and failing open: an agent that says nothing, dies or stalls does
/// not suppress the notification. A command hook launches a real process (this test host in its <c>--hook-echo</c> mode, which records its arguments and
/// standard input); a fake HTTP server shows what the http action sends.
/// </summary>
public static class WebhookHooksTests
{
    /// <summary>The <c>--hook-echo FILE EXIT [ARGS…]</c> mode of the test host: append one JSON line { args, stdin } to FILE, exit with EXIT (non-zero also writes to stderr).</summary>
    public static int Echo(string[] a)
    {
        var stdin = Console.In.ReadToEnd();
        File.AppendAllText(a[0], JsonSerializer.Serialize(new { args = a.Skip(2).ToArray(), stdin }) + "\n");
        if (a[1] != "0") Console.Error.WriteLine("refused: simulated failure\nsecond line");
        return int.Parse(a[1]);
    }

    /// <summary>Where the echo process writes; one JSON line per run.</summary>
    internal sealed class Sink
    {
        public string File { get; } = Path.Combine(T.TempDir("hook-sink"), "calls.jsonl");

        public List<JsonObject> Calls() => !System.IO.File.Exists(File) ? [] :
            [.. System.IO.File.ReadAllLines(File).Where(l => l.Trim().Length > 0).Select(l => JsonNode.Parse(l)!.AsObject())];

        public int Count => Calls().Count;
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
                            if (Encoding.ASCII.GetString(buffer.ToArray()).EndsWith("\r\n\r\n")) break;
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

    /// <summary>A command action: this test host in echo mode. <paramref name="json"/> is what the program reads on standard input.</summary>
    private static JsonObject Run(Sink sink, object json, int exit = 0, params string[] extraArgs)
    {
        var args = new JsonArray(typeof(WebhookHooksTests).Assembly.Location, "--hook-echo", sink.File, exit.ToString());
        foreach (var a in extraArgs) args.Add(a);
        return new JsonObject { ["type"] = "command", ["command"] = Environment.ProcessPath!, ["args"] = args, ["json"] = NetPiJson.ToNode(json) };
    }

    private static object Hook(Sink sink, string name, string on, object? when, object json, string? once = null, int? afterMinutes = null, int exit = 0)
    {
        var h = new JsonObject { ["name"] = name, ["on"] = on, ["do"] = Run(sink, json, exit) };
        if (when is not null) h["when"] = NetPiJson.ToNode(when);
        if (once is not null) h["once"] = once;
        if (afterMinutes is not null) h["afterMinutes"] = afterMinutes;
        return h;
    }

    private static async Task<(WebhooksTests.Env Env, Sink Sink)> Setup() => (await new WebhooksTests.Env().StartAsync(), new Sink());

    private static void AssistantSays(WebhooksTests.Env env, string sessionId, string text) =>
        env.Ctx.SessionsFake.AppendMessage(sessionId, new ChatMessage { Role = MessageRole.Assistant, Parts = [new TextPart { Text = text }], CreatedAt = env.Time.Now });

    /// <summary>The standard input of the n-th (1-based) run of the echo program, as JSON.</summary>
    private static async Task<JsonObject> Ran(Sink sink, int count)
    {
        for (var i = 0; i < 150 && sink.Count < count; i++) await Task.Delay(20);
        Check.True(sink.Count >= count, $"the echo program ran {sink.Count} time(s), expected {count}");
        return JsonNode.Parse(S(sink.Calls()[count - 1]["stdin"])!)!.AsObject();
    }

    public static void Register(TestRunner r)
    {
        r.Add("hooks: a received hook runs for every payload before filters and grouping; json templates cannot be broken by the alert's text", async () =>
        {
            var (env, sink) = await Setup();
            var hooks = new[]
            {
                Hook(sink, "clear", "received", new[] { new { path = "state", @in = new[] { "OK", "UP" } } }, new { clear = true, id = "checkmk_{{host}}_{{service}}" }),
                Hook(sink, "page", "received", new[] { new { path = "state", @in = new[] { "DOWN" } } }, new { title = "{{host}} is DOWN", message = "{{output}}", id = "checkmk_{{host}}" }),
            };
            var added = await env.AddGrouped(extra: new { hooks });
            var id = S(added["id"])!; var token = S(added["token"])!;

            await env.Post(id, token, Alert());   // CRIT: no hook wants it
            await env.Post(id, token, Alert("Host", state: "DOWN", output: "say \"hi\"\nline two \\ backslash"));
            var page = await Ran(sink, 1);
            Check.Equal("nuc is DOWN", S(page["title"]));
            Check.Equal("say \"hi\"\nline two \\ backslash", S(page["message"]), "quotes, newlines and backslashes survive: the stdin is built as JSON, not pasted into it");

            // a recovery fails the match and has no open incident (skipped), and still clears the notification
            var recovery = await env.Post(id, token, Alert("Container radio", host: "server", state: "OK"));
            Check.Equal("skipped", S(recovery.Body["status"]));
            var clear = await Ran(sink, 2);
            Check.Equal(true, clear["clear"]!.GetValue<bool>());
            Check.Equal("checkmk_server_Container radio", S(clear["id"]));
            Check.Equal(2, sink.Count, "exactly the two hooks that matched");
            Check.Equal(2, (await env.History(id)).Select(h => S(h!["status"])).Count(s => s == "hook"), "each action is in the history");
            Check.Equal("skipped", S(((JsonObject)(await env.Get(id)))["lastStatus"]), "a hook is not the webhook's last delivery");
        });

        r.Add("hooks: the finished hook gates on the agent's verdict and fails open - silence, an error or an unknown verdict still notifies, and once means once", async () =>
        {
            var (env, sink) = await Setup();
            var hooks = new[]
            {
                Hook(sink, "notify", "finished", new[] { new { path = "verdict", notIn = new[] { "noise", "resolved" } } },
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
            Check.Equal(0, sink.Count, "the chat is still running: not finished");

            env.FinishChats();
            AssistantSays(env, sid, "Benign: a stale service.\nVERDICT: noise");
            env.Time.Advance(TimeSpan.FromSeconds(10));
            await env.Tick();
            await Task.Delay(200);
            Check.Equal(0, sink.Count, "the agent said noise: nothing is sent");
            Check.Equal("noise", S((await env.Incidents(id)).Single()!["verdict"]));

            // a new failure wakes the same chat; this time the agent says nothing useful
            env.Time.Advance(TimeSpan.FromMinutes(2));
            Check.Equal("appended", S((await env.Post(id, token, Alert("Container radio", output: "Exited (1)"))).Body["status"]));
            env.FinishChats();
            AssistantSays(env, sid, "The radio container crashed (OOM). I restarted it with docker restart radio and it is healthy.");
            env.Time.Advance(TimeSpan.FromSeconds(10));
            await env.Tick();
            var notice = await Ran(sink, 1);
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
            await Task.Delay(200);
            Check.Equal(1, sink.Count, "the once key already fired for this incident");

            // an errored run is "error", not silence
            var added2 = await env.AddGrouped(extra: new { name = "second", hooks = new[] { Hook(sink, "err", "finished", new[] { new { path = "status", equals = "error" } }, new { message = "agent failed: {{reason}}" }) } });
            await env.Post(S(added2["id"])!, S(added2["token"])!, Alert("Disk", host: "server"));
            env.Time.Advance(TimeSpan.FromSeconds(91));
            await env.Tick();
            var failedAgent = env.Runtime.Agents.Last();
            failedAgent.Status = AgentStatus.Failed;
            failedAgent.Error = "the model is not loaded";
            env.Time.Advance(TimeSpan.FromSeconds(10));
            await env.Tick();
            var err = await Ran(sink, 2);
            Check.Equal("agent failed: the model is not loaded", S(err["message"]));
        });

        r.Add("hooks: stalled fires once after its minutes, asking carries the question, failed carries the reason", async () =>
        {
            var (env, sink) = await Setup();
            var hooks = new object[]
            {
                Hook(sink, "slow", "stalled", null, new { message = "no answer after {{elapsedMinutes}} min: {{title}}" }, afterMinutes: 5),
                Hook(sink, "ask", "asking", null, new { message = "needs you: {{question}}" }),
            };
            var added = await env.AddGrouped(extra: new { hooks, title = "{{host}}: {{service}} is {{state}}" });
            var id = S(added["id"])!; var token = S(added["token"])!;
            await env.Post(id, token, Alert("Kernel errors"));
            env.Time.Advance(TimeSpan.FromSeconds(91));
            await env.Tick();

            env.Time.Advance(TimeSpan.FromMinutes(4));
            await env.Tick();
            await Task.Delay(200);
            Check.Equal(0, sink.Count, "4 min: not stalled yet");
            env.Time.Advance(TimeSpan.FromMinutes(2));
            await env.Tick();
            var slow = await Ran(sink, 1);
            Check.Contains(S(slow["message"]), "no answer after 6 min: nuc: Kernel errors is CRIT");
            env.Time.Advance(TimeSpan.FromMinutes(2));
            await env.Tick();
            await Task.Delay(200);
            Check.Equal(1, sink.Count, "stalled fires once per wait");

            // the chat asks the user
            env.Ctx.Rpc.Register("ask.pending", (_, _) => Task.FromResult<object?>(new JsonArray(new JsonObject { ["questions"] = new JsonArray(new JsonObject { ["question"] = "Restart the radio container?" }) })));
            env.Runtime.Agents.Last().Status = AgentStatus.Yielded;
            env.Time.Advance(TimeSpan.FromSeconds(10));
            await env.Tick();
            var ask = await Ran(sink, 2);
            Check.Equal("needs you: Restart the radio container?", S(ask["message"]));
            await env.Tick();
            await Task.Delay(200);
            Check.Equal(2, sink.Count, "asking fires once per wait too");

            // batches that cannot start become a failed hook, with the reason
            var (env2, sink2) = await Setup();
            env2.Agents(("local", false, true));
            var failedHook = await env2.AddGrouped(extra: new { agent = "local", title = "{{host}}: {{service}} is {{state}}", hooks = new[] { Hook(sink2, "lost", "failed", null, new { title = "unreviewed: {{title}}", message = "{{reason}}" }) } });
            await env2.Post(S(failedHook["id"])!, S(failedHook["token"])!, Alert("Kernel errors"));
            for (var i = 0; i < 70; i++) { env2.Time.Advance(TimeSpan.FromSeconds(30)); await env2.Tick(); }
            var lost = await Ran(sink2, 1);
            Check.Equal("unreviewed: nuc: Kernel errors is CRIT", S(lost["title"]));
            Check.Contains(S(lost["message"]), "unavailable");
        });

        r.Add("hooks: an ungrouped webhook is watched too, a failing action is recorded and does not stop the chat, and the http action sends what it is told", async () =>
        {
            var (env, sink) = await Setup();
            using var http = new FakeHttp();
            var ping = new JsonObject
            {
                ["on"] = "finished", ["name"] = "ping",
                ["do"] = new JsonObject
                {
                    ["type"] = "http", ["url"] = $"http://127.0.0.1:{http.Port}/notify/{{{{host}}}}", ["method"] = "POST",
                    ["headers"] = new JsonObject { ["X-Source"] = "netpi-{{host}}" },
                    ["json"] = new JsonObject { ["verdict"] = "{{verdict}}", ["text"] = "{{summary}}" },
                },
            };
            var added = await env.Add(extra: new { hooks = new object[] { ping } });   // no group key: every alert its own chat
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

            // a program that fails, a program that does not exist, and a server that errors: recorded, not thrown
            http.Status = 500;
            var missing = new JsonObject { ["on"] = "received", ["name"] = "nope", ["do"] = new JsonObject { ["type"] = "command", ["command"] = "netpi-no-such-program-xyz", ["text"] = "x" } };
            var added2 = await env.Add(extra: new { name = "bad", hooks = new object[] { Hook(sink, "refuses", "received", null, new { x = "1" }, exit: 3), missing, ping } });
            var again = await env.Post(S(added2["id"])!, S(added2["token"])!, Alert("Disk"));
            Check.Equal("started", S(again.Body["status"]), "failing hooks do not stop the delivery");
            var reasons = (await env.History(S(added2["id"])!)).Where(h => S(h!["status"]) == "hook").Select(h => S(h!["reason"])).ToList();
            Check.True(reasons.Any(x => x!.Contains("refuses: FAILED") && x.Contains("exited 3: refused: simulated failure")), "the exit code and the program's own words are in the history: " + string.Join(" | ", reasons));
            Check.True(reasons.Any(x => x!.Contains("nope: FAILED") && x.Contains("could not be started")), "a missing program says so: " + string.Join(" | ", reasons));
            env.FinishChats();
            AssistantSays(env, S(again.Body["sessionId"])!, "done");
            env.Time.Advance(TimeSpan.FromSeconds(10));
            await env.Tick();
            reasons = (await env.History(S(added2["id"])!)).Where(h => S(h!["status"]) == "hook").Select(h => S(h!["reason"])).ToList();
            Check.True(reasons.Any(x => x!.Contains("ping: FAILED") && x.Contains("HTTP 500")), "the http error is in the history: " + string.Join(" | ", reasons));
        });

        r.Add("hooks: a command gets its arguments and environment templated, never a shell, and is stopped when it hangs", async () =>
        {
            var (env, sink) = await Setup();
            var evil = "x\"; echo pwned; \"";
            var added = await env.Add(extra: new
            {
                hooks = new object[]
                {
                    new JsonObject
                    {
                        ["on"] = "received", ["name"] = "args",
                        ["do"] = Run(sink, new { text = "{{output}}" }, 0, "{{host}}", "--note={{output}}"),
                    },
                    new JsonObject
                    {
                        ["on"] = "received", ["name"] = "hang", ["when"] = JsonNode.Parse("""[{"path":"service","equals":"Hang"}]"""),
                        ["do"] = new JsonObject
                        {
                            ["type"] = "command", ["command"] = Environment.ProcessPath!, ["timeoutSeconds"] = 1,
                            ["args"] = new JsonArray(typeof(WebhookHooksTests).Assembly.Location, "--hook-sleep"),
                        },
                    },
                },
            });
            var id = S(added["id"])!; var token = S(added["token"])!;
            await env.Post(id, token, Alert("Kernel errors", output: evil));
            var call = sink.Calls().Single();
            var args = call["args"]!.AsArray().Select(S).ToList();
            Check.Equal("nuc", args[0]);
            Check.Equal("--note=" + evil, args[1], "the alert's text arrives as ONE argument, untouched: nothing interprets it");
            Check.Equal(evil, S(JsonNode.Parse(S(call["stdin"])!)!["text"]), "and as JSON on stdin");

            var started = System.Diagnostics.Stopwatch.StartNew();
            await env.Post(id, token, Alert("Hang"));
            Check.True(started.Elapsed < TimeSpan.FromSeconds(10), "a hanging program is stopped at its timeout: " + started.Elapsed);
            var reasons = (await env.History(id)).Where(h => S(h!["status"]) == "hook").Select(h => S(h!["reason"])).ToList();
            Check.True(reasons.Any(x => x!.Contains("hang: FAILED") && x.Contains("did not finish within 1 s")), string.Join(" | ", reasons));
        });

        r.Add("hooks: testHook runs one action with a sample context; bad hook definitions are refused when saved", async () =>
        {
            var (env, sink) = await Setup();
            var added = await env.Add(extra: new { hooks = new[] { Hook(sink, "page", "finished", null, new { title = "{{host}}: {{summary}}" }) } });
            var id = S(added["id"])!;
            var ok = (await env.Ctx.RpcFake.CallAsync("webhooks.testHook", new { id, index = 1, context = new { host = "nuc", summary = "it works" } }))!;
            Check.Equal(true, ok["ok"]!.GetValue<bool>());
            Check.Contains(S(ok["detail"]), "exited 0");
            var sent = await Ran(sink, 1);
            Check.Equal("nuc: it works", S(sent["title"]));
            await Check.ThrowsAsync<RpcException>(() => env.Ctx.RpcFake.InvokeAsync("webhooks.testHook", new { id, index = 5 }));

            async Task<string> Refused(object hook) => (await Check.ThrowsAsync<RpcException>(() => env.Add(extra: new { name = "x", hooks = new[] { hook } }))).Message;
            Check.Contains(await Refused(new { on = "sometimes", @do = new { type = "command", command = "x" } }), "on must be one of");
            Check.Contains(await Refused(new { on = "received", @do = new { type = "command" } }), "needs a command");
            Check.Contains(await Refused(new { on = "received", @do = new { type = "command", command = "{{host}}" } }), "cannot be a template");
            Check.Contains(await Refused(new { on = "received", @do = new { type = "command", command = "x", args = new object[] { 1 } } }), "array of at most 50 strings");
            Check.Contains(await Refused(new { on = "received", @do = new { type = "command", command = "x", timeoutSeconds = 999 } }), "1–300");
            Check.Contains(await Refused(new { on = "received", @do = new { type = "mqtt", topic = "t", text = "x" } }), "command or http");
            Check.Contains(await Refused(new { on = "received", @do = new { type = "http", url = "ftp://x/y" } }), "http(s) url");
            Check.Contains(await Refused(new { on = "received", afterMinutes = 5, @do = new { type = "command", command = "x" } }), "afterMinutes only belongs to a stalled hook");
            Check.Contains(await Refused(new { on = "stalled", afterMinutes = 0, @do = new { type = "command", command = "x" } }), "1–1440");
            Check.Contains(await Refused(new { on = "received", colour = "red", @do = new { type = "command", command = "x" } }), "unknown field");
            Check.Contains(await Refused(new { on = "received", when = new[] { new { path = "x", matches = "(" } }, @do = new { type = "command", command = "x" } }), "condition on x");

            // an agent cannot give a webhook a hook: its change to hooks pauses the webhook as a proposal
            var proposed = (await env.Ctx.RpcFake.CallAsync("webhooks.update", new { id, hooks = new[] { Hook(sink, "evil", "received", null, new { x = 1 }) }, proposedBy = "session:ses_1" }))!;
            Check.Equal(false, proposed["enabled"]!.GetValue<bool>());
        });
    }
}
