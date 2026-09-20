using NetPI.Abstractions;
using NetPI.Host.Plugins;

namespace NetPI.Host.Services;

/// <summary>
/// Host service registry with plugin ownership (PLAN §6/§7).
///
/// - Every registration is owned by the plugin generation that created it
///   (owner is captured via <see cref="ServiceOwner.Current"/> during LoadAsync).
/// - <see cref="IValueLease{T}"/> tracks live leases per owning plugin; while a
///   lease is live the owning plugin cannot be reloaded (reload drains).
/// - Acquiring from a draining/unloading plugin is denied.
/// - On unload the host calls <see cref="RemoveAllFor"/>: registrations are
///   removed even if the plugin forgot to dispose its handles.
/// </summary>
public sealed class ServiceRegistry : IServiceRegistry
{
    private sealed class Entry
    {
        public string Id = null!;
        public Type ServiceType = null!;
        public object Instance = null!;
        public WeakReference<PluginInstance> Owner = null!;
        public bool Removed;
    }


    private sealed record LeaseCore(object instance, Type serviceType, WeakReference<PluginInstance> ownerRef);

    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public IDisposable Register<T>(string id, T instance) where T : notnull
    {
        if (instance is null) throw new ArgumentNullException(nameof(instance));

        var owner = ServiceOwner.Current;
        var entry = new Entry
        {
            Id = id,
            ServiceType = typeof(T),
            Instance = instance,
            Owner = owner is null ? null! : new WeakReference<PluginInstance>(owner)
        };


        var handle = new RegistrationHandle(id, entry, owner, _gate, _entries);
        lock (_gate)
        {
            if (_entries.TryGetValue(id, out var existing) && !existing.Removed)
                throw new InvalidOperationException($"Service '{id}' is already registered (by plugin '{PluginName(existing)}').");
            _entries[id] = entry;
        }
        if (owner is not null)
            owner.Registrations[id] = handle;
        return handle;
    }

    private static string PluginName(Entry e) => GetTarget(e.Owner)?.PluginId ?? "<host>";

    private static PluginInstance? GetTarget(WeakReference<PluginInstance>? wr) =>
        wr is not null && wr.TryGetTarget(out var t) ? t : null;


    /// <summary>
    /// Acquire a lease on a registered service whose expected type is a
    /// compile-time generic argument.
    /// </summary>
    public IValueLease<T> Acquire<T>(string id) where T : notnull
    {
        var raw = AcquireCore(id, typeof(T));
        if (raw.instance is not T typed)
            throw new ServiceUnavailableException(id, $"registered as {raw.serviceType.Name}, not {typeof(T).Name}");
        var lease = new Lease<T>(typed, raw.ownerRef);
        var owner = GetTarget(raw.ownerRef);
        if (owner is not null &&
            owner.State is PluginState.Draining or PluginState.Unloading or PluginState.Unloaded or PluginState.Failed)
        {
            ((IDisposable)lease).Dispose();
            throw new ServiceUnavailableException(id, $"owning plugin '{owner.PluginId}' is {owner.State}");
        }
        if (owner is not null)
            owner.AddLease(lease.Id, lease);
        return lease;
    }

    /// <summary>
    /// Acquire a lease with the expected service type supplied as a
    /// <see cref="Type"/> (cross-ALC acquisition: host tooling or tests
    /// referencing the plugin assembly, where the service type cannot be used
    /// as a generic type argument).
    /// </summary>
    public IValueLease<object> Acquire(string id, Type expectedType)
    {
        if (expectedType is null) throw new ArgumentNullException(nameof(expectedType));
        var raw = AcquireCore(id, expectedType);
        var lease = new Lease<object>(raw.instance, raw.ownerRef);
        var owner = GetTarget(raw.ownerRef);
        if (owner is not null &&
            owner.State is PluginState.Draining or PluginState.Unloading or PluginState.Unloaded or PluginState.Failed)
        {
            ((IDisposable)lease).Dispose();
            throw new ServiceUnavailableException(id, $"owning plugin '{owner.PluginId}' is {owner.State}");
        }
        if (owner is not null)
            owner.AddLease(lease.Id, lease);
        return lease;
    }

    /// <summary>
    /// PLAN §11/§44: a lease on the owning plugin itself, held for the duration
    /// of work. Counts toward the owning generation's live lease count so a
    /// reload's lease drain blocks until it is released.
    /// </summary>
    public IValueLease<T> AcquireSelfLease<T>() where T : notnull
    {
        var owner = ServiceOwner.Current;
        if (owner is null)
            return new SelfLease<T>(); // host-side / test: no owning generation
        var lease = new SelfLease<T>(owner);
        owner.AddLease(lease.Id, lease);
        return lease;
    }

    private sealed class SelfLease<T>(PluginInstance? owner = null) : IValueLease<T> where T : notnull
    {
        private int _released;
        public Guid Id { get; } = Guid.NewGuid();
        private readonly T _value = default!;
        public T Value => _value;
        private void Release()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0) return;
            owner?.RemoveLease(Id, out _);
        }
        void IDisposable.Dispose() => Release();
        public ValueTask DisposeAsync() { Release(); return ValueTask.CompletedTask; }
    }

    private LeaseCore AcquireCore(string id, Type expectedType)
    {
        object instance;
        Type serviceType;
        var ownerRef = new WeakReference<PluginInstance>(null!);
        lock (_gate)
        {
            if (!_entries.TryGetValue(id, out var e) || e.Removed)
                throw new ServiceUnavailableException(id, "not registered");
            if (e.Instance is null || !expectedType.IsAssignableFrom(e.Instance.GetType()))
                throw new ServiceUnavailableException(id, $"registered as {e.ServiceType.Name}, not {expectedType.Name}");
            instance = e.Instance;
            serviceType = e.ServiceType;
            // Host-owned entries have a null Owner; keep ownerRef non-null so
            // Lease.Release never sees a null reference (PLAN §46 host surfaces).
            ownerRef = e.Owner ?? new WeakReference<PluginInstance>(null!);
        }
        return new LeaseCore(instance, serviceType, ownerRef);
    }

    public T Resolve<T>(string id) where T : notnull
    {
        // Keep the lease alive for the caller's scope (PLAN §11): disposing it
        // here would not prevent a mid-call unload of the owning plugin.
        return Acquire<T>(id).Value;
    }

    /// <summary>
    /// Remove every registration owned by <paramref name="instance"/>
    /// (PLAN §7: the host removes registrations even if the plugin forgot).
    /// </summary>
    public void RemoveAllFor(PluginInstance instance)
    {
        lock (_gate)
        {
            foreach (var kv in _entries.ToList())
            {
                var e = kv.Value;
                if (!e.Removed && ReferenceEquals(GetTarget(e.Owner), instance))
                {
                    e.Removed = true;
                    instance.Registrations.TryRemove(kv.Key, out _);
                    // Physically remove the entry so the old generation's
                    // service instances become collectible (ALC unload).
                    _entries.Remove(kv.Key);
                }
            }
        }
    }

    /// <summary>Current registrations (snapshot). For diagnostics.</summary>
    public IReadOnlyList<(string Id, string Service, string PluginId, Type ServiceType)> Snapshot()
    {
        lock (_gate)
            return _entries.Values
                .Where(e => !e.Removed)
                .Select(e => (e.Id, e.Instance!.GetType().Name, PluginName(e), e.ServiceType))
                .OrderBy(x => x.Id, StringComparer.Ordinal)
                .ToList();


    }

    private sealed class RegistrationHandle(
        string id, Entry entry, PluginInstance? owner, object gate, Dictionary<string, Entry> entries) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            lock (gate)
            {
                if (entries.TryGetValue(id, out var e) && ReferenceEquals(e, entry) && !e.Removed)
                    e.Removed = true;
            }
            owner?.Registrations.TryRemove(id, out _);
        }
    }

    private sealed class Lease<T>(T value, WeakReference<PluginInstance> ownerRef) : IValueLease<T> where T : notnull
    {
        private int _released;
        public Guid Id { get; } = Guid.NewGuid();
        public T Value => value;

        private void Release()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0) return;
            var owner = ownerRef.TryGetTarget(out var o) ? o : null;
            owner?.RemoveLease(Id, out _);
        }

        void IDisposable.Dispose() => Release();
        public ValueTask DisposeAsync()
        {
            Release();
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>
    /// Context of the plugin currently loading. Set by the plugin manager
    /// around <c>LoadAsync</c>; registrations made inside attribute to that
    /// plugin generation. Host-side code (no context) registers under the
    /// host itself.
    /// </summary>
    public sealed class ServiceOwner
    {
        public static PluginInstance? Current { get; set; }
    }
}
