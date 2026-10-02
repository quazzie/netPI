namespace NetPI;

/// <summary>
/// A scheduler or a model middleware refused a run or a call, and says why in <see cref="Message"/>. The loop and the tools that
/// start work catch this one type and relay the reason; they do not need to know what policy refused (a spending limit, an agent that
/// is not available…). <see cref="Kind"/> names the reason for whoever presents it ("budget", "unavailable").
/// </summary>
public sealed class CallRefusedException(string message) : Exception(message)
{
    public string Kind { get; init; } = "refused";

    /// <summary>The user may let this one through (and say so): the refusal is a limit of theirs, not a fact.</summary>
    public bool CanOverride { get; init; }
}
