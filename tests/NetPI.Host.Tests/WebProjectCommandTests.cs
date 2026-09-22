using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using NetPI.Abstractions;
using NetPI.Web;
using NetPI.Host.Events;
using Xunit;

namespace NetPI.Host.Tests;

/// <summary>
/// astra-1 F: the Web hub's project-management and run-query commands. The plan's
/// Package F command table adds <c>project.list/create/update</c>,
/// <c>session.project.refresh</c>, <c>runs.list</c> and an optional
/// <c>runId</c> on <c>agent.cancel</c>; these drive the REAL WebApp over a real
/// loopback WebSocket (the pure-predicate tests can't see the command routing).
/// </summary>
public sealed class WebProjectCommandTests : IAsyncLifetime
{
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(10);

    private TestContext? _ctx;
    private WebApp? _app;
    private ClientWebSocket? _ws;
    private FakeRunner? _runner;
    private FakeProjectStore? _projects;
    private FakeResolver? _resolver;
    private FakeSessionStore? _sessions;
    private FakeOrchestrationStore? _orchStore;
    private int _port;

    // ---- the plugin context (the pattern from ProjectPendingApplyTests) --------

    private sealed class TestContext : IPluginContext
    {
        private readonly FixedServiceRegistry _services = new();
        private readonly RecordingBus _bus = new();
        public RecordingBus Bus => _bus;
        public FakeSessionStore? Sessions { get; set; }
        public PluginInfo Info => new("test", "test", "1.0.0");
        public IServiceRegistry Services => _services;
        public IEventBus Events => _bus;
        public ICommandRegistry Commands => new NetPI.Host.Services.CommandRegistry();
        public IWebPanelRegistry WebPanels => new NetPI.Host.Services.WebPanelRegistry();
        public System.Text.Json.JsonElement OwnConfig => System.Text.Json.JsonDocument.Parse("{}").RootElement;
        public IPluginLogger Log => new NoopLogger();
        public IValueLease<object> LeaseSelf() => new NoopLease();
        public void Add(string id, object service) => _services.Put(id, service);

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

    /// <summary>Collects WebApp log lines; dumped to test output on failure so
    /// a swallowed server error is never invisible again.</summary>
    private sealed class WsLogger : IPluginLogger
    {
        public static readonly System.Collections.Concurrent.ConcurrentQueue<string> Lines = new();
        public void Debug(string m) { }
        public void Information(string m) { Lines.Enqueue("I " + m); }
        public void Warning(string m) { Lines.Enqueue("W " + m); }
        public void Error(string m, Exception? e = null) { Lines.Enqueue("E " + m + " :: " + (e?.Message ?? "")); }
    }

    /// <summary>Recording IAgentRunner — the cancel/run-query surface the new
    /// commands hit.</summary>
    private sealed class FakeRunner : IAgentRunner
    {
        public readonly List<string> CancelledRuns = [];
        public int CancelActiveCount;
        // astra-2 §13 test hooks: when true, StartRunAsync returns a QUEUED
        // disposition (full pool) with the given run id; the queued-cancel path
        // records the ids it cancels.
        public bool QueuedMode;
        public string? QueuedRunId;
        public readonly List<string> CancelledQueuedRuns = [];

        public bool IsRunning => false;
        public ValueTask<bool> SuspendRunAsync(string runId, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(false);
        public ValueTask<bool> RequeueRunAsync(AgentRunRequest request, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(false);

        public ValueTask<AgentRunStart> StartRunAsync(AgentRunRequest request, CancellationToken ct = default)
        {
            if (QueuedMode)
            {
                var rid = request.RunId ?? QueuedRunId ?? Guid.NewGuid().ToString("n");
                return ValueTask.FromResult(new AgentRunStart(request.SessionId, "Queued: waiting for a free lane.", rid, RunDisposition.Queued));
            }
            return ValueTask.FromResult(new AgentRunStart(request.SessionId, null, request.RunId));
        }
        public ValueTask CancelRunAsync(CancellationToken ct = default)
        {
            CancelActiveCount++;
            return ValueTask.CompletedTask;
        }
        public IReadOnlyList<RunInfo> ListRuns() => new[]
        {
            new RunInfo("run-1", "s1", "m1", AgentState.Idle,
                DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddMinutes(-4), RunState.Completed),
            new RunInfo("run-2", "s2", "m1", AgentState.CallingModel,
                DateTimeOffset.UtcNow.AddMinutes(-1), null, RunState.Running),
        };
        public RunInfo? GetRun(string runId) => ListRuns().FirstOrDefault(r => r.RunId == runId);
        public RunInfo? GetSessionRun(string sessionId) =>
            ListRuns().FirstOrDefault(r => r.SessionId == sessionId && r.Outcome == RunState.Running);
        public bool CancelRun(string runId)
        {
            if (ListRuns().Any(r => r.RunId == runId)) { CancelledRuns.Add(runId); return true; }
            return false;
        }
        public ValueTask<bool> CancelQueuedRun(string runId, CancellationToken cancellationToken = default)
        {
            lock (CancelledQueuedRuns) CancelledQueuedRuns.Add(runId);
            return ValueTask.FromResult(true);
        }
        public System.Threading.SemaphoreSlim SessionGate(string sessionId) => new(1, 1);
    }

    /// <summary>astra-2 §13: a minimal IOrchestrationStore that records durable
    /// assignment creation — the queued-send path persists a Queued row through
    /// <c>EnsureRootAgentAsync</c> + <c>CreateAssignmentAsync</c>. Unused members
    /// throw (the test only exercises the two above).</summary>
    private sealed class FakeOrchestrationStore : IOrchestrationStore
    {
        public readonly List<AgentAssignmentRow> Created = [];
        public int EnsureRootCount;
        public string? LastOperationId;
        public string? LastSessionId;

        public ValueTask<AgentIdentity> EnsureRootAgentAsync(string sessionId, string? teamId, string title, CancellationToken ct = default)
        {
            lock (Created) { EnsureRootCount++; }
            return ValueTask.FromResult(new AgentIdentity("agent-1", teamId, null, sessionId, title, false, DateTimeOffset.UtcNow));
        }
        public ValueTask<AgentAssignmentRow> CreateAssignmentAsync(string operationId, string agentId, string sessionId, string? teamId,
            string? parentAgentId, string? modelId, string? poolId, string? deploymentId, string title, string? briefRef, CancellationToken ct = default)
        {
            var row = new AgentAssignmentRow("a-new", agentId, teamId, sessionId, parentAgentId,
                AgentAssignmentLifecycle.Queued, AgentState.Idle, DeploymentExecutionMode.DirectCloud,
                poolId, null, deploymentId, modelId, title, DateTimeOffset.UtcNow, null, null, null);
            lock (Created) { Created.Add(row); LastOperationId = operationId; LastSessionId = sessionId; }
            return ValueTask.FromResult(row);
        }
        // ---- unused members ---------------------------------------------------
        public ValueTask<AgentSpawnOutcome> SpawnChildAsync(string operationId, string? parentAgentId, string? teamId, string modelId, string? poolId, string? deploymentId, string brief, string title, CancellationToken ct = default) => throw new NotImplementedException();
        public ValueTask<AgentIdentity?> GetAgentAsync(string agentId, CancellationToken ct = default) => throw new NotImplementedException();
        public ValueTask<AgentIdentity?> GetAgentBySessionAsync(string sessionId, CancellationToken ct = default) => throw new NotImplementedException();
        public ValueTask<bool> TransitionAsync(string assignmentId, int expectedVersion, AgentAssignmentLifecycle lifecycle, AgentState phase, string? poolId, string? laneId, string? deploymentId, string? reason, string? checkpointRef, CancellationToken ct = default) => throw new NotImplementedException();
        public ValueTask<AgentAssignmentRow?> GetAssignmentAsync(string assignmentId, CancellationToken ct = default) => throw new NotImplementedException();
        public ValueTask<AgentAssignmentRow?> GetNonterminalAsync(string sessionId, CancellationToken ct = default) => throw new NotImplementedException();
        public ValueTask<IReadOnlyList<AgentAssignmentRow>> ListNonterminalAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public ValueTask<IReadOnlyList<QueuedAdoptionInfo>> ListQueuedForAdoptionAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public ValueTask MarkToolBatchInFlightAsync(string assignmentId, string agentId, string sessionId, int transcriptCursor, string? toolCallIdsJson, CancellationToken ct = default) => throw new NotImplementedException();
        public ValueTask ClearToolBatchCheckpointAsync(string assignmentId, CancellationToken ct = default) => throw new NotImplementedException();
        public ValueTask<bool> HasToolBatchCheckpointAsync(string assignmentId, CancellationToken ct = default) => ValueTask.FromResult(false);
        public ValueTask<AgentAssignmentRow?> GetByRunIdAsync(string runId, CancellationToken ct = default) => throw new NotImplementedException();
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
    }

    /// <summary>In-memory IProjectStore — upsert by exact path (the canonical
    /// dedupe lives in the sqlite store; a plain equality check suffices here).</summary>
    private sealed class FakeProjectStore : IProjectStore
    {
        private readonly List<ProjectInfo> _projects = [];
        public ValueTask<IReadOnlyList<ProjectInfo>> ListAsync(CancellationToken ct = default)
            => ValueTask.FromResult<IReadOnlyList<ProjectInfo>>(_projects);
        public ValueTask<ProjectInfo?> GetAsync(string id, CancellationToken ct = default)
            => ValueTask.FromResult(_projects.FirstOrDefault(p => p.Id == id));
        public ValueTask<ProjectInfo?> GetByWorkspaceAsync(string workspacePath, CancellationToken ct = default)
            => ValueTask.FromResult(_projects.FirstOrDefault(p => p.WorkspacePath == workspacePath));
        public ValueTask<ProjectInfo> CreateAsync(string name, string workspacePath, CancellationToken ct = default)
        {
            var existing = _projects.FirstOrDefault(p => p.WorkspacePath == workspacePath);
            if (existing is not null) return ValueTask.FromResult(existing);
            var p = new ProjectInfo(Guid.NewGuid().ToString("N"), name, workspacePath,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
            _projects.Add(p);
            return ValueTask.FromResult(p);
        }
        public ValueTask RenameAsync(string id, string newName, CancellationToken ct = default)
        {
            var idx = _projects.FindIndex(p => p.Id == id);
            if (idx >= 0) _projects[idx] = _projects[idx] with { Name = newName, UpdatedAt = DateTimeOffset.UtcNow };
            return ValueTask.CompletedTask;
        }
        public ValueTask DeleteAsync(string id, CancellationToken ct = default)
        {
            _projects.RemoveAll(p => p.Id == id);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeResolver : IInstructionContextResolver
    {
        public int ResolveCount;
        public ValueTask<ProjectContextSnapshot> ResolveAsync(
            string projectId, string projectName, string workspace, CancellationToken ct = default)
        {
            ResolveCount++;
            var snap = new ProjectContextSnapshot(projectId, projectName, workspace,
                Array.Empty<string>(), "instructions:" + ResolveCount, "hash-" + ResolveCount,
                DateTimeOffset.UtcNow);
            return ValueTask.FromResult(snap);
        }
    }

    private sealed class FakeSessionStore : ISessionStore
    {
        public readonly Dictionary<string, SessionInfo?> Sessions = new();
        public readonly List<ProjectChangeRequest> ProjectSets = [];
        /// <summary>asta-1 F: persisted entries per session (for the streaming-snapshot
        /// cursor test). ReadAsync returns the latest <paramref name="count"/> of these.</summary>
        public readonly Dictionary<string, List<SessionEntry>> Entries = new();
        /// <summary>astra-1 G1: optional server-side search hook — the WS test asserts the
        /// <c>session.list { query }</c> routing, not SQLite's LIKE (covered in
        /// SessionSearchTests). Returns the matched sessions, or null to defer to the
        /// full listing (the interface default).</summary>
        public Func<string, IReadOnlyList<SessionInfo>>? SearchFilter;
        public ValueTask<SessionInfo> CreateAsync(string? workspacePath, CancellationToken ct = default)
            => ValueTask.FromResult(new SessionInfo("s1", workspacePath, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 0, null));
        public ValueTask<SessionInfo?> GetAsync(string id, CancellationToken ct = default)
            => ValueTask.FromResult(Sessions.TryGetValue(id, out var s) ? s : null);
        public ValueTask AppendAsync(SessionEntry entry, CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask<IReadOnlyList<SessionInfo>> ListAsync(int count = 50, int offset = 0, CancellationToken ct = default)
            => ValueTask.FromResult<IReadOnlyList<SessionInfo>>(Sessions.Values.Where(s => s is not null).Cast<SessionInfo>().ToArray());
        public ValueTask<int> CountAsync(CancellationToken ct = default) => ValueTask.FromResult(Sessions.Count);
        public ValueTask<IReadOnlyList<SessionInfo>> SearchAsync(string query, int count = 100, int offset = 0, CancellationToken ct = default)
        {
            var matched = SearchFilter?.Invoke(query);
            IReadOnlyList<SessionInfo> fallback = ListAsync(count, offset, ct).AsTask().GetAwaiter().GetResult();
            return ValueTask.FromResult<IReadOnlyList<SessionInfo>>(matched ?? fallback);
        }
        public ValueTask<int> SearchCountAsync(string query, CancellationToken ct = default)
            => ValueTask.FromResult(SearchFilter?.Invoke(query)?.Count ?? CountAsync(ct).AsTask().GetAwaiter().GetResult());
        public ValueTask RenameAsync(string id, string title, CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask DeleteAsync(string id, CancellationToken ct = default)
        {
            Sessions.Remove(id);
            return ValueTask.CompletedTask;
        }
        public ValueTask SetModelAsync(string sessionId, string? modelId, string? reasoningLevel, CancellationToken ct = default)
            => ValueTask.CompletedTask;
        public ValueTask SetWorkspaceAsync(string sessionId, string? workspacePath, CancellationToken ct = default)
            => ValueTask.CompletedTask;
        public ValueTask<IReadOnlyList<SessionEntry>> ReadAsync(string sessionId, int offset, int count, CancellationToken ct = default)
        {
            var all = Entries.TryGetValue(sessionId, out var list) ? list : [];
            var page = all.Skip(offset).Take(count).ToList();
            return ValueTask.FromResult<IReadOnlyList<SessionEntry>>(page);
        }
        public ValueTask<IReadOnlyList<SessionEntry>> ReadBeforeAsync(string sessionId, int beforeSequence, int count, CancellationToken ct = default)
            => ValueTask.FromResult<IReadOnlyList<SessionEntry>>([]);
        public ValueTask<IReadOnlyList<SessionEntry>> ReadAfterAsync(string sessionId, int afterSequence, int count, CancellationToken ct = default)
            => ValueTask.FromResult<IReadOnlyList<SessionEntry>>([]);
        public ValueTask<IReadOnlyList<SessionEntry>> ReadRecentAsync(string sessionId, int count, CancellationToken ct = default)
            => ValueTask.FromResult<IReadOnlyList<SessionEntry>>([]);
        public ValueTask<ProjectChangeResult> SetProjectAsync(ProjectChangeRequest change, CancellationToken ct = default)
        {
            ProjectSets.Add(change);
            Sessions[change.SessionId] = new SessionInfo(change.SessionId, change.Snapshot.WorkspacePath,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 0, null) { ProjectId = change.ProjectId };
            var entry = new SessionEntry(change.OperationId, change.SessionId, EntryKind.ProjectContext, null,
                JsonSerializer.SerializeToElement(change), DateTimeOffset.UtcNow);
            return ValueTask.FromResult(new ProjectChangeResult(Sessions[change.SessionId]!, entry));
        }
    }

    // ---- lifecycle --------------------------------------------------------------

    public async Task InitializeAsync()
    {
        _port = FindFreePort();
        _ctx = new TestContext();
        _runner = new FakeRunner();
        _projects = new FakeProjectStore();
        _resolver = new FakeResolver();
        _sessions = new FakeSessionStore();
        _ctx.Sessions = _sessions;
        _sessions.Sessions["s1"] = new SessionInfo("s1", null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 0, null);
        _ctx.Add("runner", _runner);
        _ctx.Add("projects", _projects);
        _ctx.Add("instruction-context", _resolver);
        _orchStore = new FakeOrchestrationStore();
        _ctx.Add("orchestration-store", _orchStore);
        _ctx.Add("sessions", _sessions);
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

    // ---- WS helpers -------------------------------------------------------------

    private async Task<ClientWebSocket> ConnectAsync()
    {
        var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{_port}/ws"), CancellationToken.None);
        _ws = ws;
        return ws;
    }

    /// <summary>Send one command. Every command carries a fresh <c>requestId</c>:
    /// the Web hub's ack/error helpers are no-ops without one, so a command
    /// without a requestId can never be matched or diagnosed.</summary>
    private static async Task SendCommandAsync(ClientWebSocket ws, string command, Dictionary<string, object?>? payload = null)
    {
        var json = new Dictionary<string, object?> { ["type"] = command, ["requestId"] = Guid.NewGuid().ToString("n") };
        if (payload is not null) json["payload"] = payload;
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(json));
        await ws.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
    }

    /// <summary>Receive one message, failing after <see cref="ReadTimeout"/>.
    /// <c>ClientWebSocket.ReceiveAsync</c> only takes a cancellation token, so
    /// the timeout is a linked CTS. Returns the message text, or null on close.</summary>
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

    /// <summary>Send one command and read frames until the expected <c>type</c>
    /// arrives (bootstrap / other frames are skipped). Returns that frame, cloned
    /// (the source document is disposed on loop iteration). <c>ack</c> frames carry
    /// no <c>payload</c> — callers that want one unwrap the <c>payload</c> property
    /// of data frames themselves.</summary>
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

    /// <summary>Send one command and read frames until an <c>error</c> arrives;
    /// return its message.</summary>
    private async Task<string> AwaitErrorAsync(ClientWebSocket ws, string command, Dictionary<string, object?>? payload = null)
    {
        await SendCommandAsync(ws, command, payload);
        while (true)
        {
            var msg = await ReceiveMessageAsync(ws);
            if (msg is null) { DumpServerLog(); Assert.Fail("closed before error"); }
            using var doc = JsonDocument.Parse(msg);
            if (doc.RootElement.GetProperty("type").GetString() == "error")
                return doc.RootElement.GetProperty("payload").GetProperty("message").GetString()!;
        }
    }

    // ---- tests ------------------------------------------------------------------

    [Fact]
    public async Task RunsList_ReturnsAllRuns_FromEverySession()
    {
        var ws = await ConnectAsync();
        var resp = await AwaitAsync(ws, "runs.list", null, "runs.list");
        var runs = resp.GetProperty("payload").GetProperty("runs");
        Assert.Equal(2, runs.GetArrayLength());
        Assert.Contains(runs.EnumerateArray(), r => r.GetProperty("runId").GetString() == "run-1");
        Assert.Contains(runs.EnumerateArray(), r => r.GetProperty("runId").GetString() == "run-2");
    }

    [Fact]
    public async Task AgentCancel_WithRunId_CancelsThatRun_NotTheActiveOne()
    {
        var ws = await ConnectAsync();
        await AwaitAsync(ws, "agent.cancel", new() { ["runId"] = "run-2" }, "ack");
        Assert.Equal(new[] { "run-2" }, _runner!.CancelledRuns);
        Assert.Equal(0, _runner.CancelActiveCount); // the legacy path must NOT fire
    }

    [Fact]
    public async Task AgentCancel_WithoutRunId_UsesTheLegacyActiveCancel()
    {
        var ws = await ConnectAsync();
        await AwaitAsync(ws, "agent.cancel", null, "ack");
        Assert.Equal(1, _runner!.CancelActiveCount);
        Assert.Empty(_runner.CancelledRuns);
    }

    [Fact]
    public async Task SessionList_Query_SearchesServerSide_NotJustLoadedPages()
    {
        // astra-1 G1: `session.list { query }` routes to the store's server-side
        // search over ALL stored sessions (the picker must not search loaded pages
        // only). SQLite's LIKE itself is covered by SessionSearchTests; this
        // asserts the WS routing + the query/total response shape.
        var ws = await ConnectAsync();
        var store = _sessions!;
        store.Sessions["s-search-1"] = new SessionInfo("s-search-1", null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 0, "Astra parity");
        store.Sessions["s-search-2"] = new SessionInfo("s-search-2", null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 0, "other topic");
        store.SearchFilter = q =>
            store.Sessions.Values.Where(s => s is not null)
                .Where(s => (s!.Title ?? "").Contains(q, System.StringComparison.OrdinalIgnoreCase))
                .Cast<SessionInfo>().ToList();

        // The bootstrap sends its own (queryless) session.list at connect; read
        // frames until the one answering THIS command arrives (carries query).
        await ws.SendAsync(System.Text.Encoding.UTF8.GetBytes(
            $"{{\"type\":\"session.list\",\"requestId\":\"search-1\",\"payload\":{{\"query\":\"Astra\"}}}}"),
            WebSocketMessageType.Text, true, CancellationToken.None);
        var payload = default(System.Text.Json.JsonElement);
        for (var i = 0; i < 10 && payload.ValueKind == System.Text.Json.JsonValueKind.Undefined; i++)
        {
            var msg = await ReceiveMessageAsync(ws);
            if (msg is null) Assert.Fail("connection closed");
            using var doc = System.Text.Json.JsonDocument.Parse(msg);
            var type = doc.RootElement.GetProperty("type").GetString();
            if (type == "session.list" && doc.RootElement.GetProperty("payload").TryGetProperty("query", out var q)
                && q.GetString() == "Astra")
            {
                payload = doc.RootElement.GetProperty("payload").Clone();
            }
        }
        Assert.NotEqual(System.Text.Json.JsonValueKind.Undefined, payload.ValueKind);
        var sessions = payload.GetProperty("sessions");
        Assert.Equal(1, sessions.GetArrayLength());
        Assert.Equal("s-search-1", sessions.EnumerateArray().Single().GetProperty("id").GetString());
        Assert.Equal(1, payload.GetProperty("total").GetInt32());

        // No query → the plain listing (all rows, no search semantics).
        var plain = await AwaitAsync(ws, "session.list", null, "session.list");
        var plainPayload = plain.GetProperty("payload");
        Assert.True(plainPayload.TryGetProperty("sessions", out _));
        Assert.True(plainPayload.TryGetProperty("query", out var pq) == false || string.IsNullOrEmpty(pq.GetString()),
            "plain listing must not carry a query");
        Assert.Equal(3, plainPayload.GetProperty("sessions").GetArrayLength());
    }

    [Fact]
    public async Task ProjectCreate_ThenList_ShowsIt_DedupedBySamePath()
    {
        var ws = await ConnectAsync();
        var created = await AwaitAsync(ws, "project.create",
            new() { ["name"] = "NetPI", ["workspacePath"] = @"C:\AI\Projects\NetPI" }, "project.created");
        var proj = created.GetProperty("payload").GetProperty("project");
        var id = proj.GetProperty("id").GetString()!;
        Assert.Equal("NetPI", proj.GetProperty("name").GetString());

        // Same path again → the SAME id (upsert), no duplicate row.
        var again = await AwaitAsync(ws, "project.create",
            new() { ["name"] = "netpi", ["workspacePath"] = @"C:\AI\Projects\NetPI" }, "project.created");
        Assert.Equal(id, again.GetProperty("payload").GetProperty("project").GetProperty("id").GetString());

        var list = await AwaitAsync(ws, "project.list", null, "project.list");
        var projects = list.GetProperty("payload").GetProperty("projects");
        Assert.Equal(1, projects.GetArrayLength());
    }

    [Fact]
    public async Task ProjectUpdate_RenamesTheProject()
    {
        var ws = await ConnectAsync();
        var p = await _projects!.CreateAsync("A", @"C:\a");
        await AwaitAsync(ws, "project.update", new() { ["id"] = p.Id, ["name"] = "B" }, "ack");
        Assert.Equal("B", (await _projects.GetAsync(p.Id))!.Name);
    }

    [Fact]
    public async Task SessionProjectRefresh_ResnapshotsAndReapplies()
    {
        var ws = await ConnectAsync();
        var p = await _projects!.CreateAsync("P", @"C:\p");
        _sessions!.Sessions["s1"] = new SessionInfo("s1", @"C:\p", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 0, null)
        { ProjectId = p.Id };

        await AwaitAsync(ws, "session.project.refresh",
            new() { ["sessionId"] = "s1", ["operationId"] = "op-1" }, "session.project.applied");

        Assert.Equal(1, _resolver!.ResolveCount);
        var set = _sessions.ProjectSets.Single();
        Assert.Equal(p.Id, set.ProjectId);
        Assert.Equal("instructions:1", set.Snapshot.EffectiveInstructions);
    }

    [Fact]
    public async Task SessionProjectRefresh_WithoutProject_ErrorsNotSucceeds()
    {
        var ws = await ConnectAsync();
        _sessions!.Sessions["s2"] = new SessionInfo("s2", null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 0, null);
        var err = await AwaitErrorAsync(ws, "session.project.refresh",
            new() { ["sessionId"] = "s2" });
        Assert.Contains("no project", err);
    }

    // ---- astra-1 F: session.open streaming snapshot + reconcile cursor --------

    /// <summary>Feed the WebApp's OnAgentEvent handler with one ModelStreamEvent
    /// payload (drives the streaming batchers through the real bus, synchronously).</summary>
    private static async Task StreamEventAsync(TestContext ctx, string sid, string kind, string? text = null, string? runId = null)
    {
        var payload = text is null
            ? JsonSerializer.SerializeToElement(new { kind } as object)
            : JsonSerializer.SerializeToElement(new { kind, text } as object);
        await ctx.Bus.PublishAsync(new AgentEvent(
            Guid.NewGuid().ToString("n"), AgentEventType.ModelStreamEvent,
            DateTimeOffset.UtcNow, sid, payload, runId), CancellationToken.None);
    }


    [Fact]
    public async Task SessionOpen_ReturnsStreamingSnapshot_AndReconcilesInFlightDelta()
    {
        // Connect while the store is EMPTY: the connect-time bootstrap then
        // replays nothing (no session.list rows → no session.entries replay).
        // ClientWebSocket.ReceiveAsync ABORTS the socket when a read is
        // cancelled, so these tests never drain with short-timeout reads.
        var store = _sessions!;
        store.Entries.Clear();
        store.Sessions.Clear();
        var ws = await ConnectAsync();

        // Now seed the session: ONE persisted user entry (seq 1) so the cursor
        // is meaningful.
        var user = new SessionEntry("e1", "s1", EntryKind.Message,
            new AgentMessage("m1", MessageRole.User, new MessagePart[] { new TextPart("hi") }, DateTimeOffset.UtcNow),
            null, DateTimeOffset.UtcNow, 1);
        store.Entries["s1"] = [user];
        store.Sessions["s1"] = new SessionInfo("s1", null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 1, "t");

        // Turn is in flight: a model turn started, and a text delta is in the
        // batcher (or already flushed by the 20 ms timer — both paths are
        // covered by the reconciliation invariant below).
        await StreamEventAsync(_ctx!, "s1", "model-started");
        await StreamEventAsync(_ctx!, "s1", "text-delta", "hello ");

        // Open the session — must return the persisted entry PLUS a streaming
        // snapshot of the in-flight assistant text (persistence alone cannot
        // reconstruct an unfinished streamed message).
        var entries = await AwaitAsync(ws, "session.open", new() { ["sessionId"] = "s1" }, "session.entries");
        var payload = entries.GetProperty("payload");
        Assert.True(payload.TryGetProperty("streaming", out var s) && s.ValueKind != JsonValueKind.Null,
            "expected a streaming snapshot for the in-flight turn");
        var snapshot = s.GetProperty("text").GetString() ?? "";
        Assert.Equal("hello ", snapshot);
        // cursor = highest persisted sequence (1) = the reconcile point
        Assert.Equal(1, s.GetProperty("maxSequence").GetInt32());

        // The race, made deterministic: a further delta arrives for the SAME
        // in-flight turn after the open, and model-completed forces a
        // synchronous flush (no reliance on the 20 ms timer). The in-flight
        // text must reach the client exactly once across snapshot + live
        // frames, and the terminal text.completed must equal the snapshot
        // plus the post-snapshot deltas.
        await StreamEventAsync(_ctx!, "s1", "text-delta", "world");
        await StreamEventAsync(_ctx!, "s1", "model-completed");

        var postOpenDeltas = new List<string>();
        string? completed = null;
        while (completed is null)
        {
            var msg = await ReceiveMessageAsync(ws);
            Assert.NotNull(msg);
            using var doc = JsonDocument.Parse(msg!);
            var type = doc.RootElement.GetProperty("type").GetString();
            if (type == "error")
                Assert.Fail("unexpected error: " + doc.RootElement.GetProperty("payload").ToString());
            if (type == "text.delta")
                postOpenDeltas.Add(doc.RootElement.GetProperty("payload").GetProperty("text").GetString() ?? "");
            else if (type == "text.completed")
                completed = doc.RootElement.GetProperty("payload").GetProperty("text").GetString();
        }

        // No pre-snapshot text may be re-emitted after the open (the snapshot
        // consumed the unflushed buffer); the post-snapshot delta streams once.
        Assert.True(!postOpenDeltas.Any(d => d.Contains("hello")),
            $"snapshot text re-emitted after open: {string.Join("\n", postOpenDeltas)}");
        Assert.Contains("world", postOpenDeltas);
        // Reconciliation: terminal text = snapshot + post-snapshot deltas, exactly.
        Assert.Equal(snapshot + string.Concat(postOpenDeltas), completed);
        Assert.Equal("hello world", completed);
    }

    [Fact]
    public async Task SessionOpen_NoInFlightTurn_NoStreamingField()
    {
        // Empty-store connect (no bootstrap replay), then seed.
        var store = _sessions!;
        store.Entries.Clear();
        store.Sessions.Clear();
        var ws = await ConnectAsync();
        store.Entries["s1"] = [new SessionEntry("e1", "s1", EntryKind.Message,
            new AgentMessage("m1", MessageRole.User, new MessagePart[] { new TextPart("hi") }, DateTimeOffset.UtcNow),
            null, DateTimeOffset.UtcNow, 1)];
        store.Sessions["s1"] = new SessionInfo("s1", null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 1, "t");

        var entries = await AwaitAsync(ws, "session.open", new() { ["sessionId"] = "s1" }, "session.entries");
        var payload = entries.GetProperty("payload");
        // No turn in flight → streaming is absent (or null).
        Assert.True(!payload.TryGetProperty("streaming", out var s) || s.ValueKind == JsonValueKind.Null);
    }

    [Fact]
    public async Task ModelStreamEvent_FromOlderRun_IsIgnored()
    {
        // astra-1 F/#11: a DELAYED terminal wire event from an OLDER run must be
        // ignored. Realistic ordering (runs are cancelled before the next is
        // accepted): R1 streams "old ", is cancelled; R2 starts (discarding R1's
        // stale buffered text), streams "new ", completes; R1's delayed
        // terminal arrives last and must not re-emit or pollute anything.
        var store = _sessions!;
        store.Entries.Clear();
        store.Sessions.Clear();
        var ws = await ConnectAsync();
        store.Sessions["s1"] = new SessionInfo("s1", null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 0, "t");

        var open = await AwaitAsync(ws, "session.open", new() { ["sessionId"] = "s1" }, "session.entries");
        _ = open; // establish the connection; we assert on text.completed below

        // R1 streams, then is cancelled (its buffered "old " stays in the batcher
        // because a cancel delivers no model-completed boundary flush).
        await StreamEventAsync(_ctx!, "s1", "model-started", null, "R1");
        await StreamEventAsync(_ctx!, "s1", "text-delta", "old ", "R1");
        await RuntimeEventAsync(_ctx!, "s1", AgentEventType.AgentCancelled, "R1");

        // R2 starts: its model-started must DISCARD R1's stale text.
        await StreamEventAsync(_ctx!, "s1", "model-started", null, "R2");
        await StreamEventAsync(_ctx!, "s1", "text-delta", "new ", "R2");
        // R2's turn completes synchronously (boundary flush).
        await StreamEventAsync(_ctx!, "s1", "model-completed", null, "R2");

        string? completed = null;
        for (int i = 0; i < 10; i++)
        {
            var msg = await ReceiveMessageAsync(ws);
            if (msg is null) break;
            using var doc = JsonDocument.Parse(msg);
            var ftype = doc.RootElement.GetProperty("type").GetString();
            if (ftype == "error") continue;
            if (ftype == "text.completed")
            {
                completed = doc.RootElement.GetProperty("payload").GetProperty("text").GetString();
                break;
            }
        }
        // Only R2's text — R1's stale "old " was discarded, not merged.
        Assert.Equal("new ", completed);

        // R1's DELAYED terminal arrives after R2 completed. It must be dropped:
        // no further text frame may carry "old ".
        await StreamEventAsync(_ctx!, "s1", "model-completed", null, "R1");

        // Read the frames that MAY exist (a 20 ms delayed flush could fire with
        // R1's stale text — the bug this gate prevents). Bound the read to a
        // short budget: no frame within 60 ms means no leak. Read on the fresh
        // socket (the 10 s CTS read aborts the socket on expiry).
        var buffer = new byte[64 * 1024];
        var texts = new List<string>();
        for (int i = 0; i < 5; i++)
        {
            var read = ws.ReceiveAsync(buffer, CancellationToken.None);
            var finished = await Task.WhenAny(read, Task.Delay(100));
            if (finished != read) { await Task.Delay(50); continue; } // budget elapsed; poll again
            var result = await read;
            using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(buffer, 0, result.Count));
            if (result.MessageType == WebSocketMessageType.Close) break;
            var type = doc.RootElement.GetProperty("type").GetString();
            if (type is "text.delta" or "text.completed")
                texts.Add(doc.RootElement.GetProperty("payload").GetProperty("text").GetString() ?? "");
        }
        Assert.True(texts.All(t => !t.Contains("old")),
            $"delayed terminal from older run re-emitted stale text: {string.Join("; ", texts)}");
    }

    /// <summary>Publish a runtime-level event (e.g. AgentCancelled) with a RunId.
    /// Mirrors the agent runtime's real event shape (null payload).</summary>
    private static async Task RuntimeEventAsync(TestContext ctx, string sid, AgentEventType type, string? runId)
    {
        await ctx.Bus.PublishAsync(new AgentEvent(
            Guid.NewGuid().ToString("n"), type,
            DateTimeOffset.UtcNow, sid, null, runId), CancellationToken.None);
    }

    // ---- astra-2 §13: agents.state / agent.updated / lanes.state WS forwarding ---
    // A WS client must receive the orchestration state frames that WebApp forwards
    // from the event bus — not just the panel HTTP endpoints. This test publishes
    // an AgentLifecycleEvent and a LanesStateEvent on the real bus and asserts the
    // WS client receives the expected frame shapes.
    [Fact]
    public async Task OrchestrationEvents_ForwardedToWsClient_AsAgentsStateAndLanesState()
    {
        var ws = await ConnectAsync();
        // Wait for the first bootstrap frame — a frame can only be delivered to a
        // REGISTERED client, so this deterministically proves the pipe is open
        // before we publish (no timing window on the fan-out snapshot).
        var bootstrapped = false;
        while (!bootstrapped)
        {
            var probe = await ReceiveMessageAsync(ws);
            if (probe is null) { DumpServerLog(); Assert.Fail("WS closed during bootstrap"); }
            bootstrapped = true; // any frame means the client is registered
        }

        var now = DateTimeOffset.UtcNow;
        var rows = new[]
        {
            new AgentAssignmentRow("a-1", "agent-1", null, "s1", null,
                AgentAssignmentLifecycle.Running, AgentState.CallingModel,
                DeploymentExecutionMode.Pooled, "pool-1", "lane-1", "dep-1", "m1", "row one",
                now.AddMinutes(-5), now.AddMinutes(-5), null, null),
            new AgentAssignmentRow("a-2", "agent-2", null, "s2", null,
                AgentAssignmentLifecycle.Queued, AgentState.Idle,
                DeploymentExecutionMode.Pooled, "pool-1", null, "dep-1", "m1", "row two",
                now.AddMinutes(-3), null, null, "pool full"),
        };

        var pools = new[]
        {
            new AgentPoolSnapshot("pool-1", "dep-1", "m1", 1, 1, 2, true, null),
        };

        // Publish the lifecycle event (WebApp forwards agent.updated per row + agents.state).
        await _ctx!.Bus.PublishAsync(
            new AgentLifecycleEvent(AgentLifecycleEventKind.Batch, rows, null, now), CancellationToken.None);
        // Publish the lanes state event (WebApp forwards lanes.state).
        await _ctx.Bus.PublishAsync(
            new LanesStateEvent(pools, now), CancellationToken.None);

        // Give the fire-and-forget fan-out a beat to reach the client, then drain.
        // Collect frames until we see BOTH agents.state and lanes.state.
        var seen = new Dictionary<string, JsonElement>();
        while (seen.Count < 2)
        {
            var msg = await ReceiveMessageAsync(ws);
            if (msg is null) { DumpServerLog(); Assert.Fail("WS closed before agents.state/lanes.state"); }
            using var doc = JsonDocument.Parse(msg);
            var type = doc.RootElement.GetProperty("type").GetString()!;
            if (type == "agents.state" && !seen.ContainsKey("agents.state"))
                seen["agents.state"] = doc.RootElement.Clone();
            else if (type == "lanes.state" && !seen.ContainsKey("lanes.state"))
                seen["lanes.state"] = doc.RootElement.Clone();
            else if (type == "error")
                Assert.Fail("unexpected error: " + doc.RootElement.GetProperty("payload").ToString());
        }

        // agents.state: payload.agents[] with lifecycle + reason fields.
        var agents = seen["agents.state"].GetProperty("payload").GetProperty("agents");
        Assert.Equal(2, agents.GetArrayLength());
        var first = agents[0];
        Assert.Equal("a-1", first.GetProperty("assignmentId").GetString());
        Assert.Equal("running", first.GetProperty("lifecycle").GetString());
        Assert.Equal("pool full", agents[1].GetProperty("reason").GetString());

        // lanes.state: payload.pools[] with pool fields.
        var poolFrames = seen["lanes.state"].GetProperty("payload").GetProperty("pools");
        Assert.Equal(1, poolFrames.GetArrayLength());
        var p = poolFrames[0];
        Assert.Equal("pool-1", p.GetProperty("poolId").GetString());
        Assert.Equal(1, p.GetProperty("ownedCount").GetInt32());
        Assert.Equal(1, p.GetProperty("queueCount").GetInt32());
        Assert.Equal(2, p.GetProperty("targetCapacity").GetInt32());
    }

    // astra-2 §13/§16: a QUEUED send (full pool) is ACCEPTED, not rejected — the
    // Web surface persists a durable Queued assignment, ACKs (never errors), and a
    // later `agent.cancel` by runId reaches the queued record (purging the queue).
    [Fact]
    public async Task ChatSend_WhenPoolFull_IsAcceptedQueue_NotError()
    {
        _runner!.QueuedMode = true;
        _runner.QueuedRunId = "run-q1";
        var ws = await ConnectAsync();

        // The send must ACK (not error) even though the pool is full; AwaitAsync
        // fails on any `error` frame, so a successful `ack` proves no rejection.
        await AwaitAsync(ws, "chat.send",
            new() { ["text"] = "a queued message", ["sessionId"] = "s1", ["operationId"] = "op-q1" }, "ack");

        // A durable Queued assignment was persisted: root agent + assignment row,
        // keyed by the operation id (the store's run_id == the runner's run id).
        Assert.Equal(1, _orchStore!.EnsureRootCount);
        Assert.Equal(1, _orchStore.Created.Count);
        Assert.Equal("op-q1", _orchStore.LastOperationId);
        Assert.Equal("s1", _orchStore.LastSessionId);
        Assert.Equal(AgentAssignmentLifecycle.Queued, _orchStore.Created[0].Lifecycle);
        // No legacy cancel fired (the run was queued, not live).
        Assert.Equal(0, _runner.CancelActiveCount);
        Assert.Empty(_runner.CancelledQueuedRuns);

        // agent.cancel by runId reaches the QUEUED record (purge path), not the
        // legacy active-cancel.
        await AwaitAsync(ws, "agent.cancel", new() { ["runId"] = "run-q1" }, "ack");
        Assert.Contains("run-q1", _runner.CancelledQueuedRuns);
        Assert.Equal(0, _runner.CancelActiveCount); // still no legacy cancel
    }
}
