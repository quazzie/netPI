// A session's skills (skills.list) as entries of the composer's / popup: "skill:<name>" inserts "/skill:<name> ", and the
// message is sent as typed (the skills plugin loads the skill for it). Cached per session; refreshed in the background.
import { rpc } from './rpc.svelte.js';

const TTL = 10_000;
const MAX = 16; // a fresh skill list is worth a re-fetch; the window that typed in the most sessions wins
export const cache = new Map(); // sessionId → { at, items }

/** Drop expired entries, then the oldest past the cap: with a cap the cache cannot keep one entry per session
 * ever touched (a TTL alone cannot bound it — expired entries of a window left idle would linger forever).
 * Trade-off: the `/` popup of an evicted chat refetches its list (a cheap skills.list). */
function evict() {
  const now = Date.now();
  for (const [id, e] of cache) if (now - e.at > TTL) cache.delete(id);
  while (cache.size > MAX) {
    let oldest = null;
    for (const [id, e] of cache) if (oldest === null || e.at < oldest[1].at) oldest = [id, e];
    if (!oldest) break;
    cache.delete(oldest[0]);
  }
}

/** The cached entries (possibly empty); when a refresh brings new ones, onUpdate is called. */
export function skillCommands(sessionId, onUpdate) {
  if (!sessionId) return [];
  let entry = cache.get(sessionId);
  if (!entry || Date.now() - entry.at > TTL) {
    entry ??= { at: 0, items: [] };
    entry.at = Date.now();
    cache.set(sessionId, entry);
    evict();
    const e = entry;
    rpc('skills.list', { sessionId }, { timeout: 8000 })
      .then((res) => {
        e.items = (res?.skills ?? []).filter((s) => !s.disabled).map(toCommand);
        onUpdate?.();
      })
      .catch(() => {}); // no skills plugin: no entries
  }
  return entry.items;
}

const toCommand = (s) => ({
  name: `skill:${s.name}`,
  description: s.description ?? '',
  argsHint: '[task]',
  source: 'skill',
  run: () => {},
});
