using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NetPI.Providers.OpenRouter;

/// <summary>OpenRouter's Chat Completions dialect (<c>POST /v1/chat/completions</c>, streaming).</summary>
internal static class OpenRouterChat
{
    public const string Path = "/v1/chat/completions";
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

    /// <summary>
    /// The request's value (else the catalog's), capped by <c>maxOutputTokens</c> (or the model's override), the
    /// catalog and what the model's context window has room for beside this request (idea-begg3v).
    /// </summary>
    public static int MaxTokens(ModelRequest req, OpenRouterOptions o)
    {
        var cap = o.ModelOverride(req.Model.Id).Int("maxOutputTokens") is > 0 and var ov ? ov : o.MaxOutputTokens;
        if (req.Model.MaxOutputTokens is > 0 and var catalog) cap = Math.Min(cap, catalog);
        var value = req.MaxOutputTokens is > 0 ? req.MaxOutputTokens.Value : cap;
        return Math.Min(ModelMessages.ClampMaxTokens(req, value), cap);
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

    public static JsonArray BuildMessages(ModelRequest req, OpenRouterOptions o, string providerId) =>
        new ChatBuilder(o.ReplayReasoning, req.Model.SupportsImages, providerId, req.Model.Id).Build(req.SystemPrompt, req.Messages);

    /// <summary>OpenRouter's assistant turn: the reasoning goes back unmodified, and only to the model that produced it.</summary>
    private sealed class ChatBuilder(bool replayReasoning, bool allowImages, string providerId, string modelId)
        : ChatMessageBuilder(allowImages)
    {
        protected override JsonObject? Reasoning(ChatMessage message)
        {
            if (!replayReasoning || message.Provider != providerId || message.Model != modelId) return null;
            JsonArray? details = null;
            foreach (var th in message.Parts.OfType<ThinkingPart>())
                if (th.ProviderData?[DetailsKey] is JsonArray d)
                {
                    details ??= [];
                    foreach (var x in d) details.Add(x?.DeepClone());
                }
            if (details is not null) return new JsonObject { [DetailsKey] = details };
            var text = string.Join("\n\n", message.Parts.OfType<ThinkingPart>().Where(t => t.Text.Length > 0).Select(t => t.Text));
            return text.Length > 0 ? new JsonObject { ["reasoning"] = text } : null;
        }
    }
}

/// <summary>
/// Parses OpenRouter chunks: content (optionally with inline think tags), <c>reasoning</c> text, <c>reasoning_details</c>
/// (merged by index for replay), tool calls by index, usage with cost, the generation id and upstream provider, and
/// mid-stream errors. The shared parser in <c>shared/ProviderKit</c> does the rest.
/// </summary>
internal sealed class OpenRouterStreamParser(MessageAssembler asm, string provider, bool parseThinkTags)
    : ChatCompletionsParser(asm, provider, parseThinkTags)
{
    private readonly List<JsonObject> _details = [];

    /// <summary>The response's reasoning details, merged the way a non-streamed response returns them.</summary>
    public IReadOnlyList<JsonObject> ReasoningDetails => _details;
    /// <summary>OpenRouter's name for the response id: the generation.</summary>
    public string? GenerationId => ResponseId;
    /// <summary>The upstream provider that served the request (chunk field <c>provider</c>).</summary>
    public string? UpstreamProvider { get; private set; }
    public double? Cost { get; private set; }

    protected override void OnChunk(JsonElement root)
    {
        if (root.Str("provider") is { Length: > 0 } upstream) UpstreamProvider = upstream;
    }

    protected override string ErrorText(JsonElement err, string? message)
    {
        var text = base.ErrorText(err, message);
        var who = err.Prop("metadata").Str("provider_name") ?? UpstreamProvider;
        return who is not null ? text + $" (upstream provider {who})" : text;
    }

    protected override Usage? ReadUsage(JsonElement usage)
    {
        var u = base.ReadUsage(usage)!;
        if (usage.Prop("cost").ValueKind == JsonValueKind.Number) Cost = u.CostUsd = usage.Prop("cost").GetDouble();
        return u;
    }

    protected override string? ReasoningText(JsonElement delta)
    {
        // reasoning_details carries the same text as "reasoning" plus signatures/encrypted blocks, and may be the
        // only thing a delta carries: merging it here keeps both.
        var text = base.ReasoningText(delta);
        var details = delta.Prop("reasoning_details");
        if (details.ValueKind != JsonValueKind.Array) return text;
        var fromDetails = new StringBuilder();
        foreach (var d in details.EnumerateArray())
        {
            AddDetail(d);
            if (d.IsObj()) fromDetails.Append(d.Str("text") ?? d.Str("summary"));
        }
        return string.IsNullOrEmpty(text) ? fromDetails.ToString() : text;
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
}
