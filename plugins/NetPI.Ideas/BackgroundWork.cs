namespace NetPI;

public sealed class BackgroundWorkInfo
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

public interface IBackgroundWork
{
    string Begin(string purpose, string? model, string? sessionId, string? projectId);
    void Set(string id, string status, string? reason = null);
    IReadOnlyList<BackgroundWorkInfo> Snapshot();
}
