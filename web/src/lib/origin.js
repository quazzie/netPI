// Who a chat works for, for the chat header's chips (plugins/NetPI.Autonomy's Chats.cs reads the same meta): a chat a
// webhook or a schedule started, or a subagent of one (it works for its parent's origin). Tainted: it read text an
// outsider wrote — a webhook's payload from the start, or what meta.autonomy.tainted records (written by the Autonomy
// plugin after an external tool). Null for the owner's own chats and their subagents: nothing to say.

const MAX_DEPTH = 32;

/**
 * `session`: the chat; `byId(id)`: another session by id (its parent), or undefined when the page has not loaded it.
 * → { kind: 'webhook'|'schedule', name, label, tainted, taintedBy } | null
 */
export function chatOrigin(session, byId) {
  let origin = null;
  let tainted = false;
  let taintedBy = null;
  let s = session;
  for (let depth = 0; s && depth < MAX_DEPTH; depth++) {
    const m = s.meta ?? {};
    if (!tainted && m.autonomy?.tainted) {
      tainted = true;
      taintedBy = m.autonomy.taintedBy ?? null;
    }
    if (!origin && typeof m.webhook === 'string' && m.webhook) {
      origin = { kind: 'webhook', name: m.webhookName || m.webhook };
      tainted = true;
      taintedBy ??= 'webhook payload';
    } else if (!origin && typeof m.schedule === 'string' && m.schedule) {
      const title = s.title ?? '';
      origin = { kind: 'schedule', name: title.startsWith('Scheduled: ') && title.length > 11 ? title.slice(11) : m.schedule };
    }
    if (origin) break;
    s = s.parentSessionId ? byId(s.parentSessionId) : null;
  }
  if (!origin) return null;
  return { ...origin, label: `${origin.kind} ${origin.name}`, tainted, taintedBy };
}
