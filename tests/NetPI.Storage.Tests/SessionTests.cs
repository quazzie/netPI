using System.Text.Json.Nodes;
using NetPI.Host.Tests;

namespace NetPI.Storage.Tests;

/// <summary>Projects, the session list and the session tree: what the port says about them, one promise at a time.</summary>
public static class SessionTests
{
    public static void Register(TestRunner r)
    {
        // ---------------------------------------------------------------- projects

        Providers.Add(r, "projects: the list is most recently used first, then by name ignoring ASCII case", store =>
        {
            store.Sessions.InsertProject(Build.Project("p1", p => { p.Name = "beta"; p.UpdatedAt = Build.At(1); }));
            store.Sessions.InsertProject(Build.Project("p2", p => { p.Name = "Alpha"; p.UpdatedAt = Build.At(2); }));
            store.Sessions.InsertProject(Build.Project("p3", p => { p.Name = "gamma"; p.UpdatedAt = Build.At(0); p.LastUsedAt = Build.At(10); }));
            store.Sessions.InsertProject(Build.Project("p4", p => { p.Name = "delta"; p.UpdatedAt = Build.At(0); p.LastUsedAt = Build.At(10); }));
            Check.Equal("p4,p3,p2,p1", string.Join(",", store.Sessions.ListProjects().Select(p => p.Id)),
                "lastUsedAt (else updatedAt) descending, and ties by name with ASCII case folded");
        });

        Providers.Add(r, "projects: an insert of an id that exists is refused", store =>
        {
            store.Sessions.InsertProject(Build.Project("p1", p => p.Name = "first"));
            Check.Throws<StorageException>(() => store.Sessions.InsertProject(Build.Project("p1", p => p.Name = "second")));
            Check.Equal("first", store.Sessions.GetProject("p1")!.Name, "the refused insert wrote nothing");
            Check.Equal(1, store.Sessions.ListProjects().Count);
        });

        Providers.Add(r, "projects: an update writes the name, path, updatedAt and meta, and nothing else", store =>
        {
            store.Sessions.InsertProject(Build.Project("p1", p =>
            {
                p.Name = "first"; p.Path = "/one"; p.CreatedAt = Build.At(1); p.UpdatedAt = Build.At(2);
                p.LastUsedAt = Build.At(3); p.Meta = Build.Doc(("profile", "a"));
            }));
            Check.True(store.Sessions.UpdateProject(Build.Project("p1", p =>
            {
                p.Name = "second"; p.Path = "/two"; p.CreatedAt = Build.At(90); p.UpdatedAt = Build.At(4);
                p.LastUsedAt = Build.At(90); p.Meta = Build.Doc(("profile", "b"));
            })), "an update of an existing project reports that it wrote");
            var after = store.Sessions.GetProject("p1")!;
            Check.Equal("second", after.Name);
            Check.Equal("/two", after.Path);
            Check.Equal(Build.At(4), after.UpdatedAt);
            Check.Equal("b", after.Meta?["profile"]?.GetValue<string>());
            Check.Equal(Build.At(1), after.CreatedAt, "createdAt is not the update's to write");
            Check.Equal(Build.At(3), after.LastUsedAt, "lastUsedAt is TouchProject's to write");
            Check.False(store.Sessions.UpdateProject(Build.Project("nope")), "an update of nothing reports that it wrote nothing");
        });

        Providers.Add(r, "projects: touch sets lastUsedAt, delete removes the row only", store =>
        {
            store.Sessions.InsertProject(Build.Project("p1"));
            store.Sessions.InsertSession(Build.Session("s1", s => s.ProjectId = "p1"));
            store.Sessions.TouchProject("p1", Build.At(7));
            Check.Equal(Build.At(7), store.Sessions.GetProject("p1")!.LastUsedAt);
            Check.True(store.Sessions.DeleteProject("p1"));
            Check.Equal(null, store.Sessions.GetProject("p1"));
            Check.False(store.Sessions.DeleteProject("p1"), "deleting it twice reports that there was nothing");
            Check.Equal("p1", store.Sessions.GetSession("s1")!.ProjectId, "a deleted project leaves its sessions alone: the caller clears them first");
        });

        Providers.Add(r, "projects: clearing a project detaches its sessions and says how many", store =>
        {
            store.Sessions.InsertProject(Build.Project("p1"));
            store.Sessions.InsertSession(Build.Session("s1", s => { s.ProjectId = "p1"; s.UpdatedAt = Build.At(1); }));
            store.Sessions.InsertSession(Build.Session("s2", s => { s.ProjectId = "p1"; s.UpdatedAt = Build.At(2); }));
            store.Sessions.InsertSession(Build.Session("s3", s => { s.ProjectId = "p2"; s.UpdatedAt = Build.At(3); }));
            Check.Equal(2, store.Sessions.ClearProject("p1", Build.At(9)));
            Check.Equal(null, store.Sessions.GetSession("s1")!.ProjectId);
            Check.Equal(Build.At(9), store.Sessions.GetSession("s2")!.UpdatedAt, "a detached session is stamped");
            Check.Equal("p2", store.Sessions.GetSession("s3")!.ProjectId, "another project's sessions are not touched");
            Check.Equal(0, store.Sessions.ClearProject("p1", Build.At(9)), "clearing it again counts nothing");
        });

        // ---------------------------------------------------------------- the session list

        Providers.Add(r, "sessions: the list is pinned first, then newest, then id descending", store =>
        {
            store.Sessions.InsertSession(Build.Session("s1", s => { s.Pinned = true; s.UpdatedAt = Build.At(1); }));
            store.Sessions.InsertSession(Build.Session("s2", s => s.UpdatedAt = Build.At(3)));
            store.Sessions.InsertSession(Build.Session("s3", s => s.UpdatedAt = Build.At(3)));
            store.Sessions.InsertSession(Build.Session("s4", s => s.UpdatedAt = Build.At(2)));
            Check.Equal("s1,s3,s2,s4", string.Join(",", Build.Ids(store.Sessions.ListSessions(new SessionQuery()))),
                "a pinned session comes before a newer unpinned one, and equal times fall back to the id");
        });

        Providers.Add(r, "sessions: the list filters by project, parent, subagent and archived", store =>
        {
            foreach (var s in new[]
            {
                Build.Session("chat", s => s.ProjectId = "p1"),
                Build.Session("sub", s => { s.Kind = "subagent"; s.ParentSessionId = "chat"; }),
                Build.Session("other", s => s.ProjectId = "p2"),
                Build.Session("archived", s => { s.ProjectId = "p1"; s.Archived = true; }),
            }) store.Sessions.InsertSession(s);

            // the order is the list's own (ids descending here, since nothing has been stamped)
            Check.Equal("other,chat", string.Join(",", Build.Ids(store.Sessions.ListSessions(new SessionQuery()))), "no subagents and no archived sessions by default");
            Check.Equal("chat", string.Join(",", Build.Ids(store.Sessions.ListSessions(new SessionQuery { ProjectId = "p1" }))));
            Check.Equal("chat,archived", string.Join(",", Build.Ids(store.Sessions.ListSessions(new SessionQuery { ProjectId = "p1", IncludeArchived = true }))));
            Check.Equal("sub", string.Join(",", Build.Ids(store.Sessions.ListSessions(new SessionQuery { ProjectId = "", IncludeSubagents = true }))),
                "\"\" is no project at all");
            Check.Equal("sub", string.Join(",", Build.Ids(store.Sessions.ListSessions(new SessionQuery { ParentSessionId = "chat" }))),
                "a parent filter finds the subagents, whatever the subagent switch says");
            Check.Equal("sub,other,chat", string.Join(",", Build.Ids(store.Sessions.ListSessions(new SessionQuery { IncludeSubagents = true }))));
            Check.Equal("archived", string.Join(",", Build.Ids(store.Sessions.ListSessions(new SessionQuery { ArchivedOnly = true }))),
                "ArchivedOnly wins over the rest of the query");
        });

        Providers.Add(r, "sessions: the title search is a substring, ASCII case-insensitive, with % _ and \\ literal", store =>
        {
            foreach (var title in new[] { "100% done", "under_score", @"back\slash", "Plain Title", "Ünïcode", "Plain" })
                store.Sessions.InsertSession(Build.Session(title, s => s.Title = title));

            List<string> Found(string search) => Build.Ids(store.Sessions.ListSessions(new SessionQuery { Search = search }));
            Check.Equal("100% done", string.Join(",", Found("%")), "% is a character, not a wildcard");
            Check.Equal("under_score", string.Join(",", Found("_")), "_ is a character, not a wildcard");
            Check.Equal(@"back\slash", string.Join(",", Found("\\")), @"\ is a character, not an escape");
            Check.Equal("Plain Title,Plain", string.Join(",", Found("plain")), "ASCII case does not matter");
            Check.Equal("Plain Title,Plain", string.Join(",", Found("LAIN")), "and neither does the other way round");
            Check.Equal("", string.Join(",", Found("ü")), "only ASCII letters fold: a non-ASCII case difference is a different string");
            Check.Equal(6, Found(" ").Count, "a blank search is no search");
            Check.Equal("", string.Join(",", Found("zzz")));
        });

        Providers.Add(r, "sessions: the list pages, and a limit of nothing means a hundred", store =>
        {
            for (var i = 0; i < 120; i++) store.Sessions.InsertSession(Build.Session("s" + i.ToString("D3")));
            var all = Build.Ids(store.Sessions.ListSessions(new SessionQuery { Limit = 5000 }));
            Check.Equal(120, all.Count, "the limit asked for, not the default");
            Check.Equal(100, Build.Ids(store.Sessions.ListSessions(new SessionQuery())).Count, "the default limit is a hundred");
            Check.Equal(100, Build.Ids(store.Sessions.ListSessions(new SessionQuery { Limit = 0 })).Count, "no limit means 100");
            Check.Equal(100, Build.Ids(store.Sessions.ListSessions(new SessionQuery { Limit = -5 })).Count, "a negative limit means 100");
            Check.Equal("s001,s000", string.Join(",", Build.Ids(store.Sessions.ListSessions(new SessionQuery { Limit = 3, Offset = 118 }))), "the last page of a newest-first list");
            Check.Equal("s119", Build.Ids(store.Sessions.ListSessions(new SessionQuery { Limit = 5, Offset = -1 }))[0], "a negative offset is none");
            Check.Equal(0, Build.Ids(store.Sessions.ListSessions(new SessionQuery { Limit = 5, Offset = 500 })).Count, "an offset past the end is empty");
        });

        Providers.Add(r, "sessions: Attached finds the sessions whose meta key is that string", store =>
        {
            store.Sessions.InsertSession(Build.Session("bound", s => s.Meta = Build.Doc(("workspaceId", "w1"))));
            store.Sessions.InsertSession(Build.Session("other", s => s.Meta = Build.Doc(("workspaceId", "w2"))));
            store.Sessions.InsertSession(Build.Session("number", s => s.Meta = Build.Doc(("workspaceId", 7L))));
            store.Sessions.InsertSession(Build.Session("archived", s => { s.Meta = Build.Doc(("workspaceId", "w1")); s.Archived = true; }));
            store.Sessions.InsertSession(Build.Session("none"));

            List<string> Bound(SessionQuery q) => Build.Ids(store.Sessions.ListSessions(q));
            Check.Equal("bound", string.Join(",", Bound(new SessionQuery { AttachedKey = "workspaceId", AttachedValue = "w1" })));
            Check.Equal("", string.Join(",", Bound(new SessionQuery { AttachedKey = "workspaceId", AttachedValue = "w3" })));
            Check.Equal("", string.Join(",", Bound(new SessionQuery { AttachedKey = "workspaceId" })), "a null value matches no string");
            Check.Equal(5, Bound(new SessionQuery { AttachedKey = "", IncludeArchived = true }).Count, "an empty key is no filter at all");
            Check.Equal("", string.Join(",", Bound(new SessionQuery { AttachedKey = "workspaceId", AttachedValue = "7" })),
                "a meta value of another type is not that string");
            Check.Equal("bound,archived", string.Join(",", Bound(new SessionQuery { AttachedKey = "workspaceId", AttachedValue = "w1", IncludeArchived = true })));
            Check.Equal("archived", string.Join(",", Bound(new SessionQuery { AttachedKey = "workspaceId", AttachedValue = "w1", ArchivedOnly = true })));
        });

        // ---------------------------------------------------------------- the session tree

        Providers.Add(r, "sessions: a session round-trips with every field, and a missing one is null", store =>
        {
            var session = Build.Session("s1", s =>
            {
                s.Title = "A title"; s.ProjectId = "p1"; s.ParentSessionId = "s0"; s.Kind = "subagent";
                s.Model = "aiproxy/m1"; s.Reasoning = "high"; s.CreatedAt = Build.At(1); s.UpdatedAt = Build.At(2);
                s.Archived = true; s.Pinned = true; s.MessageCount = 7; s.ContextTokens = 1234;
                s.Meta = Build.Doc(("profile", "p"), ("toolsOff", new JsonArray("read")));
            });
            store.Sessions.InsertSession(session);
            Check.Throws<StorageException>(() => store.Sessions.InsertSession(Build.Session("s1")), "the same id twice is refused");
            var back = store.Sessions.GetSession("s1")!;
            Check.Equal("A title", back.Title);
            Check.Equal("p1", back.ProjectId);
            Check.Equal("s0", back.ParentSessionId);
            Check.Equal("subagent", back.Kind);
            Check.Equal("aiproxy/m1", back.Model);
            Check.Equal("high", back.Reasoning);
            Check.Equal(Build.At(1), back.CreatedAt);
            Check.Equal(Build.At(2), back.UpdatedAt);
            Check.True(back.Archived);
            Check.True(back.Pinned);
            Check.Equal(7L, back.MessageCount);
            Check.Equal(1234L, back.ContextTokens);
            Check.Equal("p", back.Meta?["profile"]?.GetValue<string>());
            Check.Equal(null, store.Sessions.GetSession("nope"));
        });

        Providers.Add(r, "sessions: an update writes everything but the id and createdAt", store =>
        {
            store.Sessions.InsertSession(Build.Session("s1", s => { s.Title = "first"; s.CreatedAt = Build.At(1); }));
            Check.True(store.Sessions.UpdateSession(Build.Session("s1", s =>
            {
                s.Title = "second"; s.CreatedAt = Build.At(90); s.UpdatedAt = Build.At(5); s.MessageCount = 3; s.Pinned = true;
            })));
            var back = store.Sessions.GetSession("s1")!;
            Check.Equal("second", back.Title);
            Check.Equal(Build.At(5), back.UpdatedAt);
            Check.Equal(3L, back.MessageCount);
            Check.True(back.Pinned);
            Check.Equal(Build.At(1), back.CreatedAt);
            Check.False(store.Sessions.UpdateSession(Build.Session("nope")), "an update of nothing reports that it wrote nothing");
        });

        Providers.Add(r, "sessions: deleting a tree returns parents before children and takes their messages", store =>
        {
            Build.WithMessages(store, "a", 2);
            Build.WithMessages(store, "b", 1);
            Build.WithMessages(store, "c", 3);
            Build.WithMessages(store, "other", 1);
            store.Sessions.UpdateSession(Build.Session("b", s => s.ParentSessionId = "a"));
            store.Sessions.UpdateSession(Build.Session("c", s => s.ParentSessionId = "b"));

            Check.Equal("a,b,c", string.Join(",", store.Sessions.DeleteSessionTree("a")), "a chain has one possible order");
            Check.Equal(null, store.Sessions.GetSession("a"));
            Check.Equal(null, store.Sessions.GetSession("b"));
            Check.Equal(null, store.Sessions.GetSession("c"));
            Check.Equal(0, store.Sessions.GetMessages("a").Count, "the messages of a deleted session go with it");
            Check.Equal(0, store.Sessions.GetMessages("b").Count);
            Check.Equal(0, store.Sessions.GetMessages("c").Count);
            Check.Equal(1, store.Sessions.GetMessages("other").Count, "a session that is not below the one deleted stays");
            Check.Equal(0, store.Sessions.DeleteSessionTree("a").Count, "there is no tree to delete the second time");
        });

        Providers.Add(r, "sessions: a tree delete puts a parent before each of its children, at any depth", store =>
        {
            store.Sessions.InsertSession(Build.Session("a"));
            foreach (var (id, parent) in new[] { ("b", "a"), ("c", "a"), ("d", "b"), ("e", "d") })
                store.Sessions.InsertSession(Build.Session(id, s => s.ParentSessionId = parent));
            store.Sessions.InsertSession(Build.Session("f", s => s.ParentSessionId = "zzz"));

            var tree = store.Sessions.DeleteSessionTree("a").ToList();
            Check.Equal(5, tree.Count, "only the sessions below the one named");
            Check.False(tree.Contains("f"), "a parent that is not there is not a child of it");
            foreach (var (id, parent) in new[] { ("a", ""), ("b", "a"), ("c", "a"), ("d", "b"), ("e", "d") })
            {
                if (parent.Length == 0) continue;
                Check.True(tree.IndexOf(parent) < tree.IndexOf(id), $"{parent} comes before {id}");
            }
        });
    }
}
