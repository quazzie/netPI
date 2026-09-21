using System.Text.Json;

namespace NetPI.Abstractions;

/// <summary>
/// astra-1 A (Package A, message identity): deterministic identities for
/// transcript messages whose identity must be STABLE across runs — the
/// effective system context (derived from its content) and compaction
/// summaries (derived from their persisted checkpoint). Stable IDs keep
/// provider fingerprints stable (legitimate cache reuse) without weakening
/// the fingerprint check itself.
/// </summary>
public static class MessageIdentity
{
    /// <summary>A 16-hex identity derived from <paramref name="prefix"/> + content.</summary>
    public static string DeterministicId(string prefix, string content)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(prefix + "\u0000" + content));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    /// <summary>Identity of a compaction summary, derived from its persisted checkpoint entry.</summary>
    public static string SummaryId(string checkpointEntryId)
        => DeterministicId("compaction", checkpointEntryId);
}

/// <summary>Role of a message in the provider-neutral transcript (PLAN §8).</summary>
public enum MessageRole
{
    User = 0,
    Assistant = 1,
    System = 2,
    Tool = 3,
}

/// <summary>Provider-neutral agent message.</summary>
public sealed record AgentMessage(
    string Id,
    MessageRole Role,
    IReadOnlyList<MessagePart> Parts,
    DateTimeOffset CreatedAt)
{
    public override string ToString() => $"{Role}: {Parts.Count} part(s)";
}

/// <summary>Abstract part of an <see cref="AgentMessage"/>. All parts are stable, serializable, and provider-neutral.</summary>
public abstract record MessagePart
{
    /// <summary>The concrete part kind (e.g. "text", "tool-call").</summary>
    public abstract string Kind { get; }
}

/// <summary>Plain text segment.</summary>
public sealed record TextPart(string Text) : MessagePart
{
    public override string Kind => "text";
}

/// <summary>Reasoning/thinking segment, optionally signed.</summary>
public sealed record ThinkingPart(string Text, string? Signature = null) : MessagePart
{
    public override string Kind => "thinking";
}

/// <summary>A tool invocation issued by the model.</summary>
public sealed record ToolCallPart(string Id, string Name, JsonElement Arguments) : MessagePart
{
    public override string Kind => "tool-call";
}

/// <summary>Structured result returned by a tool.</summary>
public sealed record ToolResultPart(
    string ToolCallId,
    string ToolName,
    IReadOnlyList<MessagePart> Parts,
    bool IsError = false) : MessagePart
{
    public override string Kind => "tool-result";
}

/// <summary>An image attachment.</summary>
public sealed record ImagePart(string MimeType, byte[] Data) : MessagePart
{
    public override string Kind => "image";
}

/// <summary>Result of a single tool execution, as surfaced to the model/agent.</summary>
public sealed record ToolResult(
    string ToolCallId,
    string ToolName,
    MessagePart[] Parts,
    bool IsError = false,
    DateTimeOffset? CompletedAt = null);
