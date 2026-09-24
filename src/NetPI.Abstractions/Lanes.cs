namespace NetPI;

/// <summary>
/// Lanes: every model belongs to a pool with a fixed number of lanes (parallel slots). An agent holds
/// a lane for the duration of a run; when all lanes are taken, agents queue. A waiting orchestrator can
/// yield its lane to its workers and resume later with only their results.
/// </summary>
public interface ILaneScheduler
{
    /// <summary>Pool key for a model (configured pool or "provider/model").</summary>
    string ResolvePool(ModelInfo model);
    IReadOnlyList<LanePoolInfo> Snapshot();
    /// <summary>Wait for a lane (FIFO within priority). Throws <see cref="BudgetExceededException"/> if the provider is over budget.</summary>
    ValueTask<ILaneLease> AcquireAsync(LaneRequest request, CancellationToken ct);
    bool TryAcquire(LaneRequest request, out ILaneLease? lease);
}

public sealed class LaneRequest
{
    public required string PoolKey { get; init; }
    public required string AgentId { get; init; }
    public string? SessionId { get; init; }
    public string? Label { get; init; }
    /// <summary>Higher runs first. Agents resuming after a yield use a high priority.</summary>
    public int Priority { get; init; }
    public string? Provider { get; init; }
}

public interface ILaneLease : IDisposable
{
    string PoolKey { get; }
    string AgentId { get; }
    DateTimeOffset AcquiredAt { get; }
    bool IsReleased { get; }
}

public sealed class LaneOwnerInfo
{
    public string AgentId { get; set; } = "";
    public string? SessionId { get; set; }
    public string? Label { get; set; }
    public DateTimeOffset Since { get; set; }
}

public sealed class LanePoolInfo
{
    public string Key { get; set; } = "";
    public string? Provider { get; set; }
    public int Capacity { get; set; }
    public int Busy { get; set; }
    public int Queued { get; set; }
    public List<string> Models { get; set; } = [];
    public List<LaneOwnerInfo> Owners { get; set; } = [];
    public List<LaneOwnerInfo> Waiters { get; set; } = [];
    /// <summary>catalog | settings | default</summary>
    public string Source { get; set; } = "default";
    public string? Status { get; set; }
    /// <summary>A lane the user set up for one model (<c>lanes.&lt;id&gt;</c>); agents choose among these.</summary>
    public bool Configured { get; set; }
    /// <summary>The configured lane's model ref.</summary>
    public string? Model { get; set; }
    /// <summary>The user's note on when to use the lane.</summary>
    public string? Use { get; set; }
    /// <summary>Price in USD per million input / output tokens; null = unknown.</summary>
    public double? PriceInput { get; set; }
    public double? PriceOutput { get; set; }
    /// <summary>settings | catalog | local | reported (the provider reported $0) | unknown</summary>
    public string? PriceSource { get; set; }
    /// <summary>Costs nothing (local, or a price of 0).</summary>
    public bool Free { get; set; }
    public double SpentTodayUsd { get; set; }
    /// <summary>The lane's own daily cap (<c>lanes.&lt;id&gt;.budget.limitUsd</c>).</summary>
    public double? DailyLimitUsd { get; set; }
}

public sealed class BudgetExceededException(string message) : Exception(message)
{
    /// <summary>budget.onLimit "ask" and a chat of the user: the user may let this chat go over (<c>budget.allow</c>).</summary>
    public bool CanOverride { get; init; }
}
