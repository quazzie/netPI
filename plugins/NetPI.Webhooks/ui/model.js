// Pure helpers for the Webhooks tab (unit-testable: no Svelte, no DOM).

/** The state of a webhook as the row shows it: { key, tone, label }. */
export function rowState(h) {
  if (!h.enabled) {
    return h.proposedBy
      ? { key: 'proposed', tone: 'warn', label: 'proposed by an agent: enable it to let it run' }
      : { key: 'paused', tone: 'idle', label: 'paused' };
  }
  switch (h.lastStatus) {
    case 'started':
      return { key: 'started', tone: 'ok', label: 'last delivery started a chat' };
    case 'appended':
      return { key: 'appended', tone: 'ok', label: 'last delivery was added to the chat that handles the incident' };
    case 'held':
      return { key: 'held', tone: 'ok', label: h.lastReason ? `last delivery: ${h.lastReason}` : 'last delivery is being collected for a chat' };
    case 'duplicate':
      return { key: 'duplicate', tone: 'idle', label: 'last delivery repeated an alert the chat already had' };
    case 'skipped':
      return { key: 'skipped', tone: 'idle', label: h.lastReason ? `last delivery skipped: ${h.lastReason}` : 'last delivery skipped' };
    case 'failed':
      return { key: 'failed', tone: 'err', label: h.lastReason ? `last delivery failed: ${h.lastReason}` : 'last delivery failed' };
    default:
      return { key: 'idle', tone: 'ok', label: 'waiting for the first delivery' };
  }
}

/** The agent choice as the form's two controls: the select value and the free-text list. */
export function agentToForm(agent) {
  const a = (agent ?? '').trim();
  if (a === '' || a === 'any' || a === 'free') return { agentChoice: a, agentList: '' };
  if (a.includes(',')) return { agentChoice: '__list', agentList: a };
  return { agentChoice: a, agentList: '' };
}

export function formToAgent(f) {
  return f.agentChoice === '__list' ? f.agentList.split(',').map((s) => s.trim()).filter(Boolean).join(',') : f.agentChoice;
}

/** The match conditions as editable text, and back (null when the text is not a JSON array). */
export function matchToText(match) {
  return Array.isArray(match) && match.length ? JSON.stringify(match, null, 2) : '';
}

export function textToMatch(text) {
  const t = (text ?? '').trim();
  if (!t) return [];
  try {
    const v = JSON.parse(t);
    return Array.isArray(v) ? v : null;
  } catch {
    return null;
  }
}

/** A one-line description of how alerts are grouped, for the row's meta line. */
export function limitsLine(h) {
  const bits = [];
  if (h.group?.key) bits.push(`one chat per ${h.group.key} (${h.group.debounceSeconds ?? 90} s window)`);
  else bits.push('every alert its own chat');
  if (h.maxChatsPerHour) bits.push(`fuse ${h.maxChatsPerHour}/h`);
  return bits.join(' · ');
}

/** The hooks as editable text, and back (null when the text is not a JSON array). */
export function hooksToText(hooks) {
  return Array.isArray(hooks) && hooks.length ? JSON.stringify(hooks, null, 2) : '';
}

export function textToHooks(text) {
  const t = (text ?? '').trim();
  if (!t) return [];
  try {
    const v = JSON.parse(t);
    return Array.isArray(v) ? v : null;
  } catch {
    return null;
  }
}

/** One hook as a line: "finished → run ssh server … · when verdict not in noise,resolved · once notify". */
export function hookLine(h) {
  const action = h.do?.type === 'command' ? `run ${h.do.command}${h.do.args?.length ? ' ' + h.do.args.join(' ') : ''}` : h.do?.type === 'http' ? `${h.do.method ?? 'POST'} ${h.do.url}` : '?';
  const when = (h.when ?? []).map((c) => {
    const op = c.in ? `in ${c.in.join(',')}` : c.notIn ? `not in ${c.notIn.join(',')}` : c.equals !== undefined ? `= ${c.equals}` : c.matches ? `~ ${c.matches}` : 'exists';
    return `${c.path} ${op}`;
  });
  return [`${h.on} → ${action}`, when.length ? `when ${when.join(' and ')}` : null, h.on === 'stalled' ? `after ${h.afterMinutes ?? 10} min` : null, h.once ? `once ${h.once}` : null]
    .filter(Boolean)
    .join(' · ');
}

/** An incident as a line: "nuc · open · 3 alerts" or "nuc · collecting, chat starts in 40 s". */
export function incidentLine(i, nowMs = Date.now()) {
  const alerts = `${i.alertCount ?? 1} alert${(i.alertCount ?? 1) === 1 ? '' : 's'}`;
  if (i.state === 'collecting') {
    const left = Math.max(0, Math.round((Date.parse(i.startsAt) - nowMs) / 1000));
    return i.waiting ? `${i.key} · waiting to start: ${i.waiting}` : `${i.key} · collecting, chat starts in ${left} s · ${alerts}`;
  }
  return `${i.key} · open · ${alerts}` + (i.awaiting ? ' · chat working' : i.verdict ? ` · verdict ${i.verdict}` : '');
}

/** How to call the receiver, for the copy box: the curl line a sender's admin can adapt. */
export function curlLine(base, id, token) {
  return `curl -X POST ${base}${id} -H "Authorization: Bearer ${token}" -H "Content-Type: application/json" -d '{"host":"nuc","service":"Kernel errors","state":"CRIT","output":"test"}'`;
}
