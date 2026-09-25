// Agents: the workers the user sets up (settings agents.<id> = { model, instances, use, disabled, cost, budget }), as the
// scheduler reports them (agents.list / agents.changed: app.slots, the ones with configured = true). Chats and subagents
// run on agents; an agent is active while its model is loaded (local) or reachable (cloud), unless switched off.
import { rpc } from './rpc.svelte.js';
import { app, upsertSessionLocal } from './state/app.svelte.js';
import { toast } from './state/ui.svelte.js';
import { load, save } from './persist.js';

const LAST_KEY = 'netpi.lastAgent';
/** Keys under agents.* that are settings, not agents. */
export const RESERVED = new Set(['maxDepth']);

/** The agents the user set up, in the scheduler's order. */
export function agentList() {
  return app.slots.filter((p) => p.configured);
}

export function agentById(id) {
  return id ? (app.slots.find((p) => p.configured && p.key === id) ?? null) : null;
}

/**
 * The agent a chat runs on: its own (meta.agent), else the one the runtime would take (the first agent on its model).
 * implicit: the chat hasn't chosen one yet.
 */
export function agentOf(session, modelRef) {
  const own = agentById(session?.meta?.agent);
  if (own) return { agent: own, implicit: false };
  const ref = session?.model || modelRef || app.defaultModel;
  const onModel = agentList().filter((p) => p.model === ref);
  const pick = onModel.find((p) => p.available) ?? onModel[0] ?? null;
  return { agent: pick, implicit: true };
}

/** The dot and the words for an agent's state. */
export function agentState(p) {
  if (!p) return { dot: 'unloaded', text: '' };
  if (p.disabled) return { dot: 'cancelled', text: 'switched off' };
  if (!p.available) return { dot: 'unloaded', text: p.unavailable || 'not active' };
  if (p.queued > 0) return { dot: 'queued', text: `${p.busy}/${p.capacity} busy · ${p.queued} waiting` };
  if (p.busy > 0) return { dot: 'running', text: `${p.busy}/${p.capacity} busy` };
  return { dot: 'loaded', text: p.capacity > 1 ? `ready · ${p.capacity} instances` : 'ready' };
}

/** A new agent's id: a slug of the model's id, unique. */
export function newAgentId(ref, taken = []) {
  const base =
    String(ref ?? '')
      .split('/')
      .pop()
      .toLowerCase()
      .replace(/[^a-z0-9_-]+/g, '-')
      .replace(/^-+|-+$/g, '')
      .slice(0, 24) || 'agent';
  const used = new Set([...taken, ...agentList().map((p) => p.key), ...RESERVED]);
  let id = base;
  for (let n = 2; used.has(id); n++) id = `${base}-${n}`;
  return id;
}

/** A valid agent id typed by the user (letters, digits, - and _), or null. */
export function cleanAgentId(text) {
  const id = String(text ?? '')
    .trim()
    .toLowerCase()
    .replace(/[^a-z0-9_-]+/g, '-')
    .replace(/^-+|-+$/g, '');
  return id && !RESERVED.has(id) ? id : null;
}

/** Set up an agent on a model; resolves its id. */
export async function createAgent(ref, taken = []) {
  const id = newAgentId(ref, taken);
  try {
    await rpc('settings.set', { path: `agents.${id}`, value: { model: ref } });
    return id;
  } catch (e) {
    toast(`Could not create the agent: ${e.message}`, 'error');
    return null;
  }
}

/** Run a chat on an agent (its model follows). */
export async function useAgent(sessionId, id) {
  try {
    const s = await rpc('agents.use', { sessionId, agent: id });
    upsertSessionLocal(s);
    save(LAST_KEY, id);
    return s;
  } catch (e) {
    toast(e.message, 'error');
    return null;
  }
}

/** Switch an agent on or off. */
export async function setAgentEnabled(id, enabled) {
  try {
    app.slots = (await rpc('agents.setEnabled', { id, enabled })) ?? app.slots;
    return true;
  } catch (e) {
    toast(e.message, 'error');
    return false;
  }
}

/** The agent for a new chat: the last one chosen while it is active, else the first active one, else the last one. */
export function defaultAgent() {
  const last = agentById(load(LAST_KEY, null));
  if (last?.available) return last;
  return agentList().find((p) => p.available) ?? last ?? null;
}
