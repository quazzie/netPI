using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using NetPI.Host.Sessions;
using NetPI.Workspaces;

namespace NetPI.Aux.Tests;

/// <summary>
/// The persistent side of workspaces, where it lives now: the plugin's own collection and the binding on the session's
/// meta. The core keeps no workspace record and no field for it, so this is where "a bound session works in its
/// checkout" is decided — and where it must fail loudly rather than fall back to the project folder.
/// <para>
/// The sessions here are real ones over the real SQLite store: a stored session (it has messages), an archived one and
/// a message-less (in-memory only) one are three different things to the session service, and the binding has to reach
/// all three. Git is not involved: the resolver's refusals are WorkspaceTests' business.
/// </para>
/// </summary>
public static class WorkspaceStoreTests
{
    /// <summary>The plugin over a real session service on the same store: sessions, messages and workspace records in one place.</summary>
    private sealed class Fixture : IDisposable
    {
        public FakePluginContext Ctx { get; }
        public SessionService Sessions { get; }
        public WorkspaceStore Store { get; }
        public ProjectInfo Project { get; }
        public string Dir { get; }

        public Fixture()
        {
            Dir = T.TempDir("workspaces");
            Ctx = new FakePluginContext(Dir, pluginId: "netpi.workspaces");
            Sessions = new SessionService(Ctx.Storage, Ctx.Events, Dir);
            Ctx.RealSessions = Sessions;
            Project = Sessions.CreateProject("Demo", Path.Combine(Dir, "project"));
            Directory.CreateDirectory(Project.Path);
            Store = new WorkspaceStore(Ctx);
        }

        /// <summary>A workspace of its own, with a real folder.</summary>
        public WorkspaceInfo Workspace(string name = "wt", string? projectId = null, string? subdir = null)
        {
            var path = Path.Combine(Dir, subdir ?? "wt-" + Guid.NewGuid().ToString("N")[..6]);
            Directory.CreateDirectory(path);
            return Store.CreateWorkspace(new WorkspaceInfo { Name = name, Path = path, ProjectId = projectId ?? Project.Id, Kind = "worktree" });
        }

        /// <summary>A session with a message: that is what writes its row, so it is the one the store can find again.</summary>
        public SessionInfo Stored(string title = "s", string? projectId = null)
        {
            var session = Sessions.CreateSession(new SessionInfo { Title = title, ProjectId = projectId ?? Project.Id });
            Sessions.AppendMessage(session.Id, ChatMessage.UserText("hi"));
            return session;
        }

        public List<BusEvent> EventsOf(string type) => Ctx.Bus.OfType(type);

        public void Dispose() => Ctx.Unload();
    }

    public static void Register(TestRunner r)
    {
        r.Add("workspaces: a record round-trips, is listed per project, and creating one announces it", () =>
        {
            using var f = new Fixture();
            var w = f.Workspace("tests");
            Check.True(w.Id.StartsWith("wsp_"));
            Check.Equal(1, f.EventsOf(WorkspaceEvents.Created).Count, "creating one announces workspace.created");
            var stored = f.Store.GetWorkspace(w.Id)!;
            Check.Equal("tests", stored.Name);
            Check.Equal("worktree", stored.Kind);
            Check.Equal(f.Project.Id, stored.ProjectId);
            Check.False(stored.Managed);
            Check.True(Directory.Exists(stored.Path));
            Check.True(Path.IsPathRooted(stored.Path));

            f.Store.UpdateWorkspace(w.Id, x => { x.Branch = "netpi/tests"; x.Managed = true; x.BaseCommit = "abc123"; });
            var again = f.Store.GetWorkspace(w.Id)!;
            Check.Equal("netpi/tests", again.Branch);
            Check.True(again.Managed);
            Check.Equal("abc123", again.BaseCommit);

            var ofProject = f.Store.ListWorkspaces(f.Project.Id);
            Check.Equal(1, ofProject.Count);
            Check.Equal(1, f.Store.ListWorkspaces().Count);
            f.Workspace("other", projectId: "");   // no project at all
            Check.Equal(1, f.Store.ListWorkspaces(f.Project.Id).Count, "a projectless workspace is not one of the project's");
            Check.Equal(2, f.Store.ListWorkspaces().Count);

            Check.Equal(2, f.EventsOf(WorkspaceEvents.Created).Count, "and creating a second one announces it too");
            Check.Equal(1, f.EventsOf(WorkspaceEvents.Updated).Count, "while updating one announces workspace.updated");
            Check.True(f.Store.DeleteWorkspace(w.Id), "the record is removed");
            Check.False(f.Store.DeleteWorkspace(w.Id), "deleting it again is not an error, it is already gone");
            Check.Equal(1, f.EventsOf(WorkspaceEvents.Deleted).Count);
        });

        r.Add("workspaces: a record of a project that does not exist is refused", () =>
        {
            using var f = new Fixture();
            var ex = Check.Throws<KeyNotFoundException>(() =>
                f.Store.CreateWorkspace(new WorkspaceInfo { Name = "x", Path = Path.Combine(f.Dir, "x"), ProjectId = "prj_nope" }));
            Check.Contains(ex.Message, "prj_nope");
            Check.Equal(0, f.Store.ListWorkspaces().Count, "and nothing was written");
            Check.Equal(0, f.EventsOf(WorkspaceEvents.Created).Count, "nor announced");
        });

        r.Add("workspaces: binding a session writes the meta, and unbinding takes it away", () =>
        {
            using var f = new Fixture();
            var w = f.Workspace();
            var session = f.Stored();
            Check.Equal(f.Project.Path, f.Sessions.GetCwd(session), "an unbound session works in its project");

            f.Store.SetSessionWorkspace(session.Id, w.Id);
            var bound = f.Sessions.GetSession(session.Id)!;
            Check.Equal(w.Id, SessionWorkspace.Of(bound), "the binding is meta.workspaceId");
            Check.Equal(w.Path, SessionCwd.Of(bound), "and meta.cwd is the root the core runs the session in");
            Check.Equal(w.Path, f.Sessions.GetCwd(bound));
            Check.Equal(w.Id, f.Store.GetSessionWorkspace(session.Id)!.Id);

            f.Store.SetSessionWorkspace(session.Id, null);
            var free = f.Sessions.GetSession(session.Id)!;
            Check.Equal(null, SessionWorkspace.Of(free), "unbinding removes the binding");
            Check.Equal(null, SessionCwd.Of(free), "and the root with it");
            Check.Equal(f.Project.Path, f.Sessions.GetCwd(free), "the session works in its project again");
            Check.Equal(null, f.Store.GetSessionWorkspace(session.Id));

            // The announcement carries the binding that was written, once per change, and only for a session that has
            // announced anything (a message-less one has not).
            var boundEvents = f.EventsOf(WorkspaceEvents.SessionBound);
            Check.Equal(2, boundEvents.Count, "one for the binding, one for the unbinding");
            Check.Contains(NetPiJson.ToNode(boundEvents[0].Data)!.ToJsonString(), "\"workspaceId\":\"" + w.Id + "\"",
                "the first names the workspace it bound");
            Check.False(NetPiJson.ToNode(boundEvents[1].Data)!.AsObject().ContainsKey("workspaceId"),
                "and the second says the session is back in its project");
            Check.Equal(f.Project.Path, NetPiJson.ToNode(boundEvents[1].Data)!["cwd"]!.Str(),
                "and the root it works in again");
            var fresh = f.Sessions.CreateSession(new SessionInfo { Title = "new", ProjectId = f.Project.Id });
            f.Store.SetSessionWorkspace(fresh.Id, w.Id);
            Check.Equal(2, f.EventsOf(WorkspaceEvents.SessionBound).Count, "a session with no messages has announced nothing yet");
        });

        r.Add("workspaces: binding a workspace or a session that does not exist is an error, never a fall back", () =>
        {
            using var f = new Fixture();
            var session = f.Stored();
            Check.Throws<KeyNotFoundException>(() => f.Store.SetSessionWorkspace(session.Id, "wsp_nope"));
            Check.Equal(null, SessionWorkspace.Of(f.Sessions.GetSession(session.Id)), "the session is untouched");
            Check.Equal(f.Project.Path, f.Sessions.GetCwd(f.Sessions.GetSession(session.Id)!));

            var w = f.Workspace();
            Check.Throws<KeyNotFoundException>(() => f.Store.SetSessionWorkspace("ses_nope", w.Id));
            Check.Throws<KeyNotFoundException>(() => f.Store.UpdateWorkspace("wsp_nope", _ => { }));
            Check.Equal(null, f.Store.GetWorkspace("wsp_nope"));
            Check.Equal(null, f.Store.GetSessionWorkspace("ses_nope"));
        });

        r.Add("workspaces: the binding is durable - a new store over the same home still has it", () =>
        {
            var dir = T.TempDir("workspaces-restart");
            string sessionId, workspaceId, workspacePath, branch;
            var first = new FakePluginContext(dir, pluginId: "netpi.workspaces");
            try
            {
                var sessions = new SessionService(first.Storage, first.Events, dir);
                first.RealSessions = sessions;
                var store = new WorkspaceStore(first);
                var project = sessions.CreateProject("p", Path.Combine(dir, "p"));
                Directory.CreateDirectory(project.Path);
                var w = store.CreateWorkspace(new WorkspaceInfo { Name = "wt", Path = Path.Combine(dir, "wt"), ProjectId = project.Id, Kind = "worktree", Branch = "netpi/wt", OwnerSessionId = "ses_owner" });
                Directory.CreateDirectory(w.Path);
                var s = sessions.CreateSession(new SessionInfo { ProjectId = project.Id });
                store.SetSessionWorkspace(s.Id, w.Id);
                sessions.AppendMessage(s.Id, ChatMessage.UserText("hi"));
                (sessionId, workspaceId, workspacePath) = (s.Id, w.Id, w.Path);
                branch = w.Branch;
            }
            finally { first.Unload(); }

            // A brand new context over the same folder: the records and the binding are in the store, not in memory.
            var second = new FakePluginContext(dir, pluginId: "netpi.workspaces");
            try
            {
                var sessions = new SessionService(second.Storage, second.Events, dir);
                second.RealSessions = sessions;
                var store = new WorkspaceStore(second);
                var again = sessions.GetSession(sessionId)!;
                Check.Equal(workspaceId, SessionWorkspace.Of(again));
                Check.Equal(workspacePath, sessions.GetCwd(again));
                var ws = store.GetWorkspace(workspaceId)!;
                Check.Equal(branch, ws.Branch);
                Check.Equal("ses_owner", ws.OwnerSessionId);
            }
            finally { second.Unload(); }
        });

        r.Add("workspaces: deleting a workspace unbinds every kind of session and announces them", () =>
        {
            using var f = new Fixture();
            var w = f.Workspace();
            var other = f.Workspace("other");

            // Stored, archived and message-less (not yet stored) sessions, plus one bound to another workspace.
            var live = f.Stored("live");
            var archived = f.Stored("archived");
            f.Sessions.UpdateSession(archived.Id, s => s.Archived = true);
            var fresh = f.Sessions.CreateSession(new SessionInfo { Title = "fresh", ProjectId = f.Project.Id });
            var kept = f.Stored("kept");
            foreach (var id in new[] { live.Id, archived.Id, fresh.Id, kept.Id }) f.Store.SetSessionWorkspace(id, id == kept.Id ? other.Id : w.Id);
            f.Ctx.Bus.Events.Clear();   // only what the deletion itself announces

            Check.True(f.Store.DeleteWorkspace(w.Id));
            Check.True(f.Store.DeleteWorkspace(w.Id) is false, "deleting it again is not an error, it is already gone");

            foreach (var id in new[] { live.Id, archived.Id, fresh.Id })
            {
                var got = f.Sessions.GetSession(id)!;
                Check.Equal(null, SessionWorkspace.Of(got), $"session {id} works in its project again");
                Check.Equal(null, SessionCwd.Of(got));
                Check.Equal(f.Project.Id, got.ProjectId, "and is still the session it was");
                Check.Equal(f.Project.Path, f.Sessions.GetCwd(got));
            }
            Check.Equal(1, f.Sessions.GetMessages(live.Id).Count, "the messages are untouched");

            // Every detached session that had announced anything is told, with the binding it now has (none).
            var announced = f.EventsOf(WorkspaceEvents.SessionBound);
            Check.Equal(2, announced.Count, "the stored and the archived one; a message-less session had announced nothing: " +
                string.Join(", ", announced.Select(e => NetPiJson.ToNode(e.Data)!["sessionId"]!.Str())));
            Check.True(announced.All(e => !NetPiJson.ToNode(e.Data)!.AsObject().ContainsKey("workspaceId")),
                "each is told the session is unbound, with the project folder it works in again");
            Check.True(announced.All(e => NetPiJson.ToNode(e.Data)!["cwd"]!.Str() == f.Project.Path), "and which one that is");
            Check.Equal(1, f.EventsOf(WorkspaceEvents.Deleted).Count, "the deletion is announced once");

            // Another workspace's binding stands, not even mentioned.
            Check.Equal(other.Id, SessionWorkspace.Of(f.Sessions.GetSession(kept.Id)!));
        });

        r.Add("workspaces: BoundSessions sees every kind of session, past the list's window", () =>
        {
            using var f = new Fixture();
            var w = f.Workspace();
            var none = f.Workspace("none");

            var chat = f.Stored("chat");
            var subagent = f.Stored("sub");
            f.Sessions.UpdateSession(subagent.Id, s => s.Kind = "subagent");
            var archived = f.Stored("arch");
            f.Sessions.UpdateSession(archived.Id, s => s.Archived = true);
            var transient = f.Sessions.CreateSession(new SessionInfo { Title = "new", ProjectId = f.Project.Id });
            foreach (var id in new[] { chat.Id, subagent.Id, archived.Id, transient.Id }) f.Store.SetSessionWorkspace(id, w.Id);
            f.Store.SetSessionWorkspace(chat.Id, none.Id);   // one of them moves; the rest are w's

            var live = f.Store.BoundSessions(w.Id);
            Check.True(live.Contains(subagent.Id) && live.Contains(transient.Id),
                "live, subagent and message-less bindings count: " + string.Join(", ", live));
            Check.False(live.Contains(archived.Id), "an archived session is not working in its workspace");
            Check.False(live.Contains(chat.Id), "a session bound elsewhere is not counted");
            Check.True(f.Store.BoundSessions(w.Id, includeArchived: true).Contains(archived.Id), "an archived one opts in");
            Check.Equal(0, f.Store.BoundSessions("wsp_nope").Count, "nothing bound to another workspace leaks in");
            Check.Equal(0, f.Store.BoundSessions("").Count, "and an empty id asks for nothing");
        });

        r.Add("workspaces: a fork is a new writer, so it does not work in the writer's checkout", () =>
        {
            using var f = new Fixture();
            var resolver = new WorkspaceResolver(f.Ctx, f.Store, new GitProbe());
            var w = f.Workspace();
            var session = f.Stored();
            f.Store.SetSessionWorkspace(session.Id, w.Id);
            Check.Equal(w.Path, resolver.CwdOf(f.Sessions.GetSession(session.Id)!), "the writer works in its workspace");

            var fork = f.Sessions.ForkSession(session.Id, 1, SessionFork.Template(f.Sessions.GetSession(session.Id)!, 1, 0, f.Sessions.ForkResetKeys()));
            var forked = f.Sessions.GetSession(fork.Id)!;
            Check.Equal(f.Project.Id, forked.ProjectId, "the fork stays in the project");
            Check.Equal(null, SessionCwd.Of(forked), "a fork starts in its project folder: the core never copies meta.cwd");

            // The workspace id travelled with the meta, but a binding whose folder is not this one is broken, and the
            // resolver says so instead of handing the fork the checkout the writer had.
            var ex = Check.Throws<WorkspaceUnavailableException>(() => resolver.CwdOf(forked));
            Check.Contains(ex.Message, "sessions.setWorkspace");
            Check.NotContains(ex.Message, "may be written", ex.Message);
        });

        r.Add("workspaces: SessionWorkspace.Of reads the binding a plugin wrote, and nothing else", () =>
        {
            Check.Equal("wsp_x", SessionWorkspace.Of(new SessionInfo { Meta = new JsonObject { ["workspaceId"] = "wsp_x" } }));
            Check.Equal("wsp_x", SessionWorkspace.Of(new SessionInfo { Meta = new JsonObject { ["workspaceId"] = "  wsp_x  " } }), "a trimmed id");
            Check.Equal(null, SessionWorkspace.Of(new SessionInfo()));
            Check.Equal(null, SessionWorkspace.Of(new SessionInfo { Meta = new JsonObject() }));
            Check.Equal(null, SessionWorkspace.Of(new SessionInfo { Meta = new JsonObject { ["workspaceId"] = "" } }));
            Check.Equal(null, SessionWorkspace.Of(new SessionInfo { Meta = new JsonObject { ["workspaceId"] = 7 } }), "a number is not a workspace");
        });
    }
}
