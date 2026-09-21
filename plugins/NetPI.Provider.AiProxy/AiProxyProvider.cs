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
    private readonly string _wire; // "auto" (default) | "chat" | "responses" — PLAN §14b
    /// <summary>Optional host event bus for publishing <see cref="ModelRequestDiagnostics"/> (PLAN §47).</summary>
    private readonly IEventBus? _bus;
    private IReadOnlyList<ModelInfo> _models = [];
    /// <summary>
    /// PLAN §14c: per-session Responses-wire chain head, keyed
    /// <c>"sessionId|modelId"</c>. nInfer uses the chain's
    /// <c>previous_response_id</c> as the session key (LiveSession KV-cache
    /// retention vs RecentPrivate), so continuations must reference the last
    /// successful run. Covered holds the fingerprints of the transcript
    /// prefix the stored chain already contains: those items are NEVER
    /// resent (the server appends the stored chain on top of the input —
    /// resending them duplicates tokens). The head advances only on a
    /// successful ModelCompleted; a failed or abandoned run leaves the head
    /// in place (the transcript was not mutated, so it is still valid).
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, ChainHead> _chainHeads = new();

    /// <summary>PLAN §14c: one chain head for a session/model pair.</summary>
    private sealed class ChainHead
    {
        /// <summary>Last successful response.id; sent as previous_response_id.</summary>
        public string ResponseId = "";
        /// <summary>Fingerprints of the transcript messages the stored chain covers.</summary>
        public List<string> Covered = [];

        public ChainHead(string responseId, List<string> covered)
        {
            ResponseId = responseId;
            Covered = covered;
        }
    }
    private DateTimeOffset? _refreshedAt;

    public AiProxyProvider(HttpClient http, string baseUrl, IPluginLogger log, string wire = "auto", IEventBus? bus = null)
    {
        _http = http;
        _baseUrl = baseUrl.TrimEnd('/');
        _log = log;
        _wire = string.IsNullOrWhiteSpace(wire) ? "auto" : wire.Trim();
        _bus = bus;
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

                // PLAN §13: parse AiProxy's actual metadata (no model-name
                // heuristics); unknown/extra fields are simply ignored, so
                // AiProxy can evolve without breaking netPI.
                int? ctx = GetIntProp(m, "context_window") ?? GetIntProp(m, "max_model_len");
                if (ctx is null && m.TryGetProperty("meta", out var meta) && meta.ValueKind == JsonValueKind.Object
                    && meta.TryGetProperty("n_ctx", out var nc) && nc.ValueKind == JsonValueKind.Number)
                    ctx = nc.GetInt32();
                var maxOut = GetIntProp(m, "max_tokens") ?? GetIntProp(m, "max_output_tokens");

                var modalities = new List<string>();
                if (m.TryGetProperty("input_modalities", out var im) && im.ValueKind == JsonValueKind.Array)
                    foreach (var it in im.EnumerateArray())
                        if (it.ValueKind == JsonValueKind.String) modalities.Add(it.GetString()!);
                if (modalities.Count == 0) modalities.Add("text");

                // AiProxy's richer catalog is authoritative. Accept the metadata
                // shapes used by current/older AiProxy builds without falling back
                // to model-name heuristics.
                var levels = ParseReasoningLevels(m);
                var defaultReasoning = ParseReasoningDefault(m);

                list.Add(new ModelInfo(
                    modelId, "aiProxy", modelId,
                    SupportsTools: true,
                    SupportsThinking: levels is not null,
                    ContextWindowTokens: ctx,
                    MaxOutputTokens: maxOut,
                    ReasoningLevels: levels)
                {
                    InputModalities = [.. modalities],
                    DefaultReasoningLevel = defaultReasoning,
                });
            }
        }
        // Publish the catalog IMMEDIATELY. The model list is complete here and is
        // all the UI needs to show models. The /v1/responses capability probe is slow
        // on a cold nInfer (each probe forces the model to load, ~7-10 s each) and only
        // decides which wire each run uses, so it must never block RefreshAsync.
        // Runs default to chat completions until a probe refines the flag.
        _models = list;
        _refreshedAt = DateTimeOffset.UtcNow;
        _log.Information($"AiProxy catalog: {list.Count} model(s)");

        if (_wire is "auto" or "responses")
        {
            var gen = ++_catalogGeneration;
            _probeTask = Task.Run(() => RunResponsesProbeInBackground(list, gen, cancellationToken));
        }
        return _models;
    }

    /// <summary>Await the in-flight background responses-capability probe (no-op when
    /// none is running). Callers that depend on <c>SupportsResponses</c> being current
    /// (tests, diagnostics) await this; the host's UI path never does.</summary>
    public async Task WaitForProbeAsync()
    {
        var t = _probeTask;
        if (t is not null) await t;
    }

    private async Task RunResponsesProbeInBackground(List<ModelInfo> snapshot, int generation, CancellationToken externalToken)
    {
        try
        {
            var probed = new List<ModelInfo>(snapshot.Count);
            probed.AddRange(snapshot.Select(_ => (ModelInfo)null!));
            await Task.WhenAll(Enumerable.Range(0, snapshot.Count).Select(async i =>
                probed[i] = await ProbeResponsesSupportAsync(snapshot[i], externalToken)));
            if (generation == _catalogGeneration)
            {
                _models = probed;
                foreach (var m in probed)
                    _log.Debug($"responses wire: {m.ModelId} supports={m.SupportsResponses}");
            }
        }
        catch { /* best-effort: on failure the pre-probe catalog stays as-is */ }
    }

    /// <summary>One-shot non-streaming probe of <c>/v1/responses</c> for a model.</summary>
    private async Task<ModelInfo> ProbeResponsesSupportAsync(ModelInfo model, CancellationToken cancellationToken)
    {
        try
        {
            var body = new StringContent(
                JsonSerializer.Serialize(new { model = model.ModelId, input = "hi", stream = false, store = false }),
                Encoding.UTF8, "application/json");
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));
            using var resp = await _http.PostAsync($"{_baseUrl}/v1/responses", body, timeoutCts.Token);
            if (resp.IsSuccessStatusCode)
            {
                _log.Debug($"responses probe ok: {model.ModelId}");
                return model with { SupportsResponses = true };
            }
            _log.Debug($"responses probe failed ({(int)resp.StatusCode}): {model.ModelId}");
        }
        catch (Exception ex) // non-fatal by design: timeout, network blip or shutdown all keep SupportsResponses=false
        {
            _log.Debug($"responses probe error for {model.ModelId}: {ex.Message}");
        }
        return model with { SupportsResponses = false };
    }

    private static IReadOnlyList<string>? ParseReasoningLevels(JsonElement model)
    {
        var values = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        static bool IsEffortToken(string value)
        {
            var v = value.Trim().Replace('-', '_').ToLowerInvariant();
            return v is "none" or "off" or "minimal" or "low" or "medium"
                or "high" or "xhigh" or "extra_high" or "max" or "auto";
        }

        static bool IsEffortContainer(string name)
        {
            var n = name.Replace('-', '_').ToLowerInvariant();
            return n.Contains("effort", StringComparison.Ordinal)
                || n.Contains("level", StringComparison.Ordinal)
                || n is "allowed" or "values" or "options" or "profiles" or "profile";
        }

        static bool IsReasoningField(string name)
        {
            var n = name.Replace('-', '_').ToLowerInvariant();
            return n.Contains("reasoning", StringComparison.Ordinal) || IsEffortContainer(name);
        }

        void Add(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return;
            foreach (var piece in raw.Split([',', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!IsEffortToken(piece)) continue;
                if (seen.Add(piece)) values.Add(piece);
            }
        }

        void Collect(JsonElement node, int depth)
        {
            if (depth > 8) return;

            switch (node.ValueKind)
            {
                case JsonValueKind.String:
                    Add(node.GetString());
                    return;

                case JsonValueKind.Array:
                    foreach (var item in node.EnumerateArray())
                        Collect(item, depth + 1);
                    return;

                case JsonValueKind.Object:
                    foreach (var prop in node.EnumerateObject())
                    {
                        // AiProxy versions have represented effort profiles both
                        // as arrays and as keyed objects, e.g.
                        // { efforts:[...] } and { profiles:{ low:{...}, high:{...} } }.
                        if (IsEffortToken(prop.Name)
                            && prop.Value.ValueKind is not JsonValueKind.False and not JsonValueKind.Null)
                            Add(prop.Name);

                        Collect(prop.Value, depth + 1);
                    }
                    return;
            }
        }

        // Scan only reasoning/effort-related top-level metadata. Once inside
        // that subtree, accept only recognized effort tokens; metadata strings
        // such as type:"observed" can therefore never become UI options.
        foreach (var prop in model.EnumerateObject())
        {
            if (IsReasoningField(prop.Name))
                Collect(prop.Value, 0);
        }

        return values.Count > 0 ? values : null;
    }

    private static string? ParseReasoningDefault(JsonElement model)
    {
        static bool LooksLikeDefault(string name)
        {
            var n = name.Replace('-', '_').ToLowerInvariant();
            return n is "default" or "default_effort" or "defaulteffort"
                or "default_level" or "defaultlevel"
                || (n.Contains("default", StringComparison.Ordinal)
                    && (n.Contains("reason", StringComparison.Ordinal)
                        || n.Contains("effort", StringComparison.Ordinal)
                        || n.Contains("level", StringComparison.Ordinal)));
        }

        static string? Find(JsonElement node, int depth)
        {
            if (depth > 8 || node.ValueKind != JsonValueKind.Object) return null;

            foreach (var prop in node.EnumerateObject())
                if (LooksLikeDefault(prop.Name) && prop.Value.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(prop.Value.GetString()))
                    return prop.Value.GetString();

            foreach (var prop in node.EnumerateObject())
                if (prop.Value.ValueKind == JsonValueKind.Object)
                {
                    var nested = Find(prop.Value, depth + 1);
                    if (!string.IsNullOrWhiteSpace(nested)) return nested;
                }

            return null;
        }

        return Find(model, 0);
    }

    private static string? FirstString(JsonElement obj, params string[] names)
    {
        if (obj.ValueKind != JsonValueKind.Object) return null;
        foreach (var name in names)
            if (obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(value.GetString()))
                return value.GetString();
        return null;
    }

    // ---- run (dispatch — PLAN §14b) ------------------------------------------
    public async IAsyncEnumerable<ModelEvent> RunAsync(ModelRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        bool wantedResponses = UseResponsesWire(ModelById(request.ModelId));
        string failReason = "";

        if (wantedResponses)
        {
            var chainKey = string.IsNullOrEmpty(request.SessionId) ? null : ChainKey(request.SessionId, request.ModelId);

            // At most two attempts on the responses wire (PLAN §14d). A
            // pre-content failure carrying ninfer's 404 code
            // response_not_found means the server no longer stores the
            // referenced previous_response_id (nInfer restart, workload swap,
            // response-store LRU eviction): this session's chain head is
            // stale. We do NOT degrade to the chat wire for that — the head
            // would keep referencing the dead id and every later turn would
            // 404 again, re-sending the full transcript each time. Instead
            // the stale head is dropped and the run is retried ONCE on the
            // same wire as a reset (no previous_response_id, full input,
            // store:true): a clean completion re-anchors the chain so every
            // later turn chains on the new head again. Any OTHER pre-content
            // failure still falls through to the transparent chat-completions
            // fallback below (PLAN §47).
            for (var attempt = 0; ; attempt++)
            {
                bool contentSeen = false;
                bool hardFailed = false;
                var pending = new List<ModelEvent>();
                var chained = request.SessionId is not null && GetChainHead(request.SessionId, request.ModelId) is not null;

                await foreach (var ev in RunResponsesAsync(request, cancellationToken))
                {
                    if (ev is ModelFailed)
                    {
                        // A hard failure before any content (HTTP error,
                        // response.failed) is either healed as a stale chain
                        // head (below) or falls through to the chat fallback;
                        // after content it is surfaced and left to the retry
                        // plugin.
                        if (!contentSeen) { hardFailed = true; failReason = ((ModelFailed)ev).Error; break; }
                        foreach (var p in pending) yield return p;
                        pending.Clear();
                        yield return ev;
                        yield break;
                    }

                    if (!contentSeen)
                    {
                        if (ev is TextDelta or ThinkingDelta or ToolCallStarted)
                        {
                            contentSeen = true;
                            foreach (var p in pending) yield return p;
                            pending.Clear();
                            yield return ev;
                        }
                        else pending.Add(ev);
                        continue;
                    }

                    yield return ev;
                }

                if (!hardFailed)
                {
                    await PublishDiagnosticsAsync(request, "responses", true, chained, false, null, cancellationToken);
                    yield break;
                }

                if (attempt == 0 && chainKey is not null && IsStaleChainFailure(failReason)
                    && _chainHeads.TryRemove(chainKey, out _))
                {
                    _log.Information(
                        $"{request.ModelId}: session {request.SessionId} chain head is stale ({Truncate(failReason, 200)}); " +
                        "dropped, re-anchoring via reset on the responses wire");
                    continue; // same wire, reset shape: no previous_response_id
                }
                break;
            }

            _log.Warning($"responses wire failed before content for {request.ModelId} ({failReason}); retrying via chat completions — session chain disabled, full transcript re-sent every request");
        }

        // Wire decision: chat completions serves (either directly, or as the
        // transparent fallback after a responses-wire failure — PLAN §47).
        await PublishDiagnosticsAsync(request, "chat", wantedResponses, false, wantedResponses, wantedResponses ? failReason : null, cancellationToken);

        await foreach (var ev in RunChatCompletionsAsync(request, cancellationToken))
            yield return ev;
        yield break;
    }

    /// <summary>PLAN §47: publish the wire-decision record; diagnostics must never break a run.</summary>
    private async ValueTask PublishDiagnosticsAsync(ModelRequest request, string served, bool wanted, bool chained, bool fallback, string? failure, CancellationToken cancellationToken)
    {
        if (_bus is null) return;
        var ev = new ModelRequestDiagnostics(
            request.SessionId, request.ModelId, _wire, wanted ? "responses" : "chat", served, chained, fallback, failure);
        try { await _bus.PublishAsync(ev, CancellationToken.None); }
        catch { /* publish is best-effort */ }
    }

    private bool UseResponsesWire(ModelInfo? model) =>
        _wire != "chat" && model is { SupportsResponses: true };

    private int _catalogGeneration;
    private Task? _probeTask;

    private ModelInfo? ModelById(string id) => _models.FirstOrDefault(m => m.ModelId == id);

    // ---- run (chat completions, PLAN §14) ------------------------------------
    private async IAsyncEnumerable<ModelEvent> RunChatCompletionsAsync(ModelRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var payload = BuildPayload(request);
        using var content = new StringContent(JsonSerializer.Serialize(payload, WireOpts), System.Text.Encoding.UTF8, "application/json");

        // ResponseHeadersRead: must not wait for the full body (default
        // ResponseFinished would buffer the whole stream and kill streaming).
        using var resp = await _http.SendAsync(new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/v1/chat/completions") { Content = content },
            System.Net.Http.HttpCompletionOption.ResponseHeadersRead, cancellationToken);
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
        var textBuf = new StringBuilder();
        var thinkBuf = new StringBuilder();

        var usageSeen = false;

        await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        // CA2024: StreamReader.EndOfStream on an open network stream blocks
        // synchronously until the end of the physical stream, which would make
        // this loop consume the whole response at once instead of streaming.
        // Read until ReadLineAsync returns null instead.
        string? line;
        while ((line = await reader.ReadLineAsync(cancellationToken)) is not null)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var data = line["data:".Length..].Trim();
            if (data == "[DONE]") break;
            if (data.Length == 0) continue;

            foreach (var ev in ParseChunk(data, ref textStarted, ref thinkingStarted, tools))
            {
                if (ev is UsageUpdated) usageSeen = true;
                if (ev is TextDelta td) textBuf.Append(td.Text);
                else if (ev is ThinkingDelta tdt) thinkBuf.Append(tdt.Text);
                yield return ev;
            }
        }


        if (textStarted) yield return new TextCompleted(TextBlockId);
        if (thinkingStarted) yield return new ThinkingCompleted(ThinkingBlockId);
        if (!usageSeen) yield return new UsageUpdated(0, 0, 0);

        // ---- accumulate streamed parts into the completed message (PLAN §9) ----
        if (thinkBuf.Length > 0) parts.Add(new ThinkingPart(thinkBuf.ToString()));
        if (textBuf.Length > 0) parts.Add(new TextPart(textBuf.ToString()));
        foreach (var tc in tools.Values)
            if (tc.Started)
            {
                var argsJson = tc.FullArgs.Length > 0 ? tc.FullArgs.ToString().Trim() : string.Empty;
                JsonElement argsEl;
                try { argsEl = JsonDocument.Parse(argsJson).RootElement.Clone(); }
                catch { argsEl = JsonDocument.Parse("{}").RootElement.Clone(); }
                parts.Add(new ToolCallPart(tc.Id, tc.Name, argsEl));

            }

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
                            if (piece.Length > 0) { state.Args.Append(piece); state.FullArgs.Append(piece); }

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

    private static int? GetIntProp(JsonElement e, string prop)
        => e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;

    private sealed class ToolState { public string Id; public string ItemId = ""; public string Name; public StringBuilder Args = new(); public StringBuilder FullArgs = new(); public bool Started; public ToolState(string id, string name) { Id = id; Name = name; } }

    // ---- run (responses wire, PLAN §14b) -------------------------------------
    /// <summary>State accumulated across the /v1/responses SSE stream.</summary>
    private sealed class ResponseWireState
    {
        public bool TextStarted;
        public bool ThinkingStarted;
        public bool UsageSeen;
        public string ModelId = "";
        public Dictionary<string, ToolState> Tools = new();
        public List<MessagePart> Parts = new();
        public StringBuilder Text = new();
        public StringBuilder Think = new();
        /// <summary>PLAN §14c: the <c>response.id</c> of this run (from response.created/completed).</summary>
        public string? ResponseId;
        /// <summary>PLAN §14c: true once a response.failed event has been seen (do not advance the chain).</summary>
        public bool Failed;
    }

    private async IAsyncEnumerable<ModelEvent> RunResponsesAsync(ModelRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var (payload, covered) = BuildResponsesPayload(request);
        var key = string.IsNullOrEmpty(request.SessionId) ? null : ChainKey(request.SessionId, request.ModelId);
        using var content = new StringContent(JsonSerializer.Serialize(payload, WireOpts), System.Text.Encoding.UTF8, "application/json");

        // ResponseHeadersRead: must not wait for the full body (default
        // ResponseFinished would buffer the whole stream and kill streaming).
        using var resp = await _http.SendAsync(new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/v1/responses") { Content = content },
            System.Net.Http.HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(cancellationToken);
            yield return new ModelFailed(request.ModelId, $"HTTP {(int)resp.StatusCode}: {Truncate(err, 500)}");
            yield break;
        }

        var state = new ResponseWireState { ModelId = request.ModelId };
        yield return new ModelStarted(request.ModelId);

        await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        // CA2024: see the chat-completions path — EndOfStream blocks until the
        // whole response arrives; read until ReadLineAsync returns null so the
        // SSE stream is processed incrementally.
        string? line;
        while ((line = await reader.ReadLineAsync(cancellationToken)) is not null)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var data = line["data:".Length..].Trim();
            if (data.Length == 0 || data == "[DONE]") continue;

            IEnumerable<ModelEvent> evs;
            try { evs = ParseResponseEvent(data, state); }
            catch (JsonException) { continue; } // tolerate a torn/malformed chunk
            foreach (var ev in evs)
            {
                if (ev is UsageUpdated) state.UsageSeen = true;
                if (ev is TextDelta td) state.Text.Append(td.Text);
                else if (ev is ThinkingDelta tdt) state.Think.Append(tdt.Text);
                yield return ev;
            }
        }

        // ---- close out lanes (mirrors chat path) ----
        if (state.TextStarted) yield return new TextCompleted(TextBlockId);
        if (state.ThinkingStarted) yield return new ThinkingCompleted(ThinkingBlockId);
        if (!state.UsageSeen) yield return new UsageUpdated(0, 0, 0);

        // ---- assemble the completed assistant message ----
        if (state.Think.Length > 0) state.Parts.Add(new ThinkingPart(state.Think.ToString()));
        if (state.Text.Length > 0) state.Parts.Add(new TextPart(state.Text.ToString()));
        foreach (var tc in state.Tools.Values)
            if (tc.Started)
            {
                var argsJson = tc.FullArgs.Length > 0 ? tc.FullArgs.ToString().Trim() : string.Empty;
                JsonElement argsEl;
                try { argsEl = JsonDocument.Parse(argsJson).RootElement.Clone(); }
                catch { argsEl = JsonDocument.Parse("{}").RootElement.Clone(); }
                state.Parts.Add(new ToolCallPart(tc.Id, tc.Name, argsEl));
            }

        var message = new AgentMessage(Guid.NewGuid().ToString("n"), MessageRole.Assistant, state.Parts, DateTimeOffset.UtcNow);

        // PLAN §14c: advance the chain head only on a clean completion. The
        // completed assistant message the caller will append to the transcript
        // becomes the last covered item; failures leave the old head in place
        // (the transcript was not mutated, so it is still valid).
        if (key is not null && state.ResponseId is { } rid && state.Parts.Count > 0 && !state.Failed)
        {
            var newCovered = new List<string>(covered.Count + 1);
            newCovered.AddRange(covered);
            newCovered.Add(Fingerprint(message));
            _chainHeads[key] = new ChainHead(rid, newCovered);
        }

        yield return new ModelCompleted(message);
        yield break;
    }

    /// <summary>Parse one SSE <c>data:</c> event of the Responses wire into normalized events.</summary>
    private static IEnumerable<ModelEvent> ParseResponseEvent(string data, ResponseWireState s)
    {
        using var doc = JsonDocument.Parse(data);
        var root = doc.RootElement;
        var events = new List<ModelEvent>();
        if (!root.TryGetProperty("type", out var typeEl) || typeEl.ValueKind != JsonValueKind.String) return events;
        var type = typeEl.GetString()!;

        switch (type)
        {
            case "response.created" or "response.in_progress":
                // ModelStarted is emitted once by RunResponsesAsync; capture
                // the response.id (the chain head to advance, PLAN §14c).
                if (root.TryGetProperty("response", out var respIdEl) && respIdEl.ValueKind == JsonValueKind.Object
                    && respIdEl.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String)
                {
                    var rid = idEl.GetString();
                    if (rid is { Length: > 0 }) s.ResponseId = rid;
                }
                break;

            case "response.output_item.added":
                if (root.TryGetProperty("item", out var it) && it.ValueKind == JsonValueKind.Object
                    && it.TryGetProperty("type", out var itType) && itType.ValueKind == JsonValueKind.String)
                {
                    var itK = itType.GetString()!;
                    if (itK == "function_call")
                    {
                        var cid = it.TryGetProperty("call_id", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString()! : "";
                        var name = it.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() ?? "" : "";
                        var itemId = it.TryGetProperty("id", out var fi) && fi.ValueKind == JsonValueKind.String ? fi.GetString()! : "";
                        var ts = new ToolState(string.IsNullOrEmpty(cid) ? $"call_{s.Tools.Count}" : cid, name) { ItemId = itemId, Started = true };
                        s.Tools[ts.ItemId.Length > 0 ? ts.ItemId : ts.Id] = ts;
                        events.Add(new ToolCallStarted(ts.Id, name));
                    }
                    // reasoning / message items: their lanes open on the first
                    // *_text.delta (handled below); item.added itself is a marker.
                }
                break;

            case "response.reasoning_text.delta":
                if (root.TryGetProperty("delta", out var d) && d.ValueKind == JsonValueKind.String)
                {
                    var t = d.GetString() ?? "";
                    if (t.Length > 0)
                    {
                        if (!s.ThinkingStarted) { s.ThinkingStarted = true; events.Add(new ThinkingStarted(ThinkingBlockId)); }
                        events.Add(new ThinkingDelta(t));
                    }
                }
                break;

            case "response.output_text.delta":
                if (root.TryGetProperty("delta", out var d2) && d2.ValueKind == JsonValueKind.String)
                {
                    var t = d2.GetString() ?? "";
                    if (t.Length > 0)
                    {
                        if (!s.TextStarted) { s.TextStarted = true; events.Add(new TextStarted(TextBlockId)); }
                        events.Add(new TextDelta(t));
                    }
                }
                break;

            case "response.function_call_arguments.delta":
                if (root.TryGetProperty("delta", out var fd) && fd.ValueKind == JsonValueKind.String
                    && root.TryGetProperty("item_id", out var iid) && iid.ValueKind == JsonValueKind.String)
                {
                    // item_id is the fc_... item id (not the call_id) — our Tools map
                    // is keyed by that item id (see output_item.added above).
                    var piece = fd.GetString() ?? "";
                    if (piece.Length > 0 && s.Tools.TryGetValue(iid.GetString()!, out var tc))
                    {
                        tc.Args.Append(piece);
                        tc.FullArgs.Append(piece);
                        events.Add(new ToolCallArgumentsDelta(tc.Id, DrainArgs(tc)));
                    }
                }
                break;

            case "response.completed":
                if (root.TryGetProperty("response", out var r) && r.ValueKind == JsonValueKind.Object
                    && r.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
                {
                    int p = GetInt(usage, "input_tokens");
                    int c = GetInt(usage, "output_tokens");
                    int total = GetInt(usage, "total_tokens");
                    if (total == 0) total = p + c;
                    int cached = 0;
                    if (usage.TryGetProperty("input_tokens_details", out var details) && details.ValueKind == JsonValueKind.Object)
                        cached = GetInt(details, "cached_tokens");
                    events.Add(new UsageUpdated(p, c, total, cached));
                }
                break;

            case "response.failed":
                s.Failed = true; // PLAN §14c: a failed run never advances the chain
                var msg = "";
                if (root.TryGetProperty("response", out var rf) && rf.ValueKind == JsonValueKind.Object
                    && rf.TryGetProperty("error", out var e2) && e2.ValueKind == JsonValueKind.Object
                    && e2.TryGetProperty("message", out var em) && em.ValueKind == JsonValueKind.String) msg = em.GetString() ?? "";
                events.Add(new ModelFailed(s.ModelId, Truncate(string.IsNullOrEmpty(msg) ? "response.failed" : msg, 500)));
                break;

            default:
                // unknown / part-closure events (content_part.*, *_done, output_item.done) are ignored
                break;
        }
        return events;
    }

    /// <summary>
    /// Build the /v1/responses request body (PLAN §14b/§14c). Returns the
    /// payload plus the transcript prefix the stored chain covers (all
    /// fingerprints on a reset/full-input run, the head's covered list on a
    /// chained delta run).
    /// </summary>
    private (object Payload, List<string> Covered) BuildResponsesPayload(ModelRequest request)
    {
        var input = new List<object>();
        string? instructions = null;
        List<string> covered;
        var cur = MessageFingerprints(request.Messages);

        // PLAN §14c: chain when the head still covers a matching transcript
        // prefix: send previous_response_id plus only the delta beyond the
        // covered prefix (tool results, new user messages) — the server
        // appends the stored chain on top of the input, so covered items must
        // NOT be resent. Anything else (first run, compaction, equal-length
        // retry after failure) is a reset: full input, new head on success.
        var chainHead = !string.IsNullOrEmpty(request.SessionId) ? GetChainHead(request.SessionId, request.ModelId) : null;
        if (chainHead is { } head && cur.Count > head.Covered.Count
            && cur.Take(head.Covered.Count).SequenceEqual(head.Covered))
        {
            covered = head.Covered;
            for (var i = head.Covered.Count; i < request.Messages.Count; i++)
                BuildItemsForMessage(request.Messages[i], input, out _);
        }
        else
        {
            covered = cur;
            // instructions are always (re)sent; system messages carry no input items.
            instructions = request.Messages
                .Where(m => m.Role == MessageRole.System)
                .Select(m => string.Join("\n", m.Parts.OfType<TextPart>().Select(p => p.Text)))
                .FirstOrDefault(t => t.Length > 0);
            foreach (var m in request.Messages)
                if (m.Role != MessageRole.System)
                    BuildItemsForMessage(m, input, out _);
        }

        var payload = new Dictionary<string, object>
        {
            ["model"] = request.ModelId,
            ["stream"] = true,
            ["store"] = true,
            ["input"] = input,
        };
        if (chainHead is { } head2 && cur.Count > head2.Covered.Count
            && cur.Take(head2.Covered.Count).SequenceEqual(head2.Covered))
            payload["previous_response_id"] = head2.ResponseId;
        if (instructions is not null) payload["instructions"] = instructions;

        var tools = request.Tools.Count > 0
            ? request.Tools.Select(t => new Dictionary<string, object>
              {
                  ["type"] = "function", ["name"] = t.Name, ["description"] = t.Description, ["parameters"] = t.Parameters,
              }).ToList()
            : null;
        if (tools is not null) payload["tools"] = tools;
        if (request.Temperature is { } t) payload["temperature"] = t;
        if (request.MaxTokens is { } mt) payload["max_output_tokens"] = mt;
        var rl = request.ReasoningLevel;
        if (!string.IsNullOrEmpty(rl) && !string.Equals(rl, "off", StringComparison.OrdinalIgnoreCase))
            payload["reasoning"] = new Dictionary<string, object> { ["effort"] = rl };
        return (payload, covered);
    }

    /// <summary>Emit the /v1/responses input items for one transcript message.</summary>
    private static void BuildItemsForMessage(AgentMessage m, List<object> input, out string? instructions)
    {
        instructions = null;
        var text = string.Join("\n", m.Parts.OfType<TextPart>().Select(p => p.Text));
        var thinking = m.Parts.OfType<ThinkingPart>().FirstOrDefault();

        switch (m.Role)
        {
            case MessageRole.System:
                if (text.Length > 0) instructions = text;
                break;

            case MessageRole.User:
                if (text.Length > 0)
                    input.Add(new Dictionary<string, object>
                    {
                        ["type"] = "message", ["role"] = "user",
                        ["content"] = new[] { new Dictionary<string, object> { ["type"] = "input_text", ["text"] = text } },
                    });
                break;

            case MessageRole.Assistant:
                // Item order inside an assistant turn must be reasoning →
                // message content → function calls (server rejects otherwise).
                if (thinking is not null && thinking.Text.Length > 0)
                    input.Add(new Dictionary<string, object>
                    {
                        ["type"] = "reasoning",
                        ["content"] = new[] { new Dictionary<string, object> { ["type"] = "reasoning_text", ["text"] = thinking.Text } },
                    });
                if (text.Length > 0)
                    input.Add(new Dictionary<string, object>
                    {
                        ["type"] = "message", ["role"] = "assistant",
                        ["content"] = new[] { new Dictionary<string, object> { ["type"] = "output_text", ["text"] = text } },
                    });
                foreach (var c in m.Parts.OfType<ToolCallPart>())
                    input.Add(new Dictionary<string, object>
                    {
                        ["type"] = "function_call", ["call_id"] = c.Id,
                        ["name"] = c.Name, ["arguments"] = c.Arguments.GetRawText(),
                    });
                break;

            case MessageRole.Tool:
                // One function_call_output item per ToolResultPart (PLAN §14c):
                // a single tool message carries an entire result batch.
                foreach (var res in m.Parts.OfType<ToolResultPart>())
                    input.Add(new Dictionary<string, object>
                    {
                        ["type"] = "function_call_output", ["call_id"] = res.ToolCallId,
                        ["output"] = string.Join("\n", res.Parts.OfType<TextPart>().Select(p => p.Text)),
                    });
                break;
        }
    }

    /// <summary>
    /// Stable per-message fingerprint (PLAN §14c): role + message id + every
    /// part's kind and identifying payload. Message ids are stable for the
    /// lifetime of an in-memory transcript; compaction rebuilds with new ids,
    /// which is exactly the signal a chain head must reset on.
    /// </summary>
    private static string Fingerprint(AgentMessage m)
    {
        var b = new StringBuilder(m.Role.ToString());
        b.Append('\u0001').Append(m.Id);
        foreach (var p in m.Parts)
        {
            b.Append('\u0002').Append(p.Kind);
            switch (p)
            {
                case TextPart t: b.Append(t.Text); break;
                case ThinkingPart th: b.Append(th.Text); break;
                case ToolCallPart tc: b.Append(tc.Id); break;
                case ToolResultPart tr: b.Append(tr.ToolCallId); break;
                case ImagePart img: b.Append(img.MimeType); break;
            }
        }
        return b.ToString();
    }

    private static List<string> MessageFingerprints(IReadOnlyList<AgentMessage> messages)
        => messages.Select(Fingerprint).ToList();

    private ChainHead? GetChainHead(string sessionId, string modelId)
        => _chainHeads.TryGetValue(ChainKey(sessionId, modelId), out var head) ? head : null;

    private static string ChainKey(string sessionId, string modelId) => $"{sessionId}|{modelId}";


    // ---- payload building -------------------------------------------------
    private static readonly JsonSerializerOptions WireOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static object BuildPayload(ModelRequest request)
    {
        var messages = request.Messages.SelectMany(MessageToJson).ToList();
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
        // PLAN §13: data-driven reasoning passthrough — the level the user picked
        // in the composer (low/medium/high; "off"/null means no reasoning) is
        // forwarded to the provider.
        var rl = request.ReasoningLevel;
        if (!string.IsNullOrEmpty(rl) && !string.Equals(rl, "off", StringComparison.OrdinalIgnoreCase))
            payload["reasoning_effort"] = rl;
        return payload;
    }

    private static IEnumerable<Dictionary<string, object>> MessageToJson(AgentMessage m)
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
            // A single tool message can carry an entire result batch (PLAN §14c);
            // the chat wire needs one message per tool_call_id, so emit them all.
            var res = m.Parts.OfType<ToolResultPart>().ToList();
            if (res.Count == 0) yield break;
            foreach (var r in res)
            {
                yield return new Dictionary<string, object>
                {
                    ["role"] = "tool",
                    ["tool_call_id"] = r.ToolCallId,
                    ["content"] = string.Join("\n", r.Parts.OfType<TextPart>().Select(p => p.Text)),
                };
            }
        }
        else
        {
            yield return msg;
        }
    }

    /// <summary>
    /// True when a responses-wire pre-content failure means the referenced
    /// previous_response_id is no longer stored by the server — ninfer's 404
    /// code <c>response_not_found</c>, forwarded verbatim by AiProxy.
    /// </summary>
    private static bool IsStaleChainFailure(string reason) =>
        reason.Contains("response_not_found", StringComparison.Ordinal);

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}
