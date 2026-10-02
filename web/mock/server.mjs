#!/usr/bin/env node
// NetPI mock server: serves the built UI (web/dist) and plugin UI bundles, and implements the
// WebSocket protocol from docs/PROTOCOL.md with in-memory data and a scripted fake agent.
//
//   npm run mock                      → http://127.0.0.1:7431/?token=dev
//   node web/mock/server.mjs --port 7431 --token dev --no-auth
//   MOCK_SPEED=4 npm run mock         → 4× faster fake agent
import http from 'node:http';
import fs from 'node:fs';
import fsp from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { WebSocketServer } from 'ws';
import { store, seed, resetStore, seedMany, clearBulk, MODELS, DEFAULT_MODEL, REPO, mkSession, pushMessage, agentFor, newId, text } from './store.mjs';
import { createAgentRuntime } from './agent.mjs';
import { createWork } from './work.mjs';
import { createIdeas } from './ideas.mjs';
import { createDiag, toolRows } from './diag.mjs';
import { SETTINGS_SCHEMA } from './settingsSchema.mjs';

// ------------------------------------------------------------------------------------------ options
const args = process.argv.slice(2);
const opt = (name, def) => {
  const i = args.indexOf(`--${name}`);
  return i >= 0 && args[i + 1] && !args[i + 1].startsWith('--') ? args[i + 1] : def;
};
const PORT = Number(opt('port', process.env.PORT || 7431));
const HOST = opt('host', '127.0.0.1');
const TOKEN = opt('token', process.env.NETPI_TOKEN || 'dev');
const NO_AUTH = args.includes('--no-auth');
// the build output itself (not the copy a running NetPI serves from artifacts/app/wwwroot)
const WWWROOT = [path.join(REPO, 'web/dist'), path.join(REPO, 'artifacts/app/wwwroot')].find((d) => fs.existsSync(path.join(d, 'index.html'))) ?? path.join(REPO, 'web/dist');
// Plugin UI bundles the mock serves (built by `npm run build:plugins`); a tab is listed only when its bundle exists.
const PLUGIN_UIS = [
  { pluginId: 'netpi.work', dir: 'plugins/NetPI.Work/wwwroot', tabs: [{ id: 'work', title: 'Work', panel: 'right', icon: 'work', order: 10 }] },
  { pluginId: 'netpi.ideas', dir: 'plugins/NetPI.Ideas/wwwroot', tabs: [{ id: 'ideas', title: 'Ideas', panel: 'right', icon: 'idea', order: 20 }] },
  { pluginId: 'netpi.diagnostics', dir: 'plugins/NetPI.Diagnostics/wwwroot', tabs: [{ id: 'diagnostics', title: 'Diagnostics', panel: 'right', icon: 'bug', order: 90 }] },
  { pluginId: 'netpi.tools.files', dir: 'plugins/NetPI.Tools.Files/wwwroot', tabs: [{ id: 'files', title: 'Files', panel: 'left', icon: 'files', order: 30 }] },
  {
    pluginId: 'netpi.sample',
    dir: 'web/mock/sample-plugin/wwwroot',
    tabs: [
      { id: 'sample', title: 'Sample', panel: 'right', icon: 'puzzle', order: 95 },
      { id: 'events', title: 'Events', panel: 'right', icon: 'activity', order: 96, export: 'mountEvents' },
    ],
  },
];
const PLUGINS = Object.fromEntries(PLUGIN_UIS.map((p) => [p.pluginId, path.join(REPO, p.dir)]));
const VERSION = '0.1.0-mock';

seed();

// ------------------------------------------------------------------------------------------ event bus
const clients = new Set();
const wsLog = []; // the last rpc frames clients sent (mock.wsLog), so a frame that never arrived is visible in a failed e2e run
let missingMethods = new Set();
let seq = 0;
const recent = [];

function publish(type, d = {}, sid = null, source = 'mock') {
  const env = { t: 'ev', type, sid: sid ?? null, d, seq: ++seq, ts: Date.now(), source };
  recent.push(env);
  if (recent.length > 300) recent.shift();
  const json = JSON.stringify(env);
  for (const c of clients) {
    if (sid && !c.subs.has(sid) && !c.subs.has('*')) continue;
    if (c.ws.readyState === 1) c.ws.send(json);
  }
}

// log ring for logs.recent / diagnostics
const logs = [];
function log(level, category, message, exception) {
  logs.push({ time: new Date().toISOString(), level, category, message, ...(exception ? { exception } : {}) });
  if (logs.length > 500) logs.shift();
}

const work = createWork({ publish, log, agentsView: () => agentPools() });
work.start();
const ideas = createIdeas({ publish });
ideas.seed();
const diag = createDiag({ publish, log });
diag.seed();
const agent = createAgentRuntime({ publish, work, log, onFirstMessage: materialize });

// ------------------------------------------------------------------------------------------ rpc
const RPC_DOCS = {
  'agent.send': 'Send a message to a session\'s agent: { sessionId, text, images?, mode? } → AgentInfo',
  'agent.abort': 'Abort the current run of a session\'s agent: { sessionId } → bool',
  'sessions.list': 'Sessions, newest first: { projectId?, search?, includeSubagents?, includeArchived?, archivedOnly?, limit?, offset? }',
  'sessions.messages': 'Message page: { id, beforeSeq?, limit? (60) } → { messages, hasMore }',
  'work.snapshot': 'Aggregated overview for the Work tab → { agents, runs, processes, usage, time, errors? }',
  'diag.snapshot': 'Diagnostics overview → { plugins, tools, rpc, events, logs, runtime, time }',
  'diag.toolsets': "A session's tools now and every change with its cause → { sessionId, tools, baseline, changes, reloads }",
  'ideas.list': 'Ideas of a project/session: { sessionId?, projectId? } → { file, scope, ideas, … }',
  'files.list': 'List one directory for the file tree: { sessionId?, cwd?, dir? } → { root, dir, entries }',
  'files.search': 'Fuzzy file-name search for @ mentions: { sessionId?, query, limit? } → { path, rel, isDir }[]',
  'files.git': "The workspace's changes since the last commit, for the Files tab: { sessionId?, cwd? } → { repo, branch, ahead, behind, files, added, deleted } | null",
  'logs.recent': 'Recent log entries: { max? } → { time, level, category, message, exception? }[]',
};
const filesOpened = [];
const msgLoads = new Map(); // e2e test helper: sessions.messages calls per session — a fresh call means the chat store was rebuilt (evicted and reopened)

const SYSTEM_PROMPT = (project, session) => `You are a coding agent running in NetPI, an agent harness on the user's own machine. Work through the tools you have: act rather than describe, check the results and verify your work when practical. Ask only when a request is genuinely ambiguous or an action would be destructive. Be concise, and end with a short summary of what you did or found.

# Environment
- OS: ${os.type()} ${os.release()}
- Messages in <system-notice> tags come from NetPI, not from the user: your working directory and project (relative paths resolve against the latest one), instruction files, subagent reports, reminders and errors.

# Tools
- Prefer read/grep/find/ls over shell commands for exploring files.
- Use edit for small changes; include enough context for oldText to match exactly once.
- Run the relevant build or tests before you say you're done.
- Record research and plans that are deferred, out of scope or not feasible now in the ideas backlog (ideas, action add), and look at the open ideas (action list) before larger work. When you finish the work an idea describes, set it to done (action update).

# Instruction files
AGENTS.md and CLAUDE.md files reach you as notices. They are the lean entry point for agents: the essentials, plus pointers to deeper docs. When your task touches something they point to, read that doc first.
Keep them lean. When you learn something the next agent would otherwise have to rediscover (a non-obvious command, a pitfall, a convention), add one line to the most specific AGENTS.md, or put the details in the doc it points to and add a pointer there. Correct outdated lines instead of adding new ones next to them, and leave out what the code or git history already shows. Ask before creating an AGENTS.md where there is none.`;

class RpcError extends Error {
  constructor(code, message) {
    super(message);
    this.code = code;
  }
}
const notFound = (what) => new RpcError('not_found', `${what} not found`);

// eleven background idea checks in run order (oldest first): the Ideas view keeps the last 10, so mock-check-01 never shows
const ideasWorkList = () => [
  ...Array.from({ length: 10 }, (_, k) => ({
    id: `mock-check-${String(k + 1).padStart(2, '0')}`,
    purpose: `Mock check ${k + 1}`,
    model: 'aiproxy/qwen3.8-27b',
    status: 'finished',
    reason: null,
    finishedAt: new Date(Date.now() - (11 - k) * 60_000).toISOString(),
  })),
  { id: 'mock-verifier-dropped', purpose: 'Verify idea completion', model: 'aiproxy/qwen3.8-27b', status: 'dropped', reason: 'Model capacity did not open before the deadline', finishedAt: new Date().toISOString() },
];
const need = (p, k) => {
  if (p?.[k] == null || p[k] === '') throw new RpcError('bad_request', `Missing parameter: ${k}`);
  return p[k];
};
function getSession(id) {
  const s = store.sessions.get(id);
  if (!s) throw notFound(`Session ${id}`);
  return s;
}
// ------------------------------------------------------------------------------------------ agents you set up, budget
const AGENT_RESERVED = new Set(['maxDepth']);
/** Mock prices ($ per Mtok) for the cloud models; local models are free. */
const MOCK_PRICES = { 'anthropic/claude-sonnet-4-6': [3, 15] };
const MOCK_SPEND = [{ lane: 'anthropic', provider: 'anthropic', model: 'claude-sonnet-4-6', calls: 12, inputTokens: 180000, outputTokens: 9000, cacheReadTokens: 0, cacheWriteTokens: 0, costUsd: 0.675, unknownCost: false }];
/** The agents in the settings (agents.<id>), each with its state; like the host: always listed, active while its model is loaded. */
function agentsInSettings() {
  return Object.entries(store.settings.agents ?? {}).filter(([id, v]) => !AGENT_RESERVED.has(id) && v && typeof v === 'object' && typeof v.model === 'string');
}
function agentPools() {
  const pools = work.slots();
  const onModel = new Set(); // the mock's running calls go to the first agent on their model
  const agents = agentsInSettings().map(([id, v]) => {
    const m = MODELS.find((x) => x.ref === v.model);
    const price = v.cost?.input != null ? [v.cost.input, v.cost.output ?? 0] : MOCK_PRICES[v.model] ?? (m?.isLocal ? [0, 0] : null);
    const running = onModel.has(v.model) ? null : pools.find((p) => p.models.includes(v.model));
    onModel.add(v.model);
    const capacity = v.instances ?? (m?.isLocal ? (m.concurrency ?? 1) : 1);
    const disabled = !!v.disabled;
    const unavailable = disabled
      ? 'disabled'
      : !m
        ? `${v.model} is not in the model list`
        : m.isLocal && m.status !== 'loaded'
          ? `${m.id} isn't loaded`
          : null;
    const owners = running?.owners ?? [];
    const waiters = running?.waiters ?? [];
    const status = waiters.length ? 'queued' : owners.length >= capacity ? 'full' : owners.length ? 'busy' : disabled ? 'disabled' : unavailable ? 'unavailable' : 'idle';
    return {
      key: id, provider: v.model.split('/')[0], capacity, busy: owners.length, queued: waiters.length, models: [v.model], owners, waiters,
      source: 'settings', status, configured: true, model: v.model, use: v.use || null, available: !unavailable, unavailable, disabled,
      priceInput: price?.[0] ?? null, priceOutput: price?.[1] ?? null, priceSource: v.cost ? 'settings' : m?.isLocal ? 'local' : price ? 'catalog' : 'unknown',
      free: !!price && price[0] === 0 && price[1] === 0, spentTodayUsd: 0, dailyLimitUsd: v.budget?.limitUsd ?? null,
    };
  });
  const others = pools.filter((p) => (p.busy || p.queued) && !p.models.every((m) => onModel.has(m))).map((p) => ({ ...p, configured: false, available: true }));
  return [...agents, ...others];
}
/** Sample model calls for diag.calls: one running, one finished after a retry, one failed. */
function mockCalls() {
  const at = (s) => new Date(Date.now() - s * 1000).toISOString();
  return [
    { id: 3, startedAt: at(4), state: 'running', model: 'aiproxy/qwen3.8-27b', purpose: 'agent', agent: 'qwen', sessionId: null, runId: 'agt_s', firstTokenMs: 900, durationMs: 4000, attempts: 1 },
    { id: 2, startedAt: at(60), state: 'ok', model: 'aiproxy/qwen3.8-27b', purpose: 'agent', agent: 'qwen', sessionId: null, runId: 'agt_m', firstTokenMs: 1400, durationMs: 22800, attempts: 2, inputTokens: 4200, cacheReadTokens: 38000, outputTokens: 910, stopReason: 'tool_use' },
    { id: 1, startedAt: at(300), state: 'error', model: 'aiproxy/qwen38-27b-iq3s', purpose: 'agent', agent: null, sessionId: null, runId: 'agt_t', firstTokenMs: null, durationMs: 310, attempts: 1, error: 'HTTP 503 backend_unavailable [x-request-id: req_7f2]' },
  ];
}

function budgetStatus() {
  const b = store.settings.budget ?? {};
  const now = new Date();
  const reset = Math.min(28, Math.max(1, b.resetDay ?? 1));
  let start = new Date(now.getFullYear(), now.getMonth(), reset);
  if (now < start) start = new Date(now.getFullYear(), now.getMonth() - 1, reset);
  const end = new Date(start.getFullYear(), start.getMonth() + 1, reset);
  const spent = MOCK_SPEND.reduce((a, m) => a + m.costUsd, 0);
  const day = (d) => `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`;
  const warnPercent = b.warnPercent ?? 80;
  return {
    monthlyUsd: b.monthlyUsd ?? null, dailyUsd: b.dailyUsd ?? null, warnPercent, resetDay: reset, onLimit: b.onLimit ?? 'stop',
    periodStart: day(start), periodEnd: day(end), spentUsd: spent, todayUsd: 0.2,
    warning: !!b.monthlyUsd && spent >= (b.monthlyUsd * warnPercent) / 100, exhausted: !!b.monthlyUsd && spent >= b.monthlyUsd,
  };
}

/** The profiles in the settings (profiles.<id> = { name, prompt, toolsOff }), by name. */
function profilesList() {
  return Object.entries(store.settings.profiles ?? {})
    .filter(([id, v]) => id !== 'defaultProfile' && id !== 'none' && v && typeof v === 'object')
    .map(([id, v]) => ({ id, name: v.name || id, prompt: v.prompt || null, toolsOff: v.toolsOff ?? [] }))
    .sort((a, b) => a.name.localeCompare(b.name));
}
const profileById = (id) => (id ? profilesList().find((x) => x.id === id) : null);
/** A new chat's profile: its project's default (or "none"), else the global default. */
function defaultProfileFor(s) {
  const chosen = s.projectId ? store.projects.get(s.projectId)?.meta?.profile : null;
  if (chosen === 'none') return null;
  return profileById(chosen) ?? profileById(store.settings.profiles?.defaultProfile);
}
/** Apply a profile (null: none) to a session: meta.profile, meta.identity, meta.toolsOff. */
function writeProfile(s, profile) {
  const meta = { ...(s.meta ?? {}), profile: profile?.id ?? null };
  if (profile?.prompt) meta.identity = profile.prompt;
  else delete meta.identity;
  if (profile?.toolsOff?.length) meta.toolsOff = [...profile.toolsOff].sort();
  else delete meta.toolsOff;
  s.meta = meta;
}

/**
 * The first message materializes a transient session (like the host): its default profile (if it has not chosen one
 * yet), the project's lastUsedAt, and session.created — all before the message.added that triggers it.
 */
function materialize(sid) {
  const s = store.sessions.get(sid);
  if (!s) return;
  if (!s.meta || !('profile' in s.meta)) {
    const p = defaultProfileFor(s);
    if (p) writeProfile(s, p);
  }
  if (s.projectId) {
    const pr = store.projects.get(s.projectId);
    if (pr) pr.lastUsedAt = new Date().toISOString();
  }
  publish('session.created', { session: s });
}

/** agent.tools for a session: the tools its agent gets, each with its switch. */
function sessionTools(s) {
  const off = new Set((s.meta?.toolsOff ?? []).map((n) => n.toLowerCase()));
  const tools = toolRows()
    .filter((t) => t.active && !t.disabled)
    .sort((a, b) => a.name.localeCompare(b.name))
    .map(({ name, label, category, description, readOnly, pluginId }) => ({ name, label, category, description, readOnly, pluginId, on: !off.has(name) }));
  return { sessionId: s.id, started: (s.messageCount ?? 0) > 0, contextTokens: s.contextTokens ?? 0, off: [...(s.meta?.toolsOff ?? [])], tools };
}

/** Apply a change to the session's goal (goal.* RPCs); `change` returns the fields to update. */
function goalChange(p, change) {
  const s = getSession(need(p, 'sessionId'));
  const g = s.meta?.goal;
  if (!g || g.status === 'cleared') throw new RpcError('bad_request', 'There is no goal.');
  s.meta = { ...s.meta, goal: { ...g, ...change(g), updatedAt: new Date().toISOString() } };
  publish('session.updated', { session: s });
  return s.meta.goal;
}

let fileIndex = { root: null, at: 0, files: [] };
async function listFiles(root) {
  if (fileIndex.root === root && Date.now() - fileIndex.at < 10_000) return fileIndex.files;
  const skip = new Set(['.git', 'node_modules', 'bin', 'obj', '.vs', '.idea', 'dist', 'build', 'artifacts', '__pycache__', '.venv']);
  const out = [];
  async function walk(dir, rel, depth) {
    if (out.length > 20000 || depth > 12) return;
    let entries;
    try {
      entries = await fsp.readdir(dir, { withFileTypes: true });
    } catch {
      return;
    }
    for (const e of entries) {
      if (skip.has(e.name)) continue;
      const r = rel ? `${rel}/${e.name}` : e.name;
      const isDir = e.isDirectory();
      out.push({ path: path.join(dir, e.name), rel: r, isDir });
      if (isDir) await walk(path.join(dir, e.name), r, depth + 1);
    }
  }
  await walk(root, '', 0);
  fileIndex = { root, at: Date.now(), files: out };
  return out;
}
function rankFile(f, q) {
  if (!q) return f.rel.split('/').length;
  const name = f.rel.slice(f.rel.lastIndexOf('/') + 1).toLowerCase();
  const rel = f.rel.toLowerCase();
  if (name.includes(q)) return 100 + rel.length / 100;
  if (rel.includes(q)) return 300 + rel.length / 100;
  let i = 0;
  for (const ch of rel) if (ch === q[i]) i++;
  return i === q.length ? 600 + rel.length / 100 : Infinity;
}
function sessionCwd(id) {
  const s = id ? store.sessions.get(id) : null;
  const p = s?.projectId ? store.projects.get(s.projectId) : null;
  return p?.path && fs.existsSync(p.path) ? p.path : REPO;
}

function uiTabs() {
  const tabs = [];
  for (const p of PLUGIN_UIS) {
    let version;
    try {
      version = String(Math.floor(fs.statSync(path.join(PLUGINS[p.pluginId], 'ui.js')).mtimeMs));
    } catch {
      continue; // bundle not built
    }
    for (const t of p.tabs) tabs.push({ module: 'ui.js', ...t, pluginId: p.pluginId, version });
  }
  return tabs;
}

let procTailDelayMs = 0; // e2e test helper: delay the tail responses so the UI can collapse a row inside its fetchTail() await
let filesDelayMs = 0; // e2e test helper: delay the files.* responses so a workspace switch lands mid-fetch
let thinkDelayMs = 0; // e2e test helper: hold the live thinking line so a waitForSelector can see it (mock.thinkDelay)
let offlineUntil = 0; // e2e test helper: while in effect the /ws upgrades are refused and open sockets dropped — the server keeps running, its events are just lost (the browser is offline)
let filesCalls = []; // e2e test helper: the files.* responses served, in order, for the late-response checks
let listCalls = []; // e2e test helper: the sessions.list calls with their params (the archived-only check)
let diagCallsDelayMs = 0; // e2e test helper: delay the diag.calls responses so a slow poll spans the 2 s interval
let diagCallsInFlight = 0; // e2e test helper: diag.calls requests in flight right now
let diagCallsMaxInFlight = 0; // e2e test helper: the peak of the above (single-flight polling must never exceed 1)
let diagCallsServed = 0; // e2e test helper: diag.calls responses served
const handlers = {
  'app.info': () => ({ version: VERSION, os: `${os.type()} ${os.release()}`, home: os.homedir(), appDir: path.join(REPO, 'artifacts/app'), defaultWorkspace: path.join(os.homedir(), '.netpi', 'workspace'), desktop: false }),

  'projects.list': () => [...store.projects.values()],
  'projects.create': async (p) => {
    const name = need(p, 'name');
    const dir = need(p, 'path');
    if (p.create) await fsp.mkdir(dir, { recursive: true }).catch(() => {});
    const now = new Date().toISOString();
    const pr = { id: newId('prj'), name, path: dir, createdAt: now, updatedAt: now, lastUsedAt: null };
    store.projects.set(pr.id, pr);
    publish('project.created', { project: pr });
    return pr;
  },
  'projects.update': (p) => {
    const pr = store.projects.get(need(p, 'id'));
    if (!pr) throw notFound('Project');
    if (p.name) pr.name = p.name;
    if (p.path) pr.path = p.path;
    // meta is merged key by key; null removes a key
    if (p.meta && typeof p.meta === 'object') {
      const meta = { ...(pr.meta ?? {}) };
      for (const [k, v] of Object.entries(p.meta)) v == null ? delete meta[k] : (meta[k] = v);
      pr.meta = Object.keys(meta).length ? meta : null;
    }
    pr.updatedAt = new Date().toISOString();
    publish('project.updated', { project: pr });
    return pr;
  },
  'projects.delete': (p) => {
    const id = need(p, 'id');
    if (!store.projects.delete(id)) throw notFound('Project');
    publish('project.deleted', { id });
    return true;
  },

  'sessions.list': (p = {}) => {
    const q = (p.search ?? '').toLowerCase();
    listCalls.push({ search: q || null, includeSubagents: !!p.includeSubagents, includeArchived: !!p.includeArchived, archivedOnly: !!p.archivedOnly, limit: p.limit ?? 100, offset: p.offset ?? 0 });
    let list = [...store.sessions.values()].filter((s) => {
      if ((store.messages.get(s.id) ?? []).length === 0) return false; // like the host: a session without messages is not listed
      if (p.archivedOnly) {
        if (!s.archived) return false;
      } else if (!p.includeArchived && s.archived) return false;
      if (!p.includeSubagents && s.kind === 'subagent' && !p.parentSessionId) return false;
      if (p.projectId && s.projectId !== p.projectId) return false;
      if (p.parentSessionId && s.parentSessionId !== p.parentSessionId) return false;
      if (q) {
        if (s.title.toLowerCase().includes(q)) return true;
        return (store.messages.get(s.id) ?? []).some((m) => m.parts.some((x) => x.type === 'text' && x.text.toLowerCase().includes(q)));
      }
      return true;
    });
    list.sort((a, b) => Date.parse(b.updatedAt) - Date.parse(a.updatedAt));
    const off = p.offset ?? 0;
    return list.slice(off, off + (p.limit ?? 100));
  },
  'sessions.create': (p = {}) => {
    const s = mkSession({ title: p.title ?? '', projectId: p.projectId ?? null, model: p.model ?? null, reasoning: p.reasoning ?? null });
    agentFor(s.id);
    // like the host: a no-message session is transient — not listed and not announced (session.created); its default
    // profile and the project's lastUsedAt arrive with its first message (materialize)
    return s;
  },
  // like the host (SessionFork): the messages up to upToSeq with their seqs, the setup's meta without the run state
  'sessions.fork': (p = {}) => {
    const from = getSession(need(p, 'id'));
    if (from.kind === 'subagent') throw new RpcError('bad_request', "A subagent's chat can't be forked: fork the chat that started it.");
    const msgs = store.messages.get(from.id) ?? [];
    const last = msgs.at(-1)?.seq ?? 0;
    const upTo = Math.max(0, Math.min(p.upToSeq ?? last, last));
    const meta = structuredClone(from.meta ?? {});
    for (const k of ['goal', 'todo', 'budgetAllowedFrom', 'agentId', 'parentAgentId', 'agentInstructions', 'forkedFrom']) delete meta[k];
    meta.forkedFrom = { sessionId: from.id, title: from.title, seq: upTo };
    // "Title (fork)", then "Title (fork 2)"…: the first that no chat has
    const base = /^(.*) \(fork(?: \d+)?\)$/.exec(from.title ?? '')?.[1] ?? from.title ?? '';
    const taken = new Set([...store.sessions.values()].map((x) => x.title));
    let title = `${base} (fork)`;
    for (let n = 2; taken.has(title); n++) title = `${base} (fork ${n})`;
    const s = mkSession({ title, projectId: from.projectId, model: from.model, reasoning: from.reasoning, meta });
    let copied = 0;
    for (const x of msgs) {
      if (x.seq > upTo) break;
      const { id, seq, sessionId, role, parts, createdAt, ...rest } = structuredClone(x);
      pushMessage(s.id, role, parts, rest, Date.parse(createdAt));
      copied++;
    }
    agentFor(s.id);
    // like the host: a fork with no messages stays transient — no session.created, no session.forked
    if (copied > 0) {
      publish('session.created', { session: s });
      publish('session.forked', { sessionId: s.id, fromSessionId: from.id, upToSeq: upTo });
    }
    return s;
  },
  'sessions.get': (p) => getSession(need(p, 'id')),
  'sessions.update': (p) => {
    const s = getSession(need(p, 'id'));
    if (p.title !== undefined) s.title = p.title;
    if (p.model !== undefined) s.model = p.model || null;
    if (p.reasoning !== undefined) s.reasoning = p.reasoning || null;
    if (p.archived !== undefined) s.archived = !!p.archived;
    s.updatedAt = new Date().toISOString();
    publish('session.updated', { session: s });
    return s;
  },
  'sessions.delete': (p) => {
    const id = need(p, 'id');
    getSession(id);
    agent.abort(id);
    store.sessions.delete(id);
    store.messages.delete(id);
    store.agents.delete(id);
    publish('session.deleted', { id });
    return true;
  },
  'sessions.setProject': (p) => {
    const s = getSession(need(p, 'id'));
    const pr = p.projectId ? store.projects.get(p.projectId) : null;
    if (p.projectId && !pr) throw notFound('Project');
    s.projectId = pr?.id ?? null;
    if (pr) pr.lastUsedAt = new Date().toISOString();
    const msg = pr
      ? `Project changed to **${pr.name}** (\`${pr.path}\`). The working directory is now \`${pr.path}\`.`
      : 'Project detached. The working directory is now the default workspace.';
    const m = pushMessage(s.id, 'notice', [text(msg)], { meta: { kind: 'project', projectId: pr?.id ?? null } });
    publish('message.added', { sessionId: s.id, message: m }, s.id);
    publish('session.updated', { session: s });
    return s;
  },
  'sessions.messages': (p) => {
    const id = need(p, 'id');
    msgLoads.set(id, (msgLoads.get(id) ?? 0) + 1);
    getSession(id);
    let msgs = store.messages.get(id) ?? [];
    if (p.beforeSeq != null) msgs = msgs.filter((m) => m.seq < p.beforeSeq);
    const limit = p.limit ?? 60;
    const page = msgs.slice(Math.max(0, msgs.length - limit));
    return { messages: page, hasMore: msgs.length > page.length };
  },

  'models.list': () => ({ models: MODELS, defaultModel: store.settings.defaultModel ?? DEFAULT_MODEL }),
  'ui.tabs': () => uiTabs(),
  'ui.commands': () => [
    { name: 'compact', description: 'Summarize older messages to free context', rpc: 'compaction.run', pluginId: 'netpi.compaction' },
    { name: 'idea', description: 'Add an idea to the backlog', rpc: 'ideas.quickAdd', argsHint: '<title>', pluginId: 'netpi.ideas' },
    { name: 'reload', description: 'Hot-reload a plugin (or all plugins)', rpc: 'diag.reload', argsHint: '[pluginId]', pluginId: 'netpi.diagnostics' },
    { name: 'sample', description: 'Open the sample plugin tab', clientAction: 'openTab:netpi.sample/sample', pluginId: 'netpi.sample' },
  ],
  'ui.state.get': (p) => store.uiState.get(need(p, 'key')) ?? null,
  'ui.state.set': (p) => {
    store.uiState.set(need(p, 'key'), p.value ?? null);
    return true;
  },

  'plugins.list': () => diag.list(),
  'plugins.reload': (p) => {
    const id = need(p, 'id');
    diag.reload(id);
    publish('plugins.reloaded', { ids: [id], kind: 'reload' });
    return true;
  },
  'plugins.setEnabled': (p) => diag.setEnabled(need(p, 'id'), !!p.enabled),
  'plugins.rescan': () => {
    publish('plugins.changed', {});
    publish('ui.changed', {});
    return true;
  },

  'settings.get': () => ({ path: path.join(os.homedir(), '.netpi', 'settings.json'), settings: store.settings }),
  'settings.set': (p) => {
    const keys = need(p, 'path').split('.');
    let o = store.settings;
    for (const k of keys.slice(0, -1)) o = o[k] ??= {};
    if (p.value == null) delete o[keys.at(-1)]; // null removes the key: the default applies again
    else o[keys.at(-1)] = p.value;
    publish('settings.changed', { path: p.path });
    work.agentsChanged(); // like the host: lanes follow the settings
    if (p.path === 'budget' || p.path.startsWith('budget.')) publish('usage.changed', budgetStatus());
    return true;
  },
  'settings.replace': (p) => {
    const s = need(p, 'settings');
    if (typeof s !== 'object' || Array.isArray(s)) throw new RpcError('bad_request', 'settings must be an object');
    store.settings = s;
    publish('settings.changed', {});
    work.agentsChanged();
    publish('usage.changed', budgetStatus());
    return true;
  },

  'fs.dirs': async (p = {}) => {
    const dir = path.resolve(p.path || os.homedir());
    let entries;
    try {
      entries = await fsp.readdir(dir, { withFileTypes: true });
    } catch (e) {
      throw new RpcError('not_found', `Cannot read ${dir}: ${e.code}`);
    }
    const dirs = entries
      .filter((e) => e.isDirectory() && !e.name.startsWith('.'))
      .map((e) => ({ name: e.name, path: path.join(dir, e.name) }))
      .sort((a, b) => a.name.localeCompare(b.name));
    const parent = path.dirname(dir) === dir ? null : path.dirname(dir);
    return { path: dir, parent, dirs, roots: process.platform === 'win32' ? ['C:\\', 'D:\\'] : ['/'] };
  },

  'tools.list': () => toolRows(),
  'rpc.list': () => Object.keys(handlers).filter((method) => !missingMethods.has(method)).map((method) => ({ method, description: '', pluginId: 'mock' })),
  'mock.capabilities': (p = {}) => { missingMethods = new Set(p.missing ?? []); publish('rpc.changed', {}); return true; },
  'events.recent': (p = {}) => recent.slice(-(p.max ?? 100)),

  // --- agent plugin
  'agent.send': (p) => {
    const sid = need(p, 'sessionId');
    getSession(sid);
    const first = !(store.messages.get(sid) ?? []).some((m) => m.role === 'user');
    const res = agent.send(sid, p);
    // the first model call freezes the chat's system prompt (the context plugin announces it)
    if (first) setTimeout(() => publish('context.prompt', { sessionId: sid, version: 1 }, sid), 50);
    return res;
  },
  'agent.abort': (p) => agent.abort(need(p, 'sessionId')),

  // --- goal plugin (plugins/NetPI.Goal): the goal lives in session.meta.goal
  'goal.get': (p) => {
    const g = getSession(need(p, 'sessionId')).meta?.goal;
    return g && g.status !== 'cleared' ? g : null;
  },
  'goal.set': (p) => {
    const sid = need(p, 'sessionId');
    const s = getSession(sid);
    const objective = String(p.objective ?? '').trim();
    if (!objective) throw new RpcError('bad_request', 'The goal is empty: describe what should be done.');
    const now = new Date().toISOString();
    const goal = { id: newId('goal'), objective, status: 'active', reason: null, tokenBudget: p.tokenBudget ?? 0, tokensUsed: 0, continuations: 0, noProgress: 0, version: 1, createdAt: now, updatedAt: now };
    s.meta = { ...(s.meta ?? {}), goal };
    if (!s.title || s.title === 'New session') s.title = objective.split('\n')[0].slice(0, 60); // no user message to title it
    publish('session.updated', { session: s });
    if (!agent.isRunning(sid)) agent.runGoal(sid, 'set');
    return goal;
  },
  'goal.edit': (p) => goalChange(p, (g) => {
    if (!['active', 'paused', 'blocked'].includes(g.status)) throw new RpcError('bad_request', 'There is no goal to edit.');
    return p.objective && p.objective !== g.objective ? { objective: String(p.objective).trim(), version: g.version + 1 } : {};
  }),
  'goal.pause': (p) => goalChange(p, (g) => {
    if (g.status !== 'active') throw new RpcError('bad_request', `The goal is ${g.status}, not active.`);
    return { status: 'paused', reason: 'Paused by the user.' };
  }),
  'goal.resume': (p) => {
    const g = goalChange(p, (x) => {
      if (x.status === 'active' && !agent.isRunning(p.sessionId)) return {}; // active but idle: start it again
      if (!['paused', 'blocked'].includes(x.status)) throw new RpcError('bad_request', 'There is no paused goal.');
      return { status: 'active', reason: null, continuations: 0, noProgress: 0 };
    });
    if (!agent.isRunning(p.sessionId)) agent.runGoal(p.sessionId, 'resumed');
    return g;
  },
  'goal.clear': (p) => {
    goalChange(p, () => ({ status: 'cleared' }));
    return null;
  },
  // per-session tool switches (meta.toolsOff), like the agent plugin
  'agent.tools': (p) => sessionTools(getSession(need(p, 'sessionId'))),
  'agent.setTools': (p) => {
    const s = getSession(need(p, 'sessionId'));
    const off = new Map((s.meta?.toolsOff ?? []).map((n) => [n.toLowerCase(), n]));
    for (const n of p.off ?? []) off.set(String(n).toLowerCase(), String(n));
    for (const n of p.on ?? []) off.delete(String(n).toLowerCase());
    s.meta = { ...(s.meta ?? {}) };
    if (off.size) s.meta.toolsOff = [...off.values()].sort();
    else delete s.meta.toolsOff;
    s.updatedAt = new Date().toISOString();
    publish('session.updated', { session: s });
    return sessionTools(s);
  },
  'profiles.list': () => ({ defaultProfile: profileById(store.settings.profiles?.defaultProfile)?.id ?? null, profiles: profilesList() }),
  'profiles.apply': (p) => {
    const s = getSession(need(p, 'sessionId'));
    if (s.kind === 'subagent') throw new RpcError('bad_request', "Subagents don't use profiles: the agent that starts one chooses its tools.");
    const id = p.profile && p.profile !== 'none' ? p.profile : null;
    const profile = id ? profileById(id) : null;
    if (id && !profile) throw notFound('Profile');
    writeProfile(s, profile);
    publish('session.updated', { session: s });
    if ((s.messageCount ?? 0) > 0)
      agent.notice(s.id, profile ? `The user switched this chat to the profile "${profile.name}": your system prompt and tools have changed.` : "The user took this chat's profile away: your system prompt and tools are the default ones now.", { kind: 'profile' });
    return s;
  },
  'agent.queue': (p) => agent.queue(need(p, 'sessionId')),
  'agent.dequeue': (p) => agent.dequeue(need(p, 'sessionId'), need(p, 'id')),
  // like the Work plugin: agents.list and usage.summary; the ideas checks are the same list the Diagnostics tab's
  // Ideas view reads via ideas.work (eleven in run order, oldest first — the view keeps the last 10)
  'ideas.work': () => ideasWorkList(),
  'work.snapshot': () => ({
    agents: agentPools(),
    physicalOwners: [{ resource: 'local:aiproxy/qwen3.8-27b', key: 'qwen', holder: { leaseId: 'mock-retiring', agentId: 'old-run', label: 'Reloaded worker', since: new Date(Date.now() - 30_000).toISOString(), executorGeneration: 'previous', correlationId: 'mock-held-call', purpose: 'agent', retiring: true, cancellationRequestedAt: new Date().toISOString(), providerReturnedAt: null } }],
    resources: [{ key: 'local:aiproxy/qwen3.8-27b', model: 'aiproxy/qwen3.8-27b', capacity: 2, busy: 2, queued: 1, available: true, owners: [] }],
    ideasWork: ideasWorkList(),
    runs: [...store.agents.values()],
    processes: work.procList(),
    usage: { ...work.usageSummary(), budget: budgetStatus(), models: MOCK_SPEND },
    time: new Date().toISOString(),
  }),
  'runs.list': (p = {}) =>
    [...store.agents.values()].filter((a) => p.includeFinished || !['completed', 'failed', 'cancelled'].includes(a.status)),
  'agent.get': (p = {}) => (p.sessionId ? (store.agents.get(p.sessionId) ?? null) : ([...store.agents.values()].find((a) => a.id === p.id) ?? null)),

  // --- misc plugins
  'compaction.run': (p) => {
    const sid = need(p, 'sessionId');
    const msgs = store.messages.get(sid) ?? [];
    if (msgs.length < 4) return 'Nothing to compact yet';
    const upTo = msgs[msgs.length - 3].seq;
    let n = 0;
    for (const m of msgs) if (m.seq <= upTo && !m.compacted) (m.compacted = true), n++;
    publish('messages.compacted', { sessionId: sid, upToSeq: upTo }, sid);
    const m = pushMessage(sid, 'summary', [text(`## Summary\n\n${n} earlier messages were summarized. Key points: the agent scheduler now fails with a clear error for unknown pools; tests are green.`)], { meta: { kind: 'compaction', upToSeq: upTo } });
    publish('message.added', { sessionId: sid, message: m }, sid);
    return `Compacted ${n} messages`;
  },
  ...ideas.api,
  'diag.snapshot': (p = {}) => ({
    plugins: diag.list(),
    tools: toolRows(),
    rpc: Object.keys(handlers)
      .filter((m) => !m.startsWith('mock.'))
      .sort()
      .map((method) => ({ method, description: RPC_DOCS[method] ?? '', pluginId: method.split('.')[0] === 'ideas' ? 'netpi.ideas' : method.startsWith('diag') ? 'netpi.diagnostics' : method.startsWith('work') ? 'netpi.work' : 'host' })),
    events: recent.slice(-(p.events ?? 200)).map((e) => ({ seq: e.seq, type: e.type, sessionId: e.sid ?? undefined, time: new Date(e.ts).toISOString(), source: e.source })),
    logs: logs.slice(-200),
    runtime: diag.runtime(),
    time: new Date().toISOString(),
  }),
  // the inspection RPCs (docs/DEBUGGING.md), with sample data: a running call, a finished one, a failed one
  'diag.problems': () => [
    { severity: 'warn', area: 'agents', message: 'reviewer (agt_r) has waited 2 min for qwen (2/2 busy: surveyor, Index docs for semantic search).', hint: 'diag.run { agentId } of the holders: are they stuck?' },
    { severity: 'info', area: 'agents', message: "Agent gemma is inactive: gemma-4 isn't loaded." },
    { severity: 'info', area: 'plugins', message: 'Plugins reloaded: netpi.tools.shell 3 min ago, 2 chat(s) mid-turn lost their tools and got a notice.', hint: 'diag.toolsets { sessionId } of a chat that lost tools' },
  ],
  // a chat's tools now and every change with its cause (the context plugin keeps the record; the mock tells a story)
  'diag.toolsets': (p) => {
    getSession(need(p, 'sessionId'));
    const ago = 3 * 60_000;
    return {
      sessionId: p.sessionId,
      tools: toolRows().filter((t) => t.active && !t.disabled).map((t) => t.name),
      baseline: { tools: toolRows().filter((t) => t.active && !t.disabled).map((t) => t.name), sinceSeq: 4 },
      changes: [
        {
          seq: 9,
          time: new Date(Date.now() - ago).toISOString(),
          added: [],
          removed: ['bash', 'pwsh'],
          cause: 'plugin-reload',
          plugins: ['netpi.tools.shell'],
          text: 'Your tools changed. No longer available: bash, pwsh (plugin reload netpi.tools.shell).',
        },
      ],
      reloads: [{ ids: ['netpi.tools.shell'], time: new Date(Date.now() - ago).toISOString(), kind: 'reload' }],
    };
  },
  'diag.calls': async (p = {}) => {
    diagCallsInFlight++;
    diagCallsMaxInFlight = Math.max(diagCallsMaxInFlight, diagCallsInFlight);
    try {
      if (diagCallsDelayMs) await new Promise((r) => setTimeout(r, diagCallsDelayMs));
      diagCallsServed++;
      return mockCalls().filter((c) => (!p.errors || c.state === 'error') && (!p.running || c.state === 'running'));
    } finally {
      diagCallsInFlight--;
    }
  },
  'diag.call': (p) => {
    if (p?.correlationId === 'mock-held-call') return { id: 100, correlationId: p.correlationId, model: 'aiproxy/qwen3.8-27b', state: 'running', durationMs: 30000 };
    const c = mockCalls().find((x) => x.id === Number(p?.id));
    if (!c) throw new RpcError('not_found', `Call ${p?.id} is no longer in the call log`);
    return {
      ...c,
      reasoningEffort: 'medium',
      request: { messages: 42, tools: 21, systemPromptChars: 9400, inputChars: 180000, lastUser: 'Run the tests and fix whatever fails.' },
      response: { textChars: 1810, thinkingChars: 2600, toolCalls: ['bash'] },
      resets: c.attempts > 1 ? ['8400 ms: connection lost'] : [],
      notices: [],
      errorDetail: c.error ? { type: 'server_error', status: 503, transient: true } : undefined,
    };
  },
  'diag.event': (p) => {
    const e = recent.find((x) => x.seq === Number(p?.seq));
    if (!e) throw new RpcError('not_found', `Event ${p?.seq} is no longer in the buffer`);
    return { seq: e.seq, type: e.type, sessionId: e.sid ?? undefined, time: new Date(e.ts).toISOString(), source: e.source, ui: true, data: e.d };
  },
  'diag.reload': (p = {}) => {
    const id = (p.args ?? p.id ?? '').trim();
    if (!id || id === 'all' || id === '*') {
      for (const x of diag.list()) if (x.enabled) setTimeout(() => diag.reload(x.id), 50);
      return `Reloading ${diag.list().filter((x) => x.enabled).length} plugins…`;
    }
    const pl = diag.reload(id);
    publish('plugins.reloaded', { ids: [id], kind: 'reload' });
    return `Reloaded ${pl.name} (${pl.id})`;
  },
  'files.open': (p = {}) => {
    const rel = need(p, 'path');
    const s = p.sessionId ? store.sessions.get(p.sessionId) : null;
    const pr = s?.projectId ? store.projects.get(s.projectId) : null;
    const full = path.isAbsolute(rel) ? rel : path.join(pr?.path ?? REPO, rel.replace(/:\d+(:\d+)?$/, ''));
    filesOpened.push(full);
    return { path: full, action: 'open' };
  },
  'mock.filesOpened': () => filesOpened,
  // test helper: the chat whose user turns contain this phrase leaves the given plan when its tab is closed
  'mock.closeLeavesPlan': (p = {}) => ideas.closeLeavesPlan(need(p, 'phrase'), p.title),
  // test helper: offer the commit check's card for the idea whose title contains this phrase
  'mock.commitFinishesIdea': (p = {}) => ideas.commitFinishesIdea(need(p, 'phrase')),
  // test helpers: slow the process tails and observe who asks for them (the e2e leak check)
  'mock.procTailDelay': (p) => ((procTailDelayMs = p.ms ?? 0), true),
  'mock.procStats': () => work.procStats(),
  // test helpers for the Work tab's fixed slots: a job ends (the first waiter takes its slot) / a job starts on a pool
  'mock.workRelease': (p = {}) => work.releaseOwner(need(p, 'sessionId')),
  'mock.workTake': (p = {}) => work.takeSlot(need(p, 'pool'), need(p, 'owner'), !!p.waiting),
  // e2e test helpers: slow the files.* responses and observe the served order (the workspace-switch checks)
  'mock.filesDelay': (p) => ((filesDelayMs = p.ms ?? 0), true),
  'mock.filesCalls': () => filesCalls,
  // e2e test helpers: slow the diag.calls responses and observe the in-flight count (the single-flight polling
  // check). The delay setter also resets the counters: a check times its window from that call.
  'mock.diagCallsDelay': (p) => ((diagCallsDelayMs = p.ms ?? 0), (diagCallsServed = 0), (diagCallsMaxInFlight = 0), true),
  // e2e test helper: stretch the agent's thinking phase so its live line is observable (0 restores normal speed)
  'mock.thinkDelay': (p) => ((thinkDelayMs = p.ms ?? 0), agent.setThinkDelay(thinkDelayMs), true),
  // e2e forensics: the last rpc frames received, and the connected clients with their subscriptions
  'mock.wsLog': () => ({ frames: wsLog, clients: [...clients].map((c) => ({ id: c.id, open: c.ws.readyState === 1, subs: [...c.subs].length })) }),
  'mock.diagCallsStats': () => ({ served: diagCallsServed, inFlight: diagCallsInFlight, maxInFlight: diagCallsMaxInFlight }),
  // e2e test helper: how many times each session's messages were loaded (a rebuilt chat store)
  'mock.msgLoads': () => Object.fromEntries(msgLoads),
  // e2e test helpers: a wall of recent actives behind one very old archive (the archived-filter check);
  // the seeded ids live only in the store — mock.reset drops them with everything else
  'mock.seedMany': (p = {}) => seedMany({ active: p.active ?? 0, archived: p.archived ?? 0, title: p.title ?? null }),
  'mock.clearMany': () => ((clearBulk()), true),
  // e2e test helper: the sessions.list calls made so far, with their params (was the archived section asked for?)
  'mock.listCalls': () => listCalls,
  'guard.pending': (p = {}) => agent.pendingApprovals(p.sessionId),
  'guard.answer': (p = {}) => {
    if (agent.answerApproval(need(p, 'approvalId'), p.allow, p.scope) === 'not_found') throw new RpcError('not_found', 'No tool call waits for your OK with that id.');
    return true;
  },
  'ask.pending': (p = {}) => agent.pendingAsks(p.sessionId),
  'ask.answer': (p = {}) => {
    const r = agent.answerAsk(p.id ?? null, p.callId, p.answers, p.text, p.sessionId);
    if (r === 'not_found') throw new RpcError('not_found', 'No question waits with that id (it was answered, or its run ended).');
    if (r === 'empty') throw new RpcError('bad_request', 'An answer needs an option picked or some text.');
    return true;
  },
  'mock.queueInternal': (p) => (agent.queueInternal(need(p, 'sessionId')), true),
  // e2e test helper: drop the WebSocket link for `ms` (the server keeps running: runs finish and persist while the
  // browser is disconnected); mock.online ends it early
  'mock.offline': (p) => {
    offlineUntil = Date.now() + Number(p?.ms ?? 0);
    for (const c of [...clients]) {
      clients.delete(c);
      try {
        c.ws.terminate();
      } catch {}
    }
    return true;
  },
  'mock.online': () => ((offlineUntil = 0), true),
  'agentsmd.list': (p = {}) => {
    const s = p.sessionId ? store.sessions.get(p.sessionId) : null;
    const pr = p.projectId ? store.projects.get(p.projectId) : s?.projectId ? store.projects.get(s.projectId) : null;
    if (p.projectId && !pr) throw notFound('Project');
    const out = [{ path: path.join(os.homedir(), '.netpi', 'AGENTS.md'), bytes: 1843, scope: 'global' }];
    if (pr) out.push({ path: path.join(pr.path, 'AGENTS.md'), bytes: 4210, scope: 'project' }, { path: path.join(pr.path, 'web', 'AGENTS.md'), bytes: 612, scope: 'directory' });
    return out;
  },
  'skills.list': (p = {}) => {
    const s = p.sessionId ? store.sessions.get(p.sessionId) : null;
    const pr = p.projectId ? store.projects.get(p.projectId) : s?.projectId ? store.projects.get(s.projectId) : null;
    if (p.projectId && !pr) throw notFound('Project');
    const at = (root, name) => path.join(root, name, 'SKILL.md');
    const home = path.join(os.homedir(), '.agents', 'skills');
    const skills = [
      { name: 'release-notes', description: 'Write release notes from the git log since the last tag. Use when preparing a release.', path: at(home, 'release-notes'), scope: 'global', listed: true, userOnly: false, disabled: false },
      { name: 'handoff', description: 'Write a handoff for the next agent.', path: at(home, 'handoff'), scope: 'global', listed: false, userOnly: true, disabled: false },
    ];
    const problems = [];
    if (pr) {
      const proj = path.join(pr.path, '.agents', 'skills');
      skills.unshift({ name: 'svelte-tab', description: 'Build a plugin tab for NetPI with the tab kit. Use when adding UI to a plugin.', path: at(proj, 'svelte-tab'), scope: 'project', listed: true, userOnly: false, disabled: false });
      problems.push({ path: at(proj, 'draft'), level: 'error', message: 'No description: skipped (agents choose a skill by its description).' });
    }
    return { skills, problems };
  },
  'context.preview': (p) => {
    const s = store.sessions.get(p?.sessionId);
    const pr = s?.projectId ? store.projects.get(s.projectId) : null;
    const systemPrompt = SYSTEM_PROMPT(pr, s);
    const tools = toolRows().filter((t) => t.active && !t.disabled).map((t) => ({ name: t.name, description: t.description }));
    return { systemPrompt, tools, estimatedTokens: Math.round(systemPrompt.length / 3.6) + tools.length * 140 + (s?.contextTokens ?? 0) };
  },
  // the prompt a chat was sent: once it has a user message, version 1 after it (the mock never renders it again)
  'context.prompts': (p) => {
    const s = getSession(need(p, 'sessionId'));
    const firstUser = (store.messages.get(s.id) ?? []).find((m) => m.role === 'user');
    if (!firstUser) return { prompts: [] };
    const pr = s.projectId ? store.projects.get(s.projectId) : null;
    const tools = toolRows()
      .filter((t) => t.active && !t.disabled)
      .map((t) => ({ name: t.name, description: t.description, parameters: { type: 'object', properties: {} } }))
      .sort((a, b) => (a.name < b.name ? -1 : 1));
    return { prompts: [{ version: 1, afterSeq: firstUser.seq, createdAt: firstUser.createdAt, systemPrompt: SYSTEM_PROMPT(pr, s), tools }] };
  },
  'files.search': async (p = {}) => {
    const root = p.cwd || sessionCwd(p.sessionId);
    if (filesDelayMs) await new Promise((r) => setTimeout(r, filesDelayMs));
    const files = await listFiles(root);
    const q = (p.query ?? '').toLowerCase().replace(/\\/g, '/');
    const out = files
      .map((f) => ({ f, r: rankFile(f, q) }))
      .filter((x) => x.r !== Infinity)
      .sort((a, b) => a.r - b.r)
      .slice(0, Math.min(p.limit ?? 50, 500))
      .map((x) => x.f);
    filesCalls.push({ m: 'files.search', root });
    return out;
  },
  'files.list': async (p = {}) => {
    const root = p.cwd || sessionCwd(p.sessionId);
    if (filesDelayMs) await new Promise((r) => setTimeout(r, filesDelayMs));
    const dir = path.join(root, p.dir ?? '');
    let entries;
    try {
      entries = await fsp.readdir(dir, { withFileTypes: true });
    } catch (e) {
      throw new RpcError('not_found', `Cannot list ${dir}: ${e.code}`);
    }
    const IGN = new Set(['node_modules', 'bin', 'obj', '.vs', '.idea', 'dist', 'build', 'artifacts', '__pycache__', '.venv', 'wwwroot']);
    const out = [];
    for (const e of entries) {
      if (e.name === '.git') continue;
      let st = null;
      try {
        st = await fsp.stat(path.join(dir, e.name));
      } catch {}
      const isDir = e.isDirectory();
      out.push({
        name: e.name,
        rel: path.posix.join(p.dir ?? '', e.name),
        isDir,
        ...(isDir ? {} : { size: st?.size ?? 0 }),
        ...(st ? { mtime: st.mtime.toISOString() } : {}),
        ...(IGN.has(e.name) ? { ignored: true } : {}),
      });
    }
    filesCalls.push({ m: 'files.list', root });
    return { root, dir: p.dir ?? '', entries: out.sort((a, b) => b.isDir - a.isDir || a.name.localeCompare(b.name)) };
  },
  // scripted: the same uncommitted changes in every workspace (a binary file has no line counts)
  'files.git': async (p = {}) => {
    const root = p.cwd || sessionCwd(p.sessionId);
    if (filesDelayMs) await new Promise((r) => setTimeout(r, filesDelayMs));
    const files = [
      { rel: 'web/src/lib/markdown.js', status: 'modified', added: 12, deleted: 3 },
      { rel: 'web/src/lib/notify.js', status: 'new', added: 48, deleted: 0 },
      { rel: 'docs/OLD-NOTES.md', status: 'deleted', added: 0, deleted: 397 },
      { rel: 'web/public/logo.png', status: 'modified' },
    ].map((f) => ({ path: path.join(root, f.rel), ...f }));
    const sum = (k) => files.reduce((n, f) => n + (f[k] ?? 0), 0);
    filesCalls.push({ m: 'files.git', root });
    return { repo: root, branch: 'main', ahead: 2, behind: 0, files, added: sum('added'), deleted: sum('deleted') };
  },
  'processes.list': () => work.procList(),
  'processes.output': async (p) => {
    if (procTailDelayMs) await new Promise((r) => setTimeout(r, procTailDelayMs));
    return work.procOutputTail(need(p, 'id'), p.tail ?? 500);
  },
  'processes.kill': (p) => work.procKill(need(p, 'id')),
  'agents.list': () => agentPools(),
  'agents.use': (p) => {
    const s = getSession(need(p, 'sessionId'));
    const id = p.agent || null;
    if (id) {
      const a = agentsInSettings().find(([k]) => k === id)?.[1];
      if (!a) throw new RpcError('not_found', `There is no agent "${id}"`);
      s.meta = { ...(s.meta ?? {}), agent: id };
      s.model = a.model;
    } else if (s.meta) delete s.meta.agent;
    s.updatedAt = new Date().toISOString();
    publish('session.updated', { session: s });
    return s;
  },
  'agents.setEnabled': (p) => {
    const id = need(p, 'id');
    const a = agentsInSettings().find(([k]) => k === id)?.[1];
    if (!a) throw new RpcError('not_found', `There is no agent "${id}"`);
    if (p.enabled) delete a.disabled;
    else a.disabled = true;
    publish('settings.changed', {});
    work.agentsChanged();
    return agentPools();
  },
  'logs.recent': (p = {}) => logs.slice(-(p.max ?? 200)),
  // test helper: back to the seeded state
  'mock.reset': () => {
    procTailDelayMs = 0;
    filesDelayMs = 0;
    thinkDelayMs = 0;
    agent.setThinkDelay(0);
    offlineUntil = 0;
    filesCalls = [];
    listCalls = [];
    diagCallsDelayMs = 0;
    diagCallsInFlight = 0;
    diagCallsMaxInFlight = 0;
    diagCallsServed = 0;
    msgLoads.clear();
    for (const s of store.sessions.keys()) agent.abort(s);
    resetStore();
    seed();
    work.start();
    ideas.seed();
    diag.seed();
    publish('plugins.changed', {});
    return true;
  },
  'usage.summary': () => ({ ...work.usageSummary(), budget: budgetStatus(), models: MOCK_SPEND }),
  // one seeded chat used a paid model (with a subagent)
  'usage.session': (p) => {
    const s = getSession(need(p, 'sessionId'));
    const paid = s.title === 'Lane scheduler hardening';
    return { sessionId: s.id, costUsd: paid ? 0.42 : 0, calls: paid ? 7 : 0, withSubagentsUsd: paid ? 0.675 : 0, withSubagentsCalls: paid ? 12 : 0 };
  },
  'budget.status': () => budgetStatus(),
  'budget.allow': (p) => {
    const s = getSession(need(p, 'sessionId'));
    s.meta = { ...(s.meta ?? {}), budgetAllowedFrom: budgetStatus().periodStart };
    publish('session.updated', { session: s });
    agent.notice(s.id, 'The user let this chat go over the budget until the budget period ends. Continue where you stopped.', { kind: 'budget' });
    return budgetStatus();
  },
  'settings.schema': () => SETTINGS_SCHEMA,
};

async function dispatch(m, p) {
  const h = handlers[m];
  if (!h || missingMethods.has(m)) throw new RpcError('method_not_found', `Unknown method: ${m}`);
  return await h(p ?? {});
}

// ------------------------------------------------------------------------------------------ http
const TYPES = {
  '.html': 'text/html; charset=utf-8',
  '.js': 'text/javascript; charset=utf-8',
  '.mjs': 'text/javascript; charset=utf-8',
  '.css': 'text/css; charset=utf-8',
  '.json': 'application/json',
  '.svg': 'image/svg+xml',
  '.png': 'image/png',
  '.woff2': 'font/woff2',
  '.map': 'application/json',
  '.ico': 'image/x-icon',
};

function cookies(req) {
  const out = {};
  for (const part of (req.headers.cookie ?? '').split(';')) {
    const i = part.indexOf('=');
    if (i > 0) out[part.slice(0, i).trim()] = decodeURIComponent(part.slice(i + 1).trim());
  }
  return out;
}
function authorized(req, url) {
  if (NO_AUTH) return true;
  return cookies(req).netpi_token === TOKEN || req.headers['x-netpi-token'] === TOKEN || url.searchParams.get('token') === TOKEN;
}

async function sendFile(res, file, cache) {
  try {
    const st = await fsp.stat(file);
    if (!st.isFile()) throw new Error('not a file');
    res.writeHead(200, {
      'Content-Type': TYPES[path.extname(file)] ?? 'application/octet-stream',
      'Content-Length': st.size,
      'Cache-Control': cache,
    });
    fs.createReadStream(file).pipe(res);
    return true;
  } catch {
    return false;
  }
}

function safeJoin(root, rel) {
  const p = path.normalize(path.join(root, rel));
  return p.startsWith(root) ? p : null;
}

const server = http.createServer(async (req, res) => {
  const url = new URL(req.url, `http://${req.headers.host}`);
  const p = decodeURIComponent(url.pathname);

  if (p === '/' && url.searchParams.get('token')) {
    if (url.searchParams.get('token') !== TOKEN && !NO_AUTH) {
      res.writeHead(401).end('bad token');
      return;
    }
    res.writeHead(302, { 'Set-Cookie': `netpi_token=${TOKEN}; HttpOnly; SameSite=Strict; Path=/`, Location: '/' }).end();
    return;
  }

  if (p.startsWith('/api/rpc/') && req.method === 'POST') {
    if (!authorized(req, url)) return void res.writeHead(401, { 'Content-Type': 'application/json' }).end(JSON.stringify({ error: { code: 'unauthorized', message: 'Missing token' } }));
    let body = '';
    for await (const chunk of req) body += chunk;
    try {
      const r = await dispatch(p.slice('/api/rpc/'.length), body ? JSON.parse(body) : {});
      res.writeHead(200, { 'Content-Type': 'application/json' }).end(JSON.stringify(r ?? null));
    } catch (e) {
      const code = e.code ?? 'error';
      res.writeHead(code === 'not_found' || code === 'method_not_found' ? 404 : 400, { 'Content-Type': 'application/json' }).end(JSON.stringify({ error: { code, message: e.message } }));
    }
    return;
  }

  if (p.startsWith('/plugins/')) {
    if (!authorized(req, url)) return void res.writeHead(401).end('unauthorized');
    const [, , id, ...rest] = p.split('/');
    const root = PLUGINS[id];
    const file = root && safeJoin(root, rest.join('/'));
    if (file && (await sendFile(res, file, 'no-cache'))) return;
    res.writeHead(404).end('not found');
    return;
  }

  const file = safeJoin(WWWROOT, p === '/' ? 'index.html' : p);
  if (file && p !== '/' && (await sendFile(res, file, p.startsWith('/assets/') ? 'public, max-age=31536000, immutable' : 'no-cache'))) return;
  // SPA fallback
  if (await sendFile(res, path.join(WWWROOT, 'index.html'), 'no-cache')) return;
  res.writeHead(503, { 'Content-Type': 'text/plain' }).end('UI not built: run `npm run build` first.');
});

// ------------------------------------------------------------------------------------------ websocket
const wss = new WebSocketServer({ noServer: true });
server.on('upgrade', (req, socket, head) => {
  const url = new URL(req.url, `http://${req.headers.host}`);
  if (url.pathname !== '/ws') return socket.destroy();
  if (Date.now() < offlineUntil) return socket.destroy(); // e2e: the browser is offline
  const origin = req.headers.origin;
  if (origin) {
    const o = new URL(origin);
    const okHost = ['127.0.0.1', 'localhost', '[::1]'].includes(o.hostname);
    if (!okHost) {
      socket.write('HTTP/1.1 403 Forbidden\r\n\r\n');
      return socket.destroy();
    }
  }
  if (!authorized(req, url)) {
    socket.write('HTTP/1.1 401 Unauthorized\r\n\r\n');
    return socket.destroy();
  }
  wss.handleUpgrade(req, socket, head, (ws) => wss.emit('connection', ws, req));
});

wss.on('connection', (ws) => {
  const client = { ws, subs: new Set(), id: newId('c') };
  clients.add(client);
  ws.send(JSON.stringify({ t: 'hello', clientId: client.id, version: VERSION }));
  ws.on('message', async (raw) => {
    let msg;
    try {
      msg = JSON.parse(raw.toString());
    } catch {
      return;
    }
    if (msg.t === 'ping') return ws.send('{"t":"pong"}');
    if (msg.t === 'sub') {
      client.subs = new Set(Array.isArray(msg.sessions) ? msg.sessions : []);
      return;
    }
    if (msg.t === 'rpc') {
      wsLog.push({ at: new Date().toISOString().slice(11, 23), client: client.id, id: msg.id, m: msg.m });
      if (wsLog.length > 120) wsLog.shift();
      try {
        const r = await dispatch(msg.m, msg.p);
        ws.send(JSON.stringify({ t: 'res', id: msg.id, r: r ?? null }));
      } catch (e) {
        if (!e.code) console.error(`[mock] ${msg.m} failed`, e);
        ws.send(JSON.stringify({ t: 'res', id: msg.id, e: { code: e.code ?? 'error', message: e.message } }));
      }
    }
  });
  ws.on('close', () => clients.delete(client));
});

// hot reload of plugin UI bundles (like the host: a changed wwwroot bumps the tab version)
for (const p of PLUGIN_UIS) fs.watchFile(path.join(PLUGINS[p.pluginId], 'ui.js'), { interval: 500 }, () => publish('ui.changed', {}));

server.listen(PORT, HOST, () => {
  const url = `http://${HOST}:${PORT}/${NO_AUTH ? '' : `?token=${TOKEN}`}`;
  console.log(`NetPI mock server on ${url}`);
  if (!fs.existsSync(path.join(WWWROOT, 'index.html'))) console.log('  (UI not built yet — run `npm run build`)');
});
