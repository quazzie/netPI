namespace NetPI.Runtime;

/// <summary>
/// The caps <c>agent_spawn</c> gives a subagent (<c>maxUsd</c>, <c>maxTurns</c>). They travel in <see cref="SpawnRequest.Features"/> as a
/// <see cref="System.Text.Json.Nodes.JsonObject"/> with these two fields (a plugin shares no type with the runtime), are kept on the child's
/// session meta under the same names, and are enforced where the work is done: <c>maxTurns</c> by the run loop (model calls per run),
/// <c>maxUsd</c> by the Agents ledger when a call is reserved (the child's own calls, reservations included).
/// </summary>
internal static class SpawnLimits
{
    public const string MaxUsd = "maxUsd";
    public const string MaxTurns = "maxTurns";
}
