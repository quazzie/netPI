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
        => Register(id, instance, ServiceOwner.Current);

    /// <summary>
    /// astra-1 P3: registration with an EXPLICIT owner (the scoped wrapper
    /// passes the actual owning generation instead of relying on the
    /// process-global ambient <see cref="ServiceOwner"/> state).
    /// </summary>
    public IDisposable Register<T>(string id, T instance, PluginInstance? owner) where T : notnull
    {
        if (instance is null) throw new ArgumentNullException(nameof(instance));
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
        // astra-1 P3: admission is atomic with the state check — a new lease
        // can never be recorded after the owner began draining.
        var owner = GetTarget(raw.ownerRef);
        if (owner is not null && !owner.TryAcquireLease(lease.Id, (IDisposable)lease))
        {
            ((IDisposable)lease).Dispose();
            throw new ServiceUnavailableException(id, $"owning plugin '{owner.PluginId}' is {owner.State}");
        }
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
        // astra-1 P3: atomic admission (see Acquire<T>).
        var owner = GetTarget(raw.ownerRef);
        if (owner is not null && !owner.TryAcquireLease(lease.Id, (IDisposable)lease))
        {
            ((IDisposable)lease).Dispose();
            throw new ServiceUnavailableException(id, $"owning plugin '{owner.PluginId}' is {owner.State}");
        }
        return lease;
    }

    /// <summary>
    /// PLAN §11/§44: a lease on the owning plugin itself, held for the duration
    /// of work. Counts toward the owning generation's live lease count so a
    /// reload's lease drain blocks until it is released.
    /// </summary>
    public IValueLease<T> AcquireSelfLease<T>() where T : notnull
        => AcquireSelfLease<T>(ServiceOwner.Current);

    /// <summary>
    /// astra-1 P3: self-lease with an EXPLICIT owner — the scoped wrapper
    /// binds the actual owning generation instead of ambient state left over
    /// from loading — with atomic state-machine admission.
    /// </summary>
    public IValueLease<T> AcquireSelfLease<T>(PluginInstance? owner) where T : notnull
    {
        // astra-1 P3: admission is atomic with the state machine.
        if (owner is null)
            return new SelfLease<T>(); // host-side / test: no owning generation
        var lease = new SelfLease<T>(owner);
        if (!owner.TryAcquireSelfLease(lease.Id, lease))
        {
            lease.Untrack();
            return lease;
        }
        return lease;
    }

    private sealed class SelfLease<T>(PluginInstance? owner = null) : IValueLease<T> where T : notnull
    {
        private int _released;
        private bool _tracked = true;
        public Guid Id { get; } = Guid.NewGuid();
        private readonly T _value = default!;
        public T Value => _value;
        public void Untrack() => _tracked = false;
        private void Release()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0) return;
            if (_tracked)
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
        // PLAN §11: transient access — the lease tracks that this call touched
        // the owning plugin, then releases. Callers that need the unload
        // guarantee across a whole operation hold an explicit lease via
        // Acquire/AcquireLease (e.g. the agent's tool-execution batch).
        using var lease = Acquire<T>(id);
        return lease.Value;
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
                // astra-1 P3: physically remove the entry so the old
                // generation's instance and service type become collectible
                // (a dead entry keeping a live reference would pin the ALC).
                // A newer replacement under the same id is a DIFFERENT entry
                // — the reference check guarantees we never delete it.
                if (entries.TryGetValue(id, out var e) && ReferenceEquals(e, entry) && !e.Removed)
                {
                    e.Removed = true;
                    entries.Remove(id);
                }
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
