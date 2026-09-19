using NetPI.Abstractions;

namespace NetPI.Agent;

/// <summary>
/// The reloadable agent plugin (PLAN §10). Owns the <see cref="AgentRuntime"/>
/// (the model loop) and exposes <see cref="IAgentRuntime"/> (state) and
/// <see cref="ISteeringQueue"/> through the service registry.
/// </summary>
public sealed class AgentPlugin : INetPiPlugin
{
    private AgentRuntime? _runtime;

    public PluginInfo Info { get; } = new("netPI.Agent", "Agent Runtime", "0.1.0");

    public async ValueTask LoadAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        _runtime = new AgentRuntime(context);
        context.Services.Register<IAgentRuntime>("agent", _runtime);
        context.Services.Register<ISteeringQueue>("steering", _runtime);
        context.Log.Information("Agent runtime ready");
        await ValueTask.CompletedTask;
    }

    public ValueTask StartAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

    public async ValueTask StopAsync(CancellationToken cancellationToken)
    {
        _runtime = null;
        await ValueTask.CompletedTask;
    }

    public ValueTask UnloadAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
}
