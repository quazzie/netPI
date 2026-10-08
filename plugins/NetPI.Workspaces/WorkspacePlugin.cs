using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace NetPI.Workspaces;

/// <summary>
/// Workspaces: the checkout a session actually works in, as opposed to the project it belongs to. Several workers of one
/// project work in different workspaces, so two agents cannot land in the same tree by accident.
/// <para>
/// What this plugin owns, and what it deliberately does not:</para>
/// <list type="bullet">
/// <item>It registers <see cref="IWorkspaceResolver"/>, the one answer to "which directory does this session work in", plus
/// <see cref="IWorkspaceRepoProbe"/> for the git evidence that decides whether two directories are the same repository.</item>
/// <item>It provisions workspaces (a worktree and branch for a writing worker in a git project, a plain folder otherwise),
/// attaches existing checkouts, integrates a worker's branch into the project's branch one at a time, and retires only
/// the worktrees it created whose work is durable.</item>
/// <item>It stores the records in its own collection and attaches the session's binding to the session (the core stores no workspace
/// field), and the guards are in the tools themselves. Every
/// other part of the harness resolves through the service, so none of them has to know how a workspace is made.</item>
/// </list>
/// </summary>
[NetPiPlugin("netpi.workspaces", Name = "Workspaces",
    Description = "Per-worker checkouts: a worktree and branch per writing worker, attached checkouts, serialized integration",
    Order = 22)]
public sealed class WorkspacePlugin : INetPiPlugin
{
    public Task StartAsync(IPluginContext context, CancellationToken ct)
    {
        // A fork is a new writer and must not share its parent's checkout: it starts unbound (the core never copies meta.cwd either).
        context.Sessions.DeclareForkReset(SessionWorkspace.MetaKey);
        var store = new WorkspaceStore(context);
        context.Services.Register<IWorkspaceStore>(store);
        var git = new GitProbe();
        var resolver = new WorkspaceResolver(context, store, git);
        var provisioner = new WorkspaceProvisioner(context, store, resolver, git, context.Settings);

        context.Services.Register<IWorkspaceRepoProbe>(git);
        context.Services.Register<IWorkspaceResolver>(resolver);
        context.Services.Register(provisioner);   // the git-facing half, for diagnostics and tests
        var manager = new WorkspaceManager(context, store, resolver, provisioner, git);
        context.Services.Register<IWorkspaceProvisioner>(manager);
        context.Services.Register<IAgentHook>(new WorkspaceGuard(context, git));
        // Applied before every other hook: a deferred switch has to settle the root before anything reads it.
        context.Services.Register<IAgentHook>(new WorkspaceSwitchApplier(context, resolver));
        context.Tools.Register(new WorkspaceTool(context, resolver));
        context.Services.Register<IAgentHook>(new WorkspaceNotices(context, resolver));

        context.Services.Register(new SettingsSection
        {
            Id = "workspaces", Title = "Workspaces", Group = "Tools", Order = 15,
            Settings =
            [
                SettingInfo.Bool(WorkspaceProvisioner.DefaultIsolationSetting, "Give each writing worker its own worktree", true,
                    "A subagent or chat that writes gets its own git worktree and branch instead of sharing the project's checkout. " +
                    "Projects that are not git repositories get a plain folder, and are unaffected."),
                SettingInfo.Text(WorkspaceProvisioner.BranchPrefixSetting, "Branch prefix for worktrees", "netpi/",
                    "The branch name of a worktree NetPI creates is this prefix plus the workspace name."),
                SettingInfo.Text(WorkspaceProvisioner.WorktreeRootSetting, "Where worktrees are created", "",
                    "Empty: inside the project, in its .worktrees folder. Otherwise this folder (a relative path is taken from the project). "
                    + "A root inside the repository is kept out of git's status through .git/info/exclude, so no branch gains an ignore line."),
            ],
        });

        RegisterRpcs(context, store, resolver, provisioner, git);
        context.Logger.LogInformation("Workspaces ready (isolation {Isolation})", provisioner.IsolationEnabled);
        return Task.CompletedTask;
    }

    private static void RegisterRpcs(IPluginContext ctx, WorkspaceStore store, WorkspaceResolver resolver, WorkspaceProvisioner provisioner, GitProbe git)
    {
        ctx.Rpc.Register(new RpcMethod("workspaces.list", "Workspaces, newest first: { projectId? } → WorkspaceInfo[]", ReadOnly: true, Params: [RpcParam.Opt("projectId")]),
            (req, _) => Task.FromResult<object?>(store.ListWorkspaces(req.Str("projectId"))));

        ctx.Rpc.Register(new RpcMethod("workspaces.get", "One workspace: { id } → WorkspaceInfo", ReadOnly: true, Params: [RpcParam.Req("id")]),
            (req, _) =>
        {
            var id = req.Required("id");
            return Task.FromResult<object?>(store.GetWorkspace(id) ?? throw new RpcException("not_found", $"Workspace {id} not found"));
        });

        ctx.Rpc.Register(new RpcMethod("sessions.setWorkspace", "Bind/unbind a session's workspace: { id, workspaceId: string|null } → SessionInfo (publishes session.workspace). " +
           "A bound workspace that is missing is an error, never a fall back to the project checkout.", ReadOnly: false, Params: [RpcParam.Req("id", RpcParamType.String, "the session's id"), RpcParam.Opt("workspaceId", RpcParamType.String, "none unbinds")]),
            (req, _) =>
        {
            try { store.SetSessionWorkspace(req.Required("id"), req.Str("workspaceId")); }
            catch (KeyNotFoundException ex) { throw new RpcException("not_found", ex.Message); }
            return Task.FromResult<object?>(ctx.Sessions.GetSession(req.Required("id")));
        });

        // A directory is busy when a process the shell registry knows about is running in it: a background process keeps
        // its workspace's ownership, and a workspace with a live process in it is not one to delete under it.
        Func<string, bool>? busy = path =>
        {
            try { return ctx.Services.Get<IWorkspaceProcesses>()?.IsBusyIn(path) == true; }
            catch { return false; }
        };

        ctx.Rpc.Register(new RpcMethod("workspaces.resolve", "What a session's workspace resolves to: { sessionId } → { workspaceId, root, branch, baseCommit, kind, isolated, ownerSessionId, identity, projectId, available, error? }", ReadOnly: true, Params: [RpcParam.Opt("sessionId")]),
            (req, _) =>
        {
            var session = Session(ctx, req.Str("sessionId"));
            var binding = resolver.ResolveLenient(session);
            return Task.FromResult<object?>(new
            {
                workspaceId = binding?.WorkspaceId,
                root = binding?.Root ?? ctx.Sessions.GetCwd(session!),
                branch = binding?.Branch,
                baseCommit = binding?.BaseCommit,
                kind = binding?.Kind,
                isolated = binding?.Isolated ?? false,
                managed = binding?.Managed ?? false,
                ownerSessionId = binding?.OwnerSessionId,
                ownerAgentId = binding?.OwnerAgentId,
                version = binding?.Version ?? 0,
                projectId = session?.ProjectId,
                projectPath = session?.ProjectId is { } pid ? ctx.Sessions.GetProject(pid)?.Path : null,
                identity = session is null ? null : resolver.IdentityOf(session),
                available = binding is not null || SessionWorkspace.Of(session) is null,
                error = SessionWorkspace.Of(session) is { Length: > 0 } && binding is null ? Missing(session!) : null,
            });
        });

        ctx.Rpc.Register(new RpcMethod("workspaces.git", "Git evidence about a session's workspace root: { sessionId?, cwd? } → { root, topLevel?, commonDir?, branch?, head?, changes, isRepository }", ReadOnly: true, Params: [RpcParam.Opt("cwd"), RpcParam.Opt("sessionId")]),
            async (req, _) =>
        {
            var root = Root(ctx, resolver, req.Str("sessionId"), req.Str("cwd"));
            var common = git.CommonDirOf(root);
            var top = git.TopLevelOf(root);
            return await Task.FromResult<object?>(new
            {
                root,
                topLevel = top,
                commonDir = common,
                branch = git.BranchOf(root),
                head = git.HeadOf(root),
                changes = git.DescribeChanges(root),
                isRepository = common is not null,
            });
        });

        ctx.Rpc.Register(new RpcMethod("workspaces.listForProject", "The workspaces of a project, with their branches and owners: { projectId } → WorkspaceInfo[]", ReadOnly: true, Params: [RpcParam.Opt("projectId")]),
            (req, _) =>
            Task.FromResult<object?>(store
                .ListWorkspaces(req.Str("projectId"))
                .Select(Describe)));

        ctx.Rpc.Register(new RpcMethod("workspaces.create", "Create a workspace for a worker: { projectId, name, ownerSessionId?, ownerAgentId?, isolated?, base? } → WorkspaceInfo " +
           "(a git project gets a worktree and a branch from a recorded commit; a non-git project gets a plain folder)", ReadOnly: false, Params: [RpcParam.Opt("projectId"), RpcParam.Opt("name"), RpcParam.Opt("ownerSessionId"), RpcParam.Opt("ownerAgentId"), RpcParam.Opt("isolated", RpcParamType.Boolean), RpcParam.Opt("base")]),
            async (req, ct) =>
        {
            var outcome = await provisioner.ProvisionAsync(new WorkspaceRequest(
                req.Str("projectId"),
                req.Str("name") ?? "workspace",
                req.Str("ownerSessionId"),
                req.Str("ownerAgentId"),
                Isolated: req.Bool("isolated") ?? provisioner.IsolationEnabled,
                Base: req.Str("base")), ct).ConfigureAwait(false);
            if (!outcome.Ok) throw new RpcException("workspace_failed", outcome.Error!);
            return store.GetWorkspace(outcome.Binding!.WorkspaceId) is { } made ? Describe(made) : Describe(outcome.Binding!);
        });

        ctx.Rpc.Register(new RpcMethod("workspaces.attach", "Attach an existing checkout as a workspace: { path, projectId?, name?, ownerSessionId? } → WorkspaceInfo (a checkout of another repository than the project is refused)", ReadOnly: false, Params: [RpcParam.Req("path"), RpcParam.Opt("name"), RpcParam.Opt("ownerSessionId"), RpcParam.Opt("projectId")]),
            (req, _) =>
        {
            var outcome = provisioner.Attach(req.Required("path"), req.Str("projectId"), req.Str("name") ?? "attached", req.Str("ownerSessionId"));
            if (!outcome.Ok) throw new RpcException("workspace_failed", outcome.Error!);
            return Task.FromResult<object?>(store.GetWorkspace(outcome.Binding!.WorkspaceId) is { } made ? Describe(made) : Describe(outcome.Binding!));
        });

        ctx.Rpc.Register(new RpcMethod("workspaces.delete", "Remove a managed workspace whose work is merged or durable and that nothing is using: { id } → { id, removed } " +
           "(an attached or dirty or unmerged worktree is refused, never deleted)", ReadOnly: false, Params: [RpcParam.Req("id")]),
            async (req, ct) =>
        {
            var id = req.Required("id");
            var workspace = store.GetWorkspace(id) ?? throw new RpcException("not_found", $"No workspace {id}");
            var (ok, error) = await provisioner.RetireAsync(workspace, busy, ct).ConfigureAwait(false);
            if (!ok) throw new RpcException("workspace_busy", error!);
            return (object?)new { id, removed = true };
        });

        ctx.Rpc.Register(new RpcMethod("workspaces.integrate", "Merge a workspace's branch into the project's branch, serialized per repository: { id, into? } → { id, branch, merged, verified } " +
           "(the worktree's actual branch, refused when the record has gone stale; merged/verified from an ancestry check after the merge). " +
           "Gated: { id, into?, baseFirst?, verify?, verifyShell? (pwsh|cmd|sh), verifyTimeoutSeconds? (900) } refuses while the worker runs or has " +
           "uncommitted work, merges the base into the branch in its worktree, runs verify there, and only then fast-forwards the base → " +
           "{ id, branch, into, baseMerge, verify: { exitCode, timedOut, passed, tail, … }, merged, verified, reason } (a failed verify is an answer with merged false, not an error)", ReadOnly: false, Params: [RpcParam.Req("id"), RpcParam.Opt("into"), RpcParam.Opt("baseFirst", RpcParamType.Boolean), RpcParam.Opt("verify"), RpcParam.Opt("verifyShell"), RpcParam.Opt("verifyTimeoutSeconds", RpcParamType.Integer)]),
            async (req, ct) =>
        {
            var id = req.Required("id");
            var workspace = store.GetWorkspace(id) ?? throw new RpcException("not_found", $"No workspace {id}");
            // The branch to merge and verify: the one the worktree is actually on. A record that has gone stale is
            // refused before anything is merged, so a no-op merge can never be reported as merged and verified.
            var (branch, problem) = provisioner.IntegrateBranch(workspace);
            if (branch is null) throw new RpcException("integration_failed", problem ?? "The workspace cannot be integrated.");
            var into = req.Str("into");
            var baseFirst = req.Bool("baseFirst") ?? false;
            var verify = req.Str("verify");
            if (baseFirst || !string.IsNullOrWhiteSpace(verify))
            {
                var seconds = Math.Clamp(req.Int("verifyTimeoutSeconds") ?? VerifyRunner.DefaultTimeoutSeconds, 1, VerifyRunner.MaxTimeoutSeconds);
                var o = await provisioner.IntegrateGatedAsync(workspace, into, baseFirst, verify, req.Str("verifyShell"),
                    TimeSpan.FromSeconds(seconds), busy, ct).ConfigureAwait(false);
                return new
                {
                    id, branch = o.Branch, into = o.Into, baseMerge = o.BaseMerge,
                    verify = o.Verify is { } v ? new { shell = v.Shell, exitCode = v.ExitCode, timedOut = v.TimedOut, durationMs = v.DurationMs, passed = v.Passed, tail = v.Tail, error = v.StartError } : null,
                    merged = o.Merged, verified = o.Merged, reason = o.Reason,
                };
            }
            var (ok, error) = await provisioner.IntegrateAsync(workspace, into, ct).ConfigureAwait(false);
            if (!ok) throw new RpcException("integration_failed", error!);
            // Ancestry, not "the command said OK": the commit has to be an ancestor of the integration branch afterwards.
            var merged = provisioner.IsAncestor(workspace.Path, branch, into ?? provisioner.ProjectBranchOf(workspace) ?? "master");
            return new { id, branch, merged, verified = merged };
        });

        ctx.Rpc.Register(new RpcMethod("workspaces.canRetire", "Whether a workspace's checkout may be removed, and why not: { id } → { id, canRetire, reason? }", ReadOnly: true, Params: [RpcParam.Req("id")]),
            (req, _) =>
        {
            var id = req.Required("id");
            var workspace = store.GetWorkspace(id) ?? throw new RpcException("not_found", $"No workspace {id}");
            var (ok, reason) = provisioner.CanRetire(workspace, busy);
            return Task.FromResult<object?>(new { id, canRetire = ok, reason });
        });
    }

    private static SessionInfo Session(IPluginContext ctx, string? sessionId) =>
        sessionId is { Length: > 0 } ? ctx.Sessions.Require(sessionId) : null!;

    /// <summary>The root an RPC should work on: an explicit cwd, else the session's workspace root, else its project path.</summary>
    internal static string Root(IPluginContext ctx, WorkspaceResolver resolver, string? sessionId, string? cwd)
    {
        if (cwd is { Length: > 0 })
        {
            var full = Path.GetFullPath(cwd);
            if (!Directory.Exists(full)) throw new RpcException("not_found", $"Directory not found: {full}");
            return full;
        }
        if (sessionId is { Length: > 0 })
        {
            var session = Session(ctx, sessionId);
            return resolver.CwdOf(session);   // throws when the session's workspace is broken: no silent fall back
        }
        return ctx.Paths.DefaultWorkspace;
    }

    private static object Describe(WorkspaceBinding b) => new
    {
        id = b.WorkspaceId,
        workspaceId = b.WorkspaceId,
        name = (string?)null,
        path = b.Root,
        branch = b.Branch,
        baseCommit = b.BaseCommit,
        kind = b.Kind,
        isolated = b.Isolated,
        managed = b.Managed,
        ownerSessionId = b.OwnerSessionId,
        ownerAgentId = b.OwnerAgentId,
        version = b.Version,
    };

    private static object Describe(WorkspaceInfo w) => new
    {
        id = w.Id,
        workspaceId = w.Id,
        name = w.Name,
        path = w.Path,
        projectId = w.ProjectId,
        branch = w.Branch,
        baseCommit = w.BaseCommit,
        kind = w.Kind,
        isolated = w.Kind == "worktree",
        managed = w.Managed,
        ownerSessionId = w.OwnerSessionId,
        ownerAgentId = w.OwnerAgentId,
        createdAt = w.CreatedAt,
        updatedAt = w.UpdatedAt,
    };

    private static string Missing(SessionInfo session) =>
        $"The workspace of this session ({SessionWorkspace.Of(session)}) cannot be used; bind another one with sessions.setWorkspace.";
}