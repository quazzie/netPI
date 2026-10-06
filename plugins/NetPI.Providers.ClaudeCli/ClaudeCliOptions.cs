using System.Text.Json.Nodes;

namespace NetPI.Providers.ClaudeCli;

/// <summary>
/// Snapshot of <c>providers.claudeCli</c>: enabled, executable, models [ids], timeoutSeconds, tools ("default" = every
/// built-in tool of the CLI, or a list, or "none"), permissionMode, settingSources, connectors.
/// </summary>
internal sealed class ClaudeCliOptions
{
    public static readonly string[] DefaultModels = ["claude-opus-5-5", "claude-sonnet-5-5", "claude-haiku-4-5"];
    public static readonly string[] Efforts = ["low", "medium", "high", "xhigh", "max"];
    public static readonly string[] PermissionModes = ["bypassPermissions", "acceptEdits", "plan", "default"];

    public bool Enabled { get; init; }
    public string Executable { get; init; } = "claude";
    public List<string> Models { get; init; } = [.. DefaultModels];
    public int TimeoutSeconds { get; init; } = 900;
    /// <summary>"default" (all built-in tools), "none", or the tool names handed to <c>--tools</c>.</summary>
    public string Tools { get; init; } = "default";
    /// <summary>A print-mode call cannot answer a permission prompt, so the default lets its tools run.</summary>
    public string PermissionMode { get; init; } = "bypassPermissions";
    /// <summary>Settings files the CLI loads (user, project, local; empty = none). They bring hooks and permission rules with them.</summary>
    public string SettingSources { get; init; } = "";
    /// <summary>Let the CLI load the account's MCP connectors (claude.ai) as well.</summary>
    public bool Connectors { get; init; }

    public static ClaudeCliOptions Parse(JsonObject? o)
    {
        var models = new List<string>();
        if (o.Arr("models") is { } arr)
            foreach (var n in arr)
                if (n is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s)) models.Add(s.Trim());
        var mode = o.Str("permissionMode")?.Trim();
        return new ClaudeCliOptions
        {
            Enabled = o.Bool("enabled", false),
            Executable = string.IsNullOrWhiteSpace(o.Str("executable")) ? "claude" : o.Str("executable")!.Trim(),
            Models = models.Count > 0 ? models : [.. DefaultModels],
            TimeoutSeconds = Math.Clamp(o.Int("timeoutSeconds") ?? 900, 10, 7200),
            Tools = string.IsNullOrWhiteSpace(o.Str("tools")) ? "default" : o.Str("tools")!.Trim(),
            PermissionMode = PermissionModes.Contains(mode, StringComparer.OrdinalIgnoreCase) ? mode! : "bypassPermissions",
            SettingSources = (o.Str("settingSources") ?? "").Trim(),
            Connectors = o.Bool("connectors", false),
        };
    }
}
