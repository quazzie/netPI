namespace NetPI.Workspaces;

/// <summary>
/// What the agent runtime and the spawn tool ask for, without knowing that git exists: "before this worker starts, make
/// sure it has a workspace, and tell me which one." Kept separate from <see cref="WorkspaceProvisioner"/> (which knows
/// about git and worktrees) so the pieces that only need a workspace do not grow a git dependency, and so a project
/// that is not a repository never goes near one.
/// </summary>
public sealed class WorkspaceManager(
    IPluginContext ctx,
    IWorkspaceStore store,
    WorkspaceResolver resolver,
    WorkspaceProvisioner provisioner,
    GitProbe git) : IWorkspaceProvisioner
{
    /// <summary>The workspace an already-running session uses, without validating it (for display).</summary>
    public WorkspaceBinding? Of(SessionInfo? session) => resolver.ResolveLenient(session);

    /// <summary>
    /// The workspace a child worker should start in: the one that was named (and must exist), the parent's (so a reader
    /// sees what its boss sees), or a fresh one provisioned for it. Every outcome is either a binding or a refusal —
    /// there is no "start it in the parent's checkout anyway".
    /// <para>
    /// Isolation is decided by what the worker can do, not by the setting alone: a worker whose tools cannot write shares
    /// the caller's workspace (a reader gains nothing from a worktree of its own, and a worktree per research subagent is
    /// real disk and real confusion), while a worker that gets a write tool gets its own checkout when
    /// <see cref="WorkspaceProvisioner.IsolationEnabled"/> is on. <see cref="SpawnWorkspace.Isolated"/> asks for one
    /// regardless, and <c>isolated: false</c> on a reader keeps the old behavior.
    /// </para>
    /// </summary>
    public async Task<WorkspaceOutcome> ForChildAsync(SpawnRequest request, SessionInfo? parentSession, string childSessionId, string childName, CancellationToken ct)
    {
        var named = request.Workspace()?.WorkspaceId;
        if (named is { Length: > 0 } && !string.Equals(named, "new", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(named, "own", StringComparison.OrdinalIgnoreCase) && !Isolated(request, ctx.Services.Get<IToolRegistry>()))
        {
            var found = Find(named);
            if (found is null) return new WorkspaceOutcome(null, NoWorkspace(named));
            if (parentSession?.ProjectId is { } parentProject && found.ProjectId is not null && found.ProjectId != parentProject)
                return new WorkspaceOutcome(null, $"Workspace \"{found.Name}\" belongs to another project than this one; a worker of a project works in one of its workspaces.");
            if (!Directory.Exists(found.Path))
                return new WorkspaceOutcome(null, NoWorkspace(named));
            return new WorkspaceOutcome(resolver.Bind(found), null);
        }

        var projectId = request.ProjectId ?? parentSession?.ProjectId;
        if (projectId is not { Length: > 0 })
        {
            // No project: there is nothing to branch from, and a folder workspace of nowhere is just the default
            // workspace the child already has. Say so rather than inventing a checkout.
            return new WorkspaceOutcome(null, null);
        }
        if (request.ProjectId is not null && parentSession?.ProjectId is { } pp && request.ProjectId != pp)
        {
            // A project switch is a user decision (the sessions.setProject RPC); a spawn asks for a workspace, not for a
            // different project. Refusing keeps one conversation's backlog out of another project's checkout.
            if (ctx.Sessions.GetProject(request.ProjectId) is null)
                return new WorkspaceOutcome(null, NoProject(request.ProjectId));
            return new WorkspaceOutcome(null,
                $"A subagent works in its parent's project; pass workspace (one of that project's workspaces) instead of switching project. Project {pp} stays.");
        }

        if (!Isolated(request, ctx.Services.Get<IToolRegistry>()))
        {
            // Nothing asked for a checkout of its own: the child starts where its parent works, and a null binding says
            // exactly that (the runtime inherits the parent's). Provisioning here would give every reader a workspace
            // record pointing at the project's own folder, which isolates nothing and only obscures where it works.
            return new WorkspaceOutcome(null, null);
        }

        return await provisioner.ProvisionAsync(new WorkspaceRequest(
            projectId,
            string.IsNullOrWhiteSpace(request.Workspace()?.Name) ? childName : request.Workspace()!.Name!.Trim(),
            request.Workspace()?.OwnerSessionId ?? childSessionId,
            request.ParentAgentId,
            Isolated: Isolated(request),
            Base: request.Workspace()?.Base), ct).ConfigureAwait(false);
    }

    /// <summary>The tools that make a worker a writer: without one of these its writes cannot reach the checkout.</summary>
    private static readonly HashSet<string> WriteTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "write", "edit", "write_file", "edit_file", "notebook_edit", "patch", "bash", "pwsh", "shell",
    };

    /// <summary>
    /// Whether this worker gets a checkout of its own: asked for, or able to write while the setting says writers should be
    /// isolated. <paramref name="registry"/> may be null (the runtime calls this before the tools are known), in which case
    /// only an explicit request counts — an unknown tool set is not a reason to create a worktree.
    /// </summary>
    public bool Isolated(SpawnRequest request, IToolRegistry? registry = null)
    {
        if (request.Workspace()?.Isolated == true) return true;
        if (!provisioner.IsolationEnabled) return false;
        var allow = request.Tools;
        if (allow is null)
        {
            if (registry is null) return false;                       // its parent's tools, unknown here: assume a reader
            return registry.All.Any(t => WriteTools.Contains(t.Definition.Name));
        }
        return allow.Any(t => WriteTools.Contains(t));
    }

    /// <summary>A workspace by id, then by name, then by path (case-insensitively). Null when none of them is it.</summary>
    public WorkspaceInfo? Find(string idOrNameOrPath)
    {
        var all = store.ListWorkspaces();
        return all.FirstOrDefault(w => string.Equals(w.Id, idOrNameOrPath, StringComparison.Ordinal))
            ?? all.FirstOrDefault(w => string.Equals(w.Name, idOrNameOrPath, StringComparison.OrdinalIgnoreCase))
            ?? all.FirstOrDefault(w => WorkspacePaths.Comparer.Equals(WorkspacePaths.Canonical(w.Path), WorkspacePaths.Canonical(idOrNameOrPath)));
    }

    /// <summary>Bind a session to a workspace (the only way a session's working directory changes).</summary>
    public void Bind(string sessionId, string? workspaceId) => store.SetSessionWorkspace(sessionId, workspaceId);

    public GitProbe Git => git;
    public WorkspaceResolver Resolver => resolver;

    private static string NoWorkspace(string id) =>
        $"No workspace \"{id}\". List them with workspaces.listForProject, or pass isolated: true to get a new worktree for this worker.";

    private static string NoProject(string id) => $"No project {id}.";
}