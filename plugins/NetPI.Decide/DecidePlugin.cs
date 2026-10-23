using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using NetPI.Providers.Kit;

namespace NetPI.Decide;

/// <summary>
/// <c>decide</c>: typed decisions from a decision model through TypeSafe's <c>/v1/systemone</c> API behind AiGateway: Kev on the
/// nuc, or the NInfer chat model (qwen3.8-27b), which AiGateway answers through NInfer's <c>/v1/decision</c>.
/// The agent writes the questions once; the decision model answers them for one text or for every line of a file,
/// with a probability per answer, in about a tenth of a second per item. Also the RPC <c>decide.ask</c> for other plugins.
/// </summary>
[NetPiPlugin("netpi.decide", Name = "Decide", Description = "decide: yes/no, pick-one and score questions answered by a local decision model (the NInfer chat model, or Laya/Kev on the nuc)", Order = 27)]
public sealed class DecidePlugin : INetPiPlugin
{
    public Task StartAsync(IPluginContext context, CancellationToken ct)
    {
        context.Services.Register(new SettingsSection
        {
            Id = "decide", Title = "Decisions", Group = "Knowledge", Order = 50,
            Settings =
            [
                SettingInfo.Str("decide.baseUrl", "Server URL", "", "Empty: the AiProxy/AiGateway server (the first AiProxy provider).") with { Subgroup = "Server" },
                SettingInfo.Str("decide.model", "Decision model", "qwen3.8-27b", "qwen3.8-27b: the NInfer chat model, the best without training and no extra memory; it shares the 5090 with the agents. kev-9b: the nuc, load it from AiHub first (it takes the whole 4070). Several, comma-separated (for loadouts that change the model ids): the first one loaded is used.") with { Subgroup = "Server", Keywords = ["fallback", "loadout"] },
                SettingInfo.Int("decide.maxItems", "Most items per call", 500, null, 1, 5000) with { Subgroup = "Batches" },
                SettingInfo.Int("decide.parallel", "Requests at once", 4, null, 1, 16) with { Subgroup = "Batches" },
                SettingInfo.Str("decide.bulkModel", "Bulk decision model", "", "Explicit model for multi-item work; empty keeps the caller's choice. Does not load models.") with { Subgroup = "Batches" },
                SettingInfo.Int("decide.bulkThreshold", "Items before bulk routing", 8, null, 2, 5000) with { Subgroup = "Batches" },
                SettingInfo.Number("decide.groupSimilarity", "Share answers between near-identical items", DecideTool.DefaultGroupSimilarity,
                    "With embeddings, decide items at or above this cosine share one decision (a log line that differs only in a timestamp). Identical items always do. 0: only identical ones. Kept high: lines that differ in an error code must stay apart.", 0, 1) with { Subgroup = "Batches", Advanced = true },
                SettingInfo.Bool("decide.lane", "Short checks on the decision lane", true,
                    "A single short check (the guard's second opinion, the idea checks, loop hints) on a local model does not take an agent slot: NInfer answers decisions on its own lane ahead of agent work. Off: every decision waits for a slot like a chat. The decide tool always takes slots.") with { Subgroup = "Batches", Advanced = true },
            ],
        });
        // The kit's client: a bare HttpClient sent the loopback AiGateway through the system proxy.
        var http = context.Track(HttpFactory.Create());
        http.Timeout = TimeSpan.FromSeconds(120);
        var client = new DecisionClient(context, http);
        context.Services.Register<IDecisionService>(client);
        context.Tools.Register(new DecideTool(context, client));
        context.Rpc.Register(new RpcMethod("decide.ask", "Ask a decision model typed questions about one state: { state, questions, model? } → answers (TypeSafe shape)", ReadOnly: false, Params: [RpcParam.Req("questions", RpcParamType.Any), RpcParam.Req("state", RpcParamType.Any), RpcParam.Opt("model")]),
            async (r, rct) =>
        {
            var questions = r.Prop("questions") ?? throw new RpcException("bad_request", "Missing parameter 'questions'");
            var state = r.Prop("state") ?? throw new RpcException("bad_request", "Missing parameter 'state'");
            if (state.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) throw new RpcException("bad_request", "Missing parameter 'state'");
            // A malformed question is the caller's mistake, named before anything is sent (the same check the tool makes).
            try { DecideTool.NormalizeQuestions(questions); }
            catch (ArgumentException ex) { throw new RpcException("bad_request", ex.Message); }
            try
            {
                return await client.EvaluateAsync(new DecisionRequest { Model = r.Str("model"), Body = new JsonObject
                    { ["state"] = JsonNode.Parse(state.GetRawText()), ["questions"] = JsonNode.Parse(questions.GetRawText()) } }, rct).ConfigureAwait(false);
            }
            catch (DecisionException ex) { throw new RpcException(ex.Code, ex.Message); }
            catch (ArgumentException ex) { throw new RpcException("bad_request", ex.Message); }
        });
        context.Rpc.Register(new RpcMethod("decide.decision", "NInfer's /v1/decision through the same server: { messages, branches: [{ id, content, labels }], model?, share_state? } → { branches: [{ id, probabilities, mass }], usage, ms }; the messages are the shared state (NInfer caches them across requests)", ReadOnly: false, Params: [RpcParam.Req("messages", RpcParamType.Any), RpcParam.Req("branches", RpcParamType.Any), RpcParam.Opt("share_state", RpcParamType.Boolean), RpcParam.Opt("reasoning_effort"), RpcParam.Opt("model")]),
            async (r, rct) =>
        {
            var messages = r.Prop("messages") ?? throw new RpcException("bad_request", "Missing parameter 'messages'");
            var branches = r.Prop("branches") ?? throw new RpcException("bad_request", "Missing parameter 'branches'");
            var body = new JsonObject { ["messages"] = JsonNode.Parse(messages.GetRawText()), ["branches"] = JsonNode.Parse(branches.GetRawText()) };
            if (r.Bool("share_state") is { } share) body["share_state"] = share;
            if (r.Str("reasoning_effort") is { } effort) body["reasoning_effort"] = effort;
            try { return await client.EvaluateAsync(new DecisionRequest { Model = r.Str("model"), Body = body, Conversation = true }, rct).ConfigureAwait(false); }
            catch (DecisionException ex) { throw new RpcException(ex.Code, ex.Message); }
        });
        return Task.CompletedTask;
    }
}

internal sealed class DecisionException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>An answer plus what it cost: the server's <c>usage</c> lives beside <c>answers</c>, not inside it, so it
/// has to be carried out with the answer or the only evidence of a cached prefix is lost.</summary>
internal sealed record DecisionAnswer(JsonObject Answers, string Model, double Ms, JsonObject? Usage = null);

/// <summary>POST /v1/systemone through the configured server; errors keep the server's code and request id.</summary>
internal sealed class DecisionClient(IPluginContext ctx, HttpClient http) : IDecisionService
{
    private readonly Lock _limitGate = new();
    private SemaphoreSlim _requests = new(Math.Clamp(ctx.Settings.Get("decide.parallel", 4), 1, 16));
    private int _limit = Math.Clamp(ctx.Settings.Get("decide.parallel", 4), 1, 16);
    private readonly SemaphoreSlim _reuse = new(1, 1);

    /// <summary>The request gate for the current <c>decide.parallel</c>: read per use, a change takes effect on the next request.
    /// Requests already running finish against the gate they took.</summary>
    private SemaphoreSlim RequestGate()
    {
        lock (_limitGate)
        {
            var limit = Math.Clamp(ctx.Settings.Get("decide.parallel", 4), 1, 16);
            if (limit != _limit) { _requests = new SemaphoreSlim(limit); _limit = limit; }
            return _requests;
        }
    }

    public async Task<JsonObject> EvaluateAsync(DecisionRequest request, CancellationToken ct) =>
        (await EvaluateAnswerAsync(request, ct).ConfigureAwait(false)).Answers;

    /// <summary>The same call with the server's usage kept, for a caller that reports what a decision cost.</summary>
    public async Task<DecisionAnswer> EvaluateAnswerAsync(DecisionRequest request, CancellationToken ct)
    {
        var id = await ResolveAsync(request.Model, ct).ConfigureAwait(false);
        var model = await ctx.Models.FindAsync(id, ct).ConfigureAwait(false);
        var wireModel = model?.Id ?? id;
        // A short check on a local model goes on NInfer's decision lane: no agent slot, and no serializing behind the
        // other decisions on the same model (they are queued and answered first by the server itself).
        var lane = request.Lane && model is { IsLocal: true } && ctx.Settings.GetOr("decide.lane", true);
        // A decision on the caller's live model reuses admission and serializes; it cannot take its own slot twice.
        var reuse = !lane && request.ExistingLease is { IsReleased: false } && model is not null
            && string.Equals(request.HeldModel, model.Ref, StringComparison.OrdinalIgnoreCase);
        IDisposable? slot = null;
        if (reuse) await _reuse.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!reuse && !lane && model is not null)
            {
                var scheduler = ctx.Services.Get<IAgentScheduler>();
                var admission = new AgentSlotRequest
                {
                    Key = scheduler?.Resolve(model) ?? model.Ref, AgentId = "decide", Provider = model.Provider, Priority = request.Priority, Label = "decision check",
                };
                slot = scheduler is not null ? await scheduler.AcquireAsync(admission, ct).ConfigureAwait(false)
                    : await ResourceLeaseSlot.AcquireAsync(ctx.Services, ctx.Settings, model, admission, ct).ConfigureAwait(false);
            }
            var gate = RequestGate();
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var body = (JsonObject)request.Body.DeepClone();
                body.Remove("model");
                if (request.Conversation)
                {
                    if (request.ReasoningEffort is not null) body["reasoning_effort"] = request.ReasoningEffort;
                    var answer = await DecisionAsync(wireModel, body, ct).ConfigureAwait(false);
                    return new DecisionAnswer(answer, wireModel, 0, answer["usage"] as JsonObject);
                }
                var state = body["state"] ?? throw new DecisionException("bad_request", "Missing state");
                var questions = body["questions"] ?? throw new DecisionException("bad_request", "Missing questions");
                return await AskAsync(wireModel, NetPiJson.ToElement(state), DecideTool.NormalizeQuestions(NetPiJson.ToElement(questions)), ct).ConfigureAwait(false);
            }
            finally { gate.Release(); }
        }
        finally { slot?.Dispose(); if (reuse) _reuse.Release(); }
    }
    /// <summary>The model asked for, else <c>decide.model</c>; of a list (<c>"qwen3.8-27b, kev-9b"</c>) the first one.</summary>
    public string Model(string? model) => Candidates(model)[0];

    /// <summary>
    /// The model asked for, else <c>decide.model</c> (the second opinion passes <c>guardrails.secondOpinionModel</c>). Either may
    /// name several, comma-separated, because a loadout switch changes which one the server has: the first the model list
    /// knows and has loaded (or reachable) is used, else the first named (the server's own error then says why).
    /// </summary>
    public async Task<string> ResolveAsync(string? model, CancellationToken ct)
    {
        var candidates = Candidates(model);
        if (candidates.Length == 1) return candidates[0];
        foreach (var c in candidates)
        {
            ModelInfo? info = null;
            try { info = await ctx.Models.FindAsync(c, ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException) { ctx.Logger.LogDebug(ex, "Decide: looking up {Model} failed", c); }
            if (info is { Status: null or "loaded" or "available" }) return c;
        }
        return candidates[0];
    }

    private string[] Candidates(string? model)
    {
        var text = string.IsNullOrWhiteSpace(model) ? ctx.Settings.Get("decide.model", "qwen3.8-27b") ?? "qwen3.8-27b" : model;
        var list = text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return list.Length > 0 ? list : ["qwen3.8-27b"];
    }

    public string Root()
    {
        var url = ctx.Settings.Get("decide.baseUrl", "");
        if (string.IsNullOrWhiteSpace(url)) url = ProviderInstances.FirstString(ctx.Settings, "aiproxy", "baseUrl");
        url = (url ?? "http://127.0.0.1:8090").Trim().TrimEnd('/');
        return url.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ? url[..^3] : url;
    }

    public async Task<DecisionAnswer> AskAsync(string? model, JsonElement state, JsonObject questions, CancellationToken ct)
    {
        var id = Model(model);
        var body = new JsonObject { ["model"] = id, ["state"] = JsonNode.Parse(state.GetRawText()), ["questions"] = questions.DeepClone() };
        var sw = Stopwatch.StartNew();
        var (json, text) = await PostAsync("/v1/systemone", id, body, ct).ConfigureAwait(false);
        var answers = (json?["answers"] ?? json) as JsonObject ?? throw new DecisionException("bad_response", $"{id} returned no answers: {Trim(text, 200)}");
        return new DecisionAnswer((JsonObject)answers.DeepClone(), id, sw.Elapsed.TotalMilliseconds, json?["usage"] as JsonObject);
    }

    /// <summary>
    /// NInfer's <c>/v1/decision</c> as it is (thinking off): the <c>messages</c> are the shared state, each branch one
    /// question with its labels. Returns the server's answer plus <c>ms</c>.
    /// </summary>
    public async Task<JsonObject> DecisionAsync(string? model, JsonObject body, CancellationToken ct)
    {
        var id = Model(model);
        body["model"] = id;
        body["enable_thinking"] ??= false;
        var sw = Stopwatch.StartNew();
        var (json, text) = await PostAsync("/v1/decision", id, body, ct).ConfigureAwait(false);
        if (json is not JsonObject answer || answer["branches"] is not JsonArray)
            throw new DecisionException("bad_response", $"{id} returned no branches: {Trim(text, 200)}");
        answer = (JsonObject)answer.DeepClone();
        answer["ms"] = Math.Round(sw.Elapsed.TotalMilliseconds);
        return answer;
    }

    private async Task<(JsonNode? Json, string Text)> PostAsync(string path, string id, JsonObject body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, Root() + path)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, new MediaTypeHeaderValue("application/json")),
        };
        req.Headers.TryAddWithoutValidation("User-Agent", "NetPI-decide");
        HttpResponseMessage res;
        try { res = await http.SendAsync(req, ct).ConfigureAwait(false); }
        catch (HttpRequestException ex) { throw new DecisionException("unreachable", $"Cannot reach {Root()}: {ex.Message}"); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { throw new DecisionException("timeout", $"{id} did not answer within {http.Timeout.TotalSeconds:0} s"); }
        using (res)
        {
            var text = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            JsonNode? json = null;
            try { json = JsonNode.Parse(text); } catch (JsonException) { }
            if (!res.IsSuccessStatusCode)
            {
                var err = json?["error"];
                var code = CodeOf((err as JsonObject)?["code"]) ?? CodeOf(json?["code"]) ?? $"http_{(int)res.StatusCode}";
                var msg = (err as JsonObject)?["message"]?.GetValue<string>() ?? (err is JsonValue v ? v.ToString() : null) ?? text;
                if (msg.Length > 400) msg = msg[..400] + "…";
                var rid = res.Headers.TryGetValues("X-Request-Id", out var ids) ? ids.FirstOrDefault() : null;
                var hint = code == "model_not_loaded" ? $" Load {id} from AiHub (tray or dashboard)." : "";
                throw new DecisionException(code, $"{id}: HTTP {(int)res.StatusCode} {code}: {msg}{(rid is null ? "" : $" (request {rid})")}.{hint}");
            }
            return (json, text);
        }
    }

    // OpenAI servers send a string code; llama.cpp sends the HTTP status as a number.
    private static string? CodeOf(JsonNode? node) => node switch
    {
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonValue v when v.TryGetValue<int>(out var n) => $"http_{n}",
        _ => null,
    };

    private static string Trim(string s, int n) => s.Length > n ? s[..n] + "…" : s;
}
