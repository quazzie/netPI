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
        _ownConfig = context.OwnConfig; // astra-1 P6: hooks below read config
        _bus = context.Events;
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
        // astra-1 P6 test hook: fail AFTER service registration (partial
        // registration — the registered service must be rolled back).
        if (context_OwnConfigHas("registerFail"))
            throw new InvalidOperationException("TestPlugin fails after registration (registerFail=true)");
        context.Events.Subscribe<TestPluginEvent>(e =>
        {
            context.Log.Information($"TestPlugin[{generation}] saw event: {e}");
        });
        // astra-1 P6 test hook: fail AFTER the event subscription.
        if (context_OwnConfigHas("subscribeFail"))
            throw new InvalidOperationException("TestPlugin fails after subscription (subscribeFail=true)");
        context.Log.Information($"TestPlugin loaded (generation '{generation}')");
        // astra-1 P6 test hooks: register a slash command + a web-panel so
        // unload-leak tests can assert the host removes them with the
        // generation (mirrors what AutoCompact/BackgroundTasks do for real).
        if (context_OwnConfigHas("registerCommands"))
            context.Commands.Register(new CommandDefinition("/testplugin", "TestPlugin command hook"));
        if (context_OwnConfigHas("registerPanel"))
            context.WebPanels.Register(new WebPanelDefinition("testpanel", "Test Panel", "T", "/plugins/testpanel/panel", 1));
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
        // astra-1 P6 test hook: PARTIAL start — real start work (a start
        // event is published) completes BEFORE the failure, so the host
        // must stop/clean up a partially started generation.
        _startPartial = context_OwnConfigHas("startPartialFail");
        if (_startPartial && _bus is not null)
            await _bus.PublishAsync(new TestPluginEvent(generationOf(), "start-partial"), cancellationToken).ConfigureAwait(false);
        _service?.Bump();
        if (_startPartial)
            throw new InvalidOperationException("TestPlugin fails during partial start (startPartialFail=true)");
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

    private string generationOf() =>
        _ownConfig.ValueKind == JsonValueKind.Object
        && _ownConfig.TryGetProperty("generation", out var g)
        && g.ValueKind == JsonValueKind.String ? g.GetString()! : "gen";

    /// <summary>astra-1 P6: set while a start-partial generation is in flight (Stop must observe it).</summary>
    public bool StartPartialActive => _startPartial;

    private bool context_OwnConfigHas(string key) =>
        _ownConfig.ValueKind == JsonValueKind.Object
        && _ownConfig.TryGetProperty(key, out var v)
        && (v.ValueKind == JsonValueKind.True || (v.ValueKind == JsonValueKind.String && v.GetString() == "true"));

    private JsonElement _ownConfig = default;
    private int _stopMs;
    private bool _startPartial;
    private IEventBus? _bus; // astra-1 P6: retained for partial-start work in StartAsync

    public ValueTask UnloadAsync(CancellationToken cancellationToken)
    {
        _service = null;
        return ValueTask.CompletedTask;
    }
}
