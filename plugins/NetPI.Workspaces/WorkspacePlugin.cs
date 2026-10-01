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
/// <item>The host only stores the records and the session reference, and the guards are in the tools themselves. Every
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
        var store = context.Services.Get<IWorkspaceStore>() ?? context.Services.Require<IWorkspaceStore>();
        var git = new GitProbe();
        var resolver = new WorkspaceResolver(context, store, git);
        var provisioner = new WorkspaceProvisioner(context, store, resolver, git, context.Settings);

        context.Services.Register<IWorkspaceRepoProbe>(git);
        context.Services.Register<IWorkspaceResolver>(resolver);
        var manager = new WorkspaceManager(context, store, resolver, provisioner, git);
        context.Services.Register<IWorkspaceProvisioner>(manager);
        context.Services.Register<IAgentHook>(new WorkspaceGuard(git));
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
                    "Empty: a sibling folder of the project (../Project-name). Otherwise this folder (relative paths are taken from the project)."),
            ],
        });

        RegisterRpcs(context, resolver, provisioner, git);
        context.Logger.LogInformation("Workspaces ready (isolation {Isolation})", provisioner.IsolationEnabled);
        return Task.CompletedTask;
    }

    private static void RegisterRpcs(IPluginContext ctx, WorkspaceResolver resolver, WorkspaceProvisioner provisioner, GitProbe git)
    {
        // A directory is busy when a process the shell registry knows about is running in it: a background process keeps
        // its workspace's ownership, and a workspace with a live process in it is not one to delete under it.
        Func<string, bool>? busy = path =>
        {
            try { return ctx.Services.Get<IWorkspaceProcesses>()?.IsBusyIn(path) == true; }
            catch { return false; }
        };

        ctx.Rpc.RegisterReadOnly("workspaces.resolve", (req, _) =>
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
                available = binding is not null || session?.WorkspaceId is null,
                error = session?.WorkspaceId is { Length: > 0 } && binding is null ? Missing(session) : null,
            });
        }, "What a session's workspace resolves to: { sessionId } → { workspaceId, root, branch, baseCommit, kind, isolated, ownerSessionId, identity, projectId, available, error? }");

        ctx.Rpc.RegisterReadOnly("workspaces.git", async (req, _) =>
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
        }, "Git evidence about a session's workspace root: { sessionId?, cwd? } → { root, topLevel?, commonDir?, branch?, head?, changes, isRepository }");

        ctx.Rpc.RegisterReadOnly("workspaces.listForProject", (req, _) =>
            Task.FromResult<object?>(ctx.Services.Require<IWorkspaceStore>()
                .ListWorkspaces(req.Str("projectId"))
                .Select(Describe)), "The workspaces of a project, with their branches and owners: { projectId } → WorkspaceInfo[]");

        ctx.Rpc.Register("workspaces.create", async (req, ct) =>
        {
            var outcome = await provisioner.ProvisionAsync(new WorkspaceRequest(
                req.Str("projectId"),
                req.Str("name") ?? "workspace",
                req.Str("ownerSessionId"),
                req.Str("ownerAgentId"),
                Isolated: req.Bool("isolated") ?? provisioner.IsolationEnabled,
                Base: req.Str("base")), ct).ConfigureAwait(false);
            if (!outcome.Ok) throw new RpcException("workspace_failed", outcome.Error!);
            return Describe(outcome.Binding!);
        }, "Create a workspace for a worker: { projectId, name, ownerSessionId?, ownerAgentId?, isolated?, base? } → WorkspaceInfo " +
           "(a git project gets a worktree and a branch from a recorded commit; a non-git project gets a plain folder)");

        ctx.Rpc.Register("workspaces.attach", (req, _) =>
        {
            var outcome = provisioner.Attach(req.Required("path"), req.Str("projectId"), req.Str("name") ?? "attached", req.Str("ownerSessionId"));
            if (!outcome.Ok) throw new RpcException("workspace_failed", outcome.Error!);
            return Task.FromResult<object?>(Describe(outcome.Binding!));
        }, "Attach an existing checkout as a workspace: { path, projectId?, name?, ownerSessionId? } → WorkspaceInfo (a checkout of another repository than the project is refused)");

        ctx.Rpc.Register("workspaces.delete", (req, _) =>
        {
            var id = req.Required("id");
            var workspace = ctx.Services.Require<IWorkspaceStore>().GetWorkspace(id) ?? throw new RpcException("not_found", $"No workspace {id}");
            var (ok, error) = provisioner.RetireAsync(workspace, busy).GetAwaiter().GetResult();
            if (!ok) throw new RpcException("workspace_busy", error!);
            return Task.FromResult<object?>(new { id, removed = true });
        }, "Remove a managed workspace whose work is merged or durable and that nothing is using: { id } → { id, removed } " +
           "(an attached or dirty or unmerged worktree is refused, never deleted)");

        ctx.Rpc.Register("workspaces.integrate", async (req, ct) =>
        {
            var id = req.Required("id");
            var workspace = ctx.Services.Require<IWorkspaceStore>().GetWorkspace(id) ?? throw new RpcException("not_found", $"No workspace {id}");
            var (ok, error) = await provisioner.IntegrateAsync(workspace, req.Str("into"), ct).ConfigureAwait(false);
            if (!ok) throw new RpcException("integration_failed", error!);
            // Ancestry, not "the command said OK": the commit has to be an ancestor of the integration branch afterwards.
            var merged = workspace.Branch is { Length: > 0 } branch &&
                         provisioner.IsAncestor(workspace.Path, branch, req.Str("into") ?? provisioner.ProjectBranchOf(workspace) ?? "master");
            return new { id, merged, verified = merged };
        }, "Merge a workspace's branch into the project's branch, serialized per repository: { id, into? } → { id, merged, verified }");

        ctx.Rpc.Register("workspaces.canRetire", (req, _) =>
        {
            var id = req.Required("id");
            var workspace = ctx.Services.Require<IWorkspaceStore>().GetWorkspace(id) ?? throw new RpcException("not_found", $"No workspace {id}");
            var (ok, reason) = provisioner.CanRetire(workspace, busy);
            return Task.FromResult<object?>(new { id, canRetire = ok, reason });
        }, "Whether a workspace's checkout may be removed, and why not: { id } → { id, canRetire, reason? }", readOnly: true);
    }

    private static SessionInfo Session(IPluginContext ctx, string? sessionId) =>
        sessionId is { Length: > 0 } ? ctx.Sessions.GetSession(sessionId) ?? throw new RpcException("not_found", $"No session {sessionId}") : null!;

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
        $"The workspace of this session ({session.WorkspaceId}) cannot be used; bind another one with sessions.setWorkspace.";
}