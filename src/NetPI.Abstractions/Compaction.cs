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

    /// <summary>
    /// PLAN §32: prompt tokens of the LAST model request (provider-reported,
    /// authoritative). 0 = unknown (no model call happened yet), in which case
    /// the compaction service estimates the whole context from scratch.
    /// </summary>
    public int LastPromptTokens { get; init; }

    /// <summary>PLAN §32: message count of the transcript sent in the last
    /// model request — everything before it is covered by LastPromptTokens.</summary>
    public int LastUsageMessageCount { get; init; }
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
/// astra-1 G2 (F contract): the compaction policy the context meter surfaces.
/// <c>ReserveTokens</c> is the headroom the service keeps (the trigger for a
/// model with window <c>W</c> is <c>W − ReserveTokens</c>). <c>Available</c>
/// mirrors <see cref="IsAvailable"/> (the plugin is enabled). <c>null</c> from an
/// <see cref="ICompaction"/> implementation that does not publish a policy —
/// the Web surface then shows the threshold as "not reported" (honest,
/// never a fabricated zero).
/// </summary>
public sealed record CompactionPolicy(bool Available, int ReserveTokens);

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
    /// astra-1 G2 (F contract): the compaction policy the context meter surfaces
    /// (availability + the reserve the trigger is computed from). The default is
    /// null — a pre-G2 implementation that never published a policy — so adding
    /// this breaks no existing implementor.
    /// </summary>
    CompactionPolicy? ContextPolicy => null;

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


/// <summary>
/// A slash command the user can invoke from the composer (PLAN §37).
/// Registered by plugins via <see cref="ICommandRegistry"/>.
/// </summary>
public sealed record CommandDefinition(
    string Name,          // e.g. "/compact" (with leading slash)
    string Description,    // e.g. "Compact context now"
    string? Requires = null) // optional required argument name, e.g. "pluginId"
{ }

/// <summary>
/// Global registry for slash commands (PLAN §37). Plugins register commands
/// at load time; commands are hot-reloadable because they live in the
/// plugin generation's service scope.
/// </summary>
public interface ICommandRegistry
{
    /// <summary>Register a command. Returns a disposer that unregisters on release.</summary>
    IDisposable Register(CommandDefinition command);

    /// <summary>Snapshot of all currently registered commands.</summary>
    IReadOnlyList<CommandDefinition> All();

    /// <summary>Find by name (case-insensitive), or null.</summary>
    CommandDefinition? Find(string name);
}
