// Window presence: the host keeps a session open while a window shows it (the idle compaction beats the
// provider's prompt-cache TTL for the sessions a window is on), and a window's marks expire 90 s after its
// last heartbeat. This tracker is the window's side of that: it knows which sessions this window has open
// and marks them — at once on open and on close, and again for all of them every 15 s, so the host's
// expiry never drops a session a window is showing.
//
// A mark goes out only on an open connection: a window whose socket is down is not showing anything, and
// its marks are allowed to expire. The reconnect resends them (the host's marks for the new connection are
// fresh), which is why `resend()` is the reconnect's call, not a timer's.
//
// The module stays free of Svelte reactivity (it is tested in a plain vm, like the rpc's deadline tests):
// the app wires it to the tab state and the connection's.

export const HEARTBEAT_MS = 15_000;   // well under the host's 90 s expiry, even browser-throttled

export function presenceTracker({ rpc, connectionOpen }) {
  const open = new Set();
  let timer = 0;

  /** Fire one mark; a failure is not the world ending — the next beat (or the reconnect's resend) carries it. */
  function send(sessionId, isOpen) {
    if (!connectionOpen()) return;
    void rpc('session.presence', { sessionId, open: isOpen }).catch(() => {});
  }

  function beat() {
    for (const id of open) send(id, true);
  }

  return {
    /** The sessions this window shows (the heartbeat's set), in the order they opened. */
    openSessions: () => [...open],
    isOpen: (id) => open.has(id),

    /** A tab came open: mark it at once, not at the next beat. */
    open: (id) => {
      if (!id || open.has(id)) return;
      open.add(id);
      send(id, true);
    },

    /** A tab closed (or the session went): the host may drop the mark at once. */
    close: (id) => {
      if (!open.delete(id)) return;
      send(id, false);
    },

    /** Everything this window shows, at once — the reconnect's call: the host's marks for the new
     *  connection are fresh, and a first load's restored tabs were never `open()`ed. */
    resend: beat,

    /** The 15 s heartbeat; `stop` is its mirror (the page's teardown). */
    start: () => {
      if (timer) return;
      timer = setInterval(beat, HEARTBEAT_MS);
    },
    stop: () => {
      if (!timer) return;
      clearInterval(timer);
      timer = 0;
    },
  };
}
