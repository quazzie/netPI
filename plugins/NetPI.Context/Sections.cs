using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;

namespace NetPI.Context;

internal static class SectionUtil
{
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
        "Work through the tools you have: act rather than describe, check the results and verify your work when practical. " +
        "Ask only when a request is genuinely ambiguous or an action would be destructive. " +
        "Be concise, and end with a short summary of what you did or found.";

    public string Id => "identity";
    public int Order => 0;

    public ValueTask<string?> RenderAsync(PromptContext context, CancellationToken ct)
    {
        var custom = SectionUtil.SettingString(settings, "context.customPrompt");
        return ValueTask.FromResult<string?>(string.IsNullOrWhiteSpace(custom) ? Default : custom);
    }
}

/// <summary>
/// The harness the agent runs in: OS, working directory and project, model, and where notices come from (order 100).
/// Nothing here may change between the turns of a session (no date or time): the system prompt is the start of every
/// request, and any change to it makes the backend re-prefill the whole conversation.
/// </summary>
internal sealed class EnvironmentSection : IPromptSection
{
    public string Id => "environment";
    public int Order => 100;

    public ValueTask<string?> RenderAsync(PromptContext c, CancellationToken ct)
    {
        var sb = new StringBuilder("# Environment\n");
        sb.Append("- OS: ").Append(OsName()).Append('\n');
        sb.Append("- Working directory: ").Append(c.Cwd).Append(c.Project is { } p ? $" (project: {p.Name})" : " (no project: the default workspace)")
          .Append("; relative paths resolve against it\n");
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

/// <summary>
/// The guideline bullets the active tools contribute (order 200); with <c>context.toolDescriptions</c> also one line per
/// tool (descriptions and schemas are always sent with the tool definitions). Everything feature-specific comes from the
/// plugin that owns it: tools bring their own bullets, other plugins register their own sections.
/// </summary>
internal sealed class ToolsSection(ISettings settings) : IPromptSection
{
    public string Id => "tools";
    public int Order => 200;

    public ValueTask<string?> RenderAsync(PromptContext c, CancellationToken ct)
    {
        var sb = new StringBuilder();
        if (settings.Get("context.toolDescriptions", false))
            foreach (var t in c.Tools)
                sb.Append("- ").Append(t.Name).Append(": ").Append(SectionUtil.Summary(t.Description)).Append('\n');
        foreach (var g in c.Tools.SelectMany(t => t.PromptGuidelines ?? []).Where(g => !string.IsNullOrWhiteSpace(g))
                     .Select(g => g.Trim()).Distinct(StringComparer.Ordinal))
            sb.Append("- ").Append(g).Append('\n');
        return ValueTask.FromResult<string?>(sb.Length == 0 ? null : "# Tools\n" + sb.ToString().TrimEnd());
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
