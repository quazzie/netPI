// Shared clocks for relative / elapsed times (one interval each, started on first use).
let now = $state(Date.now());
let started = false;
let sec = $state(Date.now());
let secStarted = false;

/** Updates every 30s (for "5m ago" labels). */
export function clockNow() {
  if (!started && typeof window !== 'undefined') {
    started = true;
    setInterval(() => (now = Date.now()), 30_000);
  }
  return now;
}

/** Updates every second (for live elapsed timers). */
export function secondNow() {
  if (!secStarted && typeof window !== 'undefined') {
    secStarted = true;
    setInterval(() => (sec = Date.now()), 1000);
  }
  return sec;
}
