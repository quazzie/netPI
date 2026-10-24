
using Microsoft.Extensions.Logging.Abstractions;
using NetPI.Host.Storage.Sqlite;

namespace NetPI.Storage.Tests;

/// <summary>The small values, the atomic unit, the snapshot and the re-open: what the port promises around the collections.</summary>
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

        Providers.Add(r, "atomic: a nested call that finished is undone with the outer one when the outer one fails", store =>
        {
            Check.Throws<InvalidOperationException>(() => store.Sessions.Atomic(outer =>
            {
                outer.Atomic(inner => inner.InsertSession(Build.Session("inner")));
                outer.InsertSession(Build.Session("s1"));
                throw new InvalidOperationException("outer");
            }));
            Check.Equal("", string.Join(",", Build.Ids(store.Sessions.ListSessions(new SessionQuery()))), "what the finished nested call wrote went with it");
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
            Check.Equal(store.ProviderId, store.Info.Provider, "the store reports the provider it is");
            var written = string.Concat(files.Select(f => System.Text.Encoding.Latin1.GetString(File.ReadAllBytes(Path.Combine(directory, f)))));
            Check.Contains(written, "ui.state", "the snapshot holds the value that was set");
            Check.Contains(written, "s1", "and the session that was written");
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

        // ---------------------------------------------------------------- re-open
        //
        // The port's Open is "creating or upgrading as needed": a store is a place, and a new provider instance opening
        // it again finds what the last one left. Every scenario in this suite opens a fresh home and disposes it, so a
        // provider that loses what it wrote on dispose, keeps it in a process-static, or cannot open its own store back
        // passes none of them. The family below is the durable providers' — the memory provider is ephemeral by design
        // (its only file is a snapshot on request) — so it is named, like the data-damage tests.

        r.Add("[sqlite] re-open: a closed store is opened again by a new provider instance, and everything it held is read back", () =>
        {
            var home = T.TempDir("re-open-sqlite");
            IStorage Open() => new SqliteStorageProvider().Open(new StorageOpenOptions
            {
                Home = home, Logger = NullLogger.Instance, Settings = new EmptySettings(),
            });

            List<ChatMessage> first;
            using (var store = Open())
            {
                // One corner of the whole store: a project with meta, a session with meta, messages with parts, usage and
                // meta, a value, and a plugin's rows.
                store.Sessions.InsertProject(Build.Project("p1", p => p.Meta = Build.Doc(("owner", "me"))));
                store.Sessions.InsertSession(Build.Session("s1", s =>
                {
                    s.Title = "A chat"; s.ProjectId = "p1"; s.Model = "aiproxy/m1"; s.Pinned = true;
                    s.CreatedAt = Build.At(1); s.UpdatedAt = Build.At(2); s.MessageCount = 2; s.ContextTokens = 42;
                    s.Meta = Build.Doc(("workspaceId", "w1"));
                }));
                Build.Append(store, "s1", "first");
                Build.Append(store, "s1", "answer", m =>
                {
                    m.Role = MessageRole.Assistant;
                    m.Parts = [new TextPart { Text = "answer" }, new ToolCallPart { Id = "c1", Name = "read", Arguments = """{"path":"a"}""" }];
                    m.Usage = new Usage { InputTokens = 3, OutputTokens = 7 };
                    m.Compacted = true;
                    m.Meta = Build.Doc(("agentId", "a1"));
                });
                store.Values.Set("ui.state", """{"tab":"chat"}""");
                store.Plugins.For("test").Collection("things", new CollectionSpec().Text("t"))
                    .Put("a", Build.Doc(("t", "x"), ("n", 1L)));
                first = store.Sessions.GetMessages("s1").ToList();
            }

            // A new instance of the provider, the same home: the port's Open, not the old store's memory.
            using var again = Open();
            Check.Equal("me", again.Sessions.GetProject("p1")!.Meta?["owner"]?.GetValue<string>());
            var session = again.Sessions.GetSession("s1")!;
            Check.Equal("A chat", session.Title);
            Check.Equal("p1", session.ProjectId);
            Check.Equal("aiproxy/m1", session.Model);
            Check.True(session.Pinned);
            Check.Equal(Build.At(1), session.CreatedAt);
            Check.Equal(Build.At(2), session.UpdatedAt);
            Check.Equal(2L, session.MessageCount);
            Check.Equal(42L, session.ContextTokens);
            Check.Equal("w1", session.Meta?["workspaceId"]?.GetValue<string>());
            var back = again.Sessions.GetMessages("s1");
            Check.Equal(string.Join(",", first.Select(m => m.Id)), string.Join(",", back.Select(m => m.Id)),
                "the ids are the ones the first open handed out, not new ones");
            Check.Equal("1,2", string.Join(",", Build.Seqs(back)));
            Check.Equal("answer", back[1].Text);
            Check.Equal("c1", back[1].ToolCalls.Single().Id);
            Check.Equal(7L, back[1].Usage!.OutputTokens);
            Check.True(back[1].Compacted);
            Check.Equal("a1", back[1].Meta?["agentId"]?.GetValue<string>());
            Check.Equal("""{"tab":"chat"}""", again.Values.Get("ui.state"));
            var docs = again.Plugins.For("test").Collection("things", new CollectionSpec().Text("t")).Find();
            Check.Equal("a", string.Join(",", Build.Keys(docs)));
            Check.Equal("x", docs[0].Doc["t"]?.GetValue<string>());
        });

        r.Add("[sqlite] re-open: the re-opened store is opened once more, and the third open sees the writes of both", () =>
        {
            var home = T.TempDir("re-open-twice-sqlite");
            IStorage Open() => new SqliteStorageProvider().Open(new StorageOpenOptions
            {
                Home = home, Logger = NullLogger.Instance, Settings = new EmptySettings(),
            });

            using (var store = Open())
                Build.WithMessages(store, "s1", 2);

            using (var again = Open())
            {
                Check.Equal("1,2", string.Join(",", Build.Seqs(again.Sessions.GetMessages("s1"))),
                    "the second open sees what the first one wrote");
                Build.Append(again, "s1", "third");
            }

            using var onceMore = Open();
            Check.Equal("1,2,3", string.Join(",", Build.Seqs(onceMore.Sessions.GetMessages("s1"))),
                "the third open, through a third instance, sees the first open's writes and the second one's");
        });
    }
}