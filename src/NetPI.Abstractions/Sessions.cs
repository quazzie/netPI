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
    /// <summary>Model last selected for this session (PLAN §14/§31, restored on open).</summary>
    public string? ModelId { get; init; }
    /// <summary>Reasoning level last selected for this session.</summary>
    public string? ReasoningLevel { get; init; }

    public override string ToString() => $"{Id} ({EntryCount} entries)";
}

/// <summary>A single entry in an append-only session (PLAN §30).</summary>
public sealed record SessionEntry(
    string Id,
    string SessionId,
    EntryKind Kind,
    AgentMessage? Message,
    JsonElement? Payload,
    DateTimeOffset CreatedAt,
    /// <summary>Monotonic position in the session (1-based, PLAN §31). 0 when unassigned.</summary>
    int Sequence = 0)
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

    /// <summary>Most recent sessions first (drawer listing, PLAN §43).</summary>
    ValueTask<IReadOnlyList<SessionInfo>> ListAsync(int count = 50, int offset = 0, CancellationToken cancellationToken = default);

    /// <summary>Total session count (drawer "load more" pagination, PLAN §43).</summary>
    ValueTask<int> CountAsync(CancellationToken cancellationToken = default);

    /// <summary>Update a session title (drawer rename, PLAN §43).</summary>
    ValueTask RenameAsync(string id, string title, CancellationToken cancellationToken = default);

    /// <summary>Delete a session and all of its entries (drawer delete, PLAN §43).</summary>
    ValueTask DeleteAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>Remember the model/reasoning selected for a session (PLAN §14).</summary>
    ValueTask SetModelAsync(string sessionId, string? modelId, string? reasoningLevel,
        CancellationToken cancellationToken = default);

    /// <summary>Update the workspace path of a session (Settings overlay, PLAN §43).</summary>
    ValueTask SetWorkspaceAsync(string sessionId, string? workspacePath,
        CancellationToken cancellationToken = default);

    /// <summary>Paginated read of entries in append order (oldest first).</summary>
    ValueTask<IReadOnlyList<SessionEntry>> ReadAsync(
        string sessionId,
        int offset,
        int count,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// PLAN §38: entries OLDER than sequence <paramref name="beforeSequence"/>
    /// (scrolling upward), returned in append order (oldest first).
    /// </summary>
    ValueTask<IReadOnlyList<SessionEntry>> ReadBeforeAsync(
        string sessionId,
        int beforeSequence,
        int count,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// astra-1 A (Package A): the LATEST <see cref="EntryKind.Compaction"/> checkpoint
    /// of the session — found by sequence alone, independent of history pagination
    /// (a safety limit on a ReadAsync window must never substitute old history for
    /// the current one). Default: null (stores without checkpoint queries simply
    /// never report a checkpoint).
    /// </summary>
    ValueTask<SessionEntry?> LatestCompactionAsync(string sessionId, CancellationToken cancellationToken = default)
        => ValueTask.FromResult<SessionEntry?>(null);

    /// <summary>
    /// astra-1 A (Package A): entries with sequence &gt; <paramref name="afterSequence"/>
    /// in append order (oldest first), up to <paramref name="count"/> — explicit
    /// forward paging from a checkpoint, instead of a fixed oldest-first window.
    /// Default: empty (unsupported).
    /// </summary>
    ValueTask<IReadOnlyList<SessionEntry>> ReadAfterAsync(
        string sessionId, int afterSequence, int count, CancellationToken cancellationToken = default)
        => ValueTask.FromResult<IReadOnlyList<SessionEntry>>(Array.Empty<SessionEntry>());

    /// <summary>
    /// astra-1 A (Package A): the newest <paramref name="count"/> entries in append
    /// order (oldest first) — the bounded fallback when no checkpoint exists.
    /// Default: the last page of the oldest-first read (correct but O(history));
    /// stores should implement it natively.
    /// </summary>
    ValueTask<IReadOnlyList<SessionEntry>> ReadRecentAsync(
        string sessionId, int count, CancellationToken cancellationToken = default);
}
