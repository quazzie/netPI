namespace NetPI;

/// <summary>
/// How the parts that speak the higher-abstraction contracts read and write what the core's contexts carry in their
/// <see cref="FeatureSet"/>: the checkout a session works in, the slot a run holds, and what a spawner asks for.
/// </summary>
public static class FeatureExtensions
{
    /// <summary>The session's workspace, when it is bound to one. Null is the plain behaviour: relative paths resolve in the project's
    /// folder and nothing is refused. A tool that mutates should ask <see cref="WorkspacePaths.CheckMutation"/> rather than compare paths itself.</summary>
    public static WorkspaceBinding? Workspace(this ToolContext context) => context.Features.Get<WorkspaceBinding>();

    /// <summary>The slot the run holds on its agent, or null.</summary>
    public static IAgentSlot? AdmissionLease(this ToolContext context) => context.Features.Get<IAgentSlot>();

    /// <summary>The session's workspace (null when it is not bound to one). <see cref="AgentRunContext.Cwd"/> is this workspace's root when it
    /// is set, and the project's path otherwise: the two are resolved once, so every consumer of the run agrees.</summary>
    public static WorkspaceBinding? Workspace(this AgentRunContext run) => run.Features.Get<WorkspaceBinding>();

    public static void SetWorkspace(this AgentRunContext run, WorkspaceBinding? binding) => run.Features.Set(binding);

    public static IAgentSlot? AdmissionLease(this AgentRunContext run) => run.Features.Get<IAgentSlot>();

    public static void SetAdmissionLease(this AgentRunContext run, IAgentSlot? slot) => run.Features.Set(slot);

    /// <summary>What a spawner asks of the workspace for the child, or null.</summary>
    public static SpawnWorkspace? Workspace(this SpawnRequest request) => request.Features.Get<SpawnWorkspace>();
}

/// <summary>
/// What a spawn asks of the workspace for the child: an existing workspace (id or name) or a fresh isolated one. Resolved and
/// provisioned by the workspace plugin before the child runs; when none is loaded it is ignored and the child's project behaves as it did
/// before.
/// </summary>
public sealed record SpawnWorkspace(
    /// <summary>An existing workspace id or name. Null = the parent's workspace (bound or not).</summary>
    string? WorkspaceId = null,
    /// <summary>Give the subagent its own worktree and branch (a writing worker), instead of sharing the caller's checkout.</summary>
    bool Isolated = false,
    /// <summary>Name for a provisioned workspace (default: the subagent's name).</summary>
    string? Name = null,
    /// <summary>Session that owns a provisioned workspace (default: the subagent's own session). Ownership belongs to the worker, so a worker
    /// that is handed a second task keeps the checkout it already has.</summary>
    string? OwnerSessionId = null,
    /// <summary>Branch or commit a provisioned workspace starts from (default: the project's current HEAD).</summary>
    string? Base = null);
