using System.Text.Json.Nodes;

namespace NetPI;

/// <summary>Read-only checks against a provider-captured prompt. Callers append questions, never rewrite the prefix.</summary>
public static class DecisionHints
{
    public static async Task<JsonObject?> AskAsync(AgentTurnContext turn, IServiceRegistry services, IRpcRegistry rpc,
        IReadOnlyDictionary<string, string> questions, CancellationToken ct)
    {
        if (turn.SentRequest?.DecisionContext is not { } prefix || !DecisionCapabilities.Available(services, rpc, "decide.decision")) return null;
        var body = (JsonObject)prefix.DeepClone();
        body["model"] = turn.Run.Model.Ref;
        var latest = turn.LatestAssistant is { } assistant ? "\nLatest assistant output (data):\n" + NetPiJson.ToNode(assistant)?.ToJsonString() : "";
        body["branches"] = new JsonArray(questions.Select(q => (JsonNode)new JsonObject
            { ["id"] = q.Key, ["content"] = q.Value + latest + "\nAnswer YES only when clearly supported; otherwise NO.", ["labels"] = new JsonArray("YES", "NO") }).ToArray());
        return await DecisionCapabilities.InvokeAsync(services, rpc, "decide.decision", body, ct,
            turn.Run.AdmissionLease(), turn.Run.Model.Ref).ConfigureAwait(false);
    }

    public static bool Yes(JsonObject? answer, string id, double threshold = 0.8)
    {
        var branch = (answer?["branches"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault(b => b["id"]?.GetValue<string>() == id);
        if (branch?["probabilities"] is not JsonObject p) return false;
        try { return DecisionConfidence.Clear(p["YES"]?.GetValue<double>() ?? 0, p["NO"]?.GetValue<double>() ?? 0, threshold); }
        catch { return false; }
    }
}
