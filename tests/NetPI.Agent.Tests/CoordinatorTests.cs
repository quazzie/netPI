using System.Text.Json.Nodes;
using NetPI.Coordinator;

namespace NetPI.Agent.Tests;

/// <summary>The coordinator surface (sessions.dispatch, runs.result) over the real runtime, agents, profiles and workspaces.</summary>
public static class CoordinatorTests
{
    public static void Register(TestRunner t)
    {
        t.Add("coordinator: dispatch checks the agent, profile, project and workspace first, and a refused one leaves nothing", DispatchValidates);
        t.Add("coordinator: dispatch makes the chat, its workspace, agent and profile, fills in the brief and sends it, in one call", DispatchRuns);
        t.Add("coordinator: runs.result is the run's report, status, todo and workspace in one answer", Result);
        t.Add("tasks: a task waits for the one it depends on, starts when that one's run ended well, and the queue records how each ended", TasksInOrder);
        t.Add("tasks: a failed task blocks its dependents until it runs again; hold, cycles, an unknown agent; one free instance runs one task", TasksRules);
    }

    private static async Task<JsonObject> Task1(TestHost h, string id) =>
        ((JsonArray)(await h.Rpc.CallAsync("tasks.list", new { project = "MarioV3" }))!).OfType<JsonObject>().Single(x => (string?)x["id"] == id);

    private static async Task Status(TestHost h, string id, string status)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        JsonObject task;
        while ((string?)(task = await Task1(h, id))["status"] != status)
        {
            if (sw.ElapsedMilliseconds > 15_000) throw new AssertException($"{id} did not become {status}: {task.ToJsonString()}");
            await Task.Delay(25);
        }
    }

    /// <summary>
    /// The queue (idea-lsj5u2): WP02 waits for WP01 (done), WP01 starts at once on its agent's free instance, and WP02 is
    /// dispatched when WP01's run has ended well; each task's chat is its own, with the brief sent.
    /// </summary>
    private static async Task TasksInOrder()
    {
        var (h, _) = await Start();
        await using var _h = h;
        var gate = new TaskCompletionSource();
        h.Catalog.Handler = (r, ct) => r.Messages.Any(m => m.Text.Contains("first package"))
            ? Reply.Text("WP01 done.", c => gate.Task.WaitAsync(c))
            : Reply.Text("WP02 done.");
        var a = (await h.Rpc.CallAsync("tasks.add", new { project = "MarioV3", title = "WP01", text = "The first package.", agent = "qwen" }))!;
        var aId = (string)a["id"]!;
        Check.Equal("qwen-local", (string?)a["agent"], "the agent is resolved now, as a dispatch resolves it");
        var b = (await h.Rpc.CallAsync("tasks.add", new { project = "MarioV3", title = "WP02", text = "The second package.", agent = "qwen-local", after = new[] { aId } }))!;
        var bId = (string)b["id"]!;
        Check.Equal(false, (bool?)b["ready"]);
        Check.Contains(b["waitingFor"]!.ToJsonString(), aId, "it says what it waits for");

        await Status(h, aId, "running");
        var aSession = (string)(await Task1(h, aId))["sessionId"]!;
        Check.Equal("WP01", h.Sessions.GetSession(aSession)!.Title);
        Check.Equal("queued", (string?)(await Task1(h, bId))["status"], "WP02 waits while WP01 runs");

        gate.SetResult();
        await Status(h, aId, "done");
        await Status(h, bId, "done"); // it runs at once and ends at once: its "running" is too short to catch
        var bTask = await Task1(h, bId);
        Check.False(bTask["sessionId"]!.GetValue<string>() == aSession, "a chat of its own");
        Check.Equal("WP02 done.", h.Messages((string)bTask["sessionId"]!)[^1].Text);
        Check.Equal(2, ((JsonArray)(await h.Rpc.CallAsync("tasks.list", new { project = "MarioV3", status = "done" }))!).Count);
    }

    private static async Task TasksRules()
    {
        var (h, _) = await Start();
        await using var _h = h;
        h.Settings.Set("tasks.autoDispatch", JsonValue.Create(false));
        h.Catalog.Handler = (r, ct) => r.Messages.Any(m => m.Text.Contains("break"))
            ? Reply.Fail(new ModelException("the model broke", false))
            : Reply.Text("ok");
        var a = (string)(await h.Rpc.CallAsync("tasks.add", new { project = "MarioV3", title = "A", text = "please break", agent = "qwen-local" }))!["id"]!;
        var b = (string)(await h.Rpc.CallAsync("tasks.add", new { project = "MarioV3", title = "B", text = "fine", agent = "qwen-local", after = new object[] { new { task = a, when = "done" } } }))!["id"]!;

        // the checks a dispatch would make come first, and a circle is refused
        Check.Equal("bad_request", (await Check.ThrowsAsync<RpcException>(() => h.Rpc.CallAsync("tasks.add", new { project = "MarioV3", title = "C", text = "x", agent = "nobody" }))).Code);
        Check.Equal("bad_request", (await Check.ThrowsAsync<RpcException>(() => h.Rpc.CallAsync("tasks.update", new { id = a, after = new[] { b } }))).Code, "a circle");
        Check.Equal("bad_request", (await Check.ThrowsAsync<RpcException>(() => h.Rpc.CallAsync("tasks.add", new { project = "MarioV3", title = "D", text = "x", after = new[] { "task-none" } }))).Code);

        // dispatch off: nothing starts
        await Task.Delay(300);
        Check.Equal("queued", (string?)(await Task1(h, a))["status"], "tasks.autoDispatch off: the queue only keeps them");

        // on: A runs and fails; B stays queued and says why, until A is queued again
        h.Settings.Set("tasks.autoDispatch", JsonValue.Create(true));
        await h.Rpc.CallAsync("tasks.update", new { id = b, hold = true });
        await h.Rpc.CallAsync("tasks.update", new { id = a, title = "A" }); // any change pumps the queue
        await Status(h, a, "failed");
        Check.Contains((string?)(await Task1(h, a))["error"] ?? "", "broke");
        var waiting = (await Task1(h, b))["waitingFor"]!.ToJsonString();
        Check.Contains(waiting, "held", "hold is a reason too");
        Check.Contains(waiting, "failed", "and the failed one it waits for");

        h.Catalog.Handler = (r, ct) => Reply.Text("ok");
        await h.Rpc.CallAsync("tasks.update", new { id = a, status = "queued" });
        await Status(h, a, "done");
        Check.Equal("queued", (string?)(await Task1(h, b))["status"], "held: not dispatched although A is done");
        await h.Rpc.CallAsync("tasks.update", new { id = b, hold = false });
        await Status(h, b, "done");

        // one instance of the agent: two ready tasks run one after the other
        var gate = new TaskCompletionSource();
        h.Catalog.Handler = (r, ct) => Reply.Text("slow", c => gate.Task.WaitAsync(c));
        h.Settings.Set("agents.solo-on", JsonNode.Parse("""{ "model": "fake/solo", "instances": 1 }"""));
        await h.Bus.DrainAsync();
        var x = (string)(await h.Rpc.CallAsync("tasks.add", new { project = "MarioV3", title = "X", text = "x", agent = "solo-on" }))!["id"]!;
        var y = (string)(await h.Rpc.CallAsync("tasks.add", new { project = "MarioV3", title = "Y", text = "y", agent = "solo-on" }))!["id"]!;
        await Status(h, x, "running");
        await Task.Delay(300);
        Check.Equal("queued", (string?)(await Task1(h, y))["status"], "the one instance is busy: Y waits in the queue, not in the scheduler");
        gate.SetResult();
        await Status(h, y, "done");

        Check.Equal(true, NetPiJson.ToNode(await h.Rpc.InvokeAsync("tasks.remove", new { id = x }))!.GetValue<bool>());
        Check.False(((JsonArray)(await h.Rpc.CallAsync("tasks.list", new { project = "MarioV3" }))!).OfType<JsonObject>().Any(t => (string?)t["id"] == x));
    }

    private static async Task<(TestHost H, ProjectInfo Project)> Start()
    {
        var h = await TestHost.StartAsync(x =>
        {
            x.Settings.SetQuiet("agents.qwen-local", JsonNode.Parse("""{ "model": "fake/local" }"""));
            x.Settings.SetQuiet("agents.space-bunny-alpha", JsonNode.Parse("""{ "model": "fake/local" }"""));
            x.Settings.SetQuiet("agents.solo-off", JsonNode.Parse("""{ "model": "fake/solo", "disabled": true }"""));
            x.Settings.SetQuiet("profiles", JsonNode.Parse("""{ "minimal-coding": { "name": "Minimal coding", "prompt": "Be minimal." } }"""));
        });
        await h.StartPluginAsync(new NetPI.Profiles.ProfilesPlugin());
        await h.StartPluginAsync(new NetPI.Workspaces.WorkspacePlugin());
        await h.StartPluginAsync(new CoordinatorPlugin());
        var dir = Path.Combine(h.Workspace, "mario");
        Directory.CreateDirectory(dir);
        return (h, h.Sessions.CreateProject("MarioV3", dir));
    }

    private const string NL = "\n";

    private static int Chats(TestHost h) => h.Sessions.ListSessions(new SessionQuery { Limit = 500 }).Count;

    private static async Task<RpcException> Refused(TestHost h, object p)
    {
        JsonNode? answer;
        try { answer = await h.Rpc.CallAsync("sessions.dispatch", p); }
        catch (RpcException ex) { return ex; }
        throw new AssertException($"expected the dispatch to be refused: {System.Text.Json.JsonSerializer.Serialize(p)} → {answer?.ToJsonString()}");
    }

    private static async Task DispatchValidates()
    {
        var (h, _) = await Start();
        await using var _h = h;
        var before = Chats(h);

        var ex = await Refused(h, new { project = "MarioV3", agent = "gemma", text = "go" });
        Check.Equal("bad_request", ex.Code);
        Check.Contains(ex.Message, "No agent 'gemma'. Agents: ");
        Check.Contains(ex.Message, "space-bunny-alpha");
        Check.Contains((await Refused(h, new { project = "MarioV3", agent = "l", text = "go" })).Message, "could be", "a part of several keys is not a guess");
        Check.Contains((await Refused(h, new { project = "MarioV3", agent = "solo-off", text = "go" })).Message, "switched off");
        Check.Contains((await Refused(h, new { project = "MarioV3", profile = "nope", text = "go" })).Message, "Profiles: minimal-coding");
        Check.Contains((await Refused(h, new { project = "Mario", text = "go" })).Message, "Projects: MarioV3");
        Check.Contains((await Refused(h, new { project = "MarioV3", workspace = "qwen9", text = "go" })).Message, "or create: true");
        Check.Contains((await Refused(h, new { project = "MarioV3", send = true })).Message, "Nothing to send");
        Check.Contains((await Refused(h, new { project = "MarioV3", textFile = "relative.md" })).Message, "absolute path");
        Check.Equal(before, Chats(h), "nothing was made");
        Check.Equal(0, h.Catalog.Calls, "nothing ran");
    }

    private static async Task DispatchRuns()
    {
        var (h, project) = await Start();
        await using var _h = h;
        h.Catalog.Handler = (r, ct) => Reply.Text("on it");
        var brief = Path.Combine(h.Workspace, "WP01.md");
        await File.WriteAllTextAsync(brief, "Work in {{workspacePath}} on {{branch}}; you are {{sessionId}}.");

        var r = (await h.Rpc.CallAsync("sessions.dispatch", new
        {
            project = "marioV3", title = "WP01 PNG codec", workspace = new { name = "qwen1", create = true },
            agent = "bunny", profile = "Minimal coding", textFile = brief,
        }))!;
        var sid = (string)r["sessionId"]!;
        Check.Equal("space-bunny-alpha", (string?)r["agent"], "a part of exactly one key is that agent");
        Check.Equal("minimal-coding", (string?)r["profile"]);
        Check.True(r["workspace"]!["created"]!.GetValue<bool>(), r.ToJsonString());
        var wsPath = (string)r["workspace"]!["path"]!;
        Check.True(r["sent"]!.GetValue<bool>());

        await h.IdleAsync(sid);
        var s = h.Sessions.GetSession(sid)!;
        Check.Equal("WP01 PNG codec", s.Title);
        Check.Equal(project.Id, s.ProjectId);
        Check.Equal((string?)r["workspace"]!["id"], (string?)s.Meta?["workspaceId"]);
        Check.Equal("space-bunny-alpha", (string?)s.Meta?["agent"]);
        Check.Equal("minimal-coding", (string?)s.Meta?["profile"]);
        var first = h.Messages(sid).First(m => m.Role == MessageRole.User).Text;
        Check.Contains(first, $"Work in {wsPath} on");
        Check.Contains(first, $"you are {sid}.");
        Check.Equal("on it", h.Messages(sid)[^1].Text);

        // the workspace is taken while the worker runs in it: a second dispatch into it is refused
        var gate = new TaskCompletionSource();
        h.Catalog.Handler = (q, ct) => Reply.Text("busy", c => gate.Task.WaitAsync(c));
        await h.Rpc.CallAsync("agent.send", new { sessionId = sid, text = "next" });
        await Wait.Until(() => h.Runtime.GetBySession(sid)!.Status == AgentStatus.Running, "the worker runs");
        var busy = await Refused(h, new { project = "MarioV3", workspace = "qwen1", text = "another package" });
        Check.Equal("workspace_busy", busy.Code);
        gate.SetResult();
        await h.IdleAsync(sid);

        // set up only (send: false): no run starts
        var quiet = (await h.Rpc.CallAsync("sessions.dispatch", new { project = "MarioV3", workspace = "qwen1", send = false }))!;
        Check.False(quiet["sent"]!.GetValue<bool>());
        Check.Equal(0, h.Runtime.GetBySession((string)quiet["sessionId"]!)?.Runs ?? 0);
    }

    private static async Task Result()
    {
        var (h, _) = await Start();
        await using var _h = h;
        h.AddTool(new FakeTool("work", (c, a, ct) => Task.FromResult(ToolResult.Ok("building" + NL + "12 passed, 2 failed, 0 skipped, 14 total in 3.1s" + NL + "worked"))));
        var calls = 0;
        h.Catalog.Handler = (r, ct) => Interlocked.Increment(ref calls) switch
        {
            1 => Reply.Text("Reading the brief."),
            2 => Reply.Tool("work", new { step = 1 }),
            _ => Reply.Text("## Report\nDone: the codec round-trips. Commit abc123."),
        };
        var r = (await h.Rpc.CallAsync("sessions.dispatch", new { project = "MarioV3", title = "WP02", text = "first" }))!;
        var sid = (string)r["sessionId"]!;
        await h.IdleAsync(sid);
        h.Sessions.UpdateSession(sid, s =>
        {
            s.Meta ??= new JsonObject();
            s.Meta["todo"] = JsonNode.Parse("""[{ "text": "decode", "status": "done" }, { "text": "encode", "status": "in_progress" }, { "text": "docs", "status": "pending" }]""");
        });
        await h.Rpc.CallAsync("agent.send", new { sessionId = sid, text = "second" });
        await h.IdleAsync(sid);

        var res = (await h.Rpc.CallAsync("runs.result", new { sessionId = sid }))!;
        Check.Equal("WP02", (string?)res["title"]);
        Check.Equal("idle", (string?)res["status"]);
        Check.Equal(2, res["runs"]!.GetValue<int>());
        Check.Equal("## Report\nDone: the codec round-trips. Commit abc123.", (string?)res["report"], "the last run's final text, whole");
        Check.Equal("work", (string?)res["lastTool"]!["name"]);
        Check.Equal(1, res["todo"]!["done"]!.GetValue<int>());
        Check.Equal(3, res["todo"]!["total"]!.GetValue<int>());
        Check.Equal("encode", (string?)res["todo"]!["current"]);
        Check.True(res["durationMs"]!.GetValue<long>() >= 0);
        Check.Equal(0, ((JsonArray)res["commits"]!).Count, "not a repository: no commits");
        Check.Equal("12 passed, 2 failed, 0 skipped, 14 total in 3.1s", (string?)res["tests"]!["line"], "the latest test tally in the run's tool output");
        Check.Equal("work", (string?)res["tests"]!["tool"]);
        Check.Equal(true, (bool?)res["tests"]!["failed"]);

        // the tallies other runners print, and which of them count a failure
        static ChatMessage Out(string text) => new() { Role = MessageRole.Tool, Parts = [new ToolResultPart { Name = "bash", CallId = "c", Content = text }] };
        static JsonNode? Summary(params string[] outputs) => NetPiJson.ToNode(NetPI.Coordinator.Coordinator.TestSummary([.. outputs.Select(Out)]));
        Check.Equal(null, Summary("built in 3s", "no tests here"));
        Check.Equal(false, (bool?)Summary("Passed!  - Failed:     0, Passed:   123, Skipped:     0, Total:   123")!["failed"]);
        Check.Equal(true, (bool?)Summary("Failed!  - Failed:     2, Passed:    10, Skipped:     0, Total:    12")!["failed"]);
        Check.Equal(true, (bool?)Summary("Tests:       2 failed, 10 passed, 12 total")!["failed"]);
        Check.Equal(false, (bool?)Summary("test result: ok. 12 passed; 0 failed; 0 ignored")!["failed"]);
        Check.Equal("=== 10 passed in 1.20s ===", (string?)Summary("=== 3 failed, 10 passed in 1.20s ===", "=== 10 passed in 1.20s ===")!["line"], "the newest tally wins");

        var byRun = (await h.Rpc.CallAsync("runs.result", new { runId = (string)res["runId"]! }))!;
        Check.Equal(sid, (string?)byRun["sessionId"]);
        try { await h.Rpc.CallAsync("runs.result", new { }); throw new AssertException("expected bad_request"); }
        catch (RpcException ex) { Check.Equal("bad_request", ex.Code); }
    }
}
