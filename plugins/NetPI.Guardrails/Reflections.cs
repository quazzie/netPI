using System.Collections.Concurrent;

namespace NetPI.Guardrails;

/// <summary>
/// The ask rules each chat has already been told about (<c>guardrails.askReflect</c>): the first call that meets one is
/// refused with why, and the same call again (same rule, same subject) asks the user. Kept in memory for an hour: a
/// reload of this plugin or a restart only means the agent is told once more.
/// </summary>
internal sealed class Reflections(Func<DateTimeOffset>? clock = null)
{
    public static readonly TimeSpan Keep = TimeSpan.FromHours(1);
    private const int MaxEntries = 2000;
    private readonly Func<DateTimeOffset> _now = clock ?? (() => DateTimeOffset.UtcNow);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _seen = new(StringComparer.Ordinal);

    private static string Key(string sessionId, Verdict v) => sessionId + "\n" + v.Rule + "\n" + v.Subject;

    public bool Seen(string sessionId, Verdict v) =>
        _seen.TryGetValue(Key(sessionId, v), out var at) && _now() - at < Keep;

    public void Note(string sessionId, Verdict v)
    {
        var now = _now();
        _seen[Key(sessionId, v)] = now;
        if (_seen.Count <= MaxEntries) return;
        foreach (var old in _seen.Where(kv => now - kv.Value >= Keep).Select(kv => kv.Key).ToList()) _seen.TryRemove(old, out _);
        // still full of fresh entries (a runaway loop): forget the oldest half rather than grow
        if (_seen.Count > MaxEntries)
            foreach (var old in _seen.OrderBy(kv => kv.Value).Take(_seen.Count / 2).Select(kv => kv.Key).ToList()) _seen.TryRemove(old, out _);
    }
}
