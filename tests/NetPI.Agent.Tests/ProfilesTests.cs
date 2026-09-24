using System.Text.Json.Nodes;

namespace NetPI.Agent.Tests;

/// <summary>Profiles (plugins/NetPI.Profiles): the opening of a chat's prompt and its tools, with a default per project.</summary>
public static class ProfilesTests
{
    public static void Register(TestRunner t)
    {
        t.Add("profiles: a new chat gets its project's default, else the global one; its text opens the prompt and its tools are off", NewChats);
        t.Add("profiles: switching before the first message is free; after it the prompt is rendered again, with a notice and no stray tools notice", Switching);
        t.Add("profiles: an older chat is never changed by a new default; subagents get no profile, their owner's tools or the ones it names", Boundaries);
    }

    private const string Admin = "You are a system administrator for the hosts in ~/.ssh/config.";

    private static async Task<TestHost> Start(string defaultProfile = "coder")
    {
        var h = await TestHost.StartAsync(x => x.Settings.SetQuiet("profiles", JsonNode.Parse($$"""
            {
              "defaultProfile": "{{defaultProfile}}",
              "coder": { "name": "Coder" },
              "admin": { "name": "Admin", "prompt": "{{Admin}}", "toolsOff": ["bash", "write"] }
            }
            """)));
        await h.StartPluginAsync(new NetPI.Profiles.ProfilesPlugin());
        foreach (var name in new[] { "read", "write", "bash", "ssh_run" })
            h.AddTool(new FakeTool(name, (c, a, t) => Task.FromResult(ToolResult.Ok("ok"))));
        return h;
    }

    private static async Task Turn(TestHost h, string sessionId, string text)
    {
        await h.SendAsync(sessionId, text);
        await h.IdleAsync(sessionId);
    }

    private static ModelRequest Last(TestHost h, string sessionId) => h.Catalog.Requests.Last(r => r.SessionId == sessionId);
    private static List<string> Sent(TestHost h, string sessionId) => [.. Last(h, sessionId).Tools.Select(t => t.Name)];
    private static string? ProfileOf(TestHost h, string sessionId) => (string?)h.Sessions.GetSession(sessionId)!.Meta?["profile"];

    private static List<ChatMessage> Notices(TestHost h, string sessionId, string kind) =>
        [.. h.Sessions.GetMessages(sessionId, null, 1000).Where(m => m.Role == MessageRole.Notice && m.MetaString("kind") == kind)];

    private static async Task NewChats()
    {
        await using var h = await Start();
        var ops = h.Sessions.CreateProject("ops", h.Workspace);
        ops.Meta = new JsonObject { ["profile"] = "admin" };

        // session.created gives the new chat its project's default, so the UI shows it before the first message
        var s = h.NewSession(projectId: ops.Id);
        await h.Bus.DrainAsync();
        Check.Equal("admin", ProfileOf(h, s.Id));
        await Turn(h, s.Id, "hi");
        var prompt = Last(h, s.Id).SystemPrompt!;
        Check.True(prompt.StartsWith(Admin), "the profile's text opens the prompt");
        Check.NotContains(prompt, "coding agent running in NetPI");
        var sent = Sent(h, s.Id);
        Check.True(sent.Contains("read") && sent.Contains("ssh_run") && !sent.Contains("bash") && !sent.Contains("write"), string.Join(",", sent));

        // no project default: the global one; a profile without text keeps the built-in opening. Right away: the hook.
        var plain = h.NewSession();
        await Turn(h, plain.Id, "hi");
        Check.Equal("coder", ProfileOf(h, plain.Id));
        Check.Contains(Last(h, plain.Id).SystemPrompt!, "coding agent running in NetPI");
        Check.True(Sent(h, plain.Id).Contains("bash"));

        // a project that says "none"
        var bare = h.Sessions.CreateProject("bare", h.Workspace);
        bare.Meta = new JsonObject { ["profile"] = "none" };
        var n = h.NewSession(projectId: bare.Id);
        await Turn(h, n.Id, "hi");
        Check.True(h.Sessions.GetSession(n.Id)!.Meta?.ContainsKey("profile") != true, "no profile");

        var list = (await h.Rpc.CallAsync("profiles.list"))!;
        Check.Equal("coder", (string?)list["defaultProfile"]);
        Check.Equal("Admin,Coder", string.Join(",", ((JsonArray)list["profiles"]!).Select(p => (string)p!["name"]!)));
    }

    private static async Task Switching()
    {
        await using var h = await Start();
        var s = h.NewSession();
        await h.Bus.DrainAsync();

        // before the first message: free, nothing to tell
        await h.Rpc.CallAsync("profiles.apply", new { sessionId = s.Id, profile = "admin" });
        await Turn(h, s.Id, "hi");
        Check.True(Last(h, s.Id).SystemPrompt!.StartsWith(Admin));
        Check.Equal(0, Notices(h, s.Id, "profile").Count, "nothing to tell before the first call");

        // a tweak of the chat's tools is announced as usual
        await h.Rpc.CallAsync("agent.setTools", new { sessionId = s.Id, off = new[] { "ssh_run" } });
        await Turn(h, s.Id, "no ssh");
        Check.Equal(1, Notices(h, s.Id, "tools").Count);

        // after the first message: the prompt is rendered again from the new profile and the model is told; the tools
        // follow the profile (bash and ssh_run are back) without a "tools" notice, older ones no longer count
        await h.Rpc.CallAsync("profiles.apply", new { sessionId = s.Id, profile = "coder" });
        await Turn(h, s.Id, "now code");
        var r = Last(h, s.Id);
        Check.Contains(r.SystemPrompt!, "coding agent running in NetPI");
        Check.True(Sent(h, s.Id).Contains("bash") && Sent(h, s.Id).Contains("ssh_run"), string.Join(",", Sent(h, s.Id)));
        Check.Contains(Notices(h, s.Id, "profile").Single().Text, "switched this chat to the profile \"Coder\"");
        Check.Equal(1, Notices(h, s.Id, "tools").Count, "no tools notice for the switch");
        await Turn(h, s.Id, "again");
        Check.Equal(r.SystemPrompt, Last(h, s.Id).SystemPrompt, "frozen again");

        await h.Rpc.CallAsync("agent.setTools", new { sessionId = s.Id, off = new[] { "ssh_run" } });
        await Turn(h, s.Id, "no ssh again");
        Check.Equal("Your tools changed. The user switched off for this session: ssh_run.", Notices(h, s.Id, "tools").Last().Text);
        Check.Equal(2, Notices(h, s.Id, "tools").Count);

        try
        {
            await h.Rpc.CallAsync("profiles.apply", new { sessionId = s.Id, profile = "nope" });
            throw new AssertException("expected not_found");
        }
        catch (RpcException ex) { Check.Equal("not_found", ex.Code); }
    }

    private static async Task Boundaries()
    {
        await using var h = await Start(defaultProfile: "");
        var s = h.NewSession();
        await Turn(h, s.Id, "hi");
        await Turn(h, s.Id, "again");
        Check.True(h.Sessions.GetSession(s.Id)!.Meta?.ContainsKey("profile") != true);
        var prompt = Last(h, s.Id).SystemPrompt;

        // a default set later does not change a chat that already talked
        h.Settings.Set("profiles.defaultProfile", "admin");
        await Turn(h, s.Id, "still me");
        Check.True(h.Sessions.GetSession(s.Id)!.Meta?.ContainsKey("profile") != true, "unchanged");
        Check.Equal(prompt, Last(h, s.Id).SystemPrompt);
        Check.True(Sent(h, s.Id).Contains("bash"));

        // subagents: no profile; by default their owner's tools, or exactly the ones it names (even ones it lacks)
        var admin = h.NewSession();
        await h.Rpc.CallAsync("profiles.apply", new { sessionId = admin.Id, profile = "admin" });
        await Turn(h, admin.Id, "hi");
        var owner = h.Runtime.GetBySession(admin.Id)!;
        var helper = await h.Runtime.SpawnAsync(new SpawnRequest { ParentAgentId = owner.Id, Task = "look around" });
        await h.StatusAsync(helper.Id, AgentStatus.Completed);
        Check.False(Sent(h, helper.SessionId).Contains("bash"), "the owner's tools");
        var remote = await h.Runtime.SpawnAsync(new SpawnRequest { ParentAgentId = owner.Id, Task = "fix the host", Tools = ["ssh_run", "bash"] });
        await h.StatusAsync(remote.Id, AgentStatus.Completed);
        Check.Equal("bash,ssh_run", string.Join(",", Sent(h, remote.SessionId)), "exactly the tools it was given");
        Check.True(h.Sessions.GetSession(remote.SessionId)!.Meta?.ContainsKey("profile") != true, "no profile");
        try
        {
            await h.Rpc.CallAsync("profiles.apply", new { sessionId = remote.SessionId, profile = "coder" });
            throw new AssertException("expected bad_request");
        }
        catch (RpcException ex) { Check.Equal("bad_request", ex.Code); }
    }
}
