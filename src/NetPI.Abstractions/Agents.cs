namespace NetPI;

public enum AgentStatus
{
    /// <summary>Waiting for input (main chat agents between runs).</summary>
    Idle,
    /// <summary>Waiting for a free slot on its agent.</summary>
    Queued,
    Running,
    /// <summary>Gave its slot away while waiting for other agents.</summary>
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
    /// <summary>The agent (<c>agents.&lt;id&gt;</c>) it runs on, or the model's slot key when no agents are set up.</summary>
    public string? Agent { get; set; }
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
    /// <summary>"provider/model" (without agents set up). Null = parent's model.</summary>
    public string? Model { get; init; }
    public string? Reasoning { get; init; }
    public string? ParentAgentId { get; init; }
    /// <summary>Project for the subagent session (default: parent's project).</summary>
    public string? ProjectId { get; init; }
    /// <summary>Workspace for the subagent session: an existing workspace id/name, or a request for a fresh isolated one.
    /// Null = the parent's workspace (bound or not). Resolved and provisioned by the workspace plugin before the child runs;
    /// when no workspace plugin is loaded the value is ignored and the child's project behaves as it did before.</summary>
    public string? WorkspaceId { get; init; }
    /// <summary>Give the subagent its own worktree and branch (a writing worker), instead of sharing the caller's checkout.</summary>
    public bool Isolated { get; init; }
    /// <summary>Name for a provisioned workspace (default: the subagent's name).</summary>
    public string? WorkspaceName { get; init; }
    /// <summary>Session that owns a provisioned workspace (default: the subagent's own session). Ownership belongs to the
    /// worker, so a worker that is handed a second task keeps the checkout it already has.</summary>
    public string? WorkspaceOwnerSessionId { get; init; }
    /// <summary>Branch or commit a provisioned workspace starts from (default: the project's current HEAD).</summary>
    public string? WorkspaceBase { get; init; }
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
    /// Wait for agents to finish their current run. When <paramref name="yieldSlot"/> is true and the caller
    /// holds a slot, the slot is released while waiting and re-acquired (with priority) afterwards.
    /// </summary>
    Task<IReadOnlyList<AgentInfo>> WaitAsync(string? callerAgentId, IReadOnlyList<string> agentIds, bool yieldSlot = true,
        TimeSpan? timeout = null, CancellationToken ct = default);

    /// <summary>
    /// Wait for <paramref name="until"/> the way <see cref="WaitAsync"/> waits for agents: the caller's slot is released
    /// while it waits and taken again (with priority) afterwards, and the agent shows as yielded with
    /// <paramref name="activity"/> ("waiting for your answer"). False when the wait ended without it: the user steered the
    /// caller (their message follows), or the timeout passed. This default waits without releasing anything.
    /// </summary>
    async Task<bool> WaitYieldedAsync(string? callerAgentId, Task until, string activity, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        try
        {
            await until.WaitAsync(timeout ?? Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException) { return false; }
    }

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
    public IAgentSlot? AdmissionLease { get; set; }
    public required AgentInfo Agent { get; init; }
    public required SessionInfo Session { get; set; }
    public ProjectInfo? Project { get; set; }
    /// <summary>The session's workspace (null when it is not bound to one). <see cref="Cwd"/> is this workspace's root when it
    /// is set, and the project's path otherwise — the two are resolved once, here, so every consumer of the run agrees.</summary>
    public WorkspaceBinding? Workspace { get; set; }
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
    /// <summary>Reason for an aborted run, including executor removal versus a user stop.</summary>
    public string? CancelReason { get; set; }
    public Exception? Error { get; set; }
}

/// <summary>State of one model call inside a run.</summary>
public sealed class AgentTurnContext
{
    public ModelRequest? SentRequest { get; set; }
    public ChatMessage? LatestAssistant { get; set; }
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
/// Watches every model call without deciding anything: metering, diagnostics. Register implementations with
/// <c>context.Services.Register&lt;IAgentCallObserver&gt;(...)</c>; they run in ascending <see cref="Order"/> after the
/// hooks, on the message the run goes on with.
/// <para>
/// An <see cref="IAgentHook"/> that only counts or records belongs here instead: hook dispatch ends at the first
/// non-null decision, so a hook placed after the first decision-maker (tool repair, nudge, loops) would never see the
/// calls that one decided, and a budget it keeps would undercount what the run really cost.
/// </para>
/// </summary>
public interface IAgentCallObserver
{
    int Order => 0;
    /// <summary>After each model call, whatever the hooks decided (the message is already persisted).</summary>
    ValueTask OnAfterModelCallAsync(AgentTurnContext turn, ChatMessage assistant) => ValueTask.CompletedTask;
}

/// <summary>
/// Agent lifecycle hooks (pi-style extension points). Register implementations with
/// <c>context.Services.Register&lt;IAgentHook&gt;(...)</c>. Hooks run in ascending <see cref="Order"/>.
/// For decision hooks the first non-null decision wins, except <see cref="OnBeforeToolCallAsync"/>: every hook sees the
/// call, with the arguments the hooks before it changed, and a block ends it. Work that must happen whatever the hooks
/// decide belongs in an <see cref="IAgentCallObserver"/>.
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
