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
    /// The agent a run on <paramref name="model"/> goes to: <paramref name="agent"/> when it runs that model, else an agent on
    /// the model (a free one first). Null when no agents are set up (every model then runs on a slot per model). Throws
    /// <see cref="AgentUnavailableException"/> when agents are set up and none runs the model.
    /// </summary>
    string? ChooseAgent(ModelInfo model, string? agent) => null;
    IReadOnlyList<AgentSlots> Snapshot();
    /// <summary>Wait for a slot (FIFO within priority). Throws <see cref="BudgetExceededException"/> if the provider is over budget.</summary>
    ValueTask<IAgentSlot> AcquireAsync(AgentSlotRequest request, CancellationToken ct);
    bool TryAcquire(AgentSlotRequest request, out IAgentSlot? lease);
    IReadOnlyList<ModelResourceSlots> Resources() => [];
}

public sealed class AgentSlotRequest
{
    public required string Key { get; init; }
    public required string AgentId { get; init; }
    public string? SessionId { get; init; }
    public string? Label { get; init; }
    /// <summary>Higher runs first. Agents resuming after a yield use a high priority.</summary>
    public int Priority { get; init; }
    public string? Provider { get; init; }
}

public interface IAgentSlot : IDisposable
{
    string Key { get; }
    string AgentId { get; }
    DateTimeOffset AcquiredAt { get; }
    bool IsReleased { get; }
}

public sealed class SlotHolder
{
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

/// <summary>A run asked for an agent that can't take work now (disabled, or its model isn't loaded).</summary>
public sealed class AgentUnavailableException(string message) : Exception(message);

/// <summary>The agent (<c>agents.&lt;id&gt;</c>) a session runs on: <c>meta.agent</c>.</summary>
public static class SessionAgent
{
    public const string MetaKey = "agent";

    public static string? Of(SessionInfo? session) =>
        session?.Meta?[MetaKey] is System.Text.Json.Nodes.JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;
}

public sealed class BudgetExceededException(string message) : Exception(message)
{
    /// <summary>budget.onLimit "ask" and a chat of the user: the user may let this chat go over (<c>budget.allow</c>).</summary>
    public bool CanOverride { get; init; }
}
