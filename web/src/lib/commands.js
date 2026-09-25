// Slash commands: built-ins (client side) + server commands from ui.commands.
import { rpc } from './rpc.svelte.js';
import { app, newSession, updateSession, abortAgent } from './state/app.svelte.js';
import { agentList, useAgent } from './agents.js';
import { modals, toast, composer, promptDialog } from './state/ui.svelte.js';
import { openPanelTab } from './state/tabs.svelte.js';
import { editGoal } from './goal.js';

/**
 * @typedef {{ name: string, description: string, argsHint?: string, source: 'builtin'|string,
 *   run: (args: string, env: { sessionId: string|null, ui?: any }) => any }} Command
 */

/** @type {Command[]} */
const BUILTIN = [
  {
    name: 'new',
    description: 'Start a new session (same project)',
    argsHint: '[title]',
    source: 'builtin',
    run: (args) => newSession({ title: args || undefined }),
  },
  {
    name: 'rename',
    description: 'Rename this session',
    argsHint: '<title>',
    source: 'builtin',
    run: async (args, { sessionId }) => {
      if (!sessionId) return;
      let title = args;
      if (!title) {
        const s = app.sessionsById.get(sessionId);
        title = await promptDialog({ title: 'Rename session', label: 'Title', value: s?.title ?? '' });
      }
      if (title?.trim()) await updateSession(sessionId, { title: title.trim() });
    },
  },
  {
    name: 'agent',
    description: 'Run this chat on another agent',
    argsHint: '[name]',
    source: 'builtin',
    run: async (args, { sessionId, ui }) => {
      if (!sessionId) return;
      if (!args) return ui?.openAgentPicker?.();
      const q = args.trim().toLowerCase();
      const agents = agentList();
      const a = agents.find((x) => x.key.toLowerCase() === q) ?? agents.find((x) => x.key.toLowerCase().includes(q) || (x.model ?? '').toLowerCase().includes(q));
      if (!a) return toast(`No agent “${args}”`, 'warn');
      if (await useAgent(sessionId, a.key)) toast(`Agent: ${a.key}${a.available ? '' : ` (${a.disabled ? 'switched off' : a.unavailable})`}`);
    },
  },
  {
    name: 'project',
    description: 'Attach a project to this session',
    source: 'builtin',
    run: (args, { sessionId, ui }) => {
      if (!sessionId) return;
      if (ui?.openProjectPicker) ui.openProjectPicker();
      else modals.projectPicker = { sessionId, anchor: document.querySelector('[data-composer]') };
    },
  },
  {
    name: 'goal',
    description: 'Set a goal: the agent keeps going until it marks it complete (no text: edit it)',
    argsHint: '[what must be true when done]',
    source: 'builtin',
    run: (args, { sessionId }) => editGoal(sessionId, args),
  },
  { name: 'settings', description: 'Open settings', source: 'builtin', run: () => (modals.settings = true) },
  { name: 'help', description: 'Commands and keyboard shortcuts', source: 'builtin', run: () => (modals.help = true) },
  {
    name: 'abort',
    description: 'Stop the running agent (Esc)',
    source: 'builtin',
    run: (args, { sessionId }) => sessionId && abortAgent(sessionId),
  },
];

/** Built-ins first; a server command with the same name overrides the built-in. */
export function allCommands() {
  const server = (app.commands ?? []).map((c) => ({
    name: String(c.name).replace(/^\//, ''),
    description: c.description ?? '',
    argsHint: c.argsHint ?? undefined,
    source: c.pluginId ?? 'server',
    run: (args, env) => runServerCommand(c, args, env),
  }));
  const names = new Set(server.map((c) => c.name));
  return [...BUILTIN.filter((c) => !names.has(c.name)), ...server];
}

async function runServerCommand(c, args, { sessionId }) {
  if (c.rpc) {
    try {
      const r = await rpc(c.rpc, { sessionId, args: args ?? '' }, { timeout: 120_000 });
      if (typeof r === 'string' && r) toast(r);
    } catch (e) {
      toast(`/${c.name}: ${e.message}`, 'error');
    }
    return;
  }
  const action = c.clientAction ?? '';
  const i = action.indexOf(':');
  const kind = i < 0 ? action : action.slice(0, i);
  const value = i < 0 ? '' : action.slice(i + 1);
  switch (kind) {
    case 'openTab':
      if (!openPanelTab(value)) toast(`Tab ${value} is not available`, 'warn');
      break;
    case 'insert':
      composer.insertText?.(value + (args ? ` ${args}` : ''));
      break;
    case 'settings':
      modals.settings = true;
      break;
    default:
      toast(`/${c.name}: unsupported action “${action}”`, 'warn');
  }
}

/** Parse "/name args" → { cmd, args } (cmd undefined when unknown). */
export function parseCommand(text) {
  const m = /^\/([\w:.-]+)(?:\s+([\s\S]*))?$/.exec(text.trim());
  if (!m) return null;
  const name = m[1].toLowerCase();
  const cmd = allCommands().find((c) => c.name.toLowerCase() === name);
  return { name, cmd, args: (m[2] ?? '').trim() };
}
