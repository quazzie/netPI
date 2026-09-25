// What waits for the user in the chats: questions the agents ask (ask_user, plugins/NetPI.Ask), with the options picked
// so far and how the ones that stopped waiting ended (until their tool result arrives), and tool calls that wait for the
// user's OK (guardrails ask rules, plugins/NetPI.Guardrails).
import { SvelteMap } from 'svelte/reactivity';
import { rpc } from '../rpc.svelte.js';

export const asks = {
  pending: new SvelteMap(), // callId -> { sessionId, callId, agentId, agentName, questions, askedAt }
  drafts: new SvelteMap(), // callId -> string[][]: the options picked, per question
  closed: new SvelteMap(), // callId -> { status: answered|steered|withdrawn|cancelled, answers, text }
  approvals: new SvelteMap(), // callId -> { sessionId, callId, agentId, tool, kind: command|path, subject, rule, askedAt }
};

/** The question waiting in a chat (the oldest, when there are several). */
export function pendingIn(sessionId) {
  let first = null;
  for (const a of asks.pending.values()) if (a.sessionId === sessionId && (!first || a.askedAt < first.askedAt)) first = a;
  return first;
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
  for (const a of questions ?? []) asks.pending.set(a.callId, a);
  asks.approvals.clear();
  for (const a of approvals ?? []) asks.approvals.set(a.callId, a);
}

/** ask.asked / ask.closed and guard.asked / guard.closed (unscoped: every window hears of every chat's). */
export function askEvent(type, d) {
  if (!d?.callId) return;
  if (type === 'ask.asked') asks.pending.set(d.callId, d);
  else if (type === 'ask.closed') {
    asks.pending.delete(d.callId);
    asks.drafts.delete(d.callId);
    asks.closed.set(d.callId, d);
  } else if (type === 'guard.asked') asks.approvals.set(d.callId, d);
  else if (type === 'guard.closed') asks.approvals.delete(d.callId);
}

/** Lets a tool call run, or not. */
export function answerApproval(callId, allow) {
  return rpc('guard.answer', { callId, allow });
}

/** Picks an option (or toggles it, when several may be picked). */
export function pick(callId, qIndex, label, multiple) {
  const a = asks.pending.get(callId);
  if (!a) return;
  const draft = (asks.drafts.get(callId) ?? a.questions.map(() => [])).map((x) => [...x]);
  const cur = draft[qIndex] ?? [];
  draft[qIndex] = multiple ? (cur.includes(label) ? cur.filter((x) => x !== label) : [...cur, label]) : cur[0] === label ? [] : [label];
  asks.drafts.set(callId, draft);
}

/** Sends the answer: the options picked so far, and the user's own words. */
export function answerAsk(callId, text = '') {
  const answers = asks.drafts.get(callId) ?? [];
  return rpc('ask.answer', { callId, answers, ...(text.trim() ? { text: text.trim() } : {}) });
}
