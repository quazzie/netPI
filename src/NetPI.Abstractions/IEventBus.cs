
namespace NetPI.Abstractions;

/// <summary>
/// Host event bus. Subscriptions are owned by the plugin generation that
/// created them; on unload the host removes all of them automatically
/// (PLAN §7). Handlers run in deterministic priority order (ascending
/// <see cref="EventSubscriptionOptions.Priority"/>, then registration order).
/// </summary>
public interface IEventBus
{
    /// <summary>
    /// Subscribe a handler to a specific event type. Returns the owned
    /// subscription; disposing it (or unloading the plugin) removes the
    /// handler from the bus.
    /// </summary>
    IDisposable Subscribe<TEvent>(EventHandler<TEvent> handler, EventSubscriptionOptions? options = null);

    /// <summary>
    /// Publish an event to all current subscribers of
    /// <typeparamref name="TEvent"/> in deterministic order. Subscribers are
    /// snapshotted at publish time so late removals during dispatch are safe.
    /// </summary>
    ValueTask PublishAsync<TEvent>(TEvent @event, CancellationToken cancellationToken = default) where TEvent : notnull;
}

/// <summary>Controls ordering and per-plugin grouping of a subscription.</summary>
public sealed record EventSubscriptionOptions
{
    /// <summary>Lower runs first; default 0. Deterministic: ties keep registration order.</summary>
    public int Priority { get; init; }

    /// <summary>
    /// When true (default) the handler is invoked even for events published by
    /// its own plugin. Set false to ignore self-published events.
    /// </summary>
    public bool ReceiveSelfEvents { get; init; } = true;
}

/// <summary>Delegate for typed event handlers.</summary>
public delegate void EventHandler<T>(T @event);
