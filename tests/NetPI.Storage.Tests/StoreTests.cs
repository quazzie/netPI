using NetPI.Host.Tests;

namespace NetPI.Storage.Tests;

/// <summary>The small values, the atomic unit, and the snapshot: what the port promises around the collections.</summary>
public static class StoreTests
{
    public static void Register(TestRunner r)
    {
        // ---------------------------------------------------------------- key/value

        Providers.Add(r, "kv: a value is set, read, replaced and removed", store =>
        {
            Check.Equal(null, store.Values.Get("ui.state"), "a key that was never set is nothing");
            store.Values.Set("ui.state", "{}");
            Check.Equal("{}", store.Values.Get("ui.state"));
            store.Values.Set("ui.state", """{"tab":"chat"}""");
            Check.Equal("""{"tab":"chat"}""", store.Values.Get("ui.state"), "setting again replaces");
            store.Values.Set("UI.state", "other");
            Check.Equal("""{"tab":"chat"}""", store.Values.Get("ui.state"), "keys are ordinal, so the case is another key");
            store.Values.Set("UI.state", null);
            Check.Equal(null, store.Values.Get("UI.state"), "a null value removes the key");
            Check.Equal("""{"tab":"chat"}""", store.Values.Get("ui.state"), "and only that one");
        });

        // ---------------------------------------------------------------- atomic

        Providers.Add(r, "atomic: an exception puts back every write the work made", store =>
        {
            store.Sessions.InsertProject(Build.Project("p1"));
            store.Sessions.InsertSession(Build.Session("s1", s => s.ProjectId = "p1"));
            Build.Append(store, "s1");
            store.Values.Set("ui.state", "kept");

            Check.Throws<InvalidOperationException>(() => store.Sessions.Atomic(repo =>
            {
                repo.InsertProject(Build.Project("p2"));
                repo.InsertSession(Build.Session("s2", s => s.ProjectId = "p2"));
                repo.AppendMessage(Build.Message("s2", "in the transaction"));
                repo.RecordAppend("s2", Build.At(5), "A title");
                repo.TouchProject("p1", Build.At(5));
                repo.MarkCompacted("s1", 1);
                repo.DeleteSessionTree("s2");
                store.Values.Set("ui.state", "changed");
                store.Values.Set("other", "written");
                throw new InvalidOperationException("boom");
            }));

            Check.Equal(1, store.Sessions.ListProjects().Count, "the project the work inserted is gone");
            Check.Equal(null, store.Sessions.GetProject("p1")!.LastUsedAt, "and the one it touched is as it was");
            Check.Equal("s1", string.Join(",", Build.Ids(store.Sessions.ListSessions(new SessionQuery()))), "so is the session it inserted");
            Check.Equal(null, store.Sessions.GetSession("s2"), "and the one it inserted and deleted again");
            Check.Equal(0, store.Sessions.GetMessages("s2").Count, "with no message and no copy of one");
            Check.False(store.Sessions.GetMessages("s1")[0].Compacted, "the compaction it marked is not there");
            Check.Equal("kept", store.Values.Get("ui.state"), "the value it changed is back");
            Check.Equal(null, store.Values.Get("other"), "and the one it added is gone");
        });

        Providers.Add(r, "atomic: a nested call joins the transaction in progress", store =>
        {
            var answer = store.Sessions.Atomic(outer =>
            {
                outer.InsertSession(Build.Session("s1"));
                var seen = outer.Atomic(inner => inner.GetSession("s1") is null ? "missing" : "there");
                outer.InsertSession(Build.Session("s2"));
                return seen;
            });
            Check.Equal("there", answer, "a nested call sees what the outer one wrote");
            Check.Equal("s2,s1", string.Join(",", Build.Ids(store.Sessions.ListSessions(new SessionQuery()))), "both are there");
        });

        Providers.Add(r, "atomic: a nested failure rolls back its own writes and nothing else", store =>
        {
            var caught = store.Sessions.Atomic(outer =>
            {
                outer.InsertSession(Build.Session("s1"));
                try
                {
                    outer.Atomic(inner =>
                    {
                        inner.InsertSession(Build.Session("inner"));
                        throw new InvalidOperationException("inner");
                    });
                }
                catch (InvalidOperationException) { }
                outer.InsertSession(Build.Session("after"));
                return "committed";
            });
            Check.Equal("committed", caught);
            Check.Equal("s1,after", string.Join(",", Build.Ids(store.Sessions.ListSessions(new SessionQuery()))),
                "what the outer call wrote is there; what the failed nested call wrote is not");
        });

        Providers.Add(r, "atomic: the work is handed the repository it is running on", store =>
        {
            store.Sessions.Atomic(repo =>
            {
                Check.True(ReferenceEquals(repo, store.Sessions), "a nested call gets the same repository, not a second one");
                Check.True(Monitor.IsEntered(store.Lock), "and it runs under the one lock the port hands the kernel");
            });
        });

        // ---------------------------------------------------------------- the snapshot

        Providers.Add(r, "snapshot: it writes the store into a directory of its own and names what it wrote", store =>
        {
            store.Values.Set("ui.state", "{}");
            Build.WithMessages(store, "s1", 2);
            store.Plugins.For("test").Collection("things", new CollectionSpec().Text("t")).Put("a", Build.Doc(("t", "x")));

            var directory = T.TempDir("snapshot");
            var files = store.Snapshot.Write(directory);
            Check.True(files.Count > 0, "a snapshot writes at least one file");
            foreach (var file in files)
            {
                Check.False(Path.IsPathRooted(file), $"{file} is named relative to the directory");
                Check.False(file.Contains(".."), $"{file} stays inside the directory");
                var path = Path.Combine(directory, file);
                Check.True(File.Exists(path), $"{file} is there");
                Check.True(new FileInfo(path).Length > 0, $"{file} has something in it");
            }

            // The store keeps working afterwards: a snapshot is a copy, not a closing.
            store.Values.Set("ui.state", """{"tab":"chat"}""");
            Build.Append(store, "s1", "after");
            Check.Equal("""{"tab":"chat"}""", store.Values.Get("ui.state"));
            Check.Equal(3, store.Sessions.GetMessages("s1").Count);
        });

        Providers.Add(r, "snapshot: an empty store still writes a snapshot", store =>
        {
            var directory = T.TempDir("snapshot-empty");
            var files = store.Snapshot.Write(directory);
            Check.True(files.Count > 0, "a backup of an empty store is a restore point, not nothing");
            foreach (var file in files) Check.True(File.Exists(Path.Combine(directory, file)));
        });
    }
}