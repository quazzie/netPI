using System.Collections.Concurrent;
using NetPI;

namespace NetPI.Host.Sessions;

/// <summary>
/// The host's answer to <see cref="ISessionPresence"/>: a window's heartbeats (last seen per session, expired
/// after <see cref="Expiry"/>) plus the sessions that wait on the user, which are open while they wait even
/// when no window shows them. A window that dies without saying so (a crash, a pulled plug) stops being counted
/// when its heartbeats expire, so "open" is an upper bound on what is really open, never a leak.
/// </summary>
public sealed class SessionPresence : ISessionPresence
{
    /// <summary>
    /// How long a heartbeat keeps a session open: three times the web client's 30 s heartbeat, so one lost beat
    /// (a busy tab, a slow machine) does not drop the session.
    /// </summary>
    public static readonly TimeSpan Expiry = TimeSpan.FromSeconds(90);

    private readonly IServiceRegistry _services;
    private readonly Func<DateTimeOffset> _clock;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _seen = new(StringComparer.Ordinal);

    public SessionPresence(IServiceRegistry services, Func<DateTimeOffset>? clock = null)
    {
        _services = services;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>A window is showing this session (an open, a heartbeat).</summary>
    public void MarkOpen(string sessionId)
    {
        var now = _clock();
        _seen[sessionId] = now;
        Prune(now);
    }

    /// <summary>A window stopped showing this session (a close, a page unload): the other windows' heartbeats and
    /// the session's waits keep counting.</summary>
    public void MarkClosed(string sessionId) => _seen.TryRemove(sessionId, out _);

    /// <summary>Open now: a live heartbeat, or a wait on the user that any window can answer.</summary>
    public bool IsOpen(string sessionId) => Alive(sessionId) || Waiting(sessionId);

    /// <summary>Everything open now: the union of the live heartbeats and the waits.</summary>
    public IReadOnlyCollection<string> OpenSessions()
    {
        var now = _clock();
        var open = _seen.Where(kv => now - kv.Value < Expiry).Select(kv => kv.Key).ToHashSet(StringComparer.Ordinal);
        Prune(now);
        foreach (var id in WaitingSessions()) open.Add(id);
        return open;
    }

    private bool Alive(string sessionId) =>
        _seen.TryGetValue(sessionId, out var seen) && _clock() - seen < Expiry;

    /// <summary>The session has a call that waits on the user: the chat goes on when the answer comes, from any window.</summary>
    private bool Waiting(string sessionId)
    {
        var runtime = _services.Get<IAgentRuntime>();
        if (runtime is null) return false;
        foreach (var kind in WaitingKinds)
            if (runtime.Waits(sessionId, kind).Count > 0) return true;
        return false;
    }

    private IEnumerable<string> WaitingSessions()
    {
        var runtime = _services.Get<IAgentRuntime>();
        if (runtime is null) yield break;
        foreach (var w in runtime.Waits())
            if (WaitingKinds.Contains(w.Kind)) yield return w.SessionId;
    }

    /// <summary>The wait kinds that keep a chat open: a question, a guardrail, a plan (entering or deciding), a schedule.</summary>
    private static readonly string[] WaitingKinds = ["ask", "guard", "plan", "plan-enter", "schedule"];

    private void Prune(DateTimeOffset now)
    {
        foreach (var kv in _seen)
            if (now - kv.Value >= Expiry) _seen.TryRemove(kv.Key, out _);
    }
}
