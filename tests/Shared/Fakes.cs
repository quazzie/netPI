using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace NetPI.TestShared;

// The fake plugin-context building blocks, one copy each (idea-2fbluy). Linked into the suites that test
// plugins off a live server (Agent, Aux, Tools, Providers). What stays suite-local is what differs in
// fidelity, not in shape: the Agent suite's event bus dispatches on a worker thread like the host's (its tests
// await the drain), the Tools suite's does not dispatch at all (its tools are under test, not their handlers),
// and the three FakePluginContexts differ in what is real underneath them (SQLite + a real SessionService,
// the memory provider + a real SessionService, and the bare minimum).

/// <summary>A disposer that runs its action once no matter how often it is disposed (a registration is
/// disposed by the test and by the context unload).</summary>
public sealed class Disposer(Action action) : IDisposable
{
    private Action? _action = action;
    public void Dispose() => Interlocked.Exchange(ref _action, null)?.Invoke();
}

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

public sealed class FakeSettings(IEventBus? bus = null, JsonObject? root = null) : ISettings
{
    private JsonObject _root = root ?? new();
    public string FilePath => "memory://settings.json";
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

    /// <summary>Set without publishing (test setup before plugins start).</summary>
    public void SetQuiet(string path, JsonNode? value)
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

    /// <summary>Set and announce it, like the real settings file does (a bus-less fake just sets).</summary>
    public void Set(string path, JsonNode? value)
    {
        SetQuiet(path, value);
        bus?.Publish(EventTypes.SettingsChanged, new JsonObject { ["path"] = path });
    }

    public SettingsReplace Replace(JsonObject root, JsonObject? baseDocument = null)
    {
        if (baseDocument is not null && !JsonNode.DeepEquals(_root, baseDocument)) return SettingsReplace.Conflict;
        _root = root;
        bus?.Publish(EventTypes.SettingsChanged, new JsonObject());
        return SettingsReplace.Saved;
    }
}

/// <summary>The recording bus: events are kept in order and handlers run synchronously on the publisher's
/// thread (the suites that need the host's async dispatch keep their own).</summary>
public sealed class FakeBus(Ownership? owner = null) : IEventBus
{
    private readonly List<(string Pattern, Action<BusEvent> Handler)> _subs = [];
    private long _seq;
    /// <summary>Everything published, in order (live, so a test can clear it and count from a point).</summary>
    public List<BusEvent> Events { get; } = [];

    public void Publish(BusEvent evt)
    {
        evt.Seq = Interlocked.Increment(ref _seq);
        lock (Events) Events.Add(evt);
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

    public IReadOnlyList<BusEvent> Recent(int max = 200)
    {
        lock (Events) return Events.TakeLast(max).ToList();
    }

    public List<BusEvent> OfType(string type)
    {
        lock (Events) return Events.Where(e => e.Type == type).ToList();
    }

    public int Count(string type)
    {
        lock (Events) return Events.Count(e => e.Type == type);
    }

    public int SubscriberCount { get { lock (_subs) return _subs.Count; } }

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
    /// <summary>What the Agent host observes when a registration appears or goes (the kernel's cache invalidation).</summary>
    public Action<Type>? Changed { get; set; }
    private readonly List<(Type Type, object Instance, int Priority, long Seq)> _items = [];
    private long _seq;

    public IDisposable Register<T>(T instance, int priority = 0) where T : class
    {
        var entry = (typeof(T), (object)instance, priority, Interlocked.Increment(ref _seq));
        lock (_items) _items.Add(entry);
        Changed?.Invoke(typeof(T));
        var d = new Disposer(() => { lock (_items) _items.Remove(entry); Changed?.Invoke(typeof(T)); });
        owner?.Own(d);
        return d;
    }

    public T? Get<T>() where T : class => GetAll<T>().FirstOrDefault();

    public IReadOnlyList<T> GetAll<T>() where T : class
    {
        lock (_items)
            return _items.Where(i => i.Type == typeof(T)).OrderByDescending(i => i.Priority).ThenByDescending(i => i.Seq).Select(i => (T)i.Instance).ToList();
    }
}

public sealed class FakeRpc(Ownership? owner = null) : IRpcRegistry
{
    public ConcurrentDictionary<string, RpcHandler> Handlers { get; } = new();
    /// <summary>Which plugin owns each method, so List() reports PluginId like the real registry.</summary>
    public ConcurrentDictionary<string, string> Owners { get; } = new();
    /// <summary>What each method was registered as, so List() can report readOnly like the real registry (idea-de1s7t).</summary>
    public ConcurrentDictionary<string, bool> ReadOnly { get; } = new();

    public IDisposable Register(string method, RpcHandler handler, string? description = null) => Register(method, handler, description, false);

    public IDisposable Register(string method, RpcHandler handler, string? description, bool readOnly) => RegisterFor(method, handler, "test", readOnly);

    public IDisposable RegisterFor(string method, RpcHandler handler, string pluginId, bool readOnly = false)
    {
        Handlers[method] = handler;
        Owners[method] = pluginId;
        ReadOnly[method] = readOnly;
        var d = new Disposer(() => { Handlers.TryRemove(method, out _); Owners.TryRemove(method, out _); ReadOnly.TryRemove(method, out _); });
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

    /// <summary>Invoke and return the result as JSON (what the UI would receive).</summary>
    public async Task<JsonNode?> CallAsync(string method, object? parameters = null) => NetPiJson.ToNode(await InvokeAsync(method, parameters));

    public IReadOnlyList<RpcMethodInfo> List() => Handlers.Keys.Select(k => new RpcMethodInfo(k, null, Owners.GetValueOrDefault(k) ?? "test", ReadOnly.GetValueOrDefault(k))).ToList();
    public bool Exists(string method) => Handlers.ContainsKey(method);
}

public sealed class FakeTools(Ownership? owner = null, ISettings? settings = null) : IToolRegistry
{
    private readonly List<(IAgentTool Tool, int Priority, string PluginId)> _tools = [];
    /// <summary>Which plugin registered each tool (the owner, where a test needs one).</summary>
    public Dictionary<IAgentTool, string> Owners { get; } = [];

    public IDisposable Register(IAgentTool tool, int priority = 0) => Register(tool, priority, "test");

    public IDisposable Register(IAgentTool tool, string pluginId) => Register(tool, 0, pluginId);

    public IDisposable Register(IAgentTool tool, int priority, string pluginId)
    {
        var entry = (tool, priority, pluginId);
        lock (_tools) { _tools.Add(entry); Owners[tool] = pluginId; }
        var d = new Disposer(() => { lock (_tools) { _tools.Remove(entry); Owners.Remove(tool); } });
        owner?.Own(d);
        return d;
    }

    /// <summary>One registration per tool name (the highest priority wins), the disabled tools left out when the
    /// fake was given the settings that own <c>tools.disabled</c> — as the host registry answers it.</summary>
    public IReadOnlyList<IAgentTool> All
    {
        get
        {
            var disabled = settings?.Get<List<string>>("tools.disabled") ?? [];
            lock (_tools)
                return _tools.GroupBy(i => i.Tool.Definition.Name)
                    .Select(g => g.OrderByDescending(i => i.Priority).First().Tool)
                    .Where(t => !disabled.Contains(t.Definition.Name))
                    .ToList();
        }
    }

    /// <summary><see cref="All"/> by the name the older fakes used.</summary>
    public IReadOnlyList<IAgentTool> Tools => All;

    public IAgentTool? Get(string name) => All.FirstOrDefault(t => t.Definition.Name == name);

    public IReadOnlyList<ToolRegistration> Registrations
    {
        get { lock (_tools) return _tools.Select(i => new ToolRegistration(i.Tool, i.PluginId, i.Priority)).ToList(); }
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
    /// <summary>What the plugin mapped (path → handler and whether it is open), so a test can serve it.</summary>
    public ConcurrentDictionary<string, (Func<HttpContext, Task> Handler, bool Open)> Routes { get; } = new(StringComparer.OrdinalIgnoreCase);

    public IDisposable Map(string path, Func<HttpContext, Task> handler, bool open = false)
    {
        var key = path.Trim('/');
        Routes[key] = (handler, open);
        return new Disposer(() => Routes.TryRemove(key, out _));
    }
}

public sealed class ListLogger : ILogger
{
    public ConcurrentQueue<string> Lines { get; } = new();
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        Lines.Enqueue($"{logLevel}: {formatter(state, exception)}");
}
