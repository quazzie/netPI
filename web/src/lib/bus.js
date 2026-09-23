// In-page event bus for server events (and a few local ones).
// Patterns: exact type ("agent.status"), prefix wildcard ("agent.*") or "*".
// Handlers receive (data, envelope) where envelope = { type, sid, d, seq, ts }.

const exact = new Map(); // type -> Set<fn>
const prefix = new Map(); // "agent." -> Set<fn>
const all = new Set();

function add(map, key, fn) {
  let set = map.get(key);
  if (!set) map.set(key, (set = new Set()));
  set.add(fn);
  return () => {
    set.delete(fn);
    if (!set.size) map.delete(key);
  };
}

export const bus = {
  on(pattern, fn) {
    if (pattern === '*') {
      all.add(fn);
      return () => all.delete(fn);
    }
    if (pattern.endsWith('.*')) return add(prefix, pattern.slice(0, -1), fn);
    return add(exact, pattern, fn);
  },

  /** Dispatch an event envelope ({ type, sid, d, seq, ts }). */
  emit(env) {
    const type = env.type;
    const d = env.d;
    const call = (fn) => {
      try {
        fn(d, env);
      } catch (e) {
        console.error(`[bus] handler for ${type} failed`, e);
      }
    };
    const ex = exact.get(type);
    if (ex) for (const fn of ex) call(fn);
    if (prefix.size) {
      for (const [p, set] of prefix) if (type.startsWith(p)) for (const fn of set) call(fn);
    }
    if (all.size) for (const fn of all) call(fn);
  },

  /** Emit a local (client-side) event. */
  local(type, d = {}, sid = null) {
    this.emit({ type, sid, d, seq: 0, ts: Date.now(), local: true });
  },
};
