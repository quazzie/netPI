using System.Text.Json;
using NetPI.Tools.Files;
using NetPI.Tools.Shell;

namespace NetPI.Tools.Tests;

/// <summary>
/// What the file and shell surfaces do when a session is bound to a workspace: they work in that checkout, they report
/// where they actually ran, and they refuse a native write that points into another checkout of the same repository.
/// The workspace plugin's own behavior (provisioning, integration, cleanup) is covered in NetPI.Aux.Tests.
/// </summary>
public static class WorkspaceToolTests
{
    public static void Register(TestRunner r)
    {
        r.Add("workspace: the files RPCs answer with the session's workspace root, not the project's", async () =>
        {
            using var env = new Env();
            var ctx = env.Context;
            ctx.Services.Register(env.Resolver);
            await new FilesPlugin().StartAsync(ctx, CancellationToken.None);
            var session = env.Session();

            Check.Equal(env.Project, FilesPlugin.ResolveRoot(ctx, Req(new { sessionId = session.Id })));
            var workspace = env.Workspace("wt", T.TempDir("wt"));
            env.Bind(session, workspace.Id);
            Check.Equal(workspace.Path, FilesPlugin.ResolveRoot(ctx, Req(new { sessionId = session.Id })));

            // The scope the tab keys its refreshes on names the root, the workspace and a version.
            var scope = NetPiJson.ToElement(await env.Fake.RpcFake.InvokeAsync("files.scope", new { sessionId = session.Id }));
            Check.Equal(workspace.Path, scope.GetProperty("root").GetString());
            Check.Equal(workspace.Id, scope.GetProperty("workspaceId").GetString());
            Check.True(scope.GetProperty("identity").GetString()!.Contains(workspace.Id));
            // An explicit cwd still wins (the user asked for that directory).
            Check.Equal(env.Project, FilesPlugin.ResolveRoot(ctx, Req(new { sessionId = session.Id, cwd = env.Project })));
        });

        r.Add("workspace: a session whose workspace cannot be used fails the files RPC instead of showing the project", () =>
        {
            using var env = new Env();
            var ctx = env.Context;
            ctx.Services.Register(env.Resolver);
            var session = env.Session();
            env.Bind(session, "wsp_gone", cwd: env.Project);
            var ex = Check.Throws<RpcException>(() => FilesPlugin.ResolveRoot(ctx, Req(new { sessionId = session.Id })));
            Check.Equal("workspace_unavailable", ex.Code);
        });

        r.Add("workspace: the file tools refuse a write into another checkout of the same repository", async () =>
        {
            using var env = new Env();
            var binding = new WorkspaceBinding("wsp_1", Path.Combine(env.Project, "wt"), "netpi/w", null, Path.Combine(env.Project, ".git"), "worktree", "ses_owner", null, true, 1);
            Directory.CreateDirectory(binding.Root);
            // The probe is what decides "another checkout of the same repository" (git's common dir); a fake one answers it here.
            T.Services.Register<IWorkspaceRepoProbe>(new SameRepoProbe(Path.Combine(env.Project, ".git")));
            var ctx = T.Ctx(binding.Root, binding);
            var outside = Path.Combine(env.Project, "victim.txt");
            var write = await new WriteTool().ExecuteAsync(ctx, T.Args(new { path = outside, content = "x" }), CancellationToken.None);
            Check.True(write.IsError, "a write into the primary checkout was allowed");
            Check.Contains(write.Content, "Refused");
            Check.False(File.Exists(outside));

            // Inside the workspace it writes; outside the repository it is ordinary work and is allowed.
            var own = await new WriteTool().ExecuteAsync(ctx, T.Args(new { path = "inside.txt", content = "x" }), CancellationToken.None);
            Check.False(own.IsError, own.Content);
            var temp = Path.Combine(Path.GetTempPath(), "netpi-outside.txt");
            var elsewhere = await new WriteTool().ExecuteAsync(ctx, T.Args(new { path = temp, content = "x" }), CancellationToken.None);
            Check.False(elsewhere.IsError, elsewhere.Content);
            File.Delete(temp);

            // A session with no workspace is the pre-workspace behavior: nothing is refused.
            var plain = T.Ctx(env.Project);
            var allowed = await new WriteTool().ExecuteAsync(plain, T.Args(new { path = outside, content = "x" }), CancellationToken.None);
            Check.False(allowed.IsError, allowed.Content);
        });

        r.Add("workspace: the shell reports the directory it actually ran in, and says so when it is not the workspace", async () =>
        {
            using var env = new Env();
            var service = new ShellService(new ProcessRegistry(new FakeBus()), null);
            var binding = new WorkspaceBinding("wsp_1", Path.Combine(env.Project, "wt"), null, null, null, "worktree", "ses_owner", null, true, 1);
            var ctx = T.Ctx(binding.Root, binding);
            Directory.CreateDirectory(binding.Root);

            var here = await service.RunAsync("bash", ctx, new NetPI.Tools.Shell.ToolArgs(JsonSerializer.SerializeToElement(new { command = "pwd" })), CancellationToken.None);
            Check.False(here.IsError, here.Content);
            Check.Equal(binding.Root, T.D(here).Str("cwd"));
            Check.Equal("wsp_1", T.D(here).Str("workspaceId"));

            var elsewhere = await service.RunAsync("bash", ctx,
                new NetPI.Tools.Shell.ToolArgs(JsonSerializer.SerializeToElement(new { command = "pwd", cwd = env.Project })), CancellationToken.None);
            Check.False(elsewhere.IsError, elsewhere.Content);
            Check.Equal(env.Project, T.D(elsewhere).Str("cwd"));
            Check.Contains(elsewhere.Content, "not in this session's workspace");
            Check.Contains(elsewhere.Content, "not a sandbox");
        });
    }

    /// <summary>A probe that answers the same repository for the project and its worktrees, and none for anywhere else.</summary>
    private sealed class SameRepoProbe(string commonDir) : IWorkspaceRepoProbe
    {
        public string? CommonDirOf(string path) =>
            path.StartsWith(commonDir[..commonDir.LastIndexOf(Path.DirectorySeparatorChar)], StringComparison.OrdinalIgnoreCase) ? commonDir : null;
        public string? BranchOf(string path) => "main";
        public string? HeadOf(string path) => "abc123";
        public string? ProbeProblem(string path) => null;   // this fake always answers
    }

    private static RpcRequest Req(object parameters) => new()
    {
        Method = "files.list",
        Params = JsonSerializer.SerializeToElement(parameters),
    };

    /// <summary>A project, a workspace record, a resolver over them, and the real session service that holds the binding.</summary>
    private sealed class Env : IDisposable
    {
        private readonly FakePluginContext _ctx;
        private readonly Resolver _resolver;

        public string Root { get; } = Path.Combine(Path.GetTempPath(), "netpi-tests", "ws-" + Guid.NewGuid().ToString("N")[..8]);
        public string Project { get; }
        public string ProjectId { get; }

        public Env()
        {
            Project = Path.Combine(Root, "repo");
            Directory.CreateDirectory(Project);
            _ctx = new FakePluginContext(Project, home: Root);
            // The session service is the kernel's own, over the memory provider: a binding is meta on a real session.
            ProjectId = _ctx.Store.CreateProject("Repo", Project).Id;
            _resolver = new Resolver(_ctx);
        }

        public IPluginContext Context => _ctx;
        /// <summary>The context as the fake, for the registries a test drives (the RPC registry, the tool registry).</summary>
        public FakePluginContext Fake => _ctx;
        public IWorkspaceResolver Resolver => _resolver;

        public SessionInfo Session() => _ctx.Store.CreateSession(new SessionInfo { Title = "s", ProjectId = ProjectId });

        public WorkspaceInfo Workspace(string name, string path)
        {
            Directory.CreateDirectory(path);
            return _resolver.Create(new WorkspaceInfo { Name = name, Path = path, ProjectId = ProjectId, Kind = "worktree" });
        }

        /// <summary>
        /// Bind the session the way the Workspaces plugin does — <c>meta.workspaceId</c> and <c>meta.cwd</c> — through the
        /// session service, so the file and shell surfaces read exactly what production writes.
        /// </summary>
        public void Bind(SessionInfo session, string workspaceId, string? cwd = null)
        {
            var root = cwd ?? _resolver.Get(workspaceId)?.Path;
            _ctx.Store.UpdateSession(session.Id, s =>
            {
                s.Meta ??= new System.Text.Json.Nodes.JsonObject();
                s.Meta[SessionWorkspace.MetaKey] = workspaceId;
                if (root is not null) s.Meta[SessionCwd.MetaKey] = root;
                else s.Meta.Remove(SessionCwd.MetaKey);
            });
        }

        public void Dispose() { _ctx.Dispose(); try { Directory.Delete(Root, true); } catch { } }
    }

    /// <summary>
    /// The workspace records a test binds a session to, and the resolver the file and shell surfaces ask. A workspace
    /// is a plugin's own concept (a collection), so a record set is a fair double here; what must not be a double is
    /// the session side, and that is the kernel's own <see cref="SessionService"/> (see <see cref="Env"/>).
    /// </summary>
    private sealed class Resolver(IPluginContext ctx) : IWorkspaceResolver
    {
        private readonly List<WorkspaceInfo> _workspaces = [];
        private long _n;

        public WorkspaceInfo? Get(string id) => _workspaces.FirstOrDefault(w => w.Id == id);

        public WorkspaceInfo Create(WorkspaceInfo template)
        {
            if (string.IsNullOrEmpty(template.Id)) template.Id = "wsp_" + ++_n;
            template.CreatedAt = template.UpdatedAt = DateTimeOffset.UtcNow;
            _workspaces.Add(template);
            return template;
        }

        /// <summary>
        /// The session's folder. Bound means this workspace's root, and a bound workspace that cannot be used is a
        /// <see cref="WorkspaceUnavailableException"/> — never the project folder. Unbound is the core's own rule
        /// (<c>meta.cwd</c>, else the project, else the default folder).
        /// </summary>
        public string CwdOf(SessionInfo session) =>
            SessionWorkspace.Of(session) is not null ? Require(session).Root : ctx.Sessions.GetCwd(session);

        public WorkspaceBinding? Resolve(SessionInfo? session)
        {
            if (SessionWorkspace.Of(session) is not { } id) return null;
            var w = Get(id) ?? throw new WorkspaceUnavailableException($"workspace {id} is gone");
            return Bind(w);
        }

        public WorkspaceBinding? ResolveLenient(SessionInfo? session)
        {
            try { return Resolve(session); }
            catch (WorkspaceUnavailableException) { return null; }
        }

        public WorkspaceBinding? ResolveById(string workspaceId) => Get(workspaceId) is { } w ? Bind(w) : null;

        public WorkspaceBinding Require(SessionInfo session) => Resolve(session)
            ?? throw new WorkspaceUnavailableException("this session has no workspace");

        public string IdentityOf(SessionInfo session) =>
            SessionWorkspace.Of(session) is { } id ? $"workspace:{id}" : $"project:{session.ProjectId}";

        private static WorkspaceBinding Bind(WorkspaceInfo w) =>
            new(w.Id, w.Path, w.Branch, w.BaseCommit, w.RepoCommonDir, w.Kind, w.OwnerSessionId, w.OwnerAgentId, w.Managed, 1);
    }
}