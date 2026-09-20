namespace NetPI.Abstractions;

/// <summary>
/// Normalized streaming events emitted by <see cref="IModelProvider"/> (PLAN §9).
/// The same events feed agent, storage, websocket, and UI — no raw provider
/// chunks ever cross the boundary.
/// </summary>
public abstract record ModelEvent
{
    /// <summary>Kind discriminator for serialization (e.g. "text-delta").</summary>
    public abstract string Kind { get; }
}

/// <summary>A model run started. <paramref name="ModelId"/> may be the resolved concrete model.</summary>
public sealed record ModelStarted(string ModelId) : ModelEvent
{
    public override string Kind => "model-started";
}

/// <summary>Thinking/reasoning block started.</summary>
public sealed record ThinkingStarted(string BlockId) : ModelEvent
{
    public override string Kind => "thinking-started";
}

/// <summary>Incremental thinking text.</summary>
public sealed record ThinkingDelta(string Text) : ModelEvent
{
    public override string Kind => "thinking-delta";
}

/// <summary>Thinking block completed. <paramref name="Signature"/> is optional provider metadata.</summary>
public sealed record ThinkingCompleted(string BlockId, string? Signature = null) : ModelEvent
{
    public override string Kind => "thinking-completed";
}

/// <summary>Text block started.</summary>
public sealed record TextStarted(string BlockId) : ModelEvent
{
    public override string Kind => "text-started";
}

/// <summary>Incremental text.</summary>
public sealed record TextDelta(string Text) : ModelEvent
{
    public override string Kind => "text-delta";
}

/// <summary>Text block completed.</summary>
public sealed record TextCompleted(string BlockId) : ModelEvent
{
    public override string Kind => "text-completed";
}

/// <summary>The model issued a tool call. Arguments arrive as deltas afterwards.</summary>
public sealed record ToolCallStarted(string Id, string Name) : ModelEvent
{
    public override string Kind => "tool-call-started";
}

/// <summary>Fragment of the JSON arguments for a pending tool call.</summary>
public sealed record ToolCallArgumentsDelta(string Id, string Delta) : ModelEvent
{
    public override string Kind => "tool-call-arguments-delta";
}

/// <summary>A tool call's arguments are complete.</summary>
public sealed record ToolCallCompleted(string Id, string Name) : ModelEvent
{
    public override string Kind => "tool-call-completed";
}

/// <summary>Token usage update (cumulative or per-step; provider-defined).</summary>
public sealed record UsageUpdated(int PromptTokens, int CompletionTokens, int TotalTokens, int CachedTokens = 0) : ModelEvent
{
    public override string Kind => "usage-updated";
}

/// <summary>The run finished normally; <paramref name="Message"/> is the complete assistant message.</summary>
public sealed record ModelCompleted(AgentMessage Message) : ModelEvent
{
    public override string Kind => "model-completed";
}

/// <summary>The run failed. Carries the error for the retry plugin / UI.</summary>
public sealed record ModelFailed(string ModelId, string Error, Exception? Exception = null) : ModelEvent
{
    public override string Kind => "model-failed";
}

/// <summary>
/// Host-level diagnostic published by a model provider around one model request
/// (PLAN §47): which wire was configured, which was requested, which actually
/// served the run, whether it chained from a previous response, and any
/// failure/retry reason. Consumed by diagnostic plugins (e.g. netpi.diagnostics)
/// through the event bus — the provider's own decision record, never invented by
/// the agent layer.
/// </summary>
public sealed record ModelRequestDiagnostics(
    string? SessionId,
    string ModelId,
    string WireConfig,
    string WireRequested,
    string WireServed,
    bool Chained,
    bool Fallback,
    string? FailureReason)
{
    /// <summary>Single-line human-readable summary for logs and dashboards.</summary>
    public string Summary =>
        $"{ModelId} wire={WireServed}{(Chained ? " chained" : "")}" +
        (Fallback ? $" (fallback from {WireRequested}: {FailureReason ?? "unknown"})" : "");
}
