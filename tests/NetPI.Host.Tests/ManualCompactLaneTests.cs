using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using NetPI.Abstractions;
using NetPI.Web;
using Xunit;

namespace NetPI.Host.Tests;

/// <summary>
/// astra-2 §3.3 (B4): a manual <c>session.compact</c> for a POOLED model must
/// go through lane admission, never bypass it. The Web surface resolves the
/// model's execution policy:
///  - pooled + free pool   → compact runs UNDER the lane (the durable row is
///    queued until admission; the token is released exactly once);
///  - pooled + full pool   → the request QUEUES as a durable maintenance
///    assignment; NO compaction runs until a lane frees (the admission sink
///    then executes it and reconciles the row to a terminal state);
///  - busy session         → rejected (manual maintenance must not race a live
///    owner — §3.3 "manual maintenance for an idle session queues");
///  - no pool binding      → the legacy direct path (no lanes involved).
/// Drives the REAL WebApp over a real loopback WebSocket.
/// </summary>
public sealed class ManualCompactLaneTests : IAsyncLifetime
{
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(10);

    private WebApp? _app;
    private TestContext? _ctx;
    private ClientWebSocket? _ws;
    private int _port;

    private CountingLanes? Lanes;
    private RecordingCompaction? Compaction;
    private FakeRunner? Runner;
    private FakeMaintenanceStore? Store;

    public Task InitializeAsync() => InitializeAsyncImpl();

    private async Task InitializeAsyncImpl()
    {
        _port = FindFreePort();
        _ctx = new TestContext();
        _ctx.Add("sessions", new FakeSessionStore("s1"));
        _ctx.Add("runner", Runner = new FakeRunner());
        _ctx.Add("compaction", Compaction = new RecordingCompaction());
        _ctx.Add("orchestration-store", Store = new FakeMaintenanceStore());
        _ctx.Add("catalog", new FakeCatalog());
        _ctx.Add("lanes", Lanes = new CountingLanes());
        _ctx.Add("deployments", new PooledPolicySource());
        _app = new WebApp(_ctx, _port,
            System.IO.Path.Combine(AppContext.BaseDirectory, "does-not-exist"), 1024 * 1024, new WsLogger());
        await _app.StartAsync(CancellationToken.None);
    }

    public async Task DisposeAsync()
    {
        try { _ws?.Abort(); } catch { }
        try { if (_app is not null) await _app.StopAsync(CancellationToken.None); } catch { }
    }

    private static int FindFreePort()
    {
        var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        var port = ((System.Net.IPEndPoint)l.LocalEndpoint!).Port;
        l.Stop();
        return port;
    }

    private async Task<ClientWebSocket> ConnectAsync()
    {
        var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{_port}/ws"), CancellationToken.None);
        _ws = ws;
        return ws;
    }
    /// <summary>Poll until <paramref name="condition"/> holds (bounded 5 s).</summary>
    private static async Task UntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++)
            await Task.Delay(10);
        Assert.True(condition(), "condition did not hold within 5 s");
    }

    private static async Task SendCommandAsync(ClientWebSocket ws, string command,
        Dictionary<string, object?> payload)
    {
        var json = new Dictionary<string, object?>
        {
            ["type"] = command,
            ["requestId"] = Guid.NewGuid().ToString("n"),
            ["payload"] = payload,
        };
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(json));
        await ws.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
    }

    /// <summary>Send session.compact and await its result frame (ack + all
    /// bootstrap / broadcast noise is skipped). Returns the result payload.</summary>
    private async Task<JsonElement> SendCompactAsync(ClientWebSocket ws,
        Dictionary<string, object?> payload)
    {
        await SendCommandAsync(ws, "session.compact", payload);
        while (true)
        {
            var buffer = new byte[64 * 1024];
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken.None);
            cts.CancelAfter(ReadTimeout);
            WebSocketReceiveResult result;
            try
            {
                result = await ws.ReceiveAsync(buffer, cts.Token);
            }
            catch (OperationCanceledException) when (ws.State == WebSocketState.Open)
            {
                throw new TimeoutException("timed out waiting for session.compact.result");
            }
            if (result.MessageType == WebSocketMessageType.Close)
                throw new InvalidOperationException("connection closed before session.compact.result");
            using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(buffer, 0, result.Count));
            var type = doc.RootElement.GetProperty("type").GetString();
            if (type == "error")
                throw new InvalidOperationException("server error: "
                    + doc.RootElement.GetProperty("payload").ToString());
            if (type == "session.compact.result")
                return doc.RootElement.GetProperty("payload").Clone();
        }
    }

    // ---- tests ----------------------------------------------------------------

    [Fact]
    public async Task Pooled_FreePool_CompactsUnderLane_TokenReleasedOnce()
    {
        Lanes!.Capacity = 2;
        var ws = await ConnectAsync();
        var r = await SendCompactAsync(ws, new() { ["sessionId"] = "s1", ["model"] = "m-pooled" });
        Assert.True(r.GetProperty("performed").GetBoolean());
        Assert.Contains("lane admitted", r.GetProperty("note").GetString());

        Assert.Equal(1, Compaction!.CompactCalls);
        Assert.Single(Store!.Created);
        Assert.Equal("compact: manual context compaction", Store.Created[0].Title);
        // The lane was granted AND released exactly once (no leaked ownership).
        Assert.Equal(1, Lanes.AcquireCount);
        Assert.Equal(1, Lanes.ReleaseCount);
        Assert.Contains(Lanes.ReleasedAssignmentIds, a => a == Store.Created[0].AssignmentId);
        // The durable row walked Queued → Running → Completed.
        Assert.Contains(Store.Transitions, t => t.Lifecycle == AgentAssignmentLifecycle.Running);
        Assert.Contains(Store.Transitions, t => t.Lifecycle == AgentAssignmentLifecycle.Completed);
    }

    [Fact]
    public async Task Pooled_FullPool_QueuesDurable_NoCompact_UntilLaneFrees()
    {
        Lanes!.Capacity = 0; // pool full: every acquire queues
        var ws = await ConnectAsync();
        var r = await SendCompactAsync(ws, new() { ["sessionId"] = "s1", ["model"] = "m-pooled" });
        Assert.False(r.GetProperty("performed").GetBoolean());
        Assert.StartsWith("queued: position 1 in pool p1", r.GetProperty("note").GetString());

        // The queue item is durable STORED WORK — a restart would not lose it.
        Assert.Single(Store!.Created);
        Assert.Equal(0, Compaction!.CompactCalls);

        // A lane frees → the admission sink executes the deferred maintenance.
        Lanes.Capacity = 1;
        Lanes.ReleaseTestLane();
        // The maintenance runs fire-and-forget (the sink contract is non-
        // blocking), so poll until it lands.
        await UntilAsync(() => Compaction.CompactCalls == 1);
        Assert.Contains(Store.Transitions, t => t.Lifecycle == AgentAssignmentLifecycle.Completed);
        Assert.Single(Lanes.AdmittedFromQueue);
    }

    [Fact]
    public async Task Pooled_BusySession_IsRejected_NotCompacted()
    {
        Lanes!.Capacity = 2;
        Runner!.Busy = true;
        var ws = await ConnectAsync();
        var r = await SendCompactAsync(ws, new() { ["sessionId"] = "s1", ["model"] = "m-pooled" });
        Assert.False(r.GetProperty("performed").GetBoolean());
        Assert.Contains("busy", r.GetProperty("note").GetString());
        Assert.Equal(0, Compaction!.CompactCalls);
        Assert.Empty(Store!.Created);
        Assert.Equal(0, Lanes.AcquireCount);
    }

    [Fact]
    public async Task NonPooled_LegacyDirectPath_NoLanesInvolved()
    {
        Lanes!.Capacity = 0; // would queue if admission were consulted
        var ws = await ConnectAsync();
        var r = await SendCompactAsync(ws, new() { ["sessionId"] = "s1", ["model"] = "m-legacy" });
        Assert.True(r.GetProperty("performed").GetBoolean());
        Assert.Equal("compacted", r.GetProperty("note").GetString());
        Assert.Equal(1, Compaction!.CompactCalls);
        Assert.Equal(0, Lanes.AcquireCount);
        Assert.Empty(Store!.Created); // no durable row: the legacy path is unchanged
    }

    // ---- fakes ----------------------------------------------------------------

    private sealed class TestContext : IPluginContext
    {
        private readonly FixedServiceRegistry _services = new();
        public PluginInfo Info => new("test", "test", "1.0.0");
        public IServiceRegistry Services => _services;
        public IEventBus Events => new Bus();
        public ICommandRegistry Commands => new NetPI.Host.Services.CommandRegistry();
        public IWebPanelRegistry WebPanels => new NetPI.Host.Services.WebPanelRegistry();
        public System.Text.Json.JsonElement OwnConfig => System.Text.Json.JsonDocument.Parse("{}").RootElement;
        public IPluginLogger Log => new WsLogger();
        public IValueLease<object> LeaseSelf() => new NoopLease();
        public void Add(string id, object service) => _services.Put(id, service);

        private sealed class FixedServiceRegistry : IServiceRegistry
        {
            private readonly Dictionary<string, object> _map = new(StringComparer.Ordinal);
            public void Put(string id, object instance) => _map[id] = instance;
            public IDisposable Register<T>(string id, T instance) where T : notnull
            {
                _map[id] = instance;
                return new Noop();
            }
            public IValueLease<T> Acquire<T>(string id) where T : notnull
                => new Lease<T>(Require<T>(id));
            public IValueLease<object> Acquire(string id, Type expectedType)
                => new Lease<object>(Require(id, expectedType));
            public IValueLease<T> AcquireSelfLease<T>() where T : notnull
                => new Lease<T>(default!);
            public T Resolve<T>(string id) where T : notnull => Require<T>(id);

            private object Require(string id, Type type) =>
                _map.TryGetValue(id, out var v) ? v : throw new ServiceUnavailableException(id, "missing");
            private T Require<T>(string id) where T : notnull =>
                _map.TryGetValue(id, out var v) ? (T)v : throw new ServiceUnavailableException(id, "missing");

            private sealed class Noop : IDisposable { public void Dispose() { } }
            private sealed class Lease<T>(T value) : IValueLease<T> where T : notnull
            {
                public T Value => value;
                public void Dispose() { }
                public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
            }
        }

        private sealed class Bus : IEventBus
        {
            private readonly NetPI.Host.Events.EventBus _inner = new();
            public IDisposable Subscribe<TEvent>(NetPI.Abstractions.EventHandler<TEvent> handler,
                EventSubscriptionOptions? options = null) => _inner.Subscribe(handler, options);
            public ValueTask PublishAsync<TEvent>(TEvent evt, CancellationToken cancellationToken = default)
                => _inner.PublishAsync(evt, cancellationToken);
        }

        private sealed class NoopLease : IValueLease<object>
        {
            public object Value => this;
            public void Dispose() { }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }

    /// <summary>astra-2 §3.3 (B4) test fake: a capacity-counted pool. Admission
    /// grants a token while ownership &lt; capacity; otherwise the entry queues.
    /// <c>ReleaseTestLane</c> simulates an external owner freeing a lane and
    /// drains the FIFO queue through the registered sink handler (the
    /// scheduler's contract: the handler runs once per newly admitted token,
    /// non-blocking, after the admission lock releases).</summary>
    private sealed class CountingLanes : ILaneScheduler, ILaneAdmissionSink
    {
        private readonly object _gate = new();
        private readonly List<LaneQueueEntry> _queue = [];
        private readonly List<LaneOwnershipToken> _owned = [];
        public int Capacity;
        public int AcquireCount;
        public int ReleaseCount;
        private Action<LaneOwnershipToken>? _handler;
        public readonly List<LaneOwnershipToken> AdmittedFromQueue = [];
        public readonly List<string> ReleasedAssignmentIds = [];

        public void OnAdmittedFromQueue(Action<LaneOwnershipToken> handler) => _handler = handler;

        public ValueTask<LaneAcquireResult> AcquireAsync(LaneQueueEntry queue)
        {
            lock (_gate)
            {
                AcquireCount++;
                if (_owned.Count < Capacity)
                {
                    var token = new LaneOwnershipToken(queue.PoolId, "lane-" + queue.PoolId,
                        queue.AssignmentId, queue.AssignmentId, "gen-1", _owned.Count + 1,
                        DateTimeOffset.UtcNow);
                    _owned.Add(token);
                    return ValueTask.FromResult(new LaneAcquireResult(token, 0, null));
                }
                _queue.Add(queue);
                return ValueTask.FromResult(new LaneAcquireResult(null, _queue.Count, "pool at capacity"));
            }
        }

        public ValueTask ReleaseAsync(LaneOwnershipToken token)
        {
            lock (_gate)
            {
                ReleaseCount++;
                if (_owned.Remove(token))
                {
                    ReleasedAssignmentIds.Add(token.AssignmentId);
                    AdmitNext();
                }
            }
            return ValueTask.CompletedTask;
        }

        /// <summary>Test seam: an external owner frees a lane — clear ownership
        /// and drain the FIFO queue through the sink handler.</summary>
        public void ReleaseTestLane()
        {
            lock (_gate)
            {
                _owned.Clear();
                AdmitNext();
            }
        }

        private void AdmitNext()
        {
            while (_owned.Count < Capacity && _queue.Count > 0)
            {
                var entry = _queue[0];
                _queue.RemoveAt(0);
                var token = new LaneOwnershipToken(entry.PoolId, "lane-" + entry.PoolId,
                    entry.AssignmentId, entry.AssignmentId, "gen-1", _owned.Count + 1,
                    DateTimeOffset.UtcNow);
                _owned.Add(token);
                AdmittedFromQueue.Add(token);
                var handler = _handler;
                if (handler is not null)
                {
                    try { handler(token); }
                    catch { /* the scheduler logs; the fake swallows */ }
                }
            }
        }

        public ValueTask<LaneOwnershipToken?> HandoffAsync(LaneOwnershipToken fromToken, LaneQueueEntry toEntry)
            => ValueTask.FromResult<LaneOwnershipToken?>(null);
        public bool TryValidatePermit(LaneOwnershipToken? token, string poolId, string assignmentId)
            => token is not null && token.CoversPool(poolId) && token.AssignmentId == assignmentId
               && _owned.Contains(token);
        public ValueTask<bool> CancelQueuedAsync(string assignmentId)
        {
            bool removed = false;
            lock (_gate)
            {
                var i = _queue.FindIndex(q => q.AssignmentId == assignmentId);
                if (i >= 0) { _queue.RemoveAt(i); removed = true; }
            }
            return ValueTask.FromResult(removed);
        }
        public IReadOnlyList<LanePoolSnapshot> Snapshots() => Array.Empty<LanePoolSnapshot>();
        public ValueTask SetPoolEnabledAsync(string poolId, bool enabled) => ValueTask.CompletedTask;
    }

    /// <summary>Records compactions; <c>Perform</c> controls whether a pass
    /// actually happens (false = the honest "no-op below threshold").</summary>
    private sealed class RecordingCompaction : ICompaction
    {
        private readonly object _gate = new();
        public int CompactCalls;
        public bool Perform = true;
        public CompactionPolicy? ContextPolicy => new(true, 16384);
        public bool IsAvailable => true;
        public ValueTask<CompactionResult> CompactAsync(CompactionRequest request, CancellationToken cancellationToken = default)
        {
            lock (_gate) CompactCalls++;
            return ValueTask.FromResult(Perform
                ? new CompactionResult(new CompactionEntryPayload
                {
                    Summary = "s", EstimatedTokensBefore = 100, EstimatedTokensAfter = 10,
                }, [new AgentMessage("m1", MessageRole.System, [new TextPart("x")], DateTimeOffset.UtcNow)])
                : new CompactionResult(null, null));
        }
        public ValueTask<IReadOnlyList<AgentMessage>> BuildActiveContextAsync(string sessionId, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlyList<AgentMessage>>([]);
    }

    private sealed class PooledPolicySource : IDeploymentPolicySource
    {
        public DeploymentPolicy? PolicyFor(string modelId) => modelId == "m-pooled"
            ? new DeploymentPolicy(modelId, DeploymentExecutionMode.Pooled, "p1", "dep-1")
            : null;
    }

    private sealed class FakeRunner : IAgentRunner
    {
        public bool Busy;
        public bool IsRunning => false;
        public ValueTask<bool> SuspendRunAsync(string runId, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(false);
        public ValueTask<bool> RequeueRunAsync(AgentRunRequest request, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(false);
        public ValueTask<AgentRunStart> StartRunAsync(AgentRunRequest request, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new AgentRunStart(request.SessionId, null, request.RunId));
        public ValueTask CancelRunAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
        public IReadOnlyList<RunInfo> ListRuns() => new[]
        {
            new RunInfo("run-1", "s-other", "m1", AgentState.Idle,
                DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddMinutes(-4), RunState.Completed),
        };
        public RunInfo? GetRun(string runId) => null;
        public RunInfo? GetSessionRun(string sessionId) => !Busy || sessionId != "s1" ? null
            : new RunInfo("run-live", "s1", "m-pooled", AgentState.ExecutingTools,
                DateTimeOffset.UtcNow, null, RunState.Running);
        public bool CancelRun(string runId) => false;
        public ValueTask<bool> CancelQueuedRun(string runId, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(false);
        public System.Threading.SemaphoreSlim SessionGate(string sessionId) => new(1, 1);
    }

    /// <summary>Records the durable maintenance rows + the lifecycle transitions
    /// the Web surface performs while executing a held maintenance run.</summary>
    private sealed class FakeMaintenanceStore : IOrchestrationStore
    {
        public readonly List<AgentAssignmentRow> Created = [];
        public readonly List<(string AssignmentId, AgentAssignmentLifecycle Lifecycle)> Transitions = [];

        public ValueTask<AgentIdentity> EnsureRootAgentAsync(string sessionId, string? teamId, string title, CancellationToken ct = default)
            => ValueTask.FromResult(new AgentIdentity("agent-1", teamId, null, sessionId, title, false, DateTimeOffset.UtcNow));

        public ValueTask<AgentAssignmentRow> CreateAssignmentAsync(string operationId, string agentId, string sessionId, string? teamId,
            string? parentAgentId, string? modelId, string? poolId, string? deploymentId, string title, string? briefRef,
            string? workspaceMode = null, string? workspacePath = null, CancellationToken ct = default)
        {
            var row = new AgentAssignmentRow(operationId, agentId, teamId, sessionId, parentAgentId,
                AgentAssignmentLifecycle.Queued, AgentState.Idle, DeploymentExecutionMode.Pooled,
                poolId, null, deploymentId, modelId, title, DateTimeOffset.UtcNow, null, null, null);
            lock (Created) Created.Add(row);
            return ValueTask.FromResult(row);
        }

        public ValueTask<bool> TransitionAsync(string assignmentId, int expectedVersion, AgentAssignmentLifecycle lifecycle,
            AgentState phase, string? poolId, string? laneId, string? deploymentId, string? reason, string? checkpointRef,
            CancellationToken ct = default)
        {
            lock (Transitions) Transitions.Add((assignmentId, lifecycle));
            return ValueTask.FromResult(true);
        }

        public ValueTask<AgentAssignmentRow?> GetByRunIdAsync(string runId, CancellationToken ct = default)
        {
            lock (Created)
                return ValueTask.FromResult<AgentAssignmentRow?>(
                    Created.FirstOrDefault(r => r.AssignmentId == runId));
        }

        // ---- unused members ---------------------------------------------------
        public ValueTask<AgentSpawnOutcome> SpawnChildAsync(string operationId, string? parentAgentId, string? teamId, string modelId, string? poolId, string? deploymentId, string brief, string title, string? workspaceMode = null, string? workspacePath = null, CancellationToken ct = default) => throw new NotImplementedException();
        public ValueTask<AgentIdentity?> GetAgentAsync(string agentId, CancellationToken ct = default) => throw new NotImplementedException();
        public ValueTask<AgentIdentity?> GetAgentBySessionAsync(string sessionId, CancellationToken ct = default) => throw new NotImplementedException();
        public ValueTask<AgentAssignmentRow?> GetAssignmentAsync(string assignmentId, CancellationToken ct = default) => throw new NotImplementedException();
        public ValueTask<AgentAssignmentRow?> GetNonterminalAsync(string sessionId, CancellationToken ct = default) => throw new NotImplementedException();
        public ValueTask<IReadOnlyList<AgentAssignmentRow>> ListNonterminalAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public ValueTask<IReadOnlyList<QueuedAdoptionInfo>> ListQueuedForAdoptionAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public ValueTask MarkToolBatchInFlightAsync(string assignmentId, string agentId, string sessionId, int transcriptCursor, string? toolCallIdsJson, CancellationToken ct = default) => throw new NotImplementedException();
        public ValueTask ClearToolBatchCheckpointAsync(string assignmentId, CancellationToken ct = default) => throw new NotImplementedException();
        public ValueTask<bool> HasToolBatchCheckpointAsync(string assignmentId, CancellationToken ct = default) => ValueTask.FromResult(false);
        public ValueTask WriteLaneJournalAsync(string assignmentId, string? poolId, string? laneId, string state, CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask<string?> GetRunIdAsync(string assignmentId, CancellationToken ct = default) => throw new NotImplementedException();
        public ValueTask<IReadOnlyList<AgentAssignmentRow>> ListSubtreeAsync(string agentId, CancellationToken ct = default) => throw new NotImplementedException();
        public ValueTask SetSessionWorkspaceAsync(string sessionId, string? workspace, CancellationToken ct = default) => throw new NotImplementedException();
        public ValueTask<IReadOnlyList<AgentAssignmentRow>> ListRecentTerminalAsync(int limit, CancellationToken ct = default) => throw new NotImplementedException();
        public ValueTask<int> SendMessageAsync(string messageId, string fromAgentId, string toAgentId, string? teamId, string kind, string body, IReadOnlyList<string>? artifacts, string? idempotencyKey, CancellationToken ct = default) => throw new NotImplementedException();
        public ValueTask<IReadOnlyList<AgentMailboxMessage>> DrainMailboxAsync(string agentId, int count, CancellationToken ct = default) => throw new NotImplementedException();
        public ValueTask<bool> NoteMessageForWaitsAsync(string toAgentId, string fromAgentId, string kind, CancellationToken ct = default) => throw new NotImplementedException();
        public ValueTask RegisterWaitAsync(string waitId, string agentId, string assignmentId, AgentWaitCondition condition, CancellationToken ct = default) => throw new NotImplementedException();
        public ValueTask<bool> NoteTerminalForWaitsAsync(string assignmentId, CancellationToken ct = default) => throw new NotImplementedException();
        public ValueTask<IReadOnlyList<AgentSatisfiedWait>> ListSatisfiedWaitsAsync(string agentId, CancellationToken ct = default) => throw new NotImplementedException();
        public ValueTask MarkWaitConsumedAsync(string waitId, CancellationToken ct = default) => throw new NotImplementedException();
        public ValueTask<AgentTaskRecord> CreateTaskAsync(string taskId, string teamId, string title, IReadOnlyList<string> dependsOnTaskIds, CancellationToken ct = default) => throw new NotImplementedException();
        public ValueTask<IReadOnlyList<AgentTaskRecord>> ListTasksAsync(string teamId, string? status, CancellationToken ct = default) => throw new NotImplementedException();
        public ValueTask<bool> ClaimTaskAsync(string taskId, string agentId, CancellationToken ct = default) => throw new NotImplementedException();
        public ValueTask UpdateTaskStatusAsync(string taskId, string status, string? ownerAgentId, CancellationToken ct = default) => throw new NotImplementedException();
        public ValueTask<bool> DeleteTaskAsync(string taskId, CancellationToken ct = default) => throw new NotImplementedException();
        public ValueTask<int> CountOutstandingAsync(string toAgentId, CancellationToken ct = default) => throw new NotImplementedException();
    }

    private sealed class FakeSessionStore : ISessionStore
    {
        private readonly SessionInfo _s;
        public FakeSessionStore(string id) => _s = new SessionInfo(id, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 0, "test");
        public ValueTask<SessionInfo> CreateAsync(string? workspacePath, CancellationToken ct = default)
            => ValueTask.FromResult(_s);
        public ValueTask<SessionInfo?> GetAsync(string id, CancellationToken ct = default)
            => ValueTask.FromResult(id == _s.Id ? _s : null);
        public ValueTask AppendAsync(SessionEntry entry, CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask<IReadOnlyList<SessionInfo>> ListAsync(int count = 50, int offset = 0, CancellationToken ct = default)
            => ValueTask.FromResult<IReadOnlyList<SessionInfo>>([_s]);
        public ValueTask<int> CountAsync(CancellationToken ct = default) => ValueTask.FromResult(1);
        public ValueTask RenameAsync(string id, string title, CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask DeleteAsync(string id, CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask SetModelAsync(string sessionId, string? modelId, string? reasoningLevel, CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask SetWorkspaceAsync(string sessionId, string? workspacePath, CancellationToken ct = default) => ValueTask.CompletedTask;
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

    private sealed class FakeCatalog : IModelCatalog
    {
        public IReadOnlyList<ModelInfo> Models => [
            new ModelInfo("m-pooled", "aiproxy", "Pooled", true, false, 131072),
            new ModelInfo("m-legacy", "aiproxy", "Legacy", true, false, 131072),
        ];
        public bool IsStale => false;
        public DateTimeOffset? LastRefreshedAt => DateTimeOffset.UtcNow;
        public ValueTask<IReadOnlyList<ModelInfo>> RefreshAsync(CancellationToken cancellationToken)
            => ValueTask.FromResult(Models);
    }

    private sealed class WsLogger : IPluginLogger
    {
        public static readonly System.Collections.Concurrent.ConcurrentQueue<string> Lines = new();
        public void Debug(string m) { }
        public void Information(string m) { Lines.Enqueue("I " + m); }
        public void Warning(string m) { Lines.Enqueue("W " + m); }
        public void Error(string m, Exception? e = null) { Lines.Enqueue("E " + m + " :: " + (e?.Message ?? "")); }
    }
}
