export const ACTIVE = new Set(['running', 'queued', 'yielded']);
export const TERMINAL = new Set(['completed', 'failed', 'cancelled']);

/** StatusDot status for an agent status. */
export function agentDot(s) {
  if (s === 'failed') return 'error';
  if (s === 'cancelled') return 'cancelled';
  return s;
}

export function upsert(list, item, key = 'id') {
  if (!item) return list;
  const arr = list ?? [];
  const i = arr.findIndex((x) => x[key] === item[key]);
  if (i < 0) return [item, ...arr];
  const next = arr.slice();
  next[i] = item;
  return next;
}

export function shortModel(ref) {
  if (!ref) return '';
  const i = ref.indexOf('/');
  return i >= 0 ? ref.slice(i + 1) : ref;
}

export function firstLine(s, n = 140) {
  if (!s) return '';
  const l = String(s).split('\n').find((x) => x.trim()) ?? '';
  return l.length > n ? l.slice(0, n - 1) + '…' : l;
}

/**
 * Keeps a running job in the row it first got, so nothing slides when another one finishes. `prev` is the last placement
 * (one entry per row: an owner or null), `owners` who holds an instance now, `capacity` how many rows the agent has.
 * A finished job leaves a hole in its own row; a new one takes the first hole. Rows beyond `capacity` exist only while
 * something still runs in them (an agent that was reduced while busy).
 */
export function placeSlots(prev, owners, capacity) {
  const key = (o) => o.leaseId ?? `${o.agentId}@${o.since}`;
  const now = new Map((owners ?? []).map((o) => [key(o), o]));
  const out = (prev ?? []).map((o) => (o && now.has(key(o)) ? now.get(key(o)) : null));
  while (out.length < capacity) out.push(null);
  const kept = new Set(out.filter(Boolean).map(key));
  for (const o of now.values()) {
    if (kept.has(key(o))) continue;
    let i = out.findIndex((x, n) => !x && n < capacity);
    if (i < 0) i = out.findIndex((x) => !x);
    if (i < 0) i = out.push(null) - 1;
    out[i] = o;
  }
  while (out.length > capacity && out[out.length - 1] == null) out.pop();
  return out;
}

/** A shell command as one short line: the first line, without the `cd <dir> &&` an agent puts in front of it. */
export function shortCommand(cmd) {
  const first = String(cmd ?? '').split('\n').find((l) => l.trim()) ?? '';
  return first
    .replace(/^\s*(?:cd\s+(?:"[^"]*"|'[^']*'|\S+)\s*(?:&&|;)\s*)+/, '')
    .trim()
    .replace(/\s+/g, ' ');
}
