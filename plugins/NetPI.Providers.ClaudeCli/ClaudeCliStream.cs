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
    private bool _sawUsage;
    public bool Finished { get; private set; }
    /// <summary>The init line's apiKeySource: anything but "none" means the CLI would bill an API key.</summary>
    public string? ApiKeySource { get; private set; }
    public string? AssistantError { get; private set; }

    private readonly Dictionary<int, (string Name, System.Text.StringBuilder Json)> _toolBlocks = [];
    private readonly List<string> _activity = [];

    /// <summary>What the CLI's own tools did since the last call ("Read src/x.cs"): shown as notices, not as tool calls of the agent.</summary>
    public IReadOnlyList<string> TakeActivity()
    {
        if (_activity.Count == 0) return [];
        var list = _activity.ToList();
        _activity.Clear();
        return list;
    }

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
                else if (type == "tool_use") _toolBlocks[index] = (e.Obj("content_block").Str("name") ?? "tool", new System.Text.StringBuilder());
                break;
            }
            case "content_block_delta":
            {
                var d = e.Obj("delta");
                if (d.Str("type") == "input_json_delta" && _toolBlocks.TryGetValue(e.Int("index") ?? 0, out var tb)) { tb.Json.Append(d.Str("partial_json")); break; }
                if (!_blocks.TryGetValue(e.Int("index") ?? 0, out var part)) break;
                switch (d.Str("type"))
                {
                    case "text_delta" when part is TextPart t: asm.AppendText(t, d.Str("text")); _streamed = true; break;
                    case "thinking_delta" when part is ThinkingPart th: asm.AppendThinking(th, d.Str("thinking")); _streamed = true; break;
                    case "signature_delta" when part is ThinkingPart sig: sig.Signature = d.Str("signature"); break;
                }
                break;
            }
            case "content_block_stop" when _toolBlocks.Remove(e.Int("index") ?? 0, out var tool):
                _activity.Add(Describe(tool.Name, tool.Json.ToString()));
                break;
            case "content_block_stop":
                if (_blocks.TryGetValue(e.Int("index") ?? 0, out var p) && p is ThinkingPart tp) asm.EndThinking(tp);
                break;
            case "message_delta":
                ApplyUsage(e.Obj("usage"));
                if (e.Obj("delta").Str("stop_reason") == "max_tokens") asm.StopReason = "length";
                break;
        }
    }

    /// <summary>"Read src/x.cs", "Bash: git status": the tool and the one argument that says what it did.</summary>
    internal static string Describe(string tool, string json)
    {
        string? arg = null;
        try
        {
            if (JsonNode.Parse(json) is JsonObject args)
                foreach (var key in new[] { "file_path", "path", "pattern", "command", "url", "query", "description" })
                    if (args.Str(key) is { Length: > 0 } v) { arg = v; break; }
        }
        catch (JsonException) { /* cut-off arguments: the tool name alone */ }
        if (arg is null) return $"Claude CLI: {tool}";
        arg = arg.ReplaceLineEndings(" ");
        return $"Claude CLI: {tool} {(arg.Length > 160 ? arg[..160] + "…" : arg)}";
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
