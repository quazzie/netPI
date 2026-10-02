using System.Text.Json.Nodes;

namespace NetPI.Agent.Tests;

public static class ContextTests
{
    public static void Register(TestRunner t)
    {
        t.Add("context: system prompt sections in order", PromptSections);
        t.Add("context: no date or time in the system prompt (identical on every turn)", NoDateOrTime);
        t.Add("context: plugins add the guidance for what they own (agents, agent tools)", PluginOwnedGuidance);
        t.Add("context: AGENTS.md arrives as notices: all at first, then only changes, again after compaction", AgentsMdNotices);
        t.Add("context: working directory and project arrive as notices (first call, switch, moved, compaction)", ProjectNoticesFlow);
        t.Add("context: the system prompt is frozen per session; settings changes reach new sessions", FrozenPrompt);
        t.Add("context: every system prompt a session is sent is kept with its tools (context.prompts, context.prompt event)", SentPrompts);
        t.Add("context: a fork goes on with the prompt the original had at the fork point", ForkPrompt);
        t.Add("context: tools are sent sorted by name", ToolOrder);
        t.Add("context: tools added or removed mid-session arrive as a notice with their guidelines", ToolChangeNotices);
        t.Add("context: a tools notice says why (plugin reload, the user, a setting) and keeps the cause in meta", ToolChangeCauses);
        t.Add("context: a plugin whose folder disappeared is named as the cause of its vanished tools", ToolChangeCausePluginRemoved);
        t.Add("context: a profile switch is named by session.changed, not by the notice it happens to leave", ToolChangeCauseProfileEvent);
        t.Add("context: a tools notice names the user and a setting as the cause too", ToolChangeCausesUserAndSettings);
        t.Add("context: context.toolsets: the tools now, the baseline and every change with its cause", ToolSets);
        t.Add("context: context.toolsets reaches a change older than the newest 500 messages, and says when it stops", ToolSetsLongChat);
        t.Add("context: a tools notice leaves out the guidelines the model already has", ToolNoticeKnownGuidelines);
        t.Add("context: tool guidelines are grouped by category, a line tools share listed once", GuidelineGroups);
        t.Add("context: guidance on using and keeping AGENTS.md (setting replaces or drops it)", InstructionGuidance);
        t.Add("context: custom and appended prompt settings", CustomPrompt);
        t.Add("context: context.preview", Preview);
        t.Add("context: plugin sections and fallback prompt", PluginSectionAndFallback);
        t.Add("context: the first turn's setup notices go before the first message, also after notices added before it", FirstTurnOrder);
    }

    private static void FirstTurnOrder()
    {
        ChatMessage Notice(string text, string kind, bool setup = false)
        {
            var m = ChatMessage.NoticeText(text, kind);
            if (setup) m.Meta!["setup"] = true;
            return m;
        }
        string Order(IEnumerable<ChatMessage> ms) => string.Join(" | ", ms.Select(m => m.Text));
        var hi = ChatMessage.UserText("hi");
        var cwd = Notice("cwd", "project", setup: true);
        var md = Notice("AGENTS.md", "instructions", setup: true);
        var skill = Notice("skill loaded", "skill");

        // the plain case: stored after "hi", read before it; a notice that answers the message stays after it
        Check.Equal("cwd | AGENTS.md | hi | skill loaded", Order(NetPI.Runtime.ContextOrder.FirstTurnNoticesFirst([hi, cwd, md, skill])));
        // an idea (or a project) the user added before sending keeps its place first, and the setup notices still move
        var idea = Notice("idea from the backlog", "idea");
        Check.Equal("idea from the backlog | cwd | AGENTS.md | hi | skill loaded",
            Order(NetPI.Runtime.ContextOrder.FirstTurnNoticesFirst([idea, hi, cwd, md, skill])));
        // after compaction (a summary first) and in chats without setup notices nothing moves
        var summary = new ChatMessage { Role = MessageRole.Summary, Parts = [new TextPart { Text = "summary" }] };
        Check.Equal("summary | hi | cwd", Order(NetPI.Runtime.ContextOrder.FirstTurnNoticesFirst([summary, hi, cwd])));
        Check.Equal("hi | skill loaded", Order(NetPI.Runtime.ContextOrder.FirstTurnNoticesFirst([hi, skill])));
    }

    private static PromptContext Ctx(TestHost h, SessionInfo s, IReadOnlyList<ToolDefinition> tools, string? instructions = null) => new()
    {
        Agent = instructions is null ? null : new AgentInfo { Id = "agt_w1", Name = "w1", IsSubagent = true, Depth = 1 },
        Session = s,
        Project = s.ProjectId is null ? null : h.Sessions.GetProject(s.ProjectId),
        Cwd = h.Sessions.GetCwd(s),
        Model = TestHost.LocalModel(),
        Tools = tools,
        Instructions = instructions,
    };

    private static async Task PromptSections()
    {
        await using var h = await TestHost.StartAsync();
        h.AddTool(new FakeTool("read", (c, a, t) => Task.FromResult(ToolResult.Ok(""))));
        h.AddTool(new FakeTool("edit", (c, a, t) => Task.FromResult(ToolResult.Ok(""))));
        h.AddTool(new FakeTool("bash", (c, a, t) => Task.FromResult(ToolResult.Ok(""))));
        var project = h.Sessions.CreateProject("Demo", h.Workspace);
        var s = h.NewSession(projectId: project.Id);
        var builder = h.Services.Get<ISystemPromptBuilder>()!;
        var prompt = await builder.BuildAsync(Ctx(h, s, h.Tools.All.Select(t => t.Definition).ToList(), "You are \"w1\", a subagent."), CancellationToken.None);

        int Idx(string s) { var i = prompt.IndexOf(s, StringComparison.Ordinal); Check.True(i >= 0, $"missing '{s}' in prompt:\n{prompt}"); return i; }
        var identity = Idx("coding agent running in NetPI");
        var env = Idx("# Environment");
        var tools = Idx("# Tools");
        var agents = Idx("# Agents");
        var role = Idx("# Your role");
        Check.True(identity < env && env < tools && tools < agents && agents < role, "section order");
        Check.NotContains(prompt, h.Workspace, "no working directory (a notice brings it)");
        Check.NotContains(prompt, "Demo", "no project");
        Check.NotContains(prompt, "fake/local", "no model");
        Check.Contains(prompt, "<system-notice>");
        Check.Contains(prompt, "Use edit for testing.");
        Check.Contains(prompt, "Delegate independent, well-scoped work");
        Check.Contains(prompt, "in one agent_spawn call: separate calls run one after the other");
        Check.Contains(prompt, "Pass background: true only when you have other work to do meanwhile");
        Check.Contains(prompt, "Before you delegate, look at agent_choices and choose an active agent by its note and cost");
        // how the agent tools work is in their definitions (agent_choices marks the caller's free instance), not here
        Check.NotContains(prompt, "instance is free");
        Check.Contains(prompt, "You are \"w1\", a subagent.");
        Check.NotContains(prompt, "\n\n\n", "no empty sections");

        // no project, no tools: the bare base only
        var bare = await builder.BuildAsync(Ctx(h, h.NewSession(), []), CancellationToken.None);
        Check.NotContains(bare, "# Tools");
        Check.NotContains(bare, "# Agents");
        Check.NotContains(bare, "# Your role");
    }

    // The base prompt is bare; each plugin adds guidance for what it owns and it disappears with the plugin.
    private static async Task PluginOwnedGuidance()
    {
        async Task<(string Prompt, List<ToolDefinition> Tools)> Build(TestHost.Plugins plugins)
        {
            await using var h = await TestHost.StartAsync(plugins: plugins);
            var tools = h.Tools.All.Select(t => t.Definition).ToList();
            return (await h.Services.Get<ISystemPromptBuilder>()!.BuildAsync(Ctx(h, h.NewSession(), tools), CancellationToken.None), tools);
        }

        var (noAgents, noAgentsTools) = await Build(TestHost.Plugins.All & ~TestHost.Plugins.Agents);
        Check.False(noAgentsTools.Any(t => t.Name == "agent_choices"), "agent_choices comes from the agents plugin");
        Check.Contains(noAgents, "Delegate independent, well-scoped work", "the agent tools still bring their tips");
        Check.NotContains(noAgents, "# Agents");
        Check.NotContains(noAgents, "agent_choices", "nothing about the agents without the agents plugin");

        var (noAgentTools, _) = await Build(TestHost.Plugins.All & ~TestHost.Plugins.AgentTools);
        Check.NotContains(noAgentTools, "agent_spawn", "no delegation tips without the agent tools");
        Check.NotContains(noAgentTools, "# Agents", "no agents section for an agent that cannot spawn");
    }

    // Real-model regression: the date line (to the minute) changed the prompt's first ~100 tokens every minute, so the
    // first turn after a minute boundary re-prefilled the whole conversation.
    private static async Task NoDateOrTime()
    {
        await using var h = await TestHost.StartAsync();
        h.AddTool(new FakeTool("bash", (c, a, t) => Task.FromResult(ToolResult.Ok(""))));
        var project = h.Sessions.CreateProject("Demo", h.Workspace);
        var s = h.NewSession(projectId: project.Id);
        var builder = h.Services.Get<ISystemPromptBuilder>()!;
        var ctx = Ctx(h, s, h.Tools.All.Select(t => t.Definition).ToList());
        var first = await builder.BuildAsync(ctx, CancellationToken.None);
        var now = DateTimeOffset.Now;
        foreach (var stamp in new[] { "yyyy-MM-dd", "dddd", "HH:mm" }.Select(f => now.ToString(f, System.Globalization.CultureInfo.InvariantCulture)))
            Check.NotContains(first, stamp, "no date or time in the prompt");
        Check.False(System.Text.RegularExpressions.Regex.IsMatch(first, @"(?im)^- (date|time)"), "no date line");
        Check.Equal(first, await builder.BuildAsync(ctx, CancellationToken.None), "identical on the next turn");
    }

    private static List<ChatMessage> Notices(TestHost h, string sessionId, string kind) =>
        h.Sessions.GetMessages(sessionId, null, 1000).Where(m => m.Role == MessageRole.Notice && m.MetaString("kind") == kind).ToList();

    private static async Task Turn(TestHost h, string sessionId, string text)
    {
        await h.SendAsync(sessionId, text);
        await h.IdleAsync(sessionId);
    }

    /// <summary>What was sent before must be sent again unchanged: the earlier request's messages prefix the later one's.</summary>
    private static void PrefixKept(ModelRequest earlier, ModelRequest later)
    {
        Check.Equal(earlier.SystemPrompt, later.SystemPrompt, "system prompt unchanged");
        Check.True(later.Messages.Count >= earlier.Messages.Count, "history only grows");
        for (var i = 0; i < earlier.Messages.Count; i++)
            Check.Equal($"{earlier.Messages[i].Role}:{earlier.Messages[i].Text}", $"{later.Messages[i].Role}:{later.Messages[i].Text}", $"message {i} unchanged");
    }

    // Instruction files reach the model as notices (the system prompt is frozen): all at the first model call, then only
    // what changed, and again after compaction removed them.
    private static async Task AgentsMdNotices()
    {
        await using var h = await TestHost.StartAsync();
        var root = Path.Combine(h.Root, "repo");
        var sub = Path.Combine(root, "src", "app");
        Directory.CreateDirectory(sub);
        File.WriteAllText(Path.Combine(h.Home, "AGENTS.md"), "GLOBAL RULES");
        File.WriteAllText(Path.Combine(root, "AGENTS.md"), "ROOT RULES");
        File.WriteAllText(Path.Combine(root, "src", "CLAUDE.md"), "SRC CLAUDE RULES");
        File.WriteAllText(Path.Combine(sub, "AGENTS.md"), "APP AGENTS RULES");
        File.WriteAllText(Path.Combine(sub, "CLAUDE.md"), "APP CLAUDE RULES (shadowed)");
        var project = h.Sessions.CreateProject("App", sub);
        var s = h.NewSession(projectId: project.Id);

        var list = (JsonArray)(await h.Rpc.CallAsync("agentsmd.list", new { sessionId = s.Id }))!;
        Check.Equal("global,project,project,project", string.Join(",", list.Select(x => (string?)x!["scope"])));
        Check.Equal(Path.Combine(sub, "AGENTS.md"), (string?)list[^1]!["path"]);
        Check.Equal(16L, list[^1]!["bytes"]!.GetValue<long>());
        // …and the same list for the project itself (the project dialog shows it)
        var byProject = (JsonArray)(await h.Rpc.CallAsync("agentsmd.list", new { projectId = project.Id }))!;
        Check.Equal(list.ToJsonString(), byProject.ToJsonString());
        try
        {
            await h.Rpc.CallAsync("agentsmd.list", new { projectId = "prj_missing" });
            throw new AssertException("expected not_found");
        }
        catch (RpcException ex) { Check.Equal("not_found", ex.Code); }

        await Turn(h, s.Id, "hi");
        var first = Notices(h, s.Id, "instructions").Single();
        var text = first.Text;
        int At(string x) => text.IndexOf(x, StringComparison.Ordinal);
        Check.True(At("GLOBAL RULES") >= 0 && At("ROOT RULES") > At("GLOBAL RULES") && At("SRC CLAUDE RULES") > At("ROOT RULES")
                   && At("APP AGENTS RULES") > At("SRC CLAUDE RULES"), "global → root → leaf order");
        Check.NotContains(text, "shadowed", "only the first file name per directory");
        Check.Contains(text, "Instruction files that apply here");
        Check.Contains(text, "## " + Path.Combine(root, "AGENTS.md"));
        Check.Contains(text, "(global)");
        var r1 = h.Catalog.Requests.Last();
        Check.True(r1.Messages.Any(m => m.Text.Contains("APP AGENTS RULES")), "the model got the instructions");
        Check.NotContains(r1.SystemPrompt, "RULES", "not in the system prompt");

        await Turn(h, s.Id, "again");
        Check.Equal(1, Notices(h, s.Id, "instructions").Count, "nothing changed: no notice");

        // an edited file: only that file is announced, nothing earlier changes
        var appFile = Path.Combine(sub, "AGENTS.md");
        File.WriteAllText(appFile, "APP RULES V2");
        File.SetLastWriteTimeUtc(appFile, DateTime.UtcNow.AddMinutes(1));
        await Turn(h, s.Id, "edited");
        var edit = Notices(h, s.Id, "instructions").Last();
        Check.Contains(edit.Text, "The instruction files changed");
        Check.Contains(edit.Text, "APP RULES V2");
        Check.NotContains(edit.Text, "ROOT RULES", "unchanged files are not repeated");
        Check.Equal(text, Notices(h, s.Id, "instructions")[0].Text, "the first notice is untouched");
        PrefixKept(r1, h.Catalog.Requests.Last());

        // capped at 32KB with a note
        File.WriteAllText(Path.Combine(root, "AGENTS.md"), "BIG " + new string('x', 40_000));
        await Turn(h, s.Id, "big");
        Check.Contains(Notices(h, s.Id, "instructions").Last().Text, "[Truncated: this file is 39 KB; only the first 32 KB are included");

        // other file names and extra files: the app's CLAUDE.md applies instead, root AGENTS.md no longer does
        var extra = Path.Combine(h.Root, "extra.md");
        File.WriteAllText(extra, "EXTRA RULES");
        h.Settings.SetQuiet("agentsMd.fileNames", new JsonArray("CLAUDE.md"));
        h.Settings.SetQuiet("agentsMd.extraFiles", new JsonArray(extra));
        await Turn(h, s.Id, "names");
        var names = Notices(h, s.Id, "instructions").Last().Text;
        Check.Contains(names, "APP CLAUDE RULES (shadowed)");
        Check.Contains(names, "EXTRA RULES");
        Check.Contains(names, "No longer apply: ");
        Check.Contains(names, Path.Combine(root, "AGENTS.md"));
        Check.NotContains(names, "SRC CLAUDE RULES", "still applies unchanged");

        // compaction removed the notices: everything is announced again
        h.Sessions.MarkCompacted(s.Id, h.Sessions.GetMessages(s.Id, null, 1000)[^1].Seq);
        await Turn(h, s.Id, "after compaction");
        var again = Notices(h, s.Id, "instructions").Last().Text;
        Check.Contains(again, "Instruction files that apply here");
        Check.Contains(again, "GLOBAL RULES");
        Check.Contains(again, "SRC CLAUDE RULES");
    }

    // The working directory and project reach the model as "project" notices: at the first model call, right after a
    // switch, when a project folder moves; the system prompt and everything sent before stay the same.
    private static async Task ProjectNoticesFlow()
    {
        await using var h = await TestHost.StartAsync();
        string Dir(string name) { var d = Path.Combine(h.Root, name); Directory.CreateDirectory(d); return d; }
        var alpha = h.Sessions.CreateProject("Alpha", Dir("alpha"));
        var beta = h.Sessions.CreateProject("Beta", Dir("beta"));
        var s = h.NewSession(projectId: alpha.Id);

        await Turn(h, s.Id, "hi");
        var n1 = Notices(h, s.Id, "project").Single();
        Check.Equal($"Working directory: {alpha.Path} (project \"Alpha\"). Relative paths resolve against it.", n1.Text);
        Check.Equal(alpha.Path, n1.MetaString("cwd"));
        var r1 = h.Catalog.Requests.Last();
        Check.True(r1.Messages.Any(m => m.Text.StartsWith("<system-notice kind=\"project\">") && m.Text.Contains(alpha.Path)), "sent to the model as a notice");
        Check.NotContains(r1.SystemPrompt, alpha.Path, "not in the system prompt");
        // made at the first model call (stored after "hi"), the model reads it before the first message: nothing is cached yet
        var noticeAt = r1.Messages.ToList().FindIndex(m => m.Text.StartsWith("<system-notice kind=\"project\">"));
        var hiAt = r1.Messages.ToList().FindIndex(m => m.Role == MessageRole.User && m.Text == "hi");
        Check.True(noticeAt >= 0 && noticeAt < hiAt, $"the first turn's notice before the first message ({noticeAt} < {hiAt})");

        await Turn(h, s.Id, "again");
        Check.Equal(1, Notices(h, s.Id, "project").Count, "not repeated");

        h.Sessions.SetSessionProject(s.Id, beta.Id);
        await Wait.Until(() => Notices(h, s.Id, "project").Count == 2, "switch notice right away");
        Check.Equal($"The session moved to project \"Beta\": the working directory is now {beta.Path}.", Notices(h, s.Id, "project")[1].Text);
        await Turn(h, s.Id, "in beta");
        Check.Equal(2, Notices(h, s.Id, "project").Count, "the hook does not repeat the switch notice");
        PrefixKept(r1, h.Catalog.Requests.Last());
        // a later notice stays where it happened, after the replies before it
        var last = h.Catalog.Requests.Last().Messages.ToList();
        Check.True(last.FindIndex(m => m.Text.Contains("moved to project \"Beta\"")) > last.FindIndex(m => m.Role == MessageRole.User && m.Text == "again"), "the switch notice after \"again\"");

        var moved = Dir("beta-moved");
        h.Sessions.UpdateProject(beta.Id, null, moved);
        await Turn(h, s.Id, "moved");
        Check.Equal($"Project \"Beta\" moved: the working directory is now {moved}.", Notices(h, s.Id, "project").Last().Text);

        h.Sessions.SetSessionProject(s.Id, null);
        await Wait.Until(() => Notices(h, s.Id, "project").Count == 4, "detach notice");
        Check.Equal($"The session left its project: the working directory is now the default workspace, {h.Workspace}.", Notices(h, s.Id, "project").Last().Text);

        // after compaction removed it, the current directory is announced again
        h.Sessions.MarkCompacted(s.Id, h.Sessions.GetMessages(s.Id, null, 1000)[^1].Seq);
        await Turn(h, s.Id, "after compaction");
        Check.Equal($"Working directory: {h.Workspace} (no project: the default workspace). Relative paths resolve against it.", Notices(h, s.Id, "project").Last().Text);
    }

    // A session's system prompt is rendered once: later settings changes reach new sessions only.
    private static async Task FrozenPrompt()
    {
        await using var h = await TestHost.StartAsync();
        var s = h.NewSession();
        var preview = (await h.Rpc.CallAsync("context.preview", new { sessionId = s.Id }))!;
        Check.False(preview["frozen"]!.GetValue<bool>(), "a preview does not freeze");
        await Turn(h, s.Id, "hi");
        var p1 = h.Catalog.Requests.Last().SystemPrompt!;
        h.Settings.SetQuiet("context.appendPrompt", "APPENDED LATER");
        h.Settings.SetQuiet("context.customPrompt", "You are someone else.");
        await Turn(h, s.Id, "again");
        Check.Equal(p1, h.Catalog.Requests.Last().SystemPrompt, "the running session keeps its prompt");
        preview = (await h.Rpc.CallAsync("context.preview", new { sessionId = s.Id }))!;
        Check.True(preview["frozen"]!.GetValue<bool>(), "frozen after the first model call");
        Check.Equal(p1, (string?)preview["systemPrompt"], "the preview shows what is sent");

        var s2 = h.NewSession();
        await Turn(h, s2.Id, "hi");
        var p2 = h.Catalog.Requests.Last().SystemPrompt!;
        Check.True(p2.StartsWith("You are someone else.") && p2.EndsWith("APPENDED LATER"), "new sessions get the new settings");
    }

    // A fork (sessions.fork) takes the prompt the original was sent at the fork point, so its next call starts with the
    // prefix the backend saw. The real store publishes session.forked, which the context plugin answers by copying the
    // fork point's prompt into the new session; the first call of the fork then reuses it.
    private static async Task ForkPrompt()
    {
        await using var h = await TestHost.StartAsync();
        var s = h.NewSession();
        await Turn(h, s.Id, "hi");
        var p1 = h.Catalog.Requests.Last().SystemPrompt;
        await Turn(h, s.Id, "again");
        var upTo = h.Messages(s.Id).Last().Seq;
        h.Settings.SetQuiet("context.appendPrompt", "AFTER THE SWITCH");
        await h.Rpc.CallAsync("context.reset", new { sessionId = s.Id });
        await Turn(h, s.Id, "switched");
        var p2 = h.Catalog.Requests.Last().SystemPrompt;
        Check.True(p2!.EndsWith("AFTER THE SWITCH") && p1 != p2, "the original has a second prompt now");

        SessionInfo Fork(long seq) => h.Fork(s.Id, seq);
        var early = Fork(upTo);
        await Turn(h, early.Id, "in the fork");
        var sent = h.Catalog.Requests.Last();
        Check.Equal(p1, sent.SystemPrompt, "the prompt of the fork point, not a new render (which would end AFTER THE SWITCH)");
        Check.True(sent.Messages.Any(m => m.Text == "again"), "with the conversation up to the fork point");
        var prompts = (JsonArray)(await h.Rpc.CallAsync("context.prompts", new { sessionId = early.Id }))!["prompts"]!;
        Check.Equal(1, prompts.Count, "the prompts sent up to the fork point are its history");
        Check.Equal(p1, (string?)prompts[0]!["systemPrompt"]);

        var late = Fork(h.Messages(s.Id).Last().Seq);
        await Turn(h, late.Id, "later fork");
        Check.Equal(p2, h.Catalog.Requests.Last().SystemPrompt, "a fork after the switch has the second prompt");
    }

    // Every prompt a session is sent is kept with its tools (context.prompts): the first, and one after each context.reset
    // (a profile switch), which leaves the earlier ones as they were.
    private static async Task SentPrompts()
    {
        await using var h = await TestHost.StartAsync();
        var s = h.NewSession();
        JsonArray Prompts() => (JsonArray)h.Rpc.CallAsync("context.prompts", new { sessionId = s.Id }).GetAwaiter().GetResult()!["prompts"]!;
        Check.Equal(0, Prompts().Count, "nothing sent yet");

        await Turn(h, s.Id, "hi");
        var r1 = h.Catalog.Requests.Last();
        var p = Prompts();
        Check.Equal(1, p.Count);
        Check.Equal(1, p[0]!["version"]!.GetValue<int>());
        Check.Equal(r1.SystemPrompt, (string?)p[0]!["systemPrompt"], "the prompt as sent");
        var tools = ((JsonArray)p[0]!["tools"]!).OfType<JsonObject>().ToList();
        Check.Equal(string.Join(",", r1.Tools.Select(t => t.Name)), string.Join(",", tools.Select(t => (string?)t["name"])), "the tools as sent");
        Check.Equal(r1.Tools[0].Description, (string?)tools[0]["description"]);
        Check.Equal(r1.Tools[0].Parameters.ToJsonString(), tools[0]["parameters"]!.ToJsonString());
        var user = h.Messages(s.Id).First(m => m.Role == MessageRole.User);
        Check.Equal(user.Seq, p[0]!["afterSeq"]!.GetValue<long>(), "sent after the first message");
        var ev = h.Bus.OfType("context.prompt").Single();
        Check.Equal(s.Id, ev.SessionId);

        await Turn(h, s.Id, "again");
        Check.Equal(1, Prompts().Count, "the same prompt: nothing new");

        h.Settings.SetQuiet("context.appendPrompt", "AFTER THE SWITCH");
        await h.Rpc.CallAsync("context.reset", new { sessionId = s.Id });
        await Turn(h, s.Id, "switched");
        p = Prompts();
        Check.Equal(2, p.Count, "a new version after the reset");
        Check.Equal(r1.SystemPrompt, (string?)p[0]!["systemPrompt"], "the first one is kept");
        Check.True(((string?)p[1]!["systemPrompt"])!.EndsWith("AFTER THE SWITCH"), "the new one");
        Check.True(p[1]!["afterSeq"]!.GetValue<long>() > p[0]!["afterSeq"]!.GetValue<long>(), "later in the chat");
        Check.Equal(2, h.Bus.OfType("context.prompt").Count);

        try
        {
            await h.Rpc.CallAsync("context.prompts", new { sessionId = "ses_missing" });
            throw new AssertException("expected not_found");
        }
        catch (RpcException ex) { Check.Equal("not_found", ex.Code); }
    }

    // Tool definitions are part of the request prefix: sent sorted by name, not in registration order (which changes
    // when a plugin reloads).
    private static async Task ToolOrder()
    {
        await using var h = await TestHost.StartAsync();
        h.AddTool(new FakeTool("zeta", (c, a, t) => Task.FromResult(ToolResult.Ok(""))));
        h.AddTool(new FakeTool("alpha", (c, a, t) => Task.FromResult(ToolResult.Ok(""))));
        var s = h.NewSession();
        await Turn(h, s.Id, "hi");
        var names = h.Catalog.Requests.Last().Tools.Select(t => t.Name).ToList();
        Check.Equal(string.Join(",", names.OrderBy(n => n, StringComparer.Ordinal)), string.Join(",", names), "sorted by name");
    }

    // A tool that appears or disappears during a session (a plugin loaded, reloaded or disabled) is announced with a
    // "tools" notice carrying its guidelines, which the frozen prompt lacks; everything sent before stays as it was.
    private static async Task ToolChangeNotices()
    {
        await using var h = await TestHost.StartAsync();
        var s = h.NewSession();
        await Turn(h, s.Id, "hi");
        await Turn(h, s.Id, "again");
        Check.Equal(0, Notices(h, s.Id, "tools").Count, "no notice while the tools are unchanged");
        var r1 = h.Catalog.Requests.Last();

        var registration = h.Tools.Register(new FakeTool("web_probe", (c, a, t) => Task.FromResult(ToolResult.Ok(""))));
        await Turn(h, s.Id, "a new tool?");
        var added = Notices(h, s.Id, "tools").Single();
        Check.Contains(added.Text, "Your tools changed. New: web_probe.");
        Check.Contains(added.Text, "Guidelines for the new tools:\n- Use web_probe for testing.");
        var r2 = h.Catalog.Requests.Last();
        Check.True(r2.Tools.Any(t => t.Name == "web_probe"), "the new tool is sent");
        Check.NotContains(r2.SystemPrompt!, "web_probe", "the frozen prompt stays as it was");
        PrefixKept(r1, r2);

        await Turn(h, s.Id, "still there");
        Check.Equal(1, Notices(h, s.Id, "tools").Count, "announced once");

        registration.Dispose();
        await Turn(h, s.Id, "gone?");
        Check.Equal("Your tools changed. No longer available: web_probe.", Notices(h, s.Id, "tools").Last().Text);
        Check.Equal(2, Notices(h, s.Id, "tools").Count);
    }

    // A tools notice says why, so the model (and the user) can tell a plugin reload from a tool that vanished for no
    // reason: the cause and the plugins behind it go in the notice's meta, and the reload is the host's plugins.reloaded.
    private static async Task ToolChangeCauses()
    {
        await using var h = await TestHost.StartAsync();
        Task<ToolResult> Ok(ToolContext c, System.Text.Json.JsonElement a, CancellationToken t) => Task.FromResult(ToolResult.Ok(""));
        var s = h.NewSession();
        await Turn(h, s.Id, "hi");

        // a plugin's tool arrives: nothing to point at yet
        using (h.Tools.Register(new FakeTool("web_probe", Ok), 0, "netpi.tools.web"))
        {
            await Turn(h, s.Id, "a web tool?");
            Check.Equal("unknown", Notices(h, s.Id, "tools").Last().MetaString("cause"), "nothing explains a new plugin");

            // the plugin is reloaded: the notice names it, and the meta keeps the cause
            h.Bus.Publish(new BusEvent
            {
                Type = EventTypes.PluginsReloaded,
                Data = new JsonObject { ["ids"] = new JsonArray("netpi.tools.web", "netpi.tools.media"), ["kind"] = "reload" },
            });
            await h.Bus.DrainAsync();
        }
        await Turn(h, s.Id, "gone?");
        var reloaded = Notices(h, s.Id, "tools").Last();
        Check.Equal("Your tools changed. No longer available: web_probe (plugin reload netpi.tools.web).", reloaded.Text);
        Check.Equal("plugin-reload", reloaded.MetaString("cause"));
        Check.Equal("netpi.tools.web", ((JsonArray)reloaded.Meta!["plugins"]!)[0]!.GetValue<string>());
    }

    // The host stops a plugin whose folder or assembly is gone and says so on the bus (plugins.reloaded, kind: "removed").
    // Without that the notice for its vanished tools has no cause at all, and the model is told a tool is "no longer
    // available" with nothing to explain it.
    private static async Task ToolChangeCausePluginRemoved()
    {
        await using var h = await TestHost.StartAsync();
        Task<ToolResult> Ok(ToolContext c, System.Text.Json.JsonElement a, CancellationToken t) => Task.FromResult(ToolResult.Ok(""));
        var s = h.NewSession();
        await Turn(h, s.Id, "hi");
        using (h.Tools.Register(new FakeTool("web_probe", Ok), 0, "netpi.tools.web"))
        {
            await Turn(h, s.Id, "a web tool?");
            h.Bus.Publish(new BusEvent
            {
                Type = EventTypes.PluginsReloaded,
                Data = new JsonObject { ["ids"] = new JsonArray("netpi.tools.web"), ["kind"] = "removed" },
            });
            await h.Bus.DrainAsync();
        }
        await Turn(h, s.Id, "gone?");
        var notice = Notices(h, s.Id, "tools").Last();
        Check.Equal("Your tools changed. No longer available: web_probe (plugin reload netpi.tools.web).", notice.Text);
        Check.Equal("plugin-reload", notice.MetaString("cause"), "named, not unknown");
        Check.Equal("netpi.tools.web", ((JsonArray)notice.Meta!["plugins"]!)[0]!.GetValue<string>());
    }

    // The profile cause used to be a string-match on the last notice's kind, so renaming that kind would have silently
    // turned it into "unknown". The host now publishes session.changed with the meta keys that changed, and that is the
    // signal: a profile switch with no profile notice at all is still named. The notice-order check stays as the fallback
    // for a chat whose event was missed (it started before this plugin did).
    private static async Task ToolChangeCauseProfileEvent()
    {
        await using var h = await TestHost.StartAsync();
        Task<ToolResult> Ok(ToolContext c, System.Text.Json.JsonElement a, CancellationToken t) => Task.FromResult(ToolResult.Ok(""));

        // (1) the event: meta.profile written, no notice — the event alone must name the cause
        var s = h.NewSession();
        await Turn(h, s.Id, "hi");
        var reg = h.Tools.Register(new FakeTool("web_probe", Ok), 0, "netpi.tools.web");
        await Turn(h, s.Id, "a web tool?");
        h.Sessions.UpdateSession(s.Id, x => (x.Meta ??= new System.Text.Json.Nodes.JsonObject())["profile"] = "coder");
        await h.Bus.DrainAsync();
        reg.Dispose();
        await Turn(h, s.Id, "gone?");
        var byEvent = Notices(h, s.Id, "tools").Last();
        Check.Equal("Your tools changed. No longer available: web_probe (the user switched this chat's profile).", byEvent.Text);
        Check.Equal("profile", byEvent.MetaString("cause"));

        // (2) the fallback: the convention the profiles plugin follows (a "profile" notice) with no event to read
        var t2 = h.NewSession();
        await Turn(h, t2.Id, "hi");
        var reg2 = h.Tools.Register(new FakeTool("media_probe", Ok), 0, "netpi.tools.media");
        await Turn(h, t2.Id, "a media tool?");
        var switched = h.Sessions.AppendMessage(t2.Id, ChatMessage.NoticeText("The user switched this chat to the profile \"x\".", "profile"));
        await h.Bus.DrainAsync();
        reg2.Dispose();
        await Turn(h, t2.Id, "gone?");
        var byNotice = Notices(h, t2.Id, "tools").Last();
        Check.Contains(byNotice.Text, "(the user switched this chat's profile)");
        Check.Equal("profile", byNotice.MetaString("cause"), "no event seen, the notice is the fallback");

        // (3) one-shot: a meta change that changed no tools must not be blamed for a later, unrelated change
        var u = h.NewSession();
        await Turn(h, u.Id, "hi");
        var reg3 = h.Tools.Register(new FakeTool("web_probe", Ok), 0, "netpi.tools.web");
        await Turn(h, u.Id, "a web tool?");
        h.Sessions.UpdateSession(u.Id, x => (x.Meta ??= new System.Text.Json.Nodes.JsonObject())["profile"] = "coder");
        await h.Bus.DrainAsync();
        await Turn(h, u.Id, "nothing changed here");   // the event is taken by this call and dropped
        reg3.Dispose();
        await Turn(h, u.Id, "gone?");
        var stale = Notices(h, u.Id, "tools").Last();
        Check.Equal("Your tools changed. No longer available: web_probe.", stale.Text, "a stale event must not be blamed");
        Check.Equal("unknown", stale.MetaString("cause"));
    }

    // The other two causes: the user's own switches (which the text already names) and a setting that took a tool away.
    private static async Task ToolChangeCausesUserAndSettings()
    {
        await using var h = await TestHost.StartAsync();
        Task<ToolResult> Ok(ToolContext c, System.Text.Json.JsonElement a, CancellationToken t) => Task.FromResult(ToolResult.Ok(""));
        h.AddTool(new FakeTool("probe_on", Ok));
        h.AddTool(new FakeTool("probe_off", Ok));
        var s = h.NewSession();
        await Turn(h, s.Id, "hi");

        await h.Rpc.CallAsync("agent.setTools", new { sessionId = s.Id, off = new[] { "probe_on" } });
        await Turn(h, s.Id, "one less?");
        var mine = Notices(h, s.Id, "tools").Last();
        Check.Equal("Your tools changed. The user switched off for this session: probe_on.", mine.Text, "no clause: it says so already");
        Check.Equal("user", mine.MetaString("cause"));

        h.Settings.SetQuiet("tools.disabled", new JsonArray("probe_off"));
        await Turn(h, s.Id, "and another?");
        var setting = Notices(h, s.Id, "tools").Last();
        Check.Equal("Your tools changed. No longer available: probe_off (the setting tools.disabled changed).", setting.Text);
        Check.Equal("settings", setting.MetaString("cause"));
    }

    // A change older than the newest page of messages must still be in the history: GetMessages serves the newest page,
    // so a long chat used to lose its oldest tool changes silently.
    private static async Task ToolSetsLongChat()
    {
        await using var h = await TestHost.StartAsync();
        Task<ToolResult> Ok(ToolContext c, System.Text.Json.JsonElement a, CancellationToken t) => Task.FromResult(ToolResult.Ok(""));
        var s = h.NewSession();
        await Turn(h, s.Id, "hi");

        using (h.Tools.Register(new FakeTool("old_probe", Ok), 0, "netpi.tools.web")) await Turn(h, s.Id, "a tool?");
        var changeSeq = Notices(h, s.Id, "tools").Single().Seq;

        // 520 later messages push that change out of the newest page
        for (var i = 0; i < 520; i++) h.Sessions.AppendMessage(s.Id, ChatMessage.NoticeText($"filler {i}", "filler"));
        Check.True(h.Messages(s.Id).Count > 500, "the chat is longer than one page");

        var ts = (JsonObject)(await h.Rpc.CallAsync("context.toolsets", new { sessionId = s.Id }))!;
        Check.Equal(false, (bool)ts["truncated"]!, "one page back is not the cap");
        var changes = (JsonArray)ts["changes"]!;
        Check.Equal(1, changes.Count, "the change is found again");
        Check.Equal(changeSeq, (long)changes[0]!["seq"]!, "the same one, from before the filler");
    }

    // context.toolsets answers "what changed in my environment, when and why" from what the context plugin already keeps.
    private static async Task ToolSets()
    {
        await using var h = await TestHost.StartAsync();
        Task<ToolResult> Ok(ToolContext c, System.Text.Json.JsonElement a, CancellationToken t) => Task.FromResult(ToolResult.Ok(""));
        var s = h.NewSession();

        var fresh = (JsonObject)(await h.Rpc.CallAsync("context.toolsets", new { sessionId = s.Id }))!;
        Check.True(fresh["baseline"] is null, "no baseline before the first model call");
        Check.Equal(0, ((JsonArray)fresh["changes"]!).Count);

        await Turn(h, s.Id, "hi");
        using (h.Tools.Register(new FakeTool("web_probe", Ok), 0, "netpi.tools.web")) await Turn(h, s.Id, "a web tool?");
        h.Bus.Publish(new BusEvent
        {
            Type = EventTypes.PluginsReloaded,
            Data = new JsonObject { ["ids"] = new JsonArray("netpi.tools.web"), ["kind"] = "reload" },
        });
        await h.Bus.DrainAsync();
        await Turn(h, s.Id, "gone?");

        var ts = (JsonObject)(await h.Rpc.CallAsync("context.toolsets", new { sessionId = s.Id }))!;
        Check.Equal(s.Id, (string?)ts["sessionId"]);
        var tools = ((JsonArray)ts["tools"]!).Select(t => (string?)t).ToList();
        Check.True(tools.Contains("agent_choices"), "the tools it has now");
        Check.False(tools.Contains("web_probe"), "the reloaded plugin's tool is gone");
        var baseline = (JsonObject)ts["baseline"]!;
        Check.True((long)baseline["sinceSeq"]! >= 0, "the baseline is the first call (after its setup notices)");
        Check.True(((JsonArray)baseline["tools"]!).Any(t => (string?)t == "agent_choices"), "what it started with");
        Check.False(((JsonArray)baseline["tools"]!).Any(t => (string?)t == "web_probe"), "before the tool arrived");

        var changes = (JsonArray)ts["changes"]!;
        Check.Equal(2, changes.Count, "both changes, oldest first");
        Check.Equal("unknown", (string?)changes[0]!["cause"], "a new plugin, nothing to name");
        Check.Equal("web_probe", ((JsonArray)changes[0]!["added"]!)[0]!.GetValue<string>());
        var last = changes[1]!;
        Check.Equal("plugin-reload", (string?)last["cause"]);
        Check.Equal("web_probe", ((JsonArray)last["removed"]!)[0]!.GetValue<string>());
        Check.Equal("netpi.tools.web", ((JsonArray)last["plugins"]!)[0]!.GetValue<string>());
        Check.Contains((string?)last["text"], "plugin reload netpi.tools.web", "the notice as the model got it");
        var reload = (JsonObject)((JsonArray)ts["reloads"]!)[0]!;
        Check.Equal("netpi.tools.web", ((JsonArray)reload["ids"]!)[0]!.GetValue<string>());

        var ex = await Check.ThrowsAsync<RpcException>(() => h.Rpc.CallAsync("context.toolsets", new { sessionId = "ses_nope" }));
        Check.Equal("not_found", ex.Code);
    }

    // Tools of one plugin share lines (the file tools' "use the file tools, not the shell"): a new tool's shared line is in
    // the context already when a tool that carries it was there before.
    private static async Task ToolNoticeKnownGuidelines()
    {
        await using var h = await TestHost.StartAsync();
        Task<ToolResult> Ok(ToolContext c, System.Text.Json.JsonElement a, CancellationToken t) => Task.FromResult(ToolResult.Ok(""));
        h.AddTool(new FakeTool("probe_a", Ok, guidelines: ["Shared probe line."]));
        var s = h.NewSession();
        await Turn(h, s.Id, "hi");
        Check.Contains(h.Catalog.Requests.Last().SystemPrompt!, "- Shared probe line.");

        using var b = h.Tools.Register(new FakeTool("probe_b", Ok, guidelines: ["Shared probe line.", "Use probe_b for testing."]));
        using var c = h.Tools.Register(new FakeTool("probe_c", Ok, guidelines: ["Shared probe line."]));
        await Turn(h, s.Id, "more tools?");
        Check.Equal("Your tools changed. New: probe_b, probe_c.\nGuidelines for the new tools:\n- Use probe_b for testing.",
            Notices(h, s.Id, "tools").Single().Text);
    }

    private static async Task GuidelineGroups()
    {
        await using var h = await TestHost.StartAsync();
        Task<ToolResult> Ok(ToolContext c, System.Text.Json.JsonElement a, CancellationToken t) => Task.FromResult(ToolResult.Ok(""));
        // sorted by name, as the runner passes them
        var tools = new[]
        {
            new FakeTool("aa_run", Ok, category: "shell", guidelines: ["Shared shell line."]).Definition,
            new FakeTool("mm_read", Ok, category: "files", guidelines: ["  Files line. ", ""]).Definition,
            new FakeTool("zz_run", Ok, category: "shell", guidelines: ["Shared shell line.", "Only zz_run."]).Definition,
        };
        var prompt = await h.Services.Get<ISystemPromptBuilder>()!.BuildAsync(Ctx(h, h.NewSession(), tools), CancellationToken.None);
        Check.Contains(prompt, "# Tools\n- Files line.\n- Shared shell line.\n- Only zz_run.\n- Any tool called with {\"help\": true} returns its full manual instead of running.\n\n");
        Check.Equal(1, prompt.Split("Shared shell line.").Length - 1, "a shared line once");
    }

    private static async Task InstructionGuidance()
    {
        await using var h = await TestHost.StartAsync();
        var builder = h.Services.Get<ISystemPromptBuilder>()!;
        async Task<string> Prompt() => await builder.BuildAsync(Ctx(h, h.NewSession(), []), CancellationToken.None);

        var prompt = await Prompt();
        Check.Contains(prompt, "# Instruction files\nAGENTS.md and CLAUDE.md files reach you as notices.");
        Check.Contains(prompt, "pointers to deeper docs");
        Check.Contains(prompt, "add one line to the most specific AGENTS.md");
        h.Settings.SetQuiet("agentsMd.guidance", "Keep AGENTS.md short.");
        Check.Contains(await Prompt(), "# Instruction files\nKeep AGENTS.md short.");
        h.Settings.SetQuiet("agentsMd.guidance", "");
        Check.NotContains(await Prompt(), "# Instruction files");
    }

    private static async Task CustomPrompt()
    {
        await using var h = await TestHost.StartAsync(x =>
        {
            x.Settings.SetQuiet("context.customPrompt", "You are Bob, a terse robot.");
            x.Settings.SetQuiet("context.appendPrompt", "ALWAYS ANSWER IN FRENCH.");
        });
        var s = h.NewSession();
        await h.SendAsync(s.Id, "hi");
        await h.IdleAsync(s.Id);
        var prompt = h.Catalog.Requests.Single().SystemPrompt!;
        Check.True(prompt.StartsWith("You are Bob, a terse robot."), "custom identity first");
        Check.NotContains(prompt, "coding agent running in NetPI");
        Check.True(prompt.EndsWith("ALWAYS ANSWER IN FRENCH."), "appended at the end");
        Check.Contains(prompt, "# Environment");
    }

    private static async Task Preview()
    {
        await using var h = await TestHost.StartAsync();
        h.AddTool(new FakeTool("read", (c, a, t) => Task.FromResult(ToolResult.Ok(""))));
        var s = h.NewSession();
        var preview = (await h.Rpc.CallAsync("context.preview", new { sessionId = s.Id }))!;
        var prompt = (string)preview["systemPrompt"]!;
        Check.Contains(prompt, "# Environment");
        Check.NotContains(prompt, "fake/local", "no model line");
        var tools = ((JsonArray)preview["tools"]!).Select(t => (string)t!["name"]!).ToList();
        Check.True(tools.Contains("read") && tools.Contains("agent_spawn"), string.Join(",", tools));
        Check.True(((JsonArray)preview["tools"]!)[0]!["description"] is not null);
        var tokens = preview["estimatedTokens"]!.GetValue<long>();
        Check.True(tokens > prompt.Length / 4, $"estimate includes tool schemas ({tokens})");

        // a subagent session previews with its role and restricted tools
        var sub = await h.Runtime.SpawnAsync(new SpawnRequest { Task = "just read", Name = "reader", Tools = ["read"] });
        await h.StatusAsync(sub.Id, AgentStatus.Completed);
        var sp = (await h.Rpc.CallAsync("context.preview", new { sessionId = sub.SessionId }))!;
        Check.Contains((string)sp["systemPrompt"]!, "You are \"reader\"");
        Check.Equal("read", string.Join(",", ((JsonArray)sp["tools"]!).Select(t => (string)t!["name"]!)));
        // the prompt the subagent actually got matches the preview
        var actual = h.Catalog.Requests.Single(r => r.SessionId == sub.SessionId).SystemPrompt!;
        Check.Contains(actual, "You are \"reader\"");

        try
        {
            await h.Rpc.CallAsync("context.preview", new { sessionId = "ses_nope" });
            throw new AssertException("expected not_found");
        }
        catch (RpcException ex) { Check.Equal("not_found", ex.Code); }
    }

    private sealed class ExtraSection : IPromptSection
    {
        public string Id => "extra";
        public int Order => 250;
        public ValueTask<string?> RenderAsync(PromptContext context, CancellationToken ct) => ValueTask.FromResult<string?>("# Extra\nFROM A PLUGIN");
    }

    private sealed class BrokenSection : IPromptSection
    {
        public string Id => "broken";
        public int Order => 260;
        public ValueTask<string?> RenderAsync(PromptContext context, CancellationToken ct) => throw new InvalidOperationException("broken section");
    }

    private static async Task PluginSectionAndFallback()
    {
        await using (var h = await TestHost.StartAsync())
        {
            h.Services.Register<IPromptSection>(new ExtraSection());
            h.Services.Register<IPromptSection>(new BrokenSection());
            var s = h.NewSession();
            await h.SendAsync(s.Id, "hi");
            await h.IdleAsync(s.Id);
            var prompt = h.Catalog.Requests.Single().SystemPrompt!;
            Check.Contains(prompt, "FROM A PLUGIN");
            Check.True(prompt.IndexOf("# Tools", StringComparison.Ordinal) < prompt.IndexOf("# Extra", StringComparison.Ordinal)
                       && prompt.IndexOf("# Extra", StringComparison.Ordinal) < prompt.IndexOf("# Agents", StringComparison.Ordinal), "ordered by Order");
        }

        // without the context plugin the runtime uses its minimal built-in prompt
        await using var h2 = await TestHost.StartAsync(plugins: TestHost.Plugins.Agents | TestHost.Plugins.Runtime);
        var s2 = h2.NewSession();
        await h2.SendAsync(s2.Id, "hi");
        await h2.IdleAsync(s2.Id);
        var fallback = h2.Catalog.Requests.Single().SystemPrompt!;
        Check.Contains(fallback, "NetPI");
        Check.Contains(fallback, "Working directory: " + h2.Workspace);
        Check.NotContains(fallback, DateTimeOffset.Now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), "no date in the built-in prompt");
        Check.Equal("agent_choices", string.Join(",", h2.Catalog.Requests.Single().Tools.Select(t => t.Name)), "only the agents plugin's tool");
    }
}
