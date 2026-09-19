using System.Text.Json;

namespace NetPI.Abstractions;

/// <summary>Summary of a session (PLAN §29/§43).</summary>
public sealed record SessionInfo(
    string Id,
    string? WorkspacePath,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int EntryCount,
    string? Title)
{
    public override string ToString() => $"{Id} ({EntryCount} entries)";
}

/// <summary>A single entry in an append-only session (PLAN §30).</summary>
public sealed record SessionEntry(
    string Id,
    string SessionId,
    EntryKind Kind,
    AgentMessage? Message,
    JsonElement? Payload,
    DateTimeOffset CreatedAt)
{
    public override string ToString() => $"{Kind} {Id}";
}

/// <summary>Kind of session entry.</summary>
public enum EntryKind
{
    Message = 0,
    /// <summary>A compaction checkpoint (PLAN §31): prior history replaced by summary.</summary>
    Compaction = 1,
    /// <summary>Generic metadata (config snapshot, model change, ...).</summary>
    Metadata = 2,
}

/// <summary>Append-only session store. Owned by a storage plugin (later phase).</summary>
public interface ISessionStore
{
    ValueTask<SessionInfo> CreateAsync(string? workspacePath, CancellationToken cancellationToken = default);
    ValueTask<SessionInfo?> GetAsync(string id, CancellationToken cancellationToken = default);
    ValueTask AppendAsync(SessionEntry entry, CancellationToken cancellationToken = default);
    /// <summary>Paginated read of entries in append order (oldest first).</summary>
    ValueTask<IReadOnlyList<SessionEntry>> ReadAsync(
        string sessionId,
        int offset,
        int count,
        CancellationToken cancellationToken = default);
}
