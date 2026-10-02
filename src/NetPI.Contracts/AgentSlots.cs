namespace NetPI;

/// <summary>
/// The agents' slots: every agent (<c>agents.&lt;id&gt;</c>, a model with instances) has as many slots as instances, and
/// model calls without an agent get slots per model. A run holds a slot for its whole run; when all are taken, runs
/// queue. A waiting orchestrator can yield its slot to its workers and resume later with only their results.
/// </summary>
public interface IAgentScheduler
{
    /// <summary>The slot key of a model call without an agent (a slot per model).</summary>
    string Resolve(ModelInfo model);

    /// <summary>The slot key of a run on an agent (<c>agents.&lt;id&gt;</c>); null or an unknown id: as <see cref="Resolve(ModelInfo)"/>.</summary>
    string Resolve(ModelInfo model, string? agent) => Resolve(model);

    /// <summary>
    /// The agents a run may go to, best first: those that run <paramref name="model"/> (every agent when it is null, the
    /// "any available agent" choice), the ones that can take work only, a free one before a busy one, then
    /// <paramref name="preferred"/> (the agent it last ran on), then the lightest load. A run does not pick one of them
    /// up front: it passes the whole list as <see cref="AgentSlotRequest.Candidates"/> and the first agent with a free
    /// instance takes it. Empty when no agents are set up (every model then runs on a slot per model). Throws
    /// <see cref="CallRefusedException"/> (<c>Kind = "unavailable"</c>) when agents are set up and none runs the model, or (model
    /// null) none can take work. When agents run the model but none can take work, the list is the best of them: acquiring
    /// it reports why.
    /// </summary>
    IReadOnlyList<string> Candidates(ModelInfo? model, string? preferred) => [];

    /// <summary>The model ref an agent runs, null for an id that is not an agent.</summary>
    string? ModelOf(string agent) => null;

    /// <summary>Runs waiting for any of several agents (<see cref="AgentSlotRequest.Candidates"/>): they belong to no agent's queue yet.</summary>
    IReadOnlyList<SlotHolder> Unassigned() => [];

    IReadOnlyList<AgentSlots> Snapshot();
    /// <summary>Wait for a slot (FIFO within priority). Throws <see cref="CallRefusedException"/> when the run is refused (the provider is over a limit, or the agent is not available).</summary>
    ValueTask<IAgentSlot> AcquireAsync(AgentSlotRequest request, CancellationToken ct);
    bool TryAcquire(AgentSlotRequest request, out IAgentSlot? lease);
    IReadOnlyList<ModelResourceSlots> Resources() => [];
}

public sealed class AgentSlotRequest
{
    /// <summary>The slot key (the agent) the run waits on; with <see cref="Candidates"/> the preferred one, and in the granted slot the one that took it.</summary>
    public required string Key { get; init; }
    /// <summary>
    /// The agents this run may go to. With two or more it waits unassigned, in nobody's queue, and the first of them
    /// with a free instance takes it (priority, then arrival, as for any waiter): a run does not choose an agent up
    /// front, so one that frees up first is not passed over. <see cref="IAgentSlot.Key"/> of the slot says which.
    /// </summary>
    public IReadOnlyList<string>? Candidates { get; init; }
    public required string AgentId { get; init; }
    public string? SessionId { get; init; }
    public string? Label { get; init; }
    /// <summary>Higher runs first. Agents resuming after a yield use a high priority.</summary>
    public int Priority { get; init; }
    public string? Provider { get; init; }
    public string? ExecutorGeneration { get; init; }
}

public interface IAgentSlot : IDisposable
{
    string? LeaseId => null;
    string Key { get; }
    string AgentId { get; }
    DateTimeOffset AcquiredAt { get; }
    bool IsReleased { get; }
}

public sealed class SlotHolder
{
    public string? LeaseId { get; set; }
    public string? ExecutorGeneration { get; set; }
    public string? CorrelationId { get; set; }
    public string? Purpose { get; set; }
    public bool Retiring { get; set; }
    public DateTimeOffset? CancellationRequestedAt { get; set; }
    public DateTimeOffset? ProviderReturnedAt { get; set; }
    public int Priority { get; set; }
    public string? WaitingFor { get; set; }
    public string AgentId { get; set; } = "";
    public string? SessionId { get; set; }
    public string? Label { get; set; }
    public DateTimeOffset Since { get; set; }
}

public sealed class AgentSlots
{
    public string? Resource { get; set; }
    public int? ResourceCapacity { get; set; }
    public int? ResourceBusy { get; set; }
    public int? ResourceQueued { get; set; }
    public string Key { get; set; } = "";
    public string? Provider { get; set; }
    public int Capacity { get; set; }
    public int Busy { get; set; }
    public int Queued { get; set; }
    public List<string> Models { get; set; } = [];
    public List<SlotHolder> Owners { get; set; } = [];
    public List<SlotHolder> Waiters { get; set; } = [];
    /// <summary>catalog | settings | default</summary>
    public string Source { get; set; } = "default";
    public string? Status { get; set; }
    /// <summary>An agent the user set up (<c>agents.&lt;id&gt;</c>, the key is its id); chats and subagents run on these.</summary>
    public bool Configured { get; set; }
    /// <summary>The agent can take work now: not disabled, and its model loaded (local) or reachable (cloud).</summary>
    public bool Available { get; set; } = true;
    /// <summary>Why the agent can't take work ("qwen3.8-27b isn't loaded", "disabled").</summary>
    public string? Unavailable { get; set; }
    /// <summary>Switched off by the user (<c>agents.&lt;id&gt;.disabled</c>).</summary>
    public bool Disabled { get; set; }
    /// <summary>The agent's model ref.</summary>
    public string? Model { get; set; }
    /// <summary>The user's note on when to use the agent.</summary>
    public string? Use { get; set; }
    /// <summary>Price in USD per million input / output tokens; null = unknown.</summary>
    public double? PriceInput { get; set; }
    public double? PriceOutput { get; set; }
    /// <summary>settings | catalog | local | reported (the provider reported $0) | unknown</summary>
    public string? PriceSource { get; set; }
    /// <summary>Costs nothing (local, or a price of 0).</summary>
    public bool Free { get; set; }
    public double SpentTodayUsd { get; set; }
    /// <summary>The agent's own daily cap (<c>agents.&lt;id&gt;.budget.limitUsd</c>).</summary>
    public double? DailyLimitUsd { get; set; }
}

/// <summary>
/// The agent (<c>agents.&lt;id&gt;</c>) a session prefers: <c>meta.agent</c>. It is where the chat last ran, shown in the
/// picker, not a reservation: a run goes to whichever agent on the chat's model has a free instance first. The value
/// <see cref="Any"/> lifts the model too: a run of that chat goes to any agent that can take work, and the chat's
/// model follows the agent it got.
/// </summary>
public static class SessionAgent
{
    public const string MetaKey = "agent";
    /// <summary>The agent the latest run of the chat holds (<c>meta.agentRun</c>); what its calls are billed to. Run state: a fork starts without it.</summary>
    public const string RunKey = "agentRun";
    /// <summary>The "any available agent" choice, in place of an agent id (so no agent can be called this).</summary>
    public const string Any = "any";

    public static string? Of(SessionInfo? session) => Text(session, MetaKey);

    public static bool IsAny(SessionInfo? session) => string.Equals(Of(session), Any, StringComparison.OrdinalIgnoreCase);

    /// <summary>The agent the chat's latest run holds, else the one it prefers (null for "any" with no run yet).</summary>
    public static string? Running(SessionInfo? session) => Text(session, RunKey) ?? (IsAny(session) ? null : Of(session));

    private static string? Text(SessionInfo? session, string key) =>
        session?.Meta?[key] is System.Text.Json.Nodes.JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;
}
