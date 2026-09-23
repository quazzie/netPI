using System.Text;
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

    public static JsonArray BuildMessages(string? systemPrompt, IReadOnlyList<ChatMessage> messages, bool replayReasoning, bool allowImages)
    {
        var list = new JsonArray();
        if (!string.IsNullOrWhiteSpace(systemPrompt)) list.Add(new JsonObject { ["role"] = "system", ["content"] = systemPrompt });

        var toolImages = new List<(string CallId, ImagePart Image)>();
        void FlushToolImages()
        {
            if (toolImages.Count == 0) return;
            var content = new JsonArray();
            foreach (var group in toolImages.GroupBy(x => x.CallId))
            {
                content.Add(new JsonObject { ["type"] = "text", ["text"] = $"[Image(s) returned by tool call {group.Key}]" });
                foreach (var (_, img) in group) content.Add(ImageUrl(img));
            }
            list.Add(new JsonObject { ["role"] = "user", ["content"] = content });
            toolImages.Clear();
        }

        foreach (var m in messages)
        {
            if (m.Role != MessageRole.Tool) FlushToolImages();
            switch (m.Role)
            {
                case MessageRole.Assistant:
                {
                    var text = string.Join("\n", m.Parts.OfType<TextPart>().Where(t => t.Text.Length > 0).Select(t => t.Text));
                    var reasoning = replayReasoning
                        ? string.Join("\n\n", m.Parts.OfType<ThinkingPart>().Where(t => t.Text.Length > 0).Select(t => t.Text))
                        : "";
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
                    if (text.Length == 0 && calls.Count == 0 && reasoning.Length == 0) break;
                    var msg = new JsonObject { ["role"] = "assistant", ["content"] = text.Length == 0 && calls.Count > 0 ? null : text };
                    if (reasoning.Length > 0) msg["reasoning_content"] = reasoning;
                    if (calls.Count > 0) msg["tool_calls"] = calls;
                    list.Add(msg);
                    break;
                }
                case MessageRole.Tool:
                    foreach (var r in m.ToolResults)
                    {
                        list.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = r.CallId, ["content"] = r.Content ?? "" });
                        if (r.Images is { Count: > 0 } imgs && allowImages)
                            foreach (var img in imgs) toolImages.Add((r.CallId, img));
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
                            content.Add(allowImages ? ImageUrl(img) : new JsonObject { ["type"] = "text", ["text"] = OpenAiCommon.ImageOmitted });
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
}

/// <summary>Parses Chat Completions chunks (content, reasoning_content/reasoning, inline think tags, tool_calls by index).</summary>
internal sealed class ChatStreamParser(MessageAssembler asm, string provider, bool parseThinkTags) : IOpenAiStreamParser
{
    private sealed class Call
    {
        public string? Id;
        public string Name = "";
        public ToolCallPart? Part;
        public readonly StringBuilder PendingArgs = new();
    }

    private readonly ThinkTagSplitter? _think = parseThinkTags ? new ThinkTagSplitter() : null;
    private readonly Dictionary<int, Call> _calls = [];
    private readonly List<Call> _order = [];
    private string? _finish;
    private bool _done;
    private bool _anyChunk;

    public bool Finished => _done;

    public void Handle(SseEvent sse)
    {
        if (sse.IsDone) { _done = true; return; }
        JsonDocument doc;
        try { doc = JsonDocument.Parse(sse.Data); }
        catch (JsonException) { return; }
        using (doc) HandleChunk(doc.RootElement, streaming: true);
    }

    public void HandleJsonBody(string json)
    {
        using var doc = JsonDocument.Parse(json);
        HandleChunk(doc.RootElement, streaming: false);
        _done = true;
    }

    private void HandleChunk(JsonElement root, bool streaming)
    {
        if (root.ValueKind != JsonValueKind.Object) return;
        if (root.Prop("error").Has())
        {
            var err = root.Prop("error");
            var (msg, type) = ProviderErrors.ExtractError(root);
            int? status = err.IsObj() ? err.IntOrNull("code") : null;
            throw ProviderErrors.FromStream(provider, type, msg ?? err.ToString(), status);
        }
        _anyChunk = true;

        var choices = root.Prop("choices");
        if (choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0)
        {
            var choice = choices[0];
            var delta = streaming ? choice.Prop("delta") : choice.Prop("message");
            if (!delta.IsObj()) delta = choice.Prop(streaming ? "message" : "delta");
            if (delta.IsObj()) HandleDelta(delta);
            if (choice.Str("finish_reason") is { Length: > 0 } fr) _finish = fr;
        }

        var usage = root.Prop("usage");
        if (usage.IsObj()) asm.SetUsage(OpenAiCommon.ChatUsage(usage));
    }

    private void HandleDelta(JsonElement delta)
    {
        // Prefer reasoning_content; some servers send both fields with the same text.
        var reasoning = delta.Str("reasoning_content");
        if (string.IsNullOrEmpty(reasoning) && delta.Prop("reasoning").ValueKind == JsonValueKind.String) reasoning = delta.Str("reasoning");
        if (!string.IsNullOrEmpty(reasoning))
        {
            FlushThink();
            asm.AddThinking(reasoning);
        }

        var content = delta.Prop("content");
        if (content.ValueKind == JsonValueKind.String) Content(content.GetString());
        else if (content.ValueKind == JsonValueKind.Array)
            foreach (var c in content.EnumerateArray()) Content(c.Str("text"));

        var toolCalls = delta.Prop("tool_calls");
        if (toolCalls.ValueKind == JsonValueKind.Array)
        {
            FlushThink();
            foreach (var tc in toolCalls.EnumerateArray()) ToolCallDelta(tc);
        }
    }

    private void Content(string? text)
    {
        if (string.IsNullOrEmpty(text)) return;
        if (_think is null) { asm.AddText(text); return; }
        _think.Process(text, EmitSegment);
    }

    private void EmitSegment(bool thinking, string text)
    {
        if (thinking) asm.AddThinking(text);
        else asm.AddText(text);
    }

    private void FlushThink() => _think?.Flush(EmitSegment);

    private void ToolCallDelta(JsonElement tc)
    {
        var id = tc.Str("id");
        var fn = tc.Prop("function");
        var name = fn.Str("name");
        var args = fn.Prop("arguments").ValueKind switch
        {
            JsonValueKind.String => fn.Prop("arguments").GetString(),
            JsonValueKind.Object or JsonValueKind.Array => fn.Prop("arguments").GetRawText(), // non-standard servers
            _ => null,
        };

        Call? call = null;
        var index = tc.IntOrNull("index");
        if (index is null)
        {
            // Non-standard servers without "index": match by id, else a chunk without id/name continues the last call.
            if (!string.IsNullOrEmpty(id)) call = _order.FirstOrDefault(c => c.Id == id);
            else if (string.IsNullOrEmpty(name) && _order.Count > 0) call = _order[^1];
        }
        else if (_calls.TryGetValue(index.Value, out var existing))
        {
            // Same index but a different id and a name: a new call (some servers reuse index 0).
            var isNew = !string.IsNullOrEmpty(id) && existing.Id is not null && existing.Id != id && !string.IsNullOrEmpty(name);
            call = isNew ? null : existing;
        }
        if (call is null)
        {
            call = new Call { Id = string.IsNullOrEmpty(id) ? null : id };
            if (index is { } i) _calls[i] = call;
            _order.Add(call);
        }
        if (call.Id is null && !string.IsNullOrEmpty(id)) call.Id = id;
        if (!string.IsNullOrEmpty(name) && call.Part is null) call.Name += name;

        if (call.Part is null && call.Name.Length > 0)
        {
            call.Part = asm.StartToolCall(call.Id, call.Name);
            if (call.PendingArgs.Length > 0) { asm.AppendToolArgs(call.Part, call.PendingArgs.ToString()); call.PendingArgs.Clear(); }
        }
        if (string.IsNullOrEmpty(args)) return;
        if (call.Part is null) call.PendingArgs.Append(args);
        else asm.AppendToolArgs(call.Part, args);
    }

    public void Finish()
    {
        FlushThink();
        foreach (var c in _order)
        {
            if (c.Part is not null) continue;
            c.Part = asm.StartToolCall(c.Id, c.Name);
            asm.AppendToolArgs(c.Part, c.PendingArgs.ToString());
        }
        if (!_done && _finish is null) throw ProviderErrors.UnexpectedEnd(provider);
        if (!_anyChunk && !asm.HasContent) throw ProviderErrors.UnexpectedEnd(provider);

        asm.StopReason = _finish switch
        {
            "tool_calls" or "function_call" => "tool_use",
            "length" or "max_tokens" => "length",
            "content_filter" => "content_filter",
            null or "stop" or "eos" or "end_turn" => asm.ToolCallCount > 0 ? "tool_use" : "stop",
            _ => _finish,
        };
    }
}
