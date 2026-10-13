using System.Text.Json.Nodes;

namespace NetPI.Providers.ClaudeCli;

/// <summary>
/// Snapshot of <c>providers.claudeCli</c>: enabled, executable, models [ids], timeoutSeconds, tools ("default" = every
/// built-in tool of the CLI, or a list, or "none"), permissionMode, settingSources, connectors, usage (the subscription
/// usage pill), stallTimeoutSeconds, maxConcurrent.
/// </summary>
internal sealed class ClaudeCliOptions
{
    public static readonly string[] DefaultModels = ["claude-opus-5-5", "claude-sonnet-5-5", "claude-haiku-5-5"];
    public static readonly string[] Efforts = ["low", "medium", "high", "xhigh", "max"];
    public static readonly string[] PermissionModes = ["bypassPermissions", "acceptEdits", "plan", "default"];

    public bool Enabled { get; init; } = true;
    public string Executable { get; init; } = "claude";
    /// <summary>The CLI's own config folder (<c>CLAUDE_CONFIG_DIR</c>), where its login lives: another one is another account. Empty = the default.</summary>
    public string ConfigDir { get; init; } = "";
    public List<string> Models { get; init; } = [.. DefaultModels];
    /// <summary>The longest one call may run. A CLI that works for an hour on a big task is not stuck: the stall timeout and the
    /// process ending are what detect a dead one, so this is a backstop, and killing a run that was making progress wastes it.</summary>
    public int TimeoutSeconds { get; init; } = 3600;
    /// <summary>"default" (all built-in tools), "none", or the tool names handed to <c>--tools</c>.</summary>
    public string Tools { get; init; } = "default";
    /// <summary>A print-mode call cannot answer a permission prompt, so the default lets its tools run.</summary>
    public string PermissionMode { get; init; } = "bypassPermissions";
    /// <summary>Settings files the CLI loads (user, project, local; empty = none). They bring hooks and permission rules with them.</summary>
    public string SettingSources { get; init; } = "";
    /// <summary>Let the CLI load the account's MCP connectors (claude.ai) as well.</summary>
    public bool Connectors { get; init; }
    /// <summary>Show the owner's Claude subscription usage in the top bar (read from the CLI's own output).</summary>
    public bool Usage { get; init; } = true;
    /// <summary>
    /// Max silence between the CLI's stream events, in seconds (0 = no stall detection). The CLI works with its own
    /// tools and subagents, and a long Bash command or a subagent can stay silent for minutes, so the default is far
    /// above the retry plugin's global one; a dead process ends the stream, which the provider already reports.
    /// </summary>
    public int StallTimeoutSeconds { get; init; } = 1800;
    /// <summary>How many CLI processes run at once: each is a full Claude Code (memory, and the subscription's rate limits), and
    /// the calls beyond it wait.</summary>
    public int MaxConcurrent { get; init; } = 2;

    private static string ExpandHome(string path) =>
        path is "~" || path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path.TrimStart('~').TrimStart('/', '\\'))
            : path;

    public static ClaudeCliOptions Parse(JsonObject? o)
    {
        var models = new List<string>();
        if (o.Arr("models") is { } arr)
            foreach (var n in arr)
                if (n is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s)) models.Add(s.Trim());
        var mode = o.Str("permissionMode")?.Trim();
        return new ClaudeCliOptions
        {
            Enabled = o.Bool("enabled", true),
            Executable = string.IsNullOrWhiteSpace(o.Str("executable")) ? "claude" : o.Str("executable")!.Trim(),
            ConfigDir = ExpandHome((o.Str("configDir") ?? "").Trim()),
            Models = models.Count > 0 ? models : [.. DefaultModels],
            TimeoutSeconds = Math.Clamp(o.Int("timeoutSeconds") ?? 3600, 10, 14400),
            Tools = string.IsNullOrWhiteSpace(o.Str("tools")) ? "default" : o.Str("tools")!.Trim(),
            PermissionMode = PermissionModes.Contains(mode, StringComparer.OrdinalIgnoreCase) ? mode! : "bypassPermissions",
            SettingSources = (o.Str("settingSources") ?? "").Trim(),
            Connectors = o.Bool("connectors", false),
            Usage = o.Bool("usage", true),
            StallTimeoutSeconds = Math.Clamp(o.Int("stallTimeoutSeconds") ?? 1800, 0, 7200),
            MaxConcurrent = Math.Clamp(o.Int("maxConcurrent") ?? 2, 1, 16),
        };
    }
}
