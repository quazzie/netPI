using System.Text.Json.Nodes;
using NetPI.Workspaces;

namespace NetPI.Agent.Tests;

/// <summary>
/// The workspace half of spawning, with a workspace plugin that provisions against a real temporary git repository:
/// what a child inherits, what a writing child gets of its own, and that a provisioning failure stops the spawn
/// instead of leaving a runnable child in the parent's checkout.
/// </summary>
public static class SubagentWorkspaceTests
{
    public static void Register(TestRunner t)
    {
        t.Add("subagent workspaces: a child inherits its parent's workspace", InheritsWorkspace);
        t.Add("subagent workspaces: isolated: true gives a writing child its own worktree and branch", IsolatedChild);
        t.Add("subagent workspaces: a workspace that does not exist fails the spawn, and no child is created", UnknownWorkspace);
        t.Add("subagent workspaces: a batch with one bad workspace starts none of them", BatchWorkspaceRefused);
        t.Add("subagent workspaces: a failed provisioning leaves no runnable child", ProvisioningFailure);
        t.Add("subagent workspaces: a batch provisions every writing worker's worktree before the first one starts", BatchProvisionsWriters);
        t.Add("subagent workspaces: a batch whose second worktree cannot be made starts neither and releases the first", BatchProvisioningFailure);
        t.Add("subagent workspaces: without the workspace plugin a spawn behaves exactly as before", NoPlugin);
    }

    /// <summary>A host with the workspace plugin loaded (its real store over the plugin's own collection) and a provisioner over a real repository.</summary>
    private static async Task<(TestHost Host, IWorkspaceStore Store)> HostAsync(GitRepo repo)
    {
        var h = await TestHost.StartAsync(x => x.Settings.SetQuiet("agents.a", JsonNode.Parse("""{ "model": "fake/local" }""")));
        await h.StartPluginAsync(new NetPI.Workspaces.WorkspacePlugin());
        var store = h.Services.Get<IWorkspaceStore>()!;
        // The plugin's own manager and provisioner do the work (real git, real worktrees), the store is the real one.
        repo.Bind(h);   // the project the sessions of these tests belong to
        return (h, store);
    }

    /// <summary>
    /// The host with the workspace plugin, as production sees a writer: the tool registry is a service (the host kernel
    /// registers it; the test host does not) and a write tool is registered, so the provisioner's rule isolates a worker
    /// whose tools can write — which is every child spawned without a tools list.
    /// </summary>
    private static async Task<(TestHost Host, IWorkspaceStore Store)> HostWithWritersAsync(GitRepo repo)
    {
        var (h, store) = await HostAsync(repo);
        h.Services.Register<IToolRegistry>(h.Tools);
        h.AddTool(new FakeTool("write", (ctx, args, ct) => Task.FromResult(ToolResult.Ok("written"))));
        return (h, store);
    }

    private static async Task<SessionInfo?> ChildNamed(TestHost h, string name)
    {
        await Wait.Until(() => h.Sessions.ListSessions(new SessionQuery { IncludeSubagents = true, Limit = 500 }).Any(s => s.Title == name),
            $"subagent {name} created");
        return h.Sessions.ListSessions(new SessionQuery { IncludeSubagents = true, Limit = 500 }).First(s => s.Title == name);
    }

    private static async Task InheritsWorkspace()
    {
        using var repo = new GitRepo();
        if (!repo.Available) Check.Skip("no git on PATH");
        var (h, store) = await HostAsync(repo);
        await using var _ = h;
        var parent = h.NewSession(projectId: repo.ProjectId);
        var workspace = new WorkspaceInfo { Id = "wsp_parent", Name = "parent", Path = repo.RepoPath, ProjectId = repo.ProjectId, Kind = "worktree" };
        store.CreateWorkspace(workspace);
        store.SetSessionWorkspace(parent.Id, workspace.Id);

        h.Catalog.Handler = (r, ct) => IsChild(r) || Reply.HasToolResult(r)
            ? Reply.Text("done")
            : Reply.Tool("agent_spawn", new { task = "read", name = "reader", agent = "a" });
        await h.SendAsync(parent.Id, "go");
        await h.IdleAsync(parent.Id);
        var child = await ChildNamed(h, "reader");
        Check.Equal(workspace.Id, SessionWorkspace.Of(child!), "the child did not inherit its parent's workspace");
    }

    private static async Task IsolatedChild()
    {
        using var repo = new GitRepo();
        if (!repo.Available) Check.Skip("no git on PATH");
        var (h, store) = await HostAsync(repo);
        await using var _ = h;
        var parent = h.NewSession(projectId: repo.ProjectId);

        h.Catalog.Handler = (r, ct) => IsChild(r) || Reply.HasToolResult(r)
            ? Reply.Text("child done")
            : Reply.Tool("agent_spawn", new { task = "write", name = "writer", agent = "a", isolated = true });
        await h.SendAsync(parent.Id, "go");
        await h.IdleAsync(parent.Id);
        var child = await ChildNamed(h, "writer");
        Check.True(SessionWorkspace.Of(child) is { Length: > 0 }, "the writing child got no workspace of its own");
        NotEqual(repo.RepoPath, store.GetWorkspace(SessionWorkspace.Of(child)!)!.Path);
        // The workspace is a real worktree on a branch of the project's repository, from its HEAD.
        var record = store.GetWorkspace(SessionWorkspace.Of(child)!)!;
        Check.Equal("worktree", record.Kind);
        Check.True(Directory.Exists(record.Path), "the worktree was not created");
        Check.Equal(child!.Id, record.OwnerSessionId, "the owner is the worker session, not the agent or the slot");
    }

    private static async Task UnknownWorkspace()
    {
        using var repo = new GitRepo();
        if (!repo.Available) Check.Skip("no git on PATH");
        var (h, store) = await HostAsync(repo);
        await using var _ = h;
        var parent = h.NewSession(projectId: repo.ProjectId);
        string? refused = null;
        h.Catalog.Handler = (r, ct) =>
        {
            if (IsChild(r)) return Reply.Text("child done");
            refused = r.Messages[^1].ToolResults.SingleOrDefault()?.Content;
            return Reply.Tool("agent_spawn", new { task = "write", name = "writer", agent = "a", workspace = "wsp_nope" });
        };
        await h.SendAsync(parent.Id, "go");
        await h.IdleAsync(parent.Id);
        await Task.Delay(50);
        Check.Contains(refused ?? "", "wsp_nope");
        Check.Contains(refused ?? "", "None of them was started");
        var children = h.Sessions.ListSessions(new SessionQuery { IncludeSubagents = true, Limit = 500 }).Where(s => s.Kind == "subagent").ToList();
        Check.Equal(0, children.Count, "a refused workspace still created a child session");
    }

    private static async Task BatchWorkspaceRefused()
    {
        using var repo = new GitRepo();
        if (!repo.Available) Check.Skip("no git on PATH");
        var (h, store) = await HostAsync(repo);
        await using var _ = h;
        var parent = h.NewSession(projectId: repo.ProjectId);
        string? refused = null;
        h.Catalog.Handler = (r, ct) =>
        {
            if (IsChild(r)) return Reply.Text("child done");
            refused = r.Messages[^1].ToolResults.SingleOrDefault()?.Content;
            return Reply.Tool("agent_spawn", new
            {
                agent = "a",
                subagents = new object[]
                {
                    new { task = "fine", name = "fine", agent = "a" },
                    new { task = "broken", name = "broken", agent = "a", workspace = "wsp_nope" },
                },
            });
        };
        await h.SendAsync(parent.Id, "go");
        await h.IdleAsync(parent.Id);
        await Task.Delay(50);
        Check.Contains(refused ?? "", "wsp_nope");
        Check.Contains(refused ?? "", "subagents[1]");
        Check.Equal(0, h.Sessions.ListSessions(new SessionQuery { IncludeSubagents = true, Limit = 500 }).Count(s => s.Kind == "subagent"),
            "one bad workspace in the batch still started the others");
    }

    private static async Task ProvisioningFailure()
    {
        using var repo = new GitRepo();
        if (!repo.Available) Check.Skip("no git on PATH");
        await using var h = await TestHost.StartAsync(x => x.Settings.SetQuiet("agents.a", JsonNode.Parse("""{ "model": "fake/local" }""")));
        await h.StartPluginAsync(new NetPI.Workspaces.WorkspacePlugin());
        h.Services.Register<IWorkspaceProvisioner>(new FailingProvisioner(), priority: 10);   // provisioning cannot work here
        repo.Bind(h);
        var parent = h.NewSession(projectId: repo.ProjectId);
        string? refused = null;
        h.Catalog.Handler = (r, ct) =>
        {
            if (IsChild(r)) return Reply.Text("child done");
            if (Reply.HasToolResult(r)) { refused = r.Messages[^1].ToolResults.Last().Content; return Reply.Text("gave up"); }
            return Reply.Tool("agent_spawn", new { task = "write", name = "writer", agent = "a", isolated = true });
        };
        await h.SendAsync(parent.Id, "go");
        await h.IdleAsync(parent.Id);
        await Task.Delay(50);
        Check.Contains(refused ?? "", "no room for a worktree");
        Check.Equal(0, h.Sessions.ListSessions(new SessionQuery { IncludeSubagents = true, Limit = 500 }).Count(s => s.Kind == "subagent"),
            "a failed provisioning still created a runnable child");
    }

    private static async Task BatchProvisionsWriters()
    {
        using var repo = new GitRepo();
        if (!repo.Available) Check.Skip("no git on PATH");
        var (h, store) = await HostWithWritersAsync(repo);
        await using var _ = h;
        var parent = h.NewSession(projectId: repo.ProjectId);
        var worktreesAtFirstChildCall = -1;
        h.Catalog.Handler = (r, ct) =>
        {
            if (IsChild(r))
            {
                // what a child sees at its first model call: the batch made every worktree before any child started
                if (Volatile.Read(ref worktreesAtFirstChildCall) < 0)
                    Volatile.Write(ref worktreesAtFirstChildCall, store.ListWorkspaces().Count(w => w.Kind == "worktree"));
                return Reply.Text("child done");
            }
            if (Reply.HasToolResult(r)) return Reply.Text("parent done");
            return Reply.Tool("agent_spawn", new
            {
                subagents = new object[]
                {
                    new { task = "write one", name = "one", agent = "a" },
                    new { task = "write two", name = "two", agent = "a" },
                },
            });
        };
        await h.SendAsync(parent.Id, "go");
        await h.IdleAsync(parent.Id);
        Check.Equal("parent done", h.Messages(parent.Id)[^1].Text);
        var one = await ChildNamed(h, "one");
        var two = await ChildNamed(h, "two");
        Check.True(SessionWorkspace.Of(one) is { Length: > 0 } && SessionWorkspace.Of(two) is { Length: > 0 }, "a default child that can write got a workspace of its own");
        var w1 = store.GetWorkspace(SessionWorkspace.Of(one)!)!;
        var w2 = store.GetWorkspace(SessionWorkspace.Of(two)!)!;
        Check.Equal("worktree", w1.Kind, "a writer's own checkout is a worktree");
        Check.Equal("worktree", w2.Kind);
        NotEqual(w1.Path, w2.Path, "each writer has its own checkout");
        Check.True(Directory.Exists(w1.Path) && Directory.Exists(w2.Path), "the worktrees exist");
        Check.Equal(one!.Id, w1.OwnerSessionId, "the worker session owns its workspace");
        Check.Equal(two!.Id, w2.OwnerSessionId);
        Check.Equal(2, worktreesAtFirstChildCall, "both worktrees were there before the first child's first model call");
    }

    private static async Task BatchProvisioningFailure()
    {
        using var repo = new GitRepo();
        if (!repo.Available) Check.Skip("no git on PATH");
        var (h, store) = await HostWithWritersAsync(repo);
        await using var _ = h;
        var parent = h.NewSession(projectId: repo.ProjectId);
        // The second worker's worktree folder is taken: its provisioning fails after the first worker's worktree was made.
        Directory.CreateDirectory(Path.Combine(repo.RepoPath, ".worktrees", "two"));
        string? refused = null;
        h.Catalog.Handler = (r, ct) =>
        {
            if (IsChild(r)) return Reply.Text("child done");
            if (Reply.HasToolResult(r)) { refused = r.Messages[^1].ToolResults.Last().Content; return Reply.Text("gave up"); }
            return Reply.Tool("agent_spawn", new
            {
                subagents = new object[]
                {
                    new { task = "write one", name = "one", agent = "a" },
                    new { task = "write two", name = "two", agent = "a" },
                },
            });
        };
        await h.SendAsync(parent.Id, "go");
        await h.IdleAsync(parent.Id);
        Check.Contains(refused ?? "", "subagents[1]");
        Check.Contains(refused ?? "", "already exists");
        Check.Contains(refused ?? "", "None of them was started");
        Check.Equal(0, h.Sessions.ListSessions(new SessionQuery { IncludeSubagents = true, Limit = 500 }).Count(s => s.Kind == "subagent"),
            "a failed provisioning in the batch still started a child");
        Check.Equal(0, store.ListWorkspaces().Count, "the worktree made for the first worker was released");
        Check.False(Directory.Exists(Path.Combine(repo.RepoPath, ".worktrees", "one")), "the first worker's worktree is gone from disk");
    }

    private static async Task NoPlugin()
    {
        await using var h = await TestHost.StartAsync(x => x.Settings.SetQuiet("agents.a", JsonNode.Parse("""{ "model": "fake/local" }""")));
        var parent = h.NewSession();
        h.Catalog.Handler = (r, ct) => IsChild(r) || Reply.HasToolResult(r)
            ? Reply.Text("done")
            : Reply.Tool("agent_spawn", new { task = "t", name = "plain", agent = "a", isolated = true });
        await h.SendAsync(parent.Id, "go");
        await h.IdleAsync(parent.Id);
        var child = await ChildNamed(h, "plain");
        Check.Equal(null, SessionWorkspace.Of(child!), "without the workspace plugin nothing should be bound");
    }

    private static bool IsChild(ModelRequest r) => r.SystemPrompt?.Contains("a subagent working for") == true;

    /// <summary>The same as <see cref="Check.Equal"/> for values that must differ.</summary>
    private static void NotEqual<T>(T unexpected, T actual, string? message = null) => Check.False(EqualityComparer<T>.Default.Equals(unexpected, actual), message ?? $"should differ from: {actual}");

    // ------------------------------------------------------------------ doubles

    private sealed class FailingProvisioner : IWorkspaceProvisioner
    {
        public Task<WorkspaceOutcome> ForChildAsync(SpawnRequest request, SessionInfo? parentSession, string childSessionId, string childName, CancellationToken ct) =>
            Task.FromResult(new WorkspaceOutcome(null, "no room for a worktree"));
    }

    /// <summary>A real repository in a temporary directory, with a commit to branch from.</summary>
    private sealed class GitRepo : IDisposable
    {
        /// <summary>The repository's checkout (named RepoPath so it does not shadow System.IO.Path inside this class).</summary>
        public string RepoPath { get; }
        public string Id { get; private set; } = "prj_ws";
        public string ProjectId => Id;
        public bool Available { get; }

        public GitRepo()
        {
            RepoPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "netpi-agent-tests", "ws-repo-" + Ids.Short(8));
            Directory.CreateDirectory(RepoPath);
            Available = TestGit.Run(RepoPath, "init", "-q", "-b", "main");
            if (!Available) return;
            File.WriteAllText(Path.Combine(RepoPath, "README.md"), "base\n");
            TestGit.Run(RepoPath, "add", "-A");
            TestGit.Run(RepoPath, "-c", "user.name=t", "-c", "user.email=t@t", "commit", "-q", "-m", "init");
        }

        /// <summary>Register the checkout as a project of the host's session store (the tests' sessions belong to it).</summary>
        public void Bind(TestHost h)
        {
            if (!Available) return;
            var project = h.Sessions.CreateProject("ws-repo", RepoPath);
            Id = project.Id;
        }

        public void Dispose()
        {
            try { Directory.Delete(RepoPath, true); } catch { }
            foreach (var dir in Directory.EnumerateDirectories(Path.GetDirectoryName(RepoPath)!, "repo-*"))
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }
    }
}