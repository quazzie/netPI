
namespace NetPI.Abstractions;

/// <summary>
/// Repairs persisted transcripts before they are sent to a provider (PLAN §46).
/// A run that died mid-batch (host crash/restart, agent cancellation) leaves an
/// assistant message whose tool calls have no matching tool-result entry. The
/// responses wire rejects such a transcript with
/// <c>function_call_output must contain a non-empty call_id</c> (400), and chat
/// wires want the tool message anyway — so every dangling call gets a synthetic
/// error result ("interrupted") instead of being left orphaned.
/// </summary>
public static class TranscriptSanitizer
{
    public const string InterruptedResultText =
        "Tool execution was interrupted (the run was cancelled or the host shut down). " +
        "The command may or may not have completed — re-check any side effects before retrying.";

    /// <summary>
    /// Returns a new transcript in which every assistant tool call without a
    /// later tool result carries a synthetic error result appended after the
    /// last message of that assistant turn (tool results are stored as separate
    /// <see cref="MessageRole.Tool"/> messages). Messages that already carry a
    /// sanitizer-produced result are left untouched (idempotent).
    /// </summary>
    public static IReadOnlyList<AgentMessage> Sanitize(IReadOnlyList<AgentMessage> messages)
    {
        if (messages.Count == 0) return messages;

        // Pass 1: collect every call id that has a real (or synthetic) result.
        var resolved = new HashSet<string>(StringComparer.Ordinal);
        foreach (var m in messages)
            if (m.Role == MessageRole.Tool)
                foreach (var part in m.Parts)
                    if (part is ToolResultPart tr && !string.IsNullOrEmpty(tr.ToolCallId))
                        resolved.Add(tr.ToolCallId);

        // Pass 2: rebuild, inserting a synthetic tool message directly after
        // each assistant turn that carries dangling calls — adjacency matters
        // because provider wires expect the tool message to follow its call.
        var result = new List<AgentMessage>(messages.Count);
        var changed = false;
        foreach (var m in messages)
        {
            result.Add(m);
            if (m.Role != MessageRole.Assistant) continue;

            var synthetics = new List<MessagePart>();
            foreach (var tc in m.Parts.OfType<ToolCallPart>())
            {
                if (string.IsNullOrEmpty(tc.Id) || resolved.Contains(tc.Id)) continue;
                synthetics.Add(new ToolResultPart(
                    tc.Id, tc.Name,
                    [new TextPart(InterruptedResultText)], IsError: true));
                resolved.Add(tc.Id); // idempotency: a second pass sees these as resolved
                changed = true;
            }
            if (synthetics.Count > 0)
                result.Add(new AgentMessage(m.Id + "-interrupted", MessageRole.Tool, synthetics, m.CreatedAt));
        }

        return changed ? result : messages;
    }
}
