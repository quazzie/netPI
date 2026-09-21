using System.Text.Json;
using System.Text.Json.Serialization;
using NetPI.Abstractions;

namespace NetPI.Storage.Sqlite;

/// <summary>
/// Serializes <see cref="AgentMessage"/> and its provider-neutral parts to a
/// JSON <see cref="JsonElement"/> for storage and back (PLAN §8, §29). Uses a
/// discriminator on <c>kind</c> so the closed-over <see cref="MessagePart"/>
/// hierarchy round-trips without leaking implementation types.
/// </summary>
public static class MessageSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private sealed record Envelope(
        string Id,
        MessageRole Role,
        DateTimeOffset CreatedAt,
        JsonElement[] Parts)
    {
        public AgentMessage ToMessage()
        {
            var parts = Parts.Length == 0
                ? Array.Empty<MessagePart>()
                : Parts.Select(DeserializePart).ToArray();
            return new AgentMessage(Id, Role, parts, CreatedAt);
        }
    }

    private static Envelope ToEnvelope(AgentMessage m) => new(
        m.Id,
        m.Role,
        m.CreatedAt,
        m.Parts.Select(SerializePart).ToArray());

    public static JsonElement Serialize(AgentMessage message)
    {
        return JsonSerializer.SerializeToElement(ToEnvelope(message), Options);
    }

    public static AgentMessage Deserialize(JsonElement element)
    {
        var env = element.Deserialize<Envelope>(Options)
            ?? throw new InvalidOperationException("Malformed session payload: null envelope.");
        return env.ToMessage();
    }

    // ---- parts (discriminated by kind) ------------------------------------

    private static JsonElement SerializePart(MessagePart p) => p switch
    {
        TextPart t => Raw(new PartDto("text", t.Text)),
        ThinkingPart th => Raw(new PartDto("thinking", th.Text, signature: th.Signature)),
        ImagePart im => Raw(new PartDto("image", mimeType: im.MimeType, data: Convert.ToBase64String(im.Data))),
        ToolCallPart tc => Raw(new PartDto("tool-call", id: tc.Id, name: tc.Name, Arguments: tc.Arguments)),
        ToolResultPart tr => Raw(new PartDto(
            "tool-result",
            toolCallId: tr.ToolCallId,
            toolName: tr.ToolName,
            isError: tr.IsError,
            parts: tr.Parts.Select(SerializePart).ToArray())),
        _ => throw new InvalidOperationException($"Unknown part kind for {p.GetType()}")
    };

    private static JsonElement Raw(PartDto dto)
    {
        return JsonSerializer.SerializeToElement(dto, Options);
    }

    private static MessagePart DeserializePart(JsonElement el)
    {
        var kind = el.TryGetProperty("kind", out var k) ? k.GetString() : null;
        string? id = S(el, "id");
        string? name = S(el, "name");
        string? sig = S(el, "signature");
        string? mime = S(el, "mimeType");
        string? data = S(el, "data");
        string? text = S(el, "text");
        string? toolCallId = S(el, "toolCallId");
        string? toolName = S(el, "toolName");
        var args = el.TryGetProperty("arguments", out var a) ? a.Clone() : default;
        bool isError = el.TryGetProperty("isError", out var ie) && ie.GetBoolean();
        var inner = el.TryGetProperty("parts", out var innerEl)
            ? innerEl.EnumerateArray().Select(DeserializePart).ToArray()
            : Array.Empty<MessagePart>();

        return kind switch
        {
            "text" => new TextPart(text ?? string.Empty),
            "thinking" => new ThinkingPart(text ?? string.Empty, sig),
            "image" => new ImagePart(mime ?? "application/octet-stream",
                data is null ? Array.Empty<byte>() : Convert.FromBase64String(data)),
            "tool-call" => new ToolCallPart(id ?? "", name ?? "", args),
            "tool-result" => new ToolResultPart(toolCallId ?? "", toolName ?? "", inner, isError),
            _ => throw new InvalidOperationException($"Unknown part kind '{kind}'")
        };
    }

    private static string? S(JsonElement el, string name) =>
        el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    /// <summary>Flattened part DTO with every optional field; only the fields set for a given kind are emitted.</summary>
    private sealed record PartDto(
        string kind,
        string? text = null,
        string? signature = null,
        string? mimeType = null,
        string? data = null,
        string? id = null,
        string? name = null,
        JsonElement? Arguments = null,
        string? toolCallId = null,
        string? toolName = null,
        bool? isError = null,
        JsonElement[]? parts = null);
}
