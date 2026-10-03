// The refresh loop a plugin tab used to write out by hand: load on mount, load again on the events that can change what
// the tab shows, poll while it is visible, one load in flight, nothing left running behind it.
//
//   const tab = useRefresh(ctx, { load, events: ['plugins.changed', 'tools.changed'], pollMs: 5000 });
//   <IconButton onclick={tab.refresh} />
//   export function setVisible(v) { tab.setVisible(v); }        // what createTab forwards onShow/onHide to
//
// `events` are coalesced into one load (delayMs). While the tab is hidden they only mark it stale, and the next time it is
// shown it loads once. `visible: () => visible` reads a visibility the parent already holds — a view inside a tab that
// passes it down — instead of the helper's own. A load that throws is the tab's own error to show; here it only ends the
// request. Subscriptions go through ctx, so ctx.dispose() takes them with it, and the timers stop on destroy.
import { onDestroy, onMount } from 'svelte';

export function useRefresh(ctx, { load, events = [], pollMs = 0, delayMs = 300, visible: parent = null } = {}) {
  let shown = $state(true); // the helper's own visibility; the parent's wins when it has one
  let dirty = false;
  let disposed = false;
  let timer = 0; // the coalesced event load
  let pollTimer = 0;
  let inflight = null;
  let again = false; // a request came in during the load that runs
  const isVisible = () => (parent ? parent() : shown);

  /**
   * Load now. Never two at a time: a slow answer is the one that finishes first, not the one that started last. A request
   * that arrives while a load runs (a click that changed what the load is about, an event) is one more load after it, not
   * the answer of the load that started before it: what that one read may be what the request just made out of date.
   */
  function refresh() {
    if (disposed) return Promise.resolve();
    if (inflight) {
      again = true;
      return inflight;
    }
    inflight = (async () => {
      try {
        do {
          again = false;
          try {
            await load();
          } catch {
            /* the tab shows its own error */
          }
        } while (again && !disposed);
      } finally {
        inflight = null;
      }
    })();
    return inflight;
  }

  /** Something said the view is out of date: load in a moment (everything that lands in that window is one load). */
  function schedule(ms = delayMs) {
    if (disposed) return;
    if (!isVisible()) {
      dirty = true;
      return;
    }
    clearTimeout(timer);
    timer = setTimeout(refresh, ms);
  }

  /** The poll is armed by the load before it, not by a clock: it cannot stack requests on a slow rpc. */
  function poll() {
    if (disposed) return;
    clearTimeout(pollTimer);
    pollTimer = setTimeout(() => {
      if (disposed) return;
      if (isVisible()) refresh().then(poll);
      else poll();
    }, pollMs);
  }

  function setVisible(v) {
    shown = v;
    if (!v || !dirty) return;
    dirty = false;
    refresh();
  }

  onMount(() => {
    const offs = events.map((e) => ctx.on(e, () => schedule()));
    refresh();
    if (pollMs) poll();
    return () => offs.forEach((off) => off());
  });
  onDestroy(() => {
    disposed = true;
    clearTimeout(timer);
    clearTimeout(pollTimer);
  });

  return {
    refresh,
    schedule,
    setVisible,
    /** False once the component is gone: an answer that arrives after that must not be applied. */
    get alive() {
      return !disposed;
    },
    get visible() {
      return isVisible();
    },
  };
}