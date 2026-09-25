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
        t.Add("context: tools are sent sorted by name", ToolOrder);
        t.Add("context: tools added or removed mid-session arrive as a notice with their guidelines", ToolChangeNotices);
        t.Add("context: guidance on using and keeping AGENTS.md (setting replaces or drops it)", InstructionGuidance);
        t.Add("context: custom and appended prompt settings", CustomPrompt);
        t.Add("context: context.preview", Preview);
        t.Add("context: plugin sections and fallback prompt", PluginSectionAndFallback);
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
        Check.Contains(prompt, "call agent_wait once");
        Check.Contains(prompt, "Before you delegate, look at agent_choices");
        Check.Contains(prompt, "your instance is free for them: count it as a free instance of your own agent");
        Check.Contains(prompt, "start them in one agent_spawn call (subagents: [...])");
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

        await Turn(h, s.Id, "again");
        Check.Equal(1, Notices(h, s.Id, "project").Count, "not repeated");

        h.Sessions.SetSessionProject(s.Id, beta.Id);
        await Wait.Until(() => Notices(h, s.Id, "project").Count == 2, "switch notice right away");
        Check.Equal($"The session moved to project \"Beta\": the working directory is now {beta.Path}.", Notices(h, s.Id, "project")[1].Text);
        await Turn(h, s.Id, "in beta");
        Check.Equal(2, Notices(h, s.Id, "project").Count, "the hook does not repeat the switch notice");
        PrefixKept(r1, h.Catalog.Requests.Last());

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
