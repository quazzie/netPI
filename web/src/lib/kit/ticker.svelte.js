// Shared clock for relative times (one interval for every <TimeAgo>).
let now = $state(Date.now());
let started = false;

export function clockNow() {
  if (!started && typeof window !== 'undefined') {
    started = true;
    setInterval(() => (now = Date.now()), 30_000);
  }
  return now;
}
