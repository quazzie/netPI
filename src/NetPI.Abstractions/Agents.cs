namespace NetPI;

public enum AgentStatus
{
    /// <summary>Waiting for input (main chat agents between runs).</summary>
    Idle,
    /// <summary>Waiting for a free lane.</summary>
    Queued,
    Running,
    /// <summary>Gave its lane away while waiting for other agents.</summary>
    Yielded,
    /// <summary>Subagent finished its task.</summary>
    Completed,
    Failed,
    Cancelled,
}

public sealed class AgentInfo
{
    public string Id { get; set; } = "";
    public string SessionId { get; set; } = "";
    public string Name { get; set; } = "";
    public string? ParentAgentId { get; set; }
    public string? ParentSessionId { get; set; }
    public bool IsSubagent { get; set; }
    public int Depth { get; set; }
    public AgentStatus Status { get; set; }
    public string? Model { get; set; }
    public string? Pool { get; set; }
    /// <summary>What it is doing right now: "thinking", "writing", "tool: bash", "waiting for 2 agents"...</summary>
    public string? Activity { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public int Runs { get; set; }
    public int Turns { get; set; }
    public int ToolCalls { get; set; }
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public int QueuedMessages { get; set; }
    public string? Task { get; set; }
    /// <summary>Final answer of a completed subagent (its last assistant text).</summary>
    public string? Result { get; set; }
    public string? Error { get; set; }
    public List<string>? ToolAllowlist { get; set; }
    public List<string> Children { get; set; } = [];
}

/// <summary>How a message is delivered to a running agent.</summary>
public enum DeliveryMode
{
    /// <summary>Steer when running, start a run when idle.</summary>
    Auto,
    /// <summary>Deliver at the next step of the current run (remaining tool calls of the batch are skipped).</summary>
    Steer,
    /// <summary>Deliver after the current run finishes (follow-up).</summary>
    Queue,
}

public sealed class SpawnRequest
{
    public required string Task { get; init; }
    public string? Name { get; init; }
    /// <summary>"provider/model" or a lane pool key. Null = parent's model.</summary>
    public string? Model { get; init; }
    public string? Reasoning { get; init; }
    public string? ParentAgentId { get; init; }
    /// <summary>Project for the subagent session (default: parent's project).</summary>
    public string? ProjectId { get; init; }
    /// <summary>The agent (<c>agents.&lt;id&gt;</c>) the subagent runs on; its model is <see cref="Model"/>.</summary>
    public string? Agent { get; init; }
    /// <summary>The subagent's tools, chosen by its owner (null = the owner's tools: its allowlist and the tools switched off for its session).</summary>
    public IReadOnlyList<string>? Tools { get; init; }
    /// <summary>Appended to the subagent system prompt.</summary>
    public string? Instructions { get; init; }
    /// <summary>When the subagent finishes and nobody is waiting on it, notify the parent (waking it if idle).</summary>
    public bool NotifyParent { get; init; } = true;
}

public sealed class QueuedInput
{
    public required string Id { get; init; }
    public required string Text { get; init; }
    public required string Mode { get; init; } // steer | queue
    public string Source { get; init; } = "user";
    public DateTimeOffset CreatedAt { get; init; }
}

public interface IAgentRuntime
{
    IReadOnlyList<AgentInfo> List(bool includeFinished = true);
    AgentInfo? Get(string agentId);
    AgentInfo? GetBySession(string sessionId);

    /// <summary>Send input to a session's agent (creating the agent if needed).</summary>
    Task<AgentInfo> SendAsync(string sessionId, UserInput input, DeliveryMode mode = DeliveryMode.Auto, CancellationToken ct = default);

    /// <summary>Create a subagent (own session) and start it on <see cref="SpawnRequest.Task"/>. Returns immediately.</summary>
    Task<AgentInfo> SpawnAsync(SpawnRequest request, CancellationToken ct = default);

    /// <summary>
    /// Wait for agents to finish their current run. When <paramref name="yieldLane"/> is true and the caller
    /// holds a lane, the lane is released while waiting and re-acquired (with priority) afterwards.
    /// </summary>
    Task<IReadOnlyList<AgentInfo>> WaitAsync(string? callerAgentId, IReadOnlyList<string> agentIds, bool yieldLane = true,
        TimeSpan? timeout = null, CancellationToken ct = default);

    /// <summary>Abort the current run of an agent (by agent id or session id).</summary>
    Task<bool> AbortAsync(string agentOrSessionId);

    /// <summary>Agent-to-agent messaging. Delivered as a notice to the target.</summary>
    Task<bool> MessageAsync(string fromAgentId, string toAgentId, string text, DeliveryMode mode = DeliveryMode.Auto, CancellationToken ct = default);

    IReadOnlyList<QueuedInput> GetQueue(string sessionId);
    bool RemoveQueued(string sessionId, string inputId);
}

// ---------------------------------------------------------------- hooks

/// <summary>State of one agent run (from input until the agent stops).</summary>
public sealed class AgentRunContext
{
    public required AgentInfo Agent { get; init; }
    public required SessionInfo Session { get; set; }
    public ProjectInfo? Project { get; set; }
    public required string Cwd { get; set; }
    public required ModelInfo Model { get; set; }
    public string? ReasoningEffort { get; set; }
    public required IServiceRegistry Services { get; init; }
    public required ISessionStore Sessions { get; init; }
    public required IModelCatalog Models { get; init; }
    public required IEventBus Events { get; init; }
    public CancellationToken CancellationToken { get; init; }
    /// <summary>Number of model calls in this run so far.</summary>
    public int TurnCount { get; set; }
    public int ToolCallCount { get; set; }
    /// <summary>Plugin scratch state for this run (e.g. nudge counters).</summary>
    public Dictionary<string, object?> Items { get; } = [];
    /// <summary>Set when the run ends: completed | aborted | failed.</summary>
    public string? Outcome { get; set; }
    public Exception? Error { get; set; }
}

/// <summary>State of one model call inside a run.</summary>
public sealed class AgentTurnContext
{
    public required AgentRunContext Run { get; init; }
    public int TurnIndex { get; init; }
    public required string SystemPrompt { get; set; }
    /// <summary>Messages sent to the model (mutable before the call).</summary>
    public required List<ChatMessage> Messages { get; set; }
    public required IReadOnlyList<ToolDefinition> Tools { get; set; }
    /// <summary>Reload <see cref="Messages"/> from the session store (e.g. after compaction).</summary>
    public required Func<Task> ReloadMessagesAsync { get; init; }
    /// <summary>Last known context size (tokens) from the previous call's usage, 0 if unknown.</summary>
    public long LastContextTokens { get; set; }
}

public enum TurnAction { Continue, Inject, Replace, Stop }

public sealed class TurnDecision
{
    public TurnAction Action { get; init; }
    /// <summary>For Inject: text of a notice appended before the next model call.</summary>
    public string? Text { get; init; }
    public string? NoticeKind { get; init; }
    /// <summary>For Replace: the corrected assistant message (e.g. textual tool calls parsed).</summary>
    public ChatMessage? Replacement { get; init; }

    public static TurnDecision Inject(string text, string kind) => new() { Action = TurnAction.Inject, Text = text, NoticeKind = kind };
    public static TurnDecision Replace(ChatMessage message) => new() { Action = TurnAction.Replace, Replacement = message };
    public static TurnDecision Stop() => new() { Action = TurnAction.Stop };
}

public sealed class ToolCallDecision
{
    public bool Block { get; init; }
    public string? Reason { get; init; }
    /// <summary>Replace the arguments JSON before execution.</summary>
    public string? Arguments { get; init; }
}

public sealed class ModelErrorDecision
{
    /// <summary>Retry the model call (e.g. after compaction freed context).</summary>
    public bool Retry { get; init; }
}

/// <summary>
/// Agent lifecycle hooks (pi-style extension points). Register implementations with
/// <c>context.Services.Register&lt;IAgentHook&gt;(...)</c>. Hooks run in ascending <see cref="Order"/>.
/// For decision hooks the first non-null decision wins.
/// </summary>
public interface IAgentHook
{
    int Order => 0;
    ValueTask OnRunStartAsync(AgentRunContext run) => ValueTask.CompletedTask;
    /// <summary>Before each model call. May modify messages/system prompt, compact, etc.</summary>
    ValueTask OnBeforeModelCallAsync(AgentTurnContext turn) => ValueTask.CompletedTask;
    /// <summary>After each model call (message already persisted). Return a decision to continue/inject/replace.</summary>
    ValueTask<TurnDecision?> OnAfterModelCallAsync(AgentTurnContext turn, ChatMessage assistant) => ValueTask.FromResult<TurnDecision?>(null);
    /// <summary>A model call failed after middleware retries.</summary>
    ValueTask<ModelErrorDecision?> OnModelErrorAsync(AgentTurnContext turn, Exception error) => ValueTask.FromResult<ModelErrorDecision?>(null);
    ValueTask<ToolCallDecision?> OnBeforeToolCallAsync(AgentTurnContext turn, ToolCallPart call) => ValueTask.FromResult<ToolCallDecision?>(null);
    ValueTask OnAfterToolCallAsync(AgentTurnContext turn, ToolCallPart call, ToolResultPart result) => ValueTask.CompletedTask;
    ValueTask OnRunEndAsync(AgentRunContext run) => ValueTask.CompletedTask;
}
