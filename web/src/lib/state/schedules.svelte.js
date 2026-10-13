// Schedule proposals (plugins/NetPI.Schedules): a schedule an agent proposed (a schedules add) that waits for the
// user's decision on the card in the chat — allow (it runs from then on) or discard (it is deleted). The schedule
// itself is persisted as a proposal (paused, proposedBy) in the Schedules tab; only the wait is here, and it is lost
// with the plugin: a reconnect lists what waits again, a deleted chat's cards go with it.
import { SvelteMap } from 'svelte/reactivity';
import { rpc } from '../rpc.svelte.js';

export const scheduleProposals = {
  pending: new SvelteMap(), // proposal id -> { id, sessionId, callId, agentId, schedule, askedAt }
  closed: new SvelteMap(), // proposal id -> { sessionId, callId, status: allowed|discarded|steered|withdrawn|cancelled, decision? }
};

const CLOSED_MAX = 200; // a proposal's outcome is history once its tool result is in: the oldest drops first

/** The proposal this tool call of the chat made, waiting (the card knows the call, the proposal has the id). */
export function proposalForCall(sessionId, callId) {
  for (const p of scheduleProposals.pending.values()) if (p.sessionId === sessionId && p.callId === callId) return p;
  return null;
}

/** The same, for a proposal that stopped waiting (its card shows the outcome). */
export function closedProposalForCall(sessionId, callId) {
  for (const p of scheduleProposals.closed.values()) if (p.sessionId === sessionId && p.callId === callId) return p;
  return null;
}

/** A schedule proposal of this chat waits for the user's decision (the tab's "asking" dot). */
export function proposalIn(sessionId) {
  for (const p of scheduleProposals.pending.values()) if (p.sessionId === sessionId) return p;
  return null;
}

let loadSeq = 0;

/** On (re)connect: the proposals that wait. Without the schedules plugin nothing does. */
export async function loadScheduleProposals() {
  const seq = ++loadSeq;
  const list = await rpc('schedules.proposals', {}).catch(() => []);
  if (seq !== loadSeq) return;
  scheduleProposals.pending.clear();
  for (const p of list ?? []) if (p?.id) scheduleProposals.pending.set(p.id, p);
}

/** schedule.proposed / schedule.decided (unscoped: every window hears of every chat's). */
export function scheduleEvent(type, d) {
  if (type === 'schedule.proposed') {
    if (d?.id) scheduleProposals.pending.set(d.id, d);
  } else if (type === 'schedule.decided') {
    if (!d?.id) return;
    scheduleProposals.pending.delete(d.id);
    while (scheduleProposals.closed.size >= CLOSED_MAX) scheduleProposals.closed.delete(scheduleProposals.closed.keys().next().value);
    scheduleProposals.closed.set(d.id, d);
  }
}

/** The session went: its proposals go with it. */
export function pruneScheduleProposals(sessionId) {
  for (const [id, p] of scheduleProposals.pending) if (p.sessionId === sessionId) scheduleProposals.pending.delete(id);
  for (const [id, p] of scheduleProposals.closed) if (p.sessionId === sessionId) scheduleProposals.closed.delete(id);
}

/** Allow (enable the proposal, it runs from now on) or discard (delete it) the schedule a proposal is for. */
export const decideProposal = (id, decision) => rpc('schedules.decide', { id, decision });
