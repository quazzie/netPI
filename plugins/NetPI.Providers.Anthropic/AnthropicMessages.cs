using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace NetPI.Providers.Anthropic;

/// <summary>Builds Messages API request bodies.</summary>
internal static partial class AnthropicRequest
{
    private static readonly HashSet<string> ImageTypes = new(StringComparer.OrdinalIgnoreCase) { "image/jpeg", "image/png", "image/gif", "image/webp" };

    [GeneratedRegex("[^a-zA-Z0-9_-]")]
    private static partial Regex InvalidIdChars();

    /// <summary>tool_use ids must match ^[a-zA-Z0-9_-]+$ (ids from other providers may not).</summary>
    public static string SanitizeId(string? id)
    {
        if (string.IsNullOrEmpty(id)) return "toolu_" + Ids.Short(20);
        return InvalidIdChars().Replace(id, "_");
    }

    private static JsonObject Ephemeral() => new() { ["type"] = "ephemeral" };

    public sealed record ThinkingPlan(JsonObject? Thinking, JsonObject? OutputConfig, int MaxTokens);

    /// <summary>Resolve max_tokens and the thinking configuration for a request.</summary>
    public static ThinkingPlan PlanThinking(ModelRequest req, AnthropicOptions o)
    {
        var maxTokens = ModelMessages.ClampMaxTokens(req, req.MaxOutputTokens is > 0 ? req.MaxOutputTokens.Value : o.DefaultMaxOutputTokens);

        var supported = req.Model.Reasoning is { Supported: true };
        var effort = (req.ReasoningEffort ?? req.Model.Reasoning?.Default)?.Trim().ToLowerInvariant();
        if (effort is "default" or "auto" or "") effort = req.Model.Reasoning?.Default?.ToLowerInvariant();
        if (!supported || o.Thinking == ThinkingMode.Off || effort is null or "none" or "off") return new(null, null, maxTokens);

        // Which configuration the model takes is a property of its generation: budget_tokens up to Claude 4.5,
        // adaptive from 4.6 (where budget_tokens is deprecated), adaptive alone from 4.7 on (budget_tokens is an
        // HTTP 400 there). Auto follows the id; an explicit mode is the user's call and goes out as asked.
        var api = ClaudeCapabilities.ThinkingApiOf(req.Model.Id);
        var mode = o.Thinking != ThinkingMode.Auto ? o.Thinking : api == ThinkingApi.Budget ? ThinkingMode.Budget : ThinkingMode.Adaptive;
        if (mode == ThinkingMode.Adaptive)
        {
            JsonObject? outputConfig = null;
            if (o.AdaptiveEffort)
            {
                // xhigh exists from Opus 4.7 on; a 4.6 asked for it gets the step it has.
                var e = effort switch { "minimal" => "low", "xhigh" when api != ThinkingApi.AdaptiveXhigh => "high", _ => effort };
                if (e is "low" or "medium" or "high" or "xhigh" or "max") outputConfig = new JsonObject { ["effort"] = e };
            }
            // display is omitted by default from 4.7 on (the thinking blocks arrive empty) and summarized on 4.6:
            // sent explicitly, so the thinking rows show what the user chose on every generation.
            return new(new JsonObject { ["type"] = "adaptive", ["display"] = o.ThinkingDisplay }, outputConfig, maxTokens);
        }

        var key = effort switch { "minimal" => "low", "xhigh" => "high", _ => effort };
        if (!o.Budgets.TryGetValue(key, out var budget)) budget = o.Budgets["medium"];
        budget = Math.Max(1024, budget);
        // A caller asking for a short answer (titles...) without choosing an effort: don't blow up max_tokens.
        if (req.ReasoningEffort is null && req.MaxOutputTokens is > 0 && req.MaxOutputTokens <= budget) return new(null, null, maxTokens);
        if (maxTokens <= budget)
        {
            if (req.MaxOutputTokens is > 0)
            {
                // An explicit caller cap is a promise: the budget ledger reserved exactly it, and a summary that
                // needs the room counts on it. Thinking is fitted inside the cap instead of max_tokens being raised
                // over it, or the call spends more than the ledger holds for it (idea-begg3v).
                budget = Math.Min(budget, maxTokens - Math.Max(1024, maxTokens / 4));
                if (budget < 1024) return new(null, null, maxTokens);
            }
            else
            {
                // No cap of the caller's: max_tokens grows to leave the budget room, as far as the model's own
                // maximum and its context window allow.
                maxTokens = ModelMessages.ClampMaxTokens(req, budget + 4096);
                if (maxTokens <= budget)
                {
                    budget = Math.Max(1024, maxTokens - 4096);
                    if (budget >= maxTokens) return new(null, null, maxTokens);
                }
            }
        }
        return new(new JsonObject { ["type"] = "enabled", ["budget_tokens"] = budget }, null, maxTokens);
    }

    public static JsonObject BuildBody(ModelRequest req, AnthropicOptions o, string providerId)
    {
        var plan = PlanThinking(req, o);
        var messages = BuildMessages(req.Messages, providerId, o.PromptCaching, hasTools: req.Tools.Count > 0);
        var thinking = plan.Thinking;

        // Budget thinking refuses a trailing tool loop with an assistant turn that has no thinking block to replay
        // (one from another provider, or made with thinking off): the call then runs without thinking. Adaptive
        // thinking takes such a turn as it is, so from 4.6 on nothing is switched off.
        if (thinking is not null && thinking.Str("type") == "enabled" && ToolLoopLacksThinking(messages)) thinking = null;

        var body = new JsonObject
        {
            ["model"] = req.Model.Id,
            ["max_tokens"] = plan.MaxTokens,
        };
        if (!string.IsNullOrWhiteSpace(req.SystemPrompt))
        {
            var block = new JsonObject { ["type"] = "text", ["text"] = req.SystemPrompt };
            if (o.PromptCaching) block["cache_control"] = Ephemeral();
            body["system"] = new JsonArray(block);
        }
        body["messages"] = messages;
        if (req.Tools.Count > 0)
        {
            var tools = new JsonArray();
            foreach (var t in req.Tools)
                tools.Add(new JsonObject { ["name"] = t.Name, ["description"] = t.Description, ["input_schema"] = t.Parameters.DeepClone() });
            if (o.PromptCaching) ((JsonObject)tools[^1]!)["cache_control"] = Ephemeral();
            body["tools"] = tools;
        }
        if (thinking is not null)
        {
            body["thinking"] = thinking;
            if (plan.OutputConfig is not null) body["output_config"] = plan.OutputConfig;
        }
        else if (req.Temperature is { } temp) body["temperature"] = temp; // temperature is not allowed with thinking
        body["stream"] = true;
        return body;
    }

    /// <summary>
    /// With thinking enabled (budget_tokens) the API refuses a request whose trailing tool loop has an assistant
    /// turn without a leading thinking block ("a final assistant message must start with a thinking block,
    /// preceeding the lastmost set of tool_use and tool_result blocks"). The loop is every assistant turn since the
    /// last user turn that carries no tool result; what came before it is history, whose thinking the API strips
    /// itself. It used to read the last assistant turn alone.
    /// </summary>
    private static bool ToolLoopLacksThinking(JsonArray messages)
    {
        if (messages.Count < 2 || messages[^1] is not JsonObject last || last.Str("role") != "user" || !HasToolResult(last)) return false;
        for (var i = messages.Count - 2; i >= 0; i--)
        {
            if (messages[i] is not JsonObject m) continue;
            if (m.Str("role") == "user")
            {
                if (!HasToolResult(m)) break; // the user's message that started the loop
                continue;
            }
            if (m["content"] is not JsonArray c || c.Count == 0 || c[0] is not JsonObject first || first.Str("type") is not ("thinking" or "redacted_thinking")) return true;
        }
        return false;
    }

    private static bool HasToolResult(JsonObject turn) =>
        turn["content"] is JsonArray c && c.OfType<JsonObject>().Any(b => b.Str("type") == "tool_result");

    /// <summary>
    /// Convert normalized messages to Messages API turns. Without tool definitions the API rejects tool_use/tool_result
    /// blocks, so they are flattened to text (e.g. compaction/title calls over an agent transcript).
    /// </summary>
    public static JsonArray BuildMessages(IReadOnlyList<ChatMessage> messages, string providerId, bool caching, bool hasTools = true)
    {
        var turns = new List<(string Role, List<JsonObject> Content)>();
        var results = new List<JsonObject>();
        var other = new List<JsonObject>();
        var pendingUser = false;

        void FlushUser()
        {
            if (!pendingUser) return;
            // Tool results must come first in the user turn, then the user's text/images.
            var content = new List<JsonObject>(results.Count + other.Count);
            content.AddRange(results);
            content.AddRange(other);
            if (content.Count == 0) content.Add(Text("(empty message)"));
            if (turns.Count > 0 && turns[^1].Role == "user") turns[^1].Content.AddRange(content);
            else turns.Add(("user", content));
            results.Clear(); other.Clear(); pendingUser = false;
        }

        foreach (var m in messages)
        {
            switch (m.Role)
            {
                case MessageRole.Assistant:
                {
                    FlushUser();
                    var blocks = AssistantBlocks(m, providerId, hasTools);
                    if (blocks.Count == 0) break;
                    if (turns.Count > 0 && turns[^1].Role == "assistant") turns[^1].Content.AddRange(blocks);
                    else turns.Add(("assistant", blocks));
                    break;
                }
                case MessageRole.Tool:
                    pendingUser = true;
                    foreach (var r in m.ToolResults)
                    {
                        if (hasTools) { results.Add(ToolResult(r)); continue; }
                        results.Add(Text($"[Tool result{(r.IsError ? " (error)" : "")} for {r.Name} ({r.CallId})]\n{r.Content}"));
                        if (r.Images is { Count: > 0 } imgs) results.AddRange(imgs.Select(Image));
                    }
                    break;
                default:
                    pendingUser = true;
                    foreach (var p in m.Parts)
                    {
                        if (p is TextPart t && !string.IsNullOrWhiteSpace(t.Text)) other.Add(Text(t.Text));
                        else if (p is ImagePart img) other.Add(Image(img));
                    }
                    break;
            }
        }
        FlushUser();

        if (turns.Count > 0 && turns[0].Role == "assistant") turns.Insert(0, ("user", [Text("(conversation continues)")]));

        if (caching)
        {
            // Rolling cache breakpoint on the last block of the last user turn.
            for (var i = turns.Count - 1; i >= 0; i--)
            {
                if (turns[i].Role != "user") continue;
                if (turns[i].Content.Count > 0) turns[i].Content[^1]["cache_control"] = Ephemeral();
                break;
            }
        }

        var arr = new JsonArray();
        foreach (var (role, content) in turns)
            arr.Add(new JsonObject { ["role"] = role, ["content"] = new JsonArray([.. content]) });
        return arr;
    }

    private static List<JsonObject> AssistantBlocks(ChatMessage m, string providerId, bool hasTools)
    {
        // Thinking leads the assistant turn: the API refuses a turn whose thinking block follows its text
        // (invalid_request_error), and the stored parts are in arrival order — a model that speaks before it
        // thinks, or a literal <think> split out of its answer, stores them interleaved. So the blocks are
        // grouped here instead of walked in place. The Responses transport carries the same rule for its items.
        var thinking = new List<JsonObject>();
        var text = new List<JsonObject>();
        var tools = new List<JsonObject>();
        var replayThinking = m.Provider is null || string.Equals(m.Provider, providerId, StringComparison.OrdinalIgnoreCase);
        foreach (var p in m.Parts)
        {
            switch (p)
            {
                case ThinkingPart th when replayThinking:
                    if (!string.IsNullOrEmpty(th.Redacted))
                        thinking.Add(new JsonObject { ["type"] = "redacted_thinking", ["data"] = th.Redacted });
                    else if (!string.IsNullOrEmpty(th.Signature))
                        thinking.Add(new JsonObject { ["type"] = "thinking", ["thinking"] = th.Text, ["signature"] = th.Signature });
                    break;
                case TextPart t when !string.IsNullOrWhiteSpace(t.Text):
                    text.Add(Text(t.Text));
                    break;
                case ToolCallPart c when !hasTools:
                    text.Add(Text($"[Tool call {c.Name} ({c.Id})] {c.Arguments}"));
                    break;
                case ToolCallPart c:
                    tools.Add(new JsonObject
                    {
                        ["type"] = "tool_use",
                        ["id"] = SanitizeId(c.Id),
                        ["name"] = c.Name,
                        ["input"] = ParseInput(c.Arguments),
                    });
                    break;
            }
        }
        return [.. thinking, .. text, .. tools];
    }

    public static JsonObject ParseInput(string? args)
    {
        if (string.IsNullOrWhiteSpace(args)) return new JsonObject();
        try { return JsonNode.Parse(args) as JsonObject ?? new JsonObject(); }
        catch (JsonException) { return new JsonObject(); }
    }

    private static JsonObject ToolResult(ToolResultPart r)
    {
        var block = new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = SanitizeId(r.CallId) };
        var text = string.IsNullOrEmpty(r.Content) ? "(no output)" : r.Content;
        if (r.Images is { Count: > 0 } imgs)
        {
            var content = new JsonArray(Text(text));
            foreach (var img in imgs) content.Add(Image(img));
            block["content"] = content;
        }
        else block["content"] = text;
        if (r.IsError) block["is_error"] = true;
        return block;
    }

    private static JsonObject Text(string text) => new() { ["type"] = "text", ["text"] = text };

    private static JsonObject Image(ImagePart img)
    {
        if (!ImageTypes.Contains(img.MediaType)) return Text($"[image omitted: unsupported media type {img.MediaType}]");
        if (ModelMessages.OversizedImage(img) is { } tooBig) return Text(tooBig);
        return new JsonObject
        {
            ["type"] = "image",
            ["source"] = new JsonObject { ["type"] = "base64", ["media_type"] = img.MediaType.ToLowerInvariant(), ["data"] = img.Data },
        };
    }
}

/// <summary>Parses the Messages API event stream.</summary>
internal sealed class AnthropicStreamParser(MessageAssembler asm, string provider)
{
    private sealed class Block
    {
        public TextPart? Text;
        public ThinkingPart? Thinking;
        public ToolCallPart? Call;
        public string? InitialInput;
    }

    private readonly Dictionary<int, Block> _blocks = [];
    private readonly Usage _usage = new();
    private readonly DroppedFrames _dropped = new();
    private string? _stop;

    public bool Finished { get; private set; }

    public void Handle(SseEvent sse)
    {
        JsonDocument doc;
        // A frame that does not parse is counted, not swallowed: message_delta carries the stop reason and the final
        // usage, so dropping one silently ends the call looking clean (idea-saljbd).
        try { doc = JsonDocument.Parse(sse.Data); }
        catch (JsonException) { _dropped.Add(sse.Data); return; }
        using (doc) HandleEvent(doc.RootElement, sse.Event);
    }

    private void HandleEvent(JsonElement e, string? sseEvent)
    {
        switch (e.Str("type") ?? sseEvent)
        {
            case "message_start":
                ReadUsage(e.Prop("message").Prop("usage"), fromDelta: false);
                break;

            case "content_block_start":
            {
                var idx = e.IntOrNull("index") ?? _blocks.Count;
                var cb = e.Prop("content_block");
                var block = new Block();
                _blocks[idx] = block;
                switch (cb.Str("type"))
                {
                    case "text":
                        block.Text = asm.TextTarget();
                        asm.AppendText(block.Text, cb.Str("text"));
                        break;
                    case "thinking":
                        block.Thinking = asm.BeginThinking();
                        asm.AppendThinking(block.Thinking, cb.Str("thinking"));
                        if (cb.Str("signature") is { Length: > 0 } sig) block.Thinking.Signature = sig;
                        break;
                    case "redacted_thinking":
                        block.Thinking = asm.BeginThinking();
                        block.Thinking.Redacted = cb.Str("data") ?? "";
                        break;
                    case "tool_use":
                        block.Call = asm.StartToolCall(cb.Str("id"), cb.Str("name"));
                        var input = cb.Prop("input");
                        if (input.IsObj() && input.EnumerateObject().Any()) block.InitialInput = input.GetRawText();
                        break;
                    // server_tool_use, web_search_tool_result, ...: not supported, ignored
                }
                break;
            }

            case "content_block_delta":
            {
                var idx = e.IntOrNull("index") ?? -1;
                var d = e.Prop("delta");
                if (!_blocks.TryGetValue(idx, out var block)) _blocks[idx] = block = new Block();
                switch (d.Str("type"))
                {
                    case "text_delta":
                        block.Text ??= asm.TextTarget();
                        asm.AppendText(block.Text, d.Str("text"));
                        break;
                    case "thinking_delta":
                        block.Thinking ??= asm.BeginThinking();
                        asm.AppendThinking(block.Thinking, d.Str("thinking"));
                        break;
                    case "signature_delta":
                        block.Thinking ??= asm.BeginThinking();
                        block.Thinking.Signature = (block.Thinking.Signature ?? "") + d.Str("signature");
                        break;
                    case "input_json_delta":
                        block.Call ??= asm.StartToolCall(null, "");
                        asm.AppendToolArgs(block.Call, d.Str("partial_json"));
                        break;
                }
                break;
            }

            case "content_block_stop":
            {
                var idx = e.IntOrNull("index") ?? -1;
                if (!_blocks.TryGetValue(idx, out var block)) break;
                if (block.Thinking is not null) asm.EndThinking(block.Thinking);
                if (block.Call is not null && block.InitialInput is not null && asm.CurrentText(block.Call).Length == 0)
                    asm.AppendToolArgs(block.Call, block.InitialInput);
                break;
            }

            case "message_delta":
                if (e.Prop("delta").Str("stop_reason") is { Length: > 0 } sr) _stop = sr;
                ReadUsage(e.Prop("usage"), fromDelta: true);
                break;

            case "message_stop":
                Finished = true;
                break;

            case "error":
            {
                var err = e.Prop("error");
                // The in-stream error carries the server's request id, which is the only way to find the failure in
                // Anthropic's own logs; it was dropped with the frame (idea-saljbd).
                RequestId ??= e.Str("request_id") ?? err.Str("request_id");
                throw ProviderErrors.FromStream(provider, err.Str("type"), err.Str("message"));
            }
            // ping and unknown events: ignore
        }
    }

    /// <summary>input_tokens is already uncached on Anthropic. message_delta usage is cumulative; zeros there never clobber message_start values.</summary>
    private void ReadUsage(JsonElement u, bool fromDelta)
    {
        if (!u.IsObj()) return;
        long? Get(string key) => u.Prop(key).ValueKind == JsonValueKind.Number && (!fromDelta || u.Long(key) > 0) ? u.Long(key) : null;
        if (Get("input_tokens") is { } input) _usage.InputTokens = input;
        if (Get("cache_read_input_tokens") is { } read) _usage.CacheReadTokens = read;
        if (Get("cache_creation_input_tokens") is { } write) _usage.CacheWriteTokens = write;
        if (Get("output_tokens") is { } output) _usage.OutputTokens = output;
        asm.SetUsage(MessageAssembler.Clone(_usage));
    }

    /// <summary>The <c>request_id</c> an error frame carried, when the response had no header with one.</summary>
    public string? RequestId { get; private set; }

    public void Finish()
    {
        if (!Finished) throw ProviderErrors.UnexpectedEnd(provider, _dropped.Note);
        asm.StopReason = _stop switch
        {
            null or "end_turn" or "stop_sequence" => asm.ToolCallCount > 0 ? "tool_use" : "stop",
            "tool_use" => "tool_use",
            "max_tokens" => "length",
            "refusal" => "content_filter",
            _ => _stop,
        };
    }
}
