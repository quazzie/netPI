using System.Text.Json.Nodes;

namespace NetPI;

public enum AgentStatus
{
    /// <summary>Waiting for input (main chat agents between runs).</summary>
    Idle,
    /// <summary>Waiting for a free slot on its agent.</summary>
    Queued,
    Running,
    /// <summary>
    /// Gave its slot away while it waits: for other agents (a run that goes on), or for the user (<see cref="UserWait"/>:
    /// the run ended at the call that waits, and the chat goes on when they answer).
    /// </summary>
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
    /// <summary>
    /// A finished subagent whose report the parent has not seen yet (not consumed by a wait, not in the parent's
    /// transcript, and not already returned by a wait): an agent wait without ids returns it.
    /// </summary>
    public bool UnseenReport { get; set; }
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
    /// <summary>What the spawner wants beyond the core's own concepts (a workspace for the child, say), by type; the part that knows the type acts on it before the child runs.</summary>
    public FeatureSet Features { get; init; } = new();
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
    /// holds a slot, the slot is released while waiting and re-acquired (with priority) afterwards. With
    /// <paramref name="any"/> the wait ends as soon as the first of the agents finishes (the others keep running),
    /// instead of when all have.
    /// </summary>
    Task<IReadOnlyList<AgentInfo>> WaitAsync(string? callerAgentId, IReadOnlyList<string> agentIds, bool yieldSlot = true,
        TimeSpan? timeout = null, CancellationToken ct = default, bool any = false);

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

    /// <summary>The calls that wait for the user (<see cref="UserWait"/>), oldest first: of one chat, of one kind, or all.</summary>
    IReadOnlyList<WaitingCall> Waits(string? sessionId = null, string? kind = null) => [];

    /// <summary>
    /// The user answered the wait <paramref name="waitId"/> (<see cref="UserWait.Id"/>). False when nothing waits under that
    /// id any more: answered already, the user wrote instead, the chat was stopped or deleted. When every wait of the
    /// chat's tool batch is answered the chat goes on: a run finishes the batch (the answers as the calls' results, the
    /// calls allowed to run, the calls after them) and calls the model.
    /// </summary>
    Task<bool> ResolveAsync(string waitId, WaitResolution resolution, CancellationToken ct = default) => Task.FromResult(false);

    /// <summary>Abort the current run of an agent (by agent id or session id).</summary>
    Task<bool> AbortAsync(string agentOrSessionId);

    /// <summary>Agent-to-agent messaging. Delivered as a notice to the target.</summary>
    Task<bool> MessageAsync(string fromAgentId, string toAgentId, string text, DeliveryMode mode = DeliveryMode.Auto, CancellationToken ct = default);

    IReadOnlyList<QueuedInput> GetQueue(string sessionId);
    bool RemoveQueued(string sessionId, string inputId);
}

// ---------------------------------------------------------------- waiting for the user

/// <summary>
/// A tool call that waits for the user: a question (<c>ask_user</c>), an OK before a call runs (a guardrail), a decision (a
/// plan, a schedule). A tool returns it in <see cref="ToolResult.Wait"/>, a hook that asks before the call runs in
/// <see cref="ToolCallDecision.Wait"/>. It ends the run at that call: the runtime keeps it with the agent, which shows as
/// <see cref="AgentStatus.Yielded"/> with <see cref="Activity"/> and holds no instance and no reload, and it survives a
/// reload of any plugin and a restart. The call has no result until it ends: the owner answers it
/// (<see cref="IAgentRuntime.ResolveAsync"/>); a message of the user's ends it with <see cref="IfSteered"/>; stopping the
/// chat with <see cref="IfCancelled"/>. Anything else for the chat (a subagent's report, a notice) waits until then. The
/// owner keeps no state of its own: its list of what waits is <see cref="IAgentRuntime.Waits"/>, and
/// <c>agent.waitEnded</c> says when one stopped waiting. A subagent cannot wait: nobody watches its chat.
/// </summary>
public sealed class UserWait
{
    /// <summary>Its own id (the card's), unique: never the model's call id, which two chats can share.</summary>
    public required string Id { get; init; }
    /// <summary>The owner's name for the kind of wait (ask, guard, plan, plan-enter, schedule): its list, its card.</summary>
    public required string Kind { get; init; }
    /// <summary>What the agent shows while it waits: "waiting for your answer".</summary>
    public required string Activity { get; init; }
    /// <summary>The owner's data for its card (the questions, the rule, the schedule), stored with the wait.</summary>
    public JsonObject? Data { get; init; }
    /// <summary>The call's result when the user writes a new message instead of answering (the message follows it).</summary>
    public required ToolResult IfSteered { get; init; }
    /// <summary>The call's result when the chat is stopped before an answer (null: the runtime's abort marker).</summary>
    public ToolResult? IfCancelled { get; init; }
}

/// <summary>How the user answered a <see cref="UserWait"/>.</summary>
public sealed class WaitResolution
{
    /// <summary>The call's result: the question answered, the plan decided.</summary>
    public ToolResult? Result { get; init; }
    /// <summary>
    /// The call runs after all: a hook asked before it ran (a guardrail), and the user allowed it. The call goes through
    /// the hooks again, and <see cref="AgentTurnContext.AllowedCalls"/> tells the one that asked.
    /// </summary>
    public bool Run { get; init; }

    public static WaitResolution Answer(ToolResult result) => new() { Result = result };
    public static WaitResolution Allow() => new() { Run = true };
}

/// <summary>A call that waits for the user, as <see cref="IAgentRuntime.Waits"/> lists it.</summary>
public sealed class WaitingCall
{
    /// <summary>The wait's own id (<see cref="UserWait.Id"/>).</summary>
    public required string Id { get; init; }
    public required string Kind { get; init; }
    public required string SessionId { get; init; }
    public required string AgentId { get; init; }
    /// <summary>The model's id of the call that waits.</summary>
    public required string CallId { get; init; }
    /// <summary>The tool that was called.</summary>
    public required string Tool { get; init; }
    public string? Activity { get; init; }
    public JsonObject? Data { get; init; }
    public DateTimeOffset Since { get; init; }
    /// <summary>Answered already, while other calls of the same batch still wait.</summary>
    public bool Answered { get; init; }
}

// ---------------------------------------------------------------- hooks

/// <summary>State of one agent run (from input until the agent stops).</summary>
public sealed class AgentRunContext
{
    /// <summary>What the loop knows beyond the core's own concepts (the checkout the session works in, the slot the run holds): ask by type.</summary>
    public FeatureSet Features { get; } = new();
    public required AgentInfo Agent { get; init; }
    public required SessionInfo Session { get; set; }
    public ProjectInfo? Project { get; set; }
    /// <summary>The folder the run works in, resolved once so every consumer of the run agrees.</summary>
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
    /// <summary>Set when the run ends: completed | aborted | failed | waiting (a call waits for the user, <see cref="UserWait"/>: the chat goes on when they answer).</summary>
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
    /// <summary>
    /// The tools the agent may call that are deferred, not declared in <see cref="Tools"/>: a call by name runs one (and
    /// loads it). What a hook keys on "the agent can use tool X" checks both lists; "the request declares X" only Tools.
    /// </summary>
    public IReadOnlyList<ToolDefinition> DeferredTools { get; set; } = [];
    /// <summary>Reload <see cref="Messages"/> from the session store (e.g. after compaction).</summary>
    public required Func<Task> ReloadMessagesAsync { get; init; }
    /// <summary>Last known context size (tokens) from the previous call's usage, 0 if unknown.</summary>
    public long LastContextTokens { get; set; }
    /// <summary>
    /// The calls of this batch the user allowed to run after a hook asked (<see cref="ToolCallDecision.Wait"/>,
    /// <see cref="WaitResolution.Allow"/>): call id → the wait's <see cref="UserWait.Kind"/>. The hook that asked lets them
    /// through. Empty except in the run that goes on after the answer.
    /// </summary>
    public IReadOnlyDictionary<string, string> AllowedCalls { get; init; } = new Dictionary<string, string>();
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
    /// <summary>
    /// The call waits for the user before it runs (a guardrail's ask rule): it does not run, and the run ends at it
    /// (<see cref="UserWait"/>). Allowed (<see cref="WaitResolution.Allow"/>), it runs in the run that goes on, through the
    /// hooks again, with <see cref="AgentTurnContext.AllowedCalls"/> naming it.
    /// </summary>
    public UserWait? Wait { get; init; }
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
