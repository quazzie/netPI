namespace NetPI.Tools.Agents;

/// <summary>
/// Agent orchestration tools (category "agents"): <c>agent_spawn</c>, and <c>agent</c> with the actions wait, send, list,
/// result and cancel. The caller is <see cref="ToolContext.AgentId"/>; the
/// runtime (<see cref="IAgentRuntime"/>) and scheduler (<see cref="IAgentScheduler"/>) are resolved per call.
/// <c>agent_choices</c> belongs to the agents plugin.
/// </summary>
[NetPiPlugin("netpi.tools.agents", Name = "Agent tools", Description = "Spawn, wait for and message subagents", Order = 60)]
public sealed class AgentToolsPlugin : INetPiPlugin
{
    internal static IReadOnlyList<IAgentTool> CreateTools(IPluginContext context) =>
    [
        new AgentSpawnTool(context),
        new AgentTool(context),
    ];

    public Task StartAsync(IPluginContext context, CancellationToken ct)
    {
        var gate = new object();
        var registrations = new List<IDisposable>();
        void Refresh()
        {
            lock (gate)
            {
                if (context.Stopping.IsCancellationRequested) return;
                var available = context.Services.Get<IAgentRuntime>() is not null;
                if (available && registrations.Count == 0)
                    foreach (var tool in CreateTools(context)) registrations.Add(context.Tools.Register(tool));
                else if (!available && registrations.Count > 0)
                {
                    foreach (var registration in registrations) registration.Dispose();
                    registrations.Clear();
                }
            }
        }
        context.Events.Subscribe("services.changed", _ => Refresh());
        context.Events.Subscribe(EventTypes.PluginsChanged, _ => Refresh());
        Refresh();
        return Task.CompletedTask;
    }
}
