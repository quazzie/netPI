using System.Text.Json;
using System.Text.Json.Nodes;

namespace NetPI.Providers.AiProxy;

/// <summary>OpenAI Chat Completions API (<c>POST /v1/chat/completions</c>, streaming).</summary>
internal static class ChatTransport
{
    public const string Path = "/v1/chat/completions";

    public static JsonObject BuildBody(ModelRequest req, ModelOptions mo, bool allowImages)
    {
        var body = new JsonObject
        {
            ["model"] = req.Model.Id,
            ["messages"] = BuildMessages(req.SystemPrompt, req.Messages, mo.ReplayReasoning, allowImages),
        };
        if (req.Tools.Count > 0)
        {
            var tools = new JsonArray();
            foreach (var t in req.Tools)
                tools.Add(new JsonObject
                {
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = t.Name,
                        ["description"] = t.Description,
                        ["parameters"] = t.Parameters.DeepClone(),
                    },
                });
            body["tools"] = tools;
        }
        body["stream"] = true;
        body["stream_options"] = new JsonObject { ["include_usage"] = true };
        body["max_tokens"] = OpenAiCommon.ResolveMaxTokens(req, mo);
        if (EffortMap.Resolve(req.ReasoningEffort, req.Model.Reasoning) is { } effort) body["reasoning_effort"] = effort;
        if (req.Temperature is { } temp) body["temperature"] = temp;
        return body;
    }

    public static JsonArray BuildMessages(string? systemPrompt, IReadOnlyList<ChatMessage> messages, bool replayReasoning, bool allowImages) =>
        new ChatBuilder(replayReasoning, allowImages).Build(systemPrompt, messages);

    /// <summary>The OpenAI-compatible assistant turn: the reasoning as text, under the name those servers read.</summary>
    private sealed class ChatBuilder(bool replayReasoning, bool allowImages) : ChatMessageBuilder(allowImages)
    {
        protected override JsonObject? Reasoning(ChatMessage message)
        {
            if (!replayReasoning) return null;
            var text = string.Join("\n\n", message.Parts.OfType<ThinkingPart>().Where(t => t.Text.Length > 0).Select(t => t.Text));
            return text.Length > 0 ? new JsonObject { ["reasoning_content"] = text } : null;
        }
    }
}

/// <summary>Parses Chat Completions chunks: content, reasoning (the OpenAI-compatible names), inline think tags,
/// tool calls by index. Everything else is the shared parser in <c>shared/ProviderKit</c>.</summary>
internal sealed class ChatStreamParser(MessageAssembler asm, string provider, bool parseThinkTags)
    : ChatCompletionsParser(asm, provider, parseThinkTags), IOpenAiStreamParser
{
    protected override string? ReasoningText(JsonElement delta)
    {
        // Prefer reasoning_content; some servers send both fields with the same text.
        var reasoning = delta.Str("reasoning_content");
        return !string.IsNullOrEmpty(reasoning) || delta.Prop("reasoning").ValueKind != JsonValueKind.String
            ? reasoning
            : delta.Str("reasoning");
    }

    protected override Usage? ReadUsage(JsonElement usage) => OpenAiCommon.ChatUsage(usage);
}
