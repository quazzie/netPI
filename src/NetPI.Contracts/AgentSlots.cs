namespace NetPI;

/// <summary>
/// The agents' slots: every agent (<c>agents.&lt;id&gt;</c>, a model with instances) has as many slots as instances, and
/// model calls without an agent get slots per model. A run holds a slot for its whole run; when all are taken, runs
/// queue, by priority (<see cref="RunPriority"/>) and then arrival, until they get one or are cancelled. A waiting
/// orchestrator can yield its slot to its workers and resume later with only their results, and a critical run can take
/// the slot of a run below it that may be paused (<see cref="IAgentSlot.Wanted"/>).
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
    /// <summary>Wait for a slot (FIFO within priority), for as long as it takes. Throws <see cref="CallRefusedException"/> when the run is refused (the provider is over a limit, or the agent is not available).</summary>
    ValueTask<IAgentSlot> AcquireAsync(AgentSlotRequest request, CancellationToken ct);
    bool TryAcquire(AgentSlotRequest request, out IAgentSlot? lease);
    IReadOnlyList<ModelResourceSlots> Resources() => [];
    /// <summary>Re-rank queued runs and refresh preemption eligibility for a session whose priority changed.</summary>
    bool UpdatePriority(string sessionId, int priority, bool pausable);
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
    /// <summary>Higher runs first: the run's level (<see cref="RunPriority"/>), plus <see cref="RunPriority.Resume"/> when it queues again after giving its slot away.</summary>
    public int Priority { get; set; }
    /// <summary>
    /// The run may give its slot to a higher run (<see cref="RunPriority.CanPreempt"/>): system-started chats with a
    /// priority stamp may yield; chats the user prioritized in the composer remain protected, except background ones.
    /// </summary>
    public bool Pausable { get; set; }
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
    /// <summary>
    /// Cancelled when a critical run waits for a slot this one could give up (a <see cref="AgentSlotRequest.Pausable"/> run
    /// below it): the run gives it up at its next model call, or while a tool call of it has run for a minute. A fresh
    /// token once <see cref="GiveUpIfWanted"/> found that no one needs it any more.
    /// </summary>
    CancellationToken Wanted => CancellationToken.None;
    /// <summary>
    /// Releases the slot to the critical run that waits for it, if one still does, in one step with the scheduler (two
    /// runs never both give theirs up for one): true when it was released, and the run must queue for a slot again.
    /// </summary>
    bool GiveUpIfWanted() => false;
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
/// How urgent a chat's runs are: they queue for a slot by it, and a critical run takes the slot of a run below it that may
/// be paused. A chat's level is <c>meta.priority</c> (<c>background</c>, <c>low</c>, <c>normal</c>, <c>high</c> or <c>critical</c>), set by
/// what started the chat or by the user in the composer. System-started chats with a priority stamp may be paused;
/// manually started chats remain protected even though they now have a priority, except background chats, which always yield (to any higher-priority waiter).
/// </summary>
public static class RunPriority
{
    public const string MetaKey = "priority";
    public const int Background = -200;
    public const int Low = -100;
    public const int Normal = 0;
    public const int High = 100;
    public const int Critical = 200;
    /// <summary>
    /// Added when a run queues again after giving its slot away (a wait for its subagents, a pause for a critical run):
    /// first among the runs of its own level, still behind the levels above it.
    /// </summary>
    public const int Resume = 50;
    /// <summary>The levels, lowest first.</summary>
    public static readonly IReadOnlyList<string> Names = ["background", "low", "normal", "high", "critical"];

    /// <summary>The level a name stands for; null for anything else.</summary>
    public static int? Parse(string? name) => name?.Trim().ToLowerInvariant() switch
    {
        "background" => Background,
        "low" => Low,
        "normal" => Normal,
        "high" => High,
        "critical" => Critical,
        _ => null,
    };

    /// <summary>The level a priority is in (a resuming run's included).</summary>
    public static string Name(int priority) =>
        priority >= Critical ? "critical" : priority >= High ? "high" : priority >= Normal ? "normal" : priority >= Low ? "low" : "background";

    /// <summary>The level of a chat's runs: its <c>meta.priority</c>, normal without one.</summary>
    public static int Of(SessionInfo? session) => Parse(Text(session)) ?? Normal;

    /// <summary>A system-started chat carries a level and can be considered for pausing; manual chats are excluded by the runtime.</summary>
    public static bool Stamped(SessionInfo? session) => Parse(Text(session)) is not null;

    /// <summary>
    /// Whether a waiting run at <paramref name="waiter"/> may take the slot of a pausable run at <paramref name="holder"/>:
    /// a critical waiter takes it from any run below critical, and any waiter above the background level takes it from a
    /// background run (a resuming run counts by its level). A background waiter preempts nothing.
    /// </summary>
    public static bool CanPreempt(int waiter, int holder) =>
        holder < waiter && holder < Critical && (waiter >= Critical || IsBackground(holder) && !IsBackground(waiter));

    /// <summary>Whether a waiter could preempt some background run (cheap pre-filter for the scheduler).</summary>
    public static bool CanPreemptAny(int waiter) => waiter >= Critical || !IsBackground(waiter);

    /// <summary>In the background level, a resuming run included.</summary>
    public static bool IsBackground(int priority) => priority < Low;

    private static string? Text(SessionInfo? session) =>
        session?.Meta?[MetaKey] is System.Text.Json.Nodes.JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
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
