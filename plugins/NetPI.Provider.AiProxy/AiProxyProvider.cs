using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NetPI.Abstractions;

namespace NetPI.Provider.AiProxy;

/// <summary>
/// OpenAI-compatible provider (PLAN §13-§15). Talks to an AiProxy endpoint
/// (baseUrl + /v1/chat/completions, /v1/models). All OpenAI wire types live
/// inside this file; only provider-neutral <see cref="ModelEvent"/>s cross the
/// Abstractions boundary.
/// </summary>
public sealed class AiProxyProvider : IModelProvider, IModelCatalog
{
    private const string TextBlockId = "text";
    private const string ThinkingBlockId = "thinking";

    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly IPluginLogger _log;
    private IReadOnlyList<ModelInfo> _models = [];
    private DateTimeOffset? _refreshedAt;

    public AiProxyProvider(HttpClient http, string baseUrl, IPluginLogger log)
    {
        _http = http;
        _baseUrl = baseUrl.TrimEnd('/');
        _log = log;
    }

    // ---- catalog ---------------------------------------------------------
    public IReadOnlyList<ModelInfo> Models => _models;
    public bool IsStale => _refreshedAt is null;
    public DateTimeOffset? LastRefreshedAt => _refreshedAt;

    public async ValueTask<IReadOnlyList<ModelInfo>> RefreshAsync(CancellationToken cancellationToken)
    {
        using var resp = await _http.GetAsync($"{_baseUrl}/v1/models", cancellationToken);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsStringAsync(cancellationToken);
        var doc = JsonDocument.Parse(body);
        var list = new List<ModelInfo>();
        if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            foreach (var m in data.EnumerateArray())
            {
                if (!m.TryGetProperty("id", out var id)) continue;
                var modelId = id.GetString() ?? "";
                list.Add(new ModelInfo(
                    modelId, "aiProxy", modelId,
                    SupportsTools: true,
                    SupportsThinking: modelId.Contains("reason") || modelId.Contains("think") || modelId.Contains("r1")));
            }
        }
        _models = list;
        _refreshedAt = DateTimeOffset.UtcNow;
        _log.Information($"AiProxy catalog: {list.Count} model(s)");
        return _models;
    }

    // ---- run (streaming) -------------------------------------------------
    public async IAsyncEnumerable<ModelEvent> RunAsync(ModelRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var payload = BuildPayload(request);
        using var content = new StringContent(JsonSerializer.Serialize(payload, WireOpts), System.Text.Encoding.UTF8, "application/json");

        using var resp = await _http.PostAsync($"{_baseUrl}/v1/chat/completions", content, cancellationToken);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(cancellationToken);
            yield return new ModelFailed(request.ModelId, $"HTTP {(int)resp.StatusCode}: {Truncate(err, 500)}");
            yield break;
        }

        var id = request.ModelId;
        yield return new ModelStarted(id);

        var textStarted = false;
        var thinkingStarted = false;
        var tools = new Dictionary<int, ToolState>();
        var parts = new List<MessagePart>();
        var usageSeen = false;

        await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        while (!reader.EndOfStream)
        {
            string? line = await reader.ReadLineAsync(cancellationToken);
            if (line is null) break;
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var data = line["data:".Length..].Trim();
            if (data == "[DONE]") break;
            if (data.Length == 0) continue;

            foreach (var ev in ParseChunk(data, ref textStarted, ref thinkingStarted, tools))
            {
                if (ev is UsageUpdated) usageSeen = true;
                yield return ev;
            }
        }

        if (textStarted) yield return new TextCompleted(TextBlockId);
        if (thinkingStarted) yield return new ThinkingCompleted(ThinkingBlockId);
        if (!usageSeen) yield return new UsageUpdated(0, 0, 0);

        var message = new AgentMessage(Guid.NewGuid().ToString("n"), MessageRole.Assistant, parts, DateTimeOffset.UtcNow);
        yield return new ModelCompleted(message);
        yield break;
    }

    /// <summary>Parse one SSE <c>data:</c> chunk into normalized events.</summary>
    private static IEnumerable<ModelEvent> ParseChunk(
        string data, ref bool textStarted, ref bool thinkingStarted, Dictionary<int, ToolState> tools)
    {
        using var doc = JsonDocument.Parse(data);
        var root = doc.RootElement;
        var events = new List<ModelEvent>();

        if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object
            && usage.TryGetProperty("total_tokens", out var tt))
        {
            int p = GetInt(usage, "prompt_tokens");
            int c = GetInt(usage, "completion_tokens");
            int total = tt.ValueKind == JsonValueKind.Number ? tt.GetInt32() : p + c;
            events.Add(new UsageUpdated(p, c, total));
        }

        if (!root.TryGetProperty("choices", out var ch) || ch.ValueKind != JsonValueKind.Array)
            return events;

        foreach (var choice in ch.EnumerateArray())
        {
            if (!choice.TryGetProperty("delta", out var delta) || delta.ValueKind != JsonValueKind.Object)
                continue;

            if (delta.TryGetProperty("reasoning_content", out var rc) && rc.ValueKind == JsonValueKind.String)
            {
                var t = rc.GetString() ?? "";
                if (t.Length > 0)
                {
                    if (!thinkingStarted) { thinkingStarted = true; events.Add(new ThinkingStarted(ThinkingBlockId)); }
                    events.Add(new ThinkingDelta(t));
                }
            }

            if (delta.TryGetProperty("content", out var ct) && ct.ValueKind == JsonValueKind.String)
            {
                var t = ct.GetString() ?? "";
                if (t.Length > 0)
                {
                    if (!textStarted) { textStarted = true; events.Add(new TextStarted(TextBlockId)); }
                    events.Add(new TextDelta(t));
                }
            }

            if (delta.TryGetProperty("tool_calls", out var tcs) && tcs.ValueKind == JsonValueKind.Array)
            {
                foreach (var tc in tcs.EnumerateArray())
                {
                    if (!tc.TryGetProperty("index", out var idxEl) || !idxEl.TryGetInt32(out var idx)) continue;
                    if (!tools.TryGetValue(idx, out var state))
                    {
                        var cid = tc.TryGetProperty("id", out var i) ? i.GetString() : null;
                        var fn0 = tc.TryGetProperty("function", out var f0) ? f0 : default;
                        var name = fn0.ValueKind == JsonValueKind.Object && fn0.TryGetProperty("name", out var nm0) ? nm0.GetString() : null;
                        state = new ToolState(cid ?? $"call_{idx}", name ?? "");
                        tools[idx] = state;
                    }
                    if (tc.TryGetProperty("function", out var fn) && fn.ValueKind == JsonValueKind.Object)
                    {
                        if (fn.TryGetProperty("name", out var nm) && nm.ValueKind == JsonValueKind.String)
                        {
                            var n2 = nm.GetString();
                            if (n2 is { Length: > 0 }) state.Name = state.Name.Length == 0 ? n2 : state.Name;
                        }
                        if (fn.TryGetProperty("arguments", out var args) && args.ValueKind == JsonValueKind.String)
                        {
                            var piece = args.GetString() ?? "";
                            if (piece.Length > 0) state.Args.Append(piece);
                        }
                    }
                    if (!state.Started) { state.Started = true; events.Add(new ToolCallStarted(state.Id, state.Name)); }
                    if (state.Args.Length > 0) events.Add(new ToolCallArgumentsDelta(state.Id, DrainArgs(state)));
                }
            }
        }
        return events;
    }

    private static string DrainArgs(ToolState s) { var x = s.Args.ToString(); s.Args.Clear(); return x; }
    private static int GetInt(JsonElement e, string prop)
        => e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;

    private sealed class ToolState { public string Id; public string Name; public StringBuilder Args = new(); public bool Started; public ToolState(string id, string name) { Id = id; Name = name; } }

    // ---- payload building -------------------------------------------------
    private static readonly JsonSerializerOptions WireOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static object BuildPayload(ModelRequest request)
    {
        var messages = request.Messages.Select(MessageToJson).ToList();
        var tools = request.Tools.Count > 0
            ? request.Tools.Select(t => new Dictionary<string, object>
              {
                  ["type"] = "function",
                  ["function"] = new Dictionary<string, object>
                  {
                      ["name"] = t.Name,
                      ["description"] = t.Description,
                      ["parameters"] = t.Parameters,
                  },
              }).ToList()
            : null;

        var payload = new Dictionary<string, object>
        {
            ["model"] = request.ModelId,
            ["messages"] = messages,
            ["stream"] = true,
            ["stream_options"] = new Dictionary<string, object> { ["include_usage"] = true },
        };
        if (tools is not null) payload["tools"] = tools;
        if (request.Temperature is { } t) payload["temperature"] = t;
        if (request.MaxTokens is { } mt) payload["max_tokens"] = mt;
        if (request.Seed is { } s && !string.IsNullOrEmpty(s) && int.TryParse(s, out var n)) payload["seed"] = n;
        return payload;
    }

    private static Dictionary<string, object> MessageToJson(AgentMessage m)
    {
        var role = m.Role switch
        {
            MessageRole.User => "user",
            MessageRole.Assistant => "assistant",
            MessageRole.System => "system",
            MessageRole.Tool => "tool",
            _ => "user",
        };
        var msg = new Dictionary<string, object> { ["role"] = role };
        var textParts = m.Parts.OfType<TextPart>().Select(p => p.Text).ToList();
        var thinking = m.Parts.OfType<ThinkingPart>().FirstOrDefault();

        if (role is "user" or "system" or "assistant")
        {
            msg["content"] = string.Join("\n", textParts);
            if (thinking is not null) msg["reasoning_content"] = thinking.Text;
        }

        if (m.Role == MessageRole.Assistant)
        {
            var calls = m.Parts.OfType<ToolCallPart>().ToList();
            if (calls.Count > 0)
                msg["tool_calls"] = calls.Select(c => new Dictionary<string, object>
                {
                    ["id"] = c.Id,
                    ["type"] = "function",
                    ["function"] = new Dictionary<string, object>
                    {
                        ["name"] = c.Name,
                        ["arguments"] = c.Arguments.GetRawText(),
                    },
                }).ToList();
        }

        if (m.Role == MessageRole.Tool)
        {
            var res = m.Parts.OfType<ToolResultPart>().FirstOrDefault();
            if (res is not null)
            {
                msg["role"] = "tool";
                msg["tool_call_id"] = res.ToolCallId;
                msg["content"] = string.Join("\n", res.Parts.OfType<TextPart>().Select(p => p.Text));
            }
        }
        return msg;
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}
