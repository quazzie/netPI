using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using NetPI.Abstractions;
using NetPI.Host.Events;
using NetPI.Web;
using Xunit;

namespace NetPI.Host.Tests;

/// <summary>
/// astra-1 §F acceptance: "reloaded services used by subsequent operations".
/// WebApp resolved the five plugin-owned services (runner/agent/steering/
/// compaction/catalog) ONCE at StartAsync and cached them in fields; a
/// <c>plugin.reload</c> of netpi.agent / netpi.autocompact /
/// netpi.provider.aiproxy left it calling the RETIRED generation's
/// instances (chat.send, agent.cancel, runs.list, compaction, models.refresh).
/// These drive the REAL WebApp over a real loopback WebSocket: after the
/// host publishes a <see cref="PluginUpdateCompletedEvent"/> (the reload),
/// the next operation must go through the NEW generation's instances — and
/// a FAILED reload (old generation stays active, the host keeps the old
/// registry entries) must keep using the OLD instances, never the new ones.
///
/// Mirrors the StoreReloadTests reload pattern (registry pointer swap + the
/// new-generation instance is what re-resolution must see) and the
/// WebProjectCommandTests fixture (TestContext + real WebApp + loopback WS).
/// </summary>
public sealed class ServiceReloadTests : IAsyncLifetime
{
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(10);

    private TestContext? _ctx;
    private WebApp? _app;
    private ClientWebSocket? _ws;
    private int _port;

    // generation 1 instances (the ones WebApp cached at StartAsync)
    private Gen1? _oldRunner;
    private FakeAgentRuntime? _oldAgent;
    private FakeCatalog? _oldCatalog;
    private FakeSteering? _oldSteering;
    private FakeCompaction? _oldCompaction;

    // generation 2 instances (registered after the simulated reload)
    private Gen1? _newRunner;
    private FakeAgentRuntime? _newAgent;
    private FakeCatalog? _newCatalog;
    private FakeSteering? _newSteering;
    private FakeCompaction? _newCompaction;

    // ---- the plugin context (the pattern from WebProjectCommandTests) --------

    private sealed class TestContext : IPluginContext
    {
        private readonly FixedServiceRegistry _services = new();
        private readonly RecordingBus _bus = new();
        public RecordingBus Bus => _bus;
        public PluginInfo Info => new("test", "test", "1.0.0");
        public IServiceRegistry Services => _services;
        public IEventBus Events => _bus;
        public ICommandRegistry Commands => new NetPI.Host.Services.CommandRegistry();
        public IWebPanelRegistry WebPanels => new NetPI.Host.Services.WebPanelRegistry();
        public System.Text.Json.JsonElement OwnConfig => System.Text.Json.JsonDocument.Parse("{}").RootElement;
        public IPluginLogger Log => new NoopLogger();
        public IValueLease<object> LeaseSelf() => new NoopLease();
        public void Put(string id, object service) => _services.Put(id, service);

        private sealed class NoopLease : IValueLease<object>
        {
            public object Value => this;
            public void Dispose() { }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
        private sealed class NoopLogger : IPluginLogger
        {
            public void Debug(string m) { }
            public void Information(string m) { }
            public void Warning(string m) { }
            public void Error(string m, Exception? e = null) { }
        }
    }

    private sealed class RecordingBus : IEventBus
    {
        private readonly EventBus _inner = new();
        public IEnumerable<AgentEvent> OfType() => Array.Empty<AgentEvent>();
        public IDisposable Subscribe<TEvent>(NetPI.Abstractions.EventHandler<TEvent> handler,
            EventSubscriptionOptions? options = null) => _inner.Subscribe(handler, options);
        public ValueTask PublishAsync<TEvent>(TEvent evt, CancellationToken cancellationToken = default)
            => _inner.PublishAsync(evt, cancellationToken);
    }

    private sealed class WsLogger : IPluginLogger
    {
        public static readonly System.Collections.Concurrent.ConcurrentQueue<string> Lines = new();
        public void Debug(string m) { }
        public void Information(string m) { Lines.Enqueue("I " + m); }
        public void Warning(string m) { Lines.Enqueue("W " + m); }
        public void Error(string m, Exception? e = null) { Lines.Enqueue("E " + m + " :: " + (e?.Message ?? "")); }
    }

    // ---- generation fakes ----------------------------------------------------

    /// <summary>Recording IAgentRunner — the runs.list / agent.cancel surface.</summary>
    private sealed class Gen1 : IAgentRunner
    {
        public int ListRunsCalls;
        public bool IsRunning => false;
        public ValueTask<bool> SuspendRunAsync(string runId, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(false);
        public ValueTask<bool> RequeueRunAsync(AgentRunRequest request, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(false);

        public ValueTask<AgentRunStart> StartRunAsync(AgentRunRequest request, CancellationToken ct = default)
            => ValueTask.FromResult(new AgentRunStart(request.SessionId, null));
        public ValueTask CancelRunAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
        public IReadOnlyList<RunInfo> ListRuns()
        {
            ListRunsCalls++;
            return new[]
            {
                new RunInfo("run-" + Guid.NewGuid().ToString("n"), "s1", "m1", AgentState.Idle,
                    DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddMinutes(-4), RunState.Completed),
            };
        }
        public RunInfo? GetRun(string runId) => ListRuns().FirstOrDefault(r => r.RunId == runId);
        public RunInfo? GetSessionRun(string sessionId) =>
            ListRuns().FirstOrDefault(r => r.SessionId == sessionId && r.Outcome == RunState.Running);
        public bool CancelRun(string runId) => false;
        public ValueTask<bool> CancelQueuedRun(string runId, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(false);
        public System.Threading.SemaphoreSlim SessionGate(string sessionId) => new(1, 1);
    }

    private sealed class FakeAgentRuntime : IAgentRuntime
    {
        public bool StateReads;
        public IAgentState State { get; } = new FakeState();
        private sealed class FakeState : IAgentState
        {
            public AgentState State => AgentState.Idle;
            public bool IsRunning => false;
            public string? ActiveSessionId => null;
            public bool IsIdle => true;
        }
    }

    /// <summary>Recording IModelCatalog — the models.refresh / models.updated surface.</summary>
    private sealed class FakeCatalog : IModelCatalog
    {
        public int RefreshCalls;
        public IReadOnlyList<ModelInfo> Models { get; } = [
            new ModelInfo("gen1-model", "gen1", "Generation One", false, false),
        ];
        public bool IsStale => false;
        public DateTimeOffset? LastRefreshedAt => null;
        public ValueTask<IReadOnlyList<ModelInfo>> RefreshAsync(CancellationToken cancellationToken)
        {
            RefreshCalls++;
            return ValueTask.FromResult<IReadOnlyList<ModelInfo>>(Models);
        }
    }

    /// <summary>Recording ISteeringQueue — the chat.steer surface.</summary>
    private sealed class FakeSteering : ISteeringQueue
    {
        public int EnqueueCalls;
        public ValueTask EnqueueAsync(string text, string? sessionId = null, CancellationToken cancellationToken = default)
        {
            EnqueueCalls++;
            return ValueTask.CompletedTask;
        }
        public int PendingCount(string? sessionId = null) => 0;
    }

    /// <summary>Recording ICompaction — the session.compact surface.</summary>
    private sealed class FakeCompaction : ICompaction
    {
        public int CompactCalls;
        public bool IsAvailable => true;
        public CompactionPolicy? ContextPolicy => new(true, 4096);
        public ValueTask<CompactionResult> CompactAsync(CompactionRequest request, CancellationToken cancellationToken = default)
        {
            CompactCalls++;
            return ValueTask.FromResult(new CompactionResult(null, null));
        }
        public ValueTask<IReadOnlyList<AgentMessage>> BuildActiveContextAsync(string sessionId, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlyList<AgentMessage>>([]);
    }

    private sealed class FakeSessionStore : ISessionStore
    {
        public ValueTask<SessionInfo> CreateAsync(string? workspacePath, CancellationToken ct = default)
            => ValueTask.FromResult(new SessionInfo("s1", workspacePath, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 0, null));
        public ValueTask<SessionInfo?> GetAsync(string id, CancellationToken ct = default)
            => ValueTask.FromResult((SessionInfo?)null);
        public ValueTask AppendAsync(SessionEntry entry, CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask<IReadOnlyList<SessionInfo>> ListAsync(int count = 50, int offset = 0, CancellationToken ct = default)
            => ValueTask.FromResult<IReadOnlyList<SessionInfo>>([]);
        public ValueTask<int> CountAsync(CancellationToken ct = default) => ValueTask.FromResult(0);
        public ValueTask RenameAsync(string id, string title, CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask DeleteAsync(string id, CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask SetModelAsync(string sessionId, string? modelId, string? reasoningLevel, CancellationToken ct = default)
            => ValueTask.CompletedTask;
        public ValueTask SetWorkspaceAsync(string sessionId, string? workspacePath, CancellationToken ct = default)
            => ValueTask.CompletedTask;
        public ValueTask<IReadOnlyList<SessionEntry>> ReadAsync(string sessionId, int offset, int count, CancellationToken ct = default)
            => ValueTask.FromResult<IReadOnlyList<SessionEntry>>([]);
        public ValueTask<IReadOnlyList<SessionEntry>> ReadBeforeAsync(string sessionId, int beforeSequence, int count, CancellationToken ct = default)
            => ValueTask.FromResult<IReadOnlyList<SessionEntry>>([]);
        public ValueTask<IReadOnlyList<SessionEntry>> ReadAfterAsync(string sessionId, int afterSequence, int count, CancellationToken ct = default)
            => ValueTask.FromResult<IReadOnlyList<SessionEntry>>([]);
        public ValueTask<IReadOnlyList<SessionEntry>> ReadRecentAsync(string sessionId, int count, CancellationToken ct = default)
            => ValueTask.FromResult<IReadOnlyList<SessionEntry>>([]);
        public ValueTask<ProjectChangeResult> SetProjectAsync(ProjectChangeRequest change, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    // ---- lifecycle --------------------------------------------------------------

    public async Task InitializeAsync()
    {
        _port = FindFreePort();
        _ctx = new TestContext();
        _oldRunner = new Gen1();
        _oldAgent = new FakeAgentRuntime();
        _oldCatalog = new FakeCatalog();
        _oldSteering = new FakeSteering();
        _oldCompaction = new FakeCompaction();
        // Generation 1 registered (what a fresh load would register); WebApp
        // resolves these at StartAsync.
        RegisterGen("runner", _oldRunner, "agent", _oldAgent, "catalog", _oldCatalog,
            "steering", _oldSteering, "compaction", _oldCompaction);
        _ctx.Put("sessions", new FakeSessionStore());
        _app = new WebApp(_ctx, _port,
            System.IO.Path.Combine(AppContext.BaseDirectory, "does-not-exist"), 1024 * 1024, new WsLogger());
        await _app.StartAsync(CancellationToken.None);
    }

    public async Task DisposeAsync()
    {
        try { _ws?.Abort(); } catch { }
        try { if (_app is not null) await _app.StopAsync(CancellationToken.None); } catch { }
    }

    private void RegisterGen(
        string runnerId, IAgentRunner runner,
        string agentId, IAgentRuntime agent,
        string catalogId, IModelCatalog catalog,
        string steeringId, ISteeringQueue steering,
        string compactionId, ICompaction compaction)
    {
        // A host reload swaps the registry entries for one id at a time
        // (RemoveAllFor(old) → the new generation re-registers).
        _ctx!.Put(runnerId, runner);
        _ctx.Put(agentId, agent);
        _ctx.Put(catalogId, catalog);
        _ctx.Put(steeringId, steering);
        _ctx.Put(compactionId, compaction);
    }

    private static int FindFreePort()
    {
        var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        var port = ((System.Net.IPEndPoint)l.LocalEndpoint!).Port;
        l.Stop();
        return port;
    }

    // ---- WS helpers -------------------------------------------------------------

    private async Task<ClientWebSocket> ConnectAsync()
    {
        var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{_port}/ws"), CancellationToken.None);
        _ws = ws;
        return ws;
    }

    private static async Task SendCommandAsync(ClientWebSocket ws, string command, Dictionary<string, object?>? payload = null)
    {
        var json = new Dictionary<string, object?> { ["type"] = command, ["requestId"] = Guid.NewGuid().ToString("n") };
        if (payload is not null) json["payload"] = payload;
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(json));
        await ws.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
    }

    private async Task<string?> ReceiveMessageAsync(ClientWebSocket ws)
    {
        var buffer = new byte[64 * 1024];
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken.None);
        cts.CancelAfter(ReadTimeout);
        try
        {
            var result = await ws.ReceiveAsync(buffer, cts.Token);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            return Encoding.UTF8.GetString(buffer, 0, result.Count);
        }
        catch (OperationCanceledException) when (ws.State == WebSocketState.Open)
        {
            throw new TimeoutException("timed out waiting for a WebSocket frame");
        }
    }

    private static void DumpServerLog()
    {
        foreach (var line in WsLogger.Lines)
            System.Console.Error.WriteLine("[server] " + line);
    }

    private async Task<JsonElement> AwaitAsync(ClientWebSocket ws, string command, Dictionary<string, object?>? payload,
        string expect)
    {
        await SendCommandAsync(ws, command, payload);
        while (true)
        {
            var msg = await ReceiveMessageAsync(ws);
            if (msg is null) break;
            using var doc = JsonDocument.Parse(msg);
            var type = doc.RootElement.GetProperty("type").GetString();
            if (type == "error")
                Assert.Fail($"unexpected error awaiting {expect}: "
                    + doc.RootElement.GetProperty("payload").ToString());
            if (type == expect) return doc.RootElement.Clone();
        }
        DumpServerLog();
        Assert.Fail($"connection closed before {expect}");
        return default;
    }

    /// <summary>
    /// Simulate the host-side reload for the given plugin id: retire the old
    /// generation's instances from the registry (RemoveAllFor semantics —
    /// the host physically removes them), register the NEW generation's
    /// instances, then publish the completion event the lifecycle runner
    /// publishes (this is what the WebApp's handler reacts to).
    /// </summary>
    private async Task SimulateReloadAsync(PluginLifecycleOutcome outcome, string pluginId)
    {
        if (outcome == PluginLifecycleOutcome.Applied)
        {
            _newRunner = new Gen1();
            _newAgent = new FakeAgentRuntime();
            _newCatalog = new FakeCatalog();
            _newSteering = new FakeSteering();
            _newCompaction = new FakeCompaction();
            RegisterGen("runner", _newRunner, "agent", _newAgent, "catalog", _newCatalog,
                "steering", _newSteering, "compaction", _newCompaction);
        }
        await _ctx!.Bus.PublishAsync(new PluginUpdateCompletedEvent(
            "op-reload-" + Guid.NewGuid().ToString("n"), PluginOperationKind.Reload, pluginId,
            [new PluginUpdateResult(pluginId, outcome, outcome == PluginLifecycleOutcome.Applied ? null : "load failed", "gen2")],
            [], null, null), CancellationToken.None);
        // The Web handler re-resolves synchronously on publish; give the fan-out
        // Task.Run events (plugins.state, ...) a beat so ordering is stable.
        await Task.Delay(50);
    }

    // ---- tests ------------------------------------------------------------------

    [Fact]
    public async Task Reload_SubsequentOperations_UseTheNewGeneration()
    {
        var ws = await ConnectAsync();

        // Precondition: generation 1 is cached and serving.
        await AwaitAsync(ws, "runs.list", null, "runs.list");
        Assert.Equal(1, _oldRunner!.ListRunsCalls);

        // The host finishes a plugin.reload of netpi.agent: the new generation
        // re-registered its services (old ones removed), the runner publishes
        // the completion event on the bus.
        await SimulateReloadAsync(PluginLifecycleOutcome.Applied, "netpi.agent");
        Assert.NotNull(_newRunner);

        // astra-1 §F acceptance: the SUBSEQUENT operation must hit the NEW
        // generation's instance — not the retired one WebApp cached.
        await AwaitAsync(ws, "runs.list", null, "runs.list");
        Assert.Equal(1, _oldRunner!.ListRunsCalls);        // only the pre-reload call
        Assert.Equal(1, _newRunner!.ListRunsCalls);        // post-reload → new generation

        // models.refresh resolves the CURRENT catalog the same way.
        await AwaitAsync(ws, "models.refresh", null, "models.updated");
        Assert.Equal(0, _oldCatalog!.RefreshCalls);
        Assert.Equal(1, _newCatalog!.RefreshCalls);

        // And the cached fields themselves now point at the new instances
        // (chat.send / agent.cancel / session.compact / chat.steer all read
        // these fields, not the registry).
        var app = _app!;
        var runnerField = typeof(WebApp).GetField("_runner", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var agentField = typeof(WebApp).GetField("_agent", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var steeringField = typeof(WebApp).GetField("_steering", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var compactionField = typeof(WebApp).GetField("_compaction", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var catalogField = typeof(WebApp).GetField("_catalog", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        Assert.Same(_newRunner, runnerField.GetValue(app));
        Assert.Same(_newAgent, agentField.GetValue(app));
        Assert.Same(_newSteering, steeringField.GetValue(app));
        Assert.Same(_newCompaction, compactionField.GetValue(app));
        Assert.Same(_newCatalog, catalogField.GetValue(app));
    }

    [Fact]
    public async Task FailedReload_OldGenerationStaysActive_KeepsServingOldInstances()
    {
        var ws = await ConnectAsync();
        await AwaitAsync(ws, "runs.list", null, "runs.list");
        Assert.Equal(1, _oldRunner!.ListRunsCalls);

        // A FAILED reload keeps the previous generation Active: the host
        // leaves the old registry entries in place (no swap). WebApp must
        // keep serving the SAME instances — a re-resolution against the
        // unchanged registry yields exactly them.
        await SimulateReloadAsync(PluginLifecycleOutcome.Failed, "netpi.agent");

        await AwaitAsync(ws, "runs.list", null, "runs.list");
        Assert.Equal(2, _oldRunner!.ListRunsCalls);
        Assert.Null(_newRunner); // no new generation was ever registered

        var app = _app!;
        var runnerField = typeof(WebApp).GetField("_runner", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        Assert.Same(_oldRunner, runnerField.GetValue(app));
    }
}
