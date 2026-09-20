namespace NetPI.Abstractions;

/// <summary>
/// Host-wide service registry. Every registration is owned by the plugin
/// generation that created it; on unload the host removes all of them
/// automatically even if the plugin forgot to.
/// </summary>
public interface IServiceRegistry
{
    /// <summary>
    /// Register an instance under <paramref name="id"/>. The returned
    /// registration is owned by the calling plugin; disposing it removes
    /// the registration early. The host removes it on plugin unload regardless.
    /// </summary>
    IDisposable Register<T>(string id, T instance) where T : notnull;

    /// <summary>
    /// Acquire a lease on a registered service. The lease increments the owning
    /// plugin's active-use counter; while any lease is live a reload of that
    /// plugin is deferred until every lease is released (PLAN §6).
    /// Denies acquisition from a draining plugin with
    /// <see cref="ServiceUnavailableException"/>.
    /// </summary>
    IValueLease<T> Acquire<T>(string id) where T : notnull;

    /// <summary>
    /// Acquire a lease when the expected service type cannot be used as a
    /// generic type argument — the type is defined in the plugin's own
    /// assembly (host tooling, tests referencing the plugin). The instance is
    /// returned as <see cref="object"/> and checked against <paramref name="expectedType"/>.
    IValueLease<object> Acquire(string id, Type expectedType);

    /// <summary>
    /// PLAN §11/§44: a lease on the owning plugin itself. Held for the duration
    /// of work (an agent run, a tool batch) so a reload cannot unload the plugin
    /// mid-work. The host implementation returns a handle that counts toward
    /// the owning generation's live lease count; the generic parameter is
    /// chosen by the caller so the lease type stays inside the caller's ALC.
    /// </summary>
    IValueLease<T> AcquireSelfLease<T>() where T : notnull;

    /// <summary>Convenience overload: acquire and immediately resolve the instance.</summary>
    T Resolve<T>(string id) where T : notnull;
}

/// <summary>
/// A lease on a plugin-owned service. Disposing the lease decrements the
/// owning plugin's active-use counter (this is what unblocks a reload).
/// </summary>
public interface IValueLease<T> : IAsyncDisposable, IDisposable where T : notnull
{
    /// <summary>Resolve the leased instance. Throws <see cref="ServiceUnavailableException"/> if the owner is draining/removed.</summary>
    T Value { get; }
}

/// <summary>Thrown when acquiring a service whose owning plugin is draining, already unloading, or missing.</summary>
public sealed class ServiceUnavailableException(string id, string reason)
    : InvalidOperationException($"Service '{id}' is unavailable: {reason}");
