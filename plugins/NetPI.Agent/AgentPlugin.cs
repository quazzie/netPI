using NetPI.Abstractions;

namespace NetPI.Agent;

/// <summary>
/// The reloadable agent plugin (PLAN §10). Owns the <see cref="AgentRuntime"/>
/// (the model loop) and exposes <see cref="IAgentRuntime"/> (state),
/// <see cref="ISteeringQueue"/> and <see cref="IAgentRunner"/> through the
/// service registry.
/// </summary>
public sealed class AgentPlugin : INetPiPlugin
{
    private AgentRuntime? _runtime;
    private AgentRunner? _runner;

    public PluginInfo Info { get; } = new("netPI.Agent", "Agent Runtime", "0.1.0");

    public async ValueTask LoadAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        _runtime = new AgentRuntime(context);
        _runner = new AgentRunner(_runtime, context);
        context.Services.Register<IAgentRuntime>("agent", _runtime);
        context.Services.Register<ISteeringQueue>("steering", _runtime);
        context.Services.Register<IAgentRunner>("runner", _runner);
        context.Log.Information("Agent runtime ready");
        await ValueTask.CompletedTask;
    }

    public ValueTask StartAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

    public async ValueTask StopAsync(CancellationToken cancellationToken)
    {
        // astra-1 A (run cleanup): cancel the owned run and AWAIT it (bounded)
        // before the generation is unloaded — releasing a still-executing run
        // would let it write to a dead context. The bound exists because a run
        // wedged in a non-cooperative await cannot be force-stopped in-process.
        if (_runner is not null)
        {
            try { await _runner.WaitForRunAsync(TimeSpan.FromSeconds(30)); }
            catch { /* already stopped */ }
        }
        _runtime = null;
        _runner = null;
        await ValueTask.CompletedTask;
    }

    public ValueTask UnloadAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
}
