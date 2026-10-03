using System.Text.Json;
using System.Text.Json.Nodes;

namespace NetPI.Skills;

/// <summary>
/// <c>skill { name }</c>: a skill's instructions (see <see cref="SkillContent"/>). The name is checked at run time, not
/// with an enum in the schema: the skills depend on the working directory and change, while a tool definition changing
/// would make the backend re-read the conversation. Loading one that is already in the context unchanged returns a short
/// note instead of a second copy. Details: <c>{ name, path, dir, scope, hash, already? }</c>.
/// </summary>
internal sealed class SkillTool(SkillLoader loader, IPluginContext ctx) : IAgentTool
{
    public const string Name = "skill";

    public ToolDefinition Definition { get; } = new()
    {
        Name = Name,
        Label = "Skill",
        Description = "Load a skill: the instructions of one of the skills listed in <available_skills> notices, with its folder and the files it " +
                      "bundles.",
        Category = "skills",
        ReadOnly = true,
        SummaryArg = "name",
        PromptGuidelines =
        [
            "Skills (listed in <available_skills> notices) are instructions for specific tasks: when a task matches a skill's description, load it with skill before you start and follow it. Load only the skills the task needs.",
        ],
        Parameters = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["name"] = new JsonObject { ["type"] = "string", ["description"] = "The skill's name, as listed." },
            },
            ["required"] = new JsonArray("name"),
        },
    };

    public Task<ToolResult> ExecuteAsync(ToolContext context, JsonElement args, CancellationToken ct)
    {
        var set = loader.Discover(context.Cwd);
        var names = set.Listed.Select(s => s.Name).ToList();
        string Choices() => names.Count > 0 ? "The skills: " + string.Join(", ", names) + "." : "No skills are available here.";

        var name = new ToolArgs(args).Str("name", "skill");
        if (string.IsNullOrWhiteSpace(name)) return Task.FromResult(ToolResult.Error("Missing 'name'. " + Choices()));
        var skill = set.Find(name);
        if (skill is null) return Task.FromResult(ToolResult.Error($"No skill named \"{name.Trim()}\" here. {Choices()}"));
        if (skill.Disabled) return Task.FromResult(ToolResult.Error($"The skill \"{skill.Name}\" is switched off in the settings. {Choices()}"));
        if (!skill.ModelInvocable)
            return Task.FromResult(ToolResult.Error($"The skill \"{skill.Name}\" is for the user to load (/skill:{skill.Name}), not for agents. {Choices()}"));

        var loaded = SkillContent.Load(skill);
        if (loaded is null) return Task.FromResult(ToolResult.Error($"Can't read {skill.Path}."));
        var details = new JsonObject
        {
            ["name"] = skill.Name, ["path"] = skill.Path, ["dir"] = skill.Dir, ["scope"] = skill.Scope, ["hash"] = loaded.Hash,
        };
        if (InContext(context.SessionId, skill.Name, loaded.Hash))
        {
            details["already"] = true;
            return Task.FromResult(ToolResult.Ok($"The skill \"{skill.Name}\" is already loaded above, unchanged: follow those instructions.", details));
        }
        SkillNotices.RecordLoaded(ctx, context.SessionId, skill.Name);
        return Task.FromResult(ToolResult.Ok(loaded.Text, details));
    }

    /// <summary>These instructions are in the context already (a skill tool result or a "skill" notice with the same content).</summary>
    private bool InContext(string sessionId, string name, string hash)
    {
        IReadOnlyList<ChatMessage> context;
        try { context = ctx.Sessions.GetContextMessages(sessionId); }
        catch { return false; }
        foreach (var m in context)
        {
            if (m.Role == MessageRole.Notice && m.MetaString("kind") == SkillNotices.SkillKind
                && string.Equals(m.MetaString("skill"), name, StringComparison.OrdinalIgnoreCase) && m.MetaString("hash") == hash) return true;
            if (m.Role != MessageRole.Tool) continue;
            foreach (var r in m.ToolResults)
                if (r.Name == Name && !r.IsError && r.Details is JsonObject d && d["already"] is null
                    && string.Equals(d["name"]?.ToString(), name, StringComparison.OrdinalIgnoreCase) && d["hash"]?.ToString() == hash) return true;
        }
        return false;
    }

}
