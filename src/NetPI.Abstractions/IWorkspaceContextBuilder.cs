namespace NetPI.Abstractions;

/// <summary>
/// Builds the structured system-prompt inputs for a workspace (PLAN §43, §46).
/// Implemented by the context plugin; resolved by the agent runner so the two
/// never reference each other's ALC (reload-safe).
/// </summary>
public interface IWorkspaceContextBuilder
{
    ValueTask<SystemPromptInputs> BuildAsync(string workspace, CancellationToken cancellationToken = default);
}
