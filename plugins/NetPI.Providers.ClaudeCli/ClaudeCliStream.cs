using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NetPI.Providers.ClaudeCli;

/// <summary>
/// Reads the CLI's <c>--output-format stream-json --include-partial-messages</c> lines into the shared assembler:
/// <c>stream_event</c> lines carry the Messages API's own streaming events; <c>assistant</c> is the whole message
/// (used when nothing streamed); <c>user</c> lines carry the results of the CLI's own tools; <c>result</c> ends the run,
/// with usage and the error state. The CLI's tool uses become tool-call parts marked as run by the provider
/// (<see cref="ToolCallPart.ProviderExecuted"/>), each with its result, so the chat shows them like any tool call.
/// </summary>
internal sealed class ClaudeCliStream(MessageAssembler asm)
{
    /// <summary>A longer tool result is cut: the agent's own transcript is not where a whole file belongs.</summary>
    internal const int MaxResultChars = 20_000;

    private readonly Dictionary<int, MessagePart> _blocks = [];
    private readonly Dictionary<int, ToolCallPart> _toolBlocks = [];
    private readonly Dictionary<string, ToolCallPart> _toolsById = [];
    private readonly Usage _usage = new();
    private bool _streamed;
    private bool _sawUsage;
    public bool Finished { get; private set; }
    /// <summary>The init line's apiKeySource: anything but "none" means the CLI would bill an API key.</summary>
    public string? ApiKeySource { get; private set; }
    public string? AssistantError { get; private set; }

    public void Handle(string line)
    {
        JsonNode? node;
        try { node = JsonNode.Parse(line); }
        catch (JsonException) { return; } // not a protocol line (a warning printed to stdout)
        if (node is not JsonObject o) return;
        var type = o.Str("type");
        // what a subagent of the CLI does is its own: only the top level conversation is this call's answer
        if (type is "stream_event" or "assistant" or "user" && o["parent_tool_use_id"] is not null) return;
        switch (type)
        {
            case "system" when o.Str("subtype") == "init": ApiKeySource = o.Str("apiKeySource"); break;
            case "stream_event": StreamEvent(o.Obj("event")); break;
            case "assistant": Assistant(o); break;
            case "user": ToolResults(o); break;
            case "result": Result(o); break;
        }
    }

    private void StreamEvent(JsonObject? e)
    {
        if (e is null) return;
        switch (e.Str("type"))
        {
            case "message_start":
                ApplyUsage(e.Obj("message").Obj("usage"));
                break;
            case "content_block_start":
            {
                var index = e.Int("index") ?? 0;
                var block = e.Obj("content_block");
                var type = block.Str("type");
                if (type == "text") _blocks[index] = asm.BeginText();
                else if (type == "thinking") _blocks[index] = asm.BeginThinking();
                else if (type == "tool_use")
                {
                    var call = asm.StartToolCall(block.Str("id"), block.Str("name"));
                    call.ProviderData = new JsonObject { [ToolCallPart.ExecutedKey] = true };
                    _toolBlocks[index] = call;
                    if (!string.IsNullOrEmpty(call.Id)) _toolsById[call.Id] = call;
                }
                break;
            }
            case "content_block_delta":
            {
                var d = e.Obj("delta");
                var index = e.Int("index") ?? 0;
                if (d.Str("type") == "input_json_delta" && _toolBlocks.TryGetValue(index, out var call)) { asm.AppendToolArgs(call, d.Str("partial_json")); break; }
                if (!_blocks.TryGetValue(index, out var part)) break;
                switch (d.Str("type"))
                {
                    case "text_delta" when part is TextPart t: asm.AppendText(t, d.Str("text")); _streamed = true; break;
                    case "thinking_delta" when part is ThinkingPart th: asm.AppendThinking(th, d.Str("thinking")); _streamed = true; break;
                    case "signature_delta" when part is ThinkingPart sig: sig.Signature = d.Str("signature"); break;
                }
                break;
            }
            case "content_block_stop":
                if (_blocks.TryGetValue(e.Int("index") ?? 0, out var p) && p is ThinkingPart tp) asm.EndThinking(tp);
                break;
            case "message_delta":
                ApplyUsage(e.Obj("usage"));
                if (e.Obj("delta").Str("stop_reason") == "max_tokens") asm.StopReason = "length";
                break;
        }
    }

    /// <summary>The results of the CLI's own tools: <c>{ type: user, message: { content: [{ type: tool_result, tool_use_id, content, is_error }] } }</c>.</summary>
    private void ToolResults(JsonObject o)
    {
        foreach (var block in o.Obj("message").Arr("content") ?? [])
        {
            if (block.Str("type") != "tool_result" || block.Str("tool_use_id") is not { } id || !_toolsById.TryGetValue(id, out var call)) continue;
            call.ProviderData![ToolCallPart.ResultKey] = new JsonObject
            {
                ["content"] = ResultText(block?["content"]),
                ["isError"] = block.Bool("is_error", false),
            };
        }
    }

    private static string ResultText(JsonNode? content)
    {
        string text;
        if (content is JsonValue v && v.TryGetValue<string>(out var s)) text = s;
        else if (content is JsonArray arr)
        {
            var sb = new StringBuilder();
            foreach (var part in arr)
            {
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(part.Str("type") == "text" ? part.Str("text") : $"[{part.Str("type") ?? "content"}]");
            }
            text = sb.ToString();
        }
        else text = "";
        return text.Length <= MaxResultChars ? text : text[..MaxResultChars] + $"\n… ({text.Length - MaxResultChars} more characters cut)";
    }

    private void Assistant(JsonObject o)
    {
        AssistantError ??= o.Str("error");
        if (_streamed || AssistantError is not null) return;
        foreach (var block in o.Obj("message").Arr("content") ?? [])
            if (block.Str("type") == "text") asm.AddText(block.Str("text"));
    }

    private void Result(JsonObject o)
    {
        Finished = true;
        var text = o.Str("result");
        if (o.Bool("is_error", false))
        {
            var status = o.Int("api_error_status");
            var msg = string.IsNullOrWhiteSpace(text) ? $"Claude CLI reported an error ({o.Str("subtype")})" : text!;
            var auth = AssistantError == "authentication_failed" || msg.Contains("Not logged in", StringComparison.OrdinalIgnoreCase);
            if (auth) msg += " Run `claude auth login` once, in the account that holds the subscription.";
            throw new ModelException($"Claude CLI: {msg}", status is >= 500, status, AssistantError ?? o.Str("terminal_reason") ?? o.Str("subtype"));
        }
        if (!asm.HasContent && !string.IsNullOrEmpty(text)) asm.AddText(text);
        // The result's usage adds up every turn of the CLI's own tool loop; the last turn's, from the stream, is the size of the context.
        if (!_sawUsage) ApplyUsage(o.Obj("usage"));
        // the calls were run by the CLI: this message is the answer, not a request for tools
        asm.StopReason ??= "stop";
    }

    // CostUsd stays null on purpose: total_cost_usd is what the call would cost on the API, not what a subscription pays.
    private void ApplyUsage(JsonObject? u)
    {
        if (u is null) return;
        _sawUsage = true;
        if (u.Int("input_tokens") is { } i) _usage.InputTokens = i;
        if (u.Int("output_tokens") is { } o && o > 0) _usage.OutputTokens = o;
        if (u.Int("cache_read_input_tokens") is { } r) _usage.CacheReadTokens = r;
        if (u.Int("cache_creation_input_tokens") is { } w) _usage.CacheWriteTokens = w;
        asm.SetUsage(_usage);
    }
}
