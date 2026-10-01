// Goals (plugins/NetPI.Goal): the session's goal lives in session.meta.goal; these helpers call the goal.* RPCs. The
// session.updated event that follows each change updates the strip.
import { rpc } from './rpc.svelte.js';
import { app } from './state/app.svelte.js';
import { toast, promptDialog, confirmDialog } from './state/ui.svelte.js';

/** The session's goal unless it was cleared: { id, objective, status, reason, tokensUsed, tokenBudget, continuations }. */
export function goalOf(session) {
  const g = session?.meta?.goal;
  return g && g.status !== 'cleared' ? g : null;
}

const open = (g) => g && ['active', 'paused', 'blocked', 'execution-unavailable'].includes(g.status);

async function call(method, params) {
  try {
    return await rpc(method, params);
  } catch (e) {
    toast(e.message, 'error');
    return null;
  }
}

/**
 * /goal [objective]: with text, set it (replacing an open goal after a confirmation); without, edit the open goal or
 * ask for a new one.
 */
export async function editGoal(sessionId, text = '') {
  if (!sessionId) return;
  const g = goalOf(app.sessionsById.get(sessionId));
  let objective = text.trim();
  if (!objective) {
    objective =
      (await promptDialog({
        title: open(g) ? 'Edit the goal' : 'Set a goal',
        label: 'What should be true when the agent is done?',
        value: open(g) ? g.objective : '',
        placeholder: 'e.g. All tests in tests/NetPI.Host.Tests pass and the new endpoint is documented in docs/PROTOCOL.md',
        multiline: true,
        confirmLabel: open(g) ? 'Save' : 'Start',
        hint: 'The agent is started again after every answer until it marks the goal complete, needs you, or stops making progress.',
      })) ?? '';
    objective = objective.trim();
    if (!objective) return;
    if (open(g)) {
      if (objective !== g.objective) await call('goal.edit', { sessionId, objective });
      return;
    }
  } else if (open(g)) {
    const ok = await confirmDialog({
      title: 'Replace the goal?',
      message: `The current goal is ${g.status}: “${g.objective.length > 120 ? g.objective.slice(0, 120) + '…' : g.objective}”`,
      confirmLabel: 'Replace',
    });
    if (!ok) return;
  }
  await call('goal.set', { sessionId, objective });
}

export const pauseGoal = (sessionId) => call('goal.pause', { sessionId });
export const resumeGoal = (sessionId) => call('goal.resume', { sessionId });
export const clearGoal = (sessionId) => call('goal.clear', { sessionId });
