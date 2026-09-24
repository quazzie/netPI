namespace NetPI.Tools.Agents;

/// <summary>
/// Agent orchestration tools (category "agents"): <c>agent_spawn</c>, <c>agent_wait</c>, <c>agent_send</c>,
/// <c>agent_list</c>, <c>agent_result</c>, <c>agent_cancel</c>. The caller is <see cref="ToolContext.AgentId"/>; the
/// runtime (<see cref="IAgentRuntime"/>) and scheduler (<see cref="ILaneScheduler"/>) are resolved per call.
/// <c>lanes_list</c> belongs to the lanes plugin.
/// </summary>
[NetPiPlugin("netpi.tools.agents", Name = "Agent tools", Description = "Spawn, wait for and message subagents", Order = 60)]
public sealed class AgentToolsPlugin : INetPiPlugin
{
    internal static IReadOnlyList<IAgentTool> CreateTools(IPluginContext context) =>
    [
        new AgentSpawnTool(context),
        new AgentWaitTool(context),
        new AgentSendTool(context),
        new AgentListTool(context),
        new AgentResultTool(context),
        new AgentCancelTool(context),
    ];

    public Task StartAsync(IPluginContext context, CancellationToken ct)
    {
        foreach (var tool in CreateTools(context)) context.Tools.Register(tool);
        return Task.CompletedTask;
    }
}
