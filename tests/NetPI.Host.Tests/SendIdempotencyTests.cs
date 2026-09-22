using NetPI.Web;
using Xunit;

namespace NetPI.Host.Tests;

/// <summary>
/// astra-1 §11a (F/A): a stable operationId makes chat.send retries idempotent —
/// a retry of the SAME id replays the existing accepted result instead of
/// appending another message or executing another run. The core table is
/// exercised directly (the Web-surface replay path is thin: TryGet to replay,
/// Accept to stamp before the ack).
/// </summary>
public sealed class SendIdempotencyTests
{
    private static SendIdempotency.Accepted Acc(string sid = "s1", string text = "hi",
        string status = "accepted")
        => new(sid, text, status, DateTimeOffset.UtcNow);

    [Fact]
    public void NewOperation_IsNotKnownThenBecomesKnown()
    {
        var s = new SendIdempotency();
        Assert.Null(s.TryGet("op-1"));
        var accepted = s.Accept("op-1", Acc());
        Assert.NotNull(accepted);
        Assert.Same(accepted, s.TryGet("op-1"));
        Assert.Equal("s1", s.TryGet("op-1")!.SessionId);
    }

    [Fact]
    public void RetryingTheSameOperation_ReplaysExistingResult_NotNewAccept()
    {
        var s = new SendIdempotency();
        var first = s.Accept("op-1", Acc(text: "original"));
        // A retry of the SAME id: the FIRST result must win, unchanged — a second
        // Accept must not overwrite it.
        var retry = s.Accept("op-1", Acc(text: "MUTATED-RETRY"));
        Assert.Same(first, retry);
        // a retry must replay the ORIGINAL accepted result, not the retry's bytes
        Assert.Equal("original", s.TryGet("op-1")!.Text);
    }

    [Fact]
    public void DifferentOperations_AreIndependent()
    {
        var s = new SendIdempotency();
        s.Accept("op-1", Acc(sid: "s1", text: "a"));
        s.Accept("op-2", Acc(sid: "s2", text: "b"));
        Assert.Equal("s1", s.TryGet("op-1")!.SessionId);
        Assert.Equal("s2", s.TryGet("op-2")!.SessionId);
        Assert.Null(s.TryGet("op-3"));
    }

    [Fact]
    public void ADeliberateRepeat_UsesANewId_ForTheSameText()
    {
        // Same text, but a DELIBERATE repeat gets a NEW id, so both are accepted
        // (two distinct operations) and the user's intent to send again is honoured.
        var s = new SendIdempotency();
        s.Accept("op-1", Acc(text: "same text"));
        var second = s.Accept("op-2", Acc(text: "same text")); // new id
        Assert.NotNull(second);
        Assert.Equal(2, s.Count); // both distinct operations retained
        Assert.Equal("same text", s.TryGet("op-1")!.Text);
        Assert.Equal("same text", s.TryGet("op-2")!.Text);
    }

    [Fact]
    public void ReleasedId_IsNoLongerKnown()
    {
        var s = new SendIdempotency();
        s.Accept("op-1", Acc());
        s.Release("op-1");
        Assert.Null(s.TryGet("op-1"));
        // A retry after release is a fresh accept (not a replay).
        s.Accept("op-1", Acc(text: "again"));
        Assert.Equal("again", s.TryGet("op-1")!.Text);
    }

    [Fact]
    public void Table_IsBounded_AndEvictsOldest()
    {
        var s = new SendIdempotency(maxEntries: 3);
        s.Accept("op-1", Acc(sid: "s1"));
        s.Accept("op-2", Acc(sid: "s2"));
        s.Accept("op-3", Acc(sid: "s3"));
        Assert.Equal(3, s.Count);
        s.Accept("op-4", Acc(sid: "s4")); // over cap: evict the oldest (op-1)
        Assert.Equal(3, s.Count);
        // the oldest accepted operation is evicted
        Assert.Null(s.TryGet("op-1"));
        Assert.NotNull(s.TryGet("op-2"));
        Assert.NotNull(s.TryGet("op-4"));
    }

    [Fact]
    public void ConcurrentAccepts_ForTheSameId_AgreeOnOneResult()
    {
        // Two threads accept the SAME id concurrently; both must observe ONE
        // authoritative result (no torn / duplicate outcome).
        var s = new SendIdempotency();
        var first = Acc(text: "first");
        var second = Acc(text: "second");
        var t0 = Task.Run(() => s.Accept("op-1", first));
        var t1 = Task.Run(() => s.Accept("op-1", second));
        Task.WaitAll(t0, t1);
        var winner = s.TryGet("op-1")!;
        Assert.Equal(1, s.Count);
        Assert.True(winner.Text == "first" || winner.Text == "second",
            "the stored result must be exactly one of the two submitted, unchanged");
    }
}
