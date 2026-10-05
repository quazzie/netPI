using System.Text.Json.Nodes;
using NetPI.Host.Storage;

namespace NetPI.Storage.Tests;

/// <summary>Plugin data: documents under keys, the index fields a query may use, and the transaction around them.</summary>
public static class CollectionTests
{
    public static void Register(TestRunner r)
    {
        Providers.Add(r, "collections: a document is put, read back, inserted once and deleted", store =>
        {
            var docs = store.Plugins.For("test").Collection("things", new CollectionSpec().Text("t").Integer("n"));
            Check.Equal("things", docs.Name);
            Check.Equal(null, docs.Get("nothing"));
            docs.Put("a", Build.Doc(("t", "one"), ("n", 1L), ("deep", Build.Doc(("x", 1L)))));
            Check.Equal("one", docs.Get("a")?["t"]?.GetValue<string>());
            Check.False(docs.Insert("a", Build.Doc(("t", "two"))), "inserting over an existing key writes nothing");
            Check.Equal("one", docs.Get("a")?["t"]?.GetValue<string>());
            Check.True(docs.Insert("b", Build.Doc(("t", "two"))), "inserting under a free key writes");
            Check.Equal("two", docs.Get("b")?["t"]?.GetValue<string>());
            docs.Put("a", Build.Doc(("t", "replaced")));
            Check.Equal("replaced", docs.Get("a")?["t"]?.GetValue<string>(), "put replaces the whole document");
            Check.Equal(null, docs.Get("a")?["n"], "a field the new document dropped is not there");
            Check.True(docs.Delete("a"));
            Check.False(docs.Delete("a"), "deleting a key that is gone reports that there was nothing");
            Check.Equal(1L, docs.Count());
        });

        Providers.Add(r, "collections: what goes in is what stays, and what comes out is the caller's own copy", store =>
        {
            var docs = store.Plugins.For("test").Collection("things", new CollectionSpec().Text("t"));
            var mine = Build.Doc(("t", "one"), ("nested", Build.Doc(("x", 1L))));
            docs.Put("a", mine);
            mine["t"] = "changed after the put";
            ((JsonObject)mine["nested"]!)["x"] = 99L;
            Check.Equal("one", docs.Get("a")?["t"]?.GetValue<string>(), "the store kept its own copy of what was put");
            Check.Equal(1L, docs.Get("a")?["nested"]?["x"]?.GetValue<long>() ?? 0L);

            var back = docs.Get("a")!;
            back["t"] = "changed after the get";
            back.Remove("nested");
            Check.Equal("one", docs.Get("a")?["t"]?.GetValue<string>(), "what comes back is the caller's own copy");
            Check.True(docs.Get("a")?["nested"] is not null);

            docs.Find()[0].Doc["t"] = "changed in a listing";
            Check.Equal("one", docs.Get("a")?["t"]?.GetValue<string>(), "and so is what a query returns");
        });

        Providers.Add(r, "collections: the same name is the same collection, and two plugins keep their own", store =>
        {
            var one = store.Plugins.For("one");
            var again = store.Plugins.For("one");      // the same plugin id, asked a second time (a reload)
            var two = store.Plugins.For("two");
            var spec = new CollectionSpec().Text("t");
            var docs = one.Collection("shared", spec);
            Check.True(ReferenceEquals(docs, again.Collection("shared", spec)), "the same name again is the same collection");

            docs.Put("a", Build.Doc(("t", "one's")));
            Check.Equal("one's", again.Collection("shared", spec).Get("a")?["t"]?.GetValue<string>(),
                "a plugin that comes back sees the documents its predecessor wrote");

            var mine = two.Collection("shared", spec);
            Check.Equal(0L, mine.Count(), "two plugins never share a collection, even under the same name");
            mine.Put("b", Build.Doc(("t", "two's")));
            Check.Equal(1L, docs.Count(), "the other plugin's document is not in this one");
            Check.Equal(1L, mine.Count());
        });

        // ---------------------------------------------------------------- filters

        Providers.Add(r, "collections: every filter, and a field with no value never compares", store =>
        {
            var docs = Seed(store);
            List<string> Keys(DataQuery q) => Build.Keys(docs.Find(q));
            string Of(DataQuery q) => string.Join(",", Keys(q));

            Check.Equal("d1,d4", Of(new DataQuery().Eq("t", "a")), "Eq on text");
            Check.Equal("d1", Of(new DataQuery().Eq("i", 10L)), "Eq on an integer");
            Check.Equal("d1", Of(new DataQuery().Eq("i", 10)), "and the same number written as an int");
            Check.Equal("d5,d6", Of(new DataQuery().Eq("i", 1L)));
            Check.Equal("d5,d6", Of(new DataQuery().Eq("i", true)), "a bool is 0/1 in an integer field");
            Check.Equal("", Of(new DataQuery().Eq("i", false)), "and false is 0, which nothing has");
            Check.Equal("d1", Of(new DataQuery().Eq("r", 1.5)), "Eq on a real");
            Check.Equal("d2,d5,d6", Of(new DataQuery().Ne("t", "a")), "Ne keeps its hands off a field with no value");
            Check.Equal("d4,d5,d6", Of(new DataQuery().Lt("i", 10L)));
            Check.Equal("d1,d4,d5,d6", Of(new DataQuery().Le("i", 10L)));
            Check.Equal("d2", Of(new DataQuery().Gt("i", 10L)));
            Check.Equal("d1,d2", Of(new DataQuery().Ge("i", 10L)));
            Check.Equal("d1,d2,d4,d5", Of(new DataQuery().In("t", ["a", "b"])), "In, in key order");
            Check.Equal("d6", Of(new DataQuery().NotIn("t", ["a", "b"])), "NotIn, and it skips the ones with no value");
            Check.Equal("d1", Of(new DataQuery().In("i", [10L, 99L])), "In on an integer");
            Check.Equal("d1,d2", Of(new DataQuery().Gt("r", 1.0)), "a real compares as a real");
            Check.Equal("d4,d5,d6", Of(new DataQuery().Lt("r", 1.0)));
            Check.Equal("d3,d7", Of(new DataQuery().IsNull("t")), "a JSON null and a property that is not there are one thing: no value");
            Check.Equal("d3,d7", Of(new DataQuery().IsNull("i")));
            Check.Equal("d1,d2,d4,d5,d6", Of(new DataQuery().NotNull("t")));
            Check.Equal("d1", Of(new DataQuery().Eq("t", "a").Gt("i", 5L)), "filters combine");
            Check.Equal(7L, docs.Count());
            Check.Equal(5L, docs.Count(new DataQuery().NotNull("i")));
            Check.Equal(2L, docs.Count(new DataQuery().IsNull("r")));
        });

        Providers.Add(r, "collections: a filter value that is null or of the wrong type is refused", store =>
        {
            var docs = Seed(store);
            Check.Throws<ArgumentException>(() => docs.Find(new DataQuery().Eq("t", null)), "a comparison needs a value");
            Check.Throws<ArgumentException>(() => docs.Find(new DataQuery().Ne("i", null)));
            Check.Throws<ArgumentException>(() => docs.Find(new DataQuery().Eq("i", "10")), "a string is not an integer");
            Check.Throws<ArgumentException>(() => docs.Find(new DataQuery().Eq("t", 5L)), "and a number is not text");
            Check.Throws<ArgumentException>(() => docs.Find(new DataQuery().In("t", ["a", null])), "nor is a null in a list");
            Check.Throws<ArgumentException>(() => docs.Find(new DataQuery().NotIn("i", ["1"])));
            Check.Throws<ArgumentException>(() => docs.Find(new DataQuery().Eq("nothing", "a")), "a filter may only name an index field");
            Check.Throws<ArgumentException>(() => docs.Find(new DataQuery().Order("nothing")));
            Check.Throws<ArgumentException>(() => docs.Count(new DataQuery().Eq("t", null)), "every entry point checks");
            Check.Throws<ArgumentException>(() => docs.Sum("i", new DataQuery().Eq("i", "1")));

            Check.Equal(0L, docs.Count(new DataQuery().In("t", [])), "an empty In matches nothing");
            Check.Equal(5L, docs.Count(new DataQuery().NotIn("t", [])), "an empty NotIn matches every document that has a value");
            var ignored = new DataQuery();
            ignored.Where.Add(new DataFilter("t", DataOp.IsNull, "a value an IsNull filter has no use for"));
            Check.Equal(2L, docs.Count(ignored), "IsNull ignores the value");
            ignored.Where.Clear();
            ignored.Where.Add(new DataFilter("t", DataOp.NotNull, 42L));
            Check.Equal(5L, docs.Count(ignored), "and so does NotNull");
        });

        // ---------------------------------------------------------------- ordering

        Providers.Add(r, "collections: the order is the one asked for, and a tie keeps key order", store =>
        {
            var docs = store.Plugins.For("test").Collection("things", new CollectionSpec().Text("g").Text("n"));
            foreach (var row in new[] { new Row("k1", "1", null), new Row("k2", "1", "2"), new Row("k3", "0", "b"),
                                       new Row("k4", "0", "a"), new Row("k5", null, null), new Row("k6", "0", "B") })
                docs.Put(row.Key, Build.Doc(("g", row.Group), ("n", row.N)));

            string Of(DataQuery q) => string.Join(",", Build.Keys(docs.Find(q)));
            Check.Equal("k1,k2,k3,k4,k5,k6", Of(new DataQuery()), "no order is key order");
            Check.Equal("k5,k3,k4,k6,k1,k2", Of(new DataQuery().Order("g")), "a field with no value sorts first ascending");
            Check.Equal("k1,k2,k3,k4,k6,k5", Of(new DataQuery().Order("g", true)), "and last descending");
            Check.Equal("k1,k5,k2,k6,k4,k3", Of(new DataQuery().Order("n")), "text orders ordinally, so \"B\" is not \"a\", and no value sorts first");
            Check.Equal("k3,k4,k6,k2,k1,k5", Of(new DataQuery().Order("n", true)), "descending is the same order turned round");
            Check.Equal("k5,k6,k4,k3,k1,k2", Of(new DataQuery().Order("g").Order("n")),
                "the second key breaks the first one's ties, and k1 before k2 where even that ties");
            Check.Equal("k1,k2", Of(new DataQuery().Eq("g", "1")), "a filter and an order together");
        });

        Providers.Add(r, "collections: limit and offset page what matched, in the query's order", store =>
        {
            var docs = store.Plugins.For("test").Collection("things", new CollectionSpec().Integer("n"));
            for (var i = 0; i < 10; i++) docs.Put("k" + i.ToString("D2"), Build.Doc(("n", (long)i)));
            string Page(int? limit = null, int offset = 0, bool desc = false)
            {
                var q = new DataQuery().Order("n", desc);
                if (limit is { } n) q.Limit = n;
                q.Offset = offset;
                return string.Join(",", Build.Keys(docs.Find(q)));
            }
            Check.Equal("k00,k01", Page(2), "the first two of the order");
            Check.Equal("k08,k09", Page(2, 8), "with an offset");
            Check.Equal("k09,k08", Page(2, 0, desc: true), "descending pages from that end too");
            Check.Equal("k00,k01,k02,k03,k04,k05,k06,k07,k08,k09", Page(), "no limit is every row");
            Check.Equal("", Page(5, 50), "a page past the end is empty");
        });

        Providers.Add(r, "collections: Count and Sum answer over what matched", store =>
        {
            var docs = Seed(store);
            Check.Equal(7L, docs.Count());
            Check.Equal(5L, docs.Count(new DataQuery().NotNull("i")));
            Check.Equal(0L, docs.Count(new DataQuery().Eq("t", "zzz")));
            Check.Equal(37d, docs.Sum("i"), "10+20+5+1+1; the documents with no value add nothing");
            Check.Equal(7d, docs.Sum("i", new DataQuery().Lt("i", 10L)), "5+1+1, over what matched");
            Check.Equal(0d, docs.Sum("i", new DataQuery().Eq("t", "zzz")), "no rows, no sum");
            Check.Equal(5.25d, docs.Sum("r"), "1.5+2.5+0.5+0.25+0.5");
            Check.Equal(4d, docs.Sum("r", new DataQuery().Ge("r", 1.0)));
        });

        Providers.Add(r, "collections: DeleteWhere removes what matched and ignores the page", store =>
        {
            var docs = Seed(store);
            Check.Equal(2, docs.DeleteWhere(new DataQuery().Eq("t", "a")), "it says how many it removed");
            Check.Equal(0, docs.DeleteWhere(new DataQuery().Eq("t", "a")), "and nothing the second time");
            Check.Equal(2, docs.DeleteWhere(new DataQuery().In("i", [5L, 1L]).Order("i", true).Take(1)),
                "an order and a limit do not narrow a delete");
            Check.Equal(3L, docs.Count(), "d2 and the two documents with no value are left");
            Check.Equal(2, docs.DeleteWhere(new DataQuery().IsNull("t")), "IsNull removes the ones with no value");
            Check.Equal(1L, docs.Count());
        });

        Providers.Add(r, "collections: names are checked alike by every provider: collection and field names, case-colliding fields, an empty key, a field's type", store =>
        {
            var data = store.Plugins.For("test");
            Check.Throws<ArgumentException>(() => data.Collection("bad name", new CollectionSpec()), "a collection name is letters, digits and underscores");
            Check.Throws<ArgumentException>(() => data.Collection("ok", new CollectionSpec().Text("bad-field")), "so is a field name");
            Check.Throws<ArgumentException>(() => data.Collection("ok", new CollectionSpec().Text("Day").Text("day")), "field names are unique ignoring case");
            var docs = data.Collection("things", new CollectionSpec().Integer("n"));
            Check.Throws<ArgumentException>(() => docs.Put("", Build.Doc(("n", 1L))), "a key is not empty");
            Check.Throws<ArgumentException>(() => docs.Get(""));
            Check.Throws<StorageException>(() => data.Collection("things", new CollectionSpec().Text("n")), "a field's type cannot change");
        });

        Providers.Add(r, "collections: a document whose index field holds the wrong kind of value is refused, and nothing of it is stored", store =>
        {
            var docs = store.Plugins.For("test").Collection("things", new CollectionSpec().Integer("n").Text("t"));
            Check.Throws<ArgumentException>(() => docs.Put("a", Build.Doc(("n", "not a number"))));
            Check.Throws<ArgumentException>(() => docs.Insert("b", Build.Doc(("t", 5L))));
            Check.Equal(0L, docs.Count(), "nothing was stored");
            docs.Put("ok", Build.Doc(("n", 3L)));
            Check.Equal(1L, docs.Count(), "a number in an Integer field and no value in the Text one is fine");
        });

        Providers.Add(r, "collections: a field a declaration dropped keeps its type: it comes back only as it was", store =>
        {
            var data = store.Plugins.For("test");
            var docs = data.Collection("things", new CollectionSpec().Integer("n"));
            docs.Put("a", Build.Doc(("n", 1L)));

            // The plugin's new version declares the collection without the field: it is not an index field any more...
            var dropped = data.Collection("things", new CollectionSpec().Text("other"));
            Check.Equal(1L, dropped.Count(), "...but the documents and their values are still there");

            // ...and when it comes back, it comes back with the type it had. A new type is a new field the port does not pretend to be.
            Check.Throws<StorageException>(() => data.Collection("things", new CollectionSpec().Text("other").Text("n")),
                "the dropped field cannot come back as another type");
            Check.Throws<StorageException>(() => data.Collection("things", new CollectionSpec().Text("other").Real("n")));
            var again = data.Collection("things", new CollectionSpec().Text("other").Integer("n"));
            Check.Equal(1L, again.Count(new DataQuery().Eq("n", 1L)), "as its old type it indexes as before");
        });

        Providers.Add(r, "queries: an In or NotIn list above the cap is refused, and one at the cap still works", store =>
        {
            var docs = store.Plugins.For("test").Collection("listy", new CollectionSpec().Integer("n"));
            for (var i = 0; i < 10; i++) docs.Put("k" + i, Build.Doc(("n", (long)i)));
            var atCap = Enumerable.Range(0, StorageNames.MaxListValues).Select(v => (object)(long)v).ToArray();
            Check.Equal(10L, docs.Count(new DataQuery().In("n", atCap)), "the cap itself fits in one statement");
            var over = atCap.Append(999999L).ToArray();
            Check.Throws<ArgumentException>(() => docs.Count(new DataQuery().In("n", over)), "one over the cap is refused");
            Check.Throws<ArgumentException>(() => docs.Count(new DataQuery().NotIn("n", over)), "and so is a NotIn over the cap");
        });

        Providers.Add(r, "collections: a changed declaration is applied to the documents already stored", store =>
        {
            var data = store.Plugins.For("test");
            var docs = data.Collection("things", new CollectionSpec().Text("status"));
            docs.Put("a", Build.Doc(("status", "open")));
            docs.Put("b", Build.Doc(("status", "done")));

            // The plugin adds an index field in a new version. The stored documents have no value in it, which is
            // what a row stored before the column existed reads as: a new field, not a new document.
            var again = data.Collection("things", new CollectionSpec().Text("status").Integer("ord"));
            Check.True(ReferenceEquals(docs, again), "the same name again is the same collection");
            Check.Equal(2L, again.Count(new DataQuery().IsNull("ord")), "every stored document has no value in the new field");
            Check.Equal(0L, again.Count(new DataQuery().NotNull("ord")));
            Check.Equal("a,b", string.Join(",", Build.Keys(again.Find(new DataQuery().Order("ord")))), "both have no value, so key order");

            var doc = again.Get("a")!;
            doc["ord"] = 1L;
            again.Put("a", doc);
            Check.Equal("b,a", string.Join(",", Build.Keys(again.Find(new DataQuery().Order("ord")))),
                "and the new field orders them, the one with no value first");
            Check.Equal(1L, again.Count(new DataQuery().NotNull("ord")), "a document written now has one");
            Check.Equal("a", string.Join(",", Build.Keys(again.Find(new DataQuery().Eq("ord", 1L)))));

            // A field the new declaration dropped is not an index field any more.
            var dropped = data.Collection("things", new CollectionSpec().Integer("ord"));
            Check.Throws<ArgumentException>(() => dropped.Find(new DataQuery().Eq("status", "open")));
            Check.Equal(2L, dropped.Count(), "but the documents are still there");
        });

        // ---------------------------------------------------------------- transactions

        Providers.Add(r, "collections: a transaction that throws rolls back everything it wrote", store =>
        {
            var data = store.Plugins.For("test");
            var docs = data.Collection("things", new CollectionSpec().Integer("n"));
            docs.Put("keep", Build.Doc(("n", 1L)));
            Check.Throws<InvalidOperationException>(() => data.Transaction(() =>
            {
                docs.Put("new", Build.Doc(("n", 2L)));
                docs.Put("keep", Build.Doc(("n", 99L)));
                docs.Delete("keep");
                data.Collection("other", new CollectionSpec().Text("t")).Put("x", Build.Doc(("t", "y")));
                throw new InvalidOperationException("boom");
            }));
            Check.Equal(1L, docs.Count(), "the new document is gone");
            Check.Equal(1L, docs.Get("keep")?["n"]?.GetValue<long>() ?? 0L, "the replaced one is back as it was");
            Check.Equal(0L, data.Collection("other", new CollectionSpec().Text("t")).Count(), "and so is the other collection");
        });

        Providers.Add(r, "collections: a declaration changed inside a transaction that rolls back leaves the plugin's handle as it was", store =>
        {
            var data = store.Plugins.For("test");
            var docs = data.Collection("things", new CollectionSpec().Integer("n"));
            docs.Put("a", Build.Doc(("n", 1L)));
            Check.Throws<InvalidOperationException>(() => data.Transaction(() =>
            {
                var wider = data.Collection("things", new CollectionSpec().Integer("n").Integer("added"));
                Check.True(ReferenceEquals(docs, wider), "the same handle takes the new declaration");
                throw new InvalidOperationException("boom");
            }));
            // the rollback took the new column away again, and the handle the plugin still holds must not write to it
            docs.Put("b", Build.Doc(("n", 2L)));
            Check.Equal(2L, docs.Count(), "the handle still writes");
            Check.Equal(1L, docs.Count(new DataQuery().Eq("n", 2L)), "and its declared field still queries");
            Check.Throws<ArgumentException>(() => docs.Find(new DataQuery().Eq("added", 1L)), "the field the rolled back declaration added is not an index field");

            // declaring it for real afterwards works, and applies to what is stored
            var again = data.Collection("things", new CollectionSpec().Integer("n").Integer("added"));
            Check.True(ReferenceEquals(docs, again));
            Check.Equal(2L, again.Count(new DataQuery().IsNull("added")), "both documents have no value in the new field");
        });

        Providers.Add(r, "collections: a declaration the stored documents cannot satisfy fails and leaves the handle usable", store =>
        {
            // The case a hot reload meets: the new version indexes a field some stored document holds the wrong kind of value in.
            var data = store.Plugins.For("test");
            var docs = data.Collection("things", new CollectionSpec().Integer("n"));
            docs.Put("a", Build.Doc(("n", 1L), ("added", "not a number")));
            Check.Throws<ArgumentException>(() => data.Collection("things", new CollectionSpec().Integer("n").Integer("added")), "the back-fill cannot read the document");
            docs.Put("b", Build.Doc(("n", 2L)));
            Check.Equal(2L, docs.Count(), "the plugin that held the handle (the generation that keeps running) still writes");
            Check.Throws<ArgumentException>(() => docs.Find(new DataQuery().Eq("added", 1L)), "the refused field was not taken on");

            docs.Put("a", Build.Doc(("n", 1L), ("added", 5L)));
            var again = data.Collection("things", new CollectionSpec().Integer("n").Integer("added"));
            Check.Equal("a", string.Join(",", Build.Keys(again.Find(new DataQuery().Eq("added", 5L)))), "once the document is right the declaration applies");
        });

        Providers.Add(r, "collections: a nested transaction that rolls back takes its declaration with it, the outer one keeps its own", store =>
        {
            var data = store.Plugins.For("test");
            var docs = data.Collection("things", new CollectionSpec().Integer("n"));
            data.Transaction(() =>
            {
                data.Collection("things", new CollectionSpec().Integer("n").Integer("kept"));
                try
                {
                    data.Transaction(() =>
                    {
                        data.Collection("things", new CollectionSpec().Integer("n").Integer("kept").Integer("lost"));
                        throw new InvalidOperationException("boom");
                    });
                }
                catch (InvalidOperationException) { }
                docs.Put("a", Build.Doc(("n", 1L), ("kept", 7L)));
            });
            Check.Equal("a", string.Join(",", Build.Keys(docs.Find(new DataQuery().Eq("kept", 7L)))), "the outer transaction's declaration is in, and writes through it");
            Check.Throws<ArgumentException>(() => docs.Find(new DataQuery().Eq("lost", 1L)), "the nested one's is not");
            docs.Put("b", Build.Doc(("n", 2L)));
            Check.Equal(2L, docs.Count());
        });

        Providers.Add(r, "collections: a transaction is re-entrant and its own writes are visible inside it", store =>
        {
            var data = store.Plugins.For("test");
            var docs = data.Collection("things", new CollectionSpec().Integer("n"));
            var seen = data.Transaction(() =>
            {
                docs.Put("a", Build.Doc(("n", 1L)));
                return data.Transaction(() => docs.Count());
            });
            Check.Equal(1L, seen, "a nested transaction joins the one in progress");
            Check.Equal(1L, docs.Count(), "and what it wrote is there");
        });

        Providers.Add(r, "collections: a session read inside a plugin transaction is a read and does not deadlock", store =>
        {
            var data = store.Plugins.For("test");
            var docs = data.Collection("things", new CollectionSpec().Integer("n"));
            Build.WithMessages(store, "s1", 1);
            var read = data.Transaction(() => store.Sessions.GetSession("s1")?.Id);
            Check.Equal("s1", read, "the session store is behind the same lock, so the read waits and then answers");
            Check.Equal(0L, docs.Count());
        });

        Providers.AddAsync(r, "collections: transactions of one plugin id never run at the same time", async store =>
        {
            var peak = await PeakInside(store.Plugins.For("test"), store.Plugins.For("test"));
            Check.Equal(1, peak, "two views of one plugin id (two generations of a plugin) take turns");
        });

        Providers.AddAsync(r, "collections: a plugin transaction is exclusive to its own plugin id, and to no other", async store =>
        {
            // Two transactions of one plugin id never run at the same time (the test above). This pins the other
            // half: the plugin's own lock is taken *outside* the provider's, so a provider that used one lock for
            // every plugin — or took the plugin lock inside the provider's — deadlocks here. The shape: this thread
            // holds the provider's lock (the kernel's, handed out by the port), a transaction on another thread has
            // taken the first plugin's lock and is waiting for the provider's, and a transaction of a *different*
            // plugin runs from this thread without waiting for anything. It is timed, because it proves the absence
            // of a deadlock: with one lock for all plugins the call below never returns, and the harness timeout
            // says so.
            var a = store.Plugins.For("one");
            var b = store.Plugins.For("two");
            var releaseA = new ManualResetEventSlim();
            var bRan = 0;
            Monitor.Enter(store.Lock);
            try
            {
                var waiting = Task.Run(() => a.Transaction(() => releaseA.Wait(10_000)));
                Thread.Sleep(250);              // long enough for that thread to reach the provider's lock
                b.Transaction(() => bRan++);   // this thread already holds the provider's lock, so only a plugin lock can hold it up
                Check.Equal(1, bRan, "another plugin id's transaction does not wait on the first one's lock");
                releaseA.Set();
                Monitor.Exit(store.Lock);
                await waiting;
            }
            catch
            {
                releaseA.Set();
                Monitor.Exit(store.Lock);
                throw;
            }
        });
    }

    /// <summary>
    /// Seven documents, one of every shape a field can have: a value of each type, a number written as an int, a
    /// bool in an integer field, a JSON null and nothing at all.
    /// </summary>
    private static IDataCollection Seed(IStorage store)
    {
        var docs = store.Plugins.For("test").Collection("things", new CollectionSpec().Text("t").Integer("i").Real("r"));
        docs.Put("d1", Build.Doc(("t", "a"), ("i", 10L), ("r", 1.5)));
        docs.Put("d2", Build.Doc(("t", "b"), ("i", 20L), ("r", 2.5)));
        docs.Put("d3", Build.Doc(("t", (string?)null), ("i", (long?)null), ("r", (double?)null)));
        docs.Put("d4", Build.Doc(("t", "a"), ("i", 5), ("r", 0.5)));
        docs.Put("d5", Build.Doc(("t", "b"), ("i", 1L), ("r", 0.25)));
        docs.Put("d6", Build.Doc(("t", "c"), ("i", true), ("r", 0.5)));
        docs.Put("d7", Build.Doc());
        return docs;
    }

    private sealed record Row(string Key, string? Group, string? N);

    /// <summary>
    /// Run a transaction on each of two plugin data views at the same time and report the highest number of them
    /// that were inside at once: 1 means they took turns, 2 that they overlapped.
    /// </summary>
    private static async Task<int> PeakInside(IPluginData first, IPluginData second)
    {
        var inside = 0;
        var peak = 0;
        var both = new CountdownEvent(2);
        async Task Run(IPluginData data) => await Task.Run(() => data.Transaction(() =>
        {
            InterlockedMax(ref peak, Interlocked.Increment(ref inside));
            both.Signal();
            both.Wait(1_000);      // a transaction that may not overlap still has to come round
            Interlocked.Decrement(ref inside);
        }));
        await Task.WhenAll(Run(first), Run(second));
        return peak;
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int seen;
        while (value > (seen = Volatile.Read(ref target)))
            if (Interlocked.CompareExchange(ref target, value, seen) == seen) return;
    }
}
