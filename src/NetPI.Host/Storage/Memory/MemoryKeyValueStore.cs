namespace NetPI.Host.Storage.Memory;

/// <summary>
/// The small string values the core keeps (UI state, remembered fork-reset keys). Keys are ordinal, so
/// <c>ui.state</c> and <c>UI.state</c> are two values, and a null value removes the key rather than storing nothing.
/// </summary>
internal sealed class MemoryKeyValueStore(MemoryStorage store) : IKeyValueStore
{
    public string? Get(string key)
    {
        lock (store.Lock)
            return store.ValuesByKey.GetValueOrDefault(key);
    }

    public void Set(string key, string? value)
    {
        lock (store.Lock)
            store.Atomic(() =>
            {
                store.Touch(() => (string?)store.ValuesByKey.GetValueOrDefault(key), was =>
                {
                    if (was is null) store.ValuesByKey.Remove(key);
                    else store.ValuesByKey[key] = was;
                });
                if (value is null) store.ValuesByKey.Remove(key);
                else store.ValuesByKey[key] = value;
            });
    }
}
