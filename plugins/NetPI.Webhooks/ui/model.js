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

/** A one-line description of the limits, for the row's meta line. */
export function limitsLine(h) {
  const bits = [];
  if (h.dedupe?.key) bits.push(`dedupe ${h.dedupe.minutes ?? 60} min`);
  bits.push(`${h.maxPerHour ?? 6}/h`);
  bits.push(`${h.maxConcurrent ?? 2} at once`);
  return bits.join(' · ');
}

/** How to call the receiver, for the copy box: the curl line a sender's admin can adapt. */
export function curlLine(base, id, token) {
  return `curl -X POST ${base}${id} -H "Authorization: Bearer ${token}" -H "Content-Type: application/json" -d '{"host":"nuc","service":"Kernel errors","state":"CRIT","output":"test"}'`;
}
