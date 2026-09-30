// Global app state: sessions, projects, models, agents, open tabs, plugin tabs and commands.
// Server events are routed here (broadcast events) and to the per-session ChatStores (scoped events).
import { SvelteMap, SvelteSet } from 'svelte/reactivity';
import { rpc, subscribe, onOpen, connect } from '../rpc.svelte.js';
import { bus } from '../bus.js';
import { load, save, persist, fetchRemote } from '../persist.js';
import { getChat, peekChat, dropChat, allChats } from './chat.svelte.js';
import { toast, syncUiStateFromHost, composer } from './ui.svelte.js';
import { toolDefs } from '../tools.js';
import { defaultAgent, useAgent } from '../agents.js';
import { notify, onNotificationClick, firstLine } from '../notify.js';
import { loadAsks, askEvent, pendingIn, approvalIn, pruneSession } from './asks.svelte.js';
import { recall } from '../../components/composer/ideaRecall.svelte.js';
import { suggestions } from '../../components/composer/ideaSuggestions.svelte.js';
import { formatBytes, payloadBytes, sendBudget } from '../images.js';

const TABS_KEY = 'netpi.openTabs';

class AppState {
  ready = $state(false);
  info = $state.raw(null);
  projects = $state.raw([]);
  sessions = $state.raw([]); // all known sessions (incl. subagents), newest first
  models = $state.raw([]);
  defaultModel = $state(null);
  slots = $state.raw([]); // agents.list: the agents the user set up (configured) and other model calls in progress
  agents = new SvelteMap(); // sessionId -> AgentInfo (latest)
  context = new SvelteMap(); // sessionId -> { used, window }
  unread = new SvelteSet();
  errored = new SvelteSet(); // background tabs whose run ended with an error
  openTabs = $state([]); // session ids
  activeId = $state(null);
  uiTabs = $state.raw([]); // plugin UiTabInfo[]
  commands = $state.raw([]); // server SlashCommandInfo[]
  settingsVersion = $state(0);

  sessionsById = $derived(new Map(this.sessions.map((s) => [s.id, s])));
  projectsById = $derived(new Map(this.projects.map((p) => [p.id, p])));
  modelsByRef = $derived(new Map(this.models.map((m) => [m.ref ?? `${m.provider}/${m.id}`, m])));
  activeSession = $derived(this.activeId ? (this.sessionsById.get(this.activeId) ?? null) : null);
  // the project the user last worked in (the active session's, kept while no session is active)
  lastProjectId = $state(load('netpi.lastProject', null));
  activeProject = $derived(
    this.activeSession?.projectId ? (this.projectsById.get(this.activeSession.projectId) ?? null) : null,
  );
}

export const app = new AppState();

// ------------------------------------------------------------------------------------------ helpers

export function sessionModelRef(s) {
  return s?.model || app.defaultModel || null;
}
export function modelFor(s) {
  const ref = sessionModelRef(s);
  return ref ? (app.modelsByRef.get(ref) ?? null) : null;
}
export function projectOf(s) {
  return s?.projectId ? (app.projectsById.get(s.projectId) ?? null) : null;
}

const BUSY = new Set(['running', 'queued', 'yielded']);
export function isBusy(sessionId) {
  const a = app.agents.get(sessionId);
  return !!a && BUSY.has(a.status);
}

/** Status for tab dots: asking (a question, or a tool call, waits for the user) | running | queued | yielded | error | unread | idle */
export function sessionStatus(sessionId) {
  if (pendingIn(sessionId) || approvalIn(sessionId)) return 'asking';
  const a = app.agents.get(sessionId);
  if (a) {
    if (a.status === 'running') return 'running';
    if (a.status === 'queued') return 'queued';
    if (a.status === 'yielded') return 'yielded';
    if (a.status === 'failed') return 'error';
  }
  if (app.errored.has(sessionId)) return 'error';
  if (app.unread.has(sessionId)) return 'unread';
  return 'idle';
}

function upsertSession(s) {
  if (!s?.id) return;
  const list = app.sessions;
  const i = list.findIndex((x) => x.id === s.id);
  if (i >= 0) {
    // an RPC result can arrive after a newer session.updated (e.g. sessions.create, then the default profile the server
    // gives the new chat right away): never replace a session with an older copy of it
    if (Date.parse(s.updatedAt) < Date.parse(list[i].updatedAt)) return;
    const next = list.slice();
    next[i] = s;
    // keep newest-first order by updatedAt
    if (i > 0 && Date.parse(s.updatedAt) > Date.parse(next[i - 1].updatedAt)) {
      next.splice(i, 1);
      next.unshift(s);
    }
    app.sessions = next;
  } else {
    app.sessions = [s, ...list];
  }
}

export const upsertSessionLocal = (s) => upsertSession(s);

function upsertProject(p) {
  if (!p?.id) return;
  const i = app.projects.findIndex((x) => x.id === p.id);
  if (i >= 0) {
    const next = app.projects.slice();
    next[i] = p;
    app.projects = next;
  } else app.projects = [...app.projects, p];
}

function persistTabs() {
  persist(TABS_KEY, { tabs: $state.snapshot(app.openTabs), active: app.activeId });
}

function resubscribe() {
  subscribe(app.openTabs);
}

// ------------------------------------------------------------------------------------------ loading

export async function loadSessions() {
  const list = await rpc('sessions.list', { includeSubagents: true, limit: 300 });
  const known = new Set(list.map((s) => s.id));
  // keep open sessions that fell outside the page
  const extra = app.sessions.filter((s) => !known.has(s.id) && app.openTabs.includes(s.id));
  app.sessions = [...list, ...extra];
}

export async function loadProjects() {
  app.projects = await rpc('projects.list');
}

export async function loadModels(refresh = false) {
  try {
    const res = await rpc('models.list', refresh ? { refresh: true } : {});
    app.models = res?.models ?? [];
    app.defaultModel = res?.defaultModel ?? null;
  } catch (e) {
    console.warn('models.list failed', e);
  }
}

export async function loadPools() {
  try {
    app.slots = (await rpc('agents.list', {}, { timeout: 8000 })) ?? [];
  } catch {
    app.slots = []; // the agents plugin is off
  }
}

export async function loadUiRegistry() {
  const [tabs, cmds] = await Promise.all([
    rpc('ui.tabs').catch(() => []),
    rpc('ui.commands').catch(() => []),
  ]);
  app.uiTabs = Array.isArray(tabs) ? tabs : [];
  app.commands = Array.isArray(cmds) ? cmds : [];
}

async function loadAgents() {
  try {
    const list = await rpc('runs.list', { includeFinished: true }, { timeout: 8000 });
    app.agents.clear();
    for (const a of list ?? []) setAgent(a);
  } catch {
    /* agent plugin missing */
  }
}

async function loadTools() {
  try {
    const list = await rpc('tools.list', {}, { timeout: 8000 });
    toolDefs.clear();
    for (const t of list ?? []) toolDefs.set(t.name, t);
  } catch {}
}

async function fetchSession(id) {
  try {
    const s = await rpc('sessions.get', { id });
    upsertSession(s);
    return s;
  } catch {
    return null;
  }
}

async function loadAll({ reconnect }) {
  try {
    const [info] = await Promise.all([
      rpc('app.info').catch(() => null),
      loadProjects(),
      loadSessions(),
      loadModels(),
      loadPools(),
      loadUiRegistry(),
      loadAgents(),
      loadTools(),
      loadAsks(),
    ]);
    app.info = info;
  } catch (e) {
    toast(`Failed to load: ${e.message}`, 'error');
  }

  if (!reconnect) {
    await syncUiStateFromHost();
    let saved = load(TABS_KEY, null);
    if (!saved) saved = await fetchRemote(TABS_KEY);
    const tabs = (saved?.tabs ?? []).filter((id) => typeof id === 'string');
    for (const id of tabs) if (!app.sessionsById.has(id)) await fetchSession(id);
    app.openTabs = tabs.filter((id) => app.sessionsById.has(id));
    const active = app.openTabs.includes(saved?.active) ? saved.active : (app.openTabs[0] ?? null);
    app.activeId = active;
    resubscribe();
    if (active) getChat(active).load();
  } else {
    // we may have missed events: refresh the active chat, mark the others stale
    resubscribe();
    for (const c of allChats()) c.stale = true;
    if (app.activeId) {
      const c = getChat(app.activeId);
      c.stale = false;
      c.load();
    }
  }
  app.ready = true;
}

// ------------------------------------------------------------------------------------------ actions

export function activate(id) {
  if (!id) {
    app.activeId = null;
    persistTabs();
    return;
  }
  if (!app.openTabs.includes(id)) app.openTabs.push(id);
  app.activeId = id;
  const act = app.sessionsById.get(id);
  if (act) noteProject(act.projectId ?? null);
  app.unread.delete(id);
  app.errored.delete(id);
  const c = getChat(id);
  if (!c.loaded || c.stale) c.load();
  resubscribe();
  persistTabs();
}

export async function openSession(id) {
  if (!id) return;
  if (!app.sessionsById.has(id)) {
    const s = await fetchSession(id);
    if (!s) {
      toast('Session not found', 'error');
      return;
    }
  }
  activate(id);
}

export function closeTab(id) {
  const i = app.openTabs.indexOf(id);
  if (i < 0) return;
  const chat = peekChat(id);
  chat?.saveDraft();
  app.openTabs.splice(i, 1);
  if (app.activeId === id) {
    const next = app.openTabs[i] ?? app.openTabs[i - 1] ?? null;
    app.activeId = next;
    if (next) {
      const c = getChat(next);
      if (!c.loaded || c.stale) c.load();
    }
  }
  resubscribe();
  persistTabs();
  // Closing is when a plan gets forgotten (plugins/NetPI.Ideas): ask in the background what the chat leaves unsaved.
  // Fire and forget — the tab is already gone and nothing here may hold up the next one.
  rpc('ideas.closed', { sessionId: id }).catch(() => {});
}

export function moveTab(id, toIndex) {
  const from = app.openTabs.indexOf(id);
  if (from < 0 || from === toIndex) return;
  app.openTabs.splice(from, 1);
  app.openTabs.splice(Math.max(0, Math.min(toIndex, app.openTabs.length)), 0, id);
  persistTabs();
}

export function cycleTab(dir) {
  const tabs = app.openTabs;
  if (!tabs.length) return;
  const i = tabs.indexOf(app.activeId);
  activate(tabs[(i + dir + tabs.length) % tabs.length]);
}

/** Remember the active session's project so a new session made with no tab open lands in it too. */
export function noteProject(projectId) {
  if (projectId === app.lastProjectId) return;
  app.lastProjectId = projectId ?? null;
  save('netpi.lastProject', app.lastProjectId);
}

export async function newSession(opts = {}) {
  // explicit projectId (null = none) › the active session's project › the last project used
  const projectId =
    opts?.projectId !== undefined
      ? opts.projectId
      : app.activeSession
        ? (app.activeSession.projectId ?? null)
        : app.lastProjectId && app.projectsById.has(app.lastProjectId)
          ? app.lastProjectId
          : null;
  try {
    const params = {};
    if (projectId) params.projectId = projectId;
    if (opts.title) params.title = opts.title;
    if (opts.model) params.model = opts.model;
    // a new chat runs on the agent chosen last (or the first active one) unless a model was asked for
    const agent = opts.model ? null : defaultAgent();
    if (agent) params.model = agent.model;
    let s = await rpc('sessions.create', params);
    if (agent) s = (await useAgent(s.id, agent.key)) ?? s;
    upsertSession(s);
    activate(s.id);
    queueMicrotask(() => composer.focus?.());
    return s;
  } catch (e) {
    toast(`Could not create session: ${e.message}`, 'error');
    return null;
  }
}

/**
 * Fork a chat (sessions.fork): a new chat with its messages up to upToSeq, opened in a tab; the original stays as it is.
 * draft: text for the new chat's message box (a user message forked "before" it, to change and send again).
 */
export async function forkSession(sessionId, upToSeq, draft = null) {
  try {
    const s = await rpc('sessions.fork', { id: sessionId, upToSeq });
    upsertSession(s);
    activate(s.id);
    if (draft) {
      const chat = getChat(s.id);
      chat.draft = draft;
      chat.saveDraft?.();
    }
    queueMicrotask(() => composer.focus?.());
    return s;
  } catch (e) {
    toast(`Could not fork the chat: ${e.message}`, 'error');
    return null;
  }
}

export async function updateSession(id, patch) {
  try {
    const s = await rpc('sessions.update', { id, ...patch });
    upsertSession(s);
    return s;
  } catch (e) {
    toast(e.message, 'error');
    return null;
  }
}

export async function deleteSession(id) {
  try {
    await rpc('sessions.delete', { id });
    removeSessionLocal(id);
  } catch (e) {
    toast(e.message, 'error');
  }
}

function removeSessionLocal(id) {
  if (app.openTabs.includes(id)) closeTab(id);
  app.sessions = app.sessions.filter((s) => s.id !== id);
  dropChat(id);
  // drop the per-session bookkeeping that would otherwise keep an entry for this chat forever
  saidJustNow.delete(id);
  recall.prune(id);
  pruneSession(id);
}

export async function setSessionProject(id, projectId) {
  try {
    const s = await rpc('sessions.setProject', { id, projectId: projectId ?? null });
    upsertSession(s);
    if (id === app.activeId) noteProject(s?.projectId ?? null);
    return s;
  } catch (e) {
    toast(e.message, 'error');
    return null;
  }
}

export async function createProject(p) {
  const res = await rpc('projects.create', p);
  upsertProject(res);
  return res;
}
export async function updateProject(p) {
  const res = await rpc('projects.update', p);
  upsertProject(res);
  return res;
}
export async function deleteProject(id) {
  await rpc('projects.delete', { id });
  app.projects = app.projects.filter((p) => p.id !== id);
}

/** Send user input to the session's agent. mode: auto | steer | queue */
export async function sendMessage(sessionId, text, images, mode = 'auto') {
  const chat = getChat(sessionId);
  if (images?.length) {
    // The envelope has to fit one WebSocket message. Refuse here with a reason, rather than have the host cut the
    // socket off mid-send, which reached the user as "Send failed" (idea-8hfc3m).
    const bytes = payloadBytes(images);
    const budget = sendBudget(app.info?.maxMessageBytes);
    if (bytes > budget) {
      toast(`Images are ${formatBytes(bytes)} — a message can carry ${formatBytes(budget)}. Remove one or attach smaller images.`, 'error');
      return false;
    }
  }
  const optimistic = mode === 'auto' && !isBusy(sessionId);
  if (optimistic) chat.pendingUser = { text, images: images ?? [], createdAt: new Date().toISOString() };
  try {
    const params = { sessionId, text, mode };
    if (images?.length) params.images = images.map((i) => ({ mediaType: i.mediaType, data: i.data }));
    const agent = await rpc('agent.send', params);
    if (agent?.sessionId) setAgent(agent);
    return true;
  } catch (e) {
    chat.pendingUser = null;
    toast(`Send failed: ${e.message}`, 'error');
    return false;
  }
}

export async function abortAgent(sessionId) {
  try {
    await rpc('agent.abort', { sessionId });
  } catch (e) {
    toast(`Abort failed: ${e.message}`, 'error');
  }
}

export async function dequeue(sessionId, id) {
  try {
    await rpc('agent.dequeue', { sessionId, id });
    const c = peekChat(sessionId);
    if (c) c.queue = c.queue.filter((q) => q.id !== id);
  } catch (e) {
    toast(e.message, 'error');
  }
}

function setAgent(a) {
  if (!a?.sessionId) return;
  const prev = app.agents.get(a.sessionId);
  app.agents.set(a.sessionId, a);
  const wasBusy = prev && BUSY.has(prev.status);
  const nowBusy = BUSY.has(a.status);
  if (wasBusy && !nowBusy) {
    peekChat(a.sessionId)?.runEnded();
    if (a.sessionId !== app.activeId && app.openTabs.includes(a.sessionId)) app.unread.add(a.sessionId);
    runEndedNotification(a);
  }
}

// ------------------------------------------------------------------------------------------ notifications (notify.js)

const titleOf = (s) => s?.title || 'New session';
const saidJustNow = new Map(); // sessionId -> when a specific notification (goal, budget) went out

function notifyAbout(sid, s, body) {
  saidJustNow.set(sid, Date.now());
  notify(sid, titleOf(s), body);
}

// A chat's run ended. Not when it goes on by itself right away (a queued message, a subagent's report waking it), not
// while its goal runs (the goal says when it is complete or blocked), and not right after a specific notification about
// it (the goal was completed, the budget asks): that one says why the run ended.
function runEndedNotification(a) {
  if (a.isSubagent) return;
  const sid = a.sessionId;
  setTimeout(() => {
    const now = app.agents.get(sid);
    if (!now || BUSY.has(now.status)) return;
    const s = app.sessionsById.get(sid);
    if (s?.meta?.goal?.status === 'active' || Date.now() - (saidJustNow.get(sid) ?? 0) < 10_000) return;
    notify(sid, titleOf(s), now.status === 'failed' ? `Failed${now.error ? `: ${firstLine(now.error)}` : ''}` : 'Finished');
  }, 1500);
}

// A chat's goal was completed or is blocked (session meta "goal", plugins/NetPI.Goal).
function goalNotification(before, s) {
  const g = s?.meta?.goal;
  if (!before || !g || s.kind === 'subagent' || g.status === before.meta?.goal?.status) return;
  if (g.status === 'complete') notifyAbout(s.id, s, 'Goal complete');
  else if (g.status === 'blocked') notifyAbout(s.id, s, `Goal blocked${g.reason ? `: ${firstLine(g.reason)}` : ''}`);
}

// An agent asks the user (ask_user): the question itself.
function askNotification(d) {
  const q = d?.questions ?? [];
  if (!q.length) return;
  notifyAbout(d.sessionId, app.sessionsById.get(d.sessionId), `Asks: ${firstLine(q[0].question)}${q.length > 1 ? ` (+${q.length - 1} more)` : ''}`);
}

// A tool call waits for the user's OK (a guardrails ask rule).
function approvalNotification(d) {
  const what = d?.kind === 'path' ? `Wants to change ${d.subject}` : `Wants to run: ${firstLine(d?.subject ?? '')}`;
  notifyAbout(d.sessionId, app.sessionsById.get(d.sessionId), what);
}

// The budget stopped a paid call and asks whether this chat may go over it.
function messageNotification(sid, m) {
  if (m?.role !== 'notice' || m.meta?.kind !== 'budget' || !m.meta?.canOverride) return;
  const s = app.sessionsById.get(sid);
  if (s?.kind === 'subagent') return;
  notifyAbout(sid, s, firstLine((m.parts ?? []).filter((p) => p.type === 'text').map((p) => p.text).join('\n')));
}

// ------------------------------------------------------------------------------------------ events

const SCOPED = new Set([
  'message.added',
  'message.updated',
  'messages.compacted',
  'stream.start',
  'stream.delta',
  'stream.tool',
  'stream.reset',
  'stream.end',
  'tool.start',
  'tool.output',
  'tool.end',
  'agent.queue',
  'agent.notice',
  'context.prompt',
]);

let uiReloadTimer = 0;

function onEvent(d, env) {
  const type = env.type;
  if (SCOPED.has(type)) {
    const sid = env.sid ?? d?.sessionId;
    if (!sid) return;
    if (type === 'message.added' && sid !== app.activeId && app.openTabs.includes(sid)) {
      const m = d?.message;
      if (m && ((m.role === 'notice' && m.meta?.kind === 'error') || m.stopReason === 'error')) app.errored.add(sid);
    }
    if (type === 'message.added') messageNotification(sid, d?.message);
    const chat = peekChat(sid);
    if (chat) chat.handle(type, d ?? {});
    else if (type === 'message.added' && sid !== app.activeId && app.openTabs.includes(sid)) {
      if (d?.message?.role === 'assistant') app.unread.add(sid);
    }
    return;
  }
  switch (type) {
    case 'session.created':
    case 'session.updated':
      goalNotification(app.sessionsById.get(d.session?.id), d.session);
      upsertSession(d.session);
      break;
    case 'session.deleted':
      removeSessionLocal(d.id);
      break;
    case 'project.created':
    case 'project.updated':
      upsertProject(d.project);
      break;
    case 'project.deleted':
      app.projects = app.projects.filter((p) => p.id !== d.id);
      break;
    case 'agent.status':
      setAgent(d.agent);
      break;
    case 'session.context':
      app.context.set(d.sessionId, { used: d.used, window: d.window });
      break;
    case 'models.changed':
      loadModels();
      break;
    case 'agents.changed':
      if (Array.isArray(d?.agents)) app.slots = d.agents;
      break;
    case 'ui.changed':
    case 'plugins.changed':
      clearTimeout(uiReloadTimer);
      uiReloadTimer = setTimeout(() => {
        loadUiRegistry();
        loadTools();
        loadPools();
      }, 150);
      break;
    case 'settings.changed':
      app.settingsVersion++;
      break;
    case 'ask.asked':
    case 'ask.closed':
    case 'guard.asked':
    case 'guard.closed':
    case 'guard.cleared':
      askEvent(type, d);
      if (type === 'ask.asked') askNotification(d);
      else if (type === 'guard.asked') approvalNotification(d);
      break;
    case 'ideas.suggested':
      suggestions.event(d);
      break;
    case 'ideas.resolved':
      suggestions.resolved(d);
      break;
    default:
      break;
  }
}

let started = false;
export function startApp() {
  if (started) return;
  started = true;
  bus.on('*', onEvent);
  onOpen(loadAll);
  onNotificationClick((id) => openSession(id));
  connect();
}
