// Persistence helpers. localStorage is the fast path (read synchronously at startup); the host's
// ui.state store is the durable copy (survives a WebView profile reset). Every access is wrapped in
// try/catch because storage may be unavailable.
import { rpc } from './rpc.svelte.js';

export function load(key, fallback) {
  try {
    const raw = localStorage.getItem(key);
    return raw == null ? fallback : JSON.parse(raw);
  } catch {
    return fallback;
  }
}

export function save(key, value) {
  try {
    localStorage.setItem(key, JSON.stringify(value));
  } catch {}
}

export function remove(key) {
  try {
    localStorage.removeItem(key);
  } catch {}
}

const timers = new Map();

/** Save locally now and to the host (ui.state.set) after a debounce. */
export function persist(key, value, delay = 600) {
  save(key, value);
  clearTimeout(timers.get(key));
  timers.set(
    key,
    setTimeout(() => {
      timers.delete(key);
      rpc('ui.state.set', { key, value }, { timeout: 10_000 }).catch(() => {});
    }, delay),
  );
}

/** Fetch the host copy (null when missing/unavailable). */
export async function fetchRemote(key) {
  try {
    return await rpc('ui.state.get', { key }, { timeout: 5_000 });
  } catch {
    return null;
  }
}
