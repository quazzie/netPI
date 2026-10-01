namespace NetPI.Ideas;

internal sealed class IdeaWork(IPluginContext ctx) : IBackgroundWork
{
    private readonly Lock _gate = new();
    private readonly List<BackgroundWorkInfo> _items = [];
    public string Begin(string purpose, string? model, string? sessionId, string? projectId)
    {
        var item = new BackgroundWorkInfo { Id = Ids.New("check"), Purpose = purpose, Model = model, SessionId = sessionId, ProjectId = projectId };
        lock (_gate) _items.Add(item);
        Changed();
        return item.Id;
    }
    public void Set(string id, string status, string? reason = null)
    {
        lock (_gate)
        {
            var item = _items.FirstOrDefault(x => x.Id == id);
            if (item is null) return;
            item.Status = status; item.Reason = reason;
            if (status is not ("waiting" or "running" or "yielding")) item.FinishedAt = DateTimeOffset.UtcNow;
            var terminal = _items.Where(x => x.FinishedAt is not null).OrderByDescending(x => x.FinishedAt).Skip(40).ToList();
            foreach (var old in terminal) _items.Remove(old);
        }
        Changed();
    }
    public IReadOnlyList<BackgroundWorkInfo> Snapshot()
    {
        lock (_gate) return _items.Select(x => new BackgroundWorkInfo
        {
            Id = x.Id, Purpose = x.Purpose, Model = x.Model, SessionId = x.SessionId, ProjectId = x.ProjectId,
            Status = x.Status, Reason = x.Reason, Since = x.Since, FinishedAt = x.FinishedAt,
        }).ToList();
    }
    private void Changed() => ctx.Events.Publish("ideas.workChanged", new { work = Snapshot() });
}
