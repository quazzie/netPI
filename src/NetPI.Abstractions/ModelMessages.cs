using System.Text;

namespace NetPI;

/// <summary>
/// Normalizes a session transcript into something every provider can send. The host applies this
/// before calling a provider, so providers can assume:
/// <list type="bullet">
/// <item>Roles are only User, Assistant and Tool (Notice/Summary become User text).</item>
/// <item>Every tool call has exactly one result, in the Tool message(s) directly following its assistant message.</item>
/// <item>No orphan tool results, no empty messages.</item>
/// </list>
/// </summary>
public static class ModelMessages
{
    public static List<ChatMessage> Normalize(IReadOnlyList<ChatMessage> input)
    {
        var output = new List<ChatMessage>(input.Count + 4);
        var pendingCalls = new List<ToolCallPart>();
        var answered = new HashSet<string>(StringComparer.Ordinal);
        var resultsBuffer = new List<ToolResultPart>();

        void FlushPending()
        {
            if (pendingCalls.Count == 0) return;
            var parts = new List<MessagePart>();
            foreach (var r in resultsBuffer) parts.Add(r);
            foreach (var call in pendingCalls)
            {
                if (answered.Contains(call.Id)) continue;
                parts.Add(new ToolResultPart
                {
                    CallId = call.Id, Name = call.Name, IsError = true,
                    Content = "Tool call was not executed (the run was interrupted).",
                });
            }
            if (parts.Count > 0) output.Add(new ChatMessage { Role = MessageRole.Tool, Parts = parts, SessionId = "" });
            pendingCalls.Clear(); answered.Clear(); resultsBuffer.Clear();
        }

        foreach (var m in input)
        {
            switch (m.Role)
            {
                case MessageRole.Tool:
                    foreach (var r in m.ToolResults)
                    {
                        if (pendingCalls.Any(c => c.Id == r.CallId) && answered.Add(r.CallId))
                            resultsBuffer.Add(r);
                    }
                    break;

                case MessageRole.Assistant:
                    FlushPending();
                    var aparts = m.Parts.Where(p => p is not TextPart t || !string.IsNullOrEmpty(t.Text)).ToList();
                    if (aparts.Count == 0) break;
                    var copy = Clone(m, MessageRole.Assistant, aparts);
                    output.Add(copy);
                    pendingCalls.AddRange(copy.ToolCalls);
                    break;

                case MessageRole.User:
                    FlushPending();
                    if (m.Parts.Count > 0) output.Add(m);
                    break;

                case MessageRole.Notice:
                    FlushPending();
                    var kind = m.MetaString("kind");
                    output.Add(Clone(m, MessageRole.User, [new TextPart { Text = WrapNotice(m.Text, kind) }]));
                    break;

                case MessageRole.Summary:
                    FlushPending();
                    output.Add(Clone(m, MessageRole.User, [new TextPart
                    {
                        Text = "<conversation-summary>\nThe earlier part of this conversation was compacted. Summary:\n\n" + m.Text + "\n</conversation-summary>",
                    }]));
                    break;
            }
        }
        FlushPending();
        return output;
    }

    public static string WrapNotice(string text, string? kind)
    {
        var sb = new StringBuilder();
        sb.Append("<system-notice");
        if (!string.IsNullOrEmpty(kind)) sb.Append(" kind=\"").Append(kind).Append('"');
        sb.Append(">\n").Append(text.Trim()).Append("\n</system-notice>");
        return sb.ToString();
    }

    private static ChatMessage Clone(ChatMessage m, MessageRole role, List<MessagePart> parts) => new()
    {
        Id = m.Id, Seq = m.Seq, SessionId = m.SessionId, Role = role, Parts = parts, CreatedAt = m.CreatedAt,
        Provider = m.Provider, Model = m.Model, StopReason = m.StopReason, Usage = m.Usage, Meta = m.Meta,
    };

    /// <summary>Rough token estimate (chars/4) for messages without usage data.</summary>
    public static long EstimateTokens(IEnumerable<ChatMessage> messages)
    {
        long chars = 0;
        foreach (var m in messages)
            foreach (var p in m.Parts)
                chars += p switch
                {
                    TextPart t => t.Text.Length,
                    ThinkingPart th => th.Text.Length,
                    ToolCallPart c => c.Arguments.Length + c.Name.Length + 16,
                    ToolResultPart r => r.Content.Length + 16 + (r.Images?.Count ?? 0) * 4000,
                    ImagePart => 4000,
                    _ => 0,
                };
        return chars / 4;
    }

    public static long EstimateTokens(string? text) => (text?.Length ?? 0) / 4;
}
