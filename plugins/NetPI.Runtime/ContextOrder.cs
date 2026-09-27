namespace NetPI.Runtime;

/// <summary>
/// The order the model reads a chat in. Notices are only ever appended (the cached start of a conversation survives a
/// later one), so the ones made at the chat's first model call are stored after the first user message. Those that
/// describe the chat's setting (<c>meta.setup</c>: working directory, instructions, the skill catalog) go before that
/// message for the model: nothing is cached yet at that call, and the model knows its setting before it reads the request.
/// Notices that answer the message (a skill it loads with /skill:name) stay after it. The chat view shows the same order
/// (chatItems.js).
/// </summary>
internal static class ContextOrder
{
    /// <summary>
    /// The setup notices among the notices right after the first user message go before it, when the context starts with
    /// that message (or only setup notices precede it; not after compaction, whose summary comes first).
    /// </summary>
    public static IReadOnlyList<ChatMessage> FirstTurnNoticesFirst(IReadOnlyList<ChatMessage> messages)
    {
        var first = 0;
        while (first < messages.Count && IsSetup(messages[first])) first++;
        if (first >= messages.Count || messages[first].Role != MessageRole.User) return messages;
        var end = first + 1;
        while (end < messages.Count && messages[end].Role == MessageRole.Notice && messages[end].MetaString("kind") is not ("steer" or "queued")) end++;
        var setup = new List<ChatMessage>();
        var other = new List<ChatMessage>();
        for (var i = first + 1; i < end; i++) (IsSetup(messages[i]) ? setup : other).Add(messages[i]);
        if (setup.Count == 0) return messages;
        var ordered = new List<ChatMessage>(messages.Count);
        for (var i = 0; i < first; i++) ordered.Add(messages[i]);
        ordered.AddRange(setup);
        ordered.Add(messages[first]);
        ordered.AddRange(other);
        for (var i = end; i < messages.Count; i++) ordered.Add(messages[i]);
        return ordered;
    }

    private static bool IsSetup(ChatMessage m) => m.Role == MessageRole.Notice && m.Meta?["setup"] is System.Text.Json.Nodes.JsonValue v && v.TryGetValue<bool>(out var b) && b;
}
