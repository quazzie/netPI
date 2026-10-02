namespace NetPI;

/// <summary>
/// A small typed bag a context carries for things the core does not know about: the part that builds a tool or run context
/// puts in what it has (the checkout a session works in, the slot a run holds), and a reader that knows the type asks for it. The
/// core types that carry one name none of those types, so the core stays free of the concepts built on top of it.
/// The bag is per call or per run and short-lived; it is not a place to keep state.
/// </summary>
public sealed class FeatureSet
{
    private readonly Dictionary<Type, object> _items = [];

    /// <summary>The value stored for <typeparamref name="T"/>, or null.</summary>
    public T? Get<T>() where T : class
    {
        lock (_items) return _items.TryGetValue(typeof(T), out var v) ? (T)v : null;
    }

    /// <summary>Store the value for <typeparamref name="T"/>; null removes it.</summary>
    public void Set<T>(T? value) where T : class
    {
        lock (_items)
        {
            if (value is null) _items.Remove(typeof(T));
            else _items[typeof(T)] = value;
        }
    }
}
