using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;

namespace NetPI.Context;

internal static class SectionUtil
{
    public static bool Has(PromptContext c, string tool) => c.Tools.Any(t => string.Equals(t.Name, tool, StringComparison.OrdinalIgnoreCase));

    public static string? SettingString(ISettings settings, string path)
    {
        try
        {
            return settings.GetNode(path) switch
            {
                JsonValue v when v.TryGetValue<string>(out var s) => s,
                JsonArray a => string.Join("\n", a.Select(x => x?.ToString())),
                _ => null,
            };
        }
        catch { return null; }
    }

    /// <summary>First line (or sentence) of a tool description, capped.</summary>
    public static string Summary(string description, int max = 220)
    {
        var s = description.Trim();
        var nl = s.IndexOf('\n');
        if (nl > 0) s = s[..nl].Trim();
        if (s.Length > max)
        {
            var dot = s.LastIndexOf(". ", max, StringComparison.Ordinal);
            s = dot > 40 ? s[..(dot + 1)] : s[..max].TrimEnd() + "…";
        }
        return s;
    }
}

/// <summary>Who the agent is (order 0). <c>context.customPrompt</c> replaces it.</summary>
internal sealed class IdentitySection(ISettings settings) : IPromptSection
{
    public const string Default =
        "You are a coding agent running in NetPI, an agent harness on the user's own machine. " +
        "Work through your tools: read, search and change files, run commands, and check the results. " +
        "Act rather than describe; ask only when a request is genuinely ambiguous or an action would be destructive. Be concise.";

    public string Id => "identity";
    public int Order => 0;

    public ValueTask<string?> RenderAsync(PromptContext context, CancellationToken ct)
    {
        var custom = SectionUtil.SettingString(settings, "context.customPrompt");
        return ValueTask.FromResult<string?>(string.IsNullOrWhiteSpace(custom) ? Default : custom);
    }
}

/// <summary>
/// OS, shells, working directory, project, model and where harness notices come from (order 100). Nothing here may
/// change between the turns of a session (no date or time): the system prompt is the start of every request, and any
/// change to it makes the backend re-prefill the whole conversation.
/// </summary>
internal sealed class EnvironmentSection : IPromptSection
{
    public string Id => "environment";
    public int Order => 100;

    public ValueTask<string?> RenderAsync(PromptContext c, CancellationToken ct)
    {
        var sb = new StringBuilder("# Environment\n");
        sb.Append("- OS: ").Append(OsName()).Append('\n');

        var shells = new List<string>();
        if (SectionUtil.Has(c, "bash")) shells.Add(OperatingSystem.IsWindows() ? "bash (Git Bash)" : "bash");
        if (SectionUtil.Has(c, "pwsh")) shells.Add("pwsh (PowerShell)");
        if (shells.Count > 0) sb.Append("- Shells: ").Append(string.Join(", ", shells)).Append('\n');

        sb.Append("- Working directory: ").Append(c.Cwd).Append(c.Project is { } p ? $" (project: {p.Name})" : " (no project: the default workspace)").Append('\n');
        sb.Append("- Model: ").Append(c.Model.Ref);
        if (c.Model.ContextWindow is { } w) sb.Append(", context window ").Append(w.ToString("N0", CultureInfo.InvariantCulture)).Append(" tokens");
        sb.Append("\n- Messages in <system-notice> tags come from NetPI (project switches, subagent reports, reminders, errors), not from the user.");
        return ValueTask.FromResult<string?>(sb.ToString());
    }

    internal static string OsName()
    {
        var arch = RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant();
        if (OperatingSystem.IsWindows())
        {
            var v = Environment.OSVersion.Version;
            var name = v.Major == 10 && v.Build >= 22000 ? "11" : v.Major == 10 ? "10" : $"{v.Major}.{v.Minor}";
            return $"Windows {name} (build {v.Build}, {arch})";
        }
        if (OperatingSystem.IsMacOS()) return $"macOS {Environment.OSVersion.Version} ({arch})";
        return $"{RuntimeInformation.OSDescription} ({arch})";
    }
}

/// <summary>One line per active tool plus each tool's prompt guidelines (order 200).</summary>
internal sealed class ToolsSection(ISettings settings) : IPromptSection
{
    public string Id => "tools";
    public int Order => 200;

    public ValueTask<string?> RenderAsync(PromptContext c, CancellationToken ct)
    {
        if (c.Tools.Count == 0) return ValueTask.FromResult<string?>(null);
        var sb = new StringBuilder("# Tools\n");
        if (settings.Get("context.toolDescriptions", false))
        {
            foreach (var t in c.Tools)
                sb.Append("- ").Append(t.Name).Append(": ").Append(SectionUtil.Summary(t.Description)).Append('\n');
        }
        else
        {
            // Descriptions and schemas are sent with the tool definitions; here only a compact index by category.
            foreach (var g in c.Tools.GroupBy(t => string.IsNullOrWhiteSpace(t.Category) ? "general" : t.Category))
                sb.Append("- ").Append(g.Key).Append(": ").Append(string.Join(", ", g.Select(t => t.Name))).Append('\n');
        }

        var bullets = c.Tools.SelectMany(t => t.PromptGuidelines ?? []).Where(g => !string.IsNullOrWhiteSpace(g))
            .Select(g => g.Trim()).Distinct(StringComparer.Ordinal).ToList();
        if (bullets.Count > 0)
        {
            sb.Append('\n');
            foreach (var g in bullets) sb.Append("- ").Append(g).Append('\n');
        }
        return ValueTask.FromResult<string?>(sb.ToString().TrimEnd());
    }
}

/// <summary>General working rules and lane/subagent guidance (order 300).</summary>
internal sealed class GuidelinesSection : IPromptSection
{
    public string Id => "guidelines";
    public int Order => 300;

    public ValueTask<string?> RenderAsync(PromptContext c, CancellationToken ct)
    {
        var sb = new StringBuilder("# Guidelines\n");
        sb.Append("- Use absolute paths or paths relative to the working directory.\n");
        sb.Append("- Verify your work when practical (build, run tests) and say what you verified.\n");
        sb.Append("- When you are done, summarize briefly what you changed or found.\n");

        var spawn = SectionUtil.Has(c, "agent_spawn");
        if (spawn || SectionUtil.Has(c, "lanes_list"))
        {
            sb.Append("\n## Lanes and subagents\n");
            sb.Append("- Every model belongs to a pool with a fixed number of lanes (parallel slots). An agent holds a lane for its whole run; when none is free it queues.");
            if (SectionUtil.Has(c, "lanes_list")) sb.Append(" `lanes_list` shows pools, capacity and what is running.");
            sb.Append('\n');
            if (spawn)
            {
                sb.Append("- Delegate independent, well-scoped work (research, exploring code, separate modules) to subagents with `agent_spawn`. " +
                          "Each subagent has its own session and does not see this conversation: give it a complete, self-contained task.\n");
                if (SectionUtil.Has(c, "agent_wait"))
                    sb.Append("- Spawn all workers first, then call `agent_wait` once. Waiting yields your lane so workers can use it, and you resume with priority, receiving only their final reports.\n");
                sb.Append("- Subagents report back automatically when they finish (an <agent-result> notice); do not poll them. Don't delegate trivial work you can do in a couple of tool calls.\n");
            }
        }
        return ValueTask.FromResult<string?>(sb.ToString().TrimEnd());
    }
}

/// <summary>Subagent role / extra instructions (order 800): <see cref="PromptContext.Instructions"/> or the session's stored instructions.</summary>
internal sealed class SubagentSection : IPromptSection
{
    public string Id => "subagent";
    public int Order => 800;

    public ValueTask<string?> RenderAsync(PromptContext c, CancellationToken ct)
    {
        var text = c.Instructions;
        if (string.IsNullOrWhiteSpace(text) && c.Session.Meta?["agentInstructions"] is JsonValue v && v.TryGetValue<string>(out var s)) text = s;
        if (string.IsNullOrWhiteSpace(text)) return ValueTask.FromResult<string?>(null);
        var header = c.IsSubagent || c.Session.Kind == "subagent" ? "# Your role" : "# Instructions";
        return ValueTask.FromResult<string?>(header + "\n" + text.Trim());
    }
}

/// <summary><c>context.appendPrompt</c> (order 900).</summary>
internal sealed class AppendSection(ISettings settings) : IPromptSection
{
    public string Id => "append";
    public int Order => 900;

    public ValueTask<string?> RenderAsync(PromptContext context, CancellationToken ct) =>
        ValueTask.FromResult(SectionUtil.SettingString(settings, "context.appendPrompt"));
}
