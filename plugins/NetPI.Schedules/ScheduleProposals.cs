using System.Text.Json.Nodes;

namespace NetPI.Schedules;

/// <summary>
/// The user's OK on a schedule an agent proposed (<c>schedules</c> add): the tool call is a wait for the user
/// (<see cref="UserWait"/>, kind <c>schedule</c>), like <c>ask_user</c> — the run ends at it and goes on with the decision.
/// The card in the chat is the <c>schedule.proposed</c> / <c>schedule.decided</c> events (unscoped, the session in their
/// data; relayed from the runtime's <c>agent.waiting</c> / <c>agent.waitEnded</c>) and the <c>schedules.decide</c> RPC
/// (allow enables the proposal, the user's own enable; discard deletes it). The schedule itself is persisted as a
/// proposal (paused, <c>proposedBy</c>), and the wait is the runtime's: this plugin keeps none, so a reload of it loses
/// none. A steer or a stop leaves the proposal in the Schedules tab.
/// </summary>
internal sealed class PendingProposals(IPluginContext ctx)
{
    public const string Kind = "schedule";

    private IEnumerable<WaitingCall> Waiting(string? sessionId) =>
        ctx.Services.Get<IAgentRuntime>()?.Waits(sessionId, Kind).Where(w => !w.Answered) ?? [];

    public JsonArray List(string? sessionId) => new([.. Waiting(sessionId).Select(w => (JsonNode)Json(NetPiJson.ToNode(w)!.AsObject()))]);

    /// <summary>
    /// The user's decision on the card: allow is the user's own enable of the proposal (it runs from now on), discard
    /// deletes it. Both go through the plugin's own RPCs, so the Schedules tab sees the same state a tab click would; then
    /// the waiting call goes on with it.
    /// </summary>
    public async Task<JsonObject> DecideAsync(string id, string decision, CancellationToken ct)
    {
        if (decision is not ("allow" or "discard")) throw new RpcException("bad_request", "decision is allow or discard");
        var w = Waiting(null).FirstOrDefault(x => x.Id == id)
                ?? throw new RpcException("not_found", "No schedule proposal waits with that id (it was decided, or its chat moved on).");
        var proposed = w.Data?["schedule"] as JsonObject ?? throw new RpcException("bad_request", "The proposal has no schedule.");
        var scheduleId = proposed["id"]?.GetValue<string>() ?? throw new RpcException("bad_request", "The proposal has no schedule id.");
        var project = (string?)w.Data?["project"];
        JsonObject answer;
        JsonObject? updated = null;
        if (decision == "allow")
            answer = updated = (JsonObject)(await ctx.Rpc.InvokeAsync("schedules.update", new JsonObject { ["id"] = scheduleId, ["enabled"] = true }, ct).ConfigureAwait(false))!;
        else
        {
            await ctx.Rpc.InvokeAsync("schedules.delete", new JsonObject { ["id"] = scheduleId }, ct).ConfigureAwait(false);
            answer = new JsonObject { ["id"] = scheduleId };
        }
        var runtime = ctx.Services.Get<IAgentRuntime>() ?? throw new RpcException("unavailable", "The agent runtime is not running.");
        await runtime.ResolveAsync(w.Id, WaitResolution.Answer(SchedulesTool.Decided(decision, proposed, updated, project)), ct).ConfigureAwait(false);
        Closed(w.Id, w.SessionId, w.CallId, decision == "allow" ? "allowed" : "discarded", decision);
        return answer;
    }

    /// <summary>The card closes: <paramref name="status"/> is allowed | discarded | steered | cancelled.</summary>
    public void Closed(string id, string sessionId, string? callId, string status, string? decision = null) =>
        ctx.Events.Publish("schedule.decided", new JsonObject
        {
            ["id"] = id,
            ["sessionId"] = sessionId,
            ["callId"] = callId,
            ["status"] = status,
            ["decision"] = decision,
        });

    /// <summary>A waiting proposal as the card, schedules.proposals and schedule.proposed show it, from the runtime's wait.</summary>
    internal static JsonObject Json(JsonObject wait) => new()
    {
        ["id"] = wait["id"]?.DeepClone(),
        ["sessionId"] = wait["sessionId"]?.DeepClone(),
        ["callId"] = wait["callId"]?.DeepClone(),
        ["agentId"] = wait["agentId"]?.DeepClone(),
        ["schedule"] = wait["data"]?["schedule"]?.DeepClone(),
        ["askedAt"] = wait["since"]?.DeepClone(),
    };
}
