using System.Collections.ObjectModel;

namespace NetPI.Host.Registries;

/// <summary>
/// Dynamic service registry. A lookup for <c>T</c> returns every registration whose registered type is <c>T</c> or
/// derives from/implements <c>T</c>, ordered by priority (desc) then registration order (latest first).
/// </summary>
internal sealed class ServiceRegistry : IServiceRegistry
{
    private sealed record Entry(object Instance, Type Type, int Priority, long Seq, string Owner);

    public sealed record ServiceInfo(string Type, string Implementation, int Priority, string Owner);

    private readonly Lock _gate = new();
    private readonly List<Entry> _entries = [];
    // Resolved lookups; cleared on every change (keys may be plugin types, so never keep them across unloads).
    private readonly Dictionary<Type, object> _cache = [];
    private long _seq;

    /// <summary>Raised (outside the lock) with the registered type whenever a registration is added or removed.</summary>
    public event Action<Type>? Changed;

    public IDisposable Register<T>(T instance, int priority = 0) where T : class => Register(instance, priority, "host");

    public IDisposable Register<T>(T instance, int priority, string owner) where T : class
    {
        ArgumentNullException.ThrowIfNull(instance);
        Entry entry;
        lock (_gate)
        {
            entry = new Entry(instance, typeof(T), priority, ++_seq, owner);
            _entries.Add(entry);
            _cache.Clear();
        }
        RaiseChanged(typeof(T));
        return new Registration(() =>
        {
            bool removed;
            lock (_gate)
            {
                removed = _entries.Remove(entry);
                if (removed) _cache.Clear();
            }
            if (removed) RaiseChanged(entry.Type);
        });
    }

    public T? Get<T>() where T : class
    {
        var all = Resolve<T>();
        return all.Count > 0 ? all[0] : null;
    }

    public IReadOnlyList<T> GetAll<T>() where T : class => Resolve<T>();

    public void ClearCache()
    {
        lock (_gate) _cache.Clear();
    }

    public IReadOnlyList<ServiceInfo> List()
    {
        lock (_gate)
            return _entries
                .OrderBy(e => e.Type.Name, StringComparer.Ordinal).ThenByDescending(e => e.Priority).ThenByDescending(e => e.Seq)
                .Select(e => new ServiceInfo(e.Type.FullName ?? e.Type.Name, e.Instance.GetType().FullName ?? "?", e.Priority, e.Owner))
                .ToList();
    }

    private ReadOnlyCollection<T> Resolve<T>() where T : class
    {
        lock (_gate)
        {
            if (_cache.TryGetValue(typeof(T), out var hit)) return (ReadOnlyCollection<T>)hit;
            var matches = new List<Entry>();
            foreach (var e in _entries)
                if (typeof(T).IsAssignableFrom(e.Type) && e.Instance is T) matches.Add(e);
            matches.Sort(static (a, b) => a.Priority != b.Priority ? b.Priority.CompareTo(a.Priority) : b.Seq.CompareTo(a.Seq));
            var result = matches.Select(e => (T)e.Instance).ToList().AsReadOnly();
            if (matches.Count > 0 || !typeof(T).IsCollectible) _cache[typeof(T)] = result;
            return result;
        }
    }

    private void RaiseChanged(Type type)
    {
        try { Changed?.Invoke(type); }
        catch { /* listeners log their own failures */ }
    }
}
