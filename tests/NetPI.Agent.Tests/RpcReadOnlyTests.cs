using NetPI.Agents;
using NetPI.Ask;
using NetPI.Goal;

namespace NetPI.Agent.Tests;

/// <summary>
/// The read-only paths (<c>scripts/netpi.mjs</c>, the <c>diag</c> tool's rpc action) call only what
/// <c>rpc.list</c> marks <c>readOnly</c>. A read surface that is not marked is one no tool can reach
/// (idea-o934y1) — the goal, the waiting questions and the budget are all things an agent is asked about.
/// </summary>
public static class RpcReadOnlyTests
{
    public static void Register(TestRunner t)
    {
        t.Add("rpc: the goal, ask and budget read surfaces are read-only; the ones that change the app are not", async () =>
        {
            await using var h = await TestHost.StartAsync();
            var ctx = await h.StartPluginAsync(new GoalPlugin());
            await h.StartPluginAsync(new AskPlugin());
            await h.StartPluginAsync(new AgentsPlugin());
            var flags = ctx.Rpc.List().ToDictionary(m => m.Method, m => m.ReadOnly);

            foreach (var m in new[] { "goal.get", "ask.pending", "agents.list", "usage.summary", "usage.session", "budget.status" })
                Check.True(flags.TryGetValue(m, out var ro) && ro, $"{m} only reads, so it is marked read-only");
            foreach (var m in new[] { "goal.set", "goal.edit", "goal.pause", "goal.resume", "goal.clear", "ask.answer", "agents.use", "agents.setEnabled", "budget.allow" })
                Check.True(flags.TryGetValue(m, out var rw) && !rw, $"{m} changes the app, so it stays unmarked");
        });
    }
}
