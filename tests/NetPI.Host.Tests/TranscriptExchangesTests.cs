using System.Text.Json;
using NetPI.Abstractions;
using Xunit;

namespace NetPI.Host.Tests;

/// <summary>
/// docs/plans/compaction-tool-history.md §2: a complete tool exchange (an
/// assistant tool-call message plus its contiguous matching result messages) is
/// the INDIVISIBLE unit of a legal compaction boundary. <see cref="TranscriptExchanges"/>
/// is the shared definition both the boundary computation and the sanitizer use.
/// </summary>
public class TranscriptExchangesTests
{
    private static SessionEntry Msg(int seq, AgentMessage m) =>
        new("e" + seq, "s1", EntryKind.Message, m, null, DateTimeOffset.UtcNow, Sequence: seq);

    private static SessionEntry NonMsg(int seq) =>
        new("n" + seq, "s1", EntryKind.Metadata, null, null, DateTimeOffset.UtcNow, Sequence: seq);

    private static AgentMessage Call(int seq, params string[] ids) =>
        new("a" + seq, MessageRole.Assistant,
            ids.Select(id => new ToolCallPart(id, "bash", JsonSerializer.SerializeToElement(new { }))).ToList(),
            DateTimeOffset.UtcNow);

    private static AgentMessage Result(int seq, params string[] ids) =>
        new("t" + seq, MessageRole.Tool,
            ids.Select(id => new ToolResultPart(id, "bash", [new TextPart("ok")], false)).ToList(),
            DateTimeOffset.UtcNow);

    [Fact]
    public void Incident_TwoMessageExchange_IsOneCompleteExchange()
    {
        // The exact shape from §1: assistant call (215) + result (216).
        var entries = new List<SessionEntry>
        {
            Msg(214, new("u214", MessageRole.User, [new TextPart("hi")], DateTimeOffset.UtcNow)),
            Msg(215, Call(215, "call_5974")),
            Msg(216, Result(216, "call_5974")),
        };

        var exchange = Assert.Single(TranscriptExchanges.Group(entries));

        Assert.Equal(215, exchange.CallSequence);
        Assert.Equal(216, exchange.FirstResultSequence);
        Assert.Equal(216, exchange.LastResultSequence);
        Assert.Equal(["call_5974"], exchange.CallIds);
        Assert.True(exchange.Complete);
    }

    [Fact]
    public void Splitting_ABoundaryInsideTheExchange_ReturnsIt()
    {
        var entries = new List<SessionEntry>
        {
            Msg(215, Call(215, "call_1")),
            Msg(216, Result(216, "call_1")),
        };
        var exchanges = TranscriptExchanges.Group(entries);

        // A cut at 216 retains only the result (216) and summarizes the call (215)
        // → illegal. The splitting exchange is returned so the boundary moves to 215.
        var split = TranscriptExchanges.Splitting(exchanges, 216);
        Assert.NotNull(split);
        Assert.Equal(215, split!.CallSequence);
    }

    [Theory]
    [InlineData(215)] // whole exchange retained (B <= call)
    [InlineData(217)] // whole exchange summarized (B > last result)
    public void Splitting_ALegalBoundary_ReturnsNull(int boundary)
    {
        var entries = new List<SessionEntry>
        {
            Msg(215, Call(215, "call_1")),
            Msg(216, Result(216, "call_1")),
        };
        Assert.Null(TranscriptExchanges.Splitting(TranscriptExchanges.Group(entries), boundary));
    }

    [Fact]
    public void MultiCallBatch_TwoCallsTwoResultMessages_IsOneExchange()
    {
        var entries = new List<SessionEntry>
        {
            Msg(7, Call(7, "call_x", "call_y")),
            Msg(8, Result(8, "call_x")),
            Msg(9, Result(9, "call_y")),
        };

        var exchange = Assert.Single(TranscriptExchanges.Group(entries));

        Assert.Equal(7, exchange.CallSequence);
        Assert.Equal(8, exchange.FirstResultSequence);
        Assert.Equal(9, exchange.LastResultSequence);
        Assert.Equal(["call_x", "call_y"], exchange.CallIds);
        Assert.True(exchange.Complete);
    }

    [Fact]
    public void AConversationMessage_EndsAnExchange()
    {
        // call + result, then a user message, then a NEW call/result exchange.
        var entries = new List<SessionEntry>
        {
            Msg(1, Call(1, "call_a")),
            Msg(2, Result(2, "call_a")),
            Msg(3, new("u3", MessageRole.User, [new TextPart("next")], DateTimeOffset.UtcNow)),
            Msg(4, Call(4, "call_b")),
            Msg(5, Result(5, "call_b")),
        };

        var exchanges = TranscriptExchanges.Group(entries);

        Assert.Equal(2, exchanges.Count);
        Assert.Equal(1, exchanges[0].CallSequence);
        Assert.Equal(4, exchanges[1].CallSequence);
    }

    [Fact]
    public void NonMessageEntries_DoNotBreakAnExchange()
    {
        // A checkpoint/metadata entry interleaved between call and result must not
        // split the exchange (PLAN: "non-message storage entries do not break a
        // tool exchange's contiguity").
        var entries = new List<SessionEntry>
        {
            Msg(1, Call(1, "call_a")),
            NonMsg(2),
            Msg(3, Result(3, "call_a")),
        };

        var exchange = Assert.Single(TranscriptExchanges.Group(entries));

        Assert.Equal(1, exchange.CallSequence);
        Assert.Equal(3, exchange.LastResultSequence);
        Assert.True(exchange.Complete);
    }

    [Fact]
    public void AWindowBeginningMidBatch_IsHeadless()
    {
        // The read window starts at the result — the call message is not in the
        // window. The exchange is headless and therefore never Complete (its call
        // set is unknown); the boundary logic must treat it conservatively.
        var entries = new List<SessionEntry>
        {
            Msg(2, Result(2, "call_a")),
        };

        var exchange = Assert.Single(TranscriptExchanges.Group(entries));

        Assert.True(exchange.Headless);
        Assert.Equal(0, exchange.CallSequence);
        Assert.False(exchange.Complete);
    }

    [Fact]
    public void AnUnresolvedCall_IsNotComplete()
    {
        // call_a has a result; call_b does not → the exchange is incomplete (a
        // dangling call the sanitizer would patch with a synthetic result).
        var entries = new List<SessionEntry>
        {
            Msg(1, Call(1, "call_a", "call_b")),
            Msg(2, Result(2, "call_a")),
        };

        var exchange = Assert.Single(TranscriptExchanges.Group(entries));

        Assert.False(exchange.Complete);
        Assert.Equal(["call_a", "call_b"], exchange.CallIds);
    }
}
