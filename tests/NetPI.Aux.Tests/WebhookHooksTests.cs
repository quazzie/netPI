using System.Text.Json;
using System.Text.Json.Nodes;
using NetPI.Webhooks;

namespace NetPI.Aux.Tests;

/// <summary>
/// Webhook hooks: a <c>pre</c> command runs for every payload and a <c>post</c> command when the chat finishes, asks, stalls or could not start; each gets its context
/// as JSON on standard input, never templates in its program or arguments, and a failing hook never stops the webhook. The post hook sees the agent's verdict
/// (<c>unknown</c> when it said nothing, so a script that gates on it fails open). A hook launches a real process: this test host in its <c>--hook-echo</c> mode,
/// which records its arguments and standard input.
/// </summary>
public static class WebhookHooksTests
{
    /// <summary>The <c>--hook-echo FILE EXIT [ARGS…]</c> mode of the test host: append one JSON line { args, stdin } to FILE, exit with EXIT (non-zero also writes to stderr).</summary>
    public static int Echo(string[] a)
    {
        var stdin = Console.In.ReadToEnd();
        File.AppendAllText(a[0], JsonSerializer.Serialize(new { args = a.Skip(2).ToArray(), stdin }) + "\n");
        if (a[1] != "0") Console.Error.WriteLine("refused: simulated failure\nsecond line");
        else if (a.Skip(2).FirstOrDefault(x => x.StartsWith("--adapt")) is { } spec) Console.WriteLine(Adapter(stdin, spec));
        else if (a.Skip(2).FirstOrDefault(x => x.StartsWith("--say=")) is { } say) Console.WriteLine(say["--say=".Length..]);
        return int.Parse(a[1]);
    }

    /// <summary>The Before command the tests use: <c>--adapt:key+title+text+crit+skipok</c> answers like a Checkmk adapter would for the alert on standard input.</summary>
    private static string Adapter(string stdin, string spec)
    {
        var features = spec.Split(':').Skip(1).SelectMany(x => x.Split('+')).ToHashSet();
        var alert = JsonNode.Parse(stdin)?["alert"];
        string Get(string name) => alert?[name]?.ToString() ?? "";
        var o = new JsonObject();
        if (features.Contains("key")) o["key"] = Get("host");
        if (features.Contains("title")) o["title"] = $"{Get("host")} / {Get("service")} is {Get("state")}";
        if (features.Contains("text")) o["text"] = $"{Get("service")} is {Get("state")} ({Get("output")})";
        if (features.Contains("crit")) o["start"] = Get("state") is "CRIT" or "DOWN";   // a recovery may join an open conversation, never start one
        if (features.Contains("skipok")) o["skip"] = Get("state") == "OK";
        return o.ToJsonString();
    }

    /// <summary>Where the echo process writes; one JSON line per run.</summary>
    internal sealed class Sink
    {
        public string File { get; } = Path.Combine(T.TempDir("hook-sink"), "calls.jsonl");

        public List<JsonObject> Calls() => !System.IO.File.Exists(File) ? [] :
            [.. System.IO.File.ReadAllLines(File).Where(l => l.Trim().Length > 0).Select(l => JsonNode.Parse(l)!.AsObject())];

        public int Count => Calls().Count;
    }

    private static string? S(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static object Alert(string service = "Kernel errors", string host = "nuc", string state = "CRIT", string output = "1 in last 30 min") =>
        new { host, service, state, output };

    /// <summary>A hook: this test host in echo mode, which records what it was given. <paramref name="exit"/> is the exit code it ends with.</summary>
    internal static JsonObject Hook(Sink sink, int exit = 0, params string[] extraArgs)
    {
        var args = new JsonArray(typeof(WebhookHooksTests).Assembly.Location, "--hook-echo", sink.File, exit.ToString());
        foreach (var a in extraArgs) args.Add(a);
        return new JsonObject { ["command"] = Environment.ProcessPath!, ["args"] = args };
    }

    /// <summary>The adapter every grouped test webhook gets: key = the host, the title and text of the alert, and a recovery that only joins.</summary>
    internal static JsonObject Adapter() => Hook(new Sink(), 0, "--adapt:key+title+text+crit");

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

    private static List<string?> HookLines(JsonArray history) => [.. history.Where(h => S(h!["status"]) == "hook").Select(h => S(h!["reason"]))];

    public static void Register(TestRunner r)
    {
        r.Add("hooks: the Before command runs for every payload before anything else, with the payload on standard input as JSON, and its answer shapes the delivery", async () =>
        {
            var (env, sink) = await Setup();
            var added = await env.AddGrouped(extra: new { pre = Hook(sink, 0, "--adapt:key+title+text+crit") });
            var id = S(added["id"])!; var token = S(added["token"])!;

            var evil = "say \"hi\"\nline two \\ backslash";
            await env.Post(id, token, Alert("Host", state: "DOWN", output: evil));
            var first = await Ran(sink, 1);
            Check.Equal("received", S(first["event"]));
            Check.Equal(id, S(first["webhook"]!["id"]));
            Check.Equal("nuc", S(first["alert"]!["host"]));
            Check.Equal(evil, S(first["alert"]!["output"]), "quotes, newlines and backslashes survive: the stdin is JSON");

            // a recovery may not start a conversation and none is open for it (skipped) - the Before command still saw it
            var recovery = await env.Post(id, token, Alert("Container radio", host: "server", state: "OK"));
            Check.Equal("skipped", S(recovery.Body["status"]));
            var second = await Ran(sink, 2);
            Check.Equal("OK", S(second["alert"]!["state"]));
            Check.Equal(2, HookLines(await env.History(id)).Count, "each run is in the history");
            Check.Equal("skipped", S(((JsonObject)(await env.Get(id)))["lastStatus"]), "a hook is not the webhook's last delivery");
        });

        r.Add("hooks: the post hook gets the agent's verdict and answer when the chat finishes - silence or an error is unknown, never a guess", async () =>
        {
            var (env, sink) = await Setup();
            var added = await env.AddGrouped(extra: new { post = Hook(sink) });
            var id = S(added["id"])!; var token = S(added["token"])!;
            await env.Post(id, token, Alert("Kernel errors"));
            var sid = env.Ctx.SessionsFake.Sessions.Single().Id;

            env.Time.Advance(TimeSpan.FromSeconds(30));
            await env.Tick();
            Check.Equal(0, sink.Count, "the chat is still running: nothing to report");

            env.FinishChats();
            AssistantSays(env, sid, "Benign: a stale service.\nVERDICT: noise");
            env.Time.Advance(TimeSpan.FromSeconds(10));
            await env.Tick();
            var done = await Ran(sink, 1);
            Check.Equal("finished", S(done["event"]));
            Check.Equal("noise", S(done["verdict"]));
            Check.Equal("ok", S(done["status"]));
            Check.Equal("Benign: a stale service.", S(done["summary"]), "the summary has no VERDICT line");
            Check.Equal("nuc / Kernel errors is CRIT", S(done["title"]), "the title the Before command gave");
            Check.Equal("nuc", S(done["key"]));
            Check.Equal(1, done["count"]!.GetValue<int>());
            Check.Equal("nuc", S(done["alert"]!["host"]), "the first alert is there too");
            Check.Equal("noise", S((await env.Incidents(id)).Single()!["verdict"]));

            // a new failure wakes the same chat; this time the agent writes no verdict line
            env.Time.Advance(TimeSpan.FromMinutes(2));
            Check.Equal("appended", S((await env.Post(id, token, Alert("Container radio", output: "Exited (1)"))).Body["status"]));
            env.FinishChats();
            AssistantSays(env, sid, "The radio container crashed (OOM). I restarted it and it is healthy.");
            env.Time.Advance(TimeSpan.FromSeconds(10));
            await env.Tick();
            var silent = await Ran(sink, 2);
            Check.Equal("unknown", S(silent["verdict"]));
            Check.Equal(2, silent["count"]!.GetValue<int>());

            // an errored run says so
            var added2 = await env.AddGrouped(extra: new { name = "second", post = Hook(sink) });
            await env.Post(S(added2["id"])!, S(added2["token"])!, Alert("Disk", host: "server"));
            var failedAgent = env.Runtime.Agents.Last();
            failedAgent.Status = AgentStatus.Failed;
            failedAgent.Error = "the model is not loaded";
            env.Time.Advance(TimeSpan.FromSeconds(10));
            await env.Tick();
            var err = await Ran(sink, 3);
            Check.Equal("error", S(err["status"]));
            Check.Equal("the model is not loaded", S(err["reason"]));
            Check.Equal("unknown", S(err["verdict"]));
        });

        r.Add("hooks: the After command is told once when a chat times out (after timeoutMinutes, 0 = never) and once per question it asks; a batch that cannot start is failed", async () =>
        {
            var (env, sink) = await Setup();
            var added = await env.AddGrouped(extra: new { post = Hook(sink), timeoutMinutes = 5 });
            var id = S(added["id"])!; var token = S(added["token"])!;
            await env.Post(id, token, Alert("Kernel errors"));

            env.Time.Advance(TimeSpan.FromMinutes(4));
            await env.Tick();
            await Task.Delay(200);
            Check.Equal(0, sink.Count, "4 min: not stalled yet");
            env.Time.Advance(TimeSpan.FromMinutes(2));
            await env.Tick();
            var slow = await Ran(sink, 1);
            Check.Equal("timeout", S(slow["event"]));
            Check.Equal(5, slow["timeoutMinutes"]!.GetValue<int>());
            Check.True(slow["elapsedMinutes"]!.GetValue<double>() >= 5, "it says how long");
            env.Time.Advance(TimeSpan.FromMinutes(2));
            await env.Tick();
            await Task.Delay(200);
            Check.Equal(1, sink.Count, "timeout fires once per wait");

            // the chat asks the user
            env.Ctx.Rpc.Register("ask.pending", (_, _) => Task.FromResult<object?>(new JsonArray(new JsonObject { ["questions"] = new JsonArray(new JsonObject { ["question"] = "Restart the radio container?" }) })));
            env.Runtime.Agents.Last().Status = AgentStatus.Yielded;
            env.Time.Advance(TimeSpan.FromSeconds(10));
            await env.Tick();
            var ask = await Ran(sink, 2);
            Check.Equal("asking", S(ask["event"]));
            Check.Equal("Restart the radio container?", S(ask["question"]));
            await env.Tick();
            await Task.Delay(200);
            Check.Equal(2, sink.Count, "asking fires once per wait too");

            // timeoutMinutes 0 = never told
            var (env0, sink0) = await Setup();
            var never = await env0.AddGrouped(extra: new { post = Hook(sink0), timeoutMinutes = 0 });
            await env0.Post(S(never["id"])!, S(never["token"])!, Alert("Kernel errors"));
            env0.Time.Advance(TimeSpan.FromHours(2));
            await env0.Tick();
            await Task.Delay(200);
            Check.Equal(0, sink0.Count, "0 = never");

            // batches that cannot start become a failed event, with the reason
            var (env2, sink2) = await Setup();
            env2.Agents(("local", false, true));
            var lostHook = await env2.AddGrouped(extra: new { agent = "local", post = Hook(sink2) });
            await env2.Post(S(lostHook["id"])!, S(lostHook["token"])!, Alert("Kernel errors"));
            for (var i = 0; i < 70; i++) { env2.Time.Advance(TimeSpan.FromSeconds(30)); await env2.Tick(); }
            var lost = await Ran(sink2, 1);
            Check.Equal("failed", S(lost["event"]));
            Check.Equal("nuc / Kernel errors is CRIT", S(lost["title"]));
            Check.Contains(S(lost["reason"]), "unavailable");
        });

        r.Add("hooks: an ungrouped webhook is watched too, and a failing, missing or hanging hook is recorded without stopping anything", async () =>
        {
            var (env, sink) = await Setup();
            var added = await env.Add(extra: new { post = Hook(sink) });   // no group key: every alert its own chat
            var id = S(added["id"])!; var token = S(added["token"])!;
            var started = await env.Post(id, token, Alert());
            Check.Equal("started", S(started.Body["status"]));
            env.FinishChats();
            AssistantSays(env, S(started.Body["sessionId"])!, "All good.\nVERDICT: resolved");
            env.Time.Advance(TimeSpan.FromSeconds(10));
            await env.Tick();
            var done = await Ran(sink, 1);
            Check.Equal("resolved", S(done["verdict"]));

            // a program that fails, one that does not exist, and one that hangs
            var failing = await env.Add(extra: new { name = "refuses", pre = Hook(sink, exit: 3) });
            var again = await env.Post(S(failing["id"])!, S(failing["token"])!, Alert("Disk"));
            Check.Equal("started", S(again.Body["status"]), "a failing hook does not stop the delivery");
            Check.True(HookLines(await env.History(S(failing["id"])!)).Any(x => x!.Contains("Before command") && x.Contains("FAILED") && x.Contains("exited 3: refused: simulated failure")), "the exit code and the program's own words are in the history");

            var missing = await env.Add(extra: new { name = "nope", pre = new { command = "netpi-no-such-program-xyz" } });
            await env.Post(S(missing["id"])!, S(missing["token"])!, Alert("Disk"));
            Check.True(HookLines(await env.History(S(missing["id"])!)).Any(x => x!.Contains("FAILED") && x.Contains("could not be started")), "a missing program says so");

            var hang = await env.Add(extra: new { name = "hang", pre = new { command = Environment.ProcessPath!, args = new[] { typeof(WebhookHooksTests).Assembly.Location, "--hook-sleep" }, timeoutSeconds = 1 } });
            var watch = System.Diagnostics.Stopwatch.StartNew();
            await env.Post(S(hang["id"])!, S(hang["token"])!, Alert("Hang"));
            Check.True(watch.Elapsed < TimeSpan.FromSeconds(10), "a hanging program is stopped at its timeout: " + watch.Elapsed);
            Check.True(HookLines(await env.History(S(hang["id"])!)).Any(x => x!.Contains("did not finish within 1 s")), "and says so");
        });

        r.Add("hooks: arguments are literal (no templates, no shell), and testHook runs a hook on a sample context", async () =>
        {
            var (env, sink) = await Setup();
            var evil = "x\"; echo pwned; \"";
            var added = await env.Add(extra: new { pre = Hook(sink, 0, "{{host}}", "--note=" + evil) });
            var id = S(added["id"])!; var token = S(added["token"])!;
            await env.Post(id, token, Alert("Kernel errors", output: evil));
            var args = sink.Calls().Single()["args"]!.AsArray().Select(S).ToList();
            Check.Equal("{{host}}", args[0], "nothing in the alert can reach an argument");
            Check.Equal("--note=" + evil, args[1], "and an argument is one argument, untouched");

            var withPost = await env.Add(extra: new { name = "tested", post = Hook(sink) });
            var ok = (await env.Ctx.RpcFake.CallAsync("webhooks.testHook", new { id = S(withPost["id"]), hook = "post" }))!;
            Check.Equal(true, ok["ok"]!.GetValue<bool>());
            Check.Contains(S(ok["detail"]), "exited 0");
            var sample = await Ran(sink, 2);
            Check.Equal("finished", S(sample["event"]));
            Check.Equal("real", S(sample["verdict"]), "the sample has the shape of a real finished run");
            var custom = (await env.Ctx.RpcFake.CallAsync("webhooks.testHook", new { id = S(withPost["id"]), hook = "post", context = new { @event = "asking", question = "ok?" } }))!;
            Check.Equal(true, custom["ok"]!.GetValue<bool>());
            Check.Equal("ok?", S((await Ran(sink, 3))["question"]));
            await Check.ThrowsAsync<RpcException>(() => env.Ctx.RpcFake.InvokeAsync("webhooks.testHook", new { id = S(withPost["id"]), hook = "pre" }));
            var withPre = await env.Add(extra: new { name = "adapter", pre = Hook(sink, 0, "--adapt:key+crit") });
            var answered = (await env.Ctx.RpcFake.CallAsync("webhooks.testHook", new { id = S(withPre["id"]), hook = "pre", context = new { alert = new { host = "sample-host", state = "CRIT" } } }))!;
            Check.Contains(S(answered["output"]), "\"key\":\"sample-host\"");   // what the command printed, so its answer can be checked before a payload arrives
            await Check.ThrowsAsync<RpcException>(() => env.Ctx.RpcFake.InvokeAsync("webhooks.testHook", new { id = S(withPost["id"]), hook = "middle" }));
        });

        r.Add("hooks: bad hook definitions are refused when saved, {} clears a hook, and an agent's change to a hook pauses the webhook as a proposal", async () =>
        {
            var (env, sink) = await Setup();
            async Task<string> Refused(object extra) => (await Check.ThrowsAsync<RpcException>(() => env.Add(extra: extra))).Message;
            Check.Contains(await Refused(new { name = "x", pre = new { args = new[] { "a" } } }), "needs a command");
            Check.Contains(await Refused(new { name = "x", post = new { command = "x", args = new object[] { 1 } } }), "array of at most 50 strings");
            Check.Contains(await Refused(new { name = "x", post = new { command = "x", timeoutSeconds = 999 } }), "1–300");
            Check.Contains(await Refused(new { name = "x", pre = new { command = "x", colour = "red" } }), "unknown field");
            Check.Contains(await Refused(new { name = "x", timeoutMinutes = 5000 }), "0–1440");

            var added = await env.Add(extra: new { pre = Hook(sink), post = Hook(sink), timeoutMinutes = 7 });
            var id = S(added["id"])!;
            Check.Equal(7, ((JsonObject)(await env.Get(id)))["timeoutMinutes"]!.GetValue<int>());
            var cleared = (await env.Ctx.RpcFake.CallAsync("webhooks.update", new { id, pre = new { } }))!;
            Check.True(cleared["pre"] is null, "{} clears the Before command");
            Check.True(cleared["post"] is JsonObject, "and leaves the post hook");

            var proposed = (await env.Ctx.RpcFake.CallAsync("webhooks.update", new { id, post = new { command = "evil" }, proposedBy = "session:ses_1" }))!;
            Check.Equal(false, proposed["enabled"]!.GetValue<bool>());
        });
    }
}
