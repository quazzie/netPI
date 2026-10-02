using Microsoft.Extensions.Logging;

namespace NetPI.Workspaces;

/// <summary>
/// Creates and retires checkouts: a folder for a non-git project, a worktree and branch for a writing worker in a git
/// project, and the serialized integration that merges a worker's commits into the project's branch.
/// <para>
/// The rules that matter, all enforced here rather than by convention:
/// </para>
/// <list type="bullet">
/// <item>A worker's worktree is created from a recorded commit and only from committed state — a dirty parent checkout's
/// uncommitted changes are never swept in, so a worker's diff is its own.</item>
/// <item>Only a <em>managed</em> worktree (one this plugin created, recorded as such) may be removed, and only when it is
/// clean or its work is merged/durable and nothing is running in it. An unfamiliar or dirty worktree is never deleted.</item>
/// <item>Merges into the project's branch are serialized per repository by a lock, so two integrators cannot interleave.</item>
/// </list>
/// </summary>
public sealed class WorkspaceProvisioner(
    IPluginContext ctx,
    IWorkspaceStore store,
    WorkspaceResolver resolver,
    GitProbe git,
    ISettings? settings = null)
{
    /// <summary>Only one integration per repository at a time; the others queue here.</summary>
    private readonly Dictionary<string, SemaphoreSlim> _integrationLocks = new(WorkspacePaths.Comparer);
    private readonly object _lockGate = new();

    public const string DefaultIsolationSetting = "workspaces.isolateWriters";
    public const string WorktreeRootSetting = "workspaces.worktreeRoot";
    public const string BranchPrefixSetting = "workspaces.branchPrefix";

    /// <summary>Whether a writing worker gets its own worktree by default (the setting; a caller can ask either way).</summary>
    public bool IsolationEnabled => Read(DefaultIsolationSetting, true);

    public string BranchPrefix => Read(BranchPrefixSetting, "netpi/");

    /// <summary>
    /// A checkout for a worker. Every refusal is an error, never a silent share of the caller's own directory: if the
    /// project is a git repository and isolation is asked for (or enabled by default), the worker gets a worktree and a
    /// branch before it runs; if it is not a repository, it gets a plain folder, so a sysadmin chat about folders is not
    /// forced through git.
    /// </summary>
    public async Task<WorkspaceOutcome> ProvisionAsync(WorkspaceRequest request, CancellationToken ct)
    {
        // Reuse first, isolation or not: a worker handed a second task keeps the checkout it already has (and its
        // uncommitted work), instead of getting a second worktree for the same repository.
        if (request.Reuse && request.OwnerSessionId is { Length: > 0 } owner)
        {
            if (store.ListWorkspaces(request.ProjectId)
                .FirstOrDefault(w => string.Equals(w.OwnerSessionId, owner, StringComparison.Ordinal) &&
                                     string.Equals(w.Name, request.Name, StringComparison.OrdinalIgnoreCase) &&
                                     Directory.Exists(w.Path)) is { } existing)
                return new WorkspaceOutcome(resolver.Bind(existing), null);
        }
        if (request.ProjectId is not { Length: > 0 } projectId)
            return new WorkspaceOutcome(null, "A workspace needs a project: this session has none, so there is no repository to branch from.");
        var project = ctx.Sessions.GetProject(projectId);
        if (project is null) return new WorkspaceOutcome(null, $"No project {projectId}.");

        var projectRoot = project.Path;
        if (!Directory.Exists(projectRoot))
            return new WorkspaceOutcome(null, $"The project folder {projectRoot} does not exist.");

        var repoCommon = git.CommonDirOf(projectRoot);
        if (repoCommon is null)
        {
            // Not a git repository: an ordinary folder is the honest answer, and no git workflow is imposed on it.
            if (request.Isolated)
                ctx.Logger.LogInformation("{Project} is not a git repository; workspace {Name} is a plain folder", project.Name, request.Name);
            var folder = Path.Combine(Path.GetDirectoryName(projectRoot) ?? projectRoot,
                $"{Path.GetFileName(projectRoot)}-{Slug(request.Name)}");
            Directory.CreateDirectory(folder);
            var plain = store.CreateWorkspace(new WorkspaceInfo
            {
                Name = request.Name, Path = folder, ProjectId = projectId, Kind = "folder",
                OwnerSessionId = request.OwnerSessionId, OwnerAgentId = request.OwnerAgentId, Managed = true,
            });
            return new WorkspaceOutcome(resolver.Bind(plain), null);
        }

        var isolated = request.Isolated || IsolationEnabled;
        if (!isolated)
        {
            // Sharing the project's own checkout: recorded explicitly, so the UI and the guards can see that this
            // worker is not isolated.
            var shared = store.CreateWorkspace(new WorkspaceInfo
            {
                Name = request.Name, Path = projectRoot, ProjectId = projectId, Kind = "attached",
                Branch = git.BranchOf(projectRoot), RepoCommonDir = repoCommon, BaseCommit = git.HeadOf(projectRoot),
                OwnerSessionId = request.OwnerSessionId, OwnerAgentId = request.OwnerAgentId, Managed = false,
            });
            return new WorkspaceOutcome(resolver.Bind(shared), null);
        }

        // A fresh branch from a recorded commit. The parent's uncommitted changes are deliberately not included.
        var branch = MakeBranchName(request.Name, projectRoot);
        var startAt = request.Base ?? git.HeadOf(projectRoot);
        if (string.IsNullOrWhiteSpace(startAt))
            return new WorkspaceOutcome(null, $"{projectRoot} has no commits yet, so there is nothing to branch a workspace from. Make the first commit, or work in the project folder without isolation.");
        var baseCommit = startAt.Trim();
        var target = Path.Combine(WorktreeRoot(projectRoot), Slug(request.Name));

        if (Directory.Exists(target))
            return new WorkspaceOutcome(null, $"{target} already exists. Pick another name for the workspace, or remove that folder.");

        // A stale administrative directory (git worktree add failed halfway) would make every later add fail.
        var (code, output) = await git.ExecAsync(projectRoot, ct, "worktree", "prune").ConfigureAwait(false);
        if (code != 0)
            ctx.Logger.LogDebug("git worktree prune in {Root} said: {Output}", projectRoot, output.Trim());

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        ExcludeFromGit(projectRoot, Path.GetDirectoryName(target)!);   // before the checkout lands, so the parent stays clean
        var (addCode, addOut) = await git.ExecAsync(projectRoot, ct, "worktree", "add", "-b", branch, target, baseCommit).ConfigureAwait(false);
        if (addCode != 0)
        {
            // Leave nothing runnable behind: a directory git could not populate is not a workspace.
            try { if (Directory.Exists(target) && Directory.GetFileSystemEntries(target).Length == 0) Directory.Delete(target); } catch { }
            return new WorkspaceOutcome(null, $"Could not create a worktree for \"{request.Name}\": {addOut.Trim()}");
        }

        var created = store.CreateWorkspace(new WorkspaceInfo
        {
            Name = request.Name, Path = target, ProjectId = projectId, Kind = "worktree",
            Branch = branch, BaseCommit = baseCommit, RepoCommonDir = repoCommon,
            OwnerSessionId = request.OwnerSessionId, OwnerAgentId = request.OwnerAgentId, Managed = true,
        });
        ctx.Logger.LogInformation("Workspace {Name} for {Owner}: {Path} (branch {Branch} from {Commit})",
            request.Name, request.OwnerSessionId ?? "?", target, branch, baseCommit[..Math.Min(8, baseCommit.Length)]);
        return new WorkspaceOutcome(resolver.Bind(created), null);
    }

    /// <summary>Attach an existing checkout to a project without creating anything: what the user already has on disk.</summary>
    public WorkspaceOutcome Attach(string path, string? projectId, string name, string? ownerSessionId = null)
    {
        var full = WorkspacePaths.Canonical(path);
        if (!Directory.Exists(full)) return new WorkspaceOutcome(null, $"Not a directory: {full}");
        if (projectId is { Length: > 0 } pid && ctx.Sessions.GetProject(pid) is not { } project)
            return new WorkspaceOutcome(null, $"No project {pid}.");

        // An attached checkout of a repository may only be attached to the project of that same repository.
        var repoCommon = git.CommonDirOf(full);
        if (projectId is { Length: > 0 } pid2 && ctx.Sessions.GetProject(pid2) is { } p)
        {
            var projectRepo = git.CommonDirOf(p.Path);
            if (projectRepo is not null && repoCommon is not null &&
                !WorkspacePaths.Comparer.Equals(WorkspacePaths.Canonical(projectRepo), WorkspacePaths.Canonical(repoCommon)))
                return new WorkspaceOutcome(null,
                    $"{full} belongs to a different repository than the project \"{p.Name}\" ({p.Path}). Attach it to the project of its own repository.");
        }
        var workspace = store.CreateWorkspace(new WorkspaceInfo
        {
            Name = name, Path = full, ProjectId = projectId, Kind = "attached",
            Branch = git.BranchOf(full), BaseCommit = git.HeadOf(full), RepoCommonDir = repoCommon,
            OwnerSessionId = ownerSessionId, Managed = false,
        });
        return new WorkspaceOutcome(resolver.Bind(workspace), null);
    }

    /// <summary>
    /// Whether a worker's commits are already durable: reachable from a ref other than the worker's own branch (merged
    /// into the project's branch, or pushed). Uncommitted work does not count — that is <see cref="CanRetire"/>'s job,
    /// because CanRetire is the one allowed to delete.
    /// </summary>
    public bool WorkIsDurable(WorkspaceInfo workspace)
    {
        if (workspace.RepoCommonDir is null || workspace.Branch is not { Length: > 0 } branch) return true;  // not a repository
        if (!Directory.Exists(workspace.Path)) return true;
        if (!git.BranchExists(workspace.Path, branch)) return true;                                        // the branch is gone
        if (git.HeadOf(workspace.Path) is not { Length: > 0 } head) return true;
        var elsewhere = git.BranchesContaining(workspace.Path, head).Where(b => !WorkspacePaths.Comparer.Equals(b, branch)).ToList();
        if (elsewhere.Count > 0) return true;
        // Not on any other branch: durable only when the project's own branch already has it.
        var target = ProjectBranchOf(workspace);
        return target is { Length: > 0 } && IsAncestor(workspace.Path, head, target);
    }

    /// <summary>
    /// Whether it is safe to delete a workspace's checkout: NetPI created it (managed), nothing is bound to it, nothing is
    /// running in it, it has no uncommitted changes, and its commits are already merged or otherwise durable. A dirty,
    /// unfamiliar or unmerged worktree is never deleted, and the reason comes back so the caller can say what to do.
    /// </summary>
    public (bool Ok, string? Reason) CanRetire(WorkspaceInfo workspace, Func<string, bool>? isDirectoryBusy = null)
    {
        if (!workspace.Managed)
            return (false, $"{workspace.Name} was attached, not created by NetPI: remove it yourself when it is no longer needed.");
        if (!Directory.Exists(workspace.Path))
            return (true, null);   // already gone: nothing to delete
        var bound = SessionsUsing(workspace.Id);
        if (bound.Count > 0)
            return (false, $"{workspace.Name} is still bound to {bound.Count} live session(s): {string.Join(", ", bound)}. Unbind them first. " +
                           "Archived sessions do not count: they are not working in the workspace, and retiring it unbinds them.");
        if (isDirectoryBusy?.Invoke(workspace.Path) == true)
            return (false, $"{workspace.Name} still has a running process working in it. Stop it first.");
        var changes = git.DescribeChanges(workspace.Path);
        if (changes.Length > 0)
            return (false, $"{workspace.Name} has uncommitted changes ({changes}); commit them, or keep the worktree.");
        if (!WorkIsDurable(workspace))
            return (false, $"{workspace.Name} holds commits on {workspace.Branch} that are not merged or pushed anywhere yet. Merge them first, or keep the worktree.");
        return (true, null);
    }

    /// <summary>Remove a managed worktree's checkout (only after <see cref="CanRetire"/> allowed it) and its record.</summary>
    public async Task<(bool Ok, string? Error)> RetireAsync(WorkspaceInfo workspace, Func<string, bool>? isDirectoryBusy = null, CancellationToken ct = default)
    {
        var (ok, reason) = CanRetire(workspace, isDirectoryBusy);
        if (!ok) return (false, reason);
        if (Directory.Exists(workspace.Path))
        {
            if (workspace.RepoCommonDir is not null && workspace.Kind == "worktree")
            {
                // Ask git to remove the administrative data too, from the main checkout (found through the common dir).
                var main = Path.GetDirectoryName(WorkspacePaths.Canonical(workspace.RepoCommonDir));
                if (main is { Length: > 0 } && Directory.Exists(main))
                {
                    var (code, output) = await git.ExecAsync(main, ct, "worktree", "remove", "--force", workspace.Path).ConfigureAwait(false);
                    if (code != 0)
                    {
                        try { Directory.Delete(workspace.Path, recursive: true); }
                        catch (Exception ex) { return (false, $"git worktree remove failed ({output.Trim()}); deleting the folder failed too: {ex.Message}"); }
                    }
                }
            }
        }
        store.DeleteWorkspace(workspace.Id);
        resolver.Forget(workspace.Id);
        git.Forget(workspace.Path);
        return (true, null);
    }

    /// <summary>
    /// The branch an integration merges and verifies: the one the worktree is actually on, checked against the record.
    /// The record can go stale — an agent or the user switches branches in the worktree and commits there — and
    /// merging the recorded branch would then be a no-op that still answers merged and verified, because the ancestry
    /// check would run against the same empty branch. A stale record is refused with both branches named; a record
    /// without a branch falls back to what git says the worktree is on (idea-ui6o31).
    /// </summary>
    public (string? Branch, string? Error) IntegrateBranch(WorkspaceInfo workspace)
    {
        var recorded = workspace.Branch is { Length: > 0 } branch ? branch : null;
        string? actual = null;
        if (Directory.Exists(workspace.Path))
        {
            // Asked uncached: the branch the worktree is on <em>right now</em> is the question. A cached answer is
            // precisely the staleness this check exists to catch — a switch made seconds ago is the common case.
            actual = git.Run(workspace.Path, "rev-parse", "--abbrev-ref", "HEAD");
        }
        if (recorded is not null && actual is not null && !WorkspacePaths.Comparer.Equals(recorded, actual))
            return (null,
                $"{workspace.Name}'s worktree is on branch {actual}, but the record says {recorded}. Merging the recorded " +
                "branch would not include what the worktree is working on, so the integration would be reported as merged " +
                "and verified without touching it. Update the record (or re-attach the workspace) to match the worktree, " +
                "and integrate again.");
        if (recorded is not null) return (recorded, null);
        if (actual is not null) return (actual, null);
        return (null, $"{workspace.Name} is not on a branch of its own; there is nothing to merge.");
    }

    /// <summary>
    /// Merge a worker's branch into the project's branch, one repository at a time. The lock is per repository, so two
    /// integrators serialize instead of racing on the same index; the second waits and re-reads the branch, which is why
    /// the merge runs after it takes the lock rather than before.
    /// </summary>
    public async Task<(bool Ok, string? Error)> IntegrateAsync(WorkspaceInfo workspace, string? intoBranch = null, CancellationToken ct = default)
    {
        // The branch to merge and verify: the worktree's actual branch, refused when the record has gone stale.
        var (branch, problem) = IntegrateBranch(workspace);
        if (branch is null)
            return (false, problem ?? $"{workspace.Name} is not on a branch of its own; there is nothing to merge.");
        var repo = workspace.RepoCommonDir is { } common ? Path.GetDirectoryName(WorkspacePaths.Canonical(common)) : null;
        if (repo is not { Length: > 0 } || !Directory.Exists(repo))
            return (false, $"The main checkout of {workspace.Name}'s repository was not found; merge it by hand.");
        var target = intoBranch ?? ProjectBranchOf(workspace) ?? "master";
        if (target == branch)
            return (false, $"{workspace.Name} already works on {target}; there is nothing to merge into it.");

        var gate = IntegrationLock(WorkspacePaths.Canonical(repo));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Re-read under the lock: the branch may have moved since the caller decided to integrate.
            if (!git.BranchExists(repo, branch))
                return (false, $"Branch {branch} does not exist any more.");
            var (checkoutCode, checkoutOut) = await git.ExecAsync(repo, ct, "checkout", target).ConfigureAwait(false);
            if (checkoutCode != 0)
                return (false, $"Could not switch {repo} to {target}: {checkoutOut.Trim()}");
            var (mergeCode, mergeOut) = await git.ExecAsync(repo, ct, "merge", "--no-ff", "-m", $"Merge {branch} into {target}", branch).ConfigureAwait(false);
            if (mergeCode != 0)
            {
                await git.ExecAsync(repo, ct, "merge", "--abort").ConfigureAwait(false);
                return (false, $"Merging {branch} into {target} did not go cleanly: {mergeOut.Trim()} Resolve it by hand in {repo}.");
            }
            return (true, mergeOut.Trim());
        }
        finally { gate.Release(); }
    }

    /// <summary>Whether <paramref name="commit"/> is an ancestor of <paramref name="ref"/>: the ancestry check after a merge.</summary>
    public bool IsAncestor(string repo, string commit, string @ref)
    {
        if (string.IsNullOrWhiteSpace(commit)) return false;
        return git.Run(repo, "merge-base", "--is-ancestor", commit, @ref) is not null;
    }

    /// <summary>
    /// The sessions bound to a workspace, exactly: the store's indexed answer, not a page of the session list — a
    /// bound session must not fall out of a newest-first window and let the worktree be retired under it
    /// (idea-2jiez8). Archived sessions are not counted: they are not working in their workspace, and retiring the
    /// workspace unbinds them (the session falls back to its project).
    /// </summary>
    public IReadOnlyList<string> SessionsUsing(string workspaceId) =>
        ctx.Sessions.SessionIdsUsingWorkspace(workspaceId);

    /// <summary>The lock that serializes integration for one repository.</summary>
    public SemaphoreSlim IntegrationLock(string repoCommonDir)
    {
        lock (_lockGate)
        {
            if (!_integrationLocks.TryGetValue(repoCommonDir, out var gate))
                _integrationLocks[repoCommonDir] = gate = new SemaphoreSlim(1, 1);
            return gate;
        }
    }

    /// <summary>Where worktrees of a project are created: <c>workspaces.worktreeRoot</c>, else <c>&lt;project&gt;/.worktrees</c>.</summary>
    public string WorktreeRoot(string projectRoot)
    {
        var configured = Read(WorktreeRootSetting, "");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var dir = Path.IsPathRooted(configured) ? configured : Path.Combine(projectRoot, configured);
            Directory.CreateDirectory(dir);
            return dir;
        }
        var inside = Path.Combine(WorkspacePaths.Canonical(projectRoot), DefaultWorktreeFolder);
        Directory.CreateDirectory(inside);
        return inside;
    }

    /// <summary>Where a project's worktrees live unless the setting says otherwise: inside the project, not beside it.</summary>
    public const string DefaultWorktreeFolder = ".worktrees";

    /// <summary>
    /// Keep a worktree root out of its repository's status, without editing the project's tracked <c>.gitignore</c>: the
    /// pattern goes into the shared <c>.git/info/exclude</c>, which is local to this clone (never committed, so no branch
    /// ever gains an ignore line because of one worker) and applies to every linked worktree of the same repository.
    /// <para>Best effort: a repository that cannot write it (read-only, or not a repository at all) is left as it was, and a
    /// worktree root outside the repository needs nothing.</para>
    /// </summary>
    private void ExcludeFromGit(string repoRoot, string worktreeRoot)
    {
        try
        {
            var repo = WorkspacePaths.Canonical(repoRoot).TrimEnd(Path.DirectorySeparatorChar);
            var root = WorkspacePaths.Canonical(worktreeRoot);
            if (!WorkspacePaths.IsInside(repo, root)) return;                   // outside the repository: nothing to ignore
            var common = WorkspacePaths.Canonical(git.CommonDirOf(repo) ?? Path.Combine(repo, ".git"));
            var exclude = Path.Combine(common, "info", "exclude");
            var pattern = "/" + root[(repo.Length + 1)..].Replace('\\', '/') + "/";
            if (File.Exists(exclude) && File.ReadAllText(exclude).Contains(pattern, StringComparison.Ordinal)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(exclude)!);
            File.AppendAllText(exclude, pattern + Environment.NewLine);
            ctx.Logger.LogDebug("Kept {Root} out of git's status through {Exclude}", root, exclude);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            ctx.Logger.LogDebug(ex, "Could not keep {Root} out of git's status", worktreeRoot);
        }
    }

    /// <summary>The branch of the project's own checkout: where a worker's work is integrated into.</summary>
    public string? ProjectBranchOf(WorkspaceInfo workspace) =>
        workspace.ProjectId is { } pid && ctx.Sessions.GetProject(pid) is { } p ? git.BranchOf(p.Path) : null;

    private string MakeBranchName(string name, string projectRoot)
    {
        var branch = BranchPrefix + Slug(name);
        if (!git.BranchExists(projectRoot, branch)) return branch;
        // An existing branch with that name is somebody's work: give this one its own.
        for (var n = 2; n < 100; n++)
            if (!git.BranchExists(projectRoot, $"{branch}-{n}")) return $"{branch}-{n}";
        return $"{branch}-{DateTime.UtcNow:yyyyMMddHHmmss}";
    }

    private static string Slug(string name)
    {
        var slug = new string([.. name.Trim().ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-')]);
        while (slug.Contains("--", StringComparison.Ordinal)) slug = slug.Replace("--", "-", StringComparison.Ordinal);
        slug = slug.Trim('-');
        return slug.Length == 0 ? "workspace" : slug[..Math.Min(40, slug.Length)];
    }

    private T Read<T>(string path, T fallback)
    {
        try { return settings is null ? fallback : settings.Get(path, fallback) ?? fallback; }
        catch { return fallback; }
    }
}