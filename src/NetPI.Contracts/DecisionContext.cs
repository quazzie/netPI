namespace NetPI;

/// <summary>
/// A plugin that reads the provider-captured conversation of an agent's model calls (<see cref="ModelRequest.DecisionContext"/>,
/// filled by the provider when <see cref="ModelRequest.CaptureDecisionContext"/> is set: the loops plugin's full-conversation
/// checks, the todo plugin's commit check) declares the need here, as a registered service. The runtime asks for the capture
/// while any registered consumer wants it, instead of knowing the consumers' settings by name. <see cref="WantsDecisionContext"/>
/// is read at every model call, so a setting switched off mid-session stops the capture at the next call, and a plugin that is
/// unloaded takes its need with it.
/// </summary>
public interface IDecisionContextConsumer
{
    bool WantsDecisionContext { get; }
}
