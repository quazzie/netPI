namespace NetPI;

/// <summary>
/// Which sessions a window has open right now. A window that shows a session asserts it with the
/// <c>session.presence</c> RPC (a heartbeat while it stays open, an open/close at once), and a session that
/// waits on the user (a question, a plan decision) counts as open no matter what: its answer can come from
/// any window. The idle compaction sweeps the open sessions: a chat that is open — or about to continue — is
/// the one whose next turn should re-read a short, cheap context.
/// </summary>
public interface ISessionPresence
{
    /// <summary>A window is showing this session (an open, a heartbeat).</summary>
    void MarkOpen(string sessionId);

    /// <summary>A window stopped showing this session (a close, a page unload).</summary>
    void MarkClosed(string sessionId);

    /// <summary>Open now: a live heartbeat, or a wait on the user that any window can answer.</summary>
    bool IsOpen(string sessionId);

    /// <summary>Everything open now: the union of the live heartbeats and the waits.</summary>
    IReadOnlyCollection<string> OpenSessions();
}
