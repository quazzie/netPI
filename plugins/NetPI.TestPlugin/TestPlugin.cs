using System.Text.Json;
using NetPI.Abstractions;


namespace NetPI.TestPlugin;

/// <summary>
/// A dummy service exposed by the test plugin through the host service registry.
/// </summary>
public sealed class TestService
{
    /// <summary>Which generation produced this instance.</summary>
    public string Generation { get; init; } = "unknown";

    /// <summary>Monotonic per-instance counter, handy for proving a new instance after reload.</summary>
    public long Counter { get; private set; }

    public long Bump() => ++Counter;
}

/// <summary>Event published/subscribed by the test plugin to prove bus ownership on unload.</summary>
public sealed record TestPluginEvent(string Generation, string Text);

/// <summary>
/// Minimal real plugin: registers <see cref="TestService"/> and subscribes to
/// <see cref="TestPluginEvent"/>. Used by the host's reload demo and the
/// reload-proof tests (PLAN §50).
/// </summary>
public sealed class TestPlugin : INetPiPlugin
{
    private TestService? _service;

    public PluginInfo Info { get; } = new("netPI.TestPlugin", "Test Plugin", "0.1.0");

    public async ValueTask LoadAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        // PLAN §50 host test hook: a config-driven failure so tests can
        // exercise the "plugin throws during Load" path deterministically.
        if (context.OwnConfig.ValueKind == JsonValueKind.Object
            && context.OwnConfig.TryGetProperty("loadFail", out var lf)
            && lf.ValueKind == JsonValueKind.True)
            throw new InvalidOperationException("TestPlugin refuses to load (loadFail=true)");

        var generation = context.OwnConfig.ValueKind == JsonValueKind.Object
            ? context.OwnConfig.TryGetProperty("generation", out var g) && g.ValueKind == JsonValueKind.String
                ? g.GetString()!
                : "gen"
            : "gen";

        var service = new TestService { Generation = generation };
        _service = service;
        // Opt out of the service registration when a second copy of this
        // plugin is staged under another directory (the registry IDs would
        // collide); tests do this to exercise host failure paths.
        var register = true;
        if (context.OwnConfig.ValueKind == JsonValueKind.Object
            && context.OwnConfig.TryGetProperty("register", out var reg)
            && reg.ValueKind == JsonValueKind.False)
            register = false;
        if (register)
            context.Services.Register<TestService>("test.service", service);
        context.Events.Subscribe<TestPluginEvent>(e =>
        {
            context.Log.Information($"TestPlugin[{generation}] saw event: {e}");
        });
        context.Log.Information($"TestPlugin loaded (generation '{generation}')");
        // astra-1 P2 test hooks: retain the config for Start/Stop decisions.
        _ownConfig = context.OwnConfig;
        if (context.OwnConfig.ValueKind == JsonValueKind.Object
            && context.OwnConfig.TryGetProperty("stopMs", out var ms)
            && ms.ValueKind == JsonValueKind.Number)
            _stopMs = ms.GetInt32();
        await ValueTask.CompletedTask;
    }

    public async ValueTask StartAsync(CancellationToken cancellationToken)
    {
        // astra-1 P2 test hook: a config-driven Start failure so tests can
        // exercise "candidate starts fail" deterministically.
        if (context_OwnConfigHas("startFail"))
            throw new InvalidOperationException("TestPlugin refuses to start (startFail=true)");
        _service?.Bump();
        await ValueTask.CompletedTask;
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken)
    {
        // astra-1 P2 test hook: a config-driven slow/uncooperative stop so
        // tests can exercise the bounded StopAsync + RestartRequired path.
        if (_stopMs > 0)
            await Task.Delay(_stopMs, cancellationToken);
        return;
    }

    private bool context_OwnConfigHas(string key) =>
        _ownConfig.ValueKind == JsonValueKind.Object
        && _ownConfig.TryGetProperty(key, out var v)
        && v.ValueKind == JsonValueKind.True;

    private JsonElement _ownConfig;
    private int _stopMs;

    public ValueTask UnloadAsync(CancellationToken cancellationToken)
    {
        _service = null;
        return ValueTask.CompletedTask;
    }
}
