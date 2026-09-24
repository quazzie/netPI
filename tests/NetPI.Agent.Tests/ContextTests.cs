using System.Text.Json.Nodes;

namespace NetPI.Agent.Tests;

public static class ContextTests
{
    public static void Register(TestRunner t)
    {
        t.Add("context: system prompt sections in order", PromptSections);
        t.Add("context: no date or time in the system prompt (identical on every turn)", NoDateOrTime);
        t.Add("context: plugins add the guidance for what they own (lanes, agent tools)", PluginOwnedGuidance);
        t.Add("context: AGENTS.md discovery, order, caps and cache", AgentsMdDiscovery);
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
        var lanes = Idx("# Lanes");
        var role = Idx("# Your role");
        Check.True(identity < env && env < tools && tools < lanes && lanes < role, "section order");
        Check.Contains(prompt, $"- Working directory: {h.Workspace} (project: Demo); relative paths resolve against it");
        Check.Contains(prompt, "- Model: fake/local, context window 100,000 tokens");
        Check.Contains(prompt, "<system-notice>");
        Check.Contains(prompt, "Use edit for testing.");
        Check.Contains(prompt, "Delegate independent, well-scoped work");
        Check.Contains(prompt, "call agent_wait once");
        Check.Contains(prompt, "lanes_list shows the pools");
        Check.Contains(prompt, "You are \"w1\", a subagent.");
        Check.NotContains(prompt, "\n\n\n", "no empty sections");

        // no project, no tools: the bare base only
        var bare = await builder.BuildAsync(Ctx(h, h.NewSession(), []), CancellationToken.None);
        Check.Contains(bare, "(no project: the default workspace)");
        Check.NotContains(bare, "# Tools");
        Check.NotContains(bare, "# Lanes");
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

        var (noLanes, noLanesTools) = await Build(TestHost.Plugins.All & ~TestHost.Plugins.Lanes);
        Check.False(noLanesTools.Any(t => t.Name == "lanes_list"), "lanes_list comes from the lanes plugin");
        Check.Contains(noLanes, "Delegate independent, well-scoped work", "the agent tools still bring their tips");
        Check.NotContains(noLanes, "# Lanes");
        Check.NotContains(noLanes, "lane", "nothing about lanes without the lanes plugin");

        var (noAgentTools, _) = await Build(TestHost.Plugins.All & ~TestHost.Plugins.AgentTools);
        Check.NotContains(noAgentTools, "agent_spawn", "no delegation tips without the agent tools");
        Check.NotContains(noAgentTools, "# Lanes", "no lanes section for an agent that cannot spawn");
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

    private static async Task AgentsMdDiscovery()
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

        var builder = h.Services.Get<ISystemPromptBuilder>()!;
        var prompt = await builder.BuildAsync(Ctx(h, s, []), CancellationToken.None);
        var g = prompt.IndexOf("GLOBAL RULES", StringComparison.Ordinal);
        var r = prompt.IndexOf("ROOT RULES", StringComparison.Ordinal);
        var c = prompt.IndexOf("SRC CLAUDE RULES", StringComparison.Ordinal);
        var a = prompt.IndexOf("APP AGENTS RULES", StringComparison.Ordinal);
        Check.True(g >= 0 && r > g && c > r && a > c, $"global → root → leaf order ({g}, {r}, {c}, {a})");
        Check.NotContains(prompt, "shadowed", "only the first file name per directory");
        Check.Contains(prompt, "# Project instructions");
        Check.Contains(prompt, "## " + Path.Combine(root, "AGENTS.md"));
        Check.Contains(prompt, "(global)");
        Check.True(prompt.IndexOf("# Environment", StringComparison.Ordinal) < prompt.IndexOf("# Project instructions", StringComparison.Ordinal), "after the environment");

        var list = (JsonArray)(await h.Rpc.CallAsync("agentsmd.list", new { sessionId = s.Id }))!;
        Check.Equal("global,project,project,project", string.Join(",", list.Select(x => (string?)x!["scope"])));
        Check.Equal(Path.Combine(sub, "AGENTS.md"), (string?)list[^1]!["path"]);
        Check.Equal(16L, list[^1]!["bytes"]!.GetValue<long>());

        // cap at 32KB with a note
        File.WriteAllText(Path.Combine(root, "AGENTS.md"), "BIG " + new string('x', 40_000));
        prompt = await builder.BuildAsync(Ctx(h, s, []), CancellationToken.None);
        Check.Contains(prompt, "[Truncated: this file is 39 KB; only the first 32 KB are included");
        Check.True(prompt.Length < 40_000, "capped");

        // cache invalidation by mtime/size
        var appFile = Path.Combine(sub, "AGENTS.md");
        File.WriteAllText(appFile, "APP RULES V2");
        File.SetLastWriteTimeUtc(appFile, DateTime.UtcNow.AddMinutes(1));
        prompt = await builder.BuildAsync(Ctx(h, s, []), CancellationToken.None);
        Check.Contains(prompt, "APP RULES V2");
        Check.NotContains(prompt, "APP AGENTS RULES");

        // custom file names + extra files
        var extra = Path.Combine(h.Root, "extra.md");
        File.WriteAllText(extra, "EXTRA RULES");
        h.Settings.SetQuiet("agentsMd.fileNames", new JsonArray("CLAUDE.md"));
        h.Settings.SetQuiet("agentsMd.extraFiles", new JsonArray(extra));
        prompt = await builder.BuildAsync(Ctx(h, s, []), CancellationToken.None);
        Check.Contains(prompt, "APP CLAUDE RULES (shadowed)");
        Check.NotContains(prompt, "ROOT RULES");
        Check.Contains(prompt, "EXTRA RULES");
        Check.True(prompt.IndexOf("EXTRA RULES", StringComparison.Ordinal) > prompt.IndexOf("APP CLAUDE RULES", StringComparison.Ordinal), "extra files last");
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
        Check.Contains(prompt, "fake/local");
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
                       && prompt.IndexOf("# Extra", StringComparison.Ordinal) < prompt.IndexOf("# Lanes", StringComparison.Ordinal), "ordered by Order");
        }

        // without the context plugin the runtime uses its minimal built-in prompt
        await using var h2 = await TestHost.StartAsync(plugins: TestHost.Plugins.Lanes | TestHost.Plugins.Agent);
        var s2 = h2.NewSession();
        await h2.SendAsync(s2.Id, "hi");
        await h2.IdleAsync(s2.Id);
        var fallback = h2.Catalog.Requests.Single().SystemPrompt!;
        Check.Contains(fallback, "NetPI");
        Check.Contains(fallback, "Working directory: " + h2.Workspace);
        Check.NotContains(fallback, DateTimeOffset.Now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), "no date in the built-in prompt");
        Check.Equal("lanes_list", string.Join(",", h2.Catalog.Requests.Single().Tools.Select(t => t.Name)), "only the lanes plugin's tool");
    }
}
