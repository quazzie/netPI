using System.Text.Json.Nodes;

namespace NetPI;

public sealed class DecisionRequest
{
    public string? Model { get; init; }
    public required JsonObject Body { get; init; }
    public bool Conversation { get; init; }
    public int Priority { get; init; }
    /// <summary>Only trusted in-process callers can supply a live lease; RPC callers cannot claim one.</summary>
    public IAgentSlot? ExistingLease { get; init; }
    public string? HeldModel { get; init; }
    public string? ReasoningEffort { get; init; }
}

public interface IDecisionService
{
    Task<JsonObject> EvaluateAsync(DecisionRequest request, CancellationToken ct);
}

public interface IGitHistory
{
    Task<JsonObject?> ReadAsync(JsonObject request, CancellationToken ct);
}

/// <summary>Optional typed capabilities with RPC compatibility for alternate/older implementations.</summary>
public static class DecisionCapabilities
{
    public static bool Available(IServiceRegistry services, IRpcRegistry rpc, string method) => services.Get<IDecisionService>() is not null || rpc.Exists(method);
    public static async Task<JsonObject?> InvokeAsync(IServiceRegistry services, IRpcRegistry rpc, string method, JsonObject body,
        CancellationToken ct, IAgentSlot? held = null, string? heldModel = null, int priority = 0)
    {
        if (services.Get<IDecisionService>() is { } service)
            return await service.EvaluateAsync(new DecisionRequest
            {
                Model = body["model"]?.GetValue<string>(), Body = (JsonObject)body.DeepClone(), Conversation = method == "decide.decision",
                ExistingLease = held, HeldModel = heldModel, Priority = priority, ReasoningEffort = body["reasoning_effort"]?.GetValue<string>(),
            }, ct).ConfigureAwait(false);
        return NetPiJson.ToNode(await rpc.InvokeAsync(method, body, ct).ConfigureAwait(false)) as JsonObject;
    }
}

public static class DecisionConfidence
{
    public static bool Probability(double p) => double.IsFinite(p) && p is >= 0 and <= 1;
    public static bool Clear(double winner, double runnerUp, double threshold, double margin = 0.15) =>
        Probability(winner) && Probability(runnerUp) && winner >= threshold && winner - runnerUp >= margin;
    public static bool Yes(double p, double threshold = 0.8, double margin = 0.15) => Clear(p, 1 - p, threshold, margin);
}
