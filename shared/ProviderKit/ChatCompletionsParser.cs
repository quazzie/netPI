// Compiled into each provider plugin from shared/ProviderKit (plugins do not reference each other): edit it here.
using System.Text;
using System.Text.Json;

namespace NetPI.Providers.Kit;

/// <summary>
/// One parser for the OpenAI Chat Completions stream (<c>POST /v1/chat/completions</c>): content, inline think
/// tags, tool calls by index, usage, and the truncated-stream error. The dialects differ in what they add to a
/// chunk - reasoning under another name, cost, the upstream provider - and override only the hooks for that.
/// </summary>
internal class ChatCompletionsParser(MessageAssembler asm, string provider, bool parseThinkTags)
{
    protected sealed class Call
    {
        public string? Id;
        public string Name = "";
        public ToolCallPart? Part;
        public readonly StringBuilder PendingArgs = new();
    }

    protected MessageAssembler Asm { get; } = asm;
    protected string Provider { get; } = provider;

    private readonly ThinkTagSplitter? _think = parseThinkTags ? new ThinkTagSplitter() : null;
    private readonly Dictionary<int, Call> _calls = [];
    private readonly List<Call> _order = [];
    private readonly DroppedFrames _dropped = new();
    private string? _finish;
    private bool _done;
    private bool _anyChunk;

    public bool Finished => _done;

    /// <summary>The completion id every chunk carries. It is the only identifier a backend that sends no
    /// <c>x-request-id</c> header offers, so a failure could not be correlated with the server's own log
    /// (idea-uab5a4). OpenRouter calls it a generation.</summary>
    public string? ResponseId { get; private set; }

    // ================================================================ dialect hooks

    /// <summary>A dialect's own fields in the chunk envelope, read before the choices.</summary>
    protected virtual void OnChunk(JsonElement root) { }

    /// <summary>The reasoning text in one delta, or null when it carries none.</summary>
    protected virtual string? ReasoningText(JsonElement delta)
    {
        // "reasoning" is the plain text; "reasoning_content" is the older OpenAI-compatible name.
        var reasoning = delta.Prop("reasoning").ValueKind == JsonValueKind.String ? delta.Str("reasoning") : null;
        return string.IsNullOrEmpty(reasoning) ? delta.Str("reasoning_content") : reasoning;
    }

    /// <summary>What an <c>error</c> in the body says, before it becomes a <see cref="ModelException"/>.</summary>
    protected virtual string ErrorText(JsonElement err, string? message) => message ?? err.ToString();

    /// <summary>The usage one chunk reports.</summary>
    protected virtual Usage? ReadUsage(JsonElement usage)
    {
        // prompt_tokens includes cache reads and writes, completion_tokens includes reasoning.
        var prompt = usage.Long("prompt_tokens");
        var details = usage.Prop("prompt_tokens_details");
        var cached = details.Long("cached_tokens");
        var written = details.Long("cache_write_tokens");
        return new Usage
        {
            InputTokens = Math.Max(0, prompt - cached - written),
            CacheReadTokens = cached,
            CacheWriteTokens = written,
            OutputTokens = usage.Long("completion_tokens"),
            ReasoningTokens = usage.Prop("completion_tokens_details").Long("reasoning_tokens"),
        };
    }

    // ================================================================ the stream

    public void Handle(SseEvent sse)
    {
        if (sse.IsDone) { _done = true; return; }
        JsonDocument doc;
        // A frame that does not parse is counted, not swallowed: the finish reason and the usage arrive last, so
        // dropping one silently ends the call looking clean (idea-saljbd).
        try { doc = JsonDocument.Parse(sse.Data); }
        catch (JsonException) { _dropped.Add(sse.Data); return; }
        using (doc) HandleChunk(doc.RootElement, streaming: true);
    }

    // A server that ignores stream:true answers with a plain JSON body, but a proxy or a WAF in front of it can
    // answer 200 + application/json with an HTML error page (or nothing). The parse used to throw a raw
    // JsonException: no provider, no request id, no saved request, and RetryMiddleware does not retry it
    // (idea-3ivjku).
    public void HandleJsonBody(string json)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException) { throw NotJson(Provider, json); }
        using (doc) HandleChunk(doc.RootElement, streaming: false);
        _done = true;
    }

    /// <summary>The 200 arrived as JSON but is not: reported like any other transport failure, and retried like one.</summary>
    public static ModelException NotJson(string provider, string json) => ProviderErrors.FromStream(provider,
        "bad_json", $"200 with a JSON content type, but the body is not JSON: {J.Truncate(json.Trim(), 200)}");

    private void HandleChunk(JsonElement root, bool streaming)
    {
        if (root.ValueKind != JsonValueKind.Object) return;
        if (root.Str("id") is { Length: > 0 } id) ResponseId ??= id;
        OnChunk(root);
        if (root.Prop("error").Has())
        {
            var err = root.Prop("error");
            var (msg, type) = ProviderErrors.ExtractError(root);
            int? status = err.IsObj() ? err.IntOrNull("code") : null;
            throw ProviderErrors.FromStream(Provider, type, ErrorText(err, msg), status);
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
        if (usage.IsObj() && ReadUsage(usage) is { } u) Asm.SetUsage(u);
    }

    private void HandleDelta(JsonElement delta)
    {
        var reasoning = ReasoningText(delta);
        if (!string.IsNullOrEmpty(reasoning))
        {
            FlushThink();
            Asm.AddThinking(reasoning);
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
        if (_think is null) { Asm.AddText(text); return; }
        _think.Process(text, EmitSegment);
    }

    private void EmitSegment(bool thinking, string text)
    {
        if (thinking) Asm.AddThinking(text);
        else Asm.AddText(text);
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
        // A name can arrive in fragments ("wr" then "ite"): keep accumulating after the part exists, or the call is
        // dispatched under its first fragment. The part keeps the id it was started with, so the events already
        // emitted for it (ToolCallStarted, ToolCallArgsDelta) and the persisted part still agree.
        if (!string.IsNullOrEmpty(name)) call.Name += name;

        if (call.Part is null && call.Name.Length > 0)
        {
            call.Part = Asm.StartToolCall(call.Id, call.Name);
            if (call.PendingArgs.Length > 0) { Asm.AppendToolArgs(call.Part, call.PendingArgs.ToString()); call.PendingArgs.Clear(); }
        }
        else if (call.Part is not null) call.Part.Name = call.Name;
        if (string.IsNullOrEmpty(args)) return;
        if (call.Part is null) call.PendingArgs.Append(args);
        else Asm.AppendToolArgs(call.Part, args);
    }

    public void Finish()
    {
        FlushThink();
        foreach (var c in _order)
        {
            if (c.Part is not null) continue;
            c.Part = Asm.StartToolCall(c.Id, c.Name);
            Asm.AppendToolArgs(c.Part, c.PendingArgs.ToString());
        }
        if (!_done && _finish is null) throw ProviderErrors.UnexpectedEnd(Provider, _dropped.Note);
        if (!_anyChunk && !Asm.HasContent) throw ProviderErrors.UnexpectedEnd(Provider, _dropped.Note);

        Asm.StopReason = _finish switch
        {
            "tool_calls" or "function_call" => "tool_use",
            "length" or "max_tokens" => "length",
            "content_filter" => "content_filter",
            null or "stop" or "eos" or "end_turn" => Asm.ToolCallCount > 0 ? "tool_use" : "stop",
            _ => _finish,
        };
    }
}