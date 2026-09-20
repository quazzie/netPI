using NetPI.Abstractions;
using NetPI.AutoCompact;
using Xunit;

namespace NetPI.Host.Tests;

/// <summary>
/// PLAN §32: context measurement must be "last provider-reported prompt usage
/// (authoritative) + estimate of the messages added since" — not a whole-session
/// chars/4 recount. Without a usage base it falls back to a full estimate.
/// </summary>
public class AutoCompactTests
{
    private static AgentMessage Msg(int n, string text)
        => new("m" + n, MessageRole.User, [new TextPart(text)], DateTimeOffset.UtcNow);

    private static string Chars(string c, int n) {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < n; i++) sb.Append(c, 0, 1);
        return sb.ToString();
    }

    /// <summary>A base usage of 1000 covers the first 2 messages; only the
    /// third (added after the model call) should be estimated.</summary>
    [Fact]
    public void Estimate_AddsOnlyMessagesAddedSinceUsage()
    {
        IReadOnlyList<AgentMessage> messages = [Msg(1, Chars("a", 400)), Msg(2, Chars("b", 400)), Msg(3, Chars("c", 400))];

        var est = TokenEstimator.EstimateContext(messages, lastPromptTokens: 1000, lastUsageMessageCount: 2);

        // 1000 (usage base) + estimate of just the 400-char third message (~100).
        Assert.True(est > 1000, $"expected base+delta, got {est}");
        Assert.True(est < 1000 + 200, $"only the delta should be estimated, got {est}");
    }

    /// <summary>With no usage base (no model call yet) → full estimate of everything.</summary>
    [Fact]
    public void Estimate_FallsBackToFullEstimateWithoutUsage()
    {
        IReadOnlyList<AgentMessage> messages = [Msg(1, Chars("x", 400)), Msg(2, Chars("y", 400))];

        var full = TokenEstimator.EstimateList(messages);
        var est = TokenEstimator.EstimateContext(messages, lastPromptTokens: 0, lastUsageMessageCount: 0);

        Assert.Equal(full, est);
    }

    /// <summary>A usage base alone (no message count) is not trustworthy → full estimate.</summary>
    [Fact]
    public void Estimate_RequiresMessageCountForBase()
    {
        IReadOnlyList<AgentMessage> messages = [Msg(1, Chars("z", 400)), Msg(2, Chars("w", 400))];

        var full = TokenEstimator.EstimateList(messages);
        var est = TokenEstimator.EstimateContext(messages, lastPromptTokens: 500, lastUsageMessageCount: 0);

        Assert.Equal(full, est);
    }

    /// <summary>A usage base that covers ALL messages → just the base (nothing added since).</summary>
    [Fact]
    public void Estimate_BaseCoversAllMessagesIsJustTheBase()
    {
        IReadOnlyList<AgentMessage> messages = [Msg(1, Chars("q", 400)), Msg(2, Chars("r", 400))];

        var est = TokenEstimator.EstimateContext(messages, lastPromptTokens: 1000, lastUsageMessageCount: 10);

        Assert.Equal(1000, est);
    }
}
