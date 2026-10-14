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
      return { key: 'appended', tone: 'ok', label: 'last delivery was added to the chat that handles its conversation' };
    case 'waiting':
      return { key: 'waiting', tone: 'ok', label: h.lastReason ? `last delivery waits for its chat: ${h.lastReason}` : 'last delivery waits for its chat to start' };
    case 'duplicate':
      return { key: 'duplicate', tone: 'idle', label: 'last delivery repeated what the chat already had' };
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

/** A one-line description of a webhook's limits, for the row's meta line. */
export function limitsLine(h) {
  const bits = [h.timeoutMinutes ? `timeout ${h.timeoutMinutes} min` : 'no timeout'];
  if (h.maxChatsPerHour) bits.push(`fuse ${h.maxChatsPerHour}/h`);
  return bits.join(' · ');
}

/** A hook (pre or post) as form fields, and back: the command and its arguments, one per line. An empty command is {} (clears the hook). */
export function hookToForm(h) {
  return { command: h?.command ?? '', args: (h?.args ?? []).join('\n') };
}

export function formToHook(command, args) {
  const c = (command ?? '').trim();
  if (!c) return {};
  const a = (args ?? '').split('\n').map((s) => s.trim()).filter(Boolean);
  return a.length ? { command: c, args: a } : { command: c };
}

/** One hook as a line for the row: the program and its arguments. */
export function hookLine(h) {
  return `${h.command}${h.args?.length ? ' ' + h.args.join(' ') : ''}`;
}

/** A conversation as a line: "nuc · open · 3 items" or "nuc · waiting to start: the model is not loaded · 2 items". */
export function incidentLine(i) {
  const alerts = `${i.alertCount ?? 1} item${(i.alertCount ?? 1) === 1 ? '' : 's'}`;
  if (i.state === 'waiting') {
    return `${i.key} · waiting to start` + (i.waiting ? `: ${i.waiting}` : '') + ` · ${alerts}`;
  }
  return `${i.key} · open · ${alerts}` + (i.awaiting ? ' · chat working' : i.verdict ? ` · verdict ${i.verdict}` : '');
}

/** How to call the receiver, for the copy box: the curl line a sender's admin can adapt. */
export function curlLine(base, id, token) {
  return `curl -X POST ${base}${id} -H "Authorization: Bearer ${token}" -H "Content-Type: application/json" -d '{"message":"test"}'`;
}
