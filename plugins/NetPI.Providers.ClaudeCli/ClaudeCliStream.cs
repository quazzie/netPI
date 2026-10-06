using System.Text.Json;
using System.Text.Json.Nodes;

namespace NetPI.Providers.ClaudeCli;

/// <summary>
/// Reads the CLI's <c>--output-format stream-json --include-partial-messages</c> lines into the shared assembler:
/// <c>stream_event</c> lines carry the Messages API's own streaming events; <c>assistant</c> is the whole message
/// (used when nothing streamed); <c>result</c> ends the run, with usage and the error state.
/// </summary>
internal sealed class ClaudeCliStream(MessageAssembler asm)
{
    private readonly Dictionary<int, MessagePart> _blocks = [];
    private readonly Usage _usage = new();
    private bool _streamed;
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
        switch (o.Str("type"))
        {
            case "system" when o.Str("subtype") == "init": ApiKeySource = o.Str("apiKeySource"); break;
            case "stream_event": StreamEvent(o.Obj("event")); break;
            case "assistant": Assistant(o); break;
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
                var type = e.Obj("content_block").Str("type");
                if (type == "text") _blocks[index] = asm.BeginText();
                else if (type == "thinking") _blocks[index] = asm.BeginThinking();
                break;
            }
            case "content_block_delta":
            {
                var d = e.Obj("delta");
                if (!_blocks.TryGetValue(e.Int("index") ?? 0, out var part)) break;
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
        ApplyUsage(o.Obj("usage"));
    }

    // CostUsd stays null on purpose: total_cost_usd is what the call would cost on the API, not what a subscription pays.
    private void ApplyUsage(JsonObject? u)
    {
        if (u is null) return;
        if (u.Int("input_tokens") is { } i) _usage.InputTokens = i;
        if (u.Int("output_tokens") is { } o && o > 0) _usage.OutputTokens = o;
        if (u.Int("cache_read_input_tokens") is { } r) _usage.CacheReadTokens = r;
        if (u.Int("cache_creation_input_tokens") is { } w) _usage.CacheWriteTokens = w;
        asm.SetUsage(_usage);
    }
}
