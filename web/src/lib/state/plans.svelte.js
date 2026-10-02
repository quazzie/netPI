// Plan mode (plugins/NetPI.Plan): the plans that wait for the user's decision, and the offers of plan mode (plan_enter).
// The mode itself is the chat's meta (session.meta.planMode: { state: planning | awaiting | approved, planId?, title? }), which
// the session events keep current; a plan's body and status live in the plugin and come over plan.get / plan.list.
import { SvelteMap } from 'svelte/reactivity';
import { rpc } from '../rpc.svelte.js';

export const plans = {
  byId: new SvelteMap(), // plan id -> { id, sessionId, callId, status: awaiting | revising | approved | cancelled, revision, title, plan, markdown, waiting }
  offers: new SvelteMap(), // offer id -> { id, sessionId, callId, reason, askedAt }
  closedOffers: new SvelteMap(), // offer id -> { sessionId, callId, status: entered | declined | steered | withdrawn | cancelled }
};

const CLOSED_MAX = 200; // an offer's outcome is history once its tool result is in: the oldest drops first

/** The chat's plan mode: planning | awaiting | approved, or null. */
export const planState = (session) => session?.meta?.planMode?.state ?? null;

/** The plan this tool call of the chat submitted (the plugin knows the latest call of each plan). */
export function planForCall(sessionId, callId) {
  for (const p of plans.byId.values()) if (p.sessionId === sessionId && p.callId === callId) return p;
  return null;
}

/** A plan of this chat waits for the user's decision (a run may or may not still wait on it). */
export function planWaiting(sessionId) {
  for (const p of plans.byId.values()) if (p.sessionId === sessionId && p.status === 'awaiting') return p;
  return null;
}

/** The offer of plan mode this tool call made, waiting or closed. */
export function offerForCall(sessionId, callId) {
  for (const o of plans.offers.values()) if (o.sessionId === sessionId && o.callId === callId) return { ...o, status: 'waiting' };
  for (const o of plans.closedOffers.values()) if (o.sessionId === sessionId && o.callId === callId) return o;
  return null;
}

let loadSeq = 0;

/** On (re)connect: the plans that wait, and the offers. Without the plan plugin nothing does. */
export async function loadPlans() {
  const seq = ++loadSeq;
  const [list, offers] = await Promise.all([rpc('plan.list', {}).catch(() => []), rpc('plan.offers', {}).catch(() => [])]);
  if (seq !== loadSeq) return;
  plans.byId.clear();
  for (const p of list ?? []) plans.byId.set(p.id, p);
  plans.offers.clear();
  for (const o of offers ?? []) plans.offers.set(o.id, o);
}

const pending = new Map(); // session id -> timer: a burst of plan.changed is one read
function refresh(sessionId) {
  clearTimeout(pending.get(sessionId));
  pending.set(
    sessionId,
    setTimeout(async () => {
      pending.delete(sessionId);
      try {
        const list = await rpc('plan.list', { sessionId });
        for (const [id, p] of plans.byId) if (p.sessionId === sessionId) plans.byId.delete(id);
        for (const p of list ?? []) plans.byId.set(p.id, p);
      } catch {
        /* the plan plugin went away: the next load clears it */
      }
    }, 30),
  );
}

/** plan.changed, plan.enter.asked and plan.enter.closed (unscoped: every window hears of every chat's). */
export function planEvent(type, d) {
  if (type === 'plan.changed') {
    if (d?.sessionId) refresh(d.sessionId);
  } else if (type === 'plan.enter.asked') {
    if (d?.id) plans.offers.set(d.id, d);
  } else if (type === 'plan.enter.closed') {
    if (!d?.id) return;
    plans.offers.delete(d.id);
    while (plans.closedOffers.size >= CLOSED_MAX) plans.closedOffers.delete(plans.closedOffers.keys().next().value);
    plans.closedOffers.set(d.id, d);
  }
}

/** The session went: its plans and offers go with it. */
export function prunePlans(sessionId) {
  for (const [id, p] of plans.byId) if (p.sessionId === sessionId) plans.byId.delete(id);
  for (const [id, o] of plans.offers) if (o.sessionId === sessionId) plans.offers.delete(id);
  for (const [id, o] of plans.closedOffers) if (o.sessionId === sessionId) plans.closedOffers.delete(id);
}

/** approve | revise (feedback) | save | file | cancel; approve takes newChat. */
export const answerPlan = (planId, decision, extra = {}) => rpc('plan.answer', { planId, decision, ...extra });

export const enterPlan = (sessionId) => rpc('plan.enter', { sessionId });

export const exitPlan = (sessionId) => rpc('plan.exit', { sessionId });

export const answerOffer = (id, enter) => rpc('plan.enterAnswer', { id, enter });
