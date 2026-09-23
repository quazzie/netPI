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
