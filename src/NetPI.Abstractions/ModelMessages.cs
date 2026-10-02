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
        // Notices stored while the calls of the last assistant message were still open: they follow the results, see below.
        var held = new List<ChatMessage>();

        void FlushPending()
        {
            if (pendingCalls.Count > 0)
            {
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
            if (held.Count > 0)
            {
                output.AddRange(held);
                held.Clear();
            }
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
                    var kind = m.MetaString("kind");
                    var notice = Clone(m, MessageRole.User, [new TextPart { Text = WrapNotice(m.Text, kind) }]);
                    // The assistant message and each tool result are stored as they happen, so a notice appended while a tool
                    // runs sits between the call and a result that is not in yet. Closing the calls here would answer them
                    // "not executed" and then drop the real results when they arrive (the model reruns what already ran).
                    // The notice waits behind the results instead; if none ever arrive, the calls are closed as before.
                    if (pendingCalls.Count > 0) held.Add(notice);
                    else output.Add(notice);
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

    /// <summary>Wraps a notice body in the <c>&lt;system-notice&gt;</c> element the system prompt names as NetPI's own.
    /// The body is not necessarily harness-authored: skill text, <c>AGENTS.md</c> and subagent reports all come through
    /// here, so a body carrying a tag would render as a second, well-formed notice with a NetPI kind. The tag is
    /// therefore neutralised in the body and the kind is quoted.</summary>
    public static string WrapNotice(string text, string? kind)
    {
        var sb = new StringBuilder();
        sb.Append("<system-notice");
        if (!string.IsNullOrEmpty(kind)) sb.Append(" kind=\"").Append(EscapeAttr(kind)).Append('"');
        sb.Append(">\n").Append(NeutraliseTag(text.Trim())).Append("\n</system-notice>");
        return sb.ToString();
    }

    /// <summary>Escape the characters that end the attribute value or the tag itself.</summary>
    private static string EscapeAttr(string value) =>
        value.Replace("&", "&amp;", StringComparison.Ordinal)
             .Replace("\"", "&quot;", StringComparison.Ordinal)
             .Replace("<", "&lt;", StringComparison.Ordinal);

    /// <summary>Escape the <c>&lt;</c> of every <c>&lt;system-notice&gt;</c> and <c>&lt;/system-notice&gt;</c> in the body,
    /// so neither an opening nor a closing tag from repository or model text can open or close an element. Case
    /// insensitive: <c>&lt;/SYSTEM-NOTICE&gt;</c> closes it just as well.</summary>
    private static string NeutraliseTag(string body)
    {
        const string name = "system-notice";
        var sb = new StringBuilder(body.Length + 16);
        var from = 0;   // start of the text not yet copied
        for (var i = 0; i < body.Length; i++)
        {
            if (body[i] != '<') continue;
            var nameAt = i + 1;
            if (nameAt < body.Length && body[nameAt] == '/') nameAt++;   // a closing tag
            if (nameAt + name.Length > body.Length || !body.AsSpan(nameAt, name.Length).Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
            sb.Append(body, from, i - from).Append("&lt;");
            from = i + 1;   // the name stays readable, only the character that starts a tag is escaped
            i = nameAt - 1;
        }
        if (from == 0) return body;
        sb.Append(body, from, body.Length - from);
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
