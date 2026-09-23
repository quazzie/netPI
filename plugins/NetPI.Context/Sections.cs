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
        "You are an expert coding agent running inside NetPI, a minimal agent harness on the user's own machine. " +
        "You help the user with software engineering tasks by reading files, running commands, editing code and writing new files.\n\n" +
        "Be concise and direct. Act, don't just describe: when something needs doing, do it with your tools and check the result. " +
        "Ask only when the request is genuinely ambiguous or an action would be destructive.";

    public string Id => "identity";
    public int Order => 0;

    public ValueTask<string?> RenderAsync(PromptContext context, CancellationToken ct)
    {
        var custom = SectionUtil.SettingString(settings, "context.customPrompt");
        return ValueTask.FromResult<string?>(string.IsNullOrWhiteSpace(custom) ? Default : custom);
    }
}

/// <summary>Date, OS, shells, cwd, project and model (order 100).</summary>
internal sealed class EnvironmentSection : IPromptSection
{
    public string Id => "environment";
    public int Order => 100;

    public ValueTask<string?> RenderAsync(PromptContext c, CancellationToken ct)
    {
        var now = DateTimeOffset.Now;
        var offset = now.Offset;
        var sign = offset < TimeSpan.Zero ? "-" : "+";
        var tz = $"{TimeZoneInfo.Local.Id} (UTC{sign}{offset.Duration():hh\\:mm})";

        var sb = new StringBuilder("# Environment\n");
        sb.Append("- Date: ").Append(now.ToString("dddd yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)).Append(", time zone ").Append(tz).Append('\n');
        sb.Append("- OS: ").Append(OsName()).Append(" (").Append(RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant()).Append(")\n");

        var shells = new List<string>();
        if (SectionUtil.Has(c, "bash"))
            shells.Add(OperatingSystem.IsWindows()
                ? "`bash` (Git Bash: Unix syntax; Windows paths and /c/... paths both work)"
                : "`bash`");
        if (SectionUtil.Has(c, "pwsh")) shells.Add("`pwsh` (PowerShell)");
        if (shells.Count > 0) sb.Append("- Shells: ").Append(string.Join(", ", shells)).Append('\n');

        sb.Append("- Working directory: ").Append(c.Cwd).Append('\n');
        sb.Append(c.Project is { } p
            ? $"- Project: {p.Name} ({p.Path})\n"
            : "- Project: none (working in the default workspace)\n");
        sb.Append("- Model: ").Append(c.Model.Ref);
        if (c.Model.ContextWindow is { } w) sb.Append(" (context window ").Append(w.ToString("N0", CultureInfo.InvariantCulture)).Append(" tokens)");
        return ValueTask.FromResult<string?>(sb.ToString());
    }

    private static string OsName()
    {
        if (OperatingSystem.IsWindows()) return $"Windows {Environment.OSVersion.Version}";
        if (OperatingSystem.IsMacOS()) return $"macOS {Environment.OSVersion.Version}";
        return RuntimeInformation.OSDescription;
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
