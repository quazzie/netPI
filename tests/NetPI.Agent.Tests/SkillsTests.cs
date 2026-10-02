using System.Text.Json.Nodes;
using NetPI.Skills;

namespace NetPI.Agent.Tests;

public static class SkillsTests
{
    public static void Register(TestRunner t)
    {
        t.Add("skills: SKILL.md frontmatter is read leniently (colons, quotes, block scalars, lists, a nested map)", FrontmatterParsing);
        t.Add("skills: found in the project up to the git root, in extra paths and globally; the project's win; problems are listed", Discovery);
        t.Add("skills: the catalog arrives as notices: all at first, then only changes, again after compaction with what was loaded", CatalogNotices);
        t.Add("skills: the skill tool returns the instructions with the folder and its files, once; refuses unknown, off and user-only skills", SkillTool);
        t.Add("skills: /skill:name at the start of a message loads that skill for it", SlashSkill);
        t.Add("skills: a recorded load is announced after compaction from the session's meta, not a history walk", MetaLoadedList);
        t.Add("skills: a chat with the skill tool switched off gets no catalog until it is on again; /skill:name still works", ToolSwitchedOff);
    }

    private sealed record Setup(TestHost Host, string Repo, string Sub, string User, SessionInfo Session);

    private static string Skill(string root, string folder, string frontmatter, string body = "Do the thing.")
    {
        var dir = Path.Combine(root, folder);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "SKILL.md"), $"---\n{frontmatter}\n---\n\n{body}\n");
        return dir;
    }

    /// <summary>A repo (with .git) whose project folder is src/app, a stand-in user home, and a session in the project.</summary>
    private static async Task<Setup> StartAsync(Action<TestHost>? setup = null)
    {
        var h = await TestHost.StartAsync(setup);
        var repo = Path.Combine(h.Root, "repo");
        var sub = Path.Combine(repo, "src", "app");
        Directory.CreateDirectory(sub);
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        var user = Path.Combine(h.Root, "user");
        Directory.CreateDirectory(user);
        await h.StartPluginAsync(new SkillsPlugin(user));
        var project = h.Sessions.CreateProject("App", sub);
        return new Setup(h, repo, sub, user, h.NewSession(projectId: project.Id));
    }

    private static List<ChatMessage> Notices(TestHost h, string sessionId, string kind) =>
        h.Sessions.GetMessages(sessionId, null, 1000).Where(m => m.Role == MessageRole.Notice && m.MetaString("kind") == kind).ToList();

    private static async Task Turn(TestHost h, string sessionId, string text)
    {
        await h.SendAsync(sessionId, text);
        await h.IdleAsync(sessionId);
    }

    private static void FrontmatterParsing()
    {
        var text = string.Join("\r\n",
            "---",
            "name: pdf-tools",
            "description: Use this skill when: the user asks about PDFs",
            "  and forms.",
            "license: \"Apache-2.0\" # comment",
            "compatibility: >",
            "  Needs python",
            "  and uv.",
            "",
            "  Second paragraph.",
            "allowed-tools: Bash(git:*) Read",
            "metadata:",
            "  author: example-org",
            "  version: \"1.0\"",
            "tags:",
            "- a",
            "- \"b c\"",
            "flow: [x, 'y z']",
            "literal: |",
            "  line 1",
            "    indented",
            "quoted: \"a \\\"q\\\" b\\nnext\"",
            "single: 'it''s'",
            "disable-model-invocation: true",
            "---",
            "",
            "# Body",
            "text");
        var r = Frontmatter.Parse(text)!;
        var f = r.Fields;
        Check.Equal("pdf-tools", f.Str("name"));
        Check.Equal("Use this skill when: the user asks about PDFs and forms.", f.Str("description"), "a colon in a plain value, folded over the next line");
        Check.Equal("Apache-2.0", f.Str("license"));
        Check.Equal("Needs python and uv.\nSecond paragraph.", f.Str("compatibility"));
        Check.Equal("Bash(git:*),Read", string.Join(",", f.Words("allowed-tools")));
        var meta = (Dictionary<string, string>)f["metadata"];
        Check.Equal("example-org", meta["author"]);
        Check.Equal("1.0", meta["version"]);
        Check.Equal("a|b c", string.Join("|", (List<string>)f["tags"]));
        Check.Equal("x|y z", string.Join("|", (List<string>)f["flow"]));
        Check.Equal("line 1\n  indented", f.Str("literal"));
        Check.Equal("a \"q\" b\nnext", f.Str("quoted"));
        Check.Equal("it's", f.Str("single"));
        Check.True(f.Flag("disable-model-invocation"));
        Check.Equal("# Body\ntext", r.Body);
        Check.Equal(27, r.BodyLine);

        Check.True(Frontmatter.Parse("# no frontmatter\n") is null, "no frontmatter");
        Check.True(Frontmatter.Parse("---\nname: x\ndescription: never closed\n") is null, "not closed");
        Check.Equal("x", Frontmatter.Parse("\n---\nname: x\n---\n")!.Fields.Str("name"), "leading blank lines");
    }

    private static async Task Discovery()
    {
        var (h, repo, sub, user, s) = await StartAsync();
        await using var _ = h;
        var projectSkills = Path.Combine(repo, ".agents", "skills");
        var deploy = Skill(Path.Combine(sub, ".netpi", "skills"), "deploy", "name: deploy\ndescription: Deploy the app.");
        Skill(projectSkills, "review", "name: review\ndescription: Project review rules.");
        Skill(projectSkills, "broken", "name: broken");
        Skill(projectSkills, "manual", "name: manual\ndescription: Only when the user asks.\ndisable-model-invocation: true");
        Skill(Path.Combine(projectSkills, "group"), "nested-one", "name: nested-one\ndescription: In a grouping folder.");
        Skill(projectSkills, "Odd_Folder", "name: odd\ndescription: Named differently.");
        Skill(Path.Combine(projectSkills, "node_modules"), "hidden", "name: hidden\ndescription: Never found.");
        var globalReview = Skill(Path.Combine(h.Home, "skills"), "review", "name: review\ndescription: Global review rules.");
        Skill(Path.Combine(user, ".agents", "skills"), "notes", "name: notes\ndescription: Take notes.");
        Skill(Path.Combine(user, ".claude", "skills"), "claude-only", "name: claude-only\ndescription: From Claude Code.");
        Skill(Path.Combine(h.Root, ".agents", "skills"), "outside", "name: outside\ndescription: Above the git root.");

        async Task<JsonObject> List() => (JsonObject)(await h.Rpc.CallAsync("skills.list", new { sessionId = s.Id }))!;
        var list = await List();
        var skills = ((JsonArray)list["skills"]!).OfType<JsonObject>().ToList();
        string Names() => string.Join(",", skills.Select(x => (string?)x["name"]).OrderBy(x => x));
        Check.Equal("deploy,manual,nested-one,notes,odd,review", Names());
        Check.Equal("deploy", (string?)skills[0]["name"], "the working folder's own skills first");
        JsonObject One(string name) => skills.Single(x => (string?)x["name"] == name);
        Check.Equal(Path.Combine(deploy, "SKILL.md"), (string?)One("deploy")["path"]);
        Check.Equal("project", (string?)One("review")["scope"]);
        Check.Equal("Project review rules.", (string?)One("review")["description"], "the project's skill wins over the global one");
        Check.Equal("global", (string?)One("notes")["scope"]);
        Check.True(One("manual")["userOnly"]!.GetValue<bool>() && !One("manual")["listed"]!.GetValue<bool>(), "user-only: not listed for agents");
        Check.True(One("deploy")["listed"]!.GetValue<bool>());

        var problems = ((JsonArray)list["problems"]!).OfType<JsonObject>().ToList();
        string Problem(string path) => string.Join(" | ", problems.Where(p => (string?)p["path"] == path).Select(p => $"{p["level"]}: {p["message"]}"));
        Check.Contains(Problem(Path.Combine(projectSkills, "broken", "SKILL.md")), "error: No description: skipped");
        Check.Contains(Problem(Path.Combine(globalReview, "SKILL.md")), "warning: Another skill named \"review\" comes first");
        Check.Contains(Problem(Path.Combine(projectSkills, "Odd_Folder", "SKILL.md")), "warning: The name \"odd\" differs from its folder \"Odd_Folder\"");

        // …and the same for the project itself
        var project = s.ProjectId!;
        Check.Equal(list.ToJsonString(), ((JsonObject)(await h.Rpc.CallAsync("skills.list", new { projectId = project }))!).ToJsonString());

        // Claude Code's folders, extra paths and switched-off skills
        var extra = Skill(Path.Combine(h.Root, "more"), "extra-one", "name: extra-one\ndescription: From an extra folder.");
        h.Settings.SetQuiet("skills.claudeCode", true);
        h.Settings.SetQuiet("skills.paths", new JsonArray(Path.Combine(h.Root, "more")));
        h.Settings.SetQuiet("skills.disabled", new JsonArray("notes"));
        skills = ((JsonArray)(await List())["skills"]!).OfType<JsonObject>().ToList();
        Check.Equal("claude-only,deploy,extra-one,manual,nested-one,notes,odd,review", Names());
        Check.Equal("extra", (string?)One("extra-one")["scope"]);
        Check.True(One("notes")["disabled"]!.GetValue<bool>() && !One("notes")["listed"]!.GetValue<bool>(), "switched off");
        h.Settings.SetQuiet("skills.paths", new JsonArray(extra)); // one skill's own folder
        skills = ((JsonArray)(await List())["skills"]!).OfType<JsonObject>().ToList();
        Check.Equal("extra", (string?)One("extra-one")["scope"]);
    }

    private static async Task CatalogNotices()
    {
        var (h, repo, _, _, s) = await StartAsync();
        await using var __ = h;
        var skills = Path.Combine(repo, ".agents", "skills");
        var deploy = Skill(skills, "deploy", "name: deploy\ndescription: Deploy the app <safely> & quickly.");
        Skill(skills, "review", "name: review\ndescription: Review a change.");
        Skill(skills, "manual", "name: manual\ndescription: User only.\ndisable-model-invocation: true");

        await Turn(h, s.Id, "hi");
        var first = Notices(h, s.Id, "skills").Single();
        Check.Contains(first.Text, "Skills: instructions for specific tasks. When a task matches a skill's description, load it with the skill tool before you start and follow it.");
        Check.Contains(first.Text, "<available_skills>");
        Check.Contains(first.Text, "<name>deploy</name>\n    <description>Deploy the app &lt;safely&gt; &amp; quickly.</description>\n  </skill>");
        Check.NotContains(first.Text, deploy, "no locations: the skill tool returns the folder");
        Check.Contains(first.Text, "<name>review</name>");
        Check.NotContains(first.Text, "manual", "user-only skills are not listed");
        var r1 = h.Catalog.Requests.Last();
        Check.True(r1.Messages.Any(m => m.Text.StartsWith("<system-notice kind=\"skills\">") && m.Text.Contains("<name>deploy</name>")), "sent to the model");
        Check.NotContains(r1.SystemPrompt, "<name>deploy</name>", "not in the system prompt");
        Check.Contains(r1.SystemPrompt, "Skills (listed in <available_skills> notices) are instructions for specific tasks", "the tool's guideline");
        Check.True(r1.Tools.Any(t => t.Name == "skill"), "the skill tool");

        await Turn(h, s.Id, "again");
        Check.Equal(1, Notices(h, s.Id, "skills").Count, "nothing changed: no notice");

        // a new skill and an edited description: only those; a removed skill by name
        Skill(skills, "notes", "name: notes\ndescription: Take notes.");
        Skill(skills, "review", "name: review\ndescription: Review a change carefully.");
        await Turn(h, s.Id, "changed");
        var delta = Notices(h, s.Id, "skills").Last();
        Check.Contains(delta.Text, "The skills changed. New or changed:");
        Check.Contains(delta.Text, "<name>notes</name>");
        Check.Contains(delta.Text, "Review a change carefully.");
        Check.NotContains(delta.Text, "deploy", "unchanged skills are not repeated");
        Directory.Delete(Path.Combine(skills, "notes"), true);
        await Turn(h, s.Id, "removed");
        Check.Equal("The skills changed. No longer available: notes.", Notices(h, s.Id, "skills").Last().Text);
        Check.Equal(first.Text, Notices(h, s.Id, "skills")[0].Text, "the first notice is untouched");
        var messages = h.Catalog.Requests.Last().Messages;
        for (var i = 0; i < r1.Messages.Count; i++) Check.Equal(r1.Messages[i].Text, messages[i].Text, $"message {i} unchanged");

        // the model loads a skill; after compaction the catalog is announced again, with what was loaded before
        h.Catalog.Handler = (r, ct) => Reply.HasToolResult(r) ? Reply.Text("loaded") : Reply.Tool("skill", new { name = "deploy" });
        await Turn(h, s.Id, "deploy it");
        Check.Equal("deploy", LoadedNames(h, s), "the load is recorded in the session's meta");
        h.Catalog.Handler = (r, ct) => Reply.Text("ok");
        h.Sessions.MarkCompacted(s.Id, h.Sessions.GetMessages(s.Id, null, 1000)[^1].Seq);
        await Turn(h, s.Id, "after compaction");
        var again = Notices(h, s.Id, "skills").Last().Text;
        Check.Contains(again, "Skills: instructions for specific tasks");
        Check.Contains(again, "<name>deploy</name>");
        Check.Contains(again, "Before the conversation was compacted you had loaded: deploy. Load a skill again if you still need its instructions.");
    }

    private static async Task SkillTool()
    {
        var (h, repo, _, _, s) = await StartAsync();
        await using var __ = h;
        var skills = Path.Combine(repo, ".agents", "skills");
        var deploy = Skill(skills, "deploy", "name: deploy\ndescription: Deploy the app.", "# Deploy\n\nRun scripts/run.sh, then check references/REF.md.");
        Directory.CreateDirectory(Path.Combine(deploy, "scripts"));
        File.WriteAllText(Path.Combine(deploy, "scripts", "run.sh"), "echo run");
        Directory.CreateDirectory(Path.Combine(deploy, "references"));
        File.WriteAllText(Path.Combine(deploy, "references", "REF.md"), "ref");
        Skill(skills, "manual", "name: manual\ndescription: User only.\ndisable-model-invocation: true");
        Skill(skills, "review", "name: review\ndescription: Review.");
        var body = string.Join("\n", Enumerable.Range(1, 600).Select(i => $"line {i:0000} " + new string('x', 40)));
        var longDir = Skill(skills, "long", "name: long\ndescription: A long one.", body);
        h.Settings.SetQuiet("skills.disabled", new JsonArray("review"));

        var calls = new object[]
        {
            new { name = "deploy" }, new { name = "Deploy" }, new { name = "nope" }, new { name = "manual" }, new { name = "review" }, new { name = "long" }, new { },
        };
        var step = 0;
        h.Catalog.Handler = (r, ct) => step < calls.Length ? Reply.Tool("skill", calls[step++]) : Reply.Text("done");
        await Turn(h, s.Id, "use skills");
        var results = h.Messages(s.Id).Where(m => m.Role == MessageRole.Tool).Select(m => m.ToolResults.Single()).ToList();
        Check.Equal(calls.Length, results.Count);

        var loaded = results[0];
        Check.False(loaded.IsError);
        Check.Contains(loaded.Content, "<skill_content name=\"deploy\">\n# Deploy\n\nRun scripts/run.sh, then check references/REF.md.");
        Check.NotContains(loaded.Content, "description:", "no frontmatter");
        Check.Contains(loaded.Content, $"Skill directory: {deploy}\nRelative paths in this skill resolve against it.");
        Check.Contains(loaded.Content, "<skill_resources>\n  <file>references/REF.md</file>\n  <file>scripts/run.sh</file>\n</skill_resources>\n</skill_content>");
        Check.NotContains(loaded.Content, "<file>SKILL.md</file>");
        var d = (JsonObject)loaded.Details!;
        Check.Equal("deploy", (string?)d["name"]);
        Check.Equal("project", (string?)d["scope"]);
        Check.Equal(Path.Combine(deploy, "SKILL.md"), (string?)d["path"]);

        Check.Equal("The skill \"deploy\" is already loaded above, unchanged: follow those instructions.", results[1].Content);
        Check.True(results[2].IsError);
        Check.Contains(results[2].Content, "No skill named \"nope\" here. The skills: deploy, long.");
        Check.Contains(results[3].Content, "The skill \"manual\" is for the user to load (/skill:manual), not for agents.");
        Check.Contains(results[4].Content, "The skill \"review\" is switched off in the settings.");
        Check.Contains(results[6].Content, "Missing 'name'.");

        // a long SKILL.md is cut at a line, with where to read on
        var cut = results[5].Content;
        Check.Contains(cut, $"[… SKILL.md continues: read {Path.Combine(longDir, "SKILL.md")} from line ");
        var line = int.Parse(System.Text.RegularExpressions.Regex.Match(cut, @"from line (\d+) for the rest").Groups[1].Value);
        var fileLines = File.ReadAllLines(Path.Combine(longDir, "SKILL.md"));
        Check.Contains(cut, fileLines[line - 2], "the line before is in");
        Check.NotContains(cut, fileLines[line - 1], "the line it names is not");
        Check.True(cut.Length < 20_000, "under the runtime's limit for a tool result");

        Check.Equal("deploy,long", LoadedNames(h, s), "fresh loads are recorded once, in load order: the repeat (Deploy) adds nothing, nor do the errors");
    }

    /// <summary>The session's <c>meta.loadedSkills</c> as names.</summary>
    private static string LoadedNames(TestHost h, SessionInfo s)
    {
        if (h.Sessions.GetSession(s.Id)?.Meta?[SkillNotices.LoadedMetaKey] is not JsonArray list) return "";
        return string.Join(",", list.Select(n => n?.GetValue<string>() ?? ""));
    }

    private static async Task ToolSwitchedOff()
    {
        var (h, repo, _, _, s) = await StartAsync();
        await using var __ = h;
        Skill(Path.Combine(repo, ".agents", "skills"), "deploy", "name: deploy\ndescription: Deploy the app.", "Ship it carefully.");
        await h.Rpc.CallAsync("agent.setTools", new { sessionId = s.Id, off = new[] { "skill" } });

        await Turn(h, s.Id, "hi");
        Check.False(h.Catalog.Requests.Last().Tools.Any(t => t.Name == "skill"), "the tool is off");
        Check.Equal(0, Notices(h, s.Id, "skills").Count, "no catalog without the tool");
        await Turn(h, s.Id, "/skill:deploy now");
        Check.Contains(Notices(h, s.Id, "skill").Single().Text, "Ship it carefully.", "the user can still load a skill");

        await h.Rpc.CallAsync("agent.setTools", new { sessionId = s.Id, on = new[] { "skill" } });
        await Turn(h, s.Id, "tool back");
        Check.Contains(Notices(h, s.Id, "skills").Single().Text, "<name>deploy</name>", "announced once the tool is on");
    }

    private static async Task SlashSkill()
    {
        var (h, repo, _, _, s) = await StartAsync();
        await using var __ = h;
        var skills = Path.Combine(repo, ".agents", "skills");
        var deploy = Skill(skills, "deploy", "name: deploy\ndescription: Deploy the app.", "# Deploy\n\nShip it carefully.");
        Skill(skills, "manual", "name: manual\ndescription: User only.\ndisable-model-invocation: true", "Manual steps.");

        await Turn(h, s.Id, "/skill:deploy to staging");
        var msgs = h.Messages(s.Id);
        var user = msgs.Single(m => m.Role == MessageRole.User);
        var notice = Notices(h, s.Id, "skill").Single();
        Check.Equal("deploy", notice.MetaString("skill"));
        Check.Equal(user.Id.ToString(), notice.MetaString("for"));
        Check.Contains(notice.Text, "The user loaded the skill \"deploy\" for their message: follow its instructions.\n\n<skill_content name=\"deploy\">\n# Deploy\n\nShip it carefully.");
        Check.Contains(notice.Text, $"Skill directory: {deploy}");
        var sent = h.Catalog.Requests.Last().Messages.Select(m => m.Text).ToList();
        var at = sent.FindIndex(t => t.Contains("/skill:deploy to staging"));
        Check.True(at >= 0 && sent.FindIndex(t => t.Contains("<skill_content name=\"deploy\">")) > at, "the skill follows the message");

        await Turn(h, s.Id, "thanks");
        Check.Equal(1, Notices(h, s.Id, "skill").Count, "only for /skill: messages, once");

        // the model loading it again gets the short note
        h.Catalog.Handler = (r, ct) => Reply.HasToolResult(r) ? Reply.Text("ok") : Reply.Tool("skill", new { name = "deploy" });
        await Turn(h, s.Id, "load deploy");
        Check.Contains(h.Messages(s.Id).Last(m => m.Role == MessageRole.Tool).ToolResults.Single().Content, "already loaded above");
        h.Catalog.Handler = (r, ct) => Reply.Text("ok");

        // user-only skills work here; an unknown one is reported
        await Turn(h, s.Id, "/skill:manual");
        Check.Contains(Notices(h, s.Id, "skill").Last().Text, "Manual steps.");
        await Turn(h, s.Id, "/skill:nope do it");
        var missing = Notices(h, s.Id, "skill").Last();
        Check.Equal("The user asked for the skill \"nope\", but there is no such skill here. The skills: deploy, manual.", missing.Text);
        Check.True(missing.Meta?["missing"] is not null);
        Check.Equal("deploy,manual", LoadedNames(h, s), "user loads are recorded in the meta; the missing skill is not");
    }

    private static async Task MetaLoadedList()
    {
        var (h, repo, _, _, s) = await StartAsync();
        await using var __ = h;
        Skill(Path.Combine(repo, ".agents", "skills"), "deploy", "name: deploy\ndescription: Deploy the app.");
        // A fresh session, no history at all: the announcement can only come from the meta, not a walk of the history.
        h.Sessions.UpdateSession(s.Id, x =>
        {
            x.Meta ??= new JsonObject();
            x.Meta[SkillNotices.LoadedMetaKey] = new JsonArray("deploy");
        });
        await Turn(h, s.Id, "hi");
        var first = Notices(h, s.Id, "skills").Single();
        Check.Contains(first.Text, "Before the conversation was compacted you had loaded: deploy. Load a skill again if you still need its instructions.");
    }
}
