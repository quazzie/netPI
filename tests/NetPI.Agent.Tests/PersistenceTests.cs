using System.Text.Json.Nodes;
using NetPI.Agents;

namespace NetPI.Agent.Tests;

public static class PersistenceTests
{
    public static void Register(TestRunner t)
    {
        t.Add("persistence: agent records survive a restart", AgentRecords);
        t.Add("persistence: usage survives a restart", UsageRecords);
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
