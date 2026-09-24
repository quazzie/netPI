// Small formatting helpers (no allocations in hot paths beyond the result string).

export function toMs(t) {
  if (t == null) return 0;
  if (typeof t === 'number') return t;
  const v = Date.parse(t);
  return Number.isNaN(v) ? 0 : v;
}

/** "3.4s", "820ms", "1m 20s", "2h 5m" */
export function duration(ms) {
  if (ms == null || !Number.isFinite(ms)) return '';
  if (ms < 1000) return `${Math.max(0, Math.round(ms))}ms`;
  const s = ms / 1000;
  if (s < 10) return `${(Math.floor(s * 10) / 10).toFixed(1)}s`;
  // floor, not round: 59.6s must not read "60s" (or "12m 60s")
  if (s < 60) return `${Math.floor(s)}s`;
  const m = Math.floor(s / 60);
  const rs = Math.floor(s - m * 60);
  if (m < 60) return rs ? `${m}m ${rs}s` : `${m}m`;
  const h = Math.floor(m / 60);
  const rm = m - h * 60;
  return rm ? `${h}h ${rm}m` : `${h}h`;
}

/** "12s ago", "5m ago", "3h ago", "yesterday", "Mar 4" */
export function timeAgo(t, now = Date.now()) {
  const ms = toMs(t);
  if (!ms) return '';
  const d = Math.max(0, now - ms);
  if (d < 45_000) return 'just now';
  if (d < 3_600_000) return `${Math.round(d / 60_000)}m ago`;
  if (d < 86_400_000) return `${Math.round(d / 3_600_000)}h ago`;
  const days = Math.floor(d / 86_400_000);
  if (days < 2) return 'yesterday';
  if (days < 7) return `${days}d ago`;
  return new Date(ms).toLocaleDateString(undefined, { month: 'short', day: 'numeric' });
}

/** Compact time-of-day or date for message stamps. */
export function stamp(t) {
  const ms = toMs(t);
  if (!ms) return '';
  const d = new Date(ms);
  const today = new Date();
  const sameDay = d.toDateString() === today.toDateString();
  return sameDay
    ? d.toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit' })
    : d.toLocaleString(undefined, { month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit' });
}

/** 262144 → "262k", 1048576 → "1M", 1234 → "1.2k" */
export function tokens(n) {
  if (n == null || !Number.isFinite(n)) return '';
  if (n < 1000) return String(n);
  if (n < 10_000) return `${(n / 1000).toFixed(1).replace(/\.0$/, '')}k`;
  if (n < 1_000_000) return `${Math.round(n / 1000)}k`;
  return `${(n / 1_000_000).toFixed(n < 10_000_000 ? 1 : 0).replace(/\.0$/, '')}M`;
}

export function bytes(n) {
  if (n == null) return '';
  if (n < 1024) return `${n} B`;
  if (n < 1024 * 1024) return `${(n / 1024).toFixed(n < 10240 ? 1 : 0)} KB`;
  if (n < 1024 ** 3) return `${(n / 1024 / 1024).toFixed(1)} MB`;
  return `${(n / 1024 ** 3).toFixed(1)} GB`;
}

/** Bucket for session list grouping. */
export function recencyBucket(t, now = new Date()) {
  const ms = toMs(t);
  const startToday = new Date(now.getFullYear(), now.getMonth(), now.getDate()).getTime();
  if (ms >= startToday) return 'Today';
  if (ms >= startToday - 86_400_000) return 'Yesterday';
  if (ms >= startToday - 6 * 86_400_000) return 'Previous 7 days';
  return 'Earlier';
}

export function plural(n, word, pluralWord) {
  return `${n} ${n === 1 ? word : (pluralWord ?? word + 's')}`;
}

/** Shorten a path relative to a base (project folder); normalizes separators for display. */
export function relPath(p, base) {
  if (!p) return '';
  const norm = String(p).replace(/\\/g, '/');
  if (base) {
    const b = String(base).replace(/\\/g, '/').replace(/\/+$/, '');
    if (b && (norm === b || norm.toLowerCase().startsWith(b.toLowerCase() + '/'))) {
      return norm.slice(b.length + 1) || '.';
    }
  }
  return norm;
}

export function basename(p) {
  const s = String(p ?? '').replace(/[\\/]+$/, '');
  const i = Math.max(s.lastIndexOf('/'), s.lastIndexOf('\\'));
  return i >= 0 ? s.slice(i + 1) : s;
}

export function truncate(s, n) {
  if (!s) return '';
  return s.length > n ? s.slice(0, n - 1) + '…' : s;
}

export function firstLine(s) {
  if (!s) return '';
  const i = s.indexOf('\n');
  return i < 0 ? s : s.slice(0, i);
}
