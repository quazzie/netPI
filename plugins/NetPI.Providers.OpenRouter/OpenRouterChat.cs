using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NetPI.Providers.OpenRouter;

/// <summary>OpenRouter's Chat Completions dialect (<c>POST /v1/chat/completions</c>, streaming).</summary>
internal static class OpenRouterChat
{
    public const string Path = "/v1/chat/completions";
    public const string ImageOmitted = "[image omitted: the selected model does not accept image input]";

    /// <summary>What the model is told about a tool's images when it cannot take them (they used to be dropped in
    /// silence, so the model answered as if the image were empty; idea-vgwg26).</summary>
    public static string ToolImagesOmitted(string callId, int count) =>
        $"[{count} image{(count == 1 ? "" : "s")} returned by tool call {callId} omitted: the selected model does not accept image input]";
    /// <summary>Key of the replay data on a <see cref="ThinkingPart"/>: the response's merged <c>reasoning_details</c>.</summary>
    public const string DetailsKey = "reasoning_details";

    public static JsonObject BuildBody(ModelRequest req, OpenRouterOptions o, string providerId)
    {
        var body = new JsonObject
        {
            ["model"] = req.Model.Id,
            ["messages"] = BuildMessages(req, o, providerId),
        };
        if (req.Tools.Count > 0)
        {
            var tools = new JsonArray();
            foreach (var t in req.Tools)
                tools.Add(new JsonObject
                {
                    ["type"] = "function",
                    ["function"] = new JsonObject { ["name"] = t.Name, ["description"] = t.Description, ["parameters"] = t.Parameters.DeepClone() },
                });
            body["tools"] = tools;
        }
        body["stream"] = true;
        body["max_tokens"] = MaxTokens(req, o);
        if (Reasoning(req.ReasoningEffort, req.Model) is { } reasoning) body["reasoning"] = reasoning;
        // Sticky routing: the session's requests stay on one upstream provider, which keeps its prompt cache warm.
        if (!string.IsNullOrEmpty(req.SessionId)) body["session_id"] = req.SessionId;
        if (o.Provider is { Count: > 0 } routing) body["provider"] = routing.DeepClone();
        // Claude needs caching turned on; the top-level form moves the breakpoint forward as the conversation grows.
        if (o.PromptCaching && IsAnthropic(req.Model.Id)) body["cache_control"] = new JsonObject { ["type"] = "ephemeral" };
        if (req.Temperature is { } temp) body["temperature"] = temp;
        return body;
    }

    public static bool IsAnthropic(string modelId) =>
        modelId.StartsWith("anthropic/", StringComparison.OrdinalIgnoreCase) || modelId.StartsWith("~anthropic/", StringComparison.OrdinalIgnoreCase);

    /// <summary>The request's value (else the catalog's), capped by <c>maxOutputTokens</c> (or the model's override) and the catalog.</summary>
    public static int MaxTokens(ModelRequest req, OpenRouterOptions o)
    {
        var cap = o.ModelOverride(req.Model.Id).Int("maxOutputTokens") is > 0 and var ov ? ov : o.MaxOutputTokens;
        if (req.Model.MaxOutputTokens is > 0 and var catalog) cap = Math.Min(cap, catalog);
        var value = req.MaxOutputTokens is > 0 ? req.MaxOutputTokens.Value : cap;
        return Math.Min(value, cap);
    }

    private static readonly string[] Scale = ["none", "minimal", "low", "medium", "high", "xhigh", "max"];

    /// <summary>
    /// The unified <c>reasoning</c> object for the session's effort. Nothing for the model default (no effort chosen) or
    /// a model without reasoning; "none" only where reasoning can be turned off; an effort the model does not list maps
    /// to the nearest one; models without effort levels only get enabled on/off.
    /// </summary>
    public static JsonObject? Reasoning(string? requested, ModelInfo model)
    {
        var r = requested?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(r) || r is "default" or "auto" or "null") return null;
        var raw = model.Extra.Obj("reasoning");
        if (raw is null && model.Reasoning is not { Supported: true }) return null;

        var mandatory = raw.Bool("mandatory", false);
        List<string>? efforts;
        if (raw is not null)
        {
            if (!raw.TryGetPropertyValue("supported_efforts", out var node)) efforts = [];   // no effort selection
            else if (node is null) efforts = null;                                            // every effort accepted
            else efforts = node is JsonArray arr ? [.. arr.Select(x => x?.ToString()).OfType<string>()] : [];
        }
        else efforts = [.. model.Reasoning!.Efforts];

        if (r == "none")
        {
            if (mandatory) return null;
            return efforts is null || efforts.Contains("none") ? new JsonObject { ["effort"] = "none" } : new JsonObject { ["enabled"] = false };
        }
        if (efforts is null) return new JsonObject { ["effort"] = r };
        var levels = efforts.Where(e => e != "none").ToList();
        if (levels.Count == 0) return new JsonObject { ["enabled"] = true };
        return Nearest(r, levels) is { } e ? new JsonObject { ["effort"] = e } : new JsonObject { ["enabled"] = true };
    }

    /// <summary>The listed effort closest to <paramref name="requested"/> on the none…max scale (ties prefer the higher).</summary>
    public static string? Nearest(string requested, IReadOnlyList<string> efforts)
    {
        foreach (var e in efforts)
            if (string.Equals(e, requested, StringComparison.OrdinalIgnoreCase)) return e;
        var ri = Array.IndexOf(Scale, requested);
        if (ri < 0) return null;
        string? best = null; var bestDist = int.MaxValue; var bestRank = -1;
        foreach (var e in efforts)
        {
            var ei = Array.IndexOf(Scale, e.ToLowerInvariant());
            if (ei < 0) continue;
            var dist = Math.Abs(ei - ri);
            if (dist < bestDist || (dist == bestDist && ei > bestRank)) { best = e; bestDist = dist; bestRank = ei; }
        }
        return best;
    }

    public static JsonArray BuildMessages(ModelRequest req, OpenRouterOptions o, string providerId)
    {
        var allowImages = req.Model.SupportsImages;
        var list = new JsonArray();
        if (!string.IsNullOrWhiteSpace(req.SystemPrompt)) list.Add(new JsonObject { ["role"] = "system", ["content"] = req.SystemPrompt });

        var toolImages = new List<(string CallId, ImagePart Image)>();
        var toolImagesOmitted = new List<(string CallId, int Count)>();
        void FlushToolImages()
        {
            if (toolImages.Count == 0 && toolImagesOmitted.Count == 0) return;
            var content = new JsonArray();
            foreach (var group in toolImages.GroupBy(x => x.CallId))
            {
                content.Add(new JsonObject { ["type"] = "text", ["text"] = $"[Image(s) returned by tool call {group.Key}]" });
                foreach (var (_, img) in group) content.Add(ImageUrl(img));
            }
            foreach (var (callId, count) in toolImagesOmitted)
                content.Add(new JsonObject { ["type"] = "text", ["text"] = ToolImagesOmitted(callId, count) });
            list.Add(new JsonObject { ["role"] = "user", ["content"] = content });
            toolImages.Clear();
            toolImagesOmitted.Clear();
        }

        foreach (var m in req.Messages)
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
                            ["function"] = new JsonObject { ["name"] = c.Name, ["arguments"] = string.IsNullOrWhiteSpace(c.Arguments) ? "{}" : c.Arguments },
                        });

                    // Reasoning goes back unmodified, and only to the model that produced it.
                    JsonArray? details = null;
                    var reasoning = "";
                    if (o.ReplayReasoning && m.Provider == providerId && m.Model == req.Model.Id)
                    {
                        foreach (var th in m.Parts.OfType<ThinkingPart>())
                            if (th.ProviderData?[DetailsKey] is JsonArray d)
                            {
                                details ??= [];
                                foreach (var x in d) details.Add(x?.DeepClone());
                            }
                        if (details is null)
                            reasoning = string.Join("\n\n", m.Parts.OfType<ThinkingPart>().Where(t => t.Text.Length > 0).Select(t => t.Text));
                    }
                    if (text.Length == 0 && calls.Count == 0 && details is null && reasoning.Length == 0) break;
                    var msg = new JsonObject { ["role"] = "assistant", ["content"] = text.Length == 0 && calls.Count > 0 ? null : text };
                    if (details is not null) msg[DetailsKey] = details;
                    else if (reasoning.Length > 0) msg["reasoning"] = reasoning;
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
                    if (!m.Parts.Any(p => p is ImagePart))
                    {
                        var text = string.Join("\n", m.Parts.OfType<TextPart>().Where(t => t.Text.Length > 0).Select(t => t.Text));
                        if (text.Length > 0) list.Add(new JsonObject { ["role"] = "user", ["content"] = text });
                        break;
                    }
                    var content = new JsonArray();
                    foreach (var p in m.Parts)
                    {
                        if (p is TextPart t && t.Text.Length > 0) content.Add(new JsonObject { ["type"] = "text", ["text"] = t.Text });
                        else if (p is ImagePart img) content.Add(allowImages ? ImageUrl(img) : new JsonObject { ["type"] = "text", ["text"] = ImageOmitted });
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

    /// <summary>
    /// OpenRouter usage: <c>prompt_tokens</c> includes cache reads and writes, <c>completion_tokens</c> includes reasoning.
    /// </summary>
    public static Usage Usage(JsonElement u)
    {
        var prompt = u.Long("prompt_tokens");
        var details = u.Prop("prompt_tokens_details");
        var cached = details.Long("cached_tokens");
        var written = details.Long("cache_write_tokens");
        return new Usage
        {
            InputTokens = Math.Max(0, prompt - cached - written),
            CacheReadTokens = cached,
            CacheWriteTokens = written,
            OutputTokens = u.Long("completion_tokens"),
            ReasoningTokens = u.Prop("completion_tokens_details").Long("reasoning_tokens"),
        };
    }
}

/// <summary>
/// Parses OpenRouter chunks: content (optionally with inline think tags), <c>reasoning</c> text, <c>reasoning_details</c>
/// (merged by index for replay), tool calls by index, usage with cost, the generation id and upstream provider, and
/// mid-stream errors (a top-level <c>error</c> with <c>finish_reason: "error"</c>).
/// </summary>
internal sealed class OpenRouterStreamParser(MessageAssembler asm, string provider, bool parseThinkTags)
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
    private readonly List<JsonObject> _details = [];
    private string? _finish;
    private bool _done;
    private bool _anyChunk;

    public bool Finished => _done;
    /// <summary>The response's reasoning details, merged the way a non-streamed response returns them.</summary>
    public IReadOnlyList<JsonObject> ReasoningDetails => _details;
    public string? GenerationId { get; private set; }
    /// <summary>The upstream provider that served the request (chunk field <c>provider</c>).</summary>
    public string? UpstreamProvider { get; private set; }
    public double? Cost { get; private set; }

    public void Handle(SseEvent sse)
    {
        if (sse.IsDone) { _done = true; return; }
        JsonDocument doc;
        try { doc = JsonDocument.Parse(sse.Data); }
        catch (JsonException) { return; }
        using (doc) HandleChunk(doc.RootElement, streaming: true);
    }

    // The same 200-with-a-broken-body as on the AiProxy transports (idea-3ivjku): a raw JsonException carried no
    // provider, no generation id, no saved request, and was not retried.
    public void HandleJsonBody(string json)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException) { throw NotJson(provider, json); }
        using (doc) HandleChunk(doc.RootElement, streaming: false);
        _done = true;
    }

    /// <summary>The 200 arrived as JSON but is not: reported like any other transport failure, and retried like one.</summary>
    public static ModelException NotJson(string provider, string json) => ProviderErrors.FromStream(provider,
        "bad_json", $"200 with a JSON content type, but the body is not JSON: {J.Truncate(json.Trim(), 200)}");

    private void HandleChunk(JsonElement root, bool streaming)
    {
        if (root.ValueKind != JsonValueKind.Object) return;
        if (root.Str("id") is { Length: > 0 } id) GenerationId ??= id;
        if (root.Str("provider") is { Length: > 0 } upstream) UpstreamProvider = upstream;
        if (root.Prop("error").Has())
        {
            var err = root.Prop("error");
            var (msg, type) = ProviderErrors.ExtractError(root);
            int? status = err.IsObj() ? err.IntOrNull("code") : null;
            var who = err.Prop("metadata").Str("provider_name") ?? UpstreamProvider;
            var text = msg ?? err.ToString();
            if (who is not null) text += $" (upstream provider {who})";
            throw ProviderErrors.FromStream(provider, type, text, status);
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
        if (usage.IsObj())
        {
            var u = OpenRouterChat.Usage(usage);
            if (usage.Prop("cost").ValueKind == JsonValueKind.Number) Cost = u.CostUsd = usage.Prop("cost").GetDouble();
            asm.SetUsage(u);
        }
    }

    private void HandleDelta(JsonElement delta)
    {
        // "reasoning" is the plain text; reasoning_details carries the same text plus signatures/encrypted blocks.
        var reasoning = delta.Prop("reasoning").ValueKind == JsonValueKind.String ? delta.Str("reasoning") : null;
        if (string.IsNullOrEmpty(reasoning)) reasoning = delta.Str("reasoning_content");
        var details = delta.Prop("reasoning_details");
        if (details.ValueKind == JsonValueKind.Array)
        {
            var fromDetails = new StringBuilder();
            foreach (var d in details.EnumerateArray())
            {
                AddDetail(d);
                if (d.IsObj()) fromDetails.Append(d.Str("text") ?? d.Str("summary"));
            }
            if (string.IsNullOrEmpty(reasoning)) reasoning = fromDetails.ToString();
        }
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

    /// <summary>Streamed pieces of one detail share its index: their text/summary/data are concatenated.</summary>
    private void AddDetail(JsonElement d)
    {
        if (!d.IsObj()) return;
        var incoming = JsonNode.Parse(d.GetRawText())!.AsObject();
        var last = _details.Count > 0 ? _details[^1] : null;
        var index = d.IntOrNull("index");
        if (last is null || index is null || last.Int("index") != index || last.Str("type") != d.Str("type"))
        {
            _details.Add(incoming);
            return;
        }
        foreach (var key in new[] { "text", "summary", "data" })
            if (incoming.Str(key) is { Length: > 0 } piece) last[key] = (last.Str(key) ?? "") + piece;
        foreach (var (k, v) in incoming)
        {
            if (k is "text" or "summary" or "data" || v is null) continue;
            if (k == "signature" || last[k] is null) last[k] = v.DeepClone();
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
            JsonValueKind.Object or JsonValueKind.Array => fn.Prop("arguments").GetRawText(),
            _ => null,
        };

        Call? call = null;
        var index = tc.IntOrNull("index");
        if (index is null)
        {
            if (!string.IsNullOrEmpty(id)) call = _order.FirstOrDefault(c => c.Id == id);
            else if (string.IsNullOrEmpty(name) && _order.Count > 0) call = _order[^1];
        }
        else if (_calls.TryGetValue(index.Value, out var existing))
        {
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
            call.Part = asm.StartToolCall(call.Id, call.Name);
            if (call.PendingArgs.Length > 0) { asm.AppendToolArgs(call.Part, call.PendingArgs.ToString()); call.PendingArgs.Clear(); }
        }
        else if (call.Part is not null) call.Part.Name = call.Name;
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
