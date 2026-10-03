using System.Text.Json;
using System.Text.Json.Nodes;
using NetPI.Plan;

namespace NetPI.Agent.Tests;

/// <summary>Plan mode with the real runner and a scripted model: the gate, the plan's wait and the user's decisions.</summary>
public static class PlanTests
{
    public static void Register(TestRunner t)
    {
        t.Add("plan: the policy table (shell and browser never, writes blocked, readers and the plan's tools pass, MCP by hint or pattern)", PolicyTable);
        t.Add("plan: a spawn is rewritten to read-only tools without a checkout; the child tool list leaves out what can change things", SpawnRewrite);
        t.Add("plan: plan_submit reads lenient arguments and refuses a plan without steps", Parsing);
        t.Add("plan: blocked tools never run, readers do, the plan waits with its instance given back, and approval lets writes through", BlockedThenApproved);
        t.Add("plan: a chat that is not in plan mode writes as ever and plan_submit says so", NotInPlanMode);
        t.Add("plan: revise returns the feedback, the next submit is revision 2; a decided plan can't be decided again", Revise);
        t.Add("plan: a new message from the user ends the wait and reopens the plan for revision", Steered);
        t.Add("plan: approving after the run is gone does the same work and tells the chat with a message", ApproveWithoutRun);
        t.Add("plan: approve in a new chat creates it with the plan, attaches the idea, archives the plan chat", ApproveInNewChat);
        t.Add("plan: save as idea keeps waiting, save as file writes docs/plans, approving later updates the same idea", SaveIdeaAndFile);
        t.Add("plan: research subagents are bound by plan mode and get no checkout; the setting turns it off", Subagents);
        t.Add("plan: a subagent is bound while the chat that started it is in plan mode", ChildBound);
        t.Add("plan: MCP tools pass when read-only or matching plan.mcpAllow, everything else is blocked", Mcp);
        t.Add("plan: plan_enter waits for the user's answer and enters (or not)", EnterOffer);
        t.Add("plan: the plugin reloading ends the wait as withdrawn, and the same plan submitted again keeps its revision", ReloadDuringWait);
        t.Add("plan: /plan enters (with a task it starts the run), shows, leaves; the chat is told once per change", Command);
        t.Add("plan: a fork starts without plan mode", ForkReset);
    }

    private static async Task<TestHost> StartAsync()
    {
        var h = await TestHost.StartAsync();
        await h.StartPluginAsync(new PlanPlugin());
        return h;
    }

    private static object Plan(string title = "Add retry", params string[] steps) => new
    {
        title,
        summary = "Retry the upload when it fails.",
        steps = (steps.Length == 0 ? ["Write the retry loop", "Test it"] : steps).Select(s => new { text = s }).ToArray(),
        files = new[] { new { path = "src/Upload.cs", note = "the loop" } },
        risks = new[] { "double sends" },
        tests = new[] { "unit" },
    };

    private static FakeTool Recorder(string name, List<string> ran, bool readOnly = false, string category = "general") => new(name, (c, a, ct) =>
    {
        lock (ran) ran.Add(name);
        return Task.FromResult(ToolResult.Ok($"{name} ran"));
    }, readOnly, category);

    private static List<ToolResultPart> Results(TestHost h, string sessionId) =>
        [.. h.Messages(sessionId).Where(m => m.Role == MessageRole.Tool).SelectMany(m => m.ToolResults)];

    private static ToolResultPart Result(TestHost h, string sessionId, string tool) => Results(h, sessionId).Last(r => r.Name == tool);

    private static async Task<JsonNode> ChangedAsync(TestHost h, string sessionId, string status, int revision = 0)
    {
        JsonNode? Find() => h.Bus.OfType("plan.changed").Select(FakeBus.Data)
            .LastOrDefault(d => (string?)d["sessionId"] == sessionId && (string?)d["status"] == status && (revision == 0 || (int?)d["revision"] == revision));
        await Wait.Until(() => Find() is not null, $"plan {status} (revision {revision})");
        return Find()!;
    }

    private static string? ModeOf(TestHost h, string sessionId) => (string?)h.Sessions.GetSession(sessionId)?.Meta?["planMode"]?["state"];

    private static List<string?> Notices(TestHost h, string sessionId) =>
        [.. h.Messages(sessionId).Where(m => m.Role == MessageRole.Notice && m.MetaString("kind") is "plan" or "plan-off").Select(m => m.MetaString("kind"))];

    /// <summary>ideas.add / ideas.update / ideas.attach as the ideas plugin answers them, recording what they were asked.</summary>
    private static List<(string Method, JsonNode? Params)> FakeIdeas(TestHost h)
    {
        var calls = new List<(string, JsonNode?)>();
        RpcHandler Record(string method, object? result) => (req, _) =>
        {
            lock (calls) calls.Add((method, JsonNode.Parse(req.Params.GetRawText())));
            return Task.FromResult(result);
        };
        h.Rpc.Register("ideas.add", Record("ideas.add", new JsonObject { ["id"] = "idea_1", ["sections"] = new JsonArray(new JsonObject { ["id"] = "sec_1" }) }));
        h.Rpc.Register("ideas.update", Record("ideas.update", new JsonObject { ["id"] = "idea_1" }));
        h.Rpc.Register("ideas.attach", Record("ideas.attach", true));
        return calls;
    }

    // ------------------------------------------------------------------ the policy

    private static ToolDefinition D(string name, bool readOnly = false, string category = "general") =>
        new() { Name = name, Description = "x", ReadOnly = readOnly, Category = category };

    private static string? Block(string name, ToolDefinition? def = null, bool readOnlyCall = false, McpToolInfo? mcp = null, params string[] allow) =>
        PlanPolicy.Block(name, def ?? D(name), readOnlyCall, mcp, allow);

    private static Task PolicyTable()
    {
        Check.Contains(Block("bash"), "no shell");
        Check.Contains(Block("pwsh", D("pwsh", readOnly: true)), "no shell", "a read-only flag does not open a shell");
        Check.Contains(Block("ssh"), "no shell");
        Check.Contains(Block("process", readOnlyCall: true), "no shell", "not even a call that only lists processes");
        Check.Contains(Block("browser"), "browser");
        Check.Contains(Block("windows", readOnlyCall: true), "live windows", "the windows tool is blocked like the browser, even a call that only reads");
        Check.Contains(Block("write"), "read-only");
        Check.Contains(Block("edit"), "describe the change in the plan".Replace("describe", "Describe"));
        Check.Contains(Block("goal_set"), "can change things");
        Check.Equal<string?>(null, Block("read", D("read", readOnly: true)));
        Check.Equal<string?>(null, Block("web_fetch", D("web_fetch", readOnly: true)));
        Check.Equal<string?>(null, Block("ask_user"));
        Check.Equal<string?>(null, Block("todo_write"));
        Check.Equal<string?>(null, Block("plan_submit"));
        Check.Equal<string?>(null, Block("agent_spawn"));
        Check.Equal<string?>(null, Block("agent"));
        Check.Equal<string?>(null, Block("ideas", readOnlyCall: true), "a call a tool with actions says only reads");
        Check.Contains(Block("ideas"), "can change things");
        Check.Contains(Block("plan_enter"), "already in plan mode");

        var mcp = D("mcp_ha_x_1", category: "mcp");
        Check.Equal<string?>(null, Block("mcp_ha_x_1", D("mcp_ha_x_1", readOnly: true, category: "mcp")), "the server's own read-only claim");
        var service = new McpToolInfo("ha", "ha_call_service", false);
        Check.Contains(Block("mcp_ha_x_1", mcp, mcp: service), "ha/ha_call_service");
        Check.Contains(Block("mcp_ha_x_1", mcp, mcp: service, allow: ["ha_get_*", "ha_config_get_*"]), "not one of them");
        Check.Equal<string?>(null, Block("mcp_ha_x_1", mcp, mcp: new McpToolInfo("ha", "ha_get_state", false), allow: ["HA_GET_*"]), "a pattern is case-insensitive");
        Check.Equal<string?>(null, Block("mcp_ha_x_1", mcp, mcp: new McpToolInfo("ha", "ha_search", false), allow: ["ha/ha_search"]), "or server/name");
        Check.Equal<string?>(null, Block("mcp_ha_x_1", mcp, mcp: new McpToolInfo("ha", "ha_config_get_scene", true)));
        Check.Contains(Block("mcp_ha_x_1", mcp, mcp: new McpToolInfo("ha", "ha_config_set_scene", false), allow: ["ha_config_get_*"]), "not one of them");
        Check.Equal<string?>(null, Block("mcp_search", D("mcp_search", readOnly: true, category: "mcp")));
        Check.Contains(Block("mcp_call", D("mcp_call", category: "mcp")), "not resolved");
        return Task.CompletedTask;
    }

    private static Task SpawnRewrite()
    {
        var tools = new[] { "read", "grep" };
        var one = JsonNode.Parse(PlanPolicy.RewriteSpawn(
            """{"task":"look","tools":["read","write","bash"],"isolated":true,"workspace":"new","instructions":"be quick"}""", tools))!;
        Check.Equal("read", string.Join(",", one["tools"]!.AsArray().Select(n => (string?)n)), "only what it asked for and may have");
        Check.Equal(false, (bool?)one["isolated"]);
        Check.True(one["workspace"] is null, "no checkout of its own");
        Check.Contains((string?)one["instructions"], "be quick");
        Check.Contains((string?)one["instructions"], "research for a plan");

        var none = JsonNode.Parse(PlanPolicy.RewriteSpawn("""{"task":"look","tools":["write"]}""", tools))!;
        Check.Equal("read,grep", string.Join(",", none["tools"]!.AsArray().Select(n => (string?)n)), "nothing it asked for passes: the read-only set");

        var batch = JsonNode.Parse(PlanPolicy.RewriteSpawn("""{"subagents":[{"task":"a"},{"task":"b","isolated":true}],"background":true}""", tools))!;
        foreach (var item in batch["subagents"]!.AsArray())
        {
            Check.Equal("read,grep", string.Join(",", item!["tools"]!.AsArray().Select(n => (string?)n)));
            Check.Equal(false, (bool?)item["isolated"]);
        }
        Check.Equal(true, (bool?)batch["background"], "the rest of the call is left alone");

        var ran = new List<string>();
        var offered = PlanPolicy.ChildTools([
            Recorder("read", ran, readOnly: true), Recorder("write", ran), Recorder("bash", ran, readOnly: true), Recorder("ask_user", ran, readOnly: true),
            Recorder("plan_submit", ran), Recorder("agent_spawn", ran), Recorder("mcp_search", ran, readOnly: true, category: "mcp"), Recorder("mcp_call", ran, category: "mcp"),
            Recorder("mcp_ha_get_state_1", ran, readOnly: true, category: "mcp"), Recorder("grep", ran, readOnly: true),
        ]);
        Check.Equal("grep,mcp_call,mcp_search,read", string.Join(",", offered), "readers and the MCP gateways; no shell, no remote tool by name, nothing of the plan's");
        return Task.CompletedTask;
    }

    private static Task Parsing()
    {
        Check.True(PlanBody.TryParse(JsonSerializer.SerializeToElement(new
        {
            title = "T", summary = "S", steps = new object[] { "one", new { step = "two", description = "because" } },
            files = new object[] { "a.cs", new { file = "b.cs", what = "split" } }, risks = "one risk", questions = new[] { "which db?" },
        }), out var plan, out _));
        Check.Equal(2, plan.Steps.Count);
        Check.Equal("because", plan.Steps[1].Detail);
        Check.Equal("b.cs", plan.Files[1].Path);
        Check.Equal("split", plan.Files[1].Note);
        Check.Equal("one risk", plan.Risks.Single());
        Check.Equal("which db?", plan.OpenQuestions.Single());
        Check.Contains(plan.Markdown(), "1. one\n2. two — because\n");
        Check.Contains(plan.Markdown(), "## Files\n- `a.cs`\n- `b.cs` — split\n");

        // arguments sent as a JSON string, a missing title taken from the summary
        var asString = JsonSerializer.SerializeToElement("""{"summary":"Do the thing.\nMore.","steps":["x"]}""");
        Check.True(PlanBody.TryParse(asString, out var second, out _));
        Check.Equal("Do the thing.", second.Title);

        Check.False(PlanBody.TryParse(JsonSerializer.SerializeToElement(new { title = "T", steps = Array.Empty<string>() }), out _, out var error));
        Check.Contains(error, "needs \"steps\"");

        var many = Enumerable.Range(0, 60).Select(i => "step " + i).ToArray();
        Check.True(PlanBody.TryParse(JsonSerializer.SerializeToElement(new { title = "T", steps = many }), out var capped, out _));
        Check.Equal(PlanBody.MaxSteps, capped.Steps.Count);
        Check.True(capped.SameAs(PlanBody.FromJson(capped.ToJson())), "a plan survives its own JSON");
        return Task.CompletedTask;
    }

    // ------------------------------------------------------------------ the flow

    private static async Task BlockedThenApproved()
    {
        await using var h = await StartAsync();
        var ideas = FakeIdeas(h);
        var ran = new List<string>();
        h.AddTool(Recorder("read", ran, readOnly: true));
        h.AddTool(Recorder("write", ran));
        h.AddTool(Recorder("bash", ran));
        var other = h.NewSession(model: "fake/solo");
        var s = h.NewSession(model: "fake/solo"); // one instance
        Check.Equal("planning", (string?)(await h.Rpc.CallAsync("plan.enter", new { sessionId = s.Id }))!["mode"]);
        var step = 0;
        h.Catalog.Handler = (r, ct) => r.SessionId == other.Id ? Reply.Text("the other chat ran") : Interlocked.Increment(ref step) switch
        {
            1 => Reply.Tools(Reply.Call("read", new { path = "a" }), Reply.Call("write", new { path = "a", content = "x" }), Reply.Call("bash", new { command = "ls" })),
            2 => Reply.Tool("plan_submit", Plan()),
            3 => Reply.Tool("write", new { path = "a", content = "x" }),
            _ => Reply.Text("done"),
        };
        await h.SendAsync(s.Id, "add a retry to the uploader");
        var awaiting = await ChangedAsync(h, s.Id, "awaiting", 1);
        var planId = (string)awaiting["planId"]!;

        // blocked calls never ran; the reader did
        var results = Results(h, s.Id);
        Check.False(results[0].IsError);
        Check.True(results[1].IsError && results[2].IsError);
        Check.Contains(results[1].Content, "Plan mode is read-only: write is blocked");
        Check.Contains(results[2].Content, "Plan mode has no shell");
        Check.Equal("read", string.Join(",", ran));
        Check.Equal(1, Notices(h, s.Id).Count);
        Check.Equal("plan", Notices(h, s.Id)[0]);
        Check.Equal("awaiting", ModeOf(h, s.Id));

        // the run waits with its instance given back: another chat takes the only one
        await Wait.Until(() => h.Runtime.GetBySession(s.Id)?.Status == AgentStatus.Yielded, "the run waits with its slot given back");
        Check.Equal("waiting for your decision on the plan", h.Runtime.GetBySession(s.Id)!.Activity);
        await h.SendAsync(other.Id, "meanwhile");
        await h.IdleAsync(other.Id);
        Check.Equal("the other chat ran", h.Messages(other.Id)[^1].Text);

        var plan = (await h.Rpc.CallAsync("plan.get", new { planId }))!;
        Check.Equal("Add retry", (string?)plan["title"]);
        Check.Equal(1, (int?)plan["revision"]);
        Check.Contains((string?)plan["markdown"], "## Steps\n1. Write the retry loop");
        var listed = (await h.Rpc.CallAsync("plan.list", new { }))!.AsArray();
        Check.Equal(true, (bool?)listed.Single()!["waiting"]);
        Check.Equal(0, ideas.Count, "nothing saved before the decision");

        var answered = (await h.Rpc.CallAsync("plan.answer", new { planId, decision = "approve" }))!;
        Check.Equal("approved", (string?)answered["status"]);
        Check.Equal("idea_1", (string?)answered["ideaId"]);
        Check.Equal(true, (bool?)answered["todos"]);
        await h.IdleAsync(s.Id);

        var submit = Result(h, s.Id, "plan_submit");
        Check.Contains(submit.Content, "The user approved your plan “Add retry” (revision 1). Plan mode is off");
        Check.Contains(submit.Content, "It is saved as idea idea_1.");
        Check.Equal("approved", (string?)submit.Details!["status"]);
        Check.False(Result(h, s.Id, "write").IsError, "after the approval the write runs");
        Check.Equal("read,write", string.Join(",", ran), "the shell never ran, the write ran once");
        Check.Equal("approved", ModeOf(h, s.Id));
        Check.Equal("plan,plan-off", string.Join(",", Notices(h, s.Id)), "told once on entering and once on leaving");
        var todo = (JsonArray)h.Sessions.GetSession(s.Id)!.Meta!["todo"]!;
        Check.Equal("Write the retry loop", (string?)todo[0]!["text"]);
        Check.Equal("pending", (string?)todo[0]!["status"]);
        var added = ideas.Single(c => c.Method == "ideas.add").Params!["idea"]!;
        Check.Equal("planned", (string?)added["status"]);
        Check.Equal("plan", (string?)added["tags"]![0]);
        Check.Equal("plan", (string?)added["sections"]![0]!["kind"]);
        Check.Equal(s.Id, (string?)ideas.Single(c => c.Method == "ideas.add").Params!["sessionId"]);
        Check.Equal(0, h.Scheduler!.Snapshot().Where(x => x.Key == "fake/solo").Sum(x => x.Busy), "the slot is free again");
        Check.Contains((string?)await h.Rpc.InvokeAsync("plan.command", new { sessionId = s.Id, args = "show" }), "was approved");
        await h.Rpc.InvokeAsync("plan.enter", new { sessionId = s.Id });
        Check.Contains((string?)await h.Rpc.InvokeAsync("plan.command", new { sessionId = s.Id, args = "show" }), "no plan yet", "a new plan mode starts without the approved plan");
        Check.Equal(null, h.Sessions.GetSession(s.Id)!.Meta!["planMode"]!["planId"]);
        Check.Equal("not_found", (await Check.ThrowsAsync<RpcException>(() => h.Rpc.InvokeAsync("plan.answer", new { planId = "plan_nope", decision = "approve" }))).Code);
        Check.Equal("bad_request", (await Check.ThrowsAsync<RpcException>(() => h.Rpc.InvokeAsync("plan.answer", new { planId, decision = "approve" }), "approved already")).Code);
    }

    private static async Task NotInPlanMode()
    {
        await using var h = await StartAsync();
        var ran = new List<string>();
        h.AddTool(Recorder("write", ran));
        var s = h.NewSession();
        h.Catalog.Handler = (r, ct) => Reply.HasToolResult(r) ? Reply.Text("ok") : Reply.Tools(Reply.Call("write", new { path = "a" }), Reply.Call("plan_submit", Plan()));
        await h.SendAsync(s.Id, "go");
        await h.IdleAsync(s.Id);
        Check.Equal("write", string.Join(",", ran));
        var submit = Result(h, s.Id, "plan_submit");
        Check.True(submit.IsError);
        Check.Contains(submit.Content, "not in plan mode");
        Check.Equal(0, Notices(h, s.Id).Count, "a chat that never was in plan mode is told nothing");
    }

    private static async Task Revise()
    {
        await using var h = await StartAsync();
        var s = h.NewSession();
        await h.Rpc.InvokeAsync("plan.enter", new { sessionId = s.Id });
        var step = 0;
        h.Catalog.Handler = (r, ct) => Interlocked.Increment(ref step) switch
        {
            1 => Reply.Tool("plan_submit", Plan("Add retry", "Loop")),
            2 => Reply.Tool("plan_submit", Plan("Add retry", "Queue", "Drain it")),
            _ => Reply.Text("ok"),
        };
        await h.SendAsync(s.Id, "go");
        var first = await ChangedAsync(h, s.Id, "awaiting", 1);
        var planId = (string)first["planId"]!;
        Check.Equal("bad_request", (await Check.ThrowsAsync<RpcException>(() => h.Rpc.InvokeAsync("plan.answer", new { planId, decision = "revise", feedback = " " }), "no feedback")).Code);
        await h.Rpc.InvokeAsync("plan.answer", new { planId, decision = "revise", feedback = "use a queue, not a loop" });
        var second = await ChangedAsync(h, s.Id, "awaiting", 2);
        Check.Equal(planId, (string?)second["planId"], "one plan, a new revision");
        Check.Contains(Result(h, s.Id, "plan_submit").Content, "The user asked for changes to revision 1:\nuse a queue, not a loop");
        Check.Equal("awaiting", ModeOf(h, s.Id));

        await h.Rpc.InvokeAsync("plan.answer", new { planId, decision = "approve", feedback = "go ahead" });
        await h.IdleAsync(s.Id);
        var plan = (await h.Rpc.CallAsync("plan.get", new { sessionId = s.Id }))!;
        Check.Equal("approved", (string?)plan["status"]);
        Check.Equal(2, plan["revisions"]!.AsArray().Count);
        Check.Equal("use a queue, not a loop", (string?)plan["revisions"]![0]!["feedback"]);
        Check.Equal("Queue", (string?)plan["plan"]!["steps"]![0]!["text"]);
        var last = Results(h, s.Id).Last(r => r.Name == "plan_submit");
        Check.Contains(last.Content, "They added: go ahead");
        Check.Equal("bad_request", (await Check.ThrowsAsync<RpcException>(() => h.Rpc.InvokeAsync("plan.answer", new { planId, decision = "revise", feedback = "again" }), "decided")).Code);
    }

    private static async Task Steered()
    {
        await using var h = await StartAsync();
        var s = h.NewSession();
        await h.Rpc.InvokeAsync("plan.enter", new { sessionId = s.Id });
        var step = 0;
        h.Catalog.Handler = (r, ct) => Interlocked.Increment(ref step) == 1 ? Reply.Tool("plan_submit", Plan()) : Reply.Text("noted");
        await h.SendAsync(s.Id, "go");
        var awaiting = await ChangedAsync(h, s.Id, "awaiting", 1);
        await h.SendAsync(s.Id, "also cover the cache");
        await h.IdleAsync(s.Id);
        var result = Result(h, s.Id, "plan_submit");
        Check.Contains(result.Content, "No decision: the user wrote a new message instead");
        Check.Equal("steered", (string?)result.Details!["status"]);
        Check.Equal("planning", ModeOf(h, s.Id), "being talked about: still plan mode, not awaiting");
        var plan = (await h.Rpc.CallAsync("plan.get", new { planId = (string)awaiting["planId"]! }))!;
        Check.Equal("revising", (string?)plan["status"]);
        Check.Equal("bad_request", (await Check.ThrowsAsync<RpcException>(() => h.Rpc.InvokeAsync("plan.answer", new { planId = (string)awaiting["planId"]!, decision = "approve" }), "a plan being revised can't be approved")).Code);
    }

    private static async Task ApproveWithoutRun()
    {
        await using var h = await StartAsync();
        var s = h.NewSession();
        await h.Rpc.InvokeAsync("plan.enter", new { sessionId = s.Id });
        var seenUser = new List<string>();
        var step = 0;
        h.Catalog.Handler = (r, ct) =>
        {
            lock (seenUser) seenUser.Add(string.Join("\n", r.Messages.Where(m => m.Role == MessageRole.User).Select(m => m.Text)));
            return Interlocked.Increment(ref step) == 1 ? Reply.Tool("plan_submit", Plan()) : Reply.Text("on it");
        };
        await h.SendAsync(s.Id, "go");
        var awaiting = await ChangedAsync(h, s.Id, "awaiting", 1);
        await h.Runtime.AbortAsync(s.Id);
        await h.IdleAsync(s.Id);
        Check.Equal("awaiting", ModeOf(h, s.Id), "the plan still waits for the user");

        var planId = (string)awaiting["planId"]!;
        var plans = (await h.Rpc.CallAsync("plan.list", new { sessionId = s.Id }))!.AsArray();
        Check.Equal(false, (bool?)plans.Single()!["waiting"], "no run waits on it");
        await h.Rpc.InvokeAsync("plan.answer", new { planId, decision = "approve" });
        await Wait.Until(() => h.Messages(s.Id).Any(m => m.Role == MessageRole.Assistant && m.Text == "on it"), "the chat answers the approval");
        await h.IdleAsync(s.Id);
        Check.Contains(seenUser[^1], "The user approved your plan “Add retry”", "the approval reached the chat as a message");
        Check.Equal("approved", ModeOf(h, s.Id));
        Check.Equal("plan,plan-off", string.Join(",", Notices(h, s.Id)));
    }

    private static async Task ApproveInNewChat()
    {
        await using var h = await StartAsync();
        var ideas = FakeIdeas(h);
        var s = h.NewSession(title: "the plan chat");
        await h.Rpc.InvokeAsync("plan.enter", new { sessionId = s.Id });
        var firstOfNew = new List<string>();
        var step = 0;
        h.Catalog.Handler = (r, ct) =>
        {
            if (r.SessionId != s.Id)
            {
                lock (firstOfNew) firstOfNew.Add(Reply.LastUser(r));
                return Reply.Text("starting");
            }
            return Interlocked.Increment(ref step) == 1 ? Reply.Tool("plan_submit", Plan()) : Reply.Text("bye");
        };
        await h.SendAsync(s.Id, "go");
        var planId = (string)(await ChangedAsync(h, s.Id, "awaiting", 1))["planId"]!;
        var answered = (await h.Rpc.CallAsync("plan.answer", new { planId, decision = "approve", newChat = true }))!;
        var newId = (string)answered["newSessionId"]!;
        await h.IdleAsync(s.Id);
        await h.IdleAsync(newId);

        var created = h.Sessions.GetSession(newId)!;
        Check.Equal("Plan: Add retry", created.Title);
        Check.Equal(s.Id, (string?)created.Meta!["planFrom"]);
        Check.Equal(1, firstOfNew.Count);
        Check.Contains(firstOfNew[0], "Carry out this approved plan.");
        Check.Contains(firstOfNew[0], "# Add retry");
        Check.Contains(firstOfNew[0], "1. Write the retry loop");
        Check.Contains(firstOfNew[0], "idea_1");
        var attach = ideas.Single(c => c.Method == "ideas.attach").Params!;
        Check.Equal(newId, (string?)attach["sessionId"]);
        Check.Equal("idea_1", (string?)attach["id"]);
        Check.True(h.Sessions.GetSession(s.Id)!.Archived, "the plan chat is archived, not deleted");
        Check.Equal(newId, (string?)h.Sessions.GetSession(s.Id)!.Meta!["planMode"]!["newSessionId"], "linked");
        Check.Contains(Result(h, s.Id, "plan_submit").Content, $"moved it to a new chat ({newId})");
        Check.Equal(newId, (string?)(await h.Rpc.CallAsync("plan.get", new { planId }))!["newSessionId"]);
        Check.Equal(null, h.Sessions.GetSession(newId)!.Meta!["todo"], "the new chat starts from the plan message, not a seeded list");
    }

    private static async Task SaveIdeaAndFile()
    {
        await using var h = await StartAsync();
        var ideas = FakeIdeas(h);
        var s = h.NewSession();
        await h.Rpc.InvokeAsync("plan.enter", new { sessionId = s.Id });
        var step = 0;
        h.Catalog.Handler = (r, ct) => Interlocked.Increment(ref step) == 1 ? Reply.Tool("plan_submit", Plan()) : Reply.Text("ok");
        await h.SendAsync(s.Id, "go");
        var planId = (string)(await ChangedAsync(h, s.Id, "awaiting", 1))["planId"]!;

        var saved = (await h.Rpc.CallAsync("plan.answer", new { planId, decision = "save" }))!;
        Check.Equal("idea_1", (string?)saved["ideaId"]);
        Check.Equal("awaiting", (string?)saved["status"], "saving does not decide");
        Check.Equal("open", (string?)ideas.Single(c => c.Method == "ideas.add").Params!["idea"]!["status"]);
        await h.Rpc.InvokeAsync("plan.answer", new { planId, decision = "save" });
        Check.Equal(1, ideas.Count(c => c.Method == "ideas.add"), "saved twice, one idea");
        Check.Equal(0, ideas.Count(c => c.Method == "ideas.update"), "an unchanged plan is not written again");

        var file = (string)(await h.Rpc.CallAsync("plan.answer", new { planId, decision = "file" }))!["path"]!;
        Check.Equal($"docs/plans/{DateTime.Now:yyyy-MM-dd}-add-retry.md", file);
        var text = File.ReadAllText(Path.Combine(h.Workspace, file));
        Check.True(text.StartsWith("# Add retry\n", StringComparison.Ordinal) || text.StartsWith("# Add retry\r\n", StringComparison.Ordinal));
        Check.Contains(text, "## Steps");
        Check.Equal(file, (string?)(await h.Rpc.CallAsync("plan.answer", new { planId, decision = "file" }))!["path"], "the same plan is the same file");

        await h.Rpc.InvokeAsync("plan.answer", new { planId, decision = "approve" });
        await h.IdleAsync(s.Id);
        var update = ideas.Single(c => c.Method == "ideas.update").Params!;
        Check.Equal("idea_1", (string?)update["id"]);
        Check.Equal("planned", (string?)update["patch"]!["status"]);
        Check.Equal("sec_1", (string?)update["patch"]!["updateSections"]![0]!["id"]);
        Check.Equal(1, ideas.Count(c => c.Method == "ideas.add"), "approving follows the idea that was saved");
    }

    private static async Task Subagents()
    {
        await using var h = await StartAsync();
        var ran = new List<string>();
        h.AddTool(Recorder("read", ran, readOnly: true));
        h.AddTool(Recorder("write", ran));
        h.AddTool(Recorder("bash", ran));
        var spawn = new
        {
            task = "look at the loader", name = "researcher", tools = new[] { "write", "read", "bash" }, isolated = true, workspace = "new",
        };
        var children = new List<ModelRequest>();
        static bool IsChild(ModelRequest r) => r.SystemPrompt?.Contains("a subagent working for") == true;
        h.Catalog.Handler = (r, ct) =>
        {
            if (IsChild(r))
            {
                lock (children) children.Add(r);
                return Reply.HasToolResult(r) ? Reply.Text("REPORT") : Reply.Tools(Reply.Call("write", new { path = "a" }), Reply.Call("read", new { path = "a" }), Reply.Call("plan_submit", Plan()));
            }
            return Reply.HasToolResult(r) ? Reply.Text("parent done") : Reply.Tool("agent_spawn", spawn);
        };
        var s = h.NewSession();
        await h.Rpc.InvokeAsync("plan.enter", new { sessionId = s.Id });
        await h.SendAsync(s.Id, "research the loader");
        await h.IdleAsync(s.Id, 15_000);
        var child = h.Runtime.List().Single(a => a.IsSubagent);
        Check.True(child.ToolAllowlist!.Contains("read"), "it keeps what reads");
        Check.False(child.ToolAllowlist!.Contains("write") || child.ToolAllowlist.Contains("bash"), "and loses what changes things");
        Check.False(child.ToolAllowlist.Contains("agent_spawn"), "and cannot start others");
        var childResults = Results(h, child.SessionId);
        Check.True(childResults.Single(r => r.Name == "write").IsError, "the child has no write tool");
        Check.Contains(childResults.Single(r => r.Name == "write").Content, "Unknown tool 'write'");
        Check.False(childResults.Single(r => r.Name == "read").IsError);
        Check.Contains(childResults.Single(r => r.Name == "plan_submit").Content, "Unknown tool 'plan_submit'", "a research subagent does not submit plans");
        Check.Equal("read", string.Join(",", ran));
        Check.Equal(null, h.Sessions.GetSession(child.SessionId)!.Meta?["workspaceId"], "it shares the plan chat's workspace");
        Check.Contains(children[0].SystemPrompt, "research for a plan", "the child is told what it is for");

        // switched off: the spawn is left alone and the child is not bound
        h.Settings.Set("plan.subagentsReadOnly", false);
        ran.Clear();
        var s2 = h.NewSession();
        await h.Rpc.InvokeAsync("plan.enter", new { sessionId = s2.Id });
        await h.SendAsync(s2.Id, "research again");
        await h.IdleAsync(s2.Id, 15_000);
        Check.True(ran.Contains("write"), "the user's choice: that child may write");
    }

    private static async Task ChildBound()
    {
        await using var h = await StartAsync();
        var ran = new List<string>();
        h.AddTool(Recorder("write", ran));
        h.AddTool(Recorder("read", ran, readOnly: true));
        var ctx = new TestPluginContext(h, "netpi.plan");
        var hook = new PlanHook(ctx);
        var parent = h.NewSession();
        var child = h.Sessions.CreateSession(new SessionInfo { Kind = "subagent", ParentSessionId = parent.Id, Title = "researcher" });
        var info = new AgentInfo { Id = "ag_test", SessionId = child.Id, Name = "researcher", IsSubagent = true, ParentSessionId = parent.Id };
        async Task<ToolCallDecision?> Decide(string tool)
        {
            var run = new AgentRunContext { Agent = info, Session = child, Cwd = h.Workspace, Model = TestHost.LocalModel(), Services = h.Services, Sessions = h.Sessions, Models = h.Catalog, Events = h.Bus };
            var turn = new AgentTurnContext { Run = run, SystemPrompt = "", Messages = [], Tools = [], ReloadMessagesAsync = () => Task.CompletedTask };
            return await hook.OnBeforeToolCallAsync(turn, Reply.Call(tool, new { }));
        }
        Check.Equal(null, await Decide("write"), "the chat that started it is not in plan mode");
        await h.Rpc.InvokeAsync("plan.enter", new { sessionId = parent.Id });
        var blocked = await Decide("write");
        Check.True(blocked is { Block: true }, "its parent plans: it may not write");
        Check.Contains(blocked!.Reason, "Plan mode is read-only");
        Check.Equal(null, await Decide("read"), "readers pass");
        // two levels down: the chat that planned is the root
        var grandchild = h.Sessions.CreateSession(new SessionInfo { Kind = "subagent", ParentSessionId = child.Id, Title = "deeper" });
        info = new AgentInfo { Id = "ag_test2", SessionId = grandchild.Id, Name = "deeper", IsSubagent = true, ParentSessionId = child.Id };
        child = grandchild;
        Check.True((await Decide("write")) is { Block: true }, "and so is its child");
        h.Settings.Set("plan.subagentsReadOnly", false);
        Check.Equal(null, await Decide("write"), "unless the user turned that off");
    }

    private static async Task Mcp()
    {
        await using var h = await StartAsync();
        var ran = new List<string>();
        h.AddTool(Recorder("mcp_ha_get_state_1", ran, category: "mcp"));
        h.AddTool(Recorder("mcp_ha_call_service_2", ran, category: "mcp"));
        h.AddTool(Recorder("mcp_ha_list_3", ran, readOnly: true, category: "mcp"));
        var names = new Dictionary<string, (string Name, bool ReadOnly)>
        {
            ["mcp_ha_get_state_1"] = ("ha_get_state", false), ["mcp_ha_call_service_2"] = ("ha_call_service", false), ["mcp_ha_list_3"] = ("ha_list_areas", true),
        };
        h.Rpc.Register("mcp.tool", (req, _) =>
        {
            var id = req.Str("id")!;
            return Task.FromResult<object?>(new JsonObject { ["id"] = id, ["serverId"] = "ha", ["name"] = names[id].Name, ["readOnly"] = names[id].ReadOnly });
        });
        var s = h.NewSession();
        await h.Rpc.InvokeAsync("plan.enter", new { sessionId = s.Id });
        h.Catalog.Handler = (r, ct) => Reply.HasToolResult(r) ? Reply.Text("ok") : Reply.Tools(
            Reply.Call("mcp_ha_get_state_1", new { }), Reply.Call("mcp_ha_call_service_2", new { }), Reply.Call("mcp_ha_list_3", new { }));
        await h.SendAsync(s.Id, "what is on in the kitchen");
        await h.IdleAsync(s.Id);
        var first = Results(h, s.Id);
        Check.True(first[0].IsError, "a tool that may change things, no pattern: blocked");
        Check.Contains(first[0].Content, "ha/ha_get_state");
        Check.True(first[1].IsError);
        Check.False(first[2].IsError, "flagged read-only: runs");
        Check.Equal("mcp_ha_list_3", string.Join(",", ran));

        h.Settings.Set("plan.mcpAllow", new JsonArray("ha_get_*", "ha_search"));
        ran.Clear();
        await h.SendAsync(s.Id, "again");
        await h.IdleAsync(s.Id);
        var second = Results(h, s.Id).TakeLast(3).ToList();
        Check.False(second[0].IsError, "ha_get_* matches");
        Check.True(second[1].IsError, "ha_call_service still does not");
        Check.Equal("mcp_ha_get_state_1,mcp_ha_list_3", string.Join(",", ran.Order()));
    }

    private static async Task EnterOffer()
    {
        await using var h = await StartAsync();
        var ran = new List<string>();
        h.AddTool(Recorder("write", ran));
        var s = h.NewSession();
        var declined = h.NewSession();
        var step = 0;
        h.Catalog.Handler = (r, ct) => r.SessionId == declined.Id
            ? (Reply.HasToolResult(r) ? Reply.Text("fine") : Reply.Tool("plan_enter", new { reason = "no" }))
            : Interlocked.Increment(ref step) switch
            {
                1 => Reply.Tool("plan_enter", new { reason = "this touches 30 files" }),
                2 => Reply.Tool("write", new { path = "a" }),
                _ => Reply.Text("planning"),
            };
        await h.SendAsync(s.Id, "refactor the loader");
        await Wait.Until(() => h.Bus.OfType("plan.enter.asked").Any(), "an offer waits");
        var asked = FakeBus.Data(h.Bus.OfType("plan.enter.asked").Single());
        Check.Equal("this touches 30 files", (string?)asked["reason"]);
        Check.Equal(s.Id, (string?)asked["sessionId"]);
        var offers = (await h.Rpc.CallAsync("plan.offers", new { sessionId = s.Id }))!.AsArray();
        Check.Equal((string?)asked["id"], (string?)offers.Single()!["id"]);
        await Wait.Until(() => h.Runtime.GetBySession(s.Id)?.Status == AgentStatus.Yielded, "the run waits with its slot given back");
        Check.Equal(null, ModeOf(h, s.Id), "nothing changes before the answer");

        Check.Equal(true, await h.Rpc.InvokeAsync("plan.enterAnswer", new { id = (string)asked["id"]!, enter = true }));
        await h.IdleAsync(s.Id);
        var result = Result(h, s.Id, "plan_enter");
        Check.Contains(result.Content, "Plan mode is on");
        Check.Equal("entered", (string?)result.Details!["status"]);
        Check.Equal("planning", ModeOf(h, s.Id));
        Check.True(Result(h, s.Id, "write").IsError, "the next call is already checked");
        Check.Equal(0, ran.Count);
        Check.Equal("entered", (string?)FakeBus.Data(h.Bus.OfType("plan.enter.closed").Single())["status"]);
        Check.Equal("not_found", (await Check.ThrowsAsync<RpcException>(() => h.Rpc.InvokeAsync("plan.enterAnswer", new { id = (string)asked["id"]!, enter = true }), "answered already")).Code);

        await h.SendAsync(declined.Id, "another");
        await Wait.Until(() => h.Bus.OfType("plan.enter.asked").Count() == 2, "a second offer waits");
        var second = FakeBus.Data(h.Bus.OfType("plan.enter.asked").Last());
        await h.Rpc.InvokeAsync("plan.enterAnswer", new { id = (string)second["id"]!, enter = false });
        await h.IdleAsync(declined.Id);
        Check.Contains(Result(h, declined.Id, "plan_enter").Content, "declined plan mode");
        Check.Equal(null, ModeOf(h, declined.Id));
    }

    private static async Task ReloadDuringWait()
    {
        await using var h = await StartAsync();
        var s = h.NewSession();
        await h.Rpc.InvokeAsync("plan.enter", new { sessionId = s.Id });
        var step = 0;
        h.Catalog.Handler = (r, ct) => Interlocked.Increment(ref step) switch
        {
            1 => Reply.Tool("plan_submit", Plan()),
            2 => Reply.Text("the tool restarted; I will submit it again"),
            3 => Reply.Tool("plan_submit", Plan()),
            _ => Reply.Text("ok"),
        };
        await h.SendAsync(s.Id, "go");
        var planId = (string)(await ChangedAsync(h, s.Id, "awaiting", 1))["planId"]!;
        await h.StopPluginAsync("netpi.plan");
        await h.IdleAsync(s.Id);
        var first = Results(h, s.Id).Single(r => r.Name == "plan_submit");
        Check.Contains(first.Content, "The plan tool restarted");
        Check.Equal("withdrawn", (string?)first.Details!["status"]);
        Check.Equal("awaiting", ModeOf(h, s.Id), "the plan itself still waits for the user");

        await h.StartPluginAsync(new PlanPlugin());
        await h.SendAsync(s.Id, "submit it again");
        await Wait.Until(() => h.Runtime.GetBySession(s.Id)?.Status == AgentStatus.Yielded, "waiting again");
        var again = (await h.Rpc.CallAsync("plan.get", new { planId }))!;
        Check.Equal(1, (int?)again["revision"], "the same plan again is not a new revision");
        Check.Equal(1, (await h.Rpc.CallAsync("plan.list", new { sessionId = s.Id }))!.AsArray().Count, "and not a second plan");
        await h.Rpc.InvokeAsync("plan.answer", new { planId, decision = "approve" });
        await h.IdleAsync(s.Id);
        Check.Contains(Results(h, s.Id).Last(r => r.Name == "plan_submit").Content, "The user approved your plan");
    }

    private static async Task Command()
    {
        await using var h = await StartAsync();
        Check.True(h.Ui.Commands.Any(c => c.Name == "plan" && c.Rpc == "plan.command"), "the slash command is registered");
        var s = h.NewSession();
        var seen = new List<string>();
        h.Catalog.Handler = (r, ct) =>
        {
            lock (seen) seen.Add(string.Join("|", r.Messages.Where(m => m.Role == MessageRole.Notice || m.Text.StartsWith("<system-notice kind=\"plan", StringComparison.Ordinal)).Select(m => m.Text.Length > 40 ? m.Text[..40] : m.Text)));
            return Reply.Text("exploring");
        };
        Check.Contains((string?)await h.Rpc.InvokeAsync("plan.command", new { sessionId = s.Id, args = "" }), "Plan mode is on");
        Check.Equal("planning", ModeOf(h, s.Id));
        Check.Contains((string?)await h.Rpc.InvokeAsync("plan.command", new { sessionId = s.Id, args = "show" }), "no plan yet");
        Check.Equal("This chat is not in plan mode.", (string?)await h.Rpc.InvokeAsync("plan.command", new { sessionId = h.NewSession().Id, args = "off" }));
        Check.Equal("Plan mode is off.", (string?)await h.Rpc.InvokeAsync("plan.command", new { sessionId = s.Id, args = "off" }));
        Check.Equal(null, ModeOf(h, s.Id));

        var t = h.NewSession();
        await h.Rpc.InvokeAsync("plan.command", new { sessionId = t.Id, args = "fix the build" });
        await Wait.Until(() => h.Messages(t.Id).Any(m => m.Role == MessageRole.Assistant), "the task started a run");
        await h.IdleAsync(t.Id);
        Check.Equal("planning", ModeOf(h, t.Id));
        Check.Equal("fix the build", h.Messages(t.Id).First(m => m.Role == MessageRole.User && !Reply.IsContextNotice(m)).Text);
        await h.SendAsync(t.Id, "and then?");
        await h.IdleAsync(t.Id);
        Check.Equal("plan", string.Join(",", Notices(h, t.Id)), "two runs, one notice");
        await h.Rpc.InvokeAsync("plan.command", new { sessionId = t.Id, args = "off" });
        await h.SendAsync(t.Id, "now do it");
        await h.IdleAsync(t.Id);
        await h.SendAsync(t.Id, "and more");
        await h.IdleAsync(t.Id);
        Check.Equal("plan,plan-off", string.Join(",", Notices(h, t.Id)), "told once on leaving");
        await h.Rpc.InvokeAsync("plan.command", new { sessionId = t.Id, args = "" });
        await h.SendAsync(t.Id, "plan again");
        await h.IdleAsync(t.Id);
        Check.Equal("plan,plan-off,plan", string.Join(",", Notices(h, t.Id)), "and again on re-entering");
    }

    private static async Task ForkReset()
    {
        await using var h = await StartAsync();
        var s = h.NewSession();
        h.Catalog.Handler = (r, ct) => Reply.Text("ok");
        await h.Rpc.InvokeAsync("plan.enter", new { sessionId = s.Id });
        await h.SendAsync(s.Id, "hello");
        await h.IdleAsync(s.Id);
        Check.Equal("planning", ModeOf(h, s.Id));
        var fork = h.Fork(s.Id);
        Check.Equal(null, ModeOf(h, fork.Id), "the fork is not in plan mode");
        Check.Equal("planning", ModeOf(h, s.Id), "the original still is");
    }
}
