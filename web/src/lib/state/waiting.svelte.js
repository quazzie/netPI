// Every chat that waits for the user, in one list (the top bar's WaitingMenu): a question (ask_user), a tool call that
// needs the OK, a plan to decide, an offer of plan mode, a schedule proposal, and a budget stop that asks to go over.
// It reads the maps those features keep (asks, plans, schedules); nothing of its own is stored or fetched.
import { app, projectOf } from './app.svelte.js';
import { asks } from './asks.svelte.js';
import { plans } from './plans.svelte.js';
import { scheduleProposals } from './schedules.svelte.js';
import { firstLine } from '../notify.js';

export const KINDS = {
  question: { icon: 'message-circle', label: 'Question' },
  approval: { icon: 'terminal', label: 'Needs OK' },
  plan: { icon: 'list', label: 'Plan' },
  offer: { icon: 'target', label: 'Plan mode' },
  schedule: { icon: 'history', label: 'Schedule' },
  budget: { icon: 'dollar', label: 'Budget' },
};

/** The waiting items, oldest first: { key, sessionId, kind, since, summary, title, project }. Subagent chats are the parent's business. */
export function waitingItems() {
  const out = [];
  const add = (kind, sessionId, since, summary, key) => {
    if (!sessionId) return;
    const s = app.sessionsById.get(sessionId);
    if (s?.kind === 'subagent') return;
    const p = s ? projectOf(s) : null;
    out.push({ key: `${kind}:${key}`, sessionId, kind, since: since ?? null, summary: firstLine(summary ?? '', 140), title: s?.title || 'New session', project: p?.name ?? null });
  };
  for (const a of asks.pending.values()) {
    const q = a.questions?.[0];
    add('question', a.sessionId, a.askedAt, q?.question ?? q?.header ?? 'Asks a question', a.id ?? a.callId);
  }
  for (const a of asks.approvals.values()) {
    add('approval', a.sessionId, a.askedAt, a.kind === 'path' ? `Change ${a.subject}` : `Run: ${a.subject}`, a.approvalId);
  }
  for (const p of plans.byId.values()) if (p.status === 'awaiting') add('plan', p.sessionId, p.askedAt ?? p.updatedAt, p.title ? `Plan: ${p.title}` : 'Plan ready', p.id);
  for (const o of plans.offers.values()) add('offer', o.sessionId, o.askedAt, o.reason ? `Suggests plan mode: ${o.reason}` : 'Suggests plan mode', o.id);
  for (const p of scheduleProposals.pending.values()) add('schedule', p.sessionId, p.askedAt, `New schedule: ${p.schedule?.name ?? ''}`, p.id);
  for (const b of asks.budget.values()) add('budget', b.sessionId, b.askedAt, 'Budget spent: let this chat go over?', b.sessionId);
  return out.sort((a, b) => String(a.since ?? '').localeCompare(String(b.since ?? '')));
}
