using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace NetPI.Agent.Tests;

public sealed class Disposer(Action action) : IDisposable
{
    private int _done;
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _done, 1) == 0) action();
    }
}

// ------------------------------------------------------------------ services

public sealed class FakeServices : IServiceRegistry
{
    public Action<Type>? Changed { get; set; }
    private readonly Lock _gate = new();
    private readonly List<(object Instance, int Priority, long Seq)> _items = [];
    private long _seq;

    public IDisposable Register<T>(T instance, int priority = 0) where T : class
    {
        var entry = (Instance: (object)instance, Priority: priority, Seq: Interlocked.Increment(ref _seq));
        lock (_gate) _items.Add(entry);
        Changed?.Invoke(typeof(T));
        return new Disposer(() => { lock (_gate) _items.Remove(entry); Changed?.Invoke(typeof(T)); });
    }

    public T? Get<T>() where T : class => GetAll<T>().FirstOrDefault();

    public IReadOnlyList<T> GetAll<T>() where T : class
    {
        lock (_gate)
            return _items.Where(i => i.Instance is T).OrderByDescending(i => i.Priority).ThenByDescending(i => i.Seq).Select(i => (T)i.Instance).ToList();
    }
}

// ------------------------------------------------------------------ event bus (async dispatcher like the host)

public sealed class FakeBus : IEventBus, IDisposable
{
    private readonly Lock _gate = new();
    private readonly List<(string Pattern, Func<BusEvent, ValueTask> Handler)> _subs = [];
    private readonly List<BusEvent> _all = [];
    private readonly Channel<BusEvent> _queue = Channel.CreateUnbounded<BusEvent>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Task _pump;
    private long _seq;
    private long _dispatched;

    public FakeBus()
    {
        _pump = Task.Run(async () =>
        {
            await foreach (var evt in _queue.Reader.ReadAllAsync())
            {
                List<(string Pattern, Func<BusEvent, ValueTask> Handler)> subs;
                lock (_gate) subs = [.. _subs];
                foreach (var (pattern, handler) in subs)
                {
                    if (!Matches(pattern, evt.Type)) continue;
                    try { await handler(evt); }
                    catch (Exception ex) { Console.WriteLine($"    [bus] handler for {evt.Type} failed: {ex.Message}"); }
                }
                Interlocked.Increment(ref _dispatched);
            }
        });
    }

    private static bool Matches(string pattern, string type) =>
        pattern == "*" || pattern == type || (pattern.EndsWith(".*", StringComparison.Ordinal) && type.StartsWith(pattern[..^1], StringComparison.Ordinal));

    public void Publish(BusEvent evt)
    {
        lock (_gate)
        {
            evt.Seq = ++_seq;
            _all.Add(evt);
        }
        _queue.Writer.TryWrite(evt);
    }

    public IDisposable Subscribe(string pattern, Action<BusEvent> handler) =>
        SubscribeAsync(pattern, e => { handler(e); return ValueTask.CompletedTask; });

    public IDisposable SubscribeAsync(string pattern, Func<BusEvent, ValueTask> handler)
    {
        var entry = (pattern, handler);
        lock (_gate) _subs.Add(entry);
        return new Disposer(() => { lock (_gate) _subs.Remove(entry); });
    }

    public IReadOnlyList<BusEvent> Recent(int max = 200)
    {
        lock (_gate) return _all.TakeLast(max).ToList();
    }

    public List<BusEvent> All
    {
        get { lock (_gate) return [.. _all]; }
    }

    public List<BusEvent> OfType(string type) => All.Where(e => e.Type == type).ToList();

    /// <summary>JSON view of an event payload.</summary>
    public static JsonObject Data(BusEvent e) => (NetPiJson.ToNode(e.Data) as JsonObject) ?? new JsonObject();

    /// <summary>Wait until every event published so far has been dispatched.</summary>
    public async Task DrainAsync()
    {
        long target;
        lock (_gate) target = _seq;
        await Wait.Until(() => Interlocked.Read(ref _dispatched) >= target, "bus drained", 5000);
    }

    public void Dispose() => _queue.Writer.TryComplete();
}

// ------------------------------------------------------------------ settings

public sealed class FakeSettings(IEventBus bus) : ISettings
{
    private readonly Lock _gate = new();
    private JsonObject _root = new();
    public string FilePath => "memory://settings.json";

    public JsonObject Snapshot()
    {
        lock (_gate) return (JsonObject)_root.DeepClone();
    }

    public JsonNode? GetNode(string path)
    {
        lock (_gate)
        {
            JsonNode? node = _root;
            foreach (var seg in path.Split('.'))
                if (node is not JsonObject o || !o.TryGetPropertyValue(seg, out node)) return null;
            return node?.DeepClone();
        }
    }

    public T? Get<T>(string path, T? defaultValue = default)
    {
        var n = GetNode(path);
        if (n is null) return defaultValue;
        try { return n.Deserialize<T>(NetPiJson.Options) ?? defaultValue; } catch { return defaultValue; }
    }

    /// <summary>Set without publishing (test setup before plugins start).</summary>
    public void SetQuiet(string path, JsonNode? value)
    {
        lock (_gate)
        {
            var segs = path.Split('.');
            var o = _root;
            foreach (var seg in segs[..^1])
            {
                if (o[seg] is not JsonObject child) { child = new JsonObject(); o[seg] = child; }
                o = child;
            }
            o[segs[^1]] = value;
        }
    }

    public void Set(string path, JsonNode? value)
    {
        SetQuiet(path, value);
        bus.Publish(EventTypes.SettingsChanged, new JsonObject { ["path"] = path });
    }

    public void Replace(JsonObject root)
    {
        lock (_gate) _root = root;
        bus.Publish(EventTypes.SettingsChanged, new JsonObject());
    }
}

// ------------------------------------------------------------------ tools / rpc

public sealed class FakeTools(ISettings settings) : IToolRegistry
{
    private readonly Lock _gate = new();
    private readonly List<(IAgentTool Tool, int Priority, string PluginId)> _items = [];

    public IDisposable Register(IAgentTool tool, int priority = 0) => Register(tool, priority, "test");

    public IDisposable Register(IAgentTool tool, int priority, string pluginId)
    {
        var entry = (tool, priority, pluginId);
        lock (_gate) _items.Add(entry);
        return new Disposer(() => { lock (_gate) _items.Remove(entry); });
    }

    public IReadOnlyList<IAgentTool> All
    {
        get
        {
            var disabled = settings.Get<List<string>>("tools.disabled") ?? [];
            lock (_gate)
                return _items.GroupBy(i => i.Tool.Definition.Name)
                    .Select(g => g.OrderByDescending(i => i.Priority).First().Tool)
                    .Where(t => !disabled.Contains(t.Definition.Name))
                    .ToList();
        }
    }

    public IAgentTool? Get(string name) => All.FirstOrDefault(t => t.Definition.Name == name);

    public IReadOnlyList<ToolRegistration> Registrations
    {
        get { lock (_gate) return _items.Select(i => new ToolRegistration(i.Tool, i.PluginId, i.Priority)).ToList(); }
    }
}

public sealed class FakeRpc : IRpcRegistry
{
    private readonly ConcurrentDictionary<string, (RpcHandler Handler, string PluginId, bool ReadOnly)> _handlers = new();

    public IDisposable Register(string method, RpcHandler handler, string? description = null) => RegisterFor(method, handler, "test", false);
    public IDisposable Register(string method, RpcHandler handler, string? description, bool readOnly) => RegisterFor(method, handler, "test", readOnly);

    public IDisposable RegisterFor(string method, RpcHandler handler, string pluginId, bool readOnly = false)
    {
        _handlers[method] = (handler, pluginId, readOnly);
        return new Disposer(() => _handlers.TryRemove(method, out _));
    }

    public async Task<object?> InvokeAsync(string method, object? parameters = null, CancellationToken ct = default)
    {
        if (!_handlers.TryGetValue(method, out var h)) throw new RpcException("not_found", $"No method {method}");
        var p = parameters is null ? JsonDocument.Parse("{}").RootElement : NetPiJson.ToElement(parameters);
        return await h.Handler(new RpcRequest { Method = method, Params = p }, ct);
    }

    /// <summary>Invoke and return the result as JSON (what the UI would receive).</summary>
    public async Task<JsonNode?> CallAsync(string method, object? parameters = null) => NetPiJson.ToNode(await InvokeAsync(method, parameters));

    public IReadOnlyList<RpcMethodInfo> List() => _handlers.Select(kv => new RpcMethodInfo(kv.Key, null, kv.Value.PluginId, kv.Value.ReadOnly)).ToList();
    public bool Exists(string method) => _handlers.ContainsKey(method);
}

// ------------------------------------------------------------------ session store

public sealed class FakeSessionStore(IEventBus bus, string workspace) : ISessionStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, ProjectInfo> _projects = [];
    private readonly Dictionary<string, SessionInfo> _sessions = [];
    private readonly Dictionary<string, List<ChatMessage>> _messages = [];
    private long _nextMessageId;
    public int UpdateMessageCalls;

    public IReadOnlyList<ProjectInfo> ListProjects() { lock (_gate) return [.. _projects.Values]; }
    public ProjectInfo? GetProject(string id) { lock (_gate) return _projects.GetValueOrDefault(id); }

    public ProjectInfo CreateProject(string name, string path)
    {
        var p = new ProjectInfo { Id = Ids.New("prj"), Name = name, Path = path, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
        lock (_gate) _projects[p.Id] = p;
        return p;
    }

    public ProjectInfo UpdateProject(string id, string? name, string? path)
    {
        lock (_gate)
        {
            var p = _projects[id];
            if (name is not null) p.Name = name;
            if (path is not null) p.Path = path;
            return p;
        }
    }

    public void DeleteProject(string id) { lock (_gate) _projects.Remove(id); }

    public IReadOnlyList<SessionInfo> ListSessions(SessionQuery query)
    {
        lock (_gate)
            return _sessions.Values.Where(s => (query.IncludeSubagents || s.Kind != "subagent")
                                             && (query.ParentSessionId is null || s.ParentSessionId == query.ParentSessionId))
                .OrderByDescending(s => s.UpdatedAt).ToList();
    }

    public SessionInfo? GetSession(string id) { lock (_gate) return _sessions.GetValueOrDefault(id); }

    public SessionInfo CreateSession(SessionInfo template)
    {
        var s = template;
        if (string.IsNullOrEmpty(s.Id)) s.Id = Ids.New("ses");
        s.CreatedAt = s.UpdatedAt = DateTimeOffset.UtcNow;
        lock (_gate)
        {
            _sessions[s.Id] = s;
            _messages[s.Id] = [];
        }
        bus.Publish(EventTypes.SessionCreated, new JsonObject { ["session"] = NetPiJson.ToNode(s) });
        return s;
    }

    public SessionInfo UpdateSession(string id, Action<SessionInfo> mutate)
    {
        SessionInfo s;
        IReadOnlyDictionary<string, string> wasMeta;
        lock (_gate)
        {
            s = _sessions[id];
            wasMeta = SessionMeta.Snapshot(s.Meta);  // the write edits this very object
            mutate(s);
            s.UpdatedAt = DateTimeOffset.UtcNow;
        }
        bus.Publish(EventTypes.SessionUpdated, new JsonObject { ["session"] = NetPiJson.ToNode(s) });
        // the real store publishes this too: a plugin that watches it must not see less here than in production
        var keys = SessionMeta.Changed(wasMeta, s.Meta);
        if (keys.Count > 0)
            bus.Publish(EventTypes.SessionChanged, new JsonObject { ["sessionId"] = id, ["keys"] = new JsonArray(keys.Select(k => (JsonNode?)k).ToArray()) });
        return s;
    }

    public void DeleteSession(string id) { lock (_gate) { _sessions.Remove(id); _messages.Remove(id); } }

    /// <summary>Like the host: store the switch and publish session.project (plugins append what the model is told).</summary>
    public SessionInfo SetSessionProject(string sessionId, string? projectId)
    {
        if (GetSession(sessionId)?.ProjectId == projectId) return GetSession(sessionId)!;
        var s = UpdateSession(sessionId, x => x.ProjectId = projectId);
        bus.Publish(EventTypes.SessionProject, new JsonObject { ["sessionId"] = sessionId, ["projectId"] = projectId, ["cwd"] = GetCwd(s) });
        return s;
    }

    public string GetCwd(SessionInfo session)
    {
        var p = session.ProjectId is null ? null : GetProject(session.ProjectId);
        return p?.Path ?? workspace;
    }

    public ChatMessage AppendMessage(string sessionId, ChatMessage message)
    {
        lock (_gate)
        {
            if (!_messages.TryGetValue(sessionId, out var list)) throw new KeyNotFoundException($"No session {sessionId}");
            message.Id = ++_nextMessageId;
            message.Seq = list.Count == 0 ? 1 : list[^1].Seq + 1;
            message.SessionId = sessionId;
            list.Add(message);
            if (_sessions.TryGetValue(sessionId, out var s)) s.MessageCount = list.Count;
        }
        bus.Publish(new BusEvent
        {
            Type = EventTypes.MessageAdded,
            SessionId = sessionId,
            Data = new JsonObject { ["sessionId"] = sessionId, ["message"] = NetPiJson.ToNode(message) },
        });
        return message;
    }

    public void UpdateMessage(ChatMessage message)
    {
        Interlocked.Increment(ref UpdateMessageCalls);
        lock (_gate)
        {
            var list = _messages[message.SessionId];
            var i = list.FindIndex(m => m.Id == message.Id);
            if (i < 0) throw new KeyNotFoundException($"No message {message.Id}");
            list[i] = message;
        }
        bus.Publish(new BusEvent
        {
            Type = EventTypes.MessageUpdated,
            SessionId = message.SessionId,
            Data = new JsonObject { ["sessionId"] = message.SessionId, ["message"] = NetPiJson.ToNode(message) },
        });
    }

    public ChatMessage? GetMessage(long id)
    {
        lock (_gate) return _messages.Values.SelectMany(l => l).FirstOrDefault(m => m.Id == id);
    }

    public IReadOnlyList<ChatMessage> GetMessages(string sessionId, long? beforeSeq = null, int? limit = null)
    {
        lock (_gate)
        {
            IEnumerable<ChatMessage> q = _messages.GetValueOrDefault(sessionId) ?? [];
            if (beforeSeq is { } b) q = q.Where(m => m.Seq < b);
            var list = q.ToList();
            if (limit is { } l && list.Count > l) list = list[^l..];
            return list;
        }
    }

    public IReadOnlyList<ChatMessage> GetContextMessages(string sessionId)
    {
        lock (_gate)
        {
            var all = _messages.GetValueOrDefault(sessionId) ?? [];
            var summary = all.LastOrDefault(m => m.Role == MessageRole.Summary);
            var list = new List<ChatMessage>();
            if (summary is not null) list.Add(summary);
            list.AddRange(all.Where(m => !m.Compacted && m.Role != MessageRole.Summary));
            return list;
        }
    }

    public void MarkCompacted(string sessionId, long upToSeq)
    {
        lock (_gate)
            foreach (var m in _messages[sessionId].Where(m => m.Seq <= upToSeq)) m.Compacted = true;
    }

    public List<ChatMessage> Messages(string sessionId) => [.. GetMessages(sessionId)];
}

// ------------------------------------------------------------------ models

public delegate IAsyncEnumerable<ModelStreamEvent> ModelHandler(ModelRequest request, CancellationToken ct);

public sealed class FakeProvider(string id, bool isLocal, FakeCatalog catalog) : IModelProvider
{
    public string Id { get; } = id;
    public string DisplayName => Id;
    public bool IsLocal { get; } = isLocal;

    public Task<IReadOnlyList<ModelInfo>> ListModelsAsync(bool refresh, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ModelInfo>>(catalog.Cached.Where(m => m.Provider == Id).ToList());

    public IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken ct) => catalog.Invoke(request, ct);
}

public sealed class FakeCatalog(IServiceRegistry services) : IModelCatalog
{
    private readonly Lock _gate = new();
    private readonly List<ModelInfo> _models = [];
    private readonly Dictionary<string, FakeProvider> _providers = [];
    public ConcurrentQueue<ModelRequest> Requests { get; } = new();
    public int Calls => Requests.Count;

    /// <summary>The scripted model. Default: reply "ok".</summary>
    public ModelHandler Handler { get; set; } = (_, _) => Reply.Text("ok");

    public string? DefaultModelRef { get; set; }

    public void AddModel(ModelInfo model)
    {
        lock (_gate)
        {
            _models.Add(model);
            if (!_providers.ContainsKey(model.Provider)) _providers[model.Provider] = new FakeProvider(model.Provider, model.IsLocal, this);
        }
        DefaultModelRef ??= model.Ref;
    }

    public IReadOnlyList<ModelInfo> Cached { get { lock (_gate) return [.. _models]; } }

    /// <summary>Called by <see cref="ListAsync"/> (simulates a catalog that loads lazily).</summary>
    public Action? OnList { get; set; }

    public Task<IReadOnlyList<ModelInfo>> ListAsync(bool refresh = false, CancellationToken ct = default)
    {
        OnList?.Invoke();
        return Task.FromResult(Cached);
    }

    public Task<ModelInfo?> FindAsync(string modelRef, CancellationToken ct = default)
    {
        var all = Cached;
        var m = all.FirstOrDefault(x => string.Equals(x.Ref, modelRef, StringComparison.OrdinalIgnoreCase));
        if (m is null)
        {
            var byId = all.Where(x => string.Equals(x.Id, modelRef, StringComparison.OrdinalIgnoreCase)).ToList();
            if (byId.Count == 1) m = byId[0];
        }
        return Task.FromResult(m);
    }

    public IModelProvider? GetProvider(string providerId) { lock (_gate) return _providers.GetValueOrDefault(providerId); }
    public IReadOnlyList<IModelProvider> Providers { get { lock (_gate) return [.. _providers.Values]; } }

    internal IAsyncEnumerable<ModelStreamEvent> Invoke(ModelRequest request, CancellationToken ct)
    {
        Requests.Enqueue(request);
        return Handler(request, ct);
    }

    /// <summary>Normalize the transcript (like the host), then run the middleware pipeline to the provider.</summary>
    public IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken ct)
    {
        var normalized = new ModelRequest
        {
            CorrelationId = request.CorrelationId,
            Model = request.Model,
            SystemPrompt = request.SystemPrompt,
            Messages = ModelMessages.Normalize(request.Messages),
            Tools = request.Tools,
            ReasoningEffort = request.ReasoningEffort,
            MaxOutputTokens = request.MaxOutputTokens,
            Temperature = request.Temperature,
            SessionId = request.SessionId,
            AgentId = request.AgentId,
            Purpose = request.Purpose,
        };
        var provider = GetProvider(request.Model.Provider) ?? throw new ModelException($"No provider {request.Model.Provider}", false);
        ModelCallDelegate pipeline = provider.StreamAsync;
        foreach (var mw in services.GetAll<IModelMiddleware>().OrderByDescending(m => m.Order))
        {
            var next = pipeline;
            var m = mw;
            pipeline = (r, c) => m.InvokeAsync(r, next, c);
        }
        return pipeline(normalized, ct);
    }

    public async Task<ChatMessage> CompleteAsync(ModelRequest request, CancellationToken ct)
    {
        await foreach (var e in StreamAsync(request, ct))
            if (e is StreamCompleted c) return c.Message;
        throw new ModelException("no completion", false);
    }
}

/// <summary>Scripted model replies.</summary>
public static class Reply
{
    public static Usage DefaultUsage() => new() { InputTokens = 100, OutputTokens = 10 };

    public static ChatMessage Message(params MessagePart[] parts) => new()
    {
        Role = MessageRole.Assistant,
        Parts = [.. parts],
        Usage = DefaultUsage(),
        StopReason = parts.OfType<ToolCallPart>().Any() ? "tool_use" : "stop",
    };

    public static ToolCallPart Call(string name, object? args = null, string? id = null) => new()
    {
        Id = id ?? "call_" + Ids.Short(10),
        Name = name,
        Arguments = args is string s ? s : NetPiJson.Serialize(args ?? new { }),
    };

    /// <summary>Stream a message: deltas for every part, optionally a gate before completion, then StreamCompleted.</summary>
    public static async IAsyncEnumerable<ModelStreamEvent> Stream(ChatMessage final, Func<CancellationToken, Task>? gate = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.Yield();
        foreach (var part in final.Parts)
        {
            switch (part)
            {
                case ThinkingPart th:
                    yield return new ThinkingDelta(th.Text);
                    break;
                case TextPart t:
                    for (var i = 0; i < t.Text.Length; i += 4) yield return new TextDelta(t.Text.Substring(i, Math.Min(4, t.Text.Length - i)));
                    break;
                case ToolCallPart c:
                    yield return new ToolCallStarted(c.Id, c.Name);
                    yield return new ToolCallArgsDelta(c.Id, c.Arguments);
                    break;
            }
        }
        if (gate is not null) await gate(ct);
        ct.ThrowIfCancellationRequested();
        if (final.Usage is not null) yield return new UsageUpdate(final.Usage);
        yield return new StreamCompleted(final);
    }

    public static IAsyncEnumerable<ModelStreamEvent> Text(string text, Func<CancellationToken, Task>? gate = null) =>
        Stream(Message(new TextPart { Text = text }), gate);

    public static IAsyncEnumerable<ModelStreamEvent> Tools(params ToolCallPart[] calls) => Stream(Message([.. calls]));

    public static IAsyncEnumerable<ModelStreamEvent> Tool(string name, object? args = null) => Tools(Call(name, args));

    public static async IAsyncEnumerable<ModelStreamEvent> Fail(Exception ex)
    {
        await Task.Yield();
        throw ex;
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }

    /// <summary>
    /// The last user-visible text of the (normalized) request: the last User message, skipping the context notices
    /// (working directory, instruction files) that the harness appends before a session's first model call.
    /// </summary>
    public static string LastUser(ModelRequest r) => r.Messages.LastOrDefault(m => m.Role == MessageRole.User && !IsContextNotice(m))?.Text ?? "";

    public static bool IsContextNotice(ChatMessage m) =>
        m.Text.StartsWith("<system-notice kind=\"project\">", StringComparison.Ordinal)
        || m.Text.StartsWith("<system-notice kind=\"instructions\">", StringComparison.Ordinal)
        || m.Text.StartsWith("<system-notice kind=\"tools\">", StringComparison.Ordinal);

    /// <summary>Messages after the last assistant message (what is new this turn).</summary>
    public static List<ChatMessage> Tail(ModelRequest r)
    {
        var idx = -1;
        for (var i = r.Messages.Count - 1; i >= 0; i--)
            if (r.Messages[i].Role == MessageRole.Assistant) { idx = i; break; }
        return r.Messages.Skip(idx + 1).ToList();
    }

    public static bool HasToolResult(ModelRequest r) => r.Messages.LastOrDefault()?.Role == MessageRole.Tool;
}

// ------------------------------------------------------------------ fake tool

public sealed class FakeTool(string name, Func<ToolContext, JsonElement, CancellationToken, Task<ToolResult>> exec, bool readOnly = false, string category = "general",
    IReadOnlyList<string>? guidelines = null, string? help = null) : IAgentTool
{
    public int Calls;
    public ToolDefinition Definition { get; } = new()
    {
        Name = name,
        Description = $"Fake tool {name}. Does test things.",
        Help = help,
        ReadOnly = readOnly,
        Category = category,
        Label = name.ToUpperInvariant(),
        PromptGuidelines = guidelines ?? [$"Use {name} for testing."],
    };

    public Task<ToolResult> ExecuteAsync(ToolContext context, JsonElement args, CancellationToken ct)
    {
        Interlocked.Increment(ref Calls);
        return exec(context, args, ct);
    }
}

// ------------------------------------------------------------------ database

/// <summary>Database that does nothing (used when SQLite is not available).</summary>
public sealed class NullDatabase : IDatabase
{
    public int Execute(string sql, object? args = null) => 0;
    public long Insert(string sql, object? args = null) => 0;
    public T? Scalar<T>(string sql, object? args = null) => default;
    public List<T> Query<T>(string sql, object? args, Func<IDbRow, T> map) => [];
    public T? QuerySingle<T>(string sql, object? args, Func<IDbRow, T> map) => default;
    public T Transaction<T>(Func<IDatabase, T> work) => work(this);
    public void Transaction(Action<IDatabase> work) => work(this);
    public void Migrate(string scope, params string[] migrations) { }
}

// ------------------------------------------------------------------ logger

public sealed class ConsoleLogger(string category, bool verbose) : ILogger
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => verbose ? logLevel >= LogLevel.Debug : logLevel >= LogLevel.Error;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;
        Console.WriteLine($"    [{category} {logLevel}] {formatter(state, exception)}{(exception is null ? "" : " — " + exception.GetType().Name + ": " + exception.Message)}");
    }
}
