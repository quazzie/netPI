using System.Text.Json.Nodes;

namespace NetPI.Storage.Tests;

/// <summary>Messages: the ids and seqs they are given, what the context reads, compaction and the copy a fork makes.</summary>
public static class MessageTests
{
    public static void Register(TestRunner r)
    {
        Providers.Add(r, "messages: a message round-trips with its parts, usage and meta", store =>
        {
            Build.WithMessages(store, "s1", 0);
            var stored = Build.Append(store, "s1", "hi", m =>
            {
                m.Role = MessageRole.Assistant;
                m.Parts =
                [
                    new ThinkingPart { Text = "hmm", Signature = "sig" },
                    new TextPart { Text = "there" },
                    new ToolCallPart { Id = "call_1", Name = "read", Arguments = """{"path":"a.txt"}""" },
                ];
                m.CreatedAt = Build.At(3);
                m.Provider = "aiproxy"; m.Model = "m1"; m.StopReason = "tool_use";
                m.Usage = new Usage { InputTokens = 10, OutputTokens = 5, CacheReadTokens = 2 };
                m.DurationMs = 1234;
                m.Compacted = true;
                m.Meta = Build.Doc(("kind", "turn"));
            });
            var back = store.Sessions.GetMessage(stored.Id)!;
            Check.Equal("s1", back.SessionId);
            Check.Equal(1L, back.Seq);
            Check.Equal(MessageRole.Assistant, back.Role);
            Check.Equal("there", back.Text);
            Check.Equal("sig", back.Parts.OfType<ThinkingPart>().Single().Signature);
            Check.Equal("call_1", back.ToolCalls.Single().Id);
            Check.Equal(Build.At(3), back.CreatedAt);
            Check.Equal("aiproxy", back.Provider);
            Check.Equal("m1", back.Model);
            Check.Equal("tool_use", back.StopReason);
            Check.Equal(5L, back.Usage!.OutputTokens);
            Check.Equal(1234L, back.DurationMs);
            Check.True(back.Compacted);
            Check.Equal("turn", back.Meta?["kind"]?.GetValue<string>());
            Check.Equal(null, store.Sessions.GetMessage(9999), "an id that was never handed out is nothing");
        });

        Providers.Add(r, "messages: ids come from one counter for the whole store and are never reused", store =>
        {
            Build.WithMessages(store, "s1", 2);
            Build.WithMessages(store, "s2", 2);
            var ids = store.Sessions.GetMessages("s1").Select(m => m.Id).Concat(store.Sessions.GetMessages("s2").Select(m => m.Id)).ToList();
            Check.Equal(4, ids.Distinct().Count(), "ids are not shared between sessions");
            Check.Equal("1,2,3,4", Build.Of(ids), "one counter hands them out in order");

            // The sessions and their messages go, and the counter does not go back: an id that was handed out is
            // never handed out again, even to a session that is new.
            store.Sessions.DeleteSessionTree("s1");
            store.Sessions.DeleteSessionTree("s2");
            Build.WithMessages(store, "s3", 1);
            Check.True(store.Sessions.GetMessages("s3")[0].Id > 4, "a deleted message's id is not reused");
        });

        Providers.Add(r, "messages: seq is the session's highest plus one, and the first is one", store =>
        {
            Build.WithMessages(store, "s1", 3);
            Build.WithMessages(store, "s2", 1);
            Check.Equal("1,2,3", string.Join(",", Build.Seqs(store.Sessions.GetMessages("s1"))));
            Check.Equal("1", string.Join(",", Build.Seqs(store.Sessions.GetMessages("s2"))), "seq counts per session, not per store");
            Build.Append(store, "s1", "again");
            Check.Equal("1,2,3,4", string.Join(",", Build.Seqs(store.Sessions.GetMessages("s1"))));
        });

        Providers.Add(r, "messages: an append for a session that is not stored is refused, and changes nothing", store =>
        {
            Build.WithMessages(store, "s1", 1);
            Check.Throws<Exception>(() => store.Sessions.AppendMessage(Build.Message("nope")));
            Check.Equal(1, store.Sessions.GetMessages("s1").Count, "the refused append took nothing of the other session with it");
        });

        Providers.Add(r, "messages: RecordAppend stamps the session, counts the message and takes the title", store =>
        {
            store.Sessions.InsertSession(Build.Session("s1", s => { s.Title = "New session"; s.UpdatedAt = Build.At(0); s.MessageCount = 0; }));
            Build.Append(store, "s1");
            store.Sessions.RecordAppend("s1", Build.At(5), "A title");
            var session = store.Sessions.GetSession("s1")!;
            Check.Equal(Build.At(5), session.UpdatedAt);
            Check.Equal(1L, session.MessageCount);
            Check.Equal("A title", session.Title);
            store.Sessions.RecordAppend("s1", Build.At(6), "Another");
            Check.Equal(2L, store.Sessions.GetSession("s1")!.MessageCount);
        });

        Providers.Add(r, "messages: an update rewrites the message and reports a missing id", store =>
        {
            Build.WithMessages(store, "s1", 1);
            var stored = store.Sessions.GetMessages("s1")[0];
            stored.Role = MessageRole.Assistant;
            stored.Parts = [new TextPart { Text = "rewritten" }];
            stored.Provider = "aiproxy"; stored.Model = "m1"; stored.StopReason = "stop";
            stored.Usage = new Usage { InputTokens = 3 }; stored.DurationMs = 42; stored.Compacted = true;
            stored.Meta = Build.Doc(("kind", "edited"));
            Check.True(store.Sessions.UpdateMessage(stored));
            var back = store.Sessions.GetMessage(stored.Id)!;
            Check.Equal("rewritten", back.Text);
            Check.Equal(1L, back.Seq, "the seq is the row's own");
            Check.Equal(3L, back.Usage!.InputTokens);
            Check.Equal("edited", back.Meta?["kind"]?.GetValue<string>());
            Check.False(store.Sessions.UpdateMessage(Build.Message("s1")), "an id that is not stored is reported, not written");
        });

        Providers.Add(r, "messages: the page is the newest one and is handed back ascending", store =>
        {
            Build.WithMessages(store, "s1", 5);
            List<long> Page(long? before = null, int? limit = null) => Build.Seqs(store.Sessions.GetMessages("s1", before, limit));
            Check.Equal("1,2,3,4,5", string.Join(",", Page()));
            Check.Equal("4,5", string.Join(",", Page(limit: 2)));
            Check.Equal("1,2", string.Join(",", Page(before: 3)));
            Check.Equal("2", string.Join(",", Page(before: 3, limit: 1)));
            Check.Equal("1,2,3,4,5", string.Join(",", Page(limit: 0)), "a limit of nothing is no limit");
            Check.Equal("", string.Join(",", Page(before: 1)), "a page may be empty");
            Check.Equal(0, store.Sessions.GetMessages("nope").Count, "a session with no messages has none");
        });

        Providers.Add(r, "messages: ReadContext is the uncompacted messages and the newest uncompacted seq", store =>
        {
            Build.WithMessages(store, "s1", 5);
            var (rows, newest) = store.Sessions.ReadContext("s1");
            Check.Equal("1,2,3,4,5", string.Join(",", Build.Seqs(rows)));
            Check.Equal(5L, newest);

            store.Sessions.MarkCompacted("s1", 3);
            (rows, newest) = store.Sessions.ReadContext("s1");
            Check.Equal("4,5", string.Join(",", Build.Seqs(rows)));
            Check.Equal(5L, newest, "compacting the older messages does not move the newest");

            store.Sessions.MarkCompacted("s1", 5);
            (rows, newest) = store.Sessions.ReadContext("s1");
            Check.Equal(0, rows.Count, "nothing is left in the context");
            Check.Equal(0L, newest, "and nothing is left as the newest");

            (rows, newest) = store.Sessions.ReadContext("nope");
            Check.Equal(0, rows.Count);
            Check.Equal(0L, newest);
        });

        Providers.Add(r, "messages: ReadContext never reports a newest below the last row it returned", store =>
        {
            // The rows are read first and the newest after them, so a message that lands in between shows up as a
            // newest past the last row rather than as a view that has lost one.
            Build.WithMessages(store, "s1", 4);
            for (var round = 0; round < 25; round++)
            {
                var writer = Task.Run(() => Build.Append(store, "s1", "concurrent"));
                var (rows, newest) = store.Sessions.ReadContext("s1");
                writer.GetAwaiter().GetResult();
                Check.True(rows.Count == 0 || newest >= rows[^1].Seq, $"newest {newest} is below the last row {rows[^1].Seq}");
                Check.True(newest >= 0);
            }
        });

        Providers.Add(r, "messages: compacting marks everything up to a seq, and SetCompacted exactly those", store =>
        {
            Build.WithMessages(store, "s1", 5);
            store.Sessions.MarkCompacted("s1", 3);
            Check.Equal("true,true,true,false,false", Flags(store, "s1"), "seq <= 3, and nothing after it");
            store.Sessions.MarkCompacted("s1", 3);
            Check.Equal("true,true,true,false,false", Flags(store, "s1"), "compacting twice changes nothing");
            store.Sessions.MarkCompacted("s1", 2);
            Check.Equal("true,true,true,false,false", Flags(store, "s1"), "compacting an older range again changes nothing");

            store.Sessions.SetCompacted("s1", new HashSet<long> { 2, 4 });
            Check.Equal("false,true,false,true,false", Flags(store, "s1"), "SetCompacted makes exactly the seqs it names, compacted");
            Check.Equal(0, store.Sessions.MessageStubs("nope").Count, "a session with no messages has no stubs");
        });

        Providers.Add(r, "messages: the stubs carry the identity without the parts", store =>
        {
            Build.WithMessages(store, "s1", 2);
            Build.Append(store, "s1", "third", m => { m.Role = MessageRole.Summary; m.Compacted = true; m.Meta = Build.Doc(("coversUpToSeq", 2L)); });
            var stubs = store.Sessions.MessageStubs("s1");
            Check.Equal(3, stubs.Count);
            Check.Equal(1L, stubs[0].Id);
            Check.Equal(3L, stubs[2].Seq);
            Check.Equal(MessageRole.Summary, stubs[2].Role);
            Check.True(stubs[2].Compacted);
            Check.Equal(2L, stubs[2].Meta?["coversUpToSeq"]?.GetValue<long>() ?? 0L);
        });

        Providers.Add(r, "messages: a copy keeps the seq, the time and the rest, and takes a new id", store =>
        {
            Build.WithMessages(store, "from", 3);
            Build.Append(store, "from", "assistant", m =>
            {
                m.Role = MessageRole.Assistant;
                m.Usage = new Usage { OutputTokens = 7 };
                m.Compacted = true;
                m.Meta = Build.Doc(("kind", "x"));
                m.CreatedAt = Build.At(4);
            });
            store.Sessions.InsertSession(Build.Session("to"));

            Check.Equal(2, store.Sessions.CopyMessages("from", "to", 2), "the copy stops at the seq it was given");
            var source = store.Sessions.GetMessages("from", beforeSeq: 3);   // the two messages that were copied
            var copies = store.Sessions.GetMessages("to");
            Check.Equal("1,2", string.Join(",", Build.Seqs(copies)), "a copy keeps the seq, so a summary's range still means something");
            for (var i = 0; i < copies.Count; i++)
            {
                Check.True(source[i].Id != copies[i].Id, "and takes an id of its own");
                Check.Equal(source[i].Role, copies[i].Role);
                Check.Equal(source[i].Text, copies[i].Text);
                Check.Equal(source[i].CreatedAt, copies[i].CreatedAt);
                Check.Equal(source[i].Compacted, copies[i].Compacted);
                Check.Equal(source[i].Meta?.ToJsonString(), copies[i].Meta?.ToJsonString());
            }
            Check.Equal("to", copies[0].SessionId, "the copy belongs to the session it was copied into");
            Check.Equal(0, store.Sessions.CopyMessages("nope", "to", 10), "nothing to copy is nothing copied");
            Check.Throws<Exception>(() => store.Sessions.CopyMessages("from", "nope", 10), "a copy goes into a session that exists");
            Check.Equal(2, store.Sessions.GetMessages("to").Count, "and the refused copy wrote nothing");
        });

        Providers.Add(r, "messages: the meta of one message is rewritten on its own", store =>
        {
            Build.WithMessages(store, "s1", 2);
            var second = store.Sessions.GetMessages("s1")[1];
            store.Sessions.UpdateMessageMeta(second.Id, Build.Doc(("agentId", "a1")));
            Check.Equal("a1", store.Sessions.GetMessage(second.Id)!.Meta?["agentId"]?.GetValue<string>());
            Check.Equal(null, store.Sessions.GetMessage(store.Sessions.GetMessages("s1")[0].Id)!.Meta, "the other message is not touched");
            store.Sessions.UpdateMessageMeta(second.Id, null);
            Check.Equal(null, store.Sessions.GetMessage(second.Id)!.Meta);
        });
    }

    private static string Flags(IStorage store, string sessionId) =>
        string.Join(",", store.Sessions.GetMessages(sessionId).Select(m => m.Compacted ? "true" : "false"));
}
