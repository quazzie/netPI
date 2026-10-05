namespace NetPI.Ideas;

/// <summary>One background check as <c>ideas.work</c> lists it. Only this plugin reads and writes the list (the Work
/// tab asks over RPC), so it is the plugin's own, not a shared contract.</summary>
internal sealed class BackgroundWorkInfo
{
    public string Id { get; set; } = "";
    public string Purpose { get; set; } = "";
    public string? Model { get; set; }
    public string? SessionId { get; set; }
    public string? ProjectId { get; set; }
    public string Status { get; set; } = "waiting";
    public string? Reason { get; set; }
    public DateTimeOffset Since { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FinishedAt { get; set; }
}

internal interface IBackgroundWork
{
    string Begin(string purpose, string? model, string? sessionId, string? projectId);
    void Set(string id, string status, string? reason = null);
    IReadOnlyList<BackgroundWorkInfo> Snapshot();
}
