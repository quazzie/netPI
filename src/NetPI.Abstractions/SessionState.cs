using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace NetPI;

/// <summary>
/// Per-session state a plugin keeps while a session lives: a gate, a lock, a baseline, a "was reset" counter. Sessions
/// are not a bounded set — every subagent spawn creates one, and <c>sessions.delete</c> removes its rows but nothing in
/// this process — so the owner of such a map drops its entry when <see cref="EventTypes.SessionDeleted"/> names the
/// session. Six plugins kept six maps and six hand-written "forget" hooks, and the map outlived the session in all six
/// (idea-bv3iw4), so the rule lives here once: construct with the bus and the release is part of owning the map. The
/// subscription is the plugin's own (<c>IPluginContext.Events</c> hands out a scoped bus), so a reload takes it away too.
/// </summary>
public sealed class SessionState<T>
{
    private readonly ConcurrentDictionary<string, T> _values = new(StringComparer.Ordinal);

    /// <summary>A map with no bus behind it: the caller releases entries itself with <see cref="Forget"/>.</summary>
    public SessionState() { }

    /// <summary>A map that drops a session's entry when that session is deleted.</summary>
    public SessionState(IEventBus events) => events.Subscribe(EventTypes.SessionDeleted, OnSessionDeleted);

    /// <summary>How many sessions have an entry — what a test asserts shrank (and what a leak shows as growing).</summary>
    public int Count => _values.Count;

    /// <summary>The sessions that still have an entry.</summary>
    public IReadOnlyCollection<string> Sessions => _values.Keys.ToArray();

    /// <summary>This session's value, created by <paramref name="create"/> the first time it is asked for.</summary>
    public T GetOrAdd(string sessionId, Func<string, T> create) => _values.GetOrAdd(sessionId, create);

    public bool TryGetValue(string sessionId, out T value) => _values.TryGetValue(sessionId, out value!);

    public T this[string sessionId] { get => _values[sessionId]; set => _values[sessionId] = value; }

    public bool Contains(string sessionId) => _values.ContainsKey(sessionId);

    /// <summary>
    /// Drop this session's entry. The value is dropped, not disposed: whoever took it (a held semaphore, a running
    /// compaction) may still be using it, and the object itself is small enough that letting it go is the whole point.
    /// </summary>
    public bool Forget(string sessionId) => _values.TryRemove(sessionId, out _);

    /// <summary>Every session's entry at once.</summary>
    public void Clear() => _values.Clear();

    private void OnSessionDeleted(BusEvent e)
    {
        // sessions.delete publishes { id } as a payload object; a bus that carries the session on the envelope works too.
        var id = (e.As<JsonObject>()?["id"] is JsonValue v && v.TryGetValue<string>(out var named) ? named : null) ?? e.SessionId;
        if (id is { Length: > 0 }) Forget(id);
    }
}
