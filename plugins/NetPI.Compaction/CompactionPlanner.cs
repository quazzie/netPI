using System.Text;

namespace NetPI.Compaction;

/// <summary>Which messages are summarized and which are kept.</summary>
public sealed class CompactionPlan
{
    /// <summary>Context in planning order: the latest summary (if any) first, then the messages by seq.</summary>
    public required IReadOnlyList<ChatMessage> Messages { get; init; }
    /// <summary>Index of the first kept message.</summary>
    public required int CutIndex { get; init; }
    /// <summary>Seq of the last summarized non-summary message (argument for MarkCompacted).</summary>
    public required long UpToSeq { get; init; }
    public IEnumerable<ChatMessage> ToSummarize => Messages.Take(CutIndex);
    public IEnumerable<ChatMessage> Kept => Messages.Skip(CutIndex);
    public required long SummarizeTokens { get; init; }
    public required long KeptTokens { get; init; }
    public int SummarizeCount => CutIndex;
}

public static class CompactionPlanner
{
    /// <summary>Token estimate of one message (chars/4 plus a small per-message overhead).</summary>
    public static long Estimate(ChatMessage m) => ModelMessages.EstimateTokens([m]) + 4;

    public static long Estimate(IEnumerable<ChatMessage> messages) => messages.Sum(Estimate);

    public static long EstimateTools(IReadOnlyList<ToolDefinition> tools)
    {
        long chars = 0;
        foreach (var t in tools)
            chars += t.Name.Length + t.Description.Length + t.Parameters.ToJsonString().Length + 16;
        return chars / 4;
    }

    /// <summary>
    /// Pick the cut: keep the most recent messages totalling ≈ <paramref name="keepTokens"/>. The first kept message is
    /// a User/Notice (preferred) or an Assistant message, never a tool result or a summary, and no kept tool result may
    /// answer a call made before the cut. Returns null when there is nothing to compact.
    /// </summary>
    /// <param name="maxKeepTokens">How far past the budget the cut may move back to reach a User boundary.</param>
    public static CompactionPlan? Plan(IReadOnlyList<ChatMessage> context, long keepTokens, long maxKeepTokens)
    {
        var msgs = Order(context);
        var n = msgs.Count;
        var firstReal = n > 0 && msgs[0].Role == MessageRole.Summary ? 1 : 0;
        if (n - firstReal < 2) return null;

        var tokens = new long[n];
        var suffix = new long[n + 1];
        for (var i = n - 1; i >= 0; i--) { tokens[i] = Estimate(msgs[i]); suffix[i] = suffix[i + 1] + tokens[i]; }

        // A cut at i is invalid when a tool result at j >= i answers a call made at a < i.
        var invalid = new bool[n + 1];
        var callOwner = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var j = 0; j < n; j++)
        {
            var m = msgs[j];
            if (m.Role == MessageRole.Assistant)
                foreach (var c in m.ToolCalls) if (!string.IsNullOrEmpty(c.Id)) callOwner[c.Id] = j;
            if (m.Role == MessageRole.Tool)
                foreach (var r in m.ToolResults)
                    if (callOwner.TryGetValue(r.CallId, out var a))
                        for (var i = a + 1; i <= j; i++) invalid[i] = true;
        }

        bool Valid(int i) => i >= firstReal + 1 && i <= n - 1 && !invalid[i]
                             && msgs[i].Role is MessageRole.User or MessageRole.Notice or MessageRole.Assistant;
        bool UserLike(int i) => msgs[i].Role is MessageRole.User or MessageRole.Notice;

        // Budget candidate: the largest c with suffix[c] >= keepTokens (keeps at least the budget).
        var c0 = n - 1;
        while (c0 > 0 && suffix[c0] < keepTokens) c0--;
        maxKeepTokens = Math.Max(maxKeepTokens, keepTokens);

        int? cut = null;
        // 1) a User/Notice boundary at or before the candidate, within maxKeep
        for (var i = c0; i >= 0 && cut is null; i--)
        {
            if (suffix[i] > maxKeepTokens) break;
            if (Valid(i) && UserLike(i)) cut = i;
        }
        // 2) a User/Notice boundary after the candidate that still keeps at least half the budget
        for (var i = c0 + 1; i < n && cut is null; i++)
        {
            if (suffix[i] < keepTokens / 2) break;
            if (Valid(i) && UserLike(i)) cut = i;
        }
        // 3) any valid boundary (assistant) at or before the candidate, within maxKeep
        for (var i = c0; i >= 0 && cut is null; i--)
        {
            if (suffix[i] > maxKeepTokens) break;
            if (Valid(i)) cut = i;
        }
        // 4) any valid boundary after the candidate (keeps less than the budget)
        for (var i = c0 + 1; i < n && cut is null; i++)
            if (Valid(i)) cut = i;
        // 5) any valid boundary before the candidate (keeps more than wanted, still frees older context)
        for (var i = c0; i >= 0 && cut is null; i--)
            if (Valid(i)) cut = i;

        if (cut is not { } k) return null;
        var upTo = 0L;
        for (var i = k - 1; i >= firstReal; i--)
            if (msgs[i].Seq > 0) { upTo = msgs[i].Seq; break; }
        if (upTo <= 0) return null; // unpersisted messages cannot be marked compacted

        return new CompactionPlan
        {
            Messages = msgs, CutIndex = k, UpToSeq = upTo,
            SummarizeTokens = suffix[0] - suffix[k], KeptTokens = suffix[k],
        };
    }

    /// <summary>Latest summary first (older summaries dropped), then the other messages in their seq order.</summary>
    private static List<ChatMessage> Order(IReadOnlyList<ChatMessage> context)
    {
        ChatMessage? summary = null;
        foreach (var m in context)
            if (m.Role == MessageRole.Summary && (summary is null || m.Seq >= summary.Seq)) summary = m;
        var list = new List<ChatMessage>(context.Count);
        if (summary is not null) list.Add(summary);
        list.AddRange(context.Where(m => m.Role != MessageRole.Summary && !m.Compacted));
        return list;
    }
}

/// <summary>Role-tagged plain-text rendering of messages for the summarizer.</summary>
public static class TranscriptSerializer
{
    public const int ToolResultChars = 2000;
    public const int ToolArgsChars = 1000;

    public static string Serialize(ChatMessage m)
    {
        var sb = new StringBuilder();
        switch (m.Role)
        {
            case MessageRole.User:
                sb.Append("[User]\n").Append(m.Text.Trim());
                var images = m.Parts.OfType<ImagePart>().Count();
                if (images > 0) sb.Append($"\n[{images} image(s) attached]");
                break;
            case MessageRole.Notice:
                var kind = m.MetaString("kind");
                sb.Append(kind is null ? "[Notice]\n" : $"[Notice: {kind}]\n").Append(m.Text.Trim());
                break;
            case MessageRole.Summary:
                sb.Append("[Summary of the earlier conversation]\n").Append(m.Text.Trim());
                break;
            case MessageRole.Assistant:
                sb.Append("[Assistant]");
                foreach (var p in m.Parts)
                {
                    switch (p)
                    {
                        case TextPart t when !string.IsNullOrWhiteSpace(t.Text):
                            sb.Append('\n').Append(t.Text.Trim());
                            break;
                        case ToolCallPart c:
                            sb.Append("\n[Tool call] ").Append(c.Name).Append('(').Append(Truncate(c.Arguments.Trim(), ToolArgsChars)).Append(')');
                            break;
                        // Thinking is omitted.
                    }
                }
                if (m.StopReason is "aborted" or "error") sb.Append($"\n[response {m.StopReason}]");
                break;
            case MessageRole.Tool:
                var first = true;
                foreach (var r in m.ToolResults)
                {
                    if (!first) sb.Append('\n');
                    first = false;
                    sb.Append("[Tool result: ").Append(r.Name).Append(r.IsError ? " (error)]\n" : "]\n")
                      .Append(Truncate(r.Content.Trim(), ToolResultChars));
                }
                break;
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>Keep the head and tail of long text with a marker in between.</summary>
    public static string Truncate(string s, int max)
    {
        if (s.Length <= max) return s;
        var head = max * 3 / 4;
        var tail = max - head;
        return s[..head] + $"\n…[{s.Length - max} chars omitted]…\n" + s[^tail..];
    }

    /// <summary>Split serialized entries into chunks of at most <paramref name="maxChars"/> (oversized entries are truncated).</summary>
    public static List<string> Chunk(IEnumerable<string> entries, int maxChars)
    {
        maxChars = Math.Max(maxChars, 500);
        var chunks = new List<string>();
        var sb = new StringBuilder();
        foreach (var raw in entries)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var e = raw.Length > maxChars - 2 ? Truncate(raw, maxChars - 60) : raw;
            if (sb.Length > 0 && sb.Length + e.Length + 2 > maxChars) { chunks.Add(sb.ToString()); sb.Clear(); }
            if (sb.Length > 0) sb.Append("\n\n");
            sb.Append(e);
        }
        if (sb.Length > 0) chunks.Add(sb.ToString());
        return chunks;
    }
}
