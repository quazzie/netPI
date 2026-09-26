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
[NetPiPlugin("netpi.decide", Name = "Decide", Description = "decide: yes/no, pick-one and score questions answered by a local decision model (Kev or the NInfer chat model)", Order = 27)]
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
                SettingInfo.Str("decide.model", "Decision model", "kev-9b", "kev-9b: load it on the nuc from AiHub first. qwen3.8-27b: the NInfer chat model, no extra memory, but it shares NInfer's slots with the agents."),
                SettingInfo.Int("decide.maxItems", "Most items per call", 500, null, 1, 5000),
                SettingInfo.Int("decide.parallel", "Requests at once", 4, null, 1, 16),
            ],
        });
        var http = context.Track(new HttpClient { Timeout = TimeSpan.FromSeconds(120) });
        var client = new DecisionClient(context, http);
        context.Tools.Register(new DecideTool(context, client));
        context.Rpc.Register("decide.ask", async (r, rct) =>
        {
            var questions = r.Prop("questions") ?? throw new RpcException("bad_request", "Missing parameter 'questions'");
            var state = r.Prop("state") ?? throw new RpcException("bad_request", "Missing parameter 'state'");
            try
            {
                var answer = await client.AskAsync(r.Str("model"), state, DecideTool.NormalizeQuestions(questions), rct).ConfigureAwait(false);
                return answer.Answers;
            }
            catch (DecisionException ex) { throw new RpcException(ex.Code, ex.Message); }
        }, "Ask a decision model typed questions about one state: { state, questions, model? } → answers (TypeSafe shape)");
        return Task.CompletedTask;
    }
}

internal sealed class DecisionException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

internal sealed record DecisionAnswer(JsonObject Answers, string Model, double Ms);

/// <summary>POST /v1/systemone through the configured server; errors keep the server's code and request id.</summary>
internal sealed class DecisionClient(IPluginContext ctx, HttpClient http)
{
    public string Model(string? model) => string.IsNullOrWhiteSpace(model) ? ctx.Settings.Get("decide.model", "kev-9b") ?? "kev-9b" : model.Trim();

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
        using var req = new HttpRequestMessage(HttpMethod.Post, Root() + "/v1/systemone")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, new MediaTypeHeaderValue("application/json")),
        };
        req.Headers.TryAddWithoutValidation("User-Agent", "NetPI-decide");
        var sw = Stopwatch.StartNew();
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
            var answers = (json?["answers"] ?? json) as JsonObject ?? throw new DecisionException("bad_response", $"{id} returned no answers: {Trim(text, 200)}");
            return new DecisionAnswer((JsonObject)answers.DeepClone(), id, sw.Elapsed.TotalMilliseconds);
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
