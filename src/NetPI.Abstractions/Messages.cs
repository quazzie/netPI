using System.Text;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace NetPI;

public enum MessageRole
{
    /// <summary>Human (or another agent) input.</summary>
    User,
    /// <summary>Model output: text, thinking and tool calls.</summary>
    Assistant,
    /// <summary>Tool results (one or more <see cref="ToolResultPart"/>).</summary>
    Tool,
    /// <summary>Harness notice (project changed, nudge, agent message...). Sent to the model as a user message.</summary>
    Notice,
    /// <summary>Compaction summary replacing older (compacted) messages in the model context.</summary>
    Summary,
}

/// <summary>A persisted conversation entry.</summary>
public sealed class ChatMessage
{
    public long Id { get; set; }
    public long Seq { get; set; }
    public string SessionId { get; set; } = "";
    public MessageRole Role { get; set; }
    public List<MessagePart> Parts { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    // Assistant metadata
    public string? Provider { get; set; }
    public string? Model { get; set; }
    /// <summary>stop | tool_use | length | aborted | error | content_filter</summary>
    public string? StopReason { get; set; }
    public Usage? Usage { get; set; }
    public long? DurationMs { get; set; }

    /// <summary>Excluded from the model context (replaced by a summary). Still shown in the UI.</summary>
    public bool Compacted { get; set; }

    /// <summary>Free-form metadata: kind (nudge|project|agent-message|...), agentId, source, repaired, error...</summary>
    public JsonObject? Meta { get; set; }

    [JsonIgnore] public IEnumerable<ToolCallPart> ToolCalls => Parts.OfType<ToolCallPart>();
    [JsonIgnore] public IEnumerable<ToolResultPart> ToolResults => Parts.OfType<ToolResultPart>();

    /// <summary>Concatenated text parts.</summary>
    [JsonIgnore]
    public string Text
    {
        get
        {
            var sb = new StringBuilder();
            foreach (var p in Parts)
                if (p is TextPart t) { if (sb.Length > 0) sb.Append('\n'); sb.Append(t.Text); }
            return sb.ToString();
        }
    }

    public string? MetaString(string key) => Meta?[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    public static ChatMessage UserText(string text) => new() { Role = MessageRole.User, Parts = [new TextPart { Text = text }] };

    public static ChatMessage NoticeText(string text, string kind) => new()
    {
        Role = MessageRole.Notice,
        Parts = [new TextPart { Text = text }],
        Meta = new JsonObject { ["kind"] = kind },
    };
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(TextPart), "text")]
[JsonDerivedType(typeof(ThinkingPart), "thinking")]
[JsonDerivedType(typeof(ToolCallPart), "tool_call")]
[JsonDerivedType(typeof(ToolResultPart), "tool_result")]
[JsonDerivedType(typeof(ImagePart), "image")]
public abstract class MessagePart
{
    /// <summary>Opaque provider replay data (e.g. Responses item ids, encrypted reasoning).</summary>
    public JsonObject? ProviderData { get; set; }
}

public sealed class TextPart : MessagePart
{
    public string Text { get; set; } = "";
}

public sealed class ThinkingPart : MessagePart
{
    public string Text { get; set; } = "";
    /// <summary>Anthropic thinking signature (must be replayed verbatim).</summary>
    public string? Signature { get; set; }
    /// <summary>Redacted/encrypted reasoning payload.</summary>
    public string? Redacted { get; set; }
    public long? DurationMs { get; set; }
}

public sealed class ToolCallPart : MessagePart
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>Raw JSON arguments exactly as produced by the model (may be invalid JSON).</summary>
    public string Arguments { get; set; } = "{}";
}

public sealed class ToolResultPart : MessagePart
{
    public string CallId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Content { get; set; } = "";
    public bool IsError { get; set; }
    public List<ImagePart>? Images { get; set; }
    /// <summary>Structured details for the UI (diffs, exit codes...). Not sent to the model.</summary>
    public JsonNode? Details { get; set; }
    public long? DurationMs { get; set; }
}

public sealed class ImagePart : MessagePart
{
    public string MediaType { get; set; } = "image/png";
    /// <summary>Base64 data.</summary>
    public string Data { get; set; } = "";
}

public sealed class Usage
{
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long CacheReadTokens { get; set; }
    public long CacheWriteTokens { get; set; }
    public long ReasoningTokens { get; set; }
    /// <summary>What the call cost in USD, when the provider reports it (OpenRouter does); null = not reported.</summary>
    public double? CostUsd { get; set; }

    /// <summary>Approximate size of the context after this call (prompt + completion).</summary>
    [JsonIgnore] public long ContextTokens => InputTokens + CacheReadTokens + CacheWriteTokens + OutputTokens;

    public void Add(Usage? other)
    {
        if (other is null) return;
        InputTokens += other.InputTokens; OutputTokens += other.OutputTokens;
        CacheReadTokens += other.CacheReadTokens; CacheWriteTokens += other.CacheWriteTokens;
        ReasoningTokens += other.ReasoningTokens;
        if (other.CostUsd is { } c) CostUsd = (CostUsd ?? 0) + c;
    }
}

/// <summary>Input delivered to an agent (from the user, another agent or the harness).</summary>
public sealed class UserInput
{
    public string Id { get; init; } = Ids.New("in");
    public string Text { get; init; } = "";
    public List<ImagePart>? Images { get; init; }
    /// <summary>"user", "agent:&lt;id&gt;", "system".</summary>
    public string Source { get; init; } = "user";
    /// <summary>Persist as a notice instead of a user message (agent-to-agent, harness messages).</summary>
    public bool AsNotice { get; init; }
    public string? NoticeKind { get; init; }
    /// <summary>Extra metadata merged into the persisted message's Meta (e.g. agentName, sessionId for agent notices).</summary>
    public JsonObject? Meta { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}
