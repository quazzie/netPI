using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using NetPI.Host.Data;
using NetPI.Host.Events;
using NetPI.Host.Sessions;

namespace NetPI.Host.Tests;

/// <summary>
/// The persistent side of workspaces: the records, the session reference, the working directory a bound session gets,
/// and the failures that must never degrade into "use the project folder".
/// </summary>
public static class WorkspaceStoreTests
{
    private sealed class Fixture : IAsyncDisposable
    {
        public readonly string Dir = T.TempDir("workspaces");
        public readonly Database Db;
        public readonly EventBus Bus = new(NullLogger.Instance);
        public readonly SessionStore Store;
        public readonly List<BusEvent> Events = [];
        private readonly IDisposable _sub;

        public Fixture()
        {
            Db = new Database(Path.Combine(Dir, "netpi.db"));
            Store = new SessionStore(Db, Bus, Path.Combine(Dir, "workspace"));
            _sub = Bus.Subscribe("*", e => { lock (Events) Events.Add(e); });
        }

        public async Task<List<BusEvent>> EventsAsync(string type)
        {
            await Bus.FlushAsync();
            lock (Events) return Events.Where(e => e.Type == type).ToList();
        }

        public async ValueTask DisposeAsync()
        {
            _sub.Dispose();
            await Bus.DisposeAsync();
            Db.Dispose();
        }
    }

    private static ProjectInfo Project(Fixture f, string? name = null)
    {
        var dir = Path.Combine(f.Dir, name ?? "project");
        Directory.CreateDirectory(dir);
        return f.Store.CreateProject(name ?? "project", dir);
    }

    private static WorkspaceInfo Workspace(Fixture f, string? name, string? projectId = null, string? subdir = null)
    {
        var path = Path.Combine(f.Dir, subdir ?? "wt-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(path);
        return f.Store.CreateWorkspace(new WorkspaceInfo { Name = name ?? "wt", Path = path, ProjectId = projectId, Kind = "worktree" });
    }

    public static void Register(TestRunner r)
    {
        r.Add("workspaces: a workspace record round-trips and is listed per project", async () =>
        {
            await using var f = new Fixture();
            var project = Project(f);
            var w = Workspace(f, "tests", project.Id);
            Check.True(w.Id.StartsWith("wsp_"));
            var stored = f.Store.GetWorkspace(w.Id)!;
            Check.Equal("tests", stored.Name);
            Check.Equal("worktree", stored.Kind);
            Check.Equal(project.Id, stored.ProjectId);
            Check.False(stored.Managed);
            Check.True(Directory.Exists(stored.Path));
            Check.True(Path.IsPathRooted(stored.Path));

            f.Store.UpdateWorkspace(w.Id, x => { x.Branch = "netpi/tests"; x.Managed = true; x.BaseCommit = "abc123"; });
            var again = f.Store.GetWorkspace(w.Id)!;
            Check.Equal("netpi/tests", again.Branch);
            Check.True(again.Managed);
            Check.Equal("abc123", again.BaseCommit);

            var ofProject = f.Store.ListWorkspaces(project.Id);
            Check.Equal(1, ofProject.Count);
            Check.Equal(1, f.Store.ListWorkspaces().Count);
            f.Store.CreateWorkspace(new WorkspaceInfo { Name = "other", Path = Path.Combine(f.Dir, "other"), ProjectId = null });
            Check.Equal(1, f.Store.ListWorkspaces(project.Id).Count);
            Check.Equal(2, f.Store.ListWorkspaces().Count);
        });

        r.Add("workspaces: creating one publishes workspace.created, updating workspace.updated", async () =>
        {
            await using var f = new Fixture();
            var w = Workspace(f, "w");
            f.Store.UpdateWorkspace(w.Id, x => x.Branch = "b");
            Check.Equal(1, (await f.EventsAsync(EventTypes.WorkspaceCreated)).Count);
            Check.Equal(1, (await f.EventsAsync(EventTypes.WorkspaceUpdated)).Count);
        });

        r.Add("workspaces: a bound session works in its workspace, not in its project", async () =>
        {
            await using var f = new Fixture();
            var project = Project(f);
            var session = f.Store.CreateSession(new SessionInfo { ProjectId = project.Id });
            Check.Equal(project.Path, f.Store.GetCwd(session));

            var w = Workspace(f, "wt", project.Id);
            f.Store.SetSessionWorkspace(session.Id, w.Id);
            var bound = f.Store.GetSession(session.Id)!;
            Check.Equal(w.Id, bound.WorkspaceId);
            Check.Equal(w.Path, f.Store.GetCwd(bound));
            Check.Equal(w.Id, f.Store.GetSessionWorkspace(session.Id)!.Id);

            // Unbinding goes back to the project folder: the pre-workspace behavior.
            f.Store.SetSessionWorkspace(session.Id, null);
            Check.Equal(project.Path, f.Store.GetCwd(f.Store.GetSession(session.Id)!));
            Check.Equal(null, f.Store.GetSessionWorkspace(session.Id));
        });

        r.Add("workspaces: a binding survives a restart (a new store over the same database)", async () =>
        {
            var dir = T.TempDir("workspaces-restart");
            string sessionId, workspaceId, workspacePath;
            try
            {
                var (db1, bus1) = (new Database(Path.Combine(dir, "netpi.db")), new EventBus(NullLogger.Instance));
                var store1 = new SessionStore(db1, bus1, Path.Combine(dir, "workspace"));
                var project = store1.CreateProject("p", Path.Combine(dir, "p"));
                Directory.CreateDirectory(project.Path);
                var w = store1.CreateWorkspace(new WorkspaceInfo { Name = "wt", Path = Path.Combine(dir, "wt"), ProjectId = project.Id, Kind = "worktree", Branch = "netpi/wt", OwnerSessionId = "ses_owner" });
                var s = store1.CreateSession(new SessionInfo { ProjectId = project.Id });
                store1.SetSessionWorkspace(s.Id, w.Id);
                // A real chat has messages; that is what writes its row (a message-less session is in-memory only).
                store1.AppendMessage(s.Id, new ChatMessage { Role = MessageRole.User, Parts = [new TextPart { Text = "hi" }] });
                sessionId = s.Id;
                workspaceId = w.Id;
                workspacePath = w.Path;
                await bus1.DisposeAsync();
                db1.Dispose();

                // The same database, a brand new store: the ownership and the binding are still there.
                var (db2, bus2) = (new Database(Path.Combine(dir, "netpi.db")), new EventBus(NullLogger.Instance));
                var store2 = new SessionStore(db2, bus2, Path.Combine(dir, "workspace"));
                var again = store2.GetSession(sessionId)!;
                Check.Equal(workspaceId, again.WorkspaceId);
                Check.Equal(workspacePath, store2.GetCwd(again));
                var ws = store2.GetWorkspace(workspaceId)!;
                Check.Equal("netpi/wt", ws.Branch);
                Check.Equal("ses_owner", ws.OwnerSessionId);
                await bus2.DisposeAsync();
                db2.Dispose();
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        });

        r.Add("workspaces: binding a missing workspace is an error, never a fall back to the project", async () =>
        {
            await using var f = new Fixture();
            var project = Project(f);
            var session = f.Store.CreateSession(new SessionInfo { ProjectId = project.Id });
            Check.Throws<KeyNotFoundException>(() => f.Store.SetSessionWorkspace(session.Id, "wsp_nope"));
            Check.Equal(project.Path, f.Store.GetCwd(session));   // untouched
        });

        r.Add("workspaces: a workspace whose folder is gone leaves the session pointing at it (no silent fall back)", async () =>
        {
            await using var f = new Fixture();
            var project = Project(f);
            var w = Workspace(f, "wt", project.Id);
            var session = f.Store.CreateSession(new SessionInfo { ProjectId = project.Id, WorkspaceId = w.Id });
            Directory.Delete(w.Path, recursive: true);
            // The store still reports the workspace's path: deciding that this is unusable (and refusing loudly) is the
            // resolver's job, which is where the message the user sees comes from.
            Check.Equal(w.Path, f.Store.GetCwd(session));
        });

        r.Add("workspaces: deleting a workspace unbinds its sessions and publishes session.workspace", async () =>
        {
            await using var f = new Fixture();
            var project = Project(f);
            var w = Workspace(f, "wt", project.Id);
            var transient = f.Store.CreateSession(new SessionInfo { ProjectId = project.Id, WorkspaceId = w.Id });
            Check.Equal(w.Id, f.Store.GetSession(transient.Id)!.WorkspaceId);
            var s1 = f.Store.CreateSession(new SessionInfo { ProjectId = project.Id, WorkspaceId = w.Id });
            var s2 = f.Store.CreateSession(new SessionInfo { ProjectId = project.Id, WorkspaceId = w.Id });
            f.Store.AppendMessage(s1.Id, new ChatMessage { Role = MessageRole.User, Parts = [new TextPart { Text = "hi" }] });
            f.Store.AppendMessage(s2.Id, new ChatMessage { Role = MessageRole.User, Parts = [new TextPart { Text = "hi" }] });

            Check.True(f.Store.DeleteWorkspace(w.Id));
            Check.False(f.Store.DeleteWorkspace(w.Id));
            // Three sessions were bound: the two stored ones and the message-less one (in memory only).
            var events = await f.EventsAsync(EventTypes.SessionWorkspace);
            Check.Equal(3, events.Count);
            Check.Equal(null, f.Store.GetSession(s1.Id)!.WorkspaceId);
            Check.Equal(null, f.Store.GetSession(s2.Id)!.WorkspaceId);
            // A transient (message-less) session is detached in memory, like a stored one.
            Check.Equal(null, f.Store.GetSession(transient.Id)!.WorkspaceId);
            Check.Equal(project.Path, f.Store.GetCwd(f.Store.GetSession(transient.Id)!));
        });

        r.Add("workspaces: deleting a workspace detaches every bound session in one pass, other bindings untouched", async () =>
        {
            await using var f = new Fixture();
            var project = Project(f);
            var w = Workspace(f, "wt", project.Id);
            var other = Workspace(f, "other", project.Id);

            // Three sessions bound to the workspace being deleted (two with rows, one message-less) and one to another.
            var s1 = f.Store.CreateSession(new SessionInfo { Title = "one", ProjectId = project.Id, WorkspaceId = w.Id });
            var s2 = f.Store.CreateSession(new SessionInfo { Title = "two", ProjectId = project.Id, WorkspaceId = w.Id });
            var transient = f.Store.CreateSession(new SessionInfo { Title = "new", ProjectId = project.Id, WorkspaceId = w.Id });
            var kept = f.Store.CreateSession(new SessionInfo { Title = "kept", ProjectId = project.Id, WorkspaceId = other.Id });
            foreach (var s in new[] { s1, s2, kept })
                f.Store.AppendMessage(s.Id, new ChatMessage { Role = MessageRole.User, Parts = [new TextPart { Text = "hi" }] });
            var keptBefore = f.Store.GetSession(kept.Id)!;

            // Only what the deletion publishes: workspace.deleted first, then per detached session its updated and workspace events.
            // Flush first: a late subscriber would also be handed what the bus is still delivering from the appends above.
            await f.Bus.FlushAsync();
            var order = new List<BusEvent>();
            var subscribe = f.Bus.Subscribe("*", e => { lock (order) order.Add(e); });
            var oneBefore = f.Store.GetSession(s1.Id)!;
            try
            {
                Check.True(f.Store.DeleteWorkspace(w.Id));
                await f.Bus.FlushAsync();
            }
            finally { subscribe.Dispose(); }

            // The detached sessions survive whole: still in their project, working in its folder again, messages intact.
            Check.Equal(1, f.Store.GetSession(s1.Id)!.MessageCount);
            foreach (var id in new[] { s1.Id, s2.Id, transient.Id })
            {
                var got = f.Store.GetSession(id)!;
                Check.Equal(null, got.WorkspaceId);
                Check.Equal(project.Id, got.ProjectId);
                Check.Equal(project.Path, f.Store.GetCwd(got));
            }
            Check.True(f.Store.GetSession(s1.Id)!.UpdatedAt >= oneBefore.UpdatedAt);   // the detach is an update, not a rewrite from scratch
            // The other workspace's binding stands: not touched, not even mentioned in the events.
            Check.Equal(other.Id, f.Store.GetSession(kept.Id)!.WorkspaceId);
            Check.Equal(keptBefore.UpdatedAt, f.Store.GetSession(kept.Id)!.UpdatedAt);

            lock (order)
            {
                Check.Equal(7, order.Count);
                Check.Equal(EventTypes.WorkspaceDeleted, order[0].Type);
                for (var i = 1; i < 7; i += 2)
                {
                    Check.Equal(EventTypes.SessionUpdated, order[i].Type);
                    Check.Equal(EventTypes.SessionWorkspace, order[i + 1].Type);
                }
            }
        });

        r.Add("workspaces: a fork does not inherit the original writer's checkout", async () =>
        {
            await using var f = new Fixture();
            var project = Project(f);
            var w = Workspace(f, "wt", project.Id);
            var s = f.Store.CreateSession(new SessionInfo { Title = "writer", ProjectId = project.Id, WorkspaceId = w.Id });
            f.Store.AppendMessage(s.Id, new ChatMessage { Role = MessageRole.User, Parts = [new TextPart { Text = "do it" }] });
            f.Store.AppendMessage(s.Id, new ChatMessage { Role = MessageRole.Assistant, Parts = [new TextPart { Text = "done" }] });

            var fork = f.Store.ForkSession(s.Id, 2, SessionFork.Template(f.Store.GetSession(s.Id)!, 2, 0));
            var forked = f.Store.GetSession(fork.Id)!;
            Check.Equal(null, forked.WorkspaceId);
            Check.Equal(project.Id, forked.ProjectId);
            Check.Equal(project.Path, f.Store.GetCwd(forked));   // not the writer's worktree
            // A session created with a workspace it does not have is refused.
            Check.Throws<KeyNotFoundException>(() => f.Store.CreateSession(new SessionInfo { WorkspaceId = "wsp_missing" }));
        });

        r.Add("workspaces: deleting a project detaches its workspaces' sessions' project but keeps the workspaces", async () =>
        {
            await using var f = new Fixture();
            var project = Project(f);
            var w = Workspace(f, "wt", project.Id);
            var session = f.Store.CreateSession(new SessionInfo { ProjectId = project.Id, WorkspaceId = w.Id });
            f.Store.DeleteProject(project.Id);
            Check.Equal(null, f.Store.GetSession(session.Id)!.ProjectId);
            Check.Equal(w.Id, f.Store.GetSession(session.Id)!.WorkspaceId);   // the binding stands on its own
        });

        r.Add("workspaces: WorkspacePaths resolves spelling (relative, casing, ..) to one answer", () =>
        {
            var dir = T.TempDir("wpaths");
            try
            {
                var repo = Path.Combine(dir, "repo");
                var nested = Path.Combine(repo, "src", "app");
                Directory.CreateDirectory(nested);
                var binding = new WorkspaceBinding("wsp_1", nested, "b");

                Check.Equal(WorkspacePathVerdict.Allow, WorkspacePaths.CheckMutation(binding, Path.Combine(nested, "a.txt")));
                // A relative path out of the workspace and back in is the same file.
                Check.Equal(WorkspacePathVerdict.Allow, WorkspacePaths.CheckMutation(binding, Path.Combine(nested, "..", "app", "a.txt")));
                Check.Equal(WorkspacePathVerdict.Allow, WorkspacePaths.CheckMutation(binding, Path.Combine(nested, ".")));
                // Another checkout is outside, and refused when the probe says it is the same repository.
                var other = Path.Combine(dir, "repo-other");
                Directory.CreateDirectory(Path.Combine(other, "src"));
                // Only the two checkouts of the repository answer; anything else is outside one.
                var fake = new FixedProbe(Path.Combine(dir, "common"), repo, other);
                Check.Equal(WorkspacePathVerdict.ForeignCheckout, WorkspacePaths.CheckMutation(binding, Path.Combine(other, "x"), fake));
                Check.Equal(WorkspacePathVerdict.Outside, WorkspacePaths.CheckMutation(binding, Path.Combine(dir, "unrelated.txt"), fake));
                // No probe: outside means outside, never "another checkout" (that needs git evidence).
                Check.Equal(WorkspacePathVerdict.Outside, WorkspacePaths.CheckMutation(binding, Path.Combine(other, "x")));
                // Unbound: everything is allowed (the behavior before workspaces).
                Check.Equal(WorkspacePathVerdict.Allow, WorkspacePaths.CheckMutation(null, Path.Combine(other, "x"), fake));

                if (OperatingSystem.IsWindows())
                {
                    var cased = Path.Combine(dir.ToUpperInvariant(), "REPO-OTHER", "x");
                    Check.Equal(WorkspacePathVerdict.ForeignCheckout, WorkspacePaths.CheckMutation(binding, cased, fake));
                }

                var message = WorkspacePaths.Refusal(binding, Path.Combine(other, "x"), fake);
                Check.Contains(message, "another checkout");
                Check.Contains(message, nested);
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        });

        r.Add("workspaces: IsInside does not treat a sibling with a shared prefix as inside", () =>
        {
            var dir = T.TempDir("wpaths2");
            try
            {
                Directory.CreateDirectory(Path.Combine(dir, "repo"));
                Check.True(WorkspacePaths.IsInside(Path.Combine(dir, "repo"), Path.Combine(dir, "repo", "a", "b.txt")));
                Check.False(WorkspacePaths.IsInside(Path.Combine(dir, "repo"), Path.Combine(dir, "repo-other", "b.txt")));
                Check.True(WorkspacePaths.IsInside(Path.Combine(dir, "repo"), Path.Combine(dir, "repo")));
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        });

        r.Add("workspaces: SessionWorkspace.MetaKey reads the binding a plugin wrote", () =>
        {
            var session = new SessionInfo { Meta = new JsonObject { ["workspaceId"] = "wsp_x" } };
            Check.Equal("wsp_x", SessionWorkspace.Of(session));
            Check.Equal(null, SessionWorkspace.Of(new SessionInfo()));
        });
    }

    /// <summary>A probe with a fixed answer, so the path rules can be tested without git.</summary>
    private sealed class FixedProbe(string commonDir, params string[] checkouts) : IWorkspaceRepoProbe
    {
        public string? CommonDirOf(string path) =>
            checkouts.Any(c => WorkspacePaths.IsInside(c, path)) ? commonDir : null;
        public string? BranchOf(string path) => "test";
        public string? HeadOf(string path) => "deadbeef";
    }
}