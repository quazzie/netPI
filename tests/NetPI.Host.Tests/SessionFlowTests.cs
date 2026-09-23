using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using NetPI.Abstractions;
using NetPI.Web;
using NetPI.Host.Events;
using Xunit;

namespace NetPI.Host.Tests;

/// <summary>
/// Session-flow redesign: draft-first creation (chat.send with no sessionId
/// creates the session AND attaches the project atomically before the run
/// starts — no orphan sessions, no model turn with an empty project),
/// first-prompt auto-title (regression), and end-of-turn run metrics
/// (persisted run_metrics entry + live session.entry at the terminal event).
/// Drives the REAL WebApp over a real loopback WebSocket.
/// </summary>
public sealed class SessionFlowTests : IAsyncLifetime
{
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(10);

    private TestContext? _ctx;
    private WebApp? _app;
    private ClientWebSocket? _ws;
    private FakeRunner? _runner;
    private FakeProjectStore? _projects;
    private FakeSessionStore? _sessions;
    private int _port;

    // ---- the plugin context (pattern from WebProjectCommandTests) -------------

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
        public JsonElement OwnConfig => JsonDocument.Parse("{}").RootElement;
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

    /// <summary>Records run starts IN ORDER (the project-attach-vs-run assertion).</summary>
    private sealed class FakeRunner : IAgentRunner
    {
        public readonly List<string> Order = [];
        public AgentRunRequest? LastStart;

        public bool IsRunning => false;
        public ValueTask<bool> SuspendRunAsync(string runId, CancellationToken ct = default) => ValueTask.FromResult(false);
        public ValueTask<bool> RequeueRunAsync(AgentRunRequest request, CancellationToken ct = default) => ValueTask.FromResult(false);
        public ValueTask<AgentRunStart> StartRunAsync(AgentRunRequest request, CancellationToken ct = default)
        {
            lock (Order)
            {
                Order.Add("run:" + request.SessionId);
                LastStart = request;
            }
            return ValueTask.FromResult(new AgentRunStart(request.SessionId, null, request.RunId));
        }
        public ValueTask CancelRunAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
        public IReadOnlyList<RunInfo> ListRuns() => Array.Empty<RunInfo>();
        public RunInfo? GetRun(string runId) => null;
        public RunInfo? GetSessionRun(string sessionId) => null;
        public bool CancelRun(string runId) => false;
        public ValueTask<bool> CancelQueuedRun(string runId, CancellationToken ct = default) => ValueTask.FromResult(true);
        public System.Threading.SemaphoreSlim SessionGate(string sessionId) => new(1, 1);
    }

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
            var p = new ProjectInfo("p1", name, workspacePath, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
            _projects.Add(p);
            return ValueTask.FromResult(p);
        }
        public ValueTask RenameAsync(string id, string newName, CancellationToken ct = default)
            => ValueTask.CompletedTask;
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
            return ValueTask.FromResult(new ProjectContextSnapshot(projectId, projectName, workspace,
                Array.Empty<string>(), "instructions", "hash", DateTimeOffset.UtcNow));
        }
    }

    private sealed class FakeSessionStore : ISessionStore
    {
        public readonly Dictionary<string, SessionInfo?> Sessions = new();
        public readonly List<string> Order = [];
        public readonly List<SessionEntry> Appended = [];
        public int CreateCount;

        public ValueTask<SessionInfo> CreateAsync(string? workspacePath, CancellationToken ct = default)
        {
            lock (Order) { Order.Add("create"); CreateCount++; }
            var s = new SessionInfo("s" + CreateCount, workspacePath, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 0, null);
            lock (Sessions) Sessions[s.Id] = s;
            return ValueTask.FromResult(s);
        }
        public ValueTask<SessionInfo?> GetAsync(string id, CancellationToken ct = default)
            => ValueTask.FromResult(Sessions.TryGetValue(id, out var s) ? s : null);
        public ValueTask AppendAsync(SessionEntry entry, CancellationToken ct = default)
        {
            lock (Appended) Appended.Add(entry);
            return ValueTask.CompletedTask;
        }
        public ValueTask<IReadOnlyList<SessionInfo>> ListAsync(int count = 50, int offset = 0, CancellationToken ct = default)
            => ValueTask.FromResult<IReadOnlyList<SessionInfo>>(Sessions.Values.Where(s => s is not null).Cast<SessionInfo>().ToArray());
        public ValueTask<int> CountAsync(CancellationToken ct = default) => ValueTask.FromResult(Sessions.Count);
        public ValueTask RenameAsync(string id, string title, CancellationToken ct = default)
        {
            lock (Order) Order.Add("rename:" + title);
            if (Sessions.TryGetValue(id, out var s) && s is not null)
                Sessions[id] = s with { Title = title };
            return ValueTask.CompletedTask;
        }
        public ValueTask DeleteAsync(string id, CancellationToken ct = default)
        {
            Sessions.Remove(id);
            return ValueTask.CompletedTask;
        }
        public ValueTask SetModelAsync(string sessionId, string? modelId, string? reasoningLevel, CancellationToken ct = default)
        {
            lock (Order) Order.Add("setmodel");
            return ValueTask.CompletedTask;
        }
        public ValueTask SetWorkspaceAsync(string sessionId, string? workspacePath, CancellationToken ct = default)
            => ValueTask.CompletedTask;
        public ValueTask<IReadOnlyList<SessionEntry>> ReadAsync(string sessionId, int offset, int count, CancellationToken ct = default)
            => ValueTask.FromResult<IReadOnlyList<SessionEntry>>(Appended.Where(e => e.SessionId == sessionId).Skip(offset).Take(count).ToList());
        public ValueTask<IReadOnlyList<SessionEntry>> ReadBeforeAsync(string sessionId, int beforeSequence, int count, CancellationToken ct = default)
            => ValueTask.FromResult<IReadOnlyList<SessionEntry>>([]);
        public ValueTask<IReadOnlyList<SessionEntry>> ReadAfterAsync(string sessionId, int afterSequence, int count, CancellationToken ct = default)
            => ValueTask.FromResult<IReadOnlyList<SessionEntry>>([]);
        public ValueTask<IReadOnlyList<SessionEntry>> ReadRecentAsync(string sessionId, int count, CancellationToken ct = default)
            => ValueTask.FromResult<IReadOnlyList<SessionEntry>>([]);
        public ValueTask<ProjectChangeResult> SetProjectAsync(ProjectChangeRequest change, CancellationToken ct = default)
        {
            lock (Order) Order.Add("setproject:" + change.ProjectId);
            var s = new SessionInfo(change.SessionId, change.Snapshot.WorkspacePath,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 0, null) { ProjectId = change.ProjectId };
            lock (Sessions) Sessions[change.SessionId] = s;
            return ValueTask.FromResult(new ProjectChangeResult(s,
                new SessionEntry(change.OperationId, change.SessionId, EntryKind.ProjectContext, null,
                    JsonSerializer.SerializeToElement(change), DateTimeOffset.UtcNow)));
        }
    }

    private sealed class NoopLogger : IPluginLogger
    {
        public void Debug(string m) { }
        public void Information(string m) { }
        public void Warning(string m) { }
        public void Error(string m, Exception? e = null) { }
    }

    // ---- lifecycle --------------------------------------------------------------

    public async Task InitializeAsync()
    {
        _port = FindFreePort();
        _ctx = new TestContext();
        _runner = new FakeRunner();
        _projects = new FakeProjectStore();
        _sessions = new FakeSessionStore();
        _ctx.Add("runner", _runner);
        _ctx.Add("projects", _projects);
        _ctx.Add("instruction-context", new FakeResolver());
        _ctx.Add("sessions", _sessions);
        _app = new WebApp(_ctx, _port,
            System.IO.Path.Combine(AppContext.BaseDirectory, "does-not-exist"), 1024 * 1024, new NoopLogger());
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

    private static async Task SendCommandAsync(ClientWebSocket ws, string command, Dictionary<string, object?>? payload = null)
    {
        var json = new Dictionary<string, object?> { ["type"] = command, ["requestId"] = Guid.NewGuid().ToString("n") };
        if (payload is not null) json["payload"] = payload;
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(json));
        await ws.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
    }

    private async Task<string?> ReceiveRawAsync(ClientWebSocket ws)
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

    /// <summary>Read frames until <paramref name="expectType"/> arrives; returns the
    /// frames seen so far (including the match, last).</summary>
    private async Task<List<(string Type, JsonElement Payload)>> ReadUntilAsync(ClientWebSocket ws, string expectType)
    {
        var seen = new List<(string Type, JsonElement Payload)>();
        while (true)
        {
            var raw = await ReceiveRawAsync(ws);
            if (raw is null) throw new TimeoutException("socket closed before " + expectType);
            var doc = JsonDocument.Parse(raw);
            using (doc)
            {
                var type = doc.RootElement.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
                var payload = doc.RootElement.TryGetProperty("payload", out var p) ? p.Clone() : default;
                seen.Add((type, payload));
                if (type == expectType) return seen;
            }
        }
    }

    private static JsonElement Wire(object anon) =>
        JsonSerializer.SerializeToElement(anon, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

    // ---- tests --------------------------------------------------------------------

    [Fact]
    public async Task ChatSend_WithoutSession_CreatesSession_AttachesProject_BeforeRunStarts()
    {
        await _projects!.CreateAsync("NetPI", "C:\\work\\netpi", CancellationToken.None);
        var ws = await ConnectAsync();
        await ReadUntilAsync(ws, "session.list"); // bootstrap settled

        await SendCommandAsync(ws, "chat.send", new()
        {
            ["text"] = "Do the thing",
            ["workspace"] = (object?)null,
            ["projectId"] = "p1",
            ["operationId"] = "op1",
        });
        var frames = await ReadUntilAsync(ws, "ack");
        var types = frames.Select(f => f.Type).ToList();

        // The created session is announced WITH its project (the draft adoption
        // renders the header immediately) — before the run starts.
        var created = frames.First(f => f.Type == "session.created");
        Assert.Equal("p1", created.Payload.GetProperty("project").GetProperty("id").GetString());
        Assert.Equal("C:\\work\\netpi", created.Payload.GetProperty("workspace").GetString());
        var sid = created.Payload.GetProperty("id").GetString()!;

        // Server-side order: create -> project attached -> (title) -> model -> run.
        Assert.Equal(new[] { "create", "setproject:p1", "rename:Do the thing", "setmodel", "run:" + sid }, _sessions!.Order);
        Assert.Equal(sid, _runner!.LastStart!.SessionId);
        Assert.Contains("session.created", types);
        Assert.Contains("agent.state", types);
    }

    [Fact]
    public async Task ChatSend_UnknownProject_RejectsBeforeCreatingAnything()
    {
        await ConnectAsync();
        await SendCommandAsync(_ws!, "chat.send", new()
        {
            ["text"] = "Do the thing",
            ["projectId"] = "does-not-exist",
        });
        var frames = await ReadUntilAsync(_ws, "error");
        Assert.Contains("unknown project", frames[^1].Payload.GetProperty("message").GetString());
        Assert.Equal(0, _sessions!.CreateCount);
        Assert.Empty(_runner!.Order);
    }

    [Fact]
    public async Task ChatSend_AutoTitlesFromFirstPrompt()
    {
        await ConnectAsync();
        await SendCommandAsync(_ws!, "chat.send", new()
        {
            ["text"] = "## Go through the session flow, I sometimes see old tabs coming back\n\nmore detail here",
            ["workspace"] = "C:\\work\\x",
        });
        await ReadUntilAsync(_ws, "ack");

        // Title: first line, Markdown markers stripped, whitespace normalized.
        // The line is 65 chars — over the 48-char cap, so 47 chars + ellipsis.
        var created = _sessions!.Sessions.Values.Single(s => s is not null && s!.WorkspacePath == "C:\\work\\x");
        Assert.Equal("Go through the session flow, I sometimes see ol…", created.Title);

        // The long-line regression: capped at 48 chars with a trailing ellipsis.
        var longLine = new string('a', 80);
        await SendCommandAsync(_ws!, "chat.send", new() { ["text"] = longLine, ["workspace"] = "C:\\work\\y" });
        await ReadUntilAsync(_ws, "ack");
        var longSession = _sessions.Sessions.Values.Single(s => s is not null && s!.WorkspacePath == "C:\\work\\y");
        Assert.Equal(48, longSession.Title!.Length);
        Assert.EndsWith("…", longSession.Title);
    }

    [Fact]
    public async Task RunMetrics_PersistedAtTerminalAndBroadcastLive()
    {
        _sessions!.Sessions["s1"] = new SessionInfo("s1", "C:\\work\\s1", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 0, null);
        var ws = await ConnectAsync();
        await ReadUntilAsync(ws, "session.list");

        var bus = _ctx!.Bus;
        var now = DateTimeOffset.UtcNow;
        await bus.PublishAsync(new AgentEvent(Guid.NewGuid().ToString("n"), AgentEventType.AgentStarting, now, "s1", null, null));
        await bus.PublishAsync(new AgentEvent(Guid.NewGuid().ToString("n"), AgentEventType.ModelStreamEvent, now, "s1",
            Wire(new { kind = "model-started", modelId = "m1" }), null));
        await bus.PublishAsync(new AgentEvent(Guid.NewGuid().ToString("n"), AgentEventType.ModelStreamEvent, now, "s1",
            Wire(new { kind = "usage-updated", promptTokens = 100, completionTokens = 50, totalTokens = 150, cachedTokens = 80 }), null));
        await bus.PublishAsync(new AgentEvent(Guid.NewGuid().ToString("n"), AgentEventType.BeforeToolCall, now, "s1",
            Wire(new { toolCallId = "t1", toolName = "bash" }), null));
        await Task.Delay(20); // measurable tool wall time
        await bus.PublishAsync(new AgentEvent(Guid.NewGuid().ToString("n"), AgentEventType.AfterToolCall, now, "s1",
            Wire(new { toolCallId = "t1", toolOutput = "ok", isError = false }), null));
        await bus.PublishAsync(new AgentEvent(Guid.NewGuid().ToString("n"), AgentEventType.ModelStreamEvent, now, "s1",
            Wire(new { kind = "model-completed" }), null));
        await bus.PublishAsync(new AgentEvent(Guid.NewGuid().ToString("n"), AgentEventType.AgentCompleted,
            now.AddSeconds(3), "s1", null, null));

        var frames = await ReadUntilAsync(ws, "session.entry");
        var entry = frames.Last(f => f.Type == "session.entry" && f.Payload.TryGetProperty("entry", out var e)
            && e.TryGetProperty("type", out var t) && t.GetString() == "run_metrics");
        var p = entry.Payload.GetProperty("entry");
        Assert.Equal("run_metrics", p.GetProperty("type").GetString());
        Assert.Equal(100, p.GetProperty("promptTokens").GetInt32());
        Assert.Equal(50, p.GetProperty("completionTokens").GetInt32());
        Assert.Equal(80, p.GetProperty("cachedTokens").GetInt32());
        Assert.Equal(1, p.GetProperty("toolCount").GetInt32());
        Assert.True(p.GetProperty("runElapsedMs").GetInt32() >= 2500); // 3s terminal timestamp
        Assert.True(p.GetProperty("modelElapsedMs").GetInt32() >= 0);

        // The persisted entry (replay path) carries the same numbers.
        var persisted = _sessions.Appended.Where(e => e.Kind == EntryKind.Metadata).Single();
        var root = JsonDocument.Parse(persisted.Payload!.ToString()).RootElement;
        Assert.Equal("run_metrics", root.GetProperty("kind").GetString());
        Assert.Equal(100, root.GetProperty("promptTokens").GetInt32());
        Assert.Equal(80, root.GetProperty("cachedTokens").GetInt32());
        Assert.Equal(1, root.GetProperty("toolCount").GetInt32());
    }
}
