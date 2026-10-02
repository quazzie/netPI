using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using NetPI.Host.Settings;
using NetPI.Host.Storage.Sqlite;

namespace NetPI.Aux.Tests;

// ------------------------------------------------------------------ mini test framework

public sealed class AssertException(string message) : Exception(message);

/// <summary>A test that could not run here (no git, no browser, nothing built): a skip ends the test,
/// is counted apart from the passes and prints a SKIP line scripts/test.ps1 counts. Not a pass, and
/// not a failure either: it says what was missing (idea-r4e8rf).</summary>
public sealed class SkipException(string reason) : Exception(reason);

public static class Check
{
    /// <summary>End the test as skipped, with the reason it could not run.</summary>
    [DoesNotReturn]
    public static void Skip(string reason) => throw new SkipException(reason);

    public static void True(bool condition, string message = "expected true", [CallerArgumentExpression(nameof(condition))] string? expr = null)
    {
        if (!condition) throw new AssertException($"{message} ({expr})");
    }

    public static void False(bool condition, string message = "expected false", [CallerArgumentExpression(nameof(condition))] string? expr = null)
    {
        if (condition) throw new AssertException($"{message} ({expr})");
    }

    public static void Equal<T>(T expected, T actual, string? message = null)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new AssertException($"{message ?? "not equal"}\n      expected: {Show(expected)}\n      actual:   {Show(actual)}");
    }

    public static void Differs<T>(T unexpected, T actual, string? message = null)
    {
        if (EqualityComparer<T>.Default.Equals(unexpected, actual))
            throw new AssertException($"{message ?? "values are equal"}: {Show(actual)}");
    }

    public static void Contains(string? haystack, string needle, string? message = null)
    {
        if (haystack is null || !haystack.Contains(needle, StringComparison.Ordinal))
            throw new AssertException($"{message ?? "substring not found"}: {Show(needle)}\n      in: {Show(haystack)}");
    }

    public static void NotContains(string? haystack, string needle, string? message = null)
    {
        if (haystack is not null && haystack.Contains(needle, StringComparison.Ordinal))
            throw new AssertException($"{message ?? "unexpected substring"}: {Show(needle)}\n      in: {Show(haystack)}");
    }

    public static async Task<TEx> ThrowsAsync<TEx>(Func<Task> body, string? message = null) where TEx : Exception
    {
        try { await body().ConfigureAwait(false); }
        catch (TEx ex) { return ex; }
        catch (Exception ex) { throw new AssertException($"{message ?? "wrong exception"}: expected {typeof(TEx).Name}, got {ex.GetType().Name}: {ex.Message}"); }
        throw new AssertException($"{message ?? "no exception"}: expected {typeof(TEx).Name}");
    }

    /// <summary>The exception a synchronous body threw, as an <see cref="AssertException"/> (so a lambda can stay a lambda).</summary>
    public static TEx Throws<TEx>(Action body, string? message = null) where TEx : Exception
    {
        try { body(); }
        catch (TEx ex) { return ex; }
        catch (Exception ex) { throw new AssertException($"{message ?? "wrong exception"}: expected {typeof(TEx).Name}, got {ex.GetType().Name}: {ex.Message}"); }
        throw new AssertException($"{message ?? "no exception"}: expected {typeof(TEx).Name}");
    }

    public static string Show(object? o)
    {
        var s = o?.ToString() ?? "null";
        s = s.Replace("\r", "\\r").Replace("\n", "\\n");
        return s.Length > 800 ? s[..800] + "…" : s;
    }
}

public sealed class TestRunner
{
    /// <summary>Exit code when the filter selected no test at all: a broken selection, not a pass (idea-jw74xi). The
    /// documented direct run is <c>dotnet &lt;suite&gt;.dll [filter]</c>, and it must not report green for a typo or a
    /// renamed test. scripts/test.ps1 maps a non-zero exit with no FAIL line to a failure.</summary>
    public const int NoTestSelected = 2;

    private readonly List<(string Name, Func<Task> Body)> _tests = [];

    public void Add(string name, Func<Task> body) => _tests.Add((name, body));
    public void Add(string name, Action body) => _tests.Add((name, () => { body(); return Task.CompletedTask; }));

    public async Task<int> RunAsync(string[] filters)
    {
        var selected = _tests.Where(t => filters.Length == 0 || filters.Any(f => t.Name.Contains(f, StringComparison.OrdinalIgnoreCase))).ToList();
        int passed = 0, failed = 0, skipped = 0;
        var failures = new List<string>();
        var total = Stopwatch.StartNew();
        foreach (var (name, body) in selected)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var task = body();
                if (await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(60))) != task)
                    throw new AssertException("test timed out after 60s");
                await task;
                passed++;
                Console.WriteLine($"  PASS  {name} ({sw.ElapsedMilliseconds}ms)");
            }
            catch (SkipException ex)
            {
                skipped++;
                Console.WriteLine($"  SKIP  {name} ({ex.Message})");
            }
            catch (Exception ex)
            {
                failed++;
                var msg = ex is AssertException ? ex.Message : ex.ToString();
                failures.Add($"{name}: {msg}");
                Console.WriteLine($"  FAIL  {name} ({sw.ElapsedMilliseconds}ms)\n        {msg.Replace("\n", "\n        ")}");
            }
        }
        Console.WriteLine();
        var nothing = selected.Count == 0;
        if (nothing) Console.WriteLine("No test matches the filter.");
        Console.WriteLine($"{passed} passed, {failed} failed, {skipped} skipped, {selected.Count} total in {total.Elapsed.TotalSeconds:0.0}s");
        if (failed > 0)
        {
            Console.WriteLine("Failures:");
            foreach (var f in failures) Console.WriteLine("  - " + f.Split('\n')[0]);
        }
        return failed > 0 ? 1 : nothing ? NoTestSelected : 0;
    }
}

// ------------------------------------------------------------------ fakes

/// <summary>Collects the disposables of everything a plugin registered, like the host does.</summary>
public sealed class Ownership
{
    private readonly List<IDisposable> _owned = [];
    public IDisposable Own(IDisposable d) { lock (_owned) _owned.Add(d); return d; }
    /// <summary>What has been registered, so a test can drive a plugin object the plugin itself owns.</summary>
    public IReadOnlyList<IDisposable> Owned { get { lock (_owned) return [.. _owned]; } }
    public void DisposeAll()
    {
        List<IDisposable> items;
        lock (_owned) { items = [.. _owned]; _owned.Clear(); }
        for (var i = items.Count - 1; i >= 0; i--) { try { items[i].Dispose(); } catch { } }
    }
}

public sealed class Disposer(Action action) : IDisposable
{
    private Action? _action = action;
    public void Dispose() => Interlocked.Exchange(ref _action, null)?.Invoke();
}

public sealed class FakeSettings : ISettings
{
    private JsonObject _root = new();
    public string FilePath => "(memory)";
    public bool InvalidOnDisk { get; set; }
    public string? InvalidOnDiskError { get; set; }
    public JsonObject Snapshot() => (JsonObject)_root.DeepClone();

    public JsonNode? GetNode(string path)
    {
        JsonNode? node = _root;
        foreach (var part in path.Split('.'))
            if (node is not JsonObject o || !o.TryGetPropertyValue(part, out node)) return null;
        return node;
    }

    public T? Get<T>(string path, T? defaultValue = default)
    {
        var node = GetNode(path);
        if (node is null) return defaultValue;
        try { return node.Deserialize<T>(NetPiJson.Options) ?? defaultValue; } catch { return defaultValue; }
    }

    public void Set(string path, JsonNode? value)
    {
        var parts = path.Split('.');
        var o = _root;
        foreach (var part in parts[..^1])
        {
            if (o[part] is not JsonObject child) { child = new JsonObject(); o[part] = child; }
            o = child;
        }
        o[parts[^1]] = value;
    }

    public void Replace(JsonObject root) => _root = root;
}

public sealed class FakeBus(Ownership? owner = null) : IEventBus
{
    private readonly List<(string Pattern, Action<BusEvent> Handler)> _subs = [];
    private long _seq;
    public ConcurrentQueue<BusEvent> Events { get; } = new();

    public void Publish(BusEvent evt)
    {
        evt.Seq = Interlocked.Increment(ref _seq);
        Events.Enqueue(evt);
        List<(string Pattern, Action<BusEvent> Handler)> subs;
        lock (_subs) subs = [.. _subs];
        foreach (var (p, h) in subs)
            if (p == "*" || p == evt.Type || (p.EndsWith(".*") && evt.Type.StartsWith(p[..^1], StringComparison.Ordinal))) h(evt);
    }

    public IDisposable Subscribe(string pattern, Action<BusEvent> handler)
    {
        var entry = (pattern, handler);
        lock (_subs) _subs.Add(entry);
        var d = new Disposer(() => { lock (_subs) _subs.Remove(entry); });
        owner?.Own(d);
        return d;
    }

    public IDisposable SubscribeAsync(string pattern, Func<BusEvent, ValueTask> handler) =>
        Subscribe(pattern, e => handler(e).AsTask().GetAwaiter().GetResult());

    public IReadOnlyList<BusEvent> Recent(int max = 200) => Events.TakeLast(max).ToList();
    public List<BusEvent> OfType(string type) => Events.Where(e => e.Type == type).ToList();

    public async Task<BusEvent?> WaitForAsync(string type, Func<BusEvent, bool>? predicate = null, int timeoutMs = 5000, int skip = 0)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            var hit = OfType(type).Skip(skip).FirstOrDefault(e => predicate is null || predicate(e));
            if (hit is not null) return hit;
            await Task.Delay(25);
        }
        return null;
    }
}

public sealed class FakeServices(Ownership? owner = null) : IServiceRegistry
{
    private readonly List<(Type Type, object Instance, int Priority)> _items = [];

    public IDisposable Register<T>(T instance, int priority = 0) where T : class
    {
        var entry = (typeof(T), (object)instance, priority);
        lock (_items) _items.Add(entry);
        var d = new Disposer(() => { lock (_items) _items.Remove(entry); });
        owner?.Own(d);
        return d;
    }

    public T? Get<T>() where T : class => GetAll<T>().FirstOrDefault();

    public IReadOnlyList<T> GetAll<T>() where T : class
    {
        lock (_items) return _items.Where(i => i.Type == typeof(T)).OrderByDescending(i => i.Priority).Select(i => (T)i.Instance).ToList();
    }
}

public sealed class FakeRpc(Ownership? owner = null) : IRpcRegistry
{
    public ConcurrentDictionary<string, RpcHandler> Handlers { get; } = new();
    /// <summary>What each method was registered as, so List() can report readOnly like the real registry (idea-de1s7t).</summary>
    public ConcurrentDictionary<string, bool> ReadOnly { get; } = new();

    public IDisposable Register(string method, RpcHandler handler, string? description = null) => Register(method, handler, description, false);

    public IDisposable Register(string method, RpcHandler handler, string? description, bool readOnly)
    {
        Handlers[method] = handler;
        ReadOnly[method] = readOnly;
        var d = new Disposer(() => { Handlers.TryRemove(method, out _); ReadOnly.TryRemove(method, out _); });
        owner?.Own(d);
        return d;
    }

    public Task<object?> InvokeAsync(string method, object? parameters = null, CancellationToken ct = default)
    {
        if (!Handlers.TryGetValue(method, out var h)) throw new RpcException("not_found", $"Unknown method {method}");
        var p = parameters is null ? default : NetPiJson.ToElement(parameters);
        return h(new RpcRequest { Method = method, Params = p }, ct);
    }

    public Task<object?> Call(string method, object? parameters = null) => InvokeAsync(method, parameters);

    public IReadOnlyList<RpcMethodInfo> List() => Handlers.Keys.Select(k => new RpcMethodInfo(k, null, "test", ReadOnly.GetValueOrDefault(k))).ToList();
    public bool Exists(string method) => Handlers.ContainsKey(method);
}

public sealed class FakeTools(Ownership? owner = null) : IToolRegistry
{
    public List<IAgentTool> Tools { get; } = [];
    /// <summary>Which plugin registered each tool (the owner, where a test needs one).</summary>
    public Dictionary<IAgentTool, string> Owners { get; } = [];

    public IDisposable Register(IAgentTool tool, int priority = 0) => Register(tool, "test");

    public IDisposable Register(IAgentTool tool, string pluginId)
    {
        lock (Tools) { Tools.Add(tool); Owners[tool] = pluginId; }
        var d = new Disposer(() => { lock (Tools) { Tools.Remove(tool); Owners.Remove(tool); } });
        owner?.Own(d);
        return d;
    }

    public IReadOnlyList<IAgentTool> All { get { lock (Tools) return [.. Tools]; } }
    public IAgentTool? Get(string name) => All.FirstOrDefault(t => t.Definition.Name == name);
    public IReadOnlyList<ToolRegistration> Registrations
    {
        get { lock (Tools) return [.. Tools.Select(t => new ToolRegistration(t, Owners.GetValueOrDefault(t) ?? "test", 0))]; }
    }
}

public sealed class FakeUi(Ownership? owner = null) : IUiRegistry
{
    public List<UiTabInfo> TabList { get; } = [];
    public List<SlashCommandInfo> CommandList { get; } = [];
    public IDisposable AddTab(UiTabInfo tab)
    {
        TabList.Add(tab);
        var d = new Disposer(() => TabList.Remove(tab));
        owner?.Own(d);
        return d;
    }
    public IDisposable AddCommand(SlashCommandInfo command)
    {
        CommandList.Add(command);
        var d = new Disposer(() => CommandList.Remove(command));
        owner?.Own(d);
        return d;
    }
    public IReadOnlyList<UiTabInfo> Tabs => TabList;
    public IReadOnlyList<SlashCommandInfo> Commands => CommandList;
}

public sealed class FakeHttp : IHttpRegistry
{
    public IDisposable Map(string path, Func<HttpContext, Task> handler) => new Disposer(() => { });
}

public sealed class ListLogger : ILogger
{
    public ConcurrentQueue<string> Lines { get; } = new();
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        Lines.Enqueue($"{logLevel}: {formatter(state, exception)}");
}

/// <summary>In-memory session store (summary-first context like the real store).</summary>
public sealed class FakeSessionStore : ISessionStore
{
    private long _seq, _id;
    public List<ProjectInfo> Projects { get; } = [];
    public List<SessionInfo> Sessions { get; } = [];
    public List<ChatMessage> Messages { get; } = [];
    public List<(string SessionId, long UpToSeq)> MarkCompactedCalls { get; } = [];
    public List<ChatMessage> Appended { get; } = [];
    public List<ChatMessage> Updated { get; } = [];

    public IReadOnlyList<ProjectInfo> ListProjects() => Projects;
    public ProjectInfo? GetProject(string id) => Projects.FirstOrDefault(p => p.Id == id);
    public ProjectInfo CreateProject(string name, string path)
    {
        var p = new ProjectInfo { Id = Ids.New("prj"), Name = name, Path = path, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
        Projects.Add(p);
        return p;
    }
    public ProjectInfo UpdateProject(string id, string? name, string? path) => throw new NotSupportedException();
    public void DeleteProject(string id) => Projects.RemoveAll(p => p.Id == id);

    /// <summary>Every session, except that an attached-key query is answered like the real service: the sessions whose meta holds that
    /// string, archived ones only when asked (the fake has no list window, and its sessions are all "stored").</summary>
    public IReadOnlyList<SessionInfo> ListSessions(SessionQuery query) =>
        query.AttachedKey is { Length: > 0 } key && query.AttachedValue is not null
            ? Sessions.Where(s => s.Meta?[key] is System.Text.Json.Nodes.JsonValue v && v.TryGetValue<string>(out var text) && text == query.AttachedValue
                                  && (query.IncludeArchived || !s.Archived)).ToList()
            : Sessions;
    public void DeclareForkReset(params string[] keys) { }
    public SessionInfo ForkSession(string sessionId, long upToSeq, SessionInfo template) => throw new NotSupportedException("this fake does not fork: use a real SessionService");
    public SessionInfo? GetSession(string id) => Sessions.FirstOrDefault(s => s.Id == id);
    public SessionInfo CreateSession(SessionInfo template)
    {
        if (string.IsNullOrEmpty(template.Id)) template.Id = Ids.New("ses");
        Sessions.Add(template);
        return template;
    }
    public SessionInfo UpdateSession(string id, Action<SessionInfo> mutate)
    {
        var s = GetSession(id) ?? throw new InvalidOperationException("no session");
        // This double publishes no session events at all (not session.updated either), so a plugin that reacts to
        // session.changed is not exercised here: that test belongs in NetPI.Agent.Tests, whose store mirrors the real
        // store's events, or it publishes the event by hand.
        mutate(s);
        return s;
    }
    public void DeleteSession(string id) => Sessions.RemoveAll(s => s.Id == id);
    public SessionInfo SetSessionProject(string sessionId, string? projectId) => UpdateSession(sessionId, s => s.ProjectId = projectId);
    /// <summary>The workspace root when the session is bound to one, else its project path — as the real store answers it.
    /// (The workspace records themselves live in the host store; the tests that bind sessions use a
    /// <c>MemoryWorkspaceStore</c> and pass the binding explicitly where the root matters.)</summary>
    public string GetCwd(SessionInfo session) =>
        SessionCwd.Of(session) is { } own ? own
        : SessionWorkspace.Of(session) is { } wid && WorkspaceRoots.TryGetValue(wid, out var root) ? root
        : session.ProjectId is { } p && GetProject(p) is { } proj ? proj.Path : Path.GetTempPath();

    /// <summary>Workspace root by id, for tests that bind a session to one of their own workspaces.</summary>
    public Dictionary<string, string> WorkspaceRoots { get; } = new(StringComparer.Ordinal);

    public ChatMessage AppendMessage(string sessionId, ChatMessage message)
    {
        lock (Messages)
        {
            message.SessionId = sessionId;
            message.Id = ++_id;
            message.Seq = ++_seq;
            Messages.Add(message);
            Appended.Add(message);
            return message;
        }
    }

    public void UpdateMessage(ChatMessage message)
    {
        lock (Messages)
        {
            var i = Messages.FindIndex(m => m.Id == message.Id);
            if (i >= 0) Messages[i] = message;
            Updated.Add(message);
        }
    }

    public ChatMessage? GetMessage(long id) => Messages.FirstOrDefault(m => m.Id == id);

    /// <summary>How many message rows this store has handed out, so a test can measure what a scan really read (the
    /// real store deserialises every row it returns).</summary>
    public int MessagesRead { get; set; }

    /// <summary>As the real store: seq ascending, <c>beforeSeq</c> exclusive, the newest <c>limit</c> when one is given.</summary>
    public IReadOnlyList<ChatMessage> GetMessages(string sessionId, long? beforeSeq = null, int? limit = null)
    {
        var all = Messages.Where(m => m.SessionId == sessionId && (beforeSeq is null || m.Seq < beforeSeq)).OrderBy(m => m.Seq).ToList();
        if (limit is > 0 && all.Count > limit) all = all.GetRange(all.Count - limit.Value, limit.Value);
        MessagesRead += all.Count;
        return all;
    }

    public IReadOnlyList<ChatMessage> GetContextMessages(string sessionId)
    {
        lock (Messages)
        {
            var all = Messages.Where(m => m.SessionId == sessionId).OrderBy(m => m.Seq).ToList();
            var summary = all.LastOrDefault(m => m.Role == MessageRole.Summary && !m.Compacted);
            var rest = all.Where(m => m.Role != MessageRole.Summary && !m.Compacted).ToList();
            return summary is null ? rest : [summary, .. rest];
        }
    }

    public void MarkCompacted(string sessionId, long upToSeq)
    {
        lock (Messages)
        {
            MarkCompactedCalls.Add((sessionId, upToSeq));
            foreach (var m in Messages.Where(m => m.SessionId == sessionId && m.Seq <= upToSeq)) m.Compacted = true;
        }
    }
}

public sealed class FakeModelCatalog : IModelCatalog
{
    public Func<ModelRequest, CancellationToken, Task<ChatMessage>>? AsyncResponder { get; set; }
    /// <summary>Explicit second-pass verification script, separate from draft generation.</summary>
    public Func<ModelRequest, ChatMessage>? VerifierResponder { get; set; }
    /// <summary>When set, StreamAsync serves this instead of throwing: a scripted answer as stream events, so a test
    /// can drive the runner's streaming path (the real loop).</summary>
    public Func<ModelRequest, CancellationToken, IAsyncEnumerable<ModelStreamEvent>>? StreamResponder { get; set; }
    public List<ModelInfo> Models { get; } = [];
    public ConcurrentQueue<ModelRequest> Requests { get; } = new();
    public Func<ModelRequest, ChatMessage> Responder { get; set; } = _ => new ChatMessage
    {
        Role = MessageRole.Assistant, Parts = [new TextPart { Text = "## Task\nSUMMARY" }], StopReason = "stop",
    };
    public string? DefaultModelRef { get; set; }
    /// <summary>Fail the test when a scripted response is longer than the request's output allowance. Off by default:
    /// several tests script over-budget answers on purpose (building an oversized prior summary, for one), and a real
    /// provider would cut them off with stop reason "length" instead (idea-12wuj4).</summary>
    public bool StrictOutput { get; set; }

    public Task<IReadOnlyList<ModelInfo>> ListAsync(bool refresh = false, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ModelInfo>>(Models);
    public Task<ModelInfo?> FindAsync(string modelRef, CancellationToken ct = default) =>
        Task.FromResult(Models.FirstOrDefault(m => m.Ref == modelRef) ?? Models.FirstOrDefault(m => m.Id == modelRef));
    public IReadOnlyList<ModelInfo> Cached => Models;
    public IModelProvider? GetProvider(string providerId) => null;
    public IReadOnlyList<IModelProvider> Providers => [];
    public IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken ct)
    {
        Requests.Enqueue(request);
        if (StreamResponder is { } responder) return responder(request, ct);
        throw new NotSupportedException();
    }
    public Task<ChatMessage> CompleteAsync(ModelRequest request, CancellationToken ct)
    {
        Requests.Enqueue(request);
        if (AsyncResponder is { } asyncResponder) return asyncResponder(request, ct);
        var response = request.SystemPrompt?.StartsWith("Independently verify", StringComparison.Ordinal) == true && VerifierResponder is { } verifier
            ? verifier(request) : Responder(request);
        if (StrictOutput && request.MaxOutputTokens is > 0 and var max)
        {
            var tokens = response.Parts.OfType<TextPart>().Sum(p => ModelMessages.EstimateTokens(p.Text));
            if (tokens > max)
                throw new AssertException(
                    $"the scripted response is {tokens} tokens but the request allows {max} (model {request.Model.Ref}); a real provider would stop at the cap with reason \"length\"");
        }
        return Task.FromResult(response);
    }
}

public sealed class FakeAgentRuntime : IAgentRuntime
{
    public List<AgentInfo> Agents { get; } = [];
    public IReadOnlyList<AgentInfo> List(bool includeFinished = true) => Agents;
    public AgentInfo? Get(string agentId) => Agents.FirstOrDefault(a => a.Id == agentId);
    public AgentInfo? GetBySession(string sessionId) => Agents.FirstOrDefault(a => a.SessionId == sessionId);
    public Task<AgentInfo> SendAsync(string sessionId, UserInput input, DeliveryMode mode = DeliveryMode.Auto, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<AgentInfo> SpawnAsync(SpawnRequest request, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<IReadOnlyList<AgentInfo>> WaitAsync(string? callerAgentId, IReadOnlyList<string> agentIds, bool yieldSlot = true, TimeSpan? timeout = null, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<bool> AbortAsync(string agentOrSessionId) => Task.FromResult(false);
    public Task<bool> MessageAsync(string fromAgentId, string toAgentId, string text, DeliveryMode mode = DeliveryMode.Auto, CancellationToken ct = default) => Task.FromResult(false);
    public IReadOnlyList<QueuedInput> GetQueue(string sessionId) => [];
    public bool RemoveQueued(string sessionId, string inputId) => false;
}

public sealed class FakeAgentScheduler : IAgentScheduler
{
    public List<AgentSlotRequest> Acquired { get; } = [];
    public int Released;
    public string Resolve(ModelInfo model) => model.Ref;
    public IReadOnlyList<AgentSlots> Snapshot() => [];
    public ValueTask<IAgentSlot> AcquireAsync(AgentSlotRequest request, CancellationToken ct)
    {
        lock (Acquired) Acquired.Add(request);
        return ValueTask.FromResult<IAgentSlot>(new Lease(this, request));
    }
    public bool TryAcquire(AgentSlotRequest request, out IAgentSlot? lease) { lease = new Lease(this, request); return true; }

    private sealed class Lease(FakeAgentScheduler owner, AgentSlotRequest r) : IAgentSlot
    {
        public string Key => r.Key;
        public string AgentId => r.AgentId;
        public DateTimeOffset AcquiredAt { get; } = DateTimeOffset.UtcNow;
        public bool IsReleased { get; private set; }
        public void Dispose() { if (IsReleased) return; IsReleased = true; Interlocked.Increment(ref owner.Released); }
    }
}

public sealed class FakePluginManager : IPluginManager
{
    public List<PluginInfo> Plugins { get; } = [];
    public ConcurrentQueue<string> Reloaded { get; } = new();
    public int Rescans;
    public IReadOnlyList<PluginInfo> List() => Plugins;
    public Task ReloadAsync(string pluginId, CancellationToken ct = default) { Reloaded.Enqueue(pluginId); return Task.CompletedTask; }
    public Task SetEnabledAsync(string pluginId, bool enabled, CancellationToken ct = default) => Task.CompletedTask;
    public Task RescanAsync(CancellationToken ct = default) { Interlocked.Increment(ref Rescans); return Task.CompletedTask; }

    public static PluginInfo Info(string id, string name, int order = 100) =>
        new() { Id = id, Name = name, Directory = "/plugins/" + id, State = "running", Order = order };
}

public sealed class FakePluginContext : IPluginContext
{
    private readonly CancellationTokenSource _stopping = new();

    public FakePluginContext(string? home = null, string pluginId = "test")
    {
        PluginId = pluginId;
        home ??= T.TempDir("home");
        Paths = new NetPiPaths
        {
            AppDir = home, Home = home, LogsDir = home, WebRoot = home, SettingsFile = Path.Combine(home, "settings.json"),
            TempDir = home, PluginDirs = [], DefaultWorkspace = home,
        };
        Bus = new FakeBus(Owner);
        ServicesFake = new FakeServices(Owner);
        ServicesFake.Register<IStorageAccess>(new StorageAccessFake(this));   // what the kernel registers for every plugin
        RpcFake = new FakeRpc(Owner);
        ToolsFake = new FakeTools(Owner);
        UiFake = new FakeUi(Owner);
    }

    public Ownership Owner { get; } = new();
    public string PluginId { get; }
    public string PluginDirectory => Paths.AppDir;
    public NetPiPaths Paths { get; }
    public ListLogger Log { get; } = new();
    public ILogger Logger => Log;
    public FakeBus Bus { get; }
    public IEventBus Events => Bus;
    public FakeServices ServicesFake { get; }
    public IServiceRegistry Services => ServicesFake;
    public FakeRpc RpcFake { get; }
    public IRpcRegistry Rpc => RpcFake;
    public FakeTools ToolsFake { get; }
    public IToolRegistry Tools => ToolsFake;
    public FakeUi UiFake { get; }
    public IUiRegistry Ui => UiFake;
    public IHttpRegistry Http { get; } = new FakeHttp();
    public FakeSettings SettingsFake { get; } = new();
    public ISettings Settings => SettingsFake;
    private IStorage? _storage;
    /// <summary>
    /// The plugin's own data over a real SQLite store in the context's home (the same provider the host uses), so a plugin that keeps
    /// collections is exercised against an actual store: transactions, rollback and what survives a restart are the point, and a fake
    /// cannot prove any of them. Opened on first use and closed by <see cref="Unload"/>, so a "restart" in a test is a second store
    /// over the same folder — the data has to have survived it.
    /// </summary>
    public IPluginData Data => Storage.Plugins.For(PluginId);
    /// <summary>The whole store behind <see cref="Data"/>, for tests that read what a plugin wrote under another plugin id.</summary>
    /// <summary>What the kernel registers for plugins that ask what the store is (<see cref="IStorageAccess"/>): its info and its snapshot.</summary>
    public IStorageAccess Access => new StorageAccessFake(this);
    /// <summary>Reads the store on demand, so registering it opens nothing until a plugin asks.</summary>
    private sealed class StorageAccessFake(FakePluginContext ctx) : IStorageAccess
    {
        public StorageInfo Info => ctx.Storage.Info;
        public IStorageSnapshot Snapshot => ctx.Storage.Snapshot;
    }
    public IStorage Storage => _storage ??= new SqliteStorageProvider().Open(new StorageOpenOptions
    {
        Home = Paths.Home, Logger = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, Settings = SettingsFake,
    });
    public FakeSessionStore SessionsFake { get; } = new();
    /// <summary>
    /// A test that needs the real session service over the real store (there a stored session, an archived one and a
    /// message-less one are three different things) hands it in here; everything else gets the recording double.
    /// </summary>
    public ISessionStore? RealSessions { get; set; }
    public ISessionStore Sessions => RealSessions ?? SessionsFake;
    public FakeModelCatalog ModelsFake { get; } = new();
    public IModelCatalog Models => ModelsFake;
    public CancellationToken Stopping => _stopping.Token;
    public T1 Track<T1>(T1 disposable) where T1 : IDisposable { Owner.Own(disposable); return disposable; }

    /// <summary>What the host does on unload: cancel Stopping and dispose every registration.</summary>
    public void Unload()
    {
        _stopping.Cancel();
        Owner.DisposeAll();
        _storage?.Dispose();
        _storage = null;
    }
}

// ------------------------------------------------------------------ helpers

public static class T
{
    /// <summary>
    /// Root for this run's temporary files. Scoped to the process (or to <c>NETPI_TEST_ROOT</c>) so two
    /// runs of the same suite cannot delete each other's directories.
    /// </summary>
    public static string TestRoot { get; } = Environment.GetEnvironmentVariable("NETPI_TEST_ROOT") is { Length: > 0 } custom
        ? custom
        : Path.Combine(Path.GetTempPath(), "netpi-aux-tests", Environment.ProcessId.ToString());

    public static string TempDir(string prefix)
    {
        var dir = Path.Combine(TestRoot, prefix + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static JsonElement Args(object o) => o is string s ? JsonDocument.Parse(s).RootElement.Clone() : NetPiJson.ToElement(o);

    public static ModelInfo Model(string id = "m1", int? window = 100_000, string provider = "test", params string[] efforts) => new()
    {
        Provider = provider, Id = id, ContextWindow = window, MaxOutputTokens = 16_000,
        Reasoning = efforts.Length > 0 ? new ReasoningInfo { Supported = true, Efforts = [.. efforts] } : null,
    };

    public static ChatMessage User(string text) => ChatMessage.UserText(text);

    public static ChatMessage Assistant(string text, params ToolCallPart[] calls)
    {
        var m = new ChatMessage { Role = MessageRole.Assistant, StopReason = calls.Length > 0 ? "tool_use" : "stop" };
        if (text.Length > 0) m.Parts.Add(new TextPart { Text = text });
        m.Parts.AddRange(calls);
        return m;
    }

    public static ToolCallPart Call(string id, string name, string args = "{}") => new() { Id = id, Name = name, Arguments = args };

    public static ChatMessage ToolResult(params (string CallId, string Name, string Content)[] results) => new()
    {
        Role = MessageRole.Tool,
        Parts = results.Select(r => (MessagePart)new ToolResultPart { CallId = r.CallId, Name = r.Name, Content = r.Content }).ToList(),
    };

    public static ToolDefinition Tool(string name, JsonObject? properties = null) => new()
    {
        Name = name, Description = name + " tool",
        Parameters = new JsonObject { ["type"] = "object", ["properties"] = properties ?? new JsonObject() },
    };

    public static AgentRunContext Run(FakePluginContext ctx, ModelInfo model, SessionInfo session, CancellationToken ct = default) => new()
    {
        Agent = new AgentInfo { Id = "agt_1", SessionId = session.Id, Name = "main", Status = AgentStatus.Running },
        Session = session,
        Cwd = ctx.Paths.DefaultWorkspace,
        Model = model,
        Services = ctx.Services,
        Sessions = ctx.Sessions,
        Models = ctx.Models,
        Events = ctx.Events,
        CancellationToken = ct,
    };

    public static AgentTurnContext Turn(AgentRunContext run, List<ChatMessage>? messages = null, IReadOnlyList<ToolDefinition>? tools = null,
        Func<Task>? reload = null, int index = 0, string systemPrompt = "You are a helpful agent.") => new()
    {
        Run = run,
        TurnIndex = index,
        SystemPrompt = systemPrompt,
        Messages = messages ?? [],
        Tools = tools ?? [],
        ReloadMessagesAsync = reload ?? (() => Task.CompletedTask),
    };

    public static string Str(this JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : n?.ToJsonString() ?? "null";
}
