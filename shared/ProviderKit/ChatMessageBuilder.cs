// Compiled into each provider plugin from shared/ProviderKit (plugins do not reference each other): edit it here.
using System.Text.Json.Nodes;

namespace NetPI.Providers.Kit;

/// <summary>
/// Builds the <c>messages</c> array of a Chat Completions request from a conversation: system prompt, turns, tool
/// results with their images, and the images a model that cannot see them is told about. The dialects differ only
/// in how an assistant turn replays its reasoning, which is what <see cref="Reasoning"/> answers.
/// </summary>
internal class ChatMessageBuilder(bool allowImages)
{
    public const string ImageOmitted = "[image omitted: the selected model does not accept image input]";

    /// <summary>What the model is told about a tool's images when it cannot take them (they used to be dropped in
    /// silence, so the model answered as if the image were empty; idea-vgwg26).</summary>
    public static string ToolImagesOmitted(string callId, int count) =>
        $"[{count} image{(count == 1 ? "" : "s")} returned by tool call {callId} omitted: the selected model does not accept image input]";

    /// <summary>The properties an assistant turn's replayed reasoning adds to its message, or null for none (and
    /// then the turn is dropped when it carries no text and no tool call).</summary>
    protected virtual JsonObject? Reasoning(ChatMessage message) => null;

    public JsonArray Build(string? systemPrompt, IReadOnlyList<ChatMessage> messages)
    {
        var list = new JsonArray();
        if (!string.IsNullOrWhiteSpace(systemPrompt)) list.Add(new JsonObject { ["role"] = "system", ["content"] = systemPrompt });

        var toolImages = new List<(string CallId, ImagePart Image)>();
        var toolImagesOmitted = new List<(string CallId, int Count)>();
        void FlushToolImages()
        {
            if (toolImages.Count == 0 && toolImagesOmitted.Count == 0) return;
            var content = new JsonArray();
            foreach (var group in toolImages.GroupBy(x => x.CallId))
            {
                content.Add(new JsonObject { ["type"] = "text", ["text"] = $"[Image(s) returned by tool call {group.Key}]" });
                foreach (var (_, img) in group) content.Add(Image(img));
            }
            foreach (var (callId, count) in toolImagesOmitted)
                content.Add(new JsonObject { ["type"] = "text", ["text"] = ToolImagesOmitted(callId, count) });
            list.Add(new JsonObject { ["role"] = "user", ["content"] = content });
            toolImages.Clear();
            toolImagesOmitted.Clear();
        }

        foreach (var m in messages)
        {
            if (m.Role != MessageRole.Tool) FlushToolImages();
            switch (m.Role)
            {
                case MessageRole.Assistant:
                {
                    var text = string.Join("\n", m.Parts.OfType<TextPart>().Where(t => t.Text.Length > 0).Select(t => t.Text));
                    var calls = new JsonArray();
                    foreach (var c in m.ToolCalls)
                        calls.Add(new JsonObject
                        {
                            ["id"] = c.Id,
                            ["type"] = "function",
                            ["function"] = new JsonObject
                            {
                                ["name"] = c.Name,
                                ["arguments"] = string.IsNullOrWhiteSpace(c.Arguments) ? "{}" : c.Arguments,
                            },
                        });
                    var reasoning = Reasoning(m);
                    if (text.Length == 0 && calls.Count == 0 && reasoning is null) break;
                    var msg = new JsonObject { ["role"] = "assistant", ["content"] = text.Length == 0 && calls.Count > 0 ? null : text };
                    foreach (var p in reasoning ?? []) msg[p.Key] = p.Value?.DeepClone();
                    if (calls.Count > 0) msg["tool_calls"] = calls;
                    list.Add(msg);
                    break;
                }
                case MessageRole.Tool:
                    foreach (var r in m.ToolResults)
                    {
                        list.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = r.CallId, ["content"] = r.Content ?? "" });
                        if (r.Images is { Count: > 0 } imgs)
                        {
                            if (allowImages) foreach (var img in imgs) toolImages.Add((r.CallId, img));
                            else toolImagesOmitted.Add((r.CallId, imgs.Count));
                        }
                    }
                    break;

                default:
                {
                    var hasImages = m.Parts.Any(p => p is ImagePart);
                    if (!hasImages)
                    {
                        var text = string.Join("\n", m.Parts.OfType<TextPart>().Where(t => t.Text.Length > 0).Select(t => t.Text));
                        if (text.Length > 0) list.Add(new JsonObject { ["role"] = "user", ["content"] = text });
                        break;
                    }
                    var content = new JsonArray();
                    foreach (var p in m.Parts)
                    {
                        if (p is TextPart t && t.Text.Length > 0) content.Add(new JsonObject { ["type"] = "text", ["text"] = t.Text });
                        else if (p is ImagePart img)
                            content.Add(allowImages ? Image(img) : new JsonObject { ["type"] = "text", ["text"] = ImageOmitted });
                    }
                    if (content.Count > 0) list.Add(new JsonObject { ["role"] = "user", ["content"] = content });
                    break;
                }
            }
        }
        FlushToolImages();
        return list;
    }

    private static JsonObject ImageUrl(ImagePart img) => new()
    {
        ["type"] = "image_url",
        ["image_url"] = new JsonObject { ["url"] = $"data:{img.MediaType};base64,{img.Data}" },
    };

    /// <summary>The image, or the note that replaces one no transport takes: it would fail every later call too (idea-begg3v).</summary>
    private static JsonObject Image(ImagePart img) =>
        ModelMessages.OversizedImage(img) is { } tooBig
            ? new JsonObject { ["type"] = "text", ["text"] = tooBig }
            : ImageUrl(img);
}