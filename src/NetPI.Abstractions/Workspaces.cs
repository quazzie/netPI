using System.Text.Json.Nodes;

namespace NetPI;

/// <summary>
/// A workspace is an actual checkout a session works in: a root directory, the branch checked out there, the commit it
/// started from and who owns it. It is deliberately separate from <see cref="ProjectInfo"/>: a project is the shared
/// logical identity (backlog, defaults, which repository), and several workers of the same project work in different
/// workspaces of that one repository.
/// <para>
/// The meta key on a session that points at one is <see cref="SessionWorkspace.MetaKey"/>; the column behind it is
/// <c>sessions.workspace_id</c>. A session with none keeps the project's own path (the behavior before workspaces
/// existed), so nothing has to be migrated for an old chat to keep working.
/// </para>
/// </summary>
public sealed class WorkspaceInfo
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>The checkout root. Absolute, normalized; the only directory this workspace owns.</summary>
    public string Path { get; set; } = "";
    /// <summary>The project this workspace belongs to (null only for a workspace created before it had one).</summary>
    public string? ProjectId { get; set; }
    /// <summary>folder (an ordinary directory) | worktree (a git worktree NetPI created) | attached (an existing checkout).</summary>
    public string Kind { get; set; } = "folder";
    /// <summary>Branch checked out at <see cref="Path"/> when the record was written; null when it is not a git checkout.</summary>
    public string? Branch { get; set; }
    /// <summary>The commit the checkout was at when it was provisioned or attached; null when unknown.</summary>
    public string? BaseCommit { get; set; }
    /// <summary><c>git rev-parse --git-common-dir</c>: the repository this checkout belongs to, so a worktree and its
    /// main checkout are recognizably the same repository. Null when it is not a repository.</summary>
    public string? RepoCommonDir { get; set; }
    /// <summary>The session that owns this workspace (a worker). Ownership belongs to a worker, not to an agent or a slot.</summary>
    public string? OwnerSessionId { get; set; }
    /// <summary>Agent id of the owner when it is a subagent, else null.</summary>
    public string? OwnerAgentId { get; set; }
    /// <summary>True for a worktree NetPI created (and may therefore clean up), false for anything it merely attached.</summary>
    public bool Managed { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public JsonObject? Meta { get; set; }
}

/// <summary>
/// What a session's workspace resolves to at one moment: the root every tool resolves relative paths against, the
/// branch and starting commit to name in the UI and in notices, and the owner. <see cref="Version"/> changes whenever
/// the binding changes (or the record is rewritten), so a UI that keys its refreshes on it cannot mix two roots.
/// <para>
/// A session with no workspace binding has none of these: it works in its project's path, exactly as before, and every
/// consumer falls back to <see cref="ISessionStore.GetCwd"/>.
/// </para>
/// </summary>
public sealed record WorkspaceBinding(
    string WorkspaceId,
    string Root,
    string? Branch = null,
    string? BaseCommit = null,
    string? RepoCommonDir = null,
    string Kind = "folder",
    string? OwnerSessionId = null,
    string? OwnerAgentId = null,
    bool Managed = false,
    long Version = 0)
{
    /// <summary>True when this workspace is its own checkout of the repository (a worktree), so writes here cannot reach another worker.</summary>
    public bool Isolated => Kind is "worktree";

    /// <summary>One line for the composer, a notice and a tool result.</summary>
    public string Describe() =>
        Branch is null ? Root : $"{Root} (branch {Branch})";
}

/// <summary>
/// The workspace a session is bound to: <c>meta.workspaceId</c> on the session, written by the workspace plugin and by
/// <c>sessions.setWorkspace</c>. It lives in Abstractions because the key is the contract of
/// <see cref="EventTypes.SessionWorkspace"/>, not an implementation detail of whichever plugin wrote it.
/// </summary>
public static class SessionWorkspace
{
    public const string MetaKey = "workspaceId";

    public static string? Of(SessionInfo? session) =>
        session?.Meta?[MetaKey] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;
}

/// <summary>
/// Persistence for workspaces and the session reference to one. Implemented by the host (the sessions database); the
/// workspace plugin owns the behavior around it (git provisioning, ownership, cleanup), so the host stays a store.
/// </summary>
public interface IWorkspaceStore
{
    IReadOnlyList<WorkspaceInfo> ListWorkspaces(string? projectId = null);
    WorkspaceInfo? GetWorkspace(string id);
    WorkspaceInfo CreateWorkspace(WorkspaceInfo template);
    WorkspaceInfo UpdateWorkspace(string id, Action<WorkspaceInfo> mutate);
    /// <summary>Delete a workspace record (its sessions fall back to their project). False when it does not exist.</summary>
    bool DeleteWorkspace(string id);

    /// <summary>The workspace a session is bound to, or null when it is not bound to one.</summary>
    WorkspaceInfo? GetSessionWorkspace(string sessionId);
    /// <summary>Bind (or unbind with null) a session's workspace. Throws when the workspace does not exist.</summary>
    void SetSessionWorkspace(string sessionId, string? workspaceId);
}

/// <summary>
/// The one resolver every part of the harness asks which directory a session works in: the runtime (which sets
/// <see cref="ToolContext.Cwd"/> and <see cref="ToolContext.Workspace"/>), the file tools, the shell's default cwd,
/// file mentions, instruction and skill discovery, the context notices and the Files/Git views. Registered by the
/// workspace plugin; a consumer that does not find one falls back to <see cref="ISessionStore.GetCwd"/>, which is the
/// pre-workspace behavior.
/// </summary>
public interface IWorkspaceResolver
{
    /// <summary>The session's workspace, or null when it is not bound to one (legacy behavior: its project path).</summary>
    WorkspaceBinding? Resolve(SessionInfo? session);
    WorkspaceBinding? ResolveById(string workspaceId);
    /// <summary>The session's workspace, or a clear failure when it is bound to one that is missing or invalid.</summary>
    WorkspaceBinding Require(SessionInfo session);
    /// <summary>
    /// The binding if it can be used, else null — including when it is bound but broken. For displays (the Files tab, the
    /// composer, a notice), which have to show something either way; the strict <see cref="Resolve"/> stays the answer for
    /// anything that acts on it. The default is <see cref="Resolve"/>, so an implementation that has nothing extra to say
    /// throws like it does.
    /// </summary>
    WorkspaceBinding? ResolveLenient(SessionInfo? session) => Resolve(session);
    /// <summary>The directory the session works in, or a clear failure. Never falls back to the project checkout for a bound session.</summary>
    string CwdOf(SessionInfo session);
    /// <summary>Identity of the session's binding for UI refresh keys: the workspace id and its version, or the project path.</summary>
    string IdentityOf(SessionInfo session);
}

/// <summary>
/// What a repository a directory belongs to is. The workspace plugin implements it with git (through
/// <c>--git-common-dir</c>, which a linked worktree reports as the main checkout's git dir), so path comparison alone
/// never decides whether two checkouts are the same repository.
/// </summary>
public interface IWorkspaceRepoProbe
{
    /// <summary>The repository's common git directory for a directory, or null when it is not in a repository.</summary>
    string? CommonDirOf(string path);
    /// <summary>The branch checked out at a directory, or null.</summary>
    string? BranchOf(string path);
    /// <summary>The commit HEAD is at, or null.</summary>
    string? HeadOf(string path);
}

/// <summary>
/// What a caller asks for when it needs a checkout before it starts working. Shared so that the pieces which only need
/// "a checkout for this worker" (the agent runtime, the spawn tool) do not have to know that worktrees exist.
/// </summary>
public sealed record WorkspaceRequest(
    string? ProjectId,
    string Name,
    /// <summary>The worker that will own it: a session id, plus the agent id when it is a subagent.</summary>
    string? OwnerSessionId = null,
    string? OwnerAgentId = null,
    /// <summary>Give the worker its own git worktree and branch. False shares an existing checkout.</summary>
    bool Isolated = false,
    /// <summary>Branch or commit to start from (null = the project's current HEAD).</summary>
    string? Base = null,
    /// <summary>Reuse a workspace this owner already has with that name, instead of making a second one.</summary>
    bool Reuse = true);

/// <summary>
/// What provisioning produced, or why it refused. There is no third answer: a worker either has a workspace or the
/// spawn fails, because "start it in the parent's checkout anyway" is the accident this whole feature prevents.
/// </summary>
public sealed record WorkspaceOutcome(WorkspaceBinding? Binding, string? Error)
{
    public bool Ok => Error is null && Binding is not null;
}

/// <summary>
/// The workspace a child worker should start in, decided before the child's session exists. Implemented by the
/// workspace plugin and resolved per use, so the runtime and the spawn tool do not depend on it being loaded (without it
/// every worker shares its parent's project, exactly as before).
/// </summary>
public interface IWorkspaceProvisioner
{
    Task<WorkspaceOutcome> ForChildAsync(SpawnRequest request, SessionInfo? parentSession, string childSessionId, string childName, CancellationToken ct);
}

/// <summary>Which processes are running in which directory. The shell plugin registers the implementation (it owns the process
/// registry); the workspace plugin asks before it deletes a checkout, because a background process keeps its workspace's
/// ownership for as long as it runs and must not have its files pulled out from under it.
/// </summary>
public interface IWorkspaceProcesses
{
    bool IsBusyIn(string path);
}

/// <summary>
/// A workspace that is bound to a session but cannot be used: it is gone, it is not a directory, or it belongs to a
/// different repository than the session's project. Never a fallback to the project checkout — a silent one would put
/// a worker's writes into the primary checkout, which is the accident this whole feature exists to prevent.
/// </summary>
public sealed class WorkspaceUnavailableException(string message) : Exception(message);

/// <summary>What the value of a <see cref="ToolDefinition.Name"/>'s path-like argument means for the workspace rule.</summary>
public enum WorkspacePathVerdict
{
    /// <summary>Inside the session's workspace (or the session is not bound to one): allowed.</summary>
    Allow,
    /// <summary>Outside the workspace, but not in another checkout of the same repository: the ordinary "outside the project" case.</summary>
    Outside,
    /// <summary>Inside another checkout of the same repository (another worker, or the primary checkout): refused in an isolated workspace.</summary>
    ForeignCheckout,
}

/// <summary>
/// Path rules for workspace isolation, in one place so the file tools, the shell and any other native mutation agree:
/// Windows casing, <c>..</c>, junctions and symlinks are all resolved before anything is compared, because two
/// spellings of the same directory are one directory and the check must not depend on the spelling.
/// <para>
/// This is a path check, not an OS sandbox: it decides what the native tools may touch. It says nothing about what a
/// shell command does once it runs, which is why the shell reports where it actually ran instead of claiming to be
/// confined.
/// </para>
/// </summary>
public static class WorkspacePaths
{
    /// <summary>The comparison this platform uses for paths (case-insensitive on Windows and macOS).</summary>
    public static StringComparison Comparison =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static StringComparer Comparer =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>
    /// The canonical spelling of a path: absolute, without a trailing separator, and with every existing segment
    /// resolved to its real name (so a junction or symlink into a checkout is recognized as that checkout). Falls back
    /// to the normalized path when it cannot be resolved.
    /// </summary>
    public static string Canonical(string path)
    {
        string full;
        try { full = Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return path; }
        try
        {
            var info = new FileInfo(full);
            if (info.Exists || info.LinkTarget is not null) return Trim(info.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? info.FullName);
            var dir = new DirectoryInfo(full);
            if (dir.Exists || dir.LinkTarget is not null) return Trim(dir.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? dir.FullName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return Trim(full);
    }

    private static string Trim(string path) =>
        Path.TrimEndingDirectorySeparator(path).Length == 0 ? path : Path.TrimEndingDirectorySeparator(path);

    /// <summary>Whether <paramref name="path"/> is <paramref name="root"/> or inside it.</summary>
    public static bool IsInside(string root, string path)
    {
        var r = Canonical(root);
        var p = Canonical(path);
        if (string.Equals(r, p, Comparison)) return true;
        var prefix = r.EndsWith(Path.DirectorySeparatorChar) ? r : r + Path.DirectorySeparatorChar;
        return p.StartsWith(prefix, Comparison);
    }

    /// <summary>
    /// Whether a mutation at <paramref name="fullPath"/> may run for a session bound to <paramref name="binding"/>.
    /// An unbound session (no binding) allows everything, which is the behavior before workspaces existed.
    /// <para>
    /// Outside the root is <see cref="WorkspacePathVerdict.Outside"/> unless the path is inside <em>another checkout of
    /// the same repository</em> — then it is <see cref="WorkspacePathVerdict.ForeignCheckout"/>, and in an isolated
    /// workspace that is refused: it is another worker (or the primary checkout the worker was branched from).
    /// </para>
    /// </summary>
    public static WorkspacePathVerdict CheckMutation(WorkspaceBinding? binding, string fullPath, IWorkspaceRepoProbe? probe = null)
    {
        if (binding is null) return WorkspacePathVerdict.Allow;
        if (IsInside(binding.Root, fullPath)) return WorkspacePathVerdict.Allow;
        if (probe is null) return WorkspacePathVerdict.Outside;
        var here = probe.CommonDirOf(binding.Root);
        var there = probe.CommonDirOf(fullPath);
        if (here is null || there is null || !string.Equals(Canonical(here), Canonical(there), Comparison))
            return WorkspacePathVerdict.Outside;
        return WorkspacePathVerdict.ForeignCheckout;
    }

    /// <summary>The message a refused mutation gets: which workspace it is in, and which checkout it aimed at.</summary>
    public static string Refusal(WorkspaceBinding binding, string fullPath, IWorkspaceRepoProbe? probe = null)
    {
        var target = Canonical(fullPath);
        var line = $"Refused: {target} is not inside this session's workspace ({binding.Describe()}).";
        var repo = probe?.CommonDirOf(fullPath);
        if (repo is not null)
            line += $" It is another checkout of the same repository ({repo}), which belongs to another worker or to the primary checkout.";
        line += " Native writes stay in the session's own workspace; use absolute paths only for files that are genuinely elsewhere.";
        return line;
    }
}