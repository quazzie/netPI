using System.Text.Json.Nodes;

namespace NetPI.Providers.ClaudeCli;

/// <summary>Snapshot of <c>providers.claudeCli</c>: enabled, executable, models [ids], timeoutSeconds.</summary>
internal sealed class ClaudeCliOptions
{
    public static readonly string[] DefaultModels = ["claude-opus-5-5", "claude-sonnet-5-5", "claude-haiku-4-5"];
    public static readonly string[] Efforts = ["low", "medium", "high", "xhigh", "max"];

    public bool Enabled { get; init; }
    public string Executable { get; init; } = "claude";
    public List<string> Models { get; init; } = [.. DefaultModels];
    public int TimeoutSeconds { get; init; } = 900;

    public static ClaudeCliOptions Parse(JsonObject? o)
    {
        var models = new List<string>();
        if (o.Arr("models") is { } arr)
            foreach (var n in arr)
                if (n is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s)) models.Add(s.Trim());
        return new ClaudeCliOptions
        {
            Enabled = o.Bool("enabled", false),
            Executable = string.IsNullOrWhiteSpace(o.Str("executable")) ? "claude" : o.Str("executable")!.Trim(),
            Models = models.Count > 0 ? models : [.. DefaultModels],
            TimeoutSeconds = Math.Clamp(o.Int("timeoutSeconds") ?? 900, 10, 7200),
        };
    }
}
