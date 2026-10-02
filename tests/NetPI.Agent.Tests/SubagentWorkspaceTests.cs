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

    private static async Task<SessionInfo?> ChildNamed(TestHost h, string name)
    {
        await Wait.Until(() => h.Sessions.ListSessions(new SessionQuery { IncludeSubagents = true, Limit = 500 }).Any(s => s.Title == name),
            $"subagent {name} created");
        return h.Sessions.ListSessions(new SessionQuery { IncludeSubagents = true, Limit = 500 }).First(s => s.Title == name);
    }

    private static async Task InheritsWorkspace()
    {
        using var repo = new GitRepo();
        if (!repo.Available) { Console.WriteLine("    (no git on PATH: skipped)"); return; }
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
        if (!repo.Available) { Console.WriteLine("    (no git on PATH: skipped)"); return; }
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
        if (!repo.Available) { Console.WriteLine("    (no git on PATH: skipped)"); return; }
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
        if (!repo.Available) { Console.WriteLine("    (no git on PATH: skipped)"); return; }
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
        if (!repo.Available) { Console.WriteLine("    (no git on PATH: skipped)"); return; }
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

    private static bool IsChild(ModelRequest r) =>
        r.Messages.Any(m => m.Role == MessageRole.User && m.Text.Contains("You are \""));

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
            Available = Git(RepoPath, "init", "-q", "-b", "main");
            if (!Available) return;
            File.WriteAllText(Path.Combine(RepoPath, "README.md"), "base\n");
            Git(RepoPath, "add", "-A");
            Git(RepoPath, "-c", "user.name=t", "-c", "user.email=t@t", "commit", "-q", "-m", "init");
        }

        /// <summary>Register the checkout as a project of the host's session store (the tests' sessions belong to it).</summary>
        public void Bind(TestHost h)
        {
            if (!Available) return;
            var project = h.Sessions.CreateProject("ws-repo", RepoPath);
            Id = project.Id;
        }

        /// <summary>What the workspace plugin would do for a writing worker: a worktree and a branch of the project's repository.</summary>
        private static bool Git(string cwd, params string[] args)
        {
            var psi = new System.Diagnostics.ProcessStartInfo("git") { WorkingDirectory = cwd, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            try
            {
                using var p = System.Diagnostics.Process.Start(psi)!;
                p.StandardOutput.ReadToEnd();
                p.StandardError.ReadToEnd();
                p.WaitForExit();
                return p.ExitCode == 0;
            }
            catch (System.ComponentModel.Win32Exception) { return false; }
        }

        private static string? GitOut(string cwd, params string[] args)
        {
            var psi = new System.Diagnostics.ProcessStartInfo("git") { WorkingDirectory = cwd, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            try
            {
                using var p = System.Diagnostics.Process.Start(psi)!;
                var stdout = p.StandardOutput.ReadToEnd();
                p.StandardError.ReadToEnd();
                p.WaitForExit();
                return p.ExitCode == 0 ? stdout.Trim() : null;
            }
            catch (System.ComponentModel.Win32Exception) { return null; }
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