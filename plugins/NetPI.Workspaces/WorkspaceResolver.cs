namespace NetPI.Workspaces;

/// <summary>
/// The one place that answers "which directory does this session work in". The runtime, the file tools, the shell's
/// default cwd, file mentions, instruction and skill discovery, the context notices and the Files/Git views all call
/// this (through the registered <see cref="IWorkspaceResolver"/> service), so they cannot disagree about a session's
/// root — which was the actual bug: each of them asked the session store, and the store only knew about projects.
/// <para>
/// Three rules, in this order:
/// </para>
/// <list type="number">
/// <item>Not bound to a workspace → the project's path (or the default workspace). Exactly the behavior before this
/// feature, so an existing chat is untouched.</item>
/// <item>Bound, and the workspace is usable → its root. <see cref="Require"/> returns it; callers that only want a hint
/// use <see cref="Resolve"/>.</item>
/// <item>Bound, and the workspace is missing, is not a directory, or belongs to another repository → a
/// <see cref="WorkspaceUnavailableException"/>. Never the project checkout: a silent fall back is how a worker's write
/// lands in master.</item>
/// </list>
/// <para>
/// The version in a binding comes from the workspace record's update time, so the UI can key a refresh on identity +
/// version and an answer computed for the previous root is recognizably stale.
/// </para>
/// </summary>
public sealed class WorkspaceResolver(IPluginContext ctx, IWorkspaceStore store, GitProbe git) : IWorkspaceResolver
{
    /// <summary>How stale a resolved root may be before it is re-checked (a deleted worktree must not be used for minutes).</summary>
    private static readonly TimeSpan VerifyAfter = TimeSpan.FromSeconds(2);
    private readonly Dictionary<string, (DateTime At, WorkspaceBinding? Binding)> _cache = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    /// <summary>
    /// The session's binding, or null when it is not bound to one. <b>Strict</b>: a session bound to a workspace that is
    /// missing, gone or of another repository throws <see cref="WorkspaceUnavailableException"/> instead of returning
    /// null. Null is only ever "this session has no workspace", which is the case where falling back to the project path
    /// is correct — returning null for a broken binding is exactly the silent fall back this exists to prevent.
    /// </summary>
    public WorkspaceBinding? Resolve(SessionInfo? session)
    {
        if (session?.WorkspaceId is not { Length: > 0 } id) return null;
        var workspace = store.GetWorkspace(id) ??
            throw new WorkspaceUnavailableException(
                $"This session is bound to workspace {id}, which no longer exists. Bind a workspace that exists (sessions.setWorkspace), " +
                "or unbind it to work in the project folder again. Nothing will be written to the project checkout instead.");
        lock (_gate)
            if (_cache.TryGetValue(id, out var hit) && DateTime.UtcNow - hit.At < VerifyAfter && hit.Binding is { } b)
                return b;
        var binding = Validate(session, workspace);
        lock (_gate)
        {
            _cache[id] = (DateTime.UtcNow, binding);
            if (_cache.Count > 256) _cache.Clear();
        }
        return binding;
    }

    /// <summary>The binding if it can be used, null when the session is unbound <em>or</em> when its workspace is broken
    /// (for a display that must show something: it names the problem instead of pretending the project is the root).</summary>
    public WorkspaceBinding? ResolveLenient(SessionInfo? session)
    {
        try { return Resolve(session); }
        catch (WorkspaceUnavailableException) { return null; }
    }

    public WorkspaceBinding? ResolveById(string workspaceId) => Resolve(store.GetWorkspace(workspaceId) is { } w ? SessionFor(w) : null);

    public WorkspaceBinding Require(SessionInfo session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (session.WorkspaceId is not { Length: > 0 }) throw new WorkspaceUnavailableException(NoWorkspace(session));
        return Resolve(session) ?? throw new WorkspaceUnavailableException(NoWorkspace(session));
    }

    /// <summary>The session's working directory: its workspace root when bound (validated), else the project's path.</summary>
    public string CwdOf(SessionInfo session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return session.WorkspaceId is { Length: > 0 } ? Require(session).Root : ctx.Sessions.GetCwd(session);
    }

    /// <summary>
    /// Identity of the session's binding for a UI refresh key: workspace id + version when bound, the project id +
    /// path otherwise. Two answers computed under different keys are answers about different roots.
    /// </summary>
    public string IdentityOf(SessionInfo session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (session.WorkspaceId is not { Length: > 0 } id) return $"project:{session.ProjectId ?? "-"}";
        var workspace = store.GetWorkspace(id);
        if (workspace is null) return $"workspace:{id}:missing";
        return $"workspace:{workspace.Id}:{workspace.UpdatedAt.ToUnixTimeMilliseconds()}";
    }

    /// <summary>Check that a bound workspace can actually be used now: it exists, it is a directory, and it is the
    /// repository the session's project is. Each failure is named, because "it broke" is not actionable.</summary>
    public WorkspaceBinding Validate(SessionInfo session, WorkspaceInfo workspace)
    {
        if (!Directory.Exists(workspace.Path))
            throw new WorkspaceUnavailableException(
                $"This session's workspace {workspace.Name} ({workspace.Path}) does not exist. It was removed or moved since it was bound; " +
                "bind another workspace (sessions.setWorkspace) or unbind it. Nothing will be written to the project checkout instead.");
        var binding = Bind(workspace);
        if (session.ProjectId is { } projectId && ctx.Sessions.GetProject(projectId) is { } project)
        {
            var projectRepo = git.CommonDirOf(project.Path);
            if (projectRepo is not null && binding.RepoCommonDir is not null &&
                !WorkspacePaths.Comparer.Equals(WorkspacePaths.Canonical(projectRepo), WorkspacePaths.Canonical(binding.RepoCommonDir)))
                throw new WorkspaceUnavailableException(
                    $"This session's workspace {workspace.Name} ({workspace.Path}) belongs to a different repository than its project " +
                    $"\"{project.Name}\" ({project.Path}). A workspace of one project cannot be used by another; bind a workspace of this project.");
        }
        return binding;
    }

    /// <summary>The binding of a record: its root, plus what git says about that root.</summary>
    public WorkspaceBinding Bind(WorkspaceInfo w) => new(
        w.Id, w.Path,
        w.Branch ?? SafeBranch(w.Path),
        w.BaseCommit,
        w.RepoCommonDir ?? git.CommonDirOf(w.Path),
        w.Kind, w.OwnerSessionId, w.OwnerAgentId, w.Managed,
        Version: w.UpdatedAt.ToUnixTimeMilliseconds());

    /// <summary>Forget the cached check of a workspace (after it was rewritten or removed).</summary>
    public void Forget(string workspaceId)
    {
        lock (_gate) _cache.Remove(workspaceId);
    }

    public void ForgetAll()
    {
        lock (_gate) _cache.Clear();
    }

    private string? SafeBranch(string path)
    {
        try { return Directory.Exists(path) ? git.BranchOf(path) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private static string NoWorkspace(SessionInfo session) =>
        session.ProjectId is null ? "This session has no project and no workspace, so there is no directory to work in."
        : $"Session {session.Id} has no usable workspace.";

    /// <summary>A stand-in session for resolving a workspace by id (no project to check it against).</summary>
    private static SessionInfo SessionFor(WorkspaceInfo w) => new() { Id = "", ProjectId = null, WorkspaceId = w.Id };
}