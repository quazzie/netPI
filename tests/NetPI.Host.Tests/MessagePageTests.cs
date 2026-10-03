using System.Text.Json.Nodes;
using NetPI.Host.Rpc;

namespace NetPI.Host.Tests;

/// <summary>
/// The page <c>sessions.messages</c> answers with: the NEWEST whole messages while they fit its byte budget, ascending,
/// and <c>hasMore</c> for the rest, which the client follows with <c>beforeSeq</c> (idea-8l3mfo).
/// <para>
/// How much a page reads out of the store is the memory it costs: a chat of screenshots holds megabytes of base64 per
/// message, and the old handler read <c>limit + 1</c> messages up front only to drop the ones that did not fit — 60 × 5 MB
/// allocated 1.2 GB to answer with one message. The page is read in growing windows instead, so the rows it reads are the
/// rows it keeps plus the one that did not fit, and these tests count them.
/// </para>
/// </summary>
public static class MessagePageTests
{
    public static void Register(TestRunner r)
    {
        r.Add("sessions.messages: a page of big messages reads only the ones it keeps", BigMessagesStopTheWindow);
        r.Add("sessions.messages: the limit bounds the page, hasMore says what is left", LimitAndHasMore);
        r.Add("sessions.messages: beforeSeq pages back, and a message over the budget still comes back", PagingBack);
        r.Add("sessions.messages: the handler answers a real session with the newest messages that fit", ThroughTheRpc);
    }

    /// <summary>A reader over a chat that counts what it hands out: the rows a page pulled are its memory bound.</summary>
    private sealed class Reader(List<ChatMessage> all)
    {
        /// <summary>Every row handed to the page, whether it kept it or not.</summary>
        public int RowsRead { get; private set; }
        /// <summary>The windows the page asked for, in order (beforeSeq, count).</summary>
        public List<(long? Before, int Limit)> Windows { get; } = [];

        public List<ChatMessage> Read(long? beforeSeq, int limit)
        {
            Windows.Add((beforeSeq, limit));
            var rows = all.Where(m => beforeSeq is not { } before || m.Seq < before).ToList();
            var page = rows.Skip(Math.Max(0, rows.Count - limit)).ToList();
            RowsRead += page.Count;
            return page;
        }
    }

    private static ChatMessage Message(long seq, string image) => new()
    {
        Id = seq, Seq = seq, SessionId = "ses_1", Role = MessageRole.User,
        Parts = [new TextPart { Text = $"message {seq}" }, new ImagePart { MediaType = "image/png", Data = image }],
    };

    private static List<long> Seqs(IEnumerable<ChatMessage> messages) => messages.Select(m => m.Seq).ToList();

    /// <summary>The page holds exactly these seqs: a list is compared as a list, not by reference.</summary>
    private static void Page(long[] expected, List<long> actual, string what) =>
        Check.True(actual.SequenceEqual(expected), $"{what}: expected {string.Join(",", expected)}, got {string.Join(",", actual)}");

    private static void BigMessagesStopTheWindow()
    {
        // Ten messages of ~3 MiB of inline image data: the budget holds two of them.
        var image = new string('x', 3 * 1024 * 1024);
        var reader = new Reader([.. Enumerable.Range(1, 10).Select(i => Message(i, image))]);

        var (page, hasMore) = CoreRpc.MessagePage(reader.Read, null, 60);
        Page([9L, 10L], Seqs(page), "the newest whole messages that fit the budget");
        Check.True(hasMore, "the eight the budget dropped are paged back for with beforeSeq");
        Check.Equal(3, reader.RowsRead, "the two it keeps and the one that did not fit — not the ten it was asked for");
        Check.Equal(2, reader.Windows.Count, "the page grew its window instead of reading the limit up front");
    }

    private static void LimitAndHasMore()
    {
        var image = new string('x', 1024);
        var reader = new Reader([.. Enumerable.Range(1, 10).Select(i => Message(i, image))]);

        var (page, hasMore) = CoreRpc.MessagePage(reader.Read, null, 4);
        Page([7L, 8L, 9L, 10L], Seqs(page), "the newest limit messages");
        Check.True(hasMore, "six older messages are left");

        var (all, noMore) = CoreRpc.MessagePage(reader.Read, null, 10);
        Page([1L, 2L, 3L, 4L, 5L, 6L, 7L, 8L, 9L, 10L], Seqs(all), "the whole chat");
        Check.False(noMore, "the whole chat fits: nothing to page back for");

        var wide = new Reader([.. Enumerable.Range(1, 10).Select(i => Message(i, image))]);
        var (ten, nothing) = CoreRpc.MessagePage(wide.Read, null, 60);
        Check.Equal(10, ten.Count);
        Check.False(nothing);
        Check.Equal(10, wide.RowsRead, "a limit wider than the chat reads the chat, not the limit");

        var empty = new Reader([]);
        var (none, emptyHasMore) = CoreRpc.MessagePage(empty.Read, null, 60);
        Check.Equal(0, none.Count);
        Check.False(emptyHasMore);
        Check.Equal(1, empty.Windows.Count, "one read, not a window per message of a chat that has none");
    }

    private static void PagingBack()
    {
        var image = new string('x', 1024);
        var reader = new Reader([.. Enumerable.Range(1, 10).Select(i => Message(i, image))]);

        var (page, hasMore) = CoreRpc.MessagePage(reader.Read, 6, 4);
        Page([2L, 3L, 4L, 5L], Seqs(page), "the four messages ending just before seq 6");
        Check.True(hasMore);
        Check.Equal(5, reader.RowsRead, "the four it keeps and the one that did not fit");

        // A message bigger than the whole budget is still the one a client has to see: the page is never empty because
        // of it, and it does not walk further back than the message it would have to drop anyway.
        var whale = new string('y', 12 * 1024 * 1024);
        var big = new Reader([.. Enumerable.Range(1, 3).Select(i => Message(i, whale))]);
        var (one, bigHasMore) = CoreRpc.MessagePage(big.Read, null, 60);
        Page([3L], Seqs(one), "the newest message, though it does not fit the budget");
        Check.True(bigHasMore);
        Check.Equal(2, big.RowsRead, "one message to keep and the one that did not fit");
    }

    private static async Task ThroughTheRpc()
    {
        await using var server = await PluginTests.StartAsync(T.TempDir("noplugins"));
        var sessions = server.Kernel.Sessions;
        var chat = sessions.CreateSession(new SessionInfo { Title = "screenshots" });
        var shot = new string('A', 1300 * 1024);
        for (var i = 0; i < 20; i++)
            sessions.AppendMessage(chat.Id, new ChatMessage
            {
                Role = MessageRole.User,
                Parts = [new TextPart { Text = $"shot {i}" }, new ImagePart { MediaType = "image/png", Data = shot }],
            });

        var first = await Page(server, chat.Id, 60);
        var firstSeqs = Seqs(first);
        Check.True(firstSeqs.Count > 0 && firstSeqs.Count < 20, $"the budget cut the 20-message page short (kept {firstSeqs.Count})");
        Check.Equal(20L, firstSeqs[^1], "the page ends at the newest message");
        Check.True(firstSeqs.SequenceEqual(firstSeqs.Order()), "the page is ascending by seq");
        Check.True(first["hasMore"]!.GetValue<bool>(), "the rest is paged back for with beforeSeq");

        // paging back continues where the budget stopped, with no gap and no repeat (the walk itself is ServerTests')
        var next = Seqs(await Page(server, chat.Id, 60, beforeSeq: firstSeqs[0]));
        Check.Equal(firstSeqs[0] - 1, next[^1], "the page before continues at the message in front of the first");
        Check.True(next.All(s => s < firstSeqs[0]), "and holds nothing the first page already had");

        // a chat of small messages is bounded by the limit, not by the budget
        var talk = sessions.CreateSession(new SessionInfo { Title = "talk" });
        for (var i = 0; i < 30; i++)
            sessions.AppendMessage(talk.Id, new ChatMessage { Role = MessageRole.User, Parts = [new TextPart { Text = $"line {i}" }] });

        var all = await Page(server, talk.Id, 2000);
        Page([.. Enumerable.Range(1, 30).Select(i => (long)i)], Seqs(all), "the whole chat comes back");
        Check.False(all["hasMore"]!.GetValue<bool>(), "nothing behind it");

        var ten = await Page(server, talk.Id, 10);
        Page([.. Enumerable.Range(21, 10).Select(i => (long)i)], Seqs(ten), "the newest ten");
        Check.True(ten["hasMore"]!.GetValue<bool>());
    }

    /// <summary>The handler's own answer, as the wire has it.</summary>
    private static async Task<JsonObject> Page(NetPiServer server, string id, int limit, long? beforeSeq = null)
    {
        var answer = await server.Kernel.Rpc.InvokeAsync("sessions.messages", beforeSeq is null
            ? new { id, limit }
            : new { id, limit, beforeSeq });
        return NetPiJson.ToNode(answer)!.AsObject();
    }

    private static List<long> Seqs(JsonObject page) =>
        [.. page["messages"]!.AsArray().Select(m => m!["seq"]!.GetValue<long>())];
}
