namespace NetPI.Runtime;

/// <summary>
/// The caps <c>agent_spawn</c> gives a subagent (<c>maxUsd</c>, <c>maxTurns</c>). They arrive as <see cref="SpawnRequest.MaxUsd"/> and
/// <see cref="SpawnRequest.MaxTurns"/>, are kept on the child's session meta under these names, and are enforced where the work is done: <c>maxTurns</c> by the run loop (model calls per run),
/// <c>maxUsd</c> by the Agents ledger when a call is reserved (the child's own calls, reservations included).
/// </summary>
internal static class SpawnLimits
{
    public const string MaxUsd = "maxUsd";
    public const string MaxTurns = "maxTurns";
}
