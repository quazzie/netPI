using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NetPI.Schedules;

/// <summary>How a waiting schedule proposal ended: allowed (enabled, the updated schedule rides along) or discarded (deleted).</summary>
internal sealed record ProposalDecision(string Kind, JsonObject? Schedule = null)
{
    /// <summary>The plugin stopped while the proposal waited: the schedule stays a proposal in the Schedules tab.</summary>
    public static readonly ProposalDecision Withdrawn = new("withdrawn");
}

/// <summary>
/// The user's OK on a schedule an agent proposed (<c>schedules</c> add): the tool call waits for it with its instance
/// given back, like <c>ask_user</c>. The card in the chat is the <c>schedule.proposed</c> / <c>schedule.decided</c>
/// events (unscoped, the session in their data) and the <c>schedules.decide</c> RPC (allow enables the proposal, the
/// user's own enable; discard deletes it). The schedule itself is persisted as a proposal (paused, <c>proposedBy</c>);
/// only the wait is here, in memory — a restart or a stopped run leaves the proposal in the Schedules tab.
/// </summary>
internal sealed class PendingProposals(IPluginContext ctx)
{
    private readonly ConcurrentDictionary<string, Entry> _byId = new(StringComparer.Ordinal);

    internal sealed class Entry
    {
        public required string Id { get; init; }
        public required string SessionId { get; init; }
        public required string CallId { get; init; }
        public required string AgentId { get; init; }
        public required JsonObject Schedule { get; init; }
        public DateTimeOffset AskedAt { get; } = DateTimeOffset.UtcNow;
        public TaskCompletionSource<ProposalDecision> Decision { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public Entry Add(ToolContext context, JsonObject schedule)
    {
        var e = new Entry
        {
            Id = "schq_" + Ids.Short(12),
            SessionId = context.SessionId,
            CallId = context.CallId,
            AgentId = context.AgentId,
            Schedule = schedule,
        };
        _byId[e.Id] = e;
        ctx.Events.Publish("schedule.proposed", Json(e)); // unscoped: every window learns that a chat needs the user
        return e;
    }

    /// <summary>Stops the wait: <paramref name="status"/> is allowed | discarded | steered | withdrawn | cancelled.</summary>
    public void Close(Entry e, string status, string? decision = null)
    {
        if (!_byId.TryRemove(new KeyValuePair<string, Entry>(e.Id, e))) return;
        ctx.Events.Publish("schedule.decided", new JsonObject
        {
            ["id"] = e.Id,
            ["sessionId"] = e.SessionId,
            ["callId"] = e.CallId,
            ["status"] = status,
            ["decision"] = decision,
        });
    }

    public JsonArray List(string? sessionId) =>
        new([.. _byId.Values.Where(e => sessionId is null || e.SessionId == sessionId).OrderBy(e => e.AskedAt).Select(e => (JsonNode)Json(e))]);

    /// <summary>
    /// The user's decision on the card: allow is the user's own enable of the proposal (it runs from now on), discard
    /// deletes it. Both go through the plugin's own RPCs, so the Schedules tab sees the same state a tab click would.
    /// </summary>
    public async Task<JsonObject> DecideAsync(string id, string decision, CancellationToken ct)
    {
        if (!_byId.TryGetValue(id, out var e)) throw new RpcException("not_found", "No schedule proposal waits with that id (it was decided, or its run ended).");
        var scheduleId = e.Schedule["id"]?.GetValue<string>() ?? throw new RpcException("bad_request", "The proposal has no schedule id.");
        switch (decision)
        {
            case "allow":
                {
                    var updated = (JsonObject)(await ctx.Rpc.InvokeAsync("schedules.update", new JsonObject { ["id"] = scheduleId, ["enabled"] = true }, ct).ConfigureAwait(false))!;
                    if (!e.Decision.TrySetResult(new ProposalDecision("allow", updated))) throw new RpcException("not_found", "The proposal was already decided.");
                    Close(e, "allowed", "allow");
                    return updated;
                }
            case "discard":
                {
                    await ctx.Rpc.InvokeAsync("schedules.delete", new JsonObject { ["id"] = scheduleId }, ct).ConfigureAwait(false);
                    if (!e.Decision.TrySetResult(new ProposalDecision("discard"))) throw new RpcException("not_found", "The proposal was already decided.");
                    Close(e, "discarded", "discard");
                    return new JsonObject { ["id"] = scheduleId };
                }
            default:
                throw new RpcException("bad_request", "decision is allow or discard");
        }
    }

    /// <summary>The plugin goes: the waits end as withdrawn, so their runs go on (with their slot back) instead of hanging.</summary>
    public void WithdrawAll()
    {
        foreach (var e in _byId.Values)
        {
            e.Decision.TrySetResult(ProposalDecision.Withdrawn);
            Close(e, "withdrawn");
        }
    }

    private static JsonObject Json(Entry e) => new()
    {
        ["id"] = e.Id,
        ["sessionId"] = e.SessionId,
        ["callId"] = e.CallId,
        ["agentId"] = e.AgentId,
        ["schedule"] = e.Schedule.DeepClone(),
        ["askedAt"] = e.AskedAt.ToString("O"),
    };
}
