using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

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
            Id = "decide", Title = "Decisions", Group = "Tools", Order = 45,
            Settings =
            [
                SettingInfo.Str("decide.baseUrl", "Server URL", "", "Empty: the AiProxy/AiGateway server (providers.aiproxy.baseUrl)."),
                SettingInfo.Str("decide.model", "Decision model", "qwen3.8-27b", "qwen3.8-27b: the NInfer chat model, the best without training and no extra memory; it shares the 5090 with the agents. laya-logs: the four log questions on the nuc. kev-9b: the nuc, load it from AiHub first (it takes the whole 4070)."),
                SettingInfo.Int("decide.maxItems", "Most items per call", 500, null, 1, 5000),
                SettingInfo.Int("decide.parallel", "Requests at once", 4, null, 1, 16),
                SettingInfo.Str("decide.bulkModel", "Bulk decision model", "", "Explicit model for multi-item work; empty keeps the caller's choice. Does not load models."),
                SettingInfo.Int("decide.bulkThreshold", "Items before bulk routing", 8, null, 2, 5000),
            ],
        });
        var http = context.Track(new HttpClient { Timeout = TimeSpan.FromSeconds(120) });
        var client = new DecisionClient(context, http);
        context.Services.Register<IDecisionService>(client);
        context.Tools.Register(new DecideTool(context, client));
        context.Rpc.Register("decide.ask", async (r, rct) =>
        {
            var questions = r.Prop("questions") ?? throw new RpcException("bad_request", "Missing parameter 'questions'");
            var state = r.Prop("state") ?? throw new RpcException("bad_request", "Missing parameter 'state'");
            try
            {
                return await client.EvaluateAsync(new DecisionRequest { Model = r.Str("model"), Body = new JsonObject
                    { ["state"] = JsonNode.Parse(state.GetRawText()), ["questions"] = JsonNode.Parse(questions.GetRawText()) } }, rct).ConfigureAwait(false);
            }
            catch (DecisionException ex) { throw new RpcException(ex.Code, ex.Message); }
        }, "Ask a decision model typed questions about one state: { state, questions, model? } → answers (TypeSafe shape)");
        context.Rpc.Register("decide.decision", async (r, rct) =>
        {
            var messages = r.Prop("messages") ?? throw new RpcException("bad_request", "Missing parameter 'messages'");
            var branches = r.Prop("branches") ?? throw new RpcException("bad_request", "Missing parameter 'branches'");
            var body = new JsonObject { ["messages"] = JsonNode.Parse(messages.GetRawText()), ["branches"] = JsonNode.Parse(branches.GetRawText()) };
            if (r.Bool("share_state") is { } share) body["share_state"] = share;
            if (r.Str("reasoning_effort") is { } effort) body["reasoning_effort"] = effort;
            try { return await client.EvaluateAsync(new DecisionRequest { Model = r.Str("model"), Body = body, Conversation = true }, rct).ConfigureAwait(false); }
            catch (DecisionException ex) { throw new RpcException(ex.Code, ex.Message); }
        }, "NInfer's /v1/decision through the same server: { messages, branches: [{ id, content, labels }], model?, share_state? } → { branches: [{ id, probabilities, mass }], usage, ms }; the messages are the shared state (NInfer caches them across requests)");
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
    private readonly SemaphoreSlim _requests = new(Math.Clamp(ctx.Settings.Get("decide.parallel", 4), 1, 16));
    private readonly SemaphoreSlim _reuse = new(1, 1);

    public async Task<JsonObject> EvaluateAsync(DecisionRequest request, CancellationToken ct) =>
        (await EvaluateAnswerAsync(request, ct).ConfigureAwait(false)).Answers;

    /// <summary>The same call with the server's usage kept, for a caller that reports what a decision cost.</summary>
    public async Task<DecisionAnswer> EvaluateAnswerAsync(DecisionRequest request, CancellationToken ct)
    {
        var id = Model(request.Model);
        var model = await ctx.Models.FindAsync(id, ct).ConfigureAwait(false);
        var wireModel = model?.Id ?? id;
        // A decision on the caller's live model reuses admission and serializes; it cannot take its own slot twice.
        var reuse = request.ExistingLease is { IsReleased: false } && model is not null
            && string.Equals(request.HeldModel, model.Ref, StringComparison.OrdinalIgnoreCase);
        IDisposable? slot = null;
        if (reuse) await _reuse.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!reuse && model is not null)
            {
                var scheduler = ctx.Services.Get<IAgentScheduler>();
                var admission = new AgentSlotRequest
                {
                    Key = scheduler?.Resolve(model) ?? model.Ref, AgentId = "decide", Provider = model.Provider, Priority = request.Priority, Label = "decision check",
                };
                slot = scheduler is not null ? await scheduler.AcquireAsync(admission, ct).ConfigureAwait(false)
                    : await ResourceLeaseSlot.AcquireAsync(ctx.Services, ctx.Settings, model, admission, ct).ConfigureAwait(false);
            }
            await _requests.WaitAsync(ct).ConfigureAwait(false);
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
            finally { _requests.Release(); }
        }
        finally { slot?.Dispose(); if (reuse) _reuse.Release(); }
    }
    public string Model(string? model) => string.IsNullOrWhiteSpace(model) ? ctx.Settings.Get("decide.model", "qwen3.8-27b") ?? "qwen3.8-27b" : model.Trim();

    public string Root()
    {
        var url = ctx.Settings.Get("decide.baseUrl", "");
        if (string.IsNullOrWhiteSpace(url)) url = ctx.Settings.Get("providers.aiproxy.baseUrl", "http://127.0.0.1:8090");
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
