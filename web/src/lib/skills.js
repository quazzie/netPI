// A session's skills (skills.list) as entries of the composer's / popup: "skill:<name>" inserts "/skill:<name> ", and the
// message is sent as typed (the skills plugin loads the skill for it). Cached per session; refreshed in the background.
import { rpc } from './rpc.svelte.js';

const TTL = 10_000;
const cache = new Map(); // sessionId → { at, items }

/** The cached entries (possibly empty); when a refresh brings new ones, onUpdate is called. */
export function skillCommands(sessionId, onUpdate) {
  if (!sessionId) return [];
  let entry = cache.get(sessionId);
  if (!entry || Date.now() - entry.at > TTL) {
    entry ??= { at: 0, items: [] };
    entry.at = Date.now();
    cache.set(sessionId, entry);
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
