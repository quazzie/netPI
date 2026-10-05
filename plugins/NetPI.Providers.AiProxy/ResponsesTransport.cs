using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NetPI.Providers.AiProxy;

/// <summary>OpenAI Responses API (<c>POST /v1/responses</c>, streaming).</summary>
internal static class ResponsesTransport
{
    public const string Path = "/v1/responses";

    public static JsonObject BuildBody(ModelRequest req, ModelOptions mo, bool allowImages, string? providerId = null)
    {
        var body = new JsonObject { ["model"] = req.Model.Id };
        if (!string.IsNullOrWhiteSpace(req.SystemPrompt)) body["instructions"] = req.SystemPrompt;
        body["input"] = BuildInput(req.Messages, mo.ReplayReasoning, allowImages, providerId ?? req.Model.Provider, req.Model.Id);

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
        // OpenAI-hosted reasoning models only return replayable reasoning when asked for its encrypted form
        if (mo.ReplayReasoning && mo.IncludeEncryptedReasoning) body["include"] = new JsonArray("reasoning.encrypted_content");
        if (req.Temperature is { } temp) body["temperature"] = temp;
        return body;
    }

    /// <summary>
    /// The conversation as Responses input items. Reasoning goes back only to the model that produced it: a mid-chat
    /// model switch used to replay another model's reasoning item (a foreign <c>encrypted_content</c> blob, or plain
    /// reasoning to a backend that does not take it) and the turn was refused whole with a non-transient 400
    /// (idea-d9k11o). A turn with no recorded provider or model (older history) is replayed, as before.
    /// </summary>
    public static JsonArray BuildInput(IReadOnlyList<ChatMessage> messages, bool replayReasoning, bool allowImages,
        string? providerId = null, string? modelId = null)
    {
        var input = new JsonArray();
        var toolImages = new List<(string CallId, ImagePart Image)>();
        var toolImagesOmitted = new List<(string CallId, int Count)>();

        bool IsOurs(ChatMessage m) =>
            (m.Provider is null || providerId is null || string.Equals(m.Provider, providerId, StringComparison.OrdinalIgnoreCase))
            && (m.Model is null || modelId is null || string.Equals(m.Model, modelId, StringComparison.Ordinal));

        void FlushToolImages()
        {
            if (toolImages.Count == 0 && toolImagesOmitted.Count == 0) return;
            var content = new JsonArray();
            foreach (var group in toolImages.GroupBy(x => x.CallId))
            {
                content.Add(new JsonObject { ["type"] = "input_text", ["text"] = $"[Image(s) returned by tool call {group.Key}]" });
                foreach (var (_, img) in group) content.Add(Image(img));
            }
            foreach (var (callId, count) in toolImagesOmitted)
                content.Add(new JsonObject { ["type"] = "input_text", ["text"] = ChatMessageBuilder.ToolImagesOmitted(callId, count) });
            input.Add(new JsonObject { ["role"] = "user", ["content"] = content });
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
                    // An assistant turn's items have a fixed order — reasoning, then message content, then function
                    // calls — while the stored parts are in arrival order. A model that speaks before it thinks, or a
                    // literal <think> in its answer, stores them interleaved, and replaying that order is refused
                    // whole: 400 invalid_assistant_history. So the order is imposed here, never taken from the history.
                    // Already-stored turns are repaired by this alone: the gateway rejected the position, not the item.
                    var reasoning = new List<JsonObject>();
                    var texts = new List<JsonObject>();
                    var calls = new List<JsonObject>();
                    foreach (var p in m.Parts)
                    {
                        switch (p)
                        {
                            case ThinkingPart th when replayReasoning && IsOurs(m):
                                if (ReasoningItem(th) is { } r) reasoning.Add(r);
                                break;
                            case TextPart t when !string.IsNullOrEmpty(t.Text):
                                texts.Add(new JsonObject { ["type"] = "output_text", ["text"] = t.Text });
                                break;
                            case ToolCallPart c:
                                calls.Add(new JsonObject
                                {
                                    ["type"] = "function_call",
                                    ["call_id"] = c.Id,
                                    ["name"] = c.Name,
                                    ["arguments"] = string.IsNullOrWhiteSpace(c.Arguments) ? "{}" : c.Arguments,
                                });
                                break;
                        }
                    }
                    foreach (var r in reasoning) input.Add(r);
                    if (texts.Count > 0)
                        input.Add(new JsonObject { ["type"] = "message", ["role"] = "assistant", ["content"] = new JsonArray([.. texts]) });
                    foreach (var c in calls) input.Add(c);
                    break;
                }

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
                            else toolImagesOmitted.Add((r.CallId, imgs.Count));
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
                                    ? Image(img)
                                    : new JsonObject { ["type"] = "input_text", ["text"] = ChatMessageBuilder.ImageOmitted });
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

    /// <summary>The image, or the note that replaces one no transport takes: it would fail every later call too (idea-begg3v).</summary>
    private static JsonObject Image(ImagePart img) =>
        ModelMessages.OversizedImage(img) is { } tooBig
            ? new JsonObject { ["type"] = "input_text", ["text"] = tooBig }
            : InputImage(img);

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

/// <summary>
/// Parses the Responses API event stream into the assembler. With <paramref name="parseThinkTags"/> (setting
/// <c>parseThinkTags</c>, default on) inline <c>&lt;think&gt;…&lt;/think&gt;</c> in message text becomes thinking, as on the
/// Chat Completions transport (backends without a reasoning parser, e.g. Qwen on llama.cpp).
/// </summary>
internal sealed class ResponsesStreamParser(MessageAssembler asm, string provider, bool parseThinkTags = true) : IOpenAiStreamParser
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
        /// <summary>Message text as received (think tags included), for catching up from "done" events.</summary>
        public readonly StringBuilder Raw = new();
        public ThinkTagSplitter? Split;
    }

    private readonly Dictionary<string, Item> _byId = new(StringComparer.Ordinal);
    private readonly Dictionary<int, Item> _byIndex = [];
    private readonly List<Item> _items = [];
    private readonly DroppedFrames _dropped = new();

    public bool Finished { get; private set; }

    /// <summary>The server's response id (from response.created / in_progress / completed / failed).</summary>
    public string? ResponseId { get; private set; }

    public void Handle(SseEvent sse)
    {
        // OpenAI's [DONE] sentinel says nothing about how the response ended and carries no usage: without the
        // terminal event (response.completed / incomplete / failed) the stream was cut, and Finish reports it so.
        // It used to pass as a clean completion once anything had streamed, with no usage recorded (D3).
        if (sse.IsDone) return;
        JsonDocument doc;
        // A frame that does not parse is counted, not swallowed: the terminal event and the usage live in the last
        // frames, so dropping one silently ends the call looking clean (idea-saljbd).
        try { doc = JsonDocument.Parse(sse.Data); }
        catch (JsonException) { _dropped.Add(sse.Data); return; } // tolerate junk lines
        using (doc) HandleEvent(doc.RootElement, sse.Event);
    }

    // The same 200-with-a-broken-body as on the Chat transport (idea-3ivjku).
    public void HandleJsonBody(string json)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException) { throw ChatStreamParser.NotJson(provider, json); }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.Prop("error").Has()) { var (m, t) = ProviderErrors.ExtractError(root); throw ProviderErrors.FromStream(provider, t, m); }
            Complete(root.Prop("object").ValueKind == JsonValueKind.String ? root : root.Prop("response"));
        }
    }

    public void Finish()
    {
        foreach (var it in _items) FlushText(it);
        if (!Finished) throw ProviderErrors.UnexpectedEnd(provider, _dropped.Note);
    }

    // ---------------------------------------------------------------- message text (optionally split at <think> tags)

    private void MessageText(Item it, string? delta)
    {
        if (string.IsNullOrEmpty(delta)) return;
        it.Raw.Append(delta);
        if (!parseThinkTags)
        {
            it.Text ??= asm.BeginText();
            asm.AppendText(it.Text, delta);
            return;
        }
        (it.Split ??= new ThinkTagSplitter()).Process(delta, (thinking, text) => EmitSegment(it, thinking, text));
    }

    /// <summary>The complete text of a message item: stream whatever was not received as deltas.</summary>
    private void MessageTextFull(Item it, string? full)
    {
        if (string.IsNullOrEmpty(full)) return;
        var raw = it.Raw.ToString();
        if (full.Length <= raw.Length || !full.StartsWith(raw, StringComparison.Ordinal)) return;
        MessageText(it, full[raw.Length..]);
    }

    private void FlushText(Item it) => it.Split?.Flush((thinking, text) => EmitSegment(it, thinking, text));

    private void EmitSegment(Item it, bool thinking, string text)
    {
        if (thinking)
        {
            asm.AddThinking(text);
            return;
        }
        it.Text = asm.TextTarget(); // the trailing text part, or a new one after a thinking segment
        asm.AppendText(it.Text, text);
    }

    private void HandleEvent(JsonElement e, string? sseEvent)
    {
        var type = e.Str("type") ?? sseEvent ?? "";
        if (e.Prop("response").Str("id") is { Length: > 0 } rid) ResponseId = rid;
        // A gateway that refuses the request mid-stream can answer with a bare `data: {"error":{…}}` frame: no type,
        // no event name, so nothing below matched it, the user saw "stream ended unexpectedly" and Retry spent six
        // attempts at full price on a call the server had already refused (idea-saljbd).
        if (type.Length == 0 && e.Prop("error").Has())
        {
            var (msg, errType) = ProviderErrors.ExtractError(e);
            throw ProviderErrors.FromStream(provider, errType, msg);
        }
        switch (type)
        {
            case "response.output_item.added":
                ItemAdded(e.Prop("item"), e.IntOrNull("output_index"));
                break;

            case "response.output_text.delta":
            case "response.refusal.delta":
                MessageText(Resolve(e, "message"), e.Str("delta"));
                break;
            case "response.output_text.done":
            case "response.refusal.done":
                MessageTextFull(Resolve(e, "message"), e.Str("text") ?? e.Str("refusal"));
                break;

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
                MessageTextFull(it, sb.ToString());
                FlushText(it);
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
