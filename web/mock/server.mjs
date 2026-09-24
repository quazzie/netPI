#!/usr/bin/env node
// NetPI mock server: serves the built UI (artifacts/app/wwwroot) and plugin UI bundles, and implements the
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
import { store, seed, resetStore, MODELS, DEFAULT_MODEL, REPO, mkSession, pushMessage, agentFor, newId, text } from './store.mjs';
import { createAgentRuntime } from './agent.mjs';
import { createWork } from './work.mjs';
import { createIdeas } from './ideas.mjs';
import { createDiag, toolRows } from './diag.mjs';

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
// the app's copy when it has been built, else the committed build output
const WWWROOT = [path.join(REPO, 'artifacts/app/wwwroot'), path.join(REPO, 'web/dist')].find((d) => fs.existsSync(path.join(d, 'index.html'))) ?? path.join(REPO, 'web/dist');
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

const work = createWork({ publish, log });
work.start();
const ideas = createIdeas({ publish });
ideas.seed();
const diag = createDiag({ publish, log });
diag.seed();
const agent = createAgentRuntime({ publish, work, log });

// ------------------------------------------------------------------------------------------ rpc
const RPC_DOCS = {
  'agent.send': 'Send a message to a session\'s agent: { sessionId, text, images?, mode? } → AgentInfo',
  'agent.abort': 'Abort the current run of a session\'s agent: { sessionId } → bool',
  'sessions.list': 'Sessions, newest first: { projectId?, search?, includeSubagents?, includeArchived?, limit?, offset? }',
  'sessions.messages': 'Message page: { id, beforeSeq?, limit? (60) } → { messages, hasMore }',
  'work.snapshot': 'Aggregated overview for the Work tab → { lanes, agents, processes, usage, time, errors? }',
  'diag.snapshot': 'Diagnostics overview → { plugins, tools, rpc, events, logs, runtime, time }',
  'ideas.list': 'Ideas of a project/session: { sessionId?, projectId? } → { file, scope, ideas, … }',
  'files.list': 'List one directory for the file tree: { sessionId?, cwd?, dir? } → { root, dir, entries }',
  'files.search': 'Fuzzy file-name search for @ mentions: { sessionId?, query, limit? } → { path, rel, isDir }[]',
  'logs.recent': 'Recent log entries: { max? } → { time, level, category, message, exception? }[]',
};
const SYSTEM_PROMPT = (project, session) => `You are a coding agent running in NetPI, an agent harness on the user's own machine. Work through the tools you have: act rather than describe, check the results and verify your work when practical. Ask only when a request is genuinely ambiguous or an action would be destructive. Be concise, and end with a short summary of what you did or found.

# Environment
- OS: ${os.type()} ${os.release()}
- Messages in <system-notice> tags come from NetPI, not from the user: your working directory and project (relative paths resolve against the latest one), instruction files, subagent reports, reminders and errors.

# Tools
- Prefer read/grep/find/ls over shell commands for exploring files.
- Use edit for small changes; include enough context for oldText to match exactly once.
- Run the relevant build or tests before you say you're done.
- When research or plans are deferred, record them with idea_add instead of losing them.`;

class RpcError extends Error {
  constructor(code, message) {
    super(message);
    this.code = code;
  }
}
const notFound = (what) => new RpcError('not_found', `${what} not found`);
const need = (p, k) => {
  if (p?.[k] == null || p[k] === '') throw new RpcError('bad_request', `Missing parameter: ${k}`);
  return p[k];
};
function getSession(id) {
  const s = store.sessions.get(id);
  if (!s) throw notFound(`Session ${id}`);
  return s;
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
    let list = [...store.sessions.values()].filter((s) => {
      if (!p.includeArchived && s.archived) return false;
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
    if (s.projectId) {
      const pr = store.projects.get(s.projectId);
      if (pr) pr.lastUsedAt = new Date().toISOString();
    }
    publish('session.created', { session: s });
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
    diag.reload(need(p, 'id'));
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
    o[keys.at(-1)] = p.value;
    publish('settings.changed', {});
    return true;
  },
  'settings.replace': (p) => {
    const s = need(p, 'settings');
    if (typeof s !== 'object' || Array.isArray(s)) throw new RpcError('bad_request', 'settings must be an object');
    store.settings = s;
    publish('settings.changed', {});
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
  'rpc.list': () => Object.keys(handlers).map((method) => ({ method, description: '', pluginId: 'mock' })),
  'events.recent': (p = {}) => recent.slice(-(p.max ?? 100)),

  // --- agent plugin
  'agent.send': (p) => {
    const sid = need(p, 'sessionId');
    getSession(sid);
    return agent.send(sid, p);
  },
  'agent.abort': (p) => agent.abort(need(p, 'sessionId')),
  'agent.queue': (p) => agent.queue(need(p, 'sessionId')),
  'agent.dequeue': (p) => agent.dequeue(need(p, 'sessionId'), need(p, 'id')),
  'work.snapshot': () => ({
    lanes: work.lanes(),
    agents: [...store.agents.values()],
    processes: work.procList(),
    usage: work.usageSummary(),
    time: new Date().toISOString(),
  }),
  'agents.list': (p = {}) =>
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
    const m = pushMessage(sid, 'summary', [text(`## Summary\n\n${n} earlier messages were summarized. Key points: the lane scheduler now fails with a clear error for unknown pools; tests are green.`)], { meta: { kind: 'compaction', upToSeq: upTo } });
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
    return `Reloaded ${pl.name} (${pl.id})`;
  },
  'agentsmd.list': (p = {}) => {
    const s = p.sessionId ? store.sessions.get(p.sessionId) : null;
    const pr = p.projectId ? store.projects.get(p.projectId) : s?.projectId ? store.projects.get(s.projectId) : null;
    if (p.projectId && !pr) throw notFound('Project');
    const out = [{ path: path.join(os.homedir(), '.netpi', 'AGENTS.md'), bytes: 1843, scope: 'global' }];
    if (pr) out.push({ path: path.join(pr.path, 'AGENTS.md'), bytes: 4210, scope: 'project' }, { path: path.join(pr.path, 'web', 'AGENTS.md'), bytes: 612, scope: 'directory' });
    return out;
  },
  'context.preview': (p) => {
    const s = store.sessions.get(p?.sessionId);
    const pr = s?.projectId ? store.projects.get(s.projectId) : null;
    const systemPrompt = SYSTEM_PROMPT(pr, s);
    const tools = toolRows().filter((t) => t.active && !t.disabled).map((t) => ({ name: t.name, description: t.description }));
    return { systemPrompt, tools, estimatedTokens: Math.round(systemPrompt.length / 3.6) + tools.length * 140 + (s?.contextTokens ?? 0) };
  },
  'files.search': async (p = {}) => {
    const root = p.cwd || sessionCwd(p.sessionId);
    const files = await listFiles(root);
    const q = (p.query ?? '').toLowerCase().replace(/\\/g, '/');
    return files
      .map((f) => ({ f, r: rankFile(f, q) }))
      .filter((x) => x.r !== Infinity)
      .sort((a, b) => a.r - b.r)
      .slice(0, Math.min(p.limit ?? 50, 500))
      .map((x) => x.f);
  },
  'files.list': async (p = {}) => {
    const root = p.cwd || sessionCwd(p.sessionId);
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
    return { root, dir: p.dir ?? '', entries: out.sort((a, b) => b.isDir - a.isDir || a.name.localeCompare(b.name)) };
  },
  'processes.list': () => work.procList(),
  'processes.output': (p) => work.procOutputTail(need(p, 'id'), p.tail ?? 500),
  'processes.kill': (p) => work.procKill(need(p, 'id')),
  'lanes.list': () => work.lanes(),
  'logs.recent': (p = {}) => logs.slice(-(p.max ?? 200)),
  // test helper: back to the seeded state
  'mock.reset': () => {
    for (const s of store.sessions.keys()) agent.abort(s);
    resetStore();
    seed();
    work.start();
    ideas.seed();
    diag.seed();
    publish('plugins.changed', {});
    return true;
  },
  'usage.summary': () => work.usageSummary(),
};

async function dispatch(m, p) {
  const h = handlers[m];
  if (!h) throw new RpcError('method_not_found', `Unknown method: ${m}`);
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
