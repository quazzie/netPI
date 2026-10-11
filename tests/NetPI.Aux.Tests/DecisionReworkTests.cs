using System.Text.Json.Nodes;

namespace NetPI.Aux.Tests;

public static class DecisionReworkTests
{
    public static void Register(TestRunner r)
    {
        r.Add("decision rework: near ties and malformed probabilities never authorize a change", () =>
        {
            Check.False(DecisionConfidence.Clear(0.51, 0.49, 0.5));
            Check.False(DecisionConfidence.Clear(double.NaN, 0, 0.8));
            Check.False(DecisionConfidence.Clear(1.2, 0, 0.8));
            Check.True(DecisionConfidence.Clear(0.95, 0.05, 0.8));
        });
        r.Add("decision rework: typed capability accepts a live lease without going through a peer RPC", async () =>
        {
            var ctx = new FakePluginContext();
            var replacement = new Replacement(); ctx.ServicesFake.Register<IDecisionService>(replacement);
            var result = await DecisionCapabilities.InvokeAsync(ctx.Services, ctx.Rpc, "decide.decision", new JsonObject { ["model"] = "custom" }, CancellationToken.None);
            Check.True(DecisionCapabilities.Available(ctx.Services, ctx.Rpc, "decide.decision"));
            Check.Equal("custom", replacement.Request!.Model);
            Check.True(replacement.Request.Conversation);
            Check.Equal(0.95, (double)result!["branches"]![0]!["probabilities"]!["YES"]!);
        });
    }
    private sealed class Replacement : IDecisionService
    {
        public DecisionRequest? Request;
        public Task<JsonObject> EvaluateAsync(DecisionRequest request, CancellationToken ct)
        {
            Request = request;
            return Task.FromResult(new JsonObject { ["branches"] = new JsonArray(new JsonObject
                { ["id"] = "check", ["probabilities"] = new JsonObject { ["YES"] = 0.95, ["NO"] = 0.05 } }) });
        }
    }
}
