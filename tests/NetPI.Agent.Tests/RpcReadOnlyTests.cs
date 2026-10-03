using NetPI.Agents;
using NetPI.AgentsMd;
using NetPI.Ask;
using NetPI.Context;
using NetPI.Goal;
using NetPI.Guardrails;
using NetPI.Plan;
using NetPI.Profiles;
using NetPI.Skills;

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
        t.Add("rpc: the goal, ask, plan and budget read surfaces are read-only; the ones that change the app are not", async () =>
        {
            await using var h = await TestHost.StartAsync();
            var ctx = await h.StartPluginAsync(new GoalPlugin());
            await h.StartPluginAsync(new AskPlugin());
            await h.StartPluginAsync(new AgentsPlugin());
            await h.StartPluginAsync(new PlanPlugin());
            var flags = ctx.Rpc.List().ToDictionary(m => m.Method, m => m.ReadOnly);

            foreach (var m in new[] { "goal.get", "ask.pending", "agents.list", "usage.summary", "usage.session", "budget.status", "plan.get", "plan.list", "plan.offers", "usage.history", "usage.chats" })
                Check.True(flags.TryGetValue(m, out var ro) && ro, $"{m} only reads, so it is marked read-only");
            foreach (var m in new[] { "goal.set", "goal.edit", "goal.pause", "goal.resume", "goal.clear", "ask.answer", "agents.use", "agents.setEnabled", "budget.allow", "plan.enter", "plan.exit", "plan.answer", "plan.enterAnswer", "plan.command" })
                Check.True(flags.TryGetValue(m, out var rw) && !rw, $"{m} changes the app, so it stays unmarked");
        });

        t.Add("rpc: the run, guard and prompt read surfaces are read-only; the ones that change a run are not", async () =>
        {
            await using var h = await TestHost.StartAsync(plugins: TestHost.Plugins.None);
            var ctx = await h.StartPluginAsync(new RuntimePlugin());
            await h.StartPluginAsync(new GuardrailsPlugin());
            await h.StartPluginAsync(new ContextPlugin());
            await h.StartPluginAsync(new AgentsMdPlugin());
            await h.StartPluginAsync(new ProfilesPlugin());
            await h.StartPluginAsync(new SkillsPlugin());
            var all = ctx.Rpc.List();
            var flags = all.ToDictionary(m => m.Method, m => m.ReadOnly);

            foreach (var m in new[] { "agent.queue", "runs.list", "agent.get", "agent.tools", "guard.pending", "context.preview",
                                      "context.prompts", "context.toolsets", "agentsmd.list", "profiles.list", "skills.list" })
                Check.True(flags.TryGetValue(m, out var ro) && ro, $"{m} only reads, so it is marked read-only");

            // Every method these plugins register is listed here, split by the flag it has to carry: a new one
            // cannot slip in unmarked (a read surface no tool can reach, or a write one every tool may call).
            var expected = new Dictionary<string, (string[] ReadOnly, string[] Writable)>
            {
                ["netpi.runtime"] = (["agent.queue", "runs.list", "agent.get", "agent.tools"], ["agent.send", "agent.abort", "agent.dequeue", "agent.setTools"]),
                ["netpi.guardrails"] = (["guard.pending", "guard.outcomes"], ["guard.answer"]),
                ["netpi.context"] = (["context.preview", "context.prompts", "context.toolsets"], ["context.reset"]),
                ["netpi.agentsmd"] = (["agentsmd.list"], []),
                ["netpi.profiles"] = (["profiles.list"], ["profiles.apply"]),
                ["netpi.skills"] = (["skills.list"], []),
            };
            foreach (var (pluginId, (readOnly, writable)) in expected)
            {
                var mine = all.Where(m => m.PluginId == pluginId).ToArray();
                Check.Equal(string.Join(" ", readOnly.Order(StringComparer.Ordinal)), string.Join(" ", mine.Where(m => m.ReadOnly).Select(m => m.Method).Order(StringComparer.Ordinal)),
                    $"{pluginId}: the methods that only read");
                Check.Equal(string.Join(" ", writable.Order(StringComparer.Ordinal)), string.Join(" ", mine.Where(m => !m.ReadOnly).Select(m => m.Method).Order(StringComparer.Ordinal)),
                    $"{pluginId}: the methods that change something");
            }
        });
    }
}
