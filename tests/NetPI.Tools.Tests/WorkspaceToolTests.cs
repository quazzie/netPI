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
            var ctx = new FakePluginContext(env.Project);
            ctx.SessionsFake = env.Sessions;
            ctx.Services.Register(env.Resolver);
            await new FilesPlugin().StartAsync(ctx, CancellationToken.None);
            var session = env.Session();

            Check.Equal(env.Project, FilesPlugin.ResolveRoot(ctx, Req(new { sessionId = session.Id })));
            var workspace = env.Workspace("wt", T.TempDir("wt"));
            env.Bind(session, workspace.Id);
            Check.Equal(workspace.Path, FilesPlugin.ResolveRoot(ctx, Req(new { sessionId = session.Id })));

            // The scope the tab keys its refreshes on names the root, the workspace and a version.
            var scope = NetPiJson.ToElement(await ctx.RpcFake.InvokeAsync("files.scope", new { sessionId = session.Id }));
            Check.Equal(workspace.Path, scope.GetProperty("root").GetString());
            Check.Equal(workspace.Id, scope.GetProperty("workspaceId").GetString());
            Check.True(scope.GetProperty("identity").GetString()!.Contains(workspace.Id));
            // An explicit cwd still wins (the user asked for that directory).
            Check.Equal(env.Project, FilesPlugin.ResolveRoot(ctx, Req(new { sessionId = session.Id, cwd = env.Project })));
        });

        r.Add("workspace: a session whose workspace cannot be used fails the files RPC instead of showing the project", () =>
        {
            using var env = new Env();
            var ctx = new FakePluginContext(env.Project);
            ctx.SessionsFake = env.Sessions;
            ctx.Services.Register(env.Resolver);
            var session = env.Session();
            env.Bind(session, "wsp_gone");
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
    }

    private static RpcRequest Req(object parameters) => new()
    {
        Method = "files.list",
        Params = JsonSerializer.SerializeToElement(parameters),
    };

    /// <summary>A project, a resolver over it, and the sessions bound to it.</summary>
    private sealed class Env : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "netpi-tests", "ws-" + Guid.NewGuid().ToString("N")[..8]);
        public string Project { get; }
        private readonly Resolver _resolver;

        public Env()
        {
            Project = Path.Combine(Root, "repo");
            Directory.CreateDirectory(Project);
            _resolver = new Resolver(Project);
        }

        public Resolver Sessions => _resolver;
        public IWorkspaceResolver Resolver => _resolver;

        public SessionInfo Session() => _resolver.CreateSession(new SessionInfo { Title = "s", ProjectId = _resolver.ProjectId });

        public WorkspaceInfo Workspace(string name, string path)
        {
            Directory.CreateDirectory(path);
            return _resolver.CreateWorkspace(new WorkspaceInfo { Name = name, Path = path, ProjectId = _resolver.ProjectId, Kind = "worktree" });
        }

        public void Bind(SessionInfo session, string workspaceId)
        {
            _resolver.SetSessionWorkspace(session.Id, workspaceId);
            session.WorkspaceId = workspaceId;   // this store returns the same object it holds
        }

        public void Dispose() { try { Directory.Delete(Root, true); } catch { } }
    }

    /// <summary>The minimum the file and shell surfaces ask of a session store, with a workspace resolver over it.</summary>
    private sealed class Resolver(string projectPath) : ISessionStore, IWorkspaceStore, IWorkspaceResolver
    {
        private readonly List<(ProjectInfo, SessionInfo, WorkspaceInfo)> _rows = [];
        private long _n;

        public ProjectInfo Project { get; } = new() { Id = "prj_1", Name = "Repo", Path = projectPath };
        public string ProjectId => Project.Id;
        public ISessionStore Sessions => this;

        public WorkspaceBinding? Resolve(SessionInfo? session)
        {
            if (session?.WorkspaceId is not { Length: > 0 } id) return null;
            var w = GetWorkspace(id) ?? throw new WorkspaceUnavailableException($"workspace {id} is gone");
            return new WorkspaceBinding(w.Id, w.Path, w.Branch, w.BaseCommit, w.RepoCommonDir, w.Kind, w.OwnerSessionId, w.OwnerAgentId, w.Managed, 1);
        }

        public WorkspaceBinding? ResolveById(string workspaceId) => GetWorkspace(workspaceId) is { } w ? Resolve(new SessionInfo { WorkspaceId = workspaceId }) : null;
        public WorkspaceBinding Require(SessionInfo session) => Resolve(session)!;
        public string CwdOf(SessionInfo session) => Resolve(session)?.Root ?? GetCwd(session);
        public string IdentityOf(SessionInfo session) => session.WorkspaceId ?? $"project:{session.ProjectId}";

        public IReadOnlyList<ProjectInfo> ListProjects() => [Project];
        public ProjectInfo? GetProject(string id) => id == Project.Id ? Project : null;
        public ProjectInfo CreateProject(string name, string path) => throw new NotSupportedException();
        public ProjectInfo UpdateProject(string id, string? name, string? path) => throw new NotSupportedException();
        public void DeleteProject(string id) => throw new NotSupportedException();

        public IReadOnlyList<SessionInfo> ListSessions(SessionQuery query) => [.. _rows.Select(r => r.Item2)];
        public SessionInfo? GetSession(string id) => _rows.FirstOrDefault(r => r.Item2.Id == id).Item2;
        public SessionInfo CreateSession(SessionInfo template)
        {
            if (string.IsNullOrEmpty(template.Id)) template.Id = "ses_" + ++_n;
            _rows.Add((Project, template, null));
            return template;
        }
        public SessionInfo UpdateSession(string id, Action<SessionInfo> mutate)
        {
            var s = GetSession(id)!;
            mutate(s);
            return s;
        }
        public void DeleteSession(string id) => _rows.RemoveAll(r => r.Item2.Id == id);
        public SessionInfo SetSessionProject(string sessionId, string? projectId) => UpdateSession(sessionId, s => s.ProjectId = projectId);
        public string GetCwd(SessionInfo session) =>
            session.WorkspaceId is { Length: > 0 } id && GetWorkspace(id) is { } w ? w.Path
            : session.ProjectId is { } p && GetProject(p) is { } proj ? proj.Path : Path.GetTempPath();

        public ChatMessage AppendMessage(string sessionId, ChatMessage message) => message;
        public void UpdateMessage(ChatMessage message) { }
        public ChatMessage? GetMessage(long id) => null;
        public IReadOnlyList<ChatMessage> GetMessages(string sessionId, long? beforeSeq = null, int? limit = null) => [];
        public IReadOnlyList<ChatMessage> GetContextMessages(string sessionId) => [];
        public void MarkCompacted(string sessionId, long upToSeq) { }

        public IReadOnlyList<WorkspaceInfo> ListWorkspaces(string? projectId = null) => [.. _rows.Where(r => r.Item3 is not null).Select(r => r.Item3!)];
        public WorkspaceInfo? GetWorkspace(string id) => _rows.FirstOrDefault(r => r.Item3?.Id == id).Item3;
        public WorkspaceInfo CreateWorkspace(WorkspaceInfo template)
        {
            if (string.IsNullOrEmpty(template.Id)) template.Id = "wsp_" + ++_n;
            _rows.Add((Project, new SessionInfo(), template));
            return template;
        }
        public WorkspaceInfo UpdateWorkspace(string id, Action<WorkspaceInfo> mutate)
        {
            var w = GetWorkspace(id)!;
            mutate(w);
            return w;
        }
        public bool DeleteWorkspace(string id) => _rows.RemoveAll(r => r.Item3?.Id == id) > 0;
        public WorkspaceInfo? GetSessionWorkspace(string sessionId) => GetSession(sessionId)?.WorkspaceId is { } id ? GetWorkspace(id) : null;
        public void SetSessionWorkspace(string sessionId, string? workspaceId) => UpdateSession(sessionId, s => s.WorkspaceId = workspaceId);
    }
}