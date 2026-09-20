namespace NetPI.Abstractions;

/// <summary>
/// Request for one compaction pass (PLAN §32/§33). The AutoCompact plugin
/// performs the summary; the payload of the resulting
/// <see cref="EntryKind.Compaction"/> entry carries the fields of
/// <see cref="CompactionEntryPayload"/> so the UI and context builders can
/// reconstruct active context without re-reading raw history.
/// </summary>
public sealed record CompactionRequest
{
    public required string SessionId { get; init; }
    /// <summary>Model used to produce the summary (tools disabled).</summary>
    public required string ModelId { get; init; }
    /// <summary>Approximate token budget to retain from the recent tail (PLAN §32).
    /// 0 means "use the compaction service's configured keep-recent budget".</summary>
    public int KeepRecentTokens { get; init; }

    /// <summary>Optional reasoning level for the summarization call.</summary>
    public string? ReasoningLevel { get; init; }
}

/// <summary>
/// Wire/persistence shape of a compaction entry (PLAN §31):
/// <c>summary</c>, <c>summarizedThroughSequence</c>, <c>retainedFromSequence</c>.
/// </summary>
public sealed record CompactionEntryPayload
{
    public string Summary { get; init; } = string.Empty;
    public int SummarizedThroughSequence { get; init; }
    public int RetainedFromSequence { get; init; }
    public int EstimatedTokensBefore { get; init; }
    public int EstimatedTokensAfter { get; init; }
    public string? ModelId { get; init; }
}

/// <summary>
/// The result of one compaction pass (PLAN §32/§33).
/// <paramref name="Payload"/> is null when no compaction was performed
/// (estimate below the trigger threshold or disabled); when set, it is the
/// persisted compaction entry.
/// <paramref name="ActiveContext"/> is the reconstructed transcript the model
/// should now see (the summary message plus the retained recent tail, oldest
/// first, system prompt excluded). It is null when no compaction occurred.
/// </summary>
public sealed record CompactionResult(
    CompactionEntryPayload? Payload,
    IReadOnlyList<AgentMessage>? ActiveContext)
{
    /// <summary>True when a compaction was actually performed.</summary>
    public bool Performed => ActiveContext is not null;
}

/// <summary>
/// The compaction service (PLAN §32/§33). Registered by the AutoCompact
/// plugin under id "compaction"; the agent runtime and Web surface resolve it
/// at run time so a missing plugin simply disables compaction (the run
/// continues with the raw context).
/// </summary>
public interface ICompaction
{
    /// <summary>True when the plugin is enabled and a summarization can run.</summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Run one compaction pass for the session: estimate context usage, and
    /// when the trigger fires, summarize (tools disabled), persist a
    /// compaction entry, and return the reconstructed active context. A no-op
    /// returns a result with <c>null</c> members.
    /// </summary>
    ValueTask<CompactionResult> CompactAsync(
        CompactionRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Rebuild the active model context for a fresh run (PLAN §31/§33): the
    /// most recent compaction summary plus the retained recent messages, or
    /// the full message history when no compaction has ever occurred. The
    /// current run's user message is expected to be already persisted.
    /// </summary>
    ValueTask<IReadOnlyList<AgentMessage>> BuildActiveContextAsync(
        string sessionId,
        CancellationToken cancellationToken = default);
}

