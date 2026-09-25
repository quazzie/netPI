using System.Text.Json.Nodes;

namespace NetPI.Skills;

/// <summary>
/// Agent Skills (https://agentskills.io): folders with a SKILL.md (name, description, instructions) and whatever files
/// they bundle. Agents see the skills that apply to their working directory in "skills" notices (the catalog) and load
/// one with the <c>skill</c> tool; <c>/skill:name …</c> at the start of a message loads one for it. Where skills are
/// found: <see cref="SkillLoader"/>. See docs/PLUGIN-SKILLS.md.
/// <para>RPC: <c>skills.list { sessionId } | { projectId }</c> → <c>{ skills, problems }</c>.</para>
/// </summary>
[NetPiPlugin("netpi.skills", Name = "Skills", Description = "Agent Skills (SKILL.md folders): a catalog notice, the skill tool and /skill:name", Order = 42)]
public sealed class SkillsPlugin : INetPiPlugin
{
    private readonly string? _userHome;

    public SkillsPlugin() { }

    /// <summary>Tests: a stand-in for the user's home folder (~/.agents/skills, ~/.claude/skills).</summary>
    internal SkillsPlugin(string userHome) => _userHome = userHome;

    public Task StartAsync(IPluginContext context, CancellationToken ct)
    {
        context.Services.Register(new SettingsSection
        {
            Id = "skills", Title = "Skills", Group = "Context", Order = 30,
            Help = "Skill folders hold a SKILL.md each. Found in the project (.netpi/skills and .agents/skills, from the working folder up to the git root) and globally (skills in the NetPI home, ~/.agents/skills); the project's come first.",
            Settings =
            [
                SettingInfo.List("skills.paths", "More skill folders", [], "Folders of skills, or one skill's folder. ~ is your home; a relative path resolves against the working folder.", "~/my-skills"),
                SettingInfo.Bool("skills.claudeCode", "Claude Code's skills too", false, "Also .claude/skills in the project and ~/.claude/skills. They may refer to Claude Code's own tools."),
                SettingInfo.List("skills.disabled", "Switched off", [], "Skill names agents don't see and /skill: refuses."),
            ],
        });
        var loader = new SkillLoader(context, _userHome);
        context.Services.Register<IAgentHook>(new SkillNotices(context, loader));
        context.Tools.Register(new SkillTool(loader, context));
        context.Rpc.Register("skills.list", (req, _) =>
        {
            string cwd;
            if (req.Str("projectId") is { Length: > 0 } projectId)
                cwd = (context.Sessions.GetProject(projectId) ?? throw new RpcException("not_found", $"No project {projectId}")).Path;
            else
            {
                var sessionId = req.Required("sessionId");
                var session = context.Sessions.GetSession(sessionId) ?? throw new RpcException("not_found", $"No session {sessionId}");
                cwd = context.Sessions.GetCwd(session);
            }
            return Task.FromResult<object?>(ToJson(loader.Discover(cwd)));
        }, "Skills that apply to a session or a project folder: { sessionId } | { projectId } → { skills: [{ name, description, path, scope, listed, userOnly, disabled, license?, compatibility? }], problems: [{ path, level, message }] }");
        return Task.CompletedTask;
    }

    internal static JsonObject ToJson(SkillSet set)
    {
        var skills = new JsonArray();
        foreach (var s in set.Skills)
        {
            var o = new JsonObject
            {
                ["name"] = s.Name, ["description"] = s.Description, ["path"] = s.Path, ["scope"] = s.Scope,
                ["listed"] = s.Listed, ["userOnly"] = !s.ModelInvocable, ["disabled"] = s.Disabled,
            };
            if (s.License is not null) o["license"] = s.License;
            if (s.Compatibility is not null) o["compatibility"] = s.Compatibility;
            if (s.AllowedTools.Count > 0) o["allowedTools"] = new JsonArray([.. s.AllowedTools.Select(t => (JsonNode)JsonValue.Create(t))]);
            skills.Add(o);
        }
        var problems = new JsonArray();
        foreach (var p in set.Problems) problems.Add(new JsonObject { ["path"] = p.Path, ["level"] = p.Level, ["message"] = p.Message });
        return new JsonObject { ["skills"] = skills, ["problems"] = problems };
    }
}
