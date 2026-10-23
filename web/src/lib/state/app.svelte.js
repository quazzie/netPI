// Global app state: sessions, projects, models, agents, open tabs, plugin tabs and commands.
// Server events are routed here (broadcast events) and to the per-session ChatStores (scoped events).
import { SvelteMap, SvelteSet } from 'svelte/reactivity';
import { rpc, subscribe, onOpen, connect } from '../rpc.svelte.js';
import { bus } from '../bus.js';
import { load, save, persist, fetchRemote } from '../persist.js';
import { getChat, peekChat, pinChat, dropChat, allChats } from './chat.svelte.js';
import { toast, syncUiStateFromHost, composer, openView } from './ui.svelte.js';
import { toolDefs } from '../tools.js';
import { defaultAgent, useAgent } from '../agents.js';
import { notify, onNotificationClick } from '../notify.js';
import { firstLine as firstLineOf } from '../format.js';
import { BUSY } from '../runStatus.js';

const firstLine = (text) => firstLineOf(text, 160); // notification bodies
import { loadAsks, askEvent, askingSessionIds, budgetStop, pruneSession } from './asks.svelte.js';
import { loadPlans, planEvent, planAskingSessionIds, prunePlans } from './plans.svelte.js';
import { loadScheduleProposals, scheduleEvent, pruneScheduleProposals, proposalSessionIds } from './schedules.svelte.js';
import { welcomeIdeas } from './welcomeIdeas.svelte.js';
import { formatBytes, payloadBytes, sendBudget } from '../images.js';

const TABS_KEY = 'netpi.openTabs';

class AppState {
  rpcMethods = $state.raw(null);
  ready = $state(false);
  info = $state.raw(null);
  projects = $state.raw([]);
  sessions = $state.raw([]); // all known sessions (incl. subagents), newest first
  sessionsMore = $state(false); // the last sessions.list page was full: an older page exists ("Show older")
  sessionsOffset = 0; // rows the server has handed over so far (the next offset)
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
  // parent session id -> its subagents still at work (one pass over the agents, not one per asking row)
  busyKids = $derived.by(() => {
    const m = new Map();
    for (const x of this.agents.values()) {
      if (!x.parentSessionId || !BUSY.has(x.status)) continue;
      const l = m.get(x.parentSessionId);
      if (l) l.push(x);
      else m.set(x.parentSessionId, [x]);
    }
    return m;
  });
  // session id -> tab-dot status, for the lists that show many sessions (see sessionStatus)
  // the chats with something waiting for the user, collected once per change (a set lookup per session, not scans)
  askingIds = $derived(proposalSessionIds(planAskingSessionIds(askingSessionIds())));
  statusById = $derived(new Map(this.sessions.map((s) => [s.id, computeStatus(s.id)])));
  activeSession = $derived(this.activeId ? (this.sessionsById.get(this.activeId) ?? null) : null);
  // the project the user last worked in (the active session's, kept while no session is active)
  lastProjectId = $state(load('netpi.lastProject', null));
  activeProject = $derived(
    this.activeSession?.projectId ? (this.projectsById.get(this.activeSession.projectId) ?? null) : null,
  );
  // sessionId -> what the session's workspace resolves to (files.scope). Keyed on the identity the server gives, so a
  // refresh that answers for a different root replaces the entry instead of being merged into it.
  workspaces = $state.raw(new Map());
}

export const app = new AppState();
export const hasRpc = (method) => app.rpcMethods === null || app.rpcMethods.has(method);

async function loadCapabilities() {
  try {
    const methods = await rpc('rpc.list', {}, { timeout: 8000 });
    if (Array.isArray(methods)) app.rpcMethods = new Set(methods.map((m) => typeof m === 'string' ? m : m.method ?? m.name));
    if (!hasRpc('agent.send')) app.agents.clear();
  } catch { /* Older hosts keep availability unknown. */ }
}

// Sessions deleted while this window was open (removeSessionLocal); see there for why it is a plain Set.
export const goneSessions = new Set();

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

/** What the session's workspace resolves to, or null while it is unknown (not loaded, or not bound to one). */
export function workspaceOf(s) {
  return s?.id ? (app.workspaces.get(s.id) ?? null) : null;
}

/** Record a session's resolved workspace. `scope` is files.scope or a session.workspace payload. */
export function setWorkspace(sessionId, scope) {
  if (!sessionId || !scope) return;
  app.workspaces = new Map(app.workspaces).set(sessionId, scope);
}

/** Load (or refresh) one session's workspace; the answer is dropped when the session moved on in the meantime. */
export async function loadWorkspace(sessionId) {
  if (!sessionId) return null;
  try {
    const scope = await rpc('files.scope', { sessionId });
    if (!scope) return null;
    setWorkspace(sessionId, scope);
    return scope;
  } catch {
    // The workspace plugin is not loaded, or the workspace cannot be used: leave the last known value alone.
    return null;
  }
}

export function isBusy(sessionId) {
  const a = app.agents.get(sessionId);
  return !!a && BUSY.has(a.status);
}

/**
 * The subagents still at work for a chat whose own run has ended: its turn is over, but it is not done — their reports
 * start its next run. Empty while the chat itself runs (then it is running, whatever its subagents do).
 */
export function waitingOn(sessionId) {
  const a = app.agents.get(sessionId);
  if (a && BUSY.has(a.status)) return [];
  return app.busyKids.get(sessionId) ?? [];
}

/** Status for tab dots: asking (a question, or a tool call, waits for the user) | running | queued | yielded | waiting (on its subagents) | error | unread | idle */
export function sessionStatus(sessionId) {
  return app.statusById.get(sessionId) ?? computeStatus(sessionId);
}

function computeStatus(sessionId) {
  if (app.askingIds.has(sessionId)) return 'asking';
  const a = app.agents.get(sessionId);
  if (a) {
    if (a.status === 'running') return 'running';
    if (a.status === 'queued') return 'queued';
    if (a.status === 'yielded') return 'yielded';
    if (a.status === 'failed') return 'error';
  }
  if (waitingOn(sessionId).length) return 'waiting';
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
    // (a row without a parseable updatedAt has no recency: it only replaces a stored row that has none either)
    const stored = Date.parse(list[i].updatedAt);
    if (!Number.isNaN(stored) && !(Date.parse(s.updatedAt) >= stored)) return;
    // the session's project moved: its recorded workspace was resolved for the old one, so it is stale from now on. One
    // files.scope per workspace, not per message: the read below is the only cost of the change.
    if (list[i].projectId !== s.projectId && app.workspaces.has(s.id)) {
      const workspaces = new Map(app.workspaces);
      workspaces.delete(s.id);
      app.workspaces = workspaces;
      if (s.id === app.activeId) void loadWorkspace(s.id);
    }
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

// The session list's page size: the sessions panel's "Show older" loads the next page, so no cap hides a session silently.
const SESSION_PAGE = 300;

/**
 * Load a page of the session list (active sessions, newest first). `append` adds an older page to what is
 * already loaded instead of replacing it (the panel's "Show older" button). Existing callers' behavior is
 * unchanged: the first page replaces, keeping open sessions that fell outside it.
 */
export async function loadSessions(offset = 0, append = false) {
  // the rows in hand when the request goes out: a row that appears while it is in flight (a chat created in
  // another window) is newer than the page that answers, and must survive it
  const had = new Set(app.sessions.map((s) => s.id));
  let list;
  try {
    list = (await rpc('sessions.list', { includeSubagents: true, limit: SESSION_PAGE, offset })).filter((s) => !goneSessions.has(s.id));
  } catch (e) {
    // loadAll's Promise.all must not reject (a reconnect would skip the chats' reconcile): keep the last list.
    // "Show older" has its own caller to tell.
    if (append) throw e;
    console.warn('sessions.list failed', e);
    return null;
  }
  if (append) {
    // the rows we don't have yet, in newest-first order (a row that moved up stays where it is)
    const known = new Set(app.sessions.map((s) => s.id));
    const older = list.filter((s) => !known.has(s.id));
    if (older.length) app.sessions = [...app.sessions, ...older];
  } else {
    const known = new Set(list.map((s) => s.id));
    // keep the open sessions that fell outside the page, and the rows that arrived while it was in flight
    const extra = app.sessions.filter((s) => !known.has(s.id) && (app.openTabs.includes(s.id) || !had.has(s.id)));
    app.sessions = [...list, ...extra];
  }
  // (sessions deleted server-side between pages can make this skip a row; the next full reload heals it)
  app.sessionsOffset = offset + list.length;
  app.sessionsMore = list.length === SESSION_PAGE;
  return list;
}

export async function loadProjects() {
  try {
    app.projects = await rpc('projects.list');
  } catch (e) {
    console.warn('projects.list failed', e); // keep the last list; loadAll's Promise.all must not reject
  }
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

// The agent.status events that land while a runs.list snapshot is in flight: the server read its runs when the
// request went out, so its answer cannot have them, and the clear() below would drop them — a run that ended
// mid-read would be left looking busy (a spinner that never stops).
let runEvents = null;

async function loadAgents() {
  const events = [];
  runEvents = events;
  try {
    const list = await rpc('runs.list', { includeFinished: true }, { timeout: 8000 });
    app.agents.clear();
    for (const a of list ?? []) setAgentNow(a);
    return new Map((list ?? []).map((a) => [a.sessionId, a]));
  } catch (e) {
    // agent plugin missing: nothing runs. A timeout or a lost connection says nothing of the runs: keep what is known.
    if (e?.code === 'not_found') app.agents.clear();
    return null;
  } finally {
    // a load that started after this one holds the buffer now, and replays it over its own answer
    if (runEvents === events) {
      runEvents = null;
      for (const a of events) setAgentNow(a);
    }
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
  let agentsNow = null; // the fresh run states (loadAgents) — null when the agents plugin is missing
  try {
    const [info, , , , , , now] = await Promise.all([
      rpc('app.info').catch(() => null),
      loadProjects(),
      loadSessions(),
      loadModels(),
      loadPools(),
      loadUiRegistry(),
      loadAgents(),
      loadTools(),
      loadAsks(),
      loadPlans(),
      loadScheduleProposals(),
      loadCapabilities(),
    ]);
    app.info = info;
    agentsNow = now;
  } catch (e) {
    toast(`Failed to load: ${e.message}`, 'error');
  }

  if (!reconnect) {
    await syncUiStateFromHost();
    let saved = load(TABS_KEY, null);
    if (!saved) saved = await fetchRemote(TABS_KEY);
    const tabs = (saved?.tabs ?? []).filter((id) => typeof id === 'string');
    for (const id of tabs) if (!app.sessionsById.has(id)) await fetchSession(id);
    // A tab the person opened while this was loading (Ctrl+T right after the page came up) is kept after the saved ones, and
    // stays the active one: restoring must not erase it.
    const restored = tabs.filter((id) => app.sessionsById.has(id));
    app.openTabs = [...restored, ...app.openTabs.filter((id) => !restored.includes(id))];
    const active = app.activeId && app.openTabs.includes(app.activeId)
      ? app.activeId
      : (app.openTabs.includes(saved?.active) ? saved.active : (app.openTabs[0] ?? null));
    app.activeId = active;
    resubscribe();
    if (active) showChat(getChat(active));
    // the open tabs whose runs are busy missed the answer that streamed while the page was gone: their stores are
    // new to this page, so catch each of them up (the active one showChat did)
    for (const id of app.openTabs) {
      if (id === active || !BUSY.has(app.agents.get(id)?.status)) continue;
      getChat(id).catchUpStream();
    }
  } else {
    // we may have missed events: refresh the active chat, mark the others stale
    resubscribe();
    for (const c of allChats()) {
      c.stale = true;
      // a run may have ended while we were disconnected (its stream.end was lost with the socket): reconcile the
      // transient state against the server's run state, so a finished run's partial stream does not stay active —
      // a run that is still busy keeps its live state
      if (agentsNow) c.reconcile(!!agentsNow.get(c.id) && BUSY.has(agentsNow.get(c.id).status));
    }
    if (app.activeId) showChat(getChat(app.activeId));
  }
  app.ready = true;
}

// A chat about to be shown that has not been following its events (new to this page, evicted from the cache, or stale
// after a reconnect): its messages, and while its run is busy what its answer has streamed so far, which an unsubscribed
// page has missed. It is subscribed before this (resubscribe), so the copy and the live events meet.
function showChat(c) {
  pinChat(c.id);
  if (c.loaded && !c.stale) {
    // a loaded chat whose run is busy but whose stream is not active has missed the answer in flight (its store was
    // evicted and re-created, or reloaded stale): catch it up so the live answer is not invisible until it ends
    if (BUSY.has(app.agents.get(c.id)?.status) && !c.stream.active) c.catchUpStream();
    return;
  }
  c.load();
  if (BUSY.has(app.agents.get(c.id)?.status)) c.catchUpStream();
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
  loadWorkspace(id);   // the composer and the Files tab read this; it is per session and may change at any time
  app.unread.delete(id);
  app.errored.delete(id);
  resubscribe();
  showChat(getChat(id));
  persistTabs();
}

/**
 * A chat a webhook started with its "open in a tab" option (meta.openInTab): this window opens it as a background tab.
 * activeId stays where it is — nothing is focused and nothing is announced (a session.created is not a notification). A tab
 * already open does not move, a session that went while this window was open (goneSessions) is not re-materialized, and a chat
 * that started while no window was open is not opened retroactively: the open set is per-window and restores only what it persisted.
 */
function openInTab(session) {
  const id = session?.id;
  if (!id || session.meta?.openInTab !== true || app.openTabs.includes(id) || goneSessions.has(id)) return;
  app.openTabs.push(id);
  resubscribe();   // the store follows its events before it is shown: the copy and the live events meet (see showChat)
  showChat(getChat(id));
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

/** A chat that carries on in another (an approved plan moved to a new chat): the new one takes the old one's place in the tab row. */
export async function replaceTab(oldId, newId) {
  if (!newId) return;
  if (!app.sessionsById.has(newId) && !(await fetchSession(newId))) return;
  const i = app.openTabs.indexOf(oldId);
  if (i < 0) return activate(newId);
  const wasActive = app.activeId === oldId;
  peekChat(oldId)?.saveDraft();
  if (!app.openTabs.includes(newId)) app.openTabs.splice(i, 1, newId);
  else app.openTabs.splice(i, 1);
  if (wasActive) activate(newId);
  else {
    resubscribe();
    persistTabs();
  }
}

export function closeTab(id) {
  const i = app.openTabs.indexOf(id);
  if (i < 0) return;
  const chat = peekChat(id);
  chat?.saveDraft();
  if (chat) {
    // resubscribe() below drops the session, so the stream.end of a running chat never reaches this store: its partial
    // stream would stay active (the LRU keeps it, a reopen would show it frozen). Settle it as the reconnect does and
    // mark it stale, so the next showChat reloads it and catches a still-busy run up.
    chat.stale = true;
    chat.reconcile(false);
  }
  app.openTabs.splice(i, 1);
  if (app.activeId === id) {
    const next = app.openTabs[i] ?? app.openTabs[i - 1] ?? null;
    app.activeId = next;
  }
  resubscribe();
  if (app.activeId) showChat(getChat(app.activeId));
  persistTabs();
  // A chat closed before its first message is not a session: the host stores it in memory only (the first message
  // is what materializes it, so sessions.list never returns it again) and a list reload is the only thing that would
  // take its row away. Without this the "New session" it was created as stays in the list for the rest of the window.
  forgetEmptySession(id);
}

/**
 * A chat with nothing in it leaves the list with its tab: no messages means the host never stored it, so the row is
 * this window's own leftover. Both checks must agree — the session's own count and the messages the open chat holds —
 * so a chat whose row is one message behind (an append that has not arrived as session.updated yet) stays.
 */
function forgetEmptySession(id) {
  const session = app.sessionsById.get(id);
  if (!session || (session.messageCount ?? 0) > 0 || (peekChat(id)?.messages?.length ?? 0) > 0) return;
  app.sessions = app.sessions.filter((s) => s.id !== id);
  dropChat(id);   // the session is gone, so its store and its draft blobs go with it
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

/** Remember the last project used: it preselects the project pickers and is shown as a hint on the start screen. */
export function noteProject(projectId) {
  if (projectId === app.lastProjectId) return;
  app.lastProjectId = projectId ?? null;
  save('netpi.lastProject', app.lastProjectId);
}

export async function newSession(opts = {}) {
  // explicit projectId (null = none) — a new session never inherits one: the + button and Ctrl+T always
  // start without a project, and the project pickers (the chevrons) are the way to start in one
  const projectId = opts?.projectId ?? null;
  try {
    const params = {};
    if (projectId) params.projectId = projectId;
    if (opts.title) params.title = opts.title;
    if (opts.model) params.model = opts.model;
    // Chats the user starts default to high priority. System-created chats use their own RPC paths and retain their
    // caller-defined priority and preemption behavior.
    params.meta = { priority: opts.priority ?? 'high', prioritySource: 'manual' };
    // a new chat runs on the agent chosen last (or the first active one) unless a model was asked for
    const agent = opts.model ? null : defaultAgent();
    if (agent) params.model = agent.model;
    let s = await rpc('sessions.create', params);
    if (agent) s = (await useAgent(s.id, agent.key)) ?? s;
    // a chat the app starts on its own (idea work) gets the automatic profile (settings profiles.autoProfile)
    if (opts.auto && hasRpc('profiles.applyAuto')) s = (await rpc('profiles.applyAuto', { sessionId: s.id }).catch(() => null)) ?? s;
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
  // The plain (non-reactive) set of sessions that went while this window was open, written before any reactive
  // state changes: right after the delete, a Svelte teardown can still answer reactive reads (activeId,
  // openTabs) with the pre-delete values, and only this — read synchronously, never rolled back — may decide
  // that the app must not (re)materialize a chat store for the id. Capped: ids are short, and 4096 deleted
  // sessions in one window is far beyond anything a person deletes.
  goneSessions.add(id);
  if (goneSessions.size > 4096) goneSessions.delete(goneSessions.values().next().value);
  if (app.openTabs.includes(id)) closeTab(id);
  app.sessions = app.sessions.filter((s) => s.id !== id);
  dropChat(id);
  // its unsent draft text (closeTab above just saved it), flags and view choice go with it
  try {
    localStorage.removeItem(`netpi.draft.${id}`);
  } catch {}
  app.unread.delete(id);
  app.errored.delete(id);
  openView(id, null);
  // drop the per-session bookkeeping that would otherwise keep an entry for this chat forever
  saidJustNow.delete(id);
  pruneSession(id);
  prunePlans(id);
  pruneScheduleProposals(id);
  app.agents.delete(id); // its last run state (agent.status), a subagent's included
  app.context.delete(id); // its context ring (session.context)
  if (app.workspaces.has(id)) {
    const workspaces = new Map(app.workspaces); // $state.raw: a copy, or nothing that reads it re-renders
    workspaces.delete(id);
    app.workspaces = workspaces;
  }
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
  if (!hasRpc('agent.send')) {
    toast('Execution unavailable: enable the Runtime plugin to send. Your draft is kept.', 'warn');
    return false;
  }
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

/**
 * Stops the chat's run. By default the run alone: the subagents it started keep working and report back (which wakes the
 * chat), so a parent can be stopped and redirected without losing its workers. `all` stops its subagents too (Shift+Esc).
 */
export async function abortAgent(sessionId, all = false) {
  try {
    await rpc('agent.abort', { sessionId, subagents: all });
  } catch (e) {
    toast(`Abort failed: ${e.message}`, 'error');
  }
}

/**
 * "Steer now" on a follow-up that waits behind a long run: it is delivered at the run's next step instead of after the
 * run (agent.promote); the queue event that follows turns the chip into a steer (idea-equiw4).
 */
export async function promoteQueued(sessionId, id) {
  try {
    if (!(await rpc('agent.promote', { sessionId, id }))) toast('That message is no longer waiting in the queue.', 'warn');
  } catch (e) {
    toast(e.message, 'error');
  }
}

export async function dequeue(sessionId, id) {
  try {
    await rpc('agent.dequeue', { sessionId, id });
    peekChat(sessionId)?.dropQueued(id);
  } catch (e) {
    toast(e.message, 'error');
  }
}

/**
 * The send action on a queued chip (after a stop, when the queue has no run left to run in): the queued copy
 * is removed first, and only then is the same text sent as the next turn. If the item no longer exists —
 * already delivered, or already removed — the removal fails and nothing is sent: a message that already
 * went out is never sent twice. The chip leaves the UI either way, but a failed removal is said out loud so
 * a click that sends nothing is never a silent drop.
 */
export async function resendQueued(sessionId, id, text) {
  const c = peekChat(sessionId);
  if (!c || !c.queue.some((q) => q.id === id) || !text?.trim()) return;
  let removed;
  try {
    removed = await rpc('agent.dequeue', { sessionId, id });
  } catch (e) {
    toast(e.message || 'That queued message could not be removed.', 'error');
    return; // it is still there (or the host is gone): keep the chip, say what happened
  }
  c.dropQueued(id);
  if (!removed) {
    toast('That queued message is no longer in the queue (it was delivered or removed), so it was not sent.', 'warn');
    return;
  }
  await sendMessage(sessionId, text, [], 'auto');
}

function setAgent(a) {
  if (!a?.sessionId) return;
  if (runEvents) {
    runEvents.push(a);
    return;
  }
  setAgentNow(a);
}

function setAgentNow(a) {
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

// A suppression entry is only worth the window below (10 s): it exists so a "Finished" is not announced right
// after the "Goal complete" that explains the ending. Without a lifetime the map would keep one entry per
// notified session for the life of the window, so expired ones are dropped on every write, and the total is
// capped (a flood of notifications keeps the most recent sessions; an evicted one just gets announced a moment
// earlier than the flood would have let it).
const SAID_JUST_NOW_WINDOW = 10_000;
const SAID_JUST_NOW_MAX = 64;

function noteSaidNow(sid) {
  const now = Date.now();
  for (const [k, t] of saidJustNow) if (now - t > SAID_JUST_NOW_WINDOW) saidJustNow.delete(k);
  if (saidJustNow.size >= SAID_JUST_NOW_MAX) {
    let oldest = null;
    for (const [k, t] of saidJustNow) if (oldest === null || t < oldest[1]) oldest = [k, t];
    if (oldest) saidJustNow.delete(oldest[0]);
  }
  saidJustNow.set(sid, now);
}

function notifyAbout(sid, s, body) {
  noteSaidNow(sid);
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
    if (s?.meta?.goal?.status === 'active' || Date.now() - (saidJustNow.get(sid) ?? 0) < SAID_JUST_NOW_WINDOW) return;
    // a chat's run that fails ends idle, with its error (only a subagent's ends "failed"): a refused call (the budget, an
    // agent switched off) or a provider error is not "Finished", in a chat that is not open too, whose notice never
    // reaches this page (it subscribes to its open tabs)
    const failed = now.status === 'failed' || !!now.error;
    notify(sid, titleOf(s), failed ? `Failed${now.error ? `: ${firstLine(now.error)}` : ''}` : 'Finished');
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

// A plan was submitted and waits for the decision (plugins/NetPI.Plan).
function planNotification(d) {
  if (!d?.submitted) return;
  notifyAbout(d.sessionId, app.sessionsById.get(d.sessionId), `Plan ready: ${firstLine(d.title ?? '')}`);
}

// A schedule the agent proposed waits for the user's OK on the card (plugins/NetPI.Schedules).
function scheduleNotification(d) {
  if (!d?.id) return;
  notifyAbout(d.sessionId, app.sessionsById.get(d.sessionId), `New schedule: ${firstLine(d.schedule?.name ?? '')}`);
}

// A tool call waits for the user's OK (a guardrails ask rule).
function approvalNotification(d) {
  const what = d?.kind === 'path' ? `Wants to change ${d.subject}` : `Wants to run: ${firstLine(d?.subject ?? '')}`;
  notifyAbout(d.sessionId, app.sessionsById.get(d.sessionId), what);
}

// The budget stopped a paid call and asks whether this chat may go over it.
function messageNotification(sid, m) {
  if (m && m.role !== 'notice') budgetStop(sid, false); // the chat went on after the stop
  if (m?.role !== 'notice' || m.meta?.kind !== 'budget' || !m.meta?.canOverride) return;
  const s = app.sessionsById.get(sid);
  if (s?.kind === 'subagent') return;
  budgetStop(sid, true);
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
  'tool.result',
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
    if (chat) chat.handle(type, d ?? {}, env.seq);
    else if (type === 'message.added' && sid !== app.activeId && app.openTabs.includes(sid)) {
      if (d?.message?.role === 'assistant') app.unread.add(sid);
    }
    return;
  }
  switch (type) {
    case 'session.created':
      goalNotification(app.sessionsById.get(d.session?.id), d.session);
      upsertSession(d.session);
      openInTab(d.session);   // the webhook's "open in a tab": read on created only — a session.updated must not re-open tabs
      break;
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
    case 'session.workspace':
      // The server resolved the new root for us; an answer for the previous workspace is never kept.
      setWorkspace(d.sessionId, d);
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
    // the host restarts by itself to install an update (RestartWhenIdle): a browser tab reconnects, a desktop window reopens
    case 'app.restarting':
      toast(`NetPI restarts to install ${(d?.reasons ?? ['an update']).join(', ')}; it is back in a few seconds`);
      break;
    case 'app.restartFailed':
      toast(`NetPI could not restart to install an update: ${d?.error ?? 'unknown error'}`, 'error');
      break;
    case 'plugins.reloaded':
    case 'rpc.changed':
    case 'services.changed':
      clearTimeout(uiReloadTimer);
      uiReloadTimer = setTimeout(() => {
        loadUiRegistry();
        loadTools();
        loadPools();
        loadCapabilities();
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
    case 'plan.changed':
    case 'plan.enter.asked':
    case 'plan.enter.closed':
      planEvent(type, d);
      if (type === 'plan.changed') planNotification(d);
      else if (type === 'plan.enter.asked') notifyAbout(d.sessionId, app.sessionsById.get(d.sessionId), `Suggests plan mode${d.reason ? `: ${firstLine(d.reason)}` : ''}`);
      break;
    case 'schedule.proposed':
    case 'schedule.decided':
      scheduleEvent(type, d);
      if (type === 'schedule.proposed') scheduleNotification(d);
      break;
    case 'autonomy.braked':
      // a schedule's or webhook's chats hit their hourly cap of changes (plugins/NetPI.Autonomy): the one thing it tells unasked
      if (d?.sessionId) notifyAbout(d.sessionId, app.sessionsById.get(d.sessionId), `Stopped: ${d.label ?? 'a chat'} made ${d.limit} changes in an hour`);
      break;
    case 'ui.open':
      // a plugin shows the user one of its session views in a chat (e.g. the agent's browser): there now, and a
      // notification when the chat is not the one in front
      if (d?.sessionId) {
        openView(d.sessionId, d.view ?? null);
        if (d.view && d.sessionId !== app.activeId) {
          const s = app.sessionsById.get(d.sessionId);
          if (s?.kind !== 'subagent') notifyAbout(d.sessionId, s, firstLine(d.text || 'Wants to show you something'));
        }
      }
      break;
    case 'ideas.changed':
      // the backlog moved: the welcome screen offers what is in it now, and this window's picks too
      welcomeIdeas.changed();
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
