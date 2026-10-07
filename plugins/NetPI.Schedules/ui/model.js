// Pure helpers for the Schedules tab. No Svelte, so Node can test them (the E2E checks reasonKind against the
// literal strings ScheduleRunner.CheckAsync returns).

/** The cadence object a form shows: the RPC shape with `at` converted to the browser-local "YYYY-MM-DDTHH:mm". */
export function cadenceToForm(c) {
  const f = { kind: c?.kind ?? 'daily', atLocal: '', minutes: 60, time: '09:00', tz: c?.tz ?? '' };
  if (c?.kind === 'once' && c.at) f.atLocal = toLocalInput(new Date(c.at));
  if (c?.kind === 'every' && c.minutes != null) f.minutes = Number(c.minutes);
  if (c?.kind === 'daily') {
    if (c.time) f.time = c.time;
  }
  return f;
}

/** The RPC `cadence` object a form sends (an empty tz is omitted: the server defaults to the machine's zone). */
export function formToCadence(f) {
  if (f.kind === 'once') {
    return { kind: 'once', at: new Date(f.atLocal).toISOString() };
  }
  if (f.kind === 'every') {
    return { kind: 'every', minutes: Math.max(1, Math.round(Number(f.minutes) || 60)) };
  }
  const c = { kind: 'daily', time: f.time || '09:00' };
  if (f.tz) c.tz = f.tz;
  return c;
}

/** Two cadence objects, the same schedule? (The form sends a cadence only when it changed.) */
export function cadenceChanged(a, b) {
  if (a.kind !== b.kind) return true;
  if (a.kind === 'once') return new Date(a.at).getTime() !== new Date(b.at).getTime();
  if (a.kind === 'every') return Number(a.minutes) !== Number(b.minutes);
  return (a.time || '09:00') !== (b.time || '09:00') || (a.tz || '') !== (b.tz || '');
}

/** The row's right-hand label: "in 3 h", "tomorrow 07:30", "overdue", "paused", "finished" or "–". */
export function describeNext(s, now) {
  if (s.finishedAt) return 'finished';
  if (!s.enabled) return s.proposedBy ? 'proposed' : 'paused';
  if (!s.nextRunAt) return '–';
  const t = Date.parse(s.nextRunAt);
  if (Number.isNaN(t)) return '–';
  if (t < now - 120_000) return 'overdue';
  const d = t - now;
  if (d < 90_000) return 'now';
  if (d < 3_600_000) return `in ${Math.max(1, Math.round(d / 60_000))} m`;
  if (d < 86_400_000) return `in ${Math.round(d / 3_600_000)} h`;
  if (d < 172_800_000) {
    const at = new Date(t);
    return `tomorrow ${String(at.getHours()).padStart(2, '0')}:${String(at.getMinutes()).padStart(2, '0')}`;
  }
  return `in ${Math.round(d / 86_400_000)} d`;
}

/** A relative "N ago" for a UTC ISO stamp, for the row's last-run label. */
export function ago(iso, now) {
  if (!iso) return '';
  const t = Date.parse(iso);
  if (Number.isNaN(t)) return '';
  const d = now - t;
  if (d < 120_000) return 'just now';
  if (d < 3_600_000) return `${Math.round(d / 60_000)} m ago`;
  if (d < 86_400_000) return `${Math.round(d / 3_600_000)} h ago`;
  return `${Math.round(d / 86_400_000)} d ago`;
}

/**
 * The row's dot + label; the first match wins (docs/UI.md's narrow-panel rows). `running` is the set of session
 * ids that have a queued/running/yielded agent; `now` is the tab's clock tick.
 */
export function rowState(s, running, now) {
  if (s.finishedAt) return { key: 'finished', label: 'finished', tone: 'cancelled' };
  // an agent added or changed it: it runs once the user enables it (idea-96bh9b)
  if (!s.enabled && s.proposedBy) return { key: 'proposed', label: 'proposed by an agent: enable it to run', tone: 'warn' };
  if (!s.enabled) return { key: 'paused', label: 'paused', tone: 'cancelled' };
  if (s.lastSessionId && running.has(s.lastSessionId)) return { key: 'running', label: 'running', tone: 'running' };
  if (s.lastStatus === 'failed') return { key: 'failed', label: 'failed' + (s.lastReason ? `: ${short(s.lastReason)}` : ''), tone: 'error' };
  if (s.lastStatus === 'missed') return { key: 'missed', label: 'missed', tone: 'warn' };
  if (s.lastStatus === 'skipped') return { key: 'skipped', label: `skipped: ${reasonLabel(reasonKind(s.lastReason))}`, tone: 'warn' };
  if (s.lastStatus === 'ran') return { key: 'ran', label: `ran ${ago(s.lastRunAt, now) || ''}`.trim(), tone: 'ok' };
  if (s.nextRunAt && Date.parse(s.nextRunAt) < now - 120_000) return { key: 'overdue', label: 'overdue', tone: 'warn' };
  return { key: 'idle', label: 'not run yet', tone: 'idle' };
}

/**
 * A skip reason (free text from ScheduleRunner.CheckAsync) as a chip kind. The strings are the runner's own;
 * a reworded reason turns into "other" and the E2E's literal-string checks catch it.
 */
export function reasonKind(reason) {
  const r = String(reason ?? '');
  if (/previous run .* is still going|another start of it is under way/.test(r)) return 'running';
  if (/budget is exhausted/.test(r)) return 'budget';
  if (/agent \S+ (does not exist|is disabled|is unavailable)|no agent is available/.test(r)) return 'agent';
  if (/project \S+ no longer exists/.test(r)) return 'project';
  if (/already ran \d+ times/.test(r)) return 'cap';
  if (/no agent runtime/.test(r)) return 'runtime';
  return 'other';
}

/** The short chip label a reason kind shows (the full reason is the row's title and the expanded view). */
export function reasonLabel(kind) {
  switch (kind) {
    case 'running': return 'still running';
    case 'budget': return 'budget';
    case 'agent': return 'agent';
    case 'project': return 'project';
    case 'cap': return 'cap';
    case 'runtime': return 'no runtime';
    default: return kind;
  }
}

/** A one-line prompt preview, 80 characters. */
export function summarise(s) {
  const t = String(s?.prompt ?? '').replace(/\s+/g, ' ').trim();
  return t.length <= 80 ? t : t.slice(0, 79) + '…';
}

function short(r) {
  const t = String(r).replace(/\s+/g, ' ').trim();
  return t.length <= 40 ? t : t.slice(0, 39) + '…';
}

/** "2026-10-07T07:30:00.000Z" → "2026-10-07T07:30" in the browser's zone, for <input type="datetime-local">. */
function toLocalInput(d) {
  const p = (n) => String(n).padStart(2, '0');
  return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())}T${p(d.getHours())}:${p(d.getMinutes())}`;
}
