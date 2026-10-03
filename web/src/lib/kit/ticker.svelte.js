// Shared clocks for relative / elapsed times: one interval for every row that reads one, and none left running once the
// last of them is gone (a plugin tab bundles its own copy of the kit, and its clocks used to outlive the tab).
import { onDestroy } from 'svelte';

const clocks = new Map(); // ms → { value: $state, timer, users }

/**
 * A clock that ticks every `ms`. Call it from a component's script — that is where it subscribes — and call the getter it
 * returns inside a `$derived`: that read is what ties the row to the clock.
 */
export function clock(ms) {
  let c = clocks.get(ms);
  if (!c) {
    const value = $state(Date.now());
    clocks.set(ms, (c = { value, timer: 0, users: 0 }));
  }
  c.users++;
  if (!c.timer) {
    c.value = Date.now();
    c.timer = setInterval(() => (c.value = Date.now()), ms);
  }
  onDestroy(() => {
    if (--c.users === 0) {
      clearInterval(c.timer);
      c.timer = 0;
    }
  });
  return () => c.value;
}

/** The 30s clock for "5m ago" labels. */
export function clockNow() {
  return clock(30_000);
}

/** The 1s clock for live elapsed timers. */
export function secondNow() {
  return clock(1000);
}
