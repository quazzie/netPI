using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NetPI.Providers.AiProxy;

/// <summary>OpenAI Responses API (<c>POST /v1/responses</c>, streaming).</summary>
internal static class ResponsesTransport
{
    public const string Path = "/v1/responses";

    public static JsonObject BuildBody(ModelRequest req, ModelOptions mo, bool allowImages)
    {
        var body = new JsonObject { ["model"] = req.Model.Id };
        if (!string.IsNullOrWhiteSpace(req.SystemPrompt)) body["instructions"] = req.SystemPrompt;
        body["input"] = BuildInput(req.Messages, mo.ReplayReasoning, allowImages);

        if (req.Tools.Count > 0)
        {
            var tools = new JsonArray();
            foreach (var t in req.Tools)
                tools.Add(new JsonObject
                {
                    ["type"] = "function",
                    ["name"] = t.Name,
                    ["description"] = t.Description,
                    ["parameters"] = t.Parameters.DeepClone(),
                    ["strict"] = false,
                });
            body["tools"] = tools;
            body["parallel_tool_calls"] = true;
        }

        body["stream"] = true;
        body["store"] = false;
        body["max_output_tokens"] = OpenAiCommon.ResolveMaxTokens(req, mo);

        var effort = EffortMap.Resolve(req.ReasoningEffort, req.Model.Reasoning);
        if (effort is not null || mo.ReasoningSummary is not null)
        {
            var reasoning = new JsonObject();
            if (effort is not null) reasoning["effort"] = effort;
            if (mo.ReasoningSummary is not null) reasoning["summary"] = mo.ReasoningSummary;
            body["reasoning"] = reasoning;
        }
        if (mo.ReplayReasoning) body["include"] = new JsonArray("reasoning.encrypted_content");
        if (req.Temperature is { } temp) body["temperature"] = temp;
        return body;
    }

    public static JsonArray BuildInput(IReadOnlyList<ChatMessage> messages, bool replayReasoning, bool allowImages)
    {
        var input = new JsonArray();
        var toolImages = new List<(string CallId, ImagePart Image)>();

        void FlushToolImages()
        {
            if (toolImages.Count == 0) return;
            var content = new JsonArray();
            foreach (var group in toolImages.GroupBy(x => x.CallId))
            {
                content.Add(new JsonObject { ["type"] = "input_text", ["text"] = $"[Image(s) returned by tool call {group.Key}]" });
                foreach (var (_, img) in group) content.Add(InputImage(img));
            }
            input.Add(new JsonObject { ["role"] = "user", ["content"] = content });
            toolImages.Clear();
        }

        foreach (var m in messages)
        {
            if (m.Role != MessageRole.Tool) FlushToolImages();
            switch (m.Role)
            {
                case MessageRole.Assistant:
                    foreach (var p in m.Parts)
                    {
                        switch (p)
                        {
                            case ThinkingPart th when replayReasoning:
                                if (ReasoningItem(th) is { } r) input.Add(r);
                                break;
                            case TextPart t when !string.IsNullOrEmpty(t.Text):
                                input.Add(new JsonObject
                                {
                                    ["type"] = "message",
                                    ["role"] = "assistant",
                                    ["content"] = new JsonArray(new JsonObject { ["type"] = "output_text", ["text"] = t.Text }),
                                });
                                break;
                            case ToolCallPart c:
                                input.Add(new JsonObject
                                {
                                    ["type"] = "function_call",
                                    ["call_id"] = c.Id,
                                    ["name"] = c.Name,
                                    ["arguments"] = string.IsNullOrWhiteSpace(c.Arguments) ? "{}" : c.Arguments,
                                });
                                break;
                        }
                    }
                    break;

                case MessageRole.Tool:
                    foreach (var r in m.ToolResults)
                    {
                        input.Add(new JsonObject
                        {
                            ["type"] = "function_call_output",
                            ["call_id"] = r.CallId,
                            ["output"] = r.Content ?? "",
                        });
                        if (r.Images is { Count: > 0 } imgs)
                        {
                            if (allowImages) foreach (var img in imgs) toolImages.Add((r.CallId, img));
                        }
                    }
                    break;

                default: // User (Notice/Summary are normalized to User by the host)
                    var content = new JsonArray();
                    foreach (var p in m.Parts)
                    {
                        switch (p)
                        {
                            case TextPart t when !string.IsNullOrEmpty(t.Text):
                                content.Add(new JsonObject { ["type"] = "input_text", ["text"] = t.Text });
                                break;
                            case ImagePart img:
                                content.Add(allowImages
                                    ? InputImage(img)
                                    : new JsonObject { ["type"] = "input_text", ["text"] = OpenAiCommon.ImageOmitted });
                                break;
                        }
                    }
                    if (content.Count > 0) input.Add(new JsonObject { ["role"] = "user", ["content"] = content });
                    break;
            }
        }
        FlushToolImages();
        return input;
    }

    private static JsonObject InputImage(ImagePart img) => new()
    {
        ["type"] = "input_image",
        ["image_url"] = $"data:{img.MediaType};base64,{img.Data}",
    };

    private static JsonObject? ReasoningItem(ThinkingPart th)
    {
        var encrypted = th.ProviderData.Str("encrypted_content");
        if (string.IsNullOrEmpty(th.Text) && encrypted is null) return null;
        var item = new JsonObject { ["type"] = "reasoning" };
        if (th.ProviderData.Str("id") is { Length: > 0 } id) item["id"] = id;
        item["summary"] = new JsonArray();
        item["content"] = string.IsNullOrEmpty(th.Text)
            ? new JsonArray()
            : new JsonArray(new JsonObject { ["type"] = "reasoning_text", ["text"] = th.Text });
        if (encrypted is not null) item["encrypted_content"] = encrypted;
        return item;
    }
}

/// <summary>Parses the Responses API event stream into the assembler.</summary>
internal sealed class ResponsesStreamParser(MessageAssembler asm, string provider) : IOpenAiStreamParser
{
    private sealed class Item
    {
        public string Type = "";
        public string? Id;
        public int? OutputIndex;
        public TextPart? Text;
        public ThinkingPart? Thinking;
        public ToolCallPart? Call;
        public int? SummaryIndex;
        public bool Done;
    }

    private readonly Dictionary<string, Item> _byId = new(StringComparer.Ordinal);
    private readonly Dictionary<int, Item> _byIndex = [];
    private readonly List<Item> _items = [];

    public bool Finished { get; private set; }

    public void Handle(SseEvent sse)
    {
        if (sse.IsDone) { if (asm.HasContent || _items.Count > 0) Finished = true; return; }
        JsonDocument doc;
        try { doc = JsonDocument.Parse(sse.Data); }
        catch (JsonException) { return; } // tolerate junk lines
        using (doc) HandleEvent(doc.RootElement, sse.Event);
    }

    public void HandleJsonBody(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.Prop("error").Has()) { var (m, t) = ProviderErrors.ExtractError(root); throw ProviderErrors.FromStream(provider, t, m); }
        Complete(root.Prop("object").ValueKind == JsonValueKind.String ? root : root.Prop("response"));
    }

    public void Finish()
    {
        if (!Finished) throw ProviderErrors.UnexpectedEnd(provider);
    }

    private void HandleEvent(JsonElement e, string? sseEvent)
    {
        var type = e.Str("type") ?? sseEvent ?? "";
        switch (type)
        {
            case "response.output_item.added":
                ItemAdded(e.Prop("item"), e.IntOrNull("output_index"));
                break;

            case "response.output_text.delta":
            case "response.refusal.delta":
            {
                var it = Resolve(e, "message");
                it.Text ??= asm.BeginText();
                asm.AppendText(it.Text, e.Str("delta"));
                break;
            }
            case "response.output_text.done":
            case "response.refusal.done":
            {
                var it = Resolve(e, "message");
                var full = e.Str("text") ?? e.Str("refusal");
                if (string.IsNullOrEmpty(full)) break;
                it.Text ??= asm.BeginText();
                asm.CatchUp(it.Text, full);
                break;
            }

            case "response.reasoning_text.delta":
            case "response.reasoning.delta":
            {
                var it = Resolve(e, "reasoning");
                it.Thinking ??= asm.BeginThinking();
                asm.AppendThinking(it.Thinking, e.Str("delta"));
                break;
            }
            case "response.reasoning_text.done":
            {
                var it = Resolve(e, "reasoning");
                if (it.SummaryIndex is not null) break; // mixing summary and full text: keep what streamed
                var full = e.Str("text");
                if (string.IsNullOrEmpty(full)) break;
                it.Thinking ??= asm.BeginThinking();
                asm.CatchUp(it.Thinking, full);
                break;
            }
            case "response.reasoning_summary_text.delta":
            {
                var it = Resolve(e, "reasoning");
                it.Thinking ??= asm.BeginThinking();
                var idx = e.IntOrNull("summary_index") ?? 0;
                if (it.SummaryIndex is { } prev && prev != idx && asm.CurrentText(it.Thinking).Length > 0)
                    asm.AppendThinking(it.Thinking, "\n\n");
                it.SummaryIndex = idx;
                asm.AppendThinking(it.Thinking, e.Str("delta"));
                break;
            }

            case "response.function_call_arguments.delta":
            {
                var it = Resolve(e, "function_call");
                it.Call ??= asm.StartToolCall(null, "");
                asm.AppendToolArgs(it.Call, e.Str("delta"));
                break;
            }
            case "response.function_call_arguments.done":
            {
                var it = Resolve(e, "function_call");
                it.Call ??= asm.StartToolCall(null, e.Str("name") ?? "");
                asm.CatchUp(it.Call, e.Str("arguments"));
                break;
            }

            case "response.output_item.done":
                ItemDone(e.Prop("item"), e.IntOrNull("output_index"));
                break;

            case "response.completed":
            case "response.done":
                Complete(e.Prop("response"));
                break;

            case "response.incomplete":
            {
                var resp = e.Prop("response");
                Complete(resp);
                var reason = resp.Prop("incomplete_details").Str("reason");
                asm.StopReason = reason switch
                {
                    null or "max_output_tokens" or "max_tokens" => "length",
                    "content_filter" => "content_filter",
                    _ => reason,
                };
                break;
            }

            case "response.failed":
            {
                var err = e.Prop("response").Prop("error");
                throw ProviderErrors.FromStream(provider, err.Str("code") ?? err.Str("type") ?? "response_failed",
                    err.Str("message") ?? "the response failed");
            }

            case "error":
            {
                var err = e.Prop("error").IsObj() ? e.Prop("error") : e;
                var code = err.Str("code");
                int? status = code is not null && int.TryParse(code, out var sc) ? sc : null;
                var kind = status is null ? code ?? err.Str("type") : err.Str("type");
                if (kind == "error") kind = null;
                throw ProviderErrors.FromStream(provider, kind, err.Str("message"), status);
            }

            // response.created / in_progress / content_part.* / reasoning_summary_part.* / *.annotation / unknown: ignore
        }
    }

    private Item Resolve(JsonElement e, string expectedType)
    {
        var id = e.Str("item_id");
        var idx = e.IntOrNull("output_index");
        if (id is not null && _byId.TryGetValue(id, out var it)) return it;
        if (idx is { } i && _byIndex.TryGetValue(i, out it) && (id is null || it.Id is null)) return it;
        return Register(new Item { Type = expectedType, Id = id, OutputIndex = idx });
    }

    private Item Register(Item it)
    {
        if (it.Id is not null) _byId[it.Id] = it;
        if (it.OutputIndex is { } i) _byIndex[i] = it;
        _items.Add(it);
        return it;
    }

    private Item FindOrRegister(JsonElement item, int? outputIndex)
    {
        var id = item.Str("id");
        if (id is not null && _byId.TryGetValue(id, out var it)) return it;
        if (outputIndex is { } i && _byIndex.TryGetValue(i, out it) && (id is null || it.Id is null || it.Id == id))
        {
            if (it.Id is null && id is not null) { it.Id = id; _byId[id] = it; }
            return it;
        }
        return Register(new Item { Type = item.Str("type") ?? "", Id = id, OutputIndex = outputIndex });
    }

    private void ItemAdded(JsonElement item, int? outputIndex)
    {
        if (!item.IsObj()) return;
        var it = FindOrRegister(item, outputIndex);
        it.Type = item.Str("type") ?? it.Type;
        if (it.Type == "function_call" && it.Call is null)
        {
            it.Call = asm.StartToolCall(item.Str("call_id") ?? item.Str("id"), item.Str("name") ?? "");
            asm.AppendToolArgs(it.Call, item.Str("arguments"));
        }
    }

    private void ItemDone(JsonElement item, int? outputIndex)
    {
        if (!item.IsObj()) return;
        var it = FindOrRegister(item, outputIndex);
        it.Type = item.Str("type") ?? it.Type;
        it.Done = true;
        switch (it.Type)
        {
            case "message":
            {
                var sb = new StringBuilder();
                foreach (var c in item.Prop("content").Items())
                    sb.Append(c.Str("text") ?? c.Str("refusal") ?? "");
                if (sb.Length == 0) break;
                it.Text ??= asm.BeginText();
                asm.CatchUp(it.Text, sb.ToString());
                break;
            }
            case "reasoning":
            {
                var full = JoinTexts(item.Prop("content"));
                if (string.IsNullOrEmpty(full)) full = JoinTexts(item.Prop("summary"));
                var encrypted = item.Str("encrypted_content");
                if (it.Thinking is null && string.IsNullOrEmpty(full) && encrypted is null) break;
                it.Thinking ??= asm.BeginThinking();
                if (asm.CurrentText(it.Thinking).Length == 0) asm.CatchUp(it.Thinking, full);
                var pd = new JsonObject();
                if (item.Str("id") is { } rid) pd["id"] = rid;
                if (encrypted is not null) pd["encrypted_content"] = encrypted;
                if (pd.Count > 0) it.Thinking.ProviderData = pd;
                break;
            }
            case "function_call":
            {
                it.Call ??= asm.StartToolCall(item.Str("call_id") ?? item.Str("id"), item.Str("name") ?? "");
                if (string.IsNullOrEmpty(it.Call.Name) && item.Str("name") is { } name) it.Call.Name = name;
                asm.CatchUp(it.Call, item.Str("arguments"));
                break;
            }
        }
    }

    private static string JoinTexts(JsonElement arr)
    {
        var parts = new List<string>();
        foreach (var c in arr.Items())
            if (c.Str("text") is { Length: > 0 } t) parts.Add(t);
        return string.Join("\n\n", parts);
    }

    private void Complete(JsonElement response)
    {
        if (response.IsObj())
        {
            // Servers that skip item events entirely: take the final output list.
            var output = response.Prop("output");
            if (output.ValueKind == JsonValueKind.Array)
            {
                var i = 0;
                foreach (var item in output.EnumerateArray())
                {
                    var idx = i++;
                    var id = item.Str("id");
                    var known = (id is not null && _byId.TryGetValue(id, out var k) && k.Done)
                                || (_byIndex.TryGetValue(idx, out var k2) && k2.Done && (id is null || k2.Id == id));
                    if (!known && !(_items.Count > 0 && id is null)) ItemDone(item, idx);
                }
            }
            if (response.Prop("usage").IsObj()) asm.SetUsage(OpenAiCommon.ResponsesUsage(response.Prop("usage")));
            if (response.Str("status") == "incomplete")
                asm.StopReason = response.Prop("incomplete_details").Str("reason") is "content_filter" ? "content_filter" : "length";
        }
        asm.StopReason ??= asm.ToolCallCount > 0 ? "tool_use" : "stop";
        Finished = true;
    }
}
