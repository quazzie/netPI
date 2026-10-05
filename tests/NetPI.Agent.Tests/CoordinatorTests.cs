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
        h.AddTool(new FakeTool("work", (c, a, ct) => Task.FromResult(ToolResult.Ok("worked"))));
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

        var byRun = (await h.Rpc.CallAsync("runs.result", new { runId = (string)res["runId"]! }))!;
        Check.Equal(sid, (string?)byRun["sessionId"]);
        try { await h.Rpc.CallAsync("runs.result", new { }); throw new AssertException("expected bad_request"); }
        catch (RpcException ex) { Check.Equal("bad_request", ex.Code); }
    }
}
