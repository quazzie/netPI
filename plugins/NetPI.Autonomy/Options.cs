using System.Text.Json.Nodes;

namespace NetPI.Autonomy;

/// <summary>The <c>autonomy.*</c> settings, read per use (a change applies to the next call).</summary>
internal sealed class Options(ISettings settings)
{
    public static readonly string[] DefaultExternalTools = ["web_fetch", "web_search", "browser", "mcp_resource", "plugin:netpi.mcp"];

    /// <summary>What an unattended chat that read outside text may not do: <c>tool</c>, or <c>tool: action, action</c>.</summary>
    public static readonly string[] DefaultTaintedBlocked =
    [
        "browser: click, type, key, upload, dialog, eval, steps",
        "windows: open, click, type, key, toggle, expand, collapse, select, focus, steps",
        "schedules: add, update, run",
        "webhooks: test, pause",
    ];

    public bool Paused => Get("autonomy.paused", false);
    /// <summary><c>refuse</c> (default): an ask rule in an unattended chat is refused and kept for a grant; <c>wait</c>: it waits for the owner as before.</summary>
    public bool RefuseAsks => !string.Equals(Get("autonomy.unattendedAsks", "refuse")?.Trim(), "wait", StringComparison.OrdinalIgnoreCase);
    public int MaxChangesPerHour => Math.Max(0, Get("autonomy.maxChangesPerHour", 120));
    public int KeepDays => Math.Clamp(Get("autonomy.keepDays", 30), 1, 365);
    public int GrantUnusedDays => Math.Max(0, Get("autonomy.grantUnusedDays", 60));
    public long MaxCheckpointBytes => (long)Math.Clamp(Get("autonomy.maxCheckpointKb", 4096), 0, 65536) * 1024;
    public IReadOnlyList<string> ExternalTools => List("autonomy.externalTools", DefaultExternalTools);
    public IReadOnlyList<string> TaintedBlocked => List("autonomy.taintedBlocked", DefaultTaintedBlocked);

    /// <summary>Whether a tool brings outside text: listed by name, or registered by a listed plugin (<c>plugin:&lt;id&gt;</c>).</summary>
    public bool IsExternal(string tool, string? pluginId) => ExternalTools.Any(e =>
        e.StartsWith("plugin:", StringComparison.OrdinalIgnoreCase)
            ? pluginId is not null && e[7..].Trim().Equals(pluginId, StringComparison.OrdinalIgnoreCase)
            : e.Equals(tool, StringComparison.OrdinalIgnoreCase));

    /// <summary>The line of <see cref="TaintedBlocked"/> that holds this call, or null.</summary>
    public string? TaintedRule(string tool, string? action)
    {
        foreach (var line in TaintedBlocked)
        {
            var colon = line.IndexOf(':');
            var name = (colon < 0 ? line : line[..colon]).Trim();
            if (!name.Equals(tool, StringComparison.OrdinalIgnoreCase)) continue;
            if (colon < 0) return line;
            var actions = line[(colon + 1)..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (actions.Length == 0 || (action is not null && actions.Contains(action, StringComparer.OrdinalIgnoreCase))) return line;
        }
        return null;
    }

    private T Get<T>(string path, T fallback)
    {
        try { return settings.Get(path, fallback) ?? fallback; }
        catch { return fallback; }
    }

    private List<string> List(string path, IReadOnlyList<string> fallback)
    {
        try
        {
            if (settings.GetNode(path) is JsonArray a)
                return [.. a.Select(x => x is JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim() : null).Where(s => !string.IsNullOrEmpty(s)).Select(s => s!)];
        }
        catch { }
        return [.. fallback];
    }
}
