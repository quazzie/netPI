using System.Text.Json;
using System.Text.Json.Nodes;
using NetPI.Runtime;
using NetPI.Tools.Ssh;
using NetPI.Workspaces;

namespace NetPI.Aux.Tests;

/// <summary>
/// Workspaces, end to end, against a real git repository: provisioning a worktree per writing worker, two workers
/// editing the same file in their own checkouts, refusing a write that reaches into another one, refusing a workspace
/// that does not exist, serializing integrations and never deleting work that is not durable.
/// <para>
/// The repositories are built with real <c>git</c> (a fake <c>.git</c> cannot answer <c>--git-common-dir</c>, which is
/// the evidence the whole isolation decision rests on), and every test skips itself with a note when git is missing.
/// </para>
/// </summary>
public static class WorkspaceTests
{
    public static void Register(TestRunner t)
    {
        t.Add("workspaces: two writing workers edit the same file in separate worktrees", TwoWriters);
        t.Add("workspaces: the primary checkout is untouched while its workers work", PrimaryUntouched);
        t.Add("workspaces: a stale absolute path into the primary checkout is refused for a mutation", StalePathRefused);
        t.Add("workspaces: a missing workspace fails with a message and no fall back", MissingWorkspaceFails);
        t.Add("workspaces: a workspace of another repository is refused", OtherRepositoryRefused);
        t.Add("workspaces: provisioning starts from a recorded commit and leaves the parent's dirty changes behind", CleanStart);
        t.Add("workspaces: a second assignment to the same worker reuses its workspace", ReusePerWorker);
        t.Add("workspaces: worktrees live inside the project (.worktrees), and the primary checkout stays clean", WorktreesInsideProject);
        t.Add("workspaces: a non-git project gets a plain folder, with no git workflow", NonGitProject);
        t.Add("workspaces: a failed provisioning leaves no runnable workspace", FailedProvisioning);
        t.Add("workspaces: integrations into one branch serialize", IntegrationSerializes);
        t.Add("workspaces: cleanup refuses a dirty, unbound or running worktree and removes a merged one", CleanupSafety);
        t.Add("workspaces: a background process keeps its workspace busy", BackgroundProcessHoldsWorkspace);
        t.Add("workspaces: the guard is per spelling: relative, absolute, .. and a symlink into another checkout", GuardSpellings);
        t.Add("workspaces: the guard reads arguments as the tools do: names, order, string-encoded arguments, an ssh download's destination", GuardArguments);
        t.Add("workspaces: ssh copy refuses a download into another checkout of the repository and allows one into the worker's own", SshDownloadRefused);
        t.Add("workspaces: the notice names the checkout, the branch and what a write outside it does", NoticeText);
        t.Add("workspaces: a switch asked for mid-batch is applied at the next model call", DeferredSwitch);
        t.Add("workspaces: the switch is in the next model call's batch and guard, not one call later", SwitchVisibleToNextModelCall);
        t.Add("workspaces: every consumer agrees on the assigned root", ConsumersAgree);
        t.Add("workspaces: the identity changes with the binding, so a stale answer is recognizably stale", IdentityChanges);
    }

    // ------------------------------------------------------------------ the fixture

    /// <summary>A real repository, the workspace plugin loaded over a fake session store, and the tools it guards.</summary>
    private sealed class Env : IDisposable
    {
        public FakePluginContext Ctx { get; }
        public WorkspacePlugin Plugin { get; }
        public WorkspaceResolver Resolver { get; }
        public WorkspaceProvisioner Provisioner { get; }
        public WorkspaceManager Manager { get; }
        public GitProbe Git { get; }

        public string Root { get; }
        public string ProjectPath { get; }
        public ProjectInfo Project { get; }
        public bool GitAvailable { get; }

        public Env(bool git = true, bool isolate = true)
        {
            Ctx = new FakePluginContext(T.TempDir("ws-home"));
            Ctx.Settings.Set("workspaces.isolateWriters", JsonValue.Create(isolate));
            Root = Path.GetDirectoryName(Ctx.Paths.Home)!;
            ProjectPath = Path.Combine(Root, "repo");
            Directory.CreateDirectory(ProjectPath);
            Git = new GitProbe(TimeSpan.Zero);
            GitAvailable = git && Git_(ProjectPath, "init", "-q", "-b", "main");
            if (GitAvailable)
            {
                File.WriteAllText(Path.Combine(ProjectPath, "README.md"), "base\n");
                Git_(ProjectPath, "add", "-A");
                Git_(ProjectPath, "-c", "user.name=t", "-c", "user.email=t@t", "commit", "-q", "-m", "init");
            }
            Project = Ctx.SessionsFake.CreateProject("Repo", ProjectPath);
            Ctx.Services.Register<IWorkspaceStore>(new MemoryWorkspaceStore(Ctx.SessionsFake));
            Plugin = new WorkspacePlugin();
            Plugin.StartAsync(Ctx, CancellationToken.None).GetAwaiter().GetResult();
            Resolver = (WorkspaceResolver)Ctx.Services.Get<IWorkspaceResolver>()!;
            Provisioner = (WorkspaceProvisioner)Ctx.Services.Get<WorkspaceProvisioner>()!;
            Manager = (WorkspaceManager)Ctx.Services.Get<IWorkspaceProvisioner>()!;
        }

        /// <summary>A session of the project, optionally bound to a workspace.</summary>
        public SessionInfo Session(string title = "s", string? workspaceId = null) =>
            Ctx.SessionsFake.CreateSession(new SessionInfo { Title = title, ProjectId = Project.Id, WorkspaceId = workspaceId });

        /// <summary>Give a worker its own workspace, exactly as a spawn would (with a parent session of the project).</summary>
        public WorkspaceOutcome Provision(string name, string sessionId, bool isolated = true) =>
            Manager.ForChildAsync(new SpawnRequest { Task = "t", Name = name, Isolated = isolated, WorkspaceOwnerSessionId = sessionId },
                Session("parent-" + sessionId), sessionId, name, CancellationToken.None).GetAwaiter().GetResult();

        public string Write(string dir, string relative, string content)
        {
            var path = Path.Combine(dir, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
            return path;
        }

        public ToolContext Context(SessionInfo session, WorkspaceBinding? binding = null) => new()
        {
            SessionId = session.Id, AgentId = "agt_test", CallId = "call_1",
            Cwd = binding?.Root ?? ProjectPath, Project = Project, Workspace = binding,
            Services = Ctx.Services, Events = Ctx.Events,
        };

        /// <summary>Run the write tool against a session and say whether it was refused.</summary>
        public (ToolResult Result, string Full) Write_(SessionInfo session, WorkspaceBinding? binding, string pathArg)
        {
            var full = new ToolContext
            {
                SessionId = session.Id, AgentId = "agt", CallId = "c", Cwd = binding?.Root ?? ProjectPath,
                Project = Project, Workspace = binding, Services = Ctx.Services, Events = Ctx.Events,
            }.ResolvePath(pathArg);
            var tool = new NetPI.Tools.Files.WriteTool(Ctx.Settings);
            var result = tool.ExecuteAsync(Context(session, binding), Json(new { path = pathArg, content = "x" }), CancellationToken.None)
                .GetAwaiter().GetResult();
            return (result, full);
        }

        public void Dispose()
        {
            Ctx.Unload();
            try { Directory.Delete(Root, recursive: true); } catch { }
        }
    }

    private static JsonElement Json(object o) => JsonSerializer.SerializeToElement(o);

    private static bool Git_(string cwd, params string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("git")
        { WorkingDirectory = cwd, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
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
        var psi = new System.Diagnostics.ProcessStartInfo("git")
        { WorkingDirectory = cwd, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
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

    private static void Skip(string what) => Console.WriteLine($"    (no git on PATH: {what} not checked)");

    // ------------------------------------------------------------------ the tests

    private static Task TwoWriters()
    {
        using var env = new Env();
        if (!env.GitAvailable) { Skip("two writers"); return Task.CompletedTask; }

        var a = env.Provision("writer-a", "ses_a");
        var b = env.Provision("writer-b", "ses_b");
        Check.True(a.Ok, a.Error ?? "");
        Check.True(b.Ok, b.Error ?? "");
        Check.Differs(a.Binding!.Root, b.Binding!.Root);
        Check.Differs(a.Binding!.Branch, b.Binding!.Branch);

        // Both edit the same relative filename in their own checkout.
        var sessionA = env.Session("a", a.Binding.WorkspaceId);
        var sessionB = env.Session("b", b.Binding.WorkspaceId);
        env.Write(a.Binding.Root, "shared.txt", "from A");
        env.Write(b.Binding.Root, "shared.txt", "from B");

        Check.Equal("from A", File.ReadAllText(Path.Combine(env.Resolver.CwdOf(sessionA), "shared.txt")));
        Check.Equal("from B", File.ReadAllText(Path.Combine(env.Resolver.CwdOf(sessionB), "shared.txt")));

        // And through the tools, each resolving the same relative path in its own workspace.
        var writeA = env.Write_(sessionA, a.Binding, "shared.txt");
        var writeB = env.Write_(sessionB, b.Binding, "shared.txt");
        Check.False(writeA.Result.IsError, writeA.Result.Content);
        Check.False(writeB.Result.IsError, writeB.Result.Content);
        Check.Equal("x", File.ReadAllText(Path.Combine(a.Binding.Root, "shared.txt")));
        Check.Equal("x", File.ReadAllText(Path.Combine(b.Binding.Root, "shared.txt")));
        return Task.CompletedTask;
    }

    private static Task PrimaryUntouched()
    {
        using var env = new Env();
        if (!env.GitAvailable) { Skip("primary untouched"); return Task.CompletedTask; }
        var w = env.Provision("w", "ses_w");
        Check.True(w.Ok, w.Error ?? "");
        env.Write(w.Binding!.Root, "only-here.txt", "worker's");

        Check.False(File.Exists(Path.Combine(env.ProjectPath, "only-here.txt")), "the worker's file landed in the primary checkout");
        Check.Equal("", GitOut(env.ProjectPath, "status", "--porcelain") ?? "", "the primary checkout has pending changes");
        // The worktree's changes are its own, visible from its own status.
        Check.Contains(GitOut(w.Binding.Root, "status", "--porcelain") ?? "", "only-here.txt");
        return Task.CompletedTask;
    }

    /// <summary>The hook judges the path the tool will use: names match as the tools match them, the first name the tool
    /// tries wins, a string-encoded arguments object is unwrapped, and an ssh download's destination counts as a write.</summary>
    private static Task GuardArguments()
    {
        static JsonObject O(string json) => (JsonObject)JsonNode.Parse(json)!;

        // the arguments object sent as a JSON string, as some models do: the tools unwrap it, so the guard must
        var wrapped = WorkspaceGuard.Arguments(JsonSerializer.Serialize("""{"path":"a.txt","content":"x"}"""));
        Check.True(wrapped is not null, "a string-encoded arguments object is unwrapped");
        Check.Equal("a.txt", WorkspaceGuard.PathArg(wrapped!));
        Check.True(WorkspaceGuard.Arguments("\"not json\"") is null, "a string that is not an object is not arguments");
        Check.True(WorkspaceGuard.Arguments("{broken") is null);

        foreach (var key in (string[])["path", "Path", "PATH", "file_path", "filePath", "File-Path", "FILE PATH", "file", "filename", "fileName", "target"])
            Check.Equal("x.txt", WorkspaceGuard.PathArg(new JsonObject { [key] = "x.txt" }), $"read from '{key}'");
        Check.Equal("p", WorkspaceGuard.PathArg(O("""{"target":"t","path":"p"}""")), "path comes before target whatever order they were written in");
        Check.True(WorkspaceGuard.PathArg(O("""{"path":"","file":"f"}""")) is null, "a blank first name is the tool's refusal, not a reason to look at the next");
        Check.Equal("a\nb", WorkspaceGuard.PathArg(O("""{"path":["a","b"]}""")), "an array is its lines, as the tools join it");

        Check.Equal("/w", WorkspaceGuard.Str(O("""{"Working-Directory":"/w"}"""), WorkspaceGuard.CwdArgs));
        Check.Equal("a", WorkspaceGuard.Str(O("""{"dir":"b","cwd":"a"}"""), WorkspaceGuard.CwdArgs), "cwd comes before dir");

        // ssh: copy in the download direction writes `to` on this machine; nothing else does
        Check.Equal("/w/x", WorkspaceGuard.SshDownloadTarget("ssh", O("""{"action":"copy","direction":"download","to":"/w/x"}""")));
        Check.Equal("/w/x", WorkspaceGuard.SshDownloadTarget("SSH", O("""{"action":"download","destination":"/w/x"}""")), "the action says the direction; name case");
        Check.Equal("/w/x", WorkspaceGuard.SshDownloadTarget("ssh", O("""{"action":"scp","mode":"download","dest":"/w/x"}""")));
        Check.True(WorkspaceGuard.SshDownloadTarget("ssh", O("""{"action":"copy","direction":"upload","from":"/w/x","to":"/remote"}""")) is null, "an upload only reads");
        Check.True(WorkspaceGuard.SshDownloadTarget("ssh", O("""{"action":"run","script":"ls","to":"/w/x"}""")) is null, "not a copy");
        Check.True(WorkspaceGuard.SshDownloadTarget("bash", O("""{"action":"copy","direction":"download","to":"/w/x"}""")) is null, "only the ssh tool");
        return Task.CompletedTask;
    }

    private sealed class CountingLauncher : ISshLauncher
    {
        public int Calls;
        public Task<SshExec> RunAsync(string exe, IReadOnlyList<string> args, byte[]? stdin, string? workDir, Action<string>? onStdout,
            Action<string>? onStderr, TimeSpan timeout, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(new SshExec(0, "", "", false, false));
        }
    }

    /// <summary>The tool's own answer, for a call made without the hook: scp must not be started for a destination in another checkout.</summary>
    private static async Task SshDownloadRefused()
    {
        using var env = new Env();
        if (!env.GitAvailable) { Skip("ssh download"); return; }
        var w = env.Provision("w", "ses_w");
        var session = env.Session("w", w.Binding!.WorkspaceId);
        var config = Path.Combine(env.Root, "ssh-config");
        File.WriteAllText(config, "Host nuc\n  HostName 192.168.1.3\n  User quazzie\n");
        env.Ctx.Settings.Set("ssh.config", JsonValue.Create(config));
        env.Ctx.Settings.Set("ssh.path", JsonValue.Create("fake-ssh"));
        var launcher = new CountingLauncher();
        var ssh = SshToolSet.Create(env.Ctx, launcher).Single();

        // into the primary checkout: refused, and nothing was started
        var theirs = Path.Combine(env.ProjectPath, "pulled.txt");
        var refused = await ssh.ExecuteAsync(env.Context(session, w.Binding),
            Json(new { action = "copy", direction = "download", host = "nuc", from = "/tmp/x", to = theirs }), CancellationToken.None);
        Check.True(refused.IsError, "a download into the primary checkout was allowed");
        Check.Contains(refused.Content, "Refused");
        Check.Equal(0, launcher.Calls, "scp was started anyway");
        Check.False(File.Exists(theirs), "nothing was created there");

        // into its own workspace: the call goes through to scp
        var mine = Path.Combine(w.Binding.Root, "pulled.txt");
        var allowed = await ssh.ExecuteAsync(env.Context(session, w.Binding),
            Json(new { action = "download", host = "nuc", from = "/tmp/x", to = mine }), CancellationToken.None);
        Check.False(allowed.IsError, "a download into the worker's own workspace was refused: " + allowed.Content);
        Check.Equal(1, launcher.Calls, "scp ran once");
    }

    private static Task StalePathRefused()
    {
        using var env = new Env();
        if (!env.GitAvailable) { Skip("stale absolute path"); return Task.CompletedTask; }
        var w = env.Provision("w", "ses_w");
        var session = env.Session("w", w.Binding!.WorkspaceId);
        env.Write(env.ProjectPath, "target.txt", "primary's copy");
        // The absolute path the worker would remember from the checkout it was told about.
        var (result, _) = env.Write_(session, w.Binding, Path.Combine(env.ProjectPath, "target.txt"));
        Check.True(result.IsError, "a stale absolute path into the primary checkout was allowed");
        Check.Contains(result.Content, "Refused");
        Check.Equal("primary's copy", File.ReadAllText(Path.Combine(env.ProjectPath, "target.txt")));
        return Task.CompletedTask;
    }

    private static Task MissingWorkspaceFails()
    {
        using var env = new Env();
        // No git needed: the failure is about a workspace that does not exist.
        var session = env.Session("s", "wsp_does_not_exist");
        var ex = Check.Throws<WorkspaceUnavailableException>(() => env.Resolver.CwdOf(session));
        Check.Contains(ex.Message, "no longer exists");
        Check.Contains(ex.Message, "sessions.setWorkspace");
        // Every later read fails the same way: no read of this session quietly answers with the project folder.
        var again = Check.Throws<WorkspaceUnavailableException>(() => env.Resolver.CwdOf(session));
        Check.Equal(ex.Message, again.Message);

        // Gone from disk: a clear failure too, never the project.
        var folder = T.TempDir("ws-gone");
        var w = env.Ctx.Services.Get<IWorkspaceStore>()!.CreateWorkspace(new WorkspaceInfo { Name = "gone", Path = folder, ProjectId = env.Project.Id });
        var bound = env.Session("s2", w.Id);
        Directory.Delete(folder, recursive: true);
        var missing = Check.Throws<WorkspaceUnavailableException>(() => env.Resolver.CwdOf(bound));
        Check.Contains(missing.Message, "does not exist");
        try { Directory.Delete(folder, true); } catch { }
        return Task.CompletedTask;
    }

    private static Task OtherRepositoryRefused()
    {
        using var env = new Env();
        if (!env.GitAvailable) { Skip("other repository"); return Task.CompletedTask; }
        // A second repository, and a workspace bound to it while the session belongs to the first project.
        var otherRoot = Path.Combine(Path.GetDirectoryName(env.Root)!, "other-repo");
        Directory.CreateDirectory(otherRoot);
        if (!Git_(otherRoot, "init", "-q", "-b", "main")) { Skip("other repository"); return Task.CompletedTask; }
        try
        {
            File.WriteAllText(Path.Combine(otherRoot, "x.txt"), "x");
            Git_(otherRoot, "add", "-A");
            Git_(otherRoot, "-c", "user.name=t", "-c", "user.email=t@t", "commit", "-q", "-m", "init");
            var other = env.Ctx.Services.Get<IWorkspaceStore>()!.CreateWorkspace(new WorkspaceInfo { Name = "other", Path = otherRoot, Kind = "attached" });
            var session = env.Session("s", other.Id);
            var ex = Check.Throws<WorkspaceUnavailableException>(() => env.Resolver.CwdOf(session));
            Check.Contains(ex.Message, "different repository");

            // Attaching it to this project is refused at attach time as well.
            var attach = env.Provisioner.Attach(otherRoot, env.Project.Id, "other");
            Check.False(attach.Ok);
            Check.Contains(attach.Error!, "different repository");
        }
        finally { try { Directory.Delete(otherRoot, true); } catch { } }
        return Task.CompletedTask;
    }

    private static Task CleanStart()
    {
        using var env = new Env();
        if (!env.GitAvailable) { Skip("clean start"); return Task.CompletedTask; }
        // The parent's checkout has an uncommitted change; the worker's worktree must not contain it.
        File.WriteAllText(Path.Combine(env.ProjectPath, "README.md"), "base\nuncommitted parent edit\n");
        var head = GitOut(env.ProjectPath, "rev-parse", "HEAD");
        var w = env.Provision("clean", "ses_c");
        Check.True(w.Ok, w.Error ?? "");
        Check.Equal(head, w.Binding!.BaseCommit);
        Check.Equal(head, GitOut(w.Binding.Root, "rev-parse", "HEAD"));
        Check.Equal("base", File.ReadAllText(Path.Combine(w.Binding.Root, "README.md")).Trim());
        Check.Equal("", GitOut(w.Binding.Root, "status", "--porcelain") ?? "");
        return Task.CompletedTask;
    }

    private static Task ReusePerWorker()
    {
        using var env = new Env();
        if (!env.GitAvailable) { Skip("reuse per worker"); return Task.CompletedTask; }
        var first = env.Provision("tests", "ses_worker");
        var second = env.Provision("tests", "ses_worker");
        Check.True(first.Ok && second.Ok);
        Check.Equal(first.Binding!.WorkspaceId, second.Binding!.WorkspaceId);
        Check.Equal(first.Binding.Root, second.Binding.Root);

        // Two workers with the same name cannot share a folder: the second one is refused rather than handed the
        // first one's checkout, which is the accident this exists to prevent.
        var clash = env.Provision("tests", "ses_other");
        Check.False(clash.Ok);
        Check.Contains(clash.Error!, "already exists");

        // A differently named worker gets its own checkout, and both are of the same repository.
        var other = env.Provision("tests2", "ses_other");
        Check.True(other.Ok, other.Error ?? "");
        Check.Differs(first.Binding.WorkspaceId, other.Binding!.WorkspaceId);
        Check.Equal(first.Binding.RepoCommonDir, other.Binding.RepoCommonDir);
        return Task.CompletedTask;
    }

    /// <summary>
    /// The layout the user asked for: a project's worktrees are its own subfolder (<c>&lt;project&gt;/.worktrees/&lt;name&gt;</c>),
    /// not siblings beside it, so a repository's parent folder does not fill up with checkout folders. The parent must
    /// stay clean, which is why the root is excluded locally rather than through a committed .gitignore.
    /// </summary>
    private static Task WorktreesInsideProject()
    {
        using var env = new Env();
        if (!env.GitAvailable) { Skip("worktree layout"); return Task.CompletedTask; }
        var a = env.Provision("tests", "ses_a");
        var b = env.Provision("docs", "ses_b");
        Check.True(a.Ok && b.Ok, a.Error ?? b.Error ?? "");

        var root = Path.Combine(env.ProjectPath, WorkspaceProvisioner.DefaultWorktreeFolder);
        Check.Equal(Path.Combine(root, "tests"), a.Binding!.Root);
        Check.Equal(Path.Combine(root, "docs"), b.Binding!.Root);
        Check.True(WorkspacePaths.IsInside(env.ProjectPath, a.Binding.Root));
        Check.True(Directory.Exists(a.Binding.Root));

        // A nested checkout is still a checkout of the same repository: the evidence the guards rely on is unchanged.
        Check.Equal(a.Binding.RepoCommonDir, b.Binding.RepoCommonDir);
        Check.Contains(GitOut(env.ProjectPath, "worktree", "list") ?? "", "tests");

        // And the parent's status is clean: the root is excluded locally, so no branch gains an ignore line.
        Check.Equal("", GitOut(env.ProjectPath, "status", "--porcelain") ?? "", "the project checkout shows pending changes");
        var exclude = Path.Combine(WorkspacePaths.Canonical(a.Binding.RepoCommonDir!), "info", "exclude");
        Check.True(File.Exists(exclude), "the local exclude file was not written");
        Check.Contains(File.ReadAllText(exclude), "/" + WorkspaceProvisioner.DefaultWorktreeFolder + "/");
        Check.True(Git_(env.ProjectPath, "check-ignore", "-q", WorkspaceProvisioner.DefaultWorktreeFolder), "git does not ignore the worktree root");
        return Task.CompletedTask;
    }

    private static Task NonGitProject()
    {
        using var env = new Env(git: false);
        var w = env.Provision("plain", "ses_p", isolated: true);
        Check.True(w.Ok, w.Error ?? "");
        Check.Equal("folder", w.Binding!.Kind);
        Check.True(Directory.Exists(w.Binding.Root));
        Check.Differs(env.ProjectPath, w.Binding.Root);
        Check.False(w.Binding.Isolated);
        return Task.CompletedTask;
    }

    private static Task FailedProvisioning()
    {
        using var env = new Env();
        if (!env.GitAvailable) { Skip("failed provisioning"); return Task.CompletedTask; }
        // The target folder is already taken: provisioning must refuse rather than reuse it silently.
        var target = Path.Combine(env.ProjectPath, WorkspaceProvisioner.DefaultWorktreeFolder, "taken");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "keep.txt"), "someone else's work");
        var outcome = env.Manager.ForChildAsync(
            new SpawnRequest { Task = "t", Name = "taken", Isolated = true, WorkspaceOwnerSessionId = "ses_t" },
            env.Session("parent"), "ses_t", "taken", CancellationToken.None).GetAwaiter().GetResult();
        Check.False(outcome.Ok);
        Check.Contains(outcome.Error!, "already exists");
        Check.Equal("someone else's work", File.ReadAllText(Path.Combine(target, "keep.txt")));
        Check.Equal(0, env.Ctx.Services.Get<IWorkspaceStore>()!.ListWorkspaces().Count(w => w.Name == "taken"));
        return Task.CompletedTask;
    }

    private static async Task IntegrationSerializes()
    {
        using var env = new Env();
        if (!env.GitAvailable) { Skip("integration"); return; }
        var a = env.Provision("a", "ses_a");
        var b = env.Provision("b", "ses_b");
        Check.True(a.Ok && b.Ok);

        // Both workers commit something.
        foreach (var w in new[] { a.Binding!, b.Binding! })
        {
            env.Write(w.Root, $"{Path.GetFileName(w.Root)}.txt", "work");
            Git_(w.Root, "add", "-A");
            Git_(w.Root, "-c", "user.name=t", "-c", "user.email=t@t", "commit", "-q", "-m", "work");
        }

        // Two integrations at once into the same branch: they must serialize, and both must land.
        var store = env.Ctx.Services.Get<IWorkspaceStore>()!;
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(i => Task.Run(() =>
            env.Provisioner.IntegrateAsync(store.GetWorkspace(i % 2 == 0 ? a.Binding.WorkspaceId : b.Binding.WorkspaceId)))));
        foreach (var (ok, error) in results)
        {
            // Re-integrating an already-merged branch is a no-op merge, which is the success case; a failure here is a
            // real conflict, and must say so rather than pretend.
            Check.True(ok, error ?? "");
        }
        var log = GitOut(env.ProjectPath, "log", "--format=%s") ?? "";
        Check.Contains(log, "work");
        Check.Equal("main", GitOut(env.ProjectPath, "rev-parse", "--abbrev-ref", "HEAD"));
        // The lock is per repository and held only for the merge.
        Check.True(env.Provisioner.IntegrationLock(GitOut(env.ProjectPath, "rev-parse", "--path-format=absolute", "--git-common-dir")!).CurrentCount == 1);
        return;
    }

    private static async Task CleanupSafety()
    {
        using var env = new Env();
        if (!env.GitAvailable) { Skip("cleanup"); return; }
        var store = env.Ctx.Services.Get<IWorkspaceStore>()!;

        // Attached: never deleted, whatever its state.
        var attachedDir = Path.Combine(env.Root, "attached");
        Directory.CreateDirectory(attachedDir);
        var attached = env.Provisioner.Attach(attachedDir, env.Project.Id, "attached").Binding!;
        var attachedRecord = store.GetWorkspace(attached.WorkspaceId)!;
        var (attachedOk, attachedWhy) = env.Provisioner.CanRetire(attachedRecord);
        Check.False(attachedOk);
        Check.Contains(attachedWhy!, "attached");

        // Dirty: never deleted.
        var dirty = env.Provision("dirty", "ses_d").Binding!;
        env.Write(dirty.Root, "wip.txt", "uncommitted");
        var dirtyRecord = store.GetWorkspace(dirty.WorkspaceId)!;
        var (dirtyOk, dirtyWhy) = env.Provisioner.CanRetire(dirtyRecord);
        Check.False(dirtyOk);
        Check.Contains(dirtyWhy!, "uncommitted changes");
        Check.True(File.Exists(Path.Combine(dirty.Root, "wip.txt")));

        // Bound to a session: refused while it is in use.
        env.Session("uses", dirty.WorkspaceId);
        var (boundOk, boundWhy) = env.Provisioner.CanRetire(store.GetWorkspace(dirty.WorkspaceId)!);
        Check.False(boundOk);
        Check.Contains(boundWhy!, "still bound");

        // A process working in it: refused.
        var busy = env.Provision("busy", "ses_b").Binding!;
        var (busyOk, busyWhy) = env.Provisioner.CanRetire(store.GetWorkspace(busy.WorkspaceId)!, _ => true);
        Check.False(busyOk);
        Check.Contains(busyWhy!, "running process");

        // Clean but unmerged: refused, and the worktree survives for the integrator.
        var unmerged = env.Provision("unmerged", "ses_u").Binding!;
        env.Write(unmerged.Root, "work.txt", "work");
        Git_(unmerged.Root, "add", "-A");
        Git_(unmerged.Root, "-c", "user.name=t", "-c", "user.email=t@t", "commit", "-q", "-m", "unmerged work");
        var (unmergedOk, unmergedWhy) = env.Provisioner.CanRetire(store.GetWorkspace(unmerged.WorkspaceId)!);
        Check.False(unmergedOk);
        Check.Contains(unmergedWhy!, "not merged or pushed");
        Check.True(Directory.Exists(unmerged.Root));

        // Merged and clean: removed, and the record with it.
        var merged = env.Provision("merged", "ses_m").Binding!;
        env.Write(merged.Root, "work.txt", "work");
        Git_(merged.Root, "add", "-A");
        Git_(merged.Root, "-c", "user.name=t", "-c", "user.email=t@t", "commit", "-q", "-m", "merged work");
        var mergedRecord = store.GetWorkspace(merged.WorkspaceId)!;
        Check.True((await env.Provisioner.IntegrateAsync(mergedRecord)).Ok);
        var (retired, error) = await env.Provisioner.RetireAsync(store.GetWorkspace(merged.WorkspaceId)!);
        Check.True(retired, error ?? "");
        Check.False(Directory.Exists(merged.Root));
        Check.Equal(null, store.GetWorkspace(merged.WorkspaceId));
        return;
    }

    private static Task BackgroundProcessHoldsWorkspace()
    {
        using var env = new Env();
        if (!env.GitAvailable) { Skip("background process"); return Task.CompletedTask; }
        var w = env.Provision("bg", "ses_bg").Binding!;
        var store = env.Ctx.Services.Get<IWorkspaceStore>()!;
        var record = store.GetWorkspace(w.WorkspaceId)!;
        Check.True(env.Provisioner.CanRetire(record).Ok, "a clean, unmerged-free worktree with nothing running may go");

        // The shell plugin's answer: a directory with a running process in it is busy, and that blocks the cleanup.
        var busy = new BusyProcesses(w.Root);
        var (ok, why) = env.Provisioner.CanRetire(record, busy.IsBusyIn);
        Check.False(ok);
        Check.Contains(why!, "running process");
        return Task.CompletedTask;
    }

    private static Task GuardSpellings()
    {
        using var env = new Env();
        if (!env.GitAvailable) { Skip("guard spellings"); return Task.CompletedTask; }
        var w = env.Provision("g", "ses_g").Binding!;
        var session = env.Session("g", w.WorkspaceId);
        env.Write(env.ProjectPath, "victim.txt", "primary");
        Directory.CreateDirectory(Path.Combine(w.Root, "sub"));

        // Every spelling of "a file in the primary checkout" is refused.
        foreach (var path in new[]
        {
            Path.Combine(env.ProjectPath, "victim.txt"),
            Path.Combine(w.Root, "..", Path.GetFileName(env.ProjectPath), "victim.txt"),
        })
        {
            var (result, _) = env.Write_(session, w, path);
            Check.True(result.IsError, $"allowed a write to {path}");
        }

        // A junction/symlink that points into another checkout is the same directory, not a new spelling of "outside".
        var link = Path.Combine(w.Root, "link-to-primary");
        try
        {
            if (OperatingSystem.IsWindows())
                Directory.CreateSymbolicLink(link, env.ProjectPath);
            else
                Directory.CreateSymbolicLink(link, env.ProjectPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // No symlink permission: the rule is still covered by the other two spellings and by WorkspacePaths' own tests.
        }
        if (Directory.Exists(link))
        {
            var (result, _) = env.Write_(session, w, Path.Combine(link, "victim.txt"));
            Check.True(result.IsError, "a symlink into another checkout was allowed");
            Check.Equal("primary", File.ReadAllText(Path.Combine(env.ProjectPath, "victim.txt")));
        }

        // Its own workspace, and a file elsewhere entirely, are not the same case.
        var own = env.Write_(session, w, Path.Combine("sub", "mine.txt"));
        Check.False(own.Result.IsError, own.Result.Content);
        var elsewhere = env.Write_(session, w, Path.Combine(TempPath(), "netpi-outside.txt"));
        Check.False(elsewhere.Result.IsError, elsewhere.Result.Content);   // outside, but not another checkout

        // A session with no workspace binding is the pre-workspace behavior: nothing is refused.
        var unbound = env.Session("plain");
        var (unboundResult, _) = env.Write_(unbound, null, Path.Combine(env.ProjectPath, "unbound.txt"));
        Check.False(unboundResult.IsError, unboundResult.Content);
        return Task.CompletedTask;
    }

    private static string TempPath() => Path.Combine(Path.GetTempPath(), "netpi-ws-tests");

    private static Task NoticeText()
    {
        using var env = new Env();
        var workspace = new WorkspaceBinding("wsp_1", @"C:\wt\repo", "netpi/w", "abcdef1234", null, "worktree", "ses_owner", "agt_1", true, 42);
        var first = WorkspaceNotices.Text(null, workspace, workspace.Root, null);
        Check.Contains(first, workspace.Root);
        Check.Contains(first, "netpi/w");
        Check.Contains(first, "abcdef12");
        Check.Contains(first, "its own");

        var previous = ChatMessage.NoticeText("Working in workspace \"wsp_0\": C:\\wt\\other.", WorkspaceNotices.Kind);
        previous.Meta!["workspaceId"] = "wsp_0";
        var switched = WorkspaceNotices.Text(previous, workspace, workspace.Root, null);
        Check.Contains(switched, "moved to workspace");

        // No workspace at all: the project notice already said it, so nothing is said twice.
        Check.Equal("", WorkspaceNotices.Text(null, null, @"C:\repo", null));

        // A broken workspace says what is wrong and what to do, and does not pretend to have a directory.
        var broken = WorkspaceNotices.Text(null, null, @"C:\repo", "the folder is gone");
        Check.Contains(broken, "cannot be used");
        Check.Contains(broken, "the folder is gone");
        Check.Contains(broken, "sessions.setWorkspace");
        return Task.CompletedTask;
    }

    private static Task DeferredSwitch()
    {
        using var env = new Env();
        var store = env.Ctx.Services.Get<IWorkspaceStore>()!;
        var first = T.TempDir("ws-a");
        var second = T.TempDir("ws-b");
        var w1 = store.CreateWorkspace(new WorkspaceInfo { Name = "one", Path = first, ProjectId = env.Project.Id });
        var w2 = store.CreateWorkspace(new WorkspaceInfo { Name = "two", Path = second, ProjectId = env.Project.Id });
        var session = env.Session("s");
        Check.Equal(env.ProjectPath, env.Resolver.CwdOf(session));

        // Asked for while tools are running: nothing happens until the boundary.
        WorkspaceSwitchApplier.Request(session.Id, w2.Id);
        Check.Equal(env.ProjectPath, env.Resolver.CwdOf(session), "the switch was applied immediately");
        Check.Equal(w2.Id, WorkspaceSwitchApplier.PendingFor(session.Id));

        var hook = env.Ctx.Services.GetAll<IAgentHook>().OfType<WorkspaceSwitchApplier>().Single();
        var reloaded = 0;
        var turn = new AgentTurnContext
        {
            Run = new AgentRunContext
            {
                Agent = new AgentInfo { Id = "agt" }, Session = session, Cwd = env.ProjectPath,
                Services = env.Ctx.Services, Sessions = env.Ctx.Sessions, Models = env.Ctx.Models, Events = env.Ctx.Events,
                Model = new ModelInfo { Provider = "p", Id = "m" },
            },
            TurnIndex = 0, SystemPrompt = "", Messages = [], Tools = [], LastContextTokens = 0,
            ReloadMessagesAsync = () => { reloaded++; return Task.CompletedTask; },
        };
        hook.OnBeforeModelCallAsync(turn).AsTask().GetAwaiter().GetResult();
        Check.Equal(second, env.Resolver.CwdOf(session), "the switch was not applied at the boundary");
        Check.Equal(null, WorkspaceSwitchApplier.PendingFor(session.Id));
        Check.Equal(1, reloaded);

        // A switch to a workspace that is gone fails at the boundary and leaves the session where it was.
        Directory.Delete(second, recursive: true);
        WorkspaceSwitchApplier.Request(session.Id, w2.Id);
        hook.OnBeforeModelCallAsync(turn).AsTask().GetAwaiter().GetResult();   // must not throw
        Check.Equal(w2.Id, env.Ctx.SessionsFake.GetSession(session.Id)!.WorkspaceId, "the failed switch changed the binding");
        try { Directory.Delete(first, true); Directory.Delete(second, true); } catch { }
        return Task.CompletedTask;
    }

    /// <summary>
    /// The tool's promise, watched end to end through the real model/tool loop: a switch requested while a batch
    /// is running takes effect in the <em>next</em> model call — the very call after the batch, with its tools and its
    /// guard resolving against the new checkout. The turn's root is settled before the hook pass, so without a
    /// re-resolution after it (idea-y0i6gu) the promised call still works in the checkout the session just left,
    /// and the "moved" notice arrives one call late.
    /// </summary>
    private static async Task SwitchVisibleToNextModelCall()
    {
        using var env = new Env();
        if (!env.GitAvailable) { Skip("switch, next model call"); return; }

        // The real host registers the session store as a service (the tools ask for it by contract).
        env.Ctx.Services.Register<ISessionStore>(env.Ctx.Sessions);

        // Two checkouts of the project's repository: the session starts in "from" and switches to "to" mid-run.
        var from = env.Provision("from", "ses_from");
        Check.True(from.Ok, from.Error ?? "");
        var to = env.Provision("to", "ses_to");
        Check.True(to.Ok, to.Error ?? "");
        var session = env.Ctx.SessionsFake.CreateSession(new SessionInfo
        {
            Title = "switcher", ProjectId = env.Project.Id, Model = "scripted/m",
        });
        var store = env.Ctx.Services.Get<IWorkspaceStore>()!;
        store.SetSessionWorkspace(session.Id, from.Binding!.WorkspaceId);   // what sessions.setWorkspace does

        // The real loop: the runtime plugin's model/tool loop, the workspaces plugin's hooks and tool, and a write tool
        // as the probe the switch has to land in.
        new RuntimePlugin().StartAsync(env.Ctx, CancellationToken.None).GetAwaiter().GetResult();
        env.Ctx.Tools.Register(new NetPI.Tools.Files.WriteTool(env.Ctx.Settings));
        env.Ctx.ModelsFake.Models.Add(new ModelInfo { Provider = "scripted", Id = "m", ContextWindow = 100_000 });

        var script = new Queue<ChatMessage>(new[]
        {
            // Batch 1: the switch, asked while its own batch runs — parked, promised for the next call.
            T.Assistant("", T.Call("c1", "workspace", Json(new { action = "switch", id = to.Binding!.WorkspaceId }).GetRawText())),
            // Batch 2: the promised call. Its write and its info must resolve in the new checkout.
            T.Assistant("",
                T.Call("c2", "write", Json(new { path = "probe.txt", content = "landed" }).GetRawText()),
                T.Call("c3", "workspace", Json(new { action = "info" }).GetRawText())),
            // Batch 3: done.
            T.Assistant("done"),
        });
        env.Ctx.ModelsFake.StreamResponder = (_, _) => StreamOf(script.Dequeue());

        var runtime = env.Ctx.Services.Get<IAgentRuntime>()!;
        await runtime.SendAsync(session.Id, new UserInput { Text = "switch and write" });
        for (var i = 0; i < 400 && runtime.GetBySession(session.Id)?.Status is AgentStatus.Running or AgentStatus.Queued or AgentStatus.Yielded; i++)
            await Task.Delay(25);
        var agent = runtime.GetBySession(session.Id)!;
        Check.Equal(AgentStatus.Idle, agent.Status, "the run ended (not still busy)");
        Check.Equal(null, agent.Error, agent.Error ?? "the run failed");

        // The promised call's write landed in the new checkout — not in the one the session just left.
        Check.True(File.Exists(Path.Combine(to.Binding.Root, "probe.txt")), "the next model call's write did not land in the new workspace");
        Check.False(File.Exists(Path.Combine(from.Binding.Root, "probe.txt")), "the next model call's write landed in the workspace the session left");

        // The info the same batch asked for describes the new root.
        var info = env.Ctx.SessionsFake.Messages.SelectMany(m => m.ToolResults).First(r => r.CallId == "c3");
        Check.Contains(info.Content, to.Binding.Root);
        Check.NotContains(info.Content, from.Binding.Root);

        // The move is announced in the promised call's context — the model is told where it is before it works there.
        var requests = env.Ctx.ModelsFake.Requests.ToArray();
        Check.Equal(3, requests.Length, "one call per batch; the restarted pass spent no model call");
        var moved = requests[1].Messages.FirstOrDefault(m =>
            m.Role == MessageRole.Notice && m.MetaString("kind") == "workspace"
            && m.Text.Contains("moved to workspace", StringComparison.Ordinal));
        Check.True(moved is not null, "the promised call's context does not say the session moved");
        Check.Contains(moved!.Text, to.Binding.Root);
        Check.True(requests[0].Messages.All(m => m.Text.Contains("moved to workspace", StringComparison.Ordinal) is false),
            "the first call was still in the old workspace");
    }

    /// <summary>A scripted assistant message as the stream events the runner consumes.</summary>
    private static IAsyncEnumerable<ModelStreamEvent> StreamOf(ChatMessage message)
    {
        message.Role = MessageRole.Assistant;
        message.StopReason ??= message.ToolCalls.Any() ? "tool_use" : "stop";
        var events = new List<ModelStreamEvent>();
        foreach (var part in message.Parts)
        {
            if (part is TextPart { Text.Length: > 0 } text) events.Add(new TextDelta(text.Text));
            else if (part is ToolCallPart call) events.Add(new ToolCallStarted(call.Id, call.Name));
        }
        events.Add(new StreamCompleted(message));
        return Events(events);

        static async IAsyncEnumerable<ModelStreamEvent> Events(List<ModelStreamEvent> list)
        {
            foreach (var e in list) yield return e;
        }
    }

    private static Task ConsumersAgree()
    {
        using var env = new Env();
        if (!env.GitAvailable) { Skip("consumers agree"); return Task.CompletedTask; }
        var w = env.Provision("agree", "ses_ag").Binding!;
        var session = env.Session("ag");
        env.Ctx.Services.Get<IWorkspaceStore>()!.SetSessionWorkspace(session.Id, w.WorkspaceId);   // what sessions.setWorkspace does

        // The runtime's resolver and the session store it asks: one answer.
        Check.Equal(w.Root, env.Resolver.CwdOf(session));
        Check.Equal(w.Root, env.Ctx.Sessions.GetCwd(session));
        Check.Equal(w.Root, env.Ctx.SessionsFake.GetCwd(session));

        // The tool context every tool call gets: relative paths resolve in the workspace, not in the project.
        var ctx = env.Context(session, w);
        Check.Equal(w.Root, ctx.Cwd);
        Check.Equal(Path.Combine(w.Root, "x.txt"), ctx.ResolvePath("x.txt"));
        Check.Equal(w, ctx.Workspace);
        return Task.CompletedTask;
    }

    private static Task IdentityChanges()
    {
        using var env = new Env();
        var store = env.Ctx.Services.Get<IWorkspaceStore>()!;
        var dir = T.TempDir("ws-id");
        var w = store.CreateWorkspace(new WorkspaceInfo { Name = "id", Path = dir, ProjectId = env.Project.Id });
        var session = env.Session("id", w.Id);
        var first = env.Resolver.IdentityOf(session);
        Check.Contains(first, w.Id);
        // A rewrite of the record (a new version) changes the identity, which is what a UI keys its refreshes on.
        Thread.Sleep(5);
        store.UpdateWorkspace(w.Id, x => x.Branch = "moved");
        var second = env.Resolver.IdentityOf(session);
        Check.Differs(first, second);

        // An unbound session's identity is its project: no workspace id, nothing to confuse with one.
        var plain = env.Session("plain");
        Check.Contains(env.Resolver.IdentityOf(plain), "project:");
        Check.Contains(env.Resolver.IdentityOf(plain), env.Project.Id);
        try { Directory.Delete(dir, true); } catch { }
        return Task.CompletedTask;
    }

    /// <summary>
    /// The store in memory, standing in for the host's (which is exercised in NetPI.Host.Tests, against SQLite). Same
    /// contract: a workspace id that does not exist is an error, and deleting one unbinds the sessions that used it.
    /// </summary>
    private sealed class MemoryWorkspaceStore(FakeSessionStore sessions) : IWorkspaceStore
    {
        private FakeSessionStore Sessions { get; } = sessions;

        private readonly Lock _gate = new();
        private readonly Dictionary<string, WorkspaceInfo> _workspaces = new(StringComparer.Ordinal);

        public IReadOnlyList<WorkspaceInfo> ListWorkspaces(string? projectId = null)
        {
            lock (_gate)
                return [.. _workspaces.Values
                    .Where(w => projectId is null || w.ProjectId == projectId)
                    .OrderByDescending(w => w.UpdatedAt)];
        }

        public WorkspaceInfo? GetWorkspace(string id) { lock (_gate) return _workspaces.GetValueOrDefault(id); }

        public WorkspaceInfo CreateWorkspace(WorkspaceInfo template)
        {
            lock (_gate)
            {
                var w = new WorkspaceInfo
                {
                    Id = string.IsNullOrEmpty(template.Id) ? Ids.New("wsp") : template.Id,
                    Name = template.Name, Path = WorkspacePaths.Canonical(template.Path), ProjectId = template.ProjectId,
                    Kind = template.Kind, Branch = template.Branch, BaseCommit = template.BaseCommit,
                    RepoCommonDir = template.RepoCommonDir, OwnerSessionId = template.OwnerSessionId,
                    OwnerAgentId = template.OwnerAgentId, Managed = template.Managed,
                    CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
                };
                return _workspaces[w.Id] = w;
            }
        }

        public WorkspaceInfo UpdateWorkspace(string id, Action<WorkspaceInfo> mutate)
        {
            lock (_gate)
            {
                var w = _workspaces[id];
                mutate(w);
                w.UpdatedAt = DateTimeOffset.UtcNow;
                return w;
            }
        }

        public bool DeleteWorkspace(string id)
        {
            lock (_gate) return _workspaces.Remove(id);
        }

        public WorkspaceInfo? GetSessionWorkspace(string sessionId)
        {
            var id = Sessions.GetSession(sessionId)?.WorkspaceId;
            return id is null ? null : GetWorkspace(id);
        }

        public void SetSessionWorkspace(string sessionId, string? workspaceId)
        {
            if (workspaceId is not null && GetWorkspace(workspaceId) is null)
                throw new KeyNotFoundException($"Workspace {workspaceId} not found");
            if (Sessions.GetSession(sessionId) is null) return;
            Sessions.UpdateSession(sessionId, s => s.WorkspaceId = workspaceId);
            if (workspaceId is not null) Sessions.WorkspaceRoots[workspaceId] = GetWorkspace(workspaceId)!.Path;
        }
    }

    /// <summary>The shell plugin's answer about a busy directory, without a real process.</summary>
    private sealed class BusyProcesses(string path) : IWorkspaceProcesses
    {
        public bool IsBusyIn(string asked) => WorkspacePaths.IsInside(path, asked);
    }
}