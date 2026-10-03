using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using NetPI.Host.Events;
using NetPI.Host.Registries;

namespace NetPI.Host.Plugins;

/// <summary>
/// Everything one plugin load registered. Disposed in reverse order on unload. Registrations made after the scope
/// closed (a straggling background task of an unloaded plugin) are disposed immediately.
/// </summary>
internal sealed class PluginScope(string pluginId, ILogger log)
{
    private readonly Lock _gate = new();
    private List<IDisposable> _items = [];
    private int _compactAt = 256;
    private bool _closed;

    public string PluginId => pluginId;

    public T Track<T>(T disposable) where T : IDisposable
    {
        ArgumentNullException.ThrowIfNull(disposable);
        bool closed;
        lock (_gate)
        {
            closed = _closed;
            if (!closed)
            {
                _items.Add(disposable);
                if (_items.Count >= _compactAt)
                {
                    // Plugins that register/unregister repeatedly must not grow this list forever.
                    _items.RemoveAll(d => d is Registration { IsDisposed: true });
                    _compactAt = Math.Max(256, _items.Count * 2);
                }
            }
        }
        if (closed) SafeDispose(disposable);
        return disposable;
    }

    public void DisposeAll()
    {
        List<IDisposable> items;
        lock (_gate)
        {
            _closed = true;
            items = _items;
            _items = [];
        }
        for (var i = items.Count - 1; i >= 0; i--) SafeDispose(items[i]);
    }

    private void SafeDispose(IDisposable d)
    {
        try { d.Dispose(); }
        catch (Exception ex) { log.LogWarning(ex, "Plugin {Id}: disposing a registration failed", pluginId); }
    }
}

internal sealed class ScopedEventBus(EventBus inner, PluginScope scope) : IEventBus
{
    public void Publish(BusEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        inner.Publish(evt.Source is not null ? evt : new BusEvent
        {
            Type = evt.Type, Data = evt.Data, SessionId = evt.SessionId, Source = scope.PluginId, Ui = evt.Ui, Time = evt.Time,
        });
    }

    public void Publish(string type, object? data = null, string? sessionId = null, bool ui = true) =>
        inner.Publish(new BusEvent { Type = type, Data = data, SessionId = sessionId, Ui = ui, Source = scope.PluginId });

    public IDisposable Subscribe(string pattern, Action<BusEvent> handler) => scope.Track(inner.Subscribe(pattern, scope.PluginId, handler));

    public IDisposable SubscribeAsync(string pattern, Func<BusEvent, ValueTask> handler) => scope.Track(inner.SubscribeAsync(pattern, scope.PluginId, handler));

    public IReadOnlyList<BusEvent> Recent(int max = 200) => inner.Recent(max);
}

internal sealed class ScopedServiceRegistry(ServiceRegistry inner, PluginScope scope) : IServiceRegistry
{
    public IDisposable Register<T>(T instance, int priority = 0) where T : class =>
        scope.Track(inner.Register(instance, priority, scope.PluginId));

    public T? Get<T>() where T : class => inner.Get<T>();

    public IReadOnlyList<T> GetAll<T>() where T : class => inner.GetAll<T>();
}

internal sealed class ScopedRpcRegistry(RpcRegistry inner, PluginScope scope) : IRpcRegistry
{
    public IDisposable Register(string method, RpcHandler handler, string? description = null) =>
        scope.Track(inner.Register(method, handler, description, scope.PluginId, false));

    public IDisposable Register(string method, RpcHandler handler, string? description, bool readOnly) =>
        scope.Track(inner.Register(method, handler, description, scope.PluginId, readOnly));

    public IDisposable Register(RpcMethod method, RpcHandler handler) =>
        scope.Track(inner.Register(method, handler, scope.PluginId));

    public Task<object?> InvokeAsync(string method, object? parameters = null, CancellationToken ct = default) =>
        inner.InvokeAsync(method, parameters, ct);

    public IReadOnlyList<RpcMethodInfo> List() => inner.List();

    public bool Exists(string method) => inner.Exists(method);
}

internal sealed class ScopedToolRegistry(ToolRegistry inner, PluginScope scope) : IToolRegistry
{
    public IDisposable Register(IAgentTool tool, int priority = 0) => scope.Track(inner.Register(tool, priority, scope.PluginId));

    public IReadOnlyList<IAgentTool> All => inner.All;

    public IAgentTool? Get(string name) => inner.Get(name);

    public IReadOnlyList<ToolRegistration> Registrations => inner.Registrations;
}

internal sealed class ScopedUiRegistry(UiRegistry inner, PluginScope scope, Func<string> version) : IUiRegistry
{
    public IDisposable AddTab(UiTabInfo tab) => scope.Track(inner.AddTab(tab, scope.PluginId, version()));

    public IDisposable AddCommand(SlashCommandInfo command) => scope.Track(inner.AddCommand(command, scope.PluginId));

    public IReadOnlyList<UiTabInfo> Tabs => inner.Tabs;

    public IReadOnlyList<SlashCommandInfo> Commands => inner.Commands;
}

internal sealed class ScopedHttpRegistry(HttpRegistry inner, PluginScope scope) : IHttpRegistry
{
    public IDisposable Map(string path, Func<HttpContext, Task> handler, bool open = false) => scope.Track(inner.Map(scope.PluginId, path, handler, open));
}
