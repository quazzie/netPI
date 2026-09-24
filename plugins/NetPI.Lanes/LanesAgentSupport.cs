using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NetPI.Lanes;

/// <summary>
/// <c>lanes_list</c>: the lane pools as an agent sees them. Registered by the lanes plugin, so it disappears (with the
/// lanes section of the system prompt) when lanes are disabled.
/// </summary>
internal sealed class LanesListTool(ILaneScheduler scheduler) : IAgentTool
{
    public ToolDefinition Definition { get; } = new()
    {
        Name = "lanes_list",
        Label = "Lanes",
        Description = "List the lane pools: each model pool has a fixed number of lanes (parallel agent slots). Shows busy/capacity, queued agents, the models of each pool and who holds its lanes. Use it before spawning subagents to pick a pool with free lanes.",
        ReadOnly = true,
        Category = "agents",
        Parameters = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() },
    };

    public Task<ToolResult> ExecuteAsync(ToolContext context, JsonElement args, CancellationToken ct)
    {
        var pools = scheduler.Snapshot();
        var runtime = context.Services.Get<IAgentRuntime>();
        var names = runtime?.List(true).ToDictionary(a => a.Id, a => a.Name) ?? [];
        string Who(LaneOwnerInfo o) => (names.TryGetValue(o.AgentId, out var n) ? n : o.Label ?? "?") + (o.AgentId == context.AgentId ? " (you)" : "");

        var sb = new StringBuilder();
        if (pools.Count == 0) sb.Append("No lane pools are known yet.");
        else sb.Append("Lane pools (busy/capacity):\n");
        foreach (var p in pools)
        {
            sb.Append("- ").Append(p.Key).Append(": ").Append(p.Busy).Append('/').Append(p.Capacity).Append(" busy");
            if (p.Queued > 0) sb.Append(", ").Append(p.Queued).Append(" queued");
            if (!string.IsNullOrEmpty(p.Status)) sb.Append(" [").Append(p.Status).Append(']');
            if (p.Models.Count > 0)
            {
                var models = p.Models.Count > 8 ? string.Join(", ", p.Models.Take(8)) + $", … (+{p.Models.Count - 8})" : string.Join(", ", p.Models);
                sb.Append("\n  models: ").Append(models);
            }
            if (p.Owners.Count > 0) sb.Append("\n  running: ").Append(string.Join(", ", p.Owners.Select(Who)));
            if (p.Waiters.Count > 0) sb.Append("\n  waiting: ").Append(string.Join(", ", p.Waiters.Select(Who)));
            sb.Append('\n');
        }
        if (runtime?.Get(context.AgentId)?.Pool is { } myPool) sb.Append("\nYou run on pool ").Append(myPool).Append('.');
        return Task.FromResult(ToolResult.Ok(sb.ToString().TrimEnd(), new JsonObject { ["pools"] = NetPiJson.ToNode(pools) }));
    }
}

/// <summary>How lanes work, for agents that can start subagents (order 300). Contributed only while the lanes plugin runs.</summary>
internal sealed class LanesPromptSection : IPromptSection
{
    public string Id => "lanes";
    public int Order => 300;

    public ValueTask<string?> RenderAsync(PromptContext c, CancellationToken ct)
    {
        bool Has(string name) => c.Tools.Any(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
        if (!Has("agent_spawn")) return ValueTask.FromResult<string?>(null);
        var sb = new StringBuilder("# Lanes\n");
        sb.Append("- Every model belongs to a pool with a fixed number of lanes (parallel slots). An agent holds a lane for its whole run and queues when none is free.");
        if (Has("lanes_list")) sb.Append(" lanes_list shows the pools, their models and who is running; use it to pick a pool with free lanes for a subagent.");
        if (Has("agent_wait")) sb.Append("\n- While you wait in agent_wait, your lane goes to your subagents and you get it back with priority.");
        return ValueTask.FromResult<string?>(sb.ToString());
    }
}
