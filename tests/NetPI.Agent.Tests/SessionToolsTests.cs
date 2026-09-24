using System.Text.Json.Nodes;

namespace NetPI.Agent.Tests;

/// <summary>Tools switched off for one session (<c>meta.toolsOff</c>, <c>agent.tools</c> / <c>agent.setTools</c>).</summary>
public static class SessionToolsTests
{
    public static void Register(TestRunner t)
    {
        t.Add("session tools: switched off before the first message they are never sent (no notice); preview and agent.tools agree", BeforeStart);
        t.Add("session tools: switched off mid-session they go from the next call with a notice naming the user; on again they are new", MidSession);
        t.Add("session tools: subagents start with their parent session's switches; an allowlist cannot bring a tool back", Subagents);
        t.Add("session tools: agent.setTools adds and removes names; the last one on removes the key; unknown session", SetSemantics);
    }

    private static FakeTool Tool(string name) => new(name, (c, a, t) => Task.FromResult(ToolResult.Ok("ok")));

    private static async Task Turn(TestHost h, string sessionId, string text)
    {
        await h.SendAsync(sessionId, text);
        await h.IdleAsync(sessionId);
    }

    private static List<string> Sent(TestHost h, string sessionId) =>
        [.. h.Catalog.Requests.Last(r => r.SessionId == sessionId).Tools.Select(t => t.Name)];

    private static List<ChatMessage> ToolNotices(TestHost h, string sessionId) =>
        [.. h.Sessions.GetMessages(sessionId, null, 1000).Where(m => m.Role == MessageRole.Notice && m.MetaString("kind") == "tools")];

    private static async Task<JsonNode> SetTools(TestHost h, string sessionId, string[]? off = null, string[]? on = null) =>
        (await h.Rpc.CallAsync("agent.setTools", new { sessionId, off = off ?? [], on = on ?? [] }))!;

    private static async Task BeforeStart()
    {
        await using var h = await TestHost.StartAsync();
        h.AddTool(Tool("read"));
        h.AddTool(Tool("bash"));
        var s = h.NewSession();

        var info = await SetTools(h, s.Id, off: ["bash"]);
        Check.False(info["started"]!.GetValue<bool>(), "a new session has not started");
        var bash = ((JsonArray)info["tools"]!).Single(t => (string)t!["name"]! == "bash")!;
        Check.False(bash["on"]!.GetValue<bool>(), "listed as off");
        Check.Equal("test", (string?)bash["pluginId"]);
        Check.True(((JsonArray)info["tools"]!).Single(t => (string)t!["name"]! == "read")!["on"]!.GetValue<bool>(), "the others stay on");

        var preview = (await h.Rpc.CallAsync("context.preview", new { sessionId = s.Id }))!;
        Check.False(((JsonArray)preview["tools"]!).Any(t => (string)t!["name"]! == "bash"), "the preview leaves it out");

        await Turn(h, s.Id, "hi");
        var sent = Sent(h, s.Id);
        Check.True(sent.Contains("read") && !sent.Contains("bash"), string.Join(",", sent));
        Check.NotContains(h.Catalog.Requests.Last().SystemPrompt!, "Use bash for testing.", "no guidelines for a tool that is off");
        await Turn(h, s.Id, "again");
        Check.Equal(0, ToolNotices(h, s.Id).Count, "nothing to announce: it was never there");
        Check.True((await h.Rpc.CallAsync("agent.tools", new { sessionId = s.Id }))!["started"]!.GetValue<bool>(), "started after the first message");
    }

    private static async Task MidSession()
    {
        await using var h = await TestHost.StartAsync();
        h.AddTool(Tool("read"));
        h.AddTool(Tool("bash"));
        var s = h.NewSession();
        await Turn(h, s.Id, "hi");
        Check.True(Sent(h, s.Id).Contains("bash"));

        await SetTools(h, s.Id, off: ["bash"]);
        await Turn(h, s.Id, "no shell now");
        Check.False(Sent(h, s.Id).Contains("bash"), "gone from the next call");
        Check.Equal("Your tools changed. The user switched off for this session: bash.", ToolNotices(h, s.Id).Single().Text);

        // another session is not affected
        var other = h.NewSession();
        await Turn(h, other.Id, "hi");
        Check.True(Sent(h, other.Id).Contains("bash"), "per session");

        await SetTools(h, s.Id, on: ["bash"]);
        await Turn(h, s.Id, "shell again");
        Check.True(Sent(h, s.Id).Contains("bash"), "back");
        Check.Contains(ToolNotices(h, s.Id).Last().Text, "Your tools changed. New: bash.");
        Check.Equal(2, ToolNotices(h, s.Id).Count);
    }

    private static async Task Subagents()
    {
        await using var h = await TestHost.StartAsync();
        h.AddTool(Tool("read"));
        h.AddTool(Tool("bash"));
        var s = h.NewSession();
        await SetTools(h, s.Id, off: ["bash"]);
        await Turn(h, s.Id, "hi");
        var parent = h.Runtime.GetBySession(s.Id)!;

        var sub = await h.Runtime.SpawnAsync(new SpawnRequest { ParentAgentId = parent.Id, Task = "look around", Tools = ["read", "bash"] });
        await h.StatusAsync(sub.Id, AgentStatus.Completed);
        var subSession = h.Sessions.GetSession(sub.SessionId)!;
        Check.Equal("bash", string.Join(",", SessionTools.Off(subSession)), "the subagent session starts with the parent's switches");
        var sent = Sent(h, sub.SessionId);
        Check.True(sent.Contains("read") && !sent.Contains("bash"), string.Join(",", sent));

        // a top-level agent without a parent session gets every tool
        var free = await h.Runtime.SpawnAsync(new SpawnRequest { Task = "free" });
        await h.StatusAsync(free.Id, AgentStatus.Completed);
        Check.True(Sent(h, free.SessionId).Contains("bash"));
    }

    private static async Task SetSemantics()
    {
        await using var h = await TestHost.StartAsync();
        h.AddTool(Tool("read"));
        h.AddTool(Tool("bash"));
        var s = h.NewSession();

        await SetTools(h, s.Id, off: ["bash", "read", "gone_tool"]);
        Check.Equal("bash,gone_tool,read", string.Join(",", SessionTools.Off(h.Sessions.GetSession(s.Id))), "sorted; unknown names are kept");
        var info = await SetTools(h, s.Id, on: ["READ"]);
        Check.Equal("bash,gone_tool", string.Join(",", ((JsonArray)info["off"]!).Select(n => (string)n!)), "case-insensitive");
        await SetTools(h, s.Id, on: ["bash", "gone_tool"]);
        Check.True(h.Sessions.GetSession(s.Id)!.Meta?[SessionTools.MetaKey] is null, "no key when everything is on");

        try
        {
            await h.Rpc.CallAsync("agent.setTools", new { sessionId = "ses_nope", off = new[] { "bash" } });
            throw new AssertException("expected not_found");
        }
        catch (RpcException ex) { Check.Equal("not_found", ex.Code); }
    }
}
