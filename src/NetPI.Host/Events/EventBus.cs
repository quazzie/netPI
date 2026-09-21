using NetPI.Abstractions;
using NetPI.Host.Services;

namespace NetPI.Host.Events;

/// <summary>
/// Host event bus with plugin-owned subscriptions (PLAN §7).
///
/// - Subscriptions are owned by the plugin generation that created them; the
///   host removes all of them on unload even if the plugin forgot.
/// - Handlers run in deterministic order: ascending <c>Priority</c>, then
///   registration order (PLAN §45).
/// - Subscribers are snapshotted at publish time.
/// </summary>
public sealed class EventBus : IEventBus
{
    private sealed class SubEntry
    {
        public int Id;
        public required object Handler;
        public required Type EventType;
        public int Priority;
        public int Order;
        public bool ReceiveSelfEvents = true;
        public WeakReference<Plugins.PluginInstance> Owner = new(null!);
        public bool Removed;
    }

    private readonly object _gate = new();
    private int _nextId;
    private int _order;
    private readonly Dictionary<Type, List<SubEntry>> _subs = new();

    public IDisposable Subscribe<TEvent>(NetPI.Abstractions.EventHandler<TEvent> handler, EventSubscriptionOptions? options = null)
        => Subscribe(handler, options, ServiceRegistry.ServiceOwner.Current);

    /// <summary>
    /// astra-1 P3: subscription with an EXPLICIT owner (the scoped wrapper
    /// passes the actual owning generation instead of relying on the
    /// process-global ambient <see cref="ServiceRegistry.ServiceOwner"/>).
    /// </summary>
    public IDisposable Subscribe<TEvent>(NetPI.Abstractions.EventHandler<TEvent> handler, EventSubscriptionOptions? options, Plugins.PluginInstance? owner)
    {
        if (handler is null) throw new ArgumentNullException(nameof(handler));

        SubEntry entry;
        IDisposable handle;
        lock (_gate)
        {
            entry = new SubEntry
            {
                Id = Interlocked.Increment(ref _nextId),
                Handler = handler,
                EventType = typeof(TEvent),
                Priority = options?.Priority ?? 0,
                Order = Interlocked.Increment(ref _order),
                ReceiveSelfEvents = options?.ReceiveSelfEvents ?? true,
                Owner = owner is null ? new WeakReference<Plugins.PluginInstance>(null!) : new WeakReference<Plugins.PluginInstance>(owner)
            };

            if (!_subs.TryGetValue(typeof(TEvent), out var list))
            {
                list = [];
                _subs[typeof(TEvent)] = list;
            }
            list.Add(entry);
        }

        handle = new SubHandle(entry, owner, _gate, _subs);
        if (owner is not null)
            owner.Subscriptions[entry.Id.ToString()] = handle;
        return handle;
    }

    public ValueTask PublishAsync<TEvent>(TEvent @event, CancellationToken cancellationToken = default) where TEvent : notnull
    {
        List<SubEntry> snapshot;
        lock (_gate)
        {
            if (!_subs.TryGetValue(typeof(TEvent), out var list))
                return ValueTask.CompletedTask;
            snapshot = list
                .Where(e => !e.Removed)
                .OrderBy(e => e.Priority)
                .ThenBy(e => e.Order)
                .ToList();
        }

        var publisher = ServiceRegistry.ServiceOwner.Current;
        foreach (var s in snapshot)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = s.Owner.TryGetTarget(out var t) ? t : null;
            if (target is not null)
            {
                if (!s.ReceiveSelfEvents && ReferenceEquals(target, publisher))
                    continue;
                if (target.State is Plugins.PluginState.Unloading or Plugins.PluginState.Unloaded or Plugins.PluginState.Failed)
                    continue;
            }
            if (s.Handler is not NetPI.Abstractions.EventHandler<TEvent> handler)
                continue;
            handler(@event);
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>Remove every subscription owned by <paramref name="instance"/> (PLAN §7).</summary>
    public void RemoveAllFor(Plugins.PluginInstance instance)
    {
        lock (_gate)
        {
            foreach (var list in _subs.Values)
            {
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    var e = list[i];
                    if (e.Owner.TryGetTarget(out var target) && ReferenceEquals(target, instance))
                        list.RemoveAt(i);
                }
            }
            // The dictionary is keyed by the event Type, and plugin event types
            // live in the (collectible) plugin ALC. Dropping empty lists breaks
            // the reference from the bus to that Type, letting the ALC collect.
            foreach (var kv in _subs.ToList())
                if (kv.Value.Count == 0)
                    _subs.Remove(kv.Key);

            instance.Subscriptions.Clear();
        }
    }

    /// <summary>Number of live (non-removed) subscriptions on the bus. For diagnostics/tests.</summary>
    public int SubscriptionCount()
    {
        lock (_gate)
            return _subs.Values.Sum(l => l.Count(e => !e.Removed));
    }

    private sealed class SubHandle(
        SubEntry entry, Plugins.PluginInstance? owner, object gate, Dictionary<Type, List<SubEntry>> subs) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            lock (gate)
            {
                entry.Removed = true;
                if (subs.TryGetValue(entry.EventType, out var list))
                {
                    list.Remove(entry);
                    // astra-1 P3: drop the per-type bucket when it goes empty
                    // — a plugin-owned event type must not leave an empty
                    // (unreachable, but retained) entry behind.
                    if (list.Count == 0)
                        subs.Remove(entry.EventType);
                }
            }
            owner?.Subscriptions.TryRemove(entry.Id.ToString(), out _);
        }
    }
}
