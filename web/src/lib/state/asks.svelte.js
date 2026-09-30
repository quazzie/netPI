// What waits for the user in the chats: questions the agents ask (ask_user, plugins/NetPI.Ask), with the options picked
// so far and how the ones that stopped waiting ended (until their tool result arrives), and tool calls that wait for the
// user's OK (guardrails ask rules, plugins/NetPI.Guardrails).
import { SvelteMap } from 'svelte/reactivity';
import { rpc } from '../rpc.svelte.js';

export const asks = {
  pending: new SvelteMap(), // question id -> { id, sessionId, callId, agentId, agentName, questions, askedAt }
  drafts: new SvelteMap(), // question id -> string[][]: the options picked, per question
  closed: new SvelteMap(), // question id -> { status: answered|steered|withdrawn|cancelled, answers, text }
  approvals: new SvelteMap(), // approvalId -> { sessionId, callId, agentId, tool, kind: command|path, subject, rule, askedAt, opinion? }
  cleared: new SvelteMap(), // [sessionId, callId] -> guard.cleared: an ask rule matched, and the second opinion let it run without asking
};

/** A question's own id. A plugin without one (an older build, hot reloaded on its own) falls back to the tool call's. */
const qid = (a) => a?.id ?? a?.callId;

/** The question waiting in a chat (the oldest, when there are several). */
export function pendingIn(sessionId) {
  let first = null;
  for (const a of asks.pending.values()) if (a.sessionId === sessionId && (!first || a.askedAt < first.askedAt)) first = a;
  return first;
}

/** The question of this chat's tool call that waits for the user: the card knows the call, the question has the id. */
export function pendingFor(sessionId, callId) {
  for (const a of asks.pending.values()) if (a.sessionId === sessionId && a.callId === callId) return a;
  return null;
}

/** The same, for a question that stopped waiting (its card shows the answer it ended with). */
export function closedFor(sessionId, callId) {
  for (const a of asks.closed.values()) if (a.sessionId === sessionId && a.callId === callId) return a;
  return null;
}

/** A tool call of a chat that waits for the user's OK. */
export function approvalIn(sessionId) {
  for (const a of asks.approvals.values()) if (a.sessionId === sessionId) return a;
  return null;
}

/** On (re)connect: what waits now. Without the ask or guardrails plugin nothing does. */
export async function loadAsks() {
  const [questions, approvals] = await Promise.all([rpc('ask.pending', {}).catch(() => []), rpc('guard.pending', {}).catch(() => [])]);
  asks.pending.clear();
  for (const a of questions ?? []) asks.pending.set(qid(a), a);
  asks.approvals.clear();
  for (const a of approvals ?? []) asks.approvals.set(a.approvalId, a);
}

/** ask.asked / ask.closed and guard.asked / guard.closed / guard.cleared (unscoped: every window hears of every chat's). */
export function askEvent(type, d) {
  if (type === 'ask.asked' || type === 'ask.closed') {
    const id = qid(d);
    if (!id) return;
    if (type === 'ask.asked') asks.pending.set(id, d);
    else {
      asks.pending.delete(id);
      asks.drafts.delete(id);
      asks.closed.set(id, d);
    }
    return;
  }
  if (!d?.callId) return;
  if (type === 'guard.asked') asks.approvals.set(d.approvalId, d);
  else if (type === 'guard.closed') asks.approvals.delete(d.approvalId);
  else if (type === 'guard.cleared') asks.cleared.set(JSON.stringify([d.sessionId, d.callId]), d);
}

/** The session went: drop its closed answers and guard clears (the maps would otherwise keep one entry per closed
 * ask of a deleted chat). */
export function pruneSession(sessionId) {
  for (const [id, a] of asks.closed) if (a.sessionId === sessionId) asks.closed.delete(id);
  for (const key of [...asks.cleared.keys()]) if (JSON.parse(key)[0] === sessionId) asks.cleared.delete(key);
}

const RISKS = { destructive: 'destructive', stops_process: 'stops a process', remote_change: 'changes a remote' };
const pct = (p) => `${Math.round(p * 100)}%`;

/** The second opinion in a few words: read-only and the likeliest risk, or why there is none. */
export function opinionText(o) {
  if (!o) return '';
  if (o.error) return `No second opinion (${o.error})`;
  const p = o.p ?? {};
  const [risk, value] = Object.keys(RISKS).map((k) => [k, p[k] ?? 0]).sort((a, b) => b[1] - a[1])[0];
  return `${o.model}: read-only ${pct(p.read_only ?? 0)} · ${RISKS[risk]} ${pct(value)}`;
}

/** Lets a tool call run, or not; scope 'session' allows the rule that asked for the rest of the chat. */
export function answerApproval(approvalId, allow, scope = 'once') {
  return rpc('guard.answer', { approvalId, allow, scope });
}

/** Picks an option (or toggles it, when several may be picked). */
export function pick(id, qIndex, label, multiple) {
  const a = asks.pending.get(id);
  if (!a) return;
  const draft = (asks.drafts.get(id) ?? a.questions.map(() => [])).map((x) => [...x]);
  const cur = draft[qIndex] ?? [];
  draft[qIndex] = multiple ? (cur.includes(label) ? cur.filter((x) => x !== label) : [...cur, label]) : cur[0] === label ? [] : [label];
  asks.drafts.set(id, draft);
}

/** Sends the answer: the options picked so far, and the user's own words. The call id rides along for an older plugin. */
export function answerAsk(id, text = '') {
  const answers = asks.drafts.get(id) ?? [];
  const callId = asks.pending.get(id)?.callId;
  return rpc('ask.answer', { id, ...(callId ? { callId } : {}), answers, ...(text.trim() ? { text: text.trim() } : {}) });
}

export function approvalFor(sessionId, callId) {
  return [...asks.approvals.values()].find(a => a.sessionId === sessionId && a.callId === callId) ?? null;
}
