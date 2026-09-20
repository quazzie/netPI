using System.Text.Json;
using NetPI.Abstractions;

namespace NetPI.Agent;

/// <summary>
/// Flattened, serializable view of a <see cref="ModelEvent"/> that the agent
/// publishes on the bus (PLAN §4). The Web plugin maps these to §41 WebSocket
/// payloads; no raw provider objects cross the boundary.
/// </summary>
public sealed record ModelEventWire
{
    public string Kind { get; init; } = "";
    public string? ModelId { get; init; }
    public string? BlockId { get; init; }
    public string? Text { get; init; }
    public string? ToolCallId { get; init; }
    public string? ToolName { get; init; }
    public int? PromptTokens { get; init; }
    public int? CompletionTokens { get; init; }
    public int? TotalTokens { get; init; }
    public int? CachedTokens { get; init; }
    public string? Error { get; init; }
    public string? ToolOutput { get; init; }
    public bool? IsError { get; init; }

    public JsonElement? AssistantMessage { get; init; }
}

/// <summary>Maps a <see cref="ModelEvent"/> to its wire form.</summary>
public static class ModelEventWireMapper
{
    public static ModelEventWire ToWire(ModelEvent ev) => ev switch
    {
        ModelStarted m => new() { Kind = ev.Kind, ModelId = m.ModelId },
        ThinkingStarted t => new() { Kind = ev.Kind, BlockId = t.BlockId },
        ThinkingDelta d => new() { Kind = ev.Kind, Text = d.Text },
        ThinkingCompleted t => new() { Kind = ev.Kind, BlockId = t.BlockId },
        TextStarted t => new() { Kind = ev.Kind, BlockId = t.BlockId },
        TextDelta d => new() { Kind = ev.Kind, Text = d.Text },
        TextCompleted t => new() { Kind = ev.Kind, BlockId = t.BlockId },
        ToolCallStarted t => new() { Kind = ev.Kind, ToolCallId = t.Id, ToolName = t.Name },
        ToolCallArgumentsDelta d => new() { Kind = ev.Kind, ToolCallId = d.Id, Text = d.Delta },
        ToolCallCompleted t => new() { Kind = ev.Kind, ToolCallId = t.Id, ToolName = t.Name },
        UsageUpdated u => new() { Kind = ev.Kind, PromptTokens = u.PromptTokens, CompletionTokens = u.CompletionTokens, TotalTokens = u.TotalTokens, CachedTokens = u.CachedTokens > 0 ? u.CachedTokens : null },
        ModelCompleted c => new() { Kind = ev.Kind, AssistantMessage = AgentMessageJson.Serialize(c.Message) },
        ModelFailed f => new() { Kind = ev.Kind, ModelId = f.ModelId, Error = f.Error },
        _ => new() { Kind = ev.Kind },
    };
}

/// <summary>Serializes an <see cref="AgentMessage"/> for embedding in a wire event.</summary>
public static class AgentMessageJson
{
    private static readonly System.Text.Json.JsonSerializerOptions Opts = new()
    {
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
    };

    public static System.Text.Json.JsonElement Serialize(AgentMessage m)
    {
        var parts = new List<Dictionary<string, object?>>();
        foreach (var p in m.Parts)
        {
            switch (p)
            {
                case TextPart t:
                    parts.Add(new() { ["kind"] = "text", ["text"] = t.Text }); break;
                case ThinkingPart th:
                    parts.Add(new() { ["kind"] = "thinking", ["text"] = th.Text, ["signature"] = th.Signature }); break;
                case ToolCallPart tc:
                    parts.Add(new() { ["kind"] = "tool-call", ["id"] = tc.Id, ["name"] = tc.Name, ["arguments"] = tc.Arguments }); break;
                case ToolResultPart tr:
                    parts.Add(new() { ["kind"] = "tool-result", ["tool_call_id"] = tr.ToolCallId, ["tool_name"] = tr.ToolName,
                        ["is_error"] = tr.IsError, ["parts"] = tr.Parts.Select(x => x is TextPart tt ? new Dictionary<string, object?> { ["kind"] = "text", ["text"] = tt.Text } : new Dictionary<string, object?> { ["kind"] = "other" }).ToList() }); break;
                default:
                    parts.Add(new() { ["kind"] = "other" }); break;
            }
        }
        var dto = new { id = m.Id, role = m.Role.ToString(), created_at = m.CreatedAt.ToString("O"), parts };
        var el = System.Text.Json.JsonSerializer.SerializeToElement(dto, Opts);
        return el;
    }
}
