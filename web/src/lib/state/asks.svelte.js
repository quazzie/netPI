// Questions the agents ask the user (ask_user, plugins/NetPI.Ask): the ones waiting for an answer, the options picked
// so far per question, and how the ones that stopped waiting ended (until their tool result arrives in the chat).
import { SvelteMap } from 'svelte/reactivity';
import { rpc } from '../rpc.svelte.js';

export const asks = {
  pending: new SvelteMap(), // callId -> { sessionId, callId, agentId, agentName, questions, askedAt }
  drafts: new SvelteMap(), // callId -> string[][]: the options picked, per question
  closed: new SvelteMap(), // callId -> { status: answered|steered|withdrawn|cancelled, answers, text }
};

/** The question waiting in a chat (the oldest, when there are several). */
export function pendingIn(sessionId) {
  let first = null;
  for (const a of asks.pending.values()) if (a.sessionId === sessionId && (!first || a.askedAt < first.askedAt)) first = a;
  return first;
}

/** On (re)connect: what waits now. Without the ask plugin nothing does. */
export async function loadAsks() {
  let list = [];
  try {
    list = (await rpc('ask.pending', {})) ?? [];
  } catch {
    // the ask plugin is off
  }
  asks.pending.clear();
  for (const a of list) asks.pending.set(a.callId, a);
}

/** ask.asked / ask.closed (unscoped: every window hears of every chat's questions). */
export function askEvent(type, d) {
  if (!d?.callId) return;
  if (type === 'ask.asked') asks.pending.set(d.callId, d);
  else if (type === 'ask.closed') {
    asks.pending.delete(d.callId);
    asks.drafts.delete(d.callId);
    asks.closed.set(d.callId, d);
  }
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
