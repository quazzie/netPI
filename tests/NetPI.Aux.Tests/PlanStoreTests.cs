using System.Text.Json.Nodes;
using NetPI.Plan;

namespace NetPI.Aux.Tests;

/// <summary>The plan store's bookkeeping off the live runner: a deleted chat takes its plans with it.</summary>
public static class PlanStoreTests
{
    public static void Register(TestRunner r)
    {
        r.Add("plan: session.deleted names the chat as { id }, and its plans leave plan.list with it", async () =>
        {
            var ctx = new FakePluginContext(T.TempDir("plan-home"), "netpi.plan");
            await new PlanPlugin().StartAsync(ctx, CancellationToken.None);
            var session = ctx.SessionsFake.CreateSession(new SessionInfo { Title = "p" });
            var store = new PlanStore(ctx);
            var doc = store.Create(session);
            store.Revise(doc, PlanBody.FromJson(new JsonObject
            {
                ["title"] = "Add retry", ["summary"] = "Retry the upload.", ["steps"] = new JsonArray(new JsonObject { ["text"] = "Write the loop" }),
            }), "c1");
            store.Put(doc);
            async Task<int> Listed() => ((JsonArray)NetPiJson.ToNode(await ctx.Rpc.InvokeAsync("plan.list", new JsonObject { ["sessionId"] = session.Id }))!).Count;
            Check.Equal(1, await Listed(), "the plan is listed for its chat");

            ctx.SessionsFake.DeleteSession(session.Id);
            // as the host publishes it: the id in the payload, nothing on the envelope
            ctx.Bus.Publish(new BusEvent { Type = EventTypes.SessionDeleted, Data = new { id = session.Id }, Source = "host" });
            Check.Equal(0, await Listed(), "a deleted chat's plans go with it");
            ctx.Unload();
        });
    }
}
