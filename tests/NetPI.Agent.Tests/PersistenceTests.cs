using System.Text.Json.Nodes;
using NetPI.Agents;

namespace NetPI.Agent.Tests;

public static class PersistenceTests
{
    public static void Register(TestRunner t)
    {
        t.Add("persistence: agent records survive a restart", AgentRecords);
        t.Add("persistence: usage survives a restart", UsageRecords);
        t.Add("persistence: queued inputs survive a runtime reload", QueuedInputsSurviveReload);
    }

    /// <summary>The runtime plugin's agent records, the shape the store declares (a test that leaves a row behind).</summary>
    private static IDataCollection Records(TestHost h) =>
        h.Storage.Plugins.For("netpi.runtime").Collection("agent_records",
            new CollectionSpec().Text("sessionId").Text("createdAt").Text("status"));

    private static async Task AgentRecords()
    {
        await using var h = await TestHost.StartAsync();
        h.Catalog.Handler = (r, ct) => Reply.Text(r.SystemPrompt!.Contains("subagent") ? "sub report" : "main reply");
        var s = h.NewSession();
        await h.SendAsync(s.Id, "hello");
        var main = await h.IdleAsync(s.Id);
        var sub = await h.Runtime.SpawnAsync(new SpawnRequest { Task = "do it", Name = "persisted", ParentAgentId = main.Id, Instructions = "custom rule" });
        await h.StatusAsync(sub.Id, AgentStatus.Completed);
        await h.IdleAsync(s.Id); // woken by the result

        // a record left behind by a crashed process
        Records(h).Put("agt_stale", new JsonObject
        {
            ["id"] = "agt_stale",
            ["sessionId"] = "ses_stale",
            ["name"] = "ghost",
            ["status"] = "running",
            ["createdAt"] = "2026-01-01T00:00:00.0000000Z",
        });

        await h.StopPluginAsync("netpi.runtime");
        await h.StopPluginAsync("netpi.tools.agents");
        await h.StartPluginAsync(new RuntimePlugin());
        await h.StartPluginAsync(new NetPI.Tools.Agents.AgentToolsPlugin());

        var rt = h.Runtime;
        var subAfter = rt.Get(sub.Id)!;
        Check.Equal(AgentStatus.Completed, subAfter.Status);
        Check.Equal("sub report", subAfter.Result);
        Check.Equal("persisted", subAfter.Name);
        Check.Equal(main.Id, subAfter.ParentAgentId);
        Check.True(subAfter.IsSubagent);
        Check.Equal(1, subAfter.Depth);
        Check.Equal("do it", subAfter.Task);
        var mainAfter = rt.GetBySession(s.Id)!;
        Check.Equal(main.Id, mainAfter.Id, "the session keeps its agent");
        Check.Equal(2, mainAfter.Runs);
        Check.True(mainAfter.Children.Contains(sub.Id));
        var stale = rt.Get("agt_stale")!;
        Check.Equal(AgentStatus.Failed, stale.Status);
        Check.Equal("interrupted", stale.Error);
        Check.Equal("failed", Records(h).Get("agt_stale")!["status"]?.GetValue<string>());

        // the reloaded runtime continues the same agent
        await h.SendAsync(s.Id, "again");
        var again = await h.IdleAsync(s.Id);
        Check.Equal(main.Id, again.Id);
        Check.Equal(3, again.Runs);
        // a restored subagent keeps its role instructions
        await h.Runtime.MessageAsync(main.Id, sub.Id, "one more thing");
        await h.StatusAsync(sub.Id, AgentStatus.Completed);
        await Wait.Until(() => h.Catalog.Requests.Count(r => r.SessionId == sub.SessionId) == 2, "sub ran again");
        Check.Contains(h.Catalog.Requests.Last(r => r.SessionId == sub.SessionId).SystemPrompt, "custom rule");
        await h.IdleAsync(s.Id);
    }

    /// <summary>A follow-up and a steer offered during a run are persisted with the agent and survive a reload of the runtime.</summary>
    private static async Task QueuedInputsSurviveReload()
    {
        await using var h = await TestHost.StartAsync();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Catalog.Handler = (r, ct) =>
        {
            if (Reply.HasToolResult(r)) return Reply.Text("done");
            return Reply.Text("working", c => gate.Task.WaitAsync(c)); // hold the run open so there is a queue to fill
        };
        var s = h.NewSession();
        await h.SendAsync(s.Id, "start");
        await Wait.Until(() => h.Runtime.GetBySession(s.Id)?.Status == AgentStatus.Running, "the run is in flight");

        // A follow-up and a steer land in the queue while the run holds the slot.
        await h.SendAsync(s.Id, "follow up please", DeliveryMode.Queue);
        await h.SendAsync(s.Id, "steer me left", DeliveryMode.Steer);
        Check.Equal(2, h.Runtime.GetQueue(s.Id).Count, "both inputs are queued before the reload");

        // The reload: the run is aborted, and a new instance rebuilds from the records (queue included).
        await h.StopPluginAsync("netpi.runtime");
        await h.StopPluginAsync("netpi.tools.agents");
        await h.StartPluginAsync(new RuntimePlugin());
        await h.StartPluginAsync(new NetPI.Tools.Agents.AgentToolsPlugin());

        var queue = h.Runtime.GetQueue(s.Id);
        Check.Equal(2, queue.Count, "the queue survived the reload");
        Check.Equal(1, queue.Count(q => q.Mode == "steer" && q.Text == "steer me left"), "the steer kept its mode and text");
        Check.Equal(1, queue.Count(q => q.Mode == "queue" && q.Text == "follow up please"), "the follow-up kept its mode and text");
        Check.Equal(2, h.Runtime.GetBySession(s.Id)!.QueuedMessages, "the count is honest after the reload");

        // The inputs are not just remembered: the agent can still take them (the UI's chip send is agent.dequeue = RemoveQueued).
        var took = h.Runtime.RemoveQueued(s.Id, queue.Single(q => q.Mode == "queue").Id);
        Check.True(took, "the surviving follow-up can be sent from the queue");
        var rest = h.Runtime.GetQueue(s.Id);
        Check.Equal(1, rest.Count, "only the steer is left");
        Check.Equal("steer me left", rest[0].Text);
    }

    private static async Task UsageRecords()
    {
        await using var h = await TestHost.StartAsync();
        var s = h.NewSession();
        await h.SendAsync(s.Id, "one");
        await h.IdleAsync(s.Id);
        await h.SendAsync(s.Id, "two");
        await h.IdleAsync(s.Id);
        await h.Bus.DrainAsync();
        var lanes = h.Storage.Plugins.For("netpi.agents").Collection("lanes_usage",
            new CollectionSpec().Text("day").Text("provider").Text("model").Integer("budgetTokens"));
        Check.Equal(2L, lanes.Find(new DataQuery().Eq("provider", "fake").Eq("model", "local"))
            .Sum(d => d.Doc["calls"]!.GetValue<long>()));

        await h.StopPluginAsync("netpi.agents");
        await h.StartPluginAsync(new AgentsPlugin());
        var summary = (await h.Rpc.CallAsync("usage.summary"))!;
        var fake = ((JsonArray)summary["providers"]!).Single(p => (string?)p!["provider"] == "fake")!;
        Check.Equal(200L, fake["inputTokens"]!.GetValue<long>());
        Check.Equal(2L, fake["calls"]!.GetValue<long>());
    }
}
