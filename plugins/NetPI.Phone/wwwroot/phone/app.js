// NetPI phone page: plain JS modules, no build step. Talks to the plugin's own listener only (/api/*).
// The event stream is read with fetch(), not EventSource: EventSource gives up for good on one 502 (NetPI restarting
// behind the proxy) and cannot tell a dead socket from a quiet one. A watchdog wants a heartbeat; every (re)connect
// re-reads what the page shows, and an open chat catches up the answer streaming in (agent.stream).
import {
  SseParser, backoff, httpProblem, mergeCatchUp, mergeMessages, lastSeq, settlePending, buildItems, withLive, newLive,
  liveApply, toolsApply, parseArgs, callSubject, mdBlocks, mdInline, ago, duration, tokens, activityText, isActive,
} from './lib.js';

const QUIET_MS = 25_000;   // the listener says something every 10 s; this long without a byte and the socket is dead
const $app = document.getElementById('app');
const store = {
  get: (k, d) => { try { return localStorage.getItem(k) ?? d; } catch { return d; } },
  set: (k, v) => { try { if (v == null || v === '') localStorage.removeItem(k); else localStorage.setItem(k, v); } catch { /* private mode */ } },
};
const S = {
  device: null, view: 'boot', sessions: [], projects: [], models: [], runs: new Map(), asks: [], guards: [], waits: [],
  work: null, workError: '', listError: '', listLoaded: false, search: '', limit: 60, build: null, updated: false,
};
let chat = null;   // the open chat (Chat), null elsewhere

// ---------------------------------------------------------------- DOM helpers
function el(tag, attrs, ...kids) {
  const n = document.createElement(tag);
  for (const [k, v] of Object.entries(attrs || {})) {
    if (v == null || v === false) continue;
    if (k === 'class') n.className = v;
    else if (k.startsWith('on')) n.addEventListener(k.slice(2), v);
    else if (k === 'value') n.value = v;
    else n.setAttribute(k, v === true ? '' : v);
  }
  for (const c of kids.flat(Infinity)) if (c != null && c !== false) n.append(c.nodeType ? c : document.createTextNode(String(c)));
  return n;
}
const PATHS = {
  back: 'M15 18l-6-6 6-6', send: 'M12 19V5M5 12l7-7 7 7', plus: 'M12 5v14M5 12h14', x: 'M18 6L6 18M6 6l12 12', chev: 'M9 6l6 6-6 6',
  clip: 'M21.4 11.1l-9.2 9.2a6 6 0 01-8.5-8.5l9.2-9.2a4 4 0 015.7 5.7l-9.2 9.2a2 2 0 01-2.8-2.8l8.5-8.5', down: 'M12 5v14M19 12l-7 7-7-7',
  refresh: 'M21 12a9 9 0 11-2.6-6.4M21 3v6h-6', stop: 'M7 7h10v10H7z',
};
function icon(name, size = 20) {
  const NS = 'http://www.w3.org/2000/svg';
  const svg = document.createElementNS(NS, 'svg');
  for (const [k, v] of Object.entries({ viewBox: '0 0 24 24', width: size, height: size, fill: 'none', stroke: 'currentColor', 'stroke-width': 2, 'stroke-linecap': 'round', 'stroke-linejoin': 'round', 'aria-hidden': 'true' })) svg.setAttribute(k, v);
  const p = document.createElementNS(NS, 'path');
  p.setAttribute('d', PATHS[name]);
  svg.append(p);
  return svg;
}
const debounce = (fn, ms) => { let t; return (...a) => { clearTimeout(t); t = setTimeout(() => fn(...a), ms); }; };
function soon(fn) { let f = 0; return () => { if (!f) f = requestAnimationFrame(() => { f = 0; fn(); }); }; }
const clock = (iso) => { const d = new Date(iso); return isNaN(d) ? '' : d.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' }); };
let toastTimer = 0;
function toast(text, tone = 'err') {
  document.getElementById('toast')?.remove();
  clearTimeout(toastTimer);
  const t = el('div', { id: 'toast', class: 'toast tone-' + tone, role: 'status', onclick: () => t.remove() }, text);
  document.body.append(t);
  toastTimer = setTimeout(() => t.remove(), tone === 'err' ? 7000 : 3500);
}
/** A button that needs a second tap within 3 s (Stop, Unpair): a thumb on the wrong spot must not end a run. */
function twoTap(label, armed, onConfirm, cls = 'danger small') {
  const b = el('button', { class: cls }, label);
  let t = 0;
  b.addEventListener('click', (e) => {
    e.stopPropagation();
    if (!t) { b.textContent = armed; b.classList.add('armed'); t = setTimeout(() => { t = 0; b.textContent = label; b.classList.remove('armed'); }, 3000); return; }
    clearTimeout(t); t = 0; b.textContent = label; b.classList.remove('armed');
    onConfirm(b);
  });
  return b;
}

// ---------------------------------------------------------------- requests
class ApiError extends Error {
  constructor(message, extra) { super(message); Object.assign(this, extra); }
}
async function api(path, body, timeout = 20_000) {
  const ctrl = new AbortController();
  const timer = setTimeout(() => ctrl.abort(), timeout);
  let res, j = null;
  try {
    res = await fetch(path, {
      method: body === undefined ? 'GET' : 'POST', headers: body === undefined ? {} : { 'Content-Type': 'application/json' },
      body: body === undefined ? undefined : JSON.stringify(body), credentials: 'same-origin', cache: 'no-store', signal: ctrl.signal,
    });
    try { j = await res.json(); } catch { /* not JSON: a proxy's error page */ }
  } catch {
    conn.suspect();
    throw new ApiError(ctrl.signal.aborted ? `NetPI did not answer within ${timeout / 1000} s` : navigator.onLine === false ? 'This phone is offline' : 'Could not reach NetPI', { network: true, timeout: ctrl.signal.aborted });
  } finally { clearTimeout(timer); }
  if (res.status === 401 && path !== '/api/pair') { unpaired(); throw new ApiError(j?.error?.message || httpProblem(401), { status: 401, code: 'unauthorized' }); }
  if (!res.ok) {
    if (res.status >= 502 && res.status <= 504) conn.suspect();
    throw new ApiError(j?.error?.message || httpProblem(res.status), { status: res.status, code: j?.error?.code });
  }
  return j;
}
const rpc = async (m, p, timeout) => (await api('/api/rpc/' + m, p || {}, timeout)).result;

// ---------------------------------------------------------------- the connection
const conn = {
  state: 'idle',   // connecting | live | retrying | offline | idle (not started, or unpaired)
  ctrl: null, timer: 0, watch: 0, attempt: 0, lastByte: 0, why: '', nextAt: 0, downSince: 0, backAt: 0, startedAt: 0,
  abortWhy: '', immediate: false,

  start() { if (!this.ctrl && !this.timer) this.connect(); },
  stop() {
    const c = this.ctrl;
    this.ctrl = null;
    c?.abort();
    clearTimeout(this.timer); this.timer = 0; clearInterval(this.watch);
    this.downSince = 0; this.attempt = 0;
    this.set('idle');
  },
  /** Reconnect now: a tap on Retry, the network came back, the page came back to the front. */
  now(why) {
    clearTimeout(this.timer); this.timer = 0;
    if (this.ctrl) { this.immediate = true; this.abortWhy = why || 'Reconnecting'; this.ctrl.abort(); }
    else if (S.device) this.connect();
  },
  async connect() {
    clearTimeout(this.timer); this.timer = 0;
    const ctrl = this.ctrl = new AbortController();
    this.startedAt = Date.now();
    if (this.state === 'idle' || this.state === 'live') this.set('connecting');
    let res;
    // a network that swallows packets answers nothing, not an error: an attempt gets 10 s
    const slow = setTimeout(() => { this.abortWhy = 'NetPI did not answer'; ctrl.abort(); }, 10_000);
    try {
      res = await fetch('/api/events', { credentials: 'same-origin', cache: 'no-store', headers: { Accept: 'text/event-stream' }, signal: ctrl.signal });
    } catch {
      return this.fail(ctrl, this.abortWhy || (navigator.onLine === false ? 'This phone is offline' : 'Could not reach NetPI'));
    } finally {
      clearTimeout(slow);
    }
    if (ctrl !== this.ctrl) return;
    if (res.status === 401) { this.ctrl = null; unpaired(); return; }
    if (!res.ok || !res.body) { ctrl.abort(); return this.fail(ctrl, httpProblem(res.status)); }
    this.lastByte = Date.now();
    this.attempt = 0;
    const wasDown = this.downSince > 0;
    this.set('live');
    this.watchdog(ctrl);
    resyncAll(wasDown);
    const reader = res.body.getReader();
    const dec = new TextDecoder();
    const parser = new SseParser();
    try {
      for (;;) {
        const { value, done } = await reader.read();
        if (done) break;
        this.lastByte = Date.now();
        for (const f of parser.push(dec.decode(value, { stream: true }))) if (f.kind === 'data') onFrame(f.data);
      }
      this.fail(ctrl, 'NetPI closed the connection (a restart or an update)');
    } catch {
      this.fail(ctrl, this.abortWhy || 'The connection dropped');
    }
  },
  watchdog(ctrl) {
    clearInterval(this.watch);
    this.watch = setInterval(() => {
      if (ctrl !== this.ctrl) { clearInterval(this.watch); return; }
      if (Date.now() - this.lastByte > QUIET_MS) { this.abortWhy = `NetPI went quiet (nothing for ${QUIET_MS / 1000} s)`; ctrl.abort(); }
    }, 3000);
  },
  fail(ctrl, why) {
    if (ctrl !== this.ctrl) return;   // an older connection, already replaced
    this.ctrl = null;
    clearInterval(this.watch);
    this.abortWhy = '';
    if (!this.downSince) this.downSince = this.state === 'live' && this.lastByte ? this.lastByte : Date.now();   // a silent socket was gone since its last byte
    this.why = why;
    if (this.immediate) { this.immediate = false; this.set('retrying'); this.connect(); return; }
    this.attempt++;
    const offline = navigator.onLine === false;
    const wait = offline ? 15_000 : backoff(this.attempt);
    this.nextAt = Date.now() + wait;
    this.set(offline ? 'offline' : 'retrying');
    // a hidden page waits: the phone freezes its timers anyway, and coming back to the front reconnects at once
    if (!document.hidden) this.timer = setTimeout(() => this.connect(), wait);
  },
  /** Back at the front (or out of the back-forward cache): the timers were frozen meanwhile, the socket may be dead without knowing. */
  wake() {
    if (!S.device) return;
    if (!this.ctrl) return this.now();
    if (this.state === 'live' && Date.now() - this.lastByte > 12_000) return this.now('Back from the background');
    if (this.state === 'live') resyncAll(false);
  },
  /** A request could not reach NetPI: if the stream has been quiet too, do not wait for the watchdog. */
  suspect() {
    if (this.state === 'live' && Date.now() - this.lastByte > 11_000) this.now('No answer from NetPI');
  },
  set(state) {
    if (state === 'live' && this.downSince) this.backAt = Date.now();
    if (state === 'live' || state === 'idle') this.downSince = 0;
    this.state = state;
    paintConn();
  },
};

function onFrame(data) {
  let e;
  try { e = JSON.parse(data); } catch { return; }
  if (e.type === 'hello') return onHello(e);
  onEvent(e);
}
/** The page on the server changed (a publish): this copy is stale. Reload, unless the person is typing (then offer it). */
function onHello(e) {
  if (!e.build) return;
  if (!S.build) { S.build = e.build; return; }
  if (e.build === S.build) return;
  const typing = /^(INPUT|TEXTAREA|SELECT)$/.test(document.activeElement?.tagName ?? '');
  if (!typing) { location.reload(); return; }
  S.updated = true;
  paintConn();
}

let resyncing = null;
let resyncAgain = false;
/** Everything the page shows, read again: after a (re)connect, a resync frame, or coming back to the front. */
function resyncAll(wasDown) {
  if (resyncing) { resyncAgain = true; return resyncing; }   // one that started before this reason may have read too early
  resyncing = Promise.allSettled([
    loadList().then(paintListSoon),
    chat?.resync(),
    S.view === 'work' ? loadWork() : null,
  ]).finally(() => {
    resyncing = null;
    paintNeeds();
    if (wasDown) paintConn();
    if (resyncAgain) { resyncAgain = false; resyncAll(false); }
  });
  return resyncing;
}

function onEvent(e) {
  const d = e.data || {};
  const sid = e.sessionId || d.sessionId;
  switch (e.type) {
    case 'resync': resyncAll(false); return;
    case 'agents.changed': case 'process.started': case 'process.exited': case 'usage.changed':
      if (S.view === 'work') refreshWork();
      return;
    case 'agent.status': {
      const a = d.agent;
      if (!a) return;
      if (!a.isSubagent) S.runs.set(a.sessionId, a);
      if (S.view === 'work') refreshWork();
      if (chat && !a.isSubagent && a.sessionId === chat.id) chat.onRun(a);
      paintListSoon();
      return;
    }
    case 'ask.asked': S.asks = [...S.asks.filter((a) => a.id !== d.id), d]; break;
    case 'ask.closed': S.asks = S.asks.filter((a) => a.id !== d.id); break;
    case 'guard.asked': S.guards = [...S.guards.filter((g) => g.approvalId !== d.approvalId), d]; break;
    case 'guard.closed': S.guards = S.guards.filter((g) => g.approvalId !== d.approvalId); break;
    case 'agent.waiting': case 'agent.waitEnded': case 'plan.changed': case 'plan.enter.asked': case 'plan.enter.closed':
      refreshWaits();
      return;
    case 'session.created': case 'session.updated': {
      const s = d.session;
      if (!s?.id) return;
      const i = S.sessions.findIndex((x) => x.id === s.id);
      if (i >= 0) S.sessions[i] = s;
      else if (e.type === 'session.created' && !S.search) S.sessions.unshift(s);
      if (chat?.id === s.id) { chat.session = s; paintHeader(); }
      paintListSoon();
      return;
    }
    case 'session.deleted': {
      S.sessions = S.sessions.filter((x) => x.id !== d.id);
      if (chat?.id === d.id) { chat.deleted = true; paintStrip(); paintComposer(); }
      paintListSoon();
      return;
    }
    default:
      if (chat && sid === chat.id) chat.onEvent(e);
      return;
  }
  refreshWaits();
  paintNeeds();
}

// ---------------------------------------------------------------- data
const waitsOf = (id) => ({
  asks: S.asks.filter((a) => a.sessionId === id),
  guards: S.guards.filter((g) => g.sessionId === id),
  other: S.waits.filter((w) => w.sessionId === id && !w.answered && w.kind !== 'ask' && w.kind !== 'guard'),
});
const needsYou = (id) => { const w = waitsOf(id); return w.asks.length + w.guards.length + w.other.length > 0; };
function rowState(s) {
  if (needsYou(s.id)) return 'wait';
  const r = S.runs.get(s.id);
  if (isActive(r?.status)) return 'run';
  return 'idle';
}
async function loadList() {
  try {
    const [sessions, runs, asks, guards, waits] = await Promise.all([
      rpc('sessions.list', { limit: S.limit, search: S.search || undefined }),
      rpc('runs.list', {}).catch(() => null), rpc('ask.pending', {}).catch(() => null), rpc('guard.pending', {}).catch(() => null),
      rpc('agent.waits', {}).catch(() => null),
    ]);
    S.sessions = Array.isArray(sessions) ? sessions : (sessions?.sessions ?? []);
    if (runs) S.runs = new Map(runs.filter((a) => !a.isSubagent).map((a) => [a.sessionId, a]));
    if (asks) S.asks = asks;
    if (guards) S.guards = guards;
    if (waits) S.waits = waits;
    S.listError = '';
    S.listLoaded = true;
  } catch (e) {
    S.listError = e.message;
  }
}
const refreshWaits = debounce(async () => {
  const [asks, guards, waits] = await Promise.all([rpc('ask.pending', {}).catch(() => null), rpc('guard.pending', {}).catch(() => null), rpc('agent.waits', {}).catch(() => null)]);
  if (asks) S.asks = asks;
  if (guards) S.guards = guards;
  if (waits) S.waits = waits;
  paintNeeds();
}, 250);
async function loadProjects() {
  if (!S.projects.length) S.projects = (await rpc('projects.list', {}).catch(() => [])) || [];
  if (!S.models.length) { const m = await rpc('models.list', {}).catch(() => null); S.models = Array.isArray(m) ? m : (m?.models || []); }
}
const projectName = (id) => S.projects.find((p) => p.id === id)?.name;

// ---------------------------------------------------------------- one chat
let pendingSeq = 0;
class Chat {
  constructor(id) {
    this.id = id;
    this.session = S.sessions.find((s) => s.id === id) ?? null;
    this.run = S.runs.get(id) ?? null;
    this.messages = []; this.loaded = false; this.hasMore = false; this.atStart = false; this.error = '';
    this.live = newLive(); this.gen = 0; this.tools = new Map(); this.queue = []; this.pending = []; this.notice = null;
    this.held = null; this.catchAgain = false; this.syncing = null; this.again = false;
    this.open = new Set(); this.cache = new Map(); this.stick = true; this.deleted = false;
    this.endTimer = 0; this.noticeTimer = 0;
  }
  get gone() { return chat !== this; }

  resync() {
    return Promise.allSettled([this.sync(), this.catchUp(), this.loadQueue(), this.loadRun(), this.loadSession()]);
  }
  async loadSession() {
    if (this.session) return;
    try { this.session = await rpc('sessions.get', { id: this.id }); } catch { /* an older listener, or gone */ }
    if (!this.gone) paintHeader();
  }
  async loadRun() {
    try {
      const a = await rpc('agent.get', { sessionId: this.id });
      if (!this.gone && a) { S.runs.set(this.id, a); this.onRun(a); }
    } catch { /* the list's runs.list is the fallback */ }
  }
  async loadQueue() {
    try {
      const q = await rpc('agent.queue', { sessionId: this.id });
      if (!this.gone && Array.isArray(q)) { this.queue = q; this.settle(); paintQueue(); }
    } catch { /* an older listener */ }
  }

  /** The window up to the newest message. One read at a time; a call meanwhile reads again after it. */
  sync() {
    if (this.syncing) { this.again = true; return this.syncing; }
    this.syncing = (async () => {
      try {
        do {
          this.again = false;
          if (!this.loaded) {
            const p = await rpc('sessions.messages', { id: this.id, limit: 50 });
            if (this.gone) return;
            this.messages = p?.messages ?? [];
            this.hasMore = !!p?.hasMore;
            this.atStart = !this.hasMore;
            this.loaded = true;
            this.stick = true;
          } else {
            for (let page = 0; ; page++) {
              const p = await rpc('sessions.messages', { id: this.id, afterSeq: lastSeq(this.messages), limit: 100 });
              if (this.gone) return;
              this.messages = mergeMessages(this.messages, p?.messages);
              if (!p?.hasMore || !p.messages?.length) break;
              if (page >= 2) { this.loaded = false; this.again = true; break; }   // far behind (away a long time): read the tail instead
            }
          }
        } while (this.again);
        this.error = '';
      } catch (e) {
        if (!this.gone) this.error = e.message;
      } finally {
        this.syncing = null;
      }
      if (!this.gone) { this.settle(); this.changed(); paintStrip(); }
    })();
    return this.syncing;
  }
  /** A stored message changed (message.updated): read just that one. */
  async refetch(seq) {
    try {
      const p = await rpc('sessions.messages', { id: this.id, afterSeq: seq - 1, limit: 1 });
      if (this.gone) return;
      const have = new Set(this.messages.map((m) => m.id));
      this.messages = mergeMessages(this.messages, (p?.messages ?? []).filter((m) => have.has(m.id)));
      this.changed();
    } catch { /* the next sync has it */ }
  }
  async loadOlder() {
    const first = this.messages[0];
    if (!first) return;
    const p = await rpc('sessions.messages', { id: this.id, beforeSeq: first.seq, limit: 40 });
    if (this.gone) return;
    this.messages = mergeMessages(this.messages, p?.messages);
    this.hasMore = !!p?.hasMore;
    this.atStart = !this.hasMore;
    this.changed(false, true);
  }

  /**
   * What the answer has streamed so far (agent.stream), for a chat opened mid-answer or back from a lost connection.
   * Stream and tool events that land meanwhile wait, then go after the copy (the copy is complete up to its seq).
   */
  async catchUp() {
    if (this.held) { this.catchAgain = true; return; }
    this.held = [];
    let snap = null;
    try { snap = await rpc('agent.stream', { sessionId: this.id }, 10_000); } catch { /* offline, or an older listener: what arrives is what there is */ }
    const held = this.held;
    this.held = null;
    if (this.gone) return;
    if (snap) {
      const skew = snap.now ? Date.now() - snap.now : 0;
      for (const ev of snap.events ?? []) if (ev.type === 'tool.start') { if (ev.ts) ev.d = { ...ev.d, startedAt: ev.ts + skew }; this.tools.delete(ev.d?.callId); }
      this.live = newLive();
      clearTimeout(this.endTimer);
    }
    for (const ev of mergeCatchUp(snap, held)) this.apply(ev);
    this.changed();
    if (this.catchAgain) { this.catchAgain = false; this.catchUp(); }
  }

  onEvent(e) {
    if (this.held && (e.type.startsWith('stream.') || e.type.startsWith('tool.'))) { this.held.push(e); return; }
    this.apply(e);
  }
  apply(e) {
    const d = e.data || {};
    switch (e.type) {
      case 'message.added': {
        const gen = this.gen;
        this.sync().then(() => { if (d.role === 'assistant' && gen === this.gen && !this.gone) this.clearLive(); });   // the stored answer replaces the streamed one
        return;
      }
      case 'message.updated':
        if (d.seq != null && d.seq <= lastSeq(this.messages)) this.refetch(d.seq);
        else this.sync();
        return;
      case 'messages.compacted': this.sync(); return;
      case 'agent.queue': this.queue = d.items ?? []; this.settle(); paintQueue(); this.changed(); return;
      case 'agent.notice': this.setNotice(d); return;
      case 'stream.start':
        this.gen++;
        clearTimeout(this.endTimer);
        if (this.notice && this.notice.level !== 'error' && !this.notice.kept) this.dropNotice(1500);
        break;
      case 'stream.delta': case 'stream.tool':
        if (this.notice?.kept) this.dropNotice(0);   // the model answers again: the compaction banner has done its job
        break;
      case 'stream.end':
        clearTimeout(this.endTimer);
        this.endTimer = setTimeout(() => { if (this.live.ended) this.clearLive(); }, 4000);
        break;
      case 'stream.reset': break;
      case 'tool.start': case 'tool.end':
        toolsApply(this.tools, e.type, d);
        this.changed();
        return;
      default: return;
    }
    this.live = liveApply(this.live, e.type, d);
    this.changed(true);
  }
  clearLive() { clearTimeout(this.endTimer); if (this.live.active) { this.live = newLive(); this.changed(); } }
  onRun(a) {
    const was = this.run;
    this.run = a;
    if (!isActive(a.status)) {
      // a run that ended while the stream was away never said stream.end: nothing streams now
      for (const [k, t] of this.tools) if (t.status === 'running') this.tools.set(k, { ...t, status: 'done' });
      if (this.notice && this.notice.level !== 'error') this.dropNotice(0);
      if (was && isActive(was.status) || this.live.active) { const gen = this.gen; this.sync().then(() => { if (gen === this.gen && !this.gone) this.clearLive(); }); }
    }
    paintStrip(); paintComposer(); paintCards(); this.changed();
  }
  setNotice(d) {
    // a compaction's banner stays while the summary is written and until the model answers again; an error stays until dismissed
    const kept = d.kind === 'compaction' && (d.phase === 'start' || (d.phase === 'done' && d.mode !== 'manual'));
    this.notice = { level: d.level ?? 'info', text: d.text ?? '', kept, at: Date.now() };
    clearTimeout(this.noticeTimer);
    if (!kept && this.notice.level !== 'error') this.noticeTimer = setTimeout(() => this.dropNotice(0), 8000);
    paintNotice();
  }
  dropNotice(ms) {
    clearTimeout(this.noticeTimer);
    this.noticeTimer = setTimeout(() => { this.notice = null; paintNotice(); }, ms);
  }
  settle() { this.pending = settlePending(this.pending, this.messages, this.queue.filter((q) => (q.source ?? 'user') === 'user')); }
  changed(live, keepPos) { paintMsgs(keepPos); }

  async send(text, images) {
    const busy = chatBusy(this);
    const mode = busy ? store.get('busyMode', 'steer') : 'auto';
    const p = { key: 'p' + ++pendingSeq, text, images, mode, state: 'sending', afterSeq: lastSeq(this.messages), at: Date.now() };
    this.pending.push(p);
    this.stick = true;
    this.changed();
    try {
      const info = await rpc('agent.send', { sessionId: this.id, text, images: images.length ? images : undefined, mode }, 30_000);
      p.state = 'sent';
      if (info && !info.isSubagent) { S.runs.set(this.id, info); if (!this.gone) this.onRun(info); }
    } catch (e) {
      p.state = e.timeout ? 'unsure' : 'failed';
      p.error = e.message;
    }
    if (this.gone) return;
    this.changed();
    this.sync();
    this.loadQueue();
  }
  retry(p) { this.pending = this.pending.filter((x) => x !== p); this.send(p.text, p.images); }
  discard(p) { this.pending = this.pending.filter((x) => x !== p); this.changed(); }
}
const chatBusy = (c) => {
  const st = c.run?.status;
  if (st === 'running' || st === 'queued') return true;
  return st === 'yielded' && !needsYou(c.id);
};

// ---------------------------------------------------------------- routing
// The address holds the view (#chat=ID, #new, #agents, nothing for the list): a reload comes back to it, and back works.
const openChat = (id) => navigate('chat=' + encodeURIComponent(id));
function navigate(h) { history.pushState({ app: 1 }, '', '#' + h); route(); }
function goBack() { if (history.state?.app) history.back(); else { history.replaceState(null, '', location.pathname); route(); } }
function route() {
  if (!S.device) return;
  const h = location.hash.slice(1);
  const m = /^chat=(.+)$/.exec(h);
  if (m) return showChat(decodeURIComponent(m[1]));
  chat = null;
  if (h === 'new') return showNew();
  if (h === 'agents') return showWork();
  showList();
}

// ---------------------------------------------------------------- the frame every view shares
function header(title, sub, back, right) {
  return el('header', { id: 'hdr' },
    back ? el('button', { class: 'icon', onclick: back, 'aria-label': 'Back' }, icon('back', 24)) : el('span', { class: 'brand' }, 'N'),
    el('div', { class: 'title' }, el('h1', { id: 'hdr-title' }, title), el('div', { class: 'sub', id: 'hdr-sub' }, sub || '')),
    right, el('button', { id: 'conn', class: 'conn', 'aria-label': 'Connection', onclick: () => conn.now('Reconnecting') }));
}
function mount(...nodes) { $app.replaceChildren(...nodes.filter(Boolean)); paintConn(); }

function paintConn() {
  const dot = document.getElementById('conn');
  if (dot) { dot.dataset.state = conn.state; dot.title = conn.state === 'live' ? 'Live' : conn.why || conn.state; }
  const box = document.getElementById('banner');
  if (!box) return;
  const now = Date.now();
  let tone = '', text = '', action = null;
  if (S.updated) { tone = 'info'; text = 'The phone page was updated.'; action = ['Reload', () => location.reload()]; }
  else if (conn.state === 'connecting' && now - conn.startedAt > 1500) { tone = 'info'; text = 'Connecting to NetPI…'; }
  else if (conn.state === 'retrying' || conn.state === 'offline') {
    const down = now - conn.downSince;
    const next = Math.max(0, Math.ceil((conn.nextAt - now) / 1000));
    tone = conn.state === 'offline' || down > 8000 ? 'err' : 'warn';
    text = (down > 8000 ? `Not connected for ${duration(down)}: nothing new arrives. ` : 'Reconnecting… ') + conn.why
      + (conn.timer && next ? ` · next try in ${next} s` : conn.ctrl ? ' · trying…' : '');
    action = ['Retry now', () => conn.now('Reconnecting')];
  } else if (conn.state === 'live' && conn.backAt && now - conn.backAt < 2500) { tone = 'ok'; text = 'Connected again · up to date'; }
  if (chat) paintStrip();
  box.className = 'banner' + (tone ? ' tone-' + tone : '');
  box.hidden = !text;
  box.replaceChildren(...(text ? [el('span', {}, text), action ? el('button', { class: 'small', onclick: action[1] }, action[0]) : null] : []));
}

function paintNeeds() {
  const n = S.sessions.filter((s) => s.kind !== 'subagent' && needsYou(s.id)).length;
  document.title = (n ? `(${n}) ` : '') + 'NetPI';
  paintListSoon();
  if (chat) { paintCards(); paintStrip(); paintComposer(); paintMsgs(); }
}

// ---------------------------------------------------------------- pairing
function renderPair(msg) {
  S.view = 'pair';
  const code = el('input', { placeholder: 'Pairing code', autocapitalize: 'characters', autocomplete: 'off', spellcheck: 'false' });
  const name = el('input', { placeholder: 'Name this phone', value: store.get('name', '') });
  const err = el('div', { class: 'err-text' }, msg || '');
  const go = el('button', { class: 'primary', onclick: async () => {
    go.disabled = true; err.textContent = '';
    try { store.set('name', name.value); const r = await api('/api/pair', { code: code.value.trim(), name: name.value }); S.device = r.device; start(); }
    catch (e) { err.textContent = e.message; go.disabled = false; }
  } }, 'Pair');
  $app.replaceChildren(el('header', { id: 'hdr' }, el('span', { class: 'brand' }, 'N'), el('div', { class: 'title' }, el('h1', {}, 'NetPI'))),
    el('main', { class: 'form' }, el('p', { class: 'dim' }, 'Open the Phone tab in NetPI on your computer, press “Pair a phone”, and type the code here (or open its link).'), code, name, go, err));
}
function renderBootError(e, retry) {
  $app.replaceChildren(el('header', { id: 'hdr' }, el('span', { class: 'brand' }, 'N'), el('div', { class: 'title' }, el('h1', {}, 'NetPI'))),
    el('main', { class: 'form' }, el('div', { class: 'banner tone-err' }, el('span', {}, 'Cannot reach NetPI: ' + e.message)),
      el('p', { class: 'dim' }, 'Trying again by itself. NetPI may be restarting, or the PC is off.'), el('button', { onclick: retry }, 'Try now')));
}
function unpaired() {
  if (!S.device) return;
  S.device = null; chat = null;
  conn.stop();
  renderPair('This phone is not paired any more (it was unpaired on the computer). Pair it again from the Phone tab.');
}

// ---------------------------------------------------------------- the chat list
function tabsBar(active) {
  const b = (id, label, hash) => el('button', { class: active === id ? 'on' : '', onclick: () => { if (active !== id) { history.replaceState({ app: 1 }, '', hash ? '#' + hash : location.pathname); route(); } } }, label);
  return el('nav', { class: 'tabs' }, b('chats', 'Chats', ''), b('agents', 'Agents', 'agents'));
}
function showList() {
  S.view = 'list';
  const search = el('input', { type: 'search', placeholder: 'Search chats', value: S.search, enterkeyhint: 'search' });
  const reload = debounce(() => { S.search = search.value.trim(); S.limit = 60; loadList().then(paintList); }, 300);
  search.addEventListener('input', reload);
  mount(header('NetPI', S.device?.name ? 'on ' + S.device.name : '', null, el('button', { class: 'icon', 'aria-label': 'Refresh', onclick: () => resyncAll(false) }, icon('refresh'))),
    el('div', { id: 'banner', class: 'banner', hidden: true }), tabsBar('chats'),
    el('main', { id: 'list', class: 'list' }, el('div', { class: 'search' }, search), el('div', { id: 'rows' })),
    el('button', { class: 'fab', onclick: () => navigate('new'), 'aria-label': 'New chat' }, icon('plus', 28)));
  paintList();
  loadList().then(paintList);
}
function paintList() {
  const box = document.getElementById('rows');
  if (!box || S.view !== 'list') return;
  const rank = { wait: 0, run: 1, idle: 2 };
  const rows = S.sessions.filter((s) => s.kind !== 'subagent').map((s) => ({ s, st: rowState(s) }));
  rows.sort((a, b) => rank[a.st] - rank[b.st] || (b.s.pinned - a.s.pinned) || new Date(b.s.updatedAt) - new Date(a.s.updatedAt));
  const labels = { wait: 'Needs you', run: 'Running', idle: S.search ? 'Found' : 'Chats' };
  const nodes = [];
  let last = null;
  if (S.listError) nodes.push(el('div', { class: 'banner tone-err' }, el('span', {}, 'Could not load the chats: ' + S.listError), el('button', { class: 'small', onclick: () => loadList().then(paintList) }, 'Retry')));
  for (const { s, st } of rows) {
    if (st !== last) { nodes.push(el('div', { class: 'group' }, labels[st])); last = st; }
    const r = S.runs.get(s.id);
    const failed = st === 'idle' && r && (r.status === 'failed' || r.error);
    const what = st === 'wait' ? 'waits for your answer' : st === 'run' ? activityText(r) : failed ? 'failed: ' + (r.error || r.status) : null;
    nodes.push(el('button', { class: 'row', onclick: () => openChat(s.id) },
      el('span', { class: 'dot ' + (failed ? 'err' : st) }),
      el('span', { class: 't' }, el('b', {}, (s.pinned ? '📌 ' : '') + (s.title || 'Untitled')),
        el('span', { class: failed ? 'sub err' : 'sub' }, [what, projectName(s.projectId)].filter(Boolean).join(' · '))),
      el('time', {}, ago(s.updatedAt))));
  }
  if (!rows.length && !S.listError) nodes.push(el('div', { class: 'empty' }, !S.listLoaded ? 'Loading…' : S.search ? 'No chat matches.' : 'No chats yet.'));
  if (S.sessions.length >= S.limit) nodes.push(el('div', { class: 'more' }, el('button', { class: 'link', onclick: () => { S.limit += 60; loadList().then(paintList); } }, 'Show more')));
  box.replaceChildren(...nodes);
}
const paintListSoon = soon(paintList);

// ---------------------------------------------------------------- agents (the Work tab)
async function showWork() { S.view = 'work'; renderWork(); await loadWork(); }
async function loadWork() {
  try { S.work = await rpc('work.snapshot', {}); S.workError = ''; } catch (e) { S.workError = e.message; }
  if (S.view === 'work') paintWork();
}
const refreshWork = debounce(loadWork, 400);
function renderWork() {
  mount(header('NetPI', S.device?.name ? 'on ' + S.device.name : '', null, el('button', { class: 'icon', 'aria-label': 'Refresh', onclick: () => loadWork() }, icon('refresh'))),
    el('div', { id: 'banner', class: 'banner', hidden: true }), tabsBar('agents'), el('main', { id: 'work', class: 'list' }));
  paintWork();
}
function paintWork() {
  const box = document.getElementById('work');
  if (!box) return;
  const w = S.work;
  const nodes = [];
  if (S.workError) nodes.push(el('div', { class: 'banner tone-err' }, el('span', {}, S.workError)));
  if (!w) nodes.push(el('div', { class: 'empty' }, 'Loading…'));
  else {
    const title = (id) => S.sessions.find((x) => x.id === id)?.title;
    nodes.push(el('div', { class: 'group' }, 'Agents'));
    // the desktop's rule for "Off": switched off or unavailable, and nothing still running on it. Those stay out of the phone's list
    const isOff = (a) => (a.disabled || a.available === false) && !(a.owners?.length || a.waiters?.length);
    const pool = (w.agents || []).filter((a) => (a.configured || a.busy || a.queued) && !isOff(a));
    nodes.push(...(pool.length ? pool.map((a) => el('div', { class: 'row static' }, el('span', { class: 'dot ' + (a.disabled || a.available === false ? 'err' : a.busy ? 'run' : 'ok') }),
      el('span', { class: 't' }, el('b', {}, a.key), el('span', { class: 'sub' }, [a.model || (a.models || [])[0], a.free ? 'free' : null, a.disabled ? 'off' : a.available === false ? (a.unavailable || 'unavailable') : null].filter(Boolean).join(' · '))),
      el('time', {}, a.busy + '/' + a.capacity + (a.queued ? ' +' + a.queued : '')))) : [el('div', { class: 'empty' }, 'No agents set up.')]));
    const runs = (w.runs || []).slice().sort((a, b) => new Date(b.startedAt || b.createdAt) - new Date(a.startedAt || a.createdAt));
    const live = runs.filter((r) => isActive(r.status));
    const done = runs.filter((r) => !isActive(r.status)).slice(0, 12);
    nodes.push(el('div', { class: 'group' }, 'Running'));
    nodes.push(...(live.length ? live.map((r) => el('div', { class: 'row', onclick: () => openChat(r.sessionId) }, el('span', { class: 'dot ' + (needsYou(r.sessionId) ? 'wait' : 'run') }),
      el('span', { class: 't' }, el('b', {}, (r.isSubagent ? '↳ ' : '') + (title(r.sessionId) || r.name || r.id)), el('span', { class: 'sub' }, [activityText(r), r.agent || r.model, tokens(r.inputTokens + r.outputTokens) + ' tok'].filter(Boolean).join(' · '))),
      twoTap('Stop', 'Stop?', () => rpc('agent.abort', { sessionId: r.sessionId, subagents: false }).then(refreshWork).catch((x) => toast(x.message)))))
      : [el('div', { class: 'empty' }, 'Nothing is running.')]));
    if (done.length) {
      nodes.push(el('div', { class: 'group' }, 'Finished'));
      nodes.push(...done.map((r) => el('div', { class: 'row', onclick: () => openChat(r.sessionId) }, el('span', { class: 'dot ' + (r.status === 'failed' ? 'err' : 'idle') }),
        el('span', { class: 't' }, el('b', {}, (r.isSubagent ? '↳ ' : '') + (title(r.sessionId) || r.name || r.id)), el('span', { class: r.status === 'failed' ? 'sub err' : 'sub' }, (r.status + (r.error ? ' · ' + r.error : r.result ? ' · ' + r.result : '')).slice(0, 140))),
        el('time', {}, ago(r.finishedAt || r.createdAt)))));
    }
    const procs = (w.processes || []).filter((p) => p.status === 'running');
    if (procs.length) {
      nodes.push(el('div', { class: 'group' }, 'Processes'));
      nodes.push(...procs.map((p) => el('div', { class: 'row static' }, el('span', { class: 'dot run' }), el('span', { class: 't' }, el('b', { class: 'mono' }, p.command.replace(/\s+/g, ' ').slice(0, 80)), el('span', { class: 'sub' }, p.shell + (p.background ? ' · background' : '') + ' · ' + ago(p.startedAt))))));
    }
    const u = w.usage?.budget;
    if (u) nodes.push(el('div', { class: 'foot' }, 'Today $' + (u.todayUsd ?? 0).toFixed(2) + (u.dailyUsd ? ' of $' + u.dailyUsd : '') + ' · this period $' + (u.spentUsd ?? 0).toFixed(2) + (u.monthlyUsd ? ' of $' + u.monthlyUsd : '')));
  }
  nodes.push(el('div', { class: 'foot' }, 'This phone: ' + (S.device?.name || 'unnamed') + ' · ',
    twoTap('Unpair', 'Unpair this phone?', () => api('/api/logout', {}).then(() => { S.device = null; conn.stop(); renderPair(); }).catch((x) => toast(x.message)), 'link small')));
  box.replaceChildren(...nodes);
}

// ---------------------------------------------------------------- a new chat
async function showNew() {
  S.view = 'new';
  renderNew();
  await loadProjects().catch(() => {});
  if (S.view === 'new') renderNew(true);
}
let newDraft = { text: '', images: [] };
function renderNew(refresh) {
  const prev = refresh ? document.getElementById('new-text') : null;
  if (prev) newDraft.text = prev.value;
  const project = el('select', { 'aria-label': 'Project' }, el('option', { value: '' }, 'No project'), S.projects.map((p) => el('option', { value: p.id, selected: p.id === store.get('lastProject', '') }, p.name)));
  const model = el('select', { 'aria-label': 'Model' }, el('option', { value: '' }, 'Default model'), S.models.map((m) => { const ref = m.ref || m.id || m; return el('option', { value: ref }, m.displayName || ref); }));
  const text = el('textarea', { id: 'new-text', rows: 7, placeholder: 'What should it do?', value: newDraft.text, oninput: () => { newDraft.text = text.value; } });
  const thumbs = el('div', { class: 'thumbs' });
  const paintThumbs = () => thumbs.replaceChildren(...newDraft.images.map((i, n) => thumbNode(i, () => { newDraft.images.splice(n, 1); paintThumbs(); })));
  const file = picker((img) => { newDraft.images.push(img); paintThumbs(); });
  const err = el('div', { class: 'err-text' });
  const go = el('button', { class: 'primary', onclick: async () => {
    const body = text.value.trim();
    if (!body && !newDraft.images.length) return;
    go.disabled = true; err.textContent = '';
    try {
      store.set('lastProject', project.value);
      const s = await rpc('sessions.create', { projectId: project.value || undefined, model: model.value || undefined });
      const images = newDraft.images;
      newDraft = { text: '', images: [] };
      S.sessions.unshift(s);
      history.replaceState({ app: 1 }, '', '#chat=' + encodeURIComponent(s.id));   // back from the new chat goes to the list, not to this form
      route();
      chat?.send(body, images);   // the chat shows the send: sending, then the message, or what went wrong
    } catch (e) { err.textContent = e.message; go.disabled = false; }
  } }, 'Start');
  mount(header('New chat', null, goBack), el('div', { id: 'banner', class: 'banner', hidden: true }),
    el('main', { class: 'form' }, project, model, text, thumbs, el('div', { class: 'row-btns' }, el('button', { onclick: () => file.click() }, icon('clip', 18), ' Picture'), file, go), err));
  paintThumbs();
}

// ---------------------------------------------------------------- the chat
function showChat(id) {
  if (chat?.id === id && S.view === 'chat' && document.getElementById('msgs')) return;
  chat = new Chat(id);
  S.view = 'chat';
  renderChat();
  chat.resync();
}
let observer = null;
function renderChat() {
  observer?.disconnect();
  mount(el('div', { id: 'hdrbox' }), el('div', { id: 'banner', class: 'banner', hidden: true }), el('div', { id: 'strip', class: 'strip', hidden: true }),
    el('main', { id: 'scroll', class: 'chat' }, el('div', { id: 'older' }), el('div', { class: 'msgs', id: 'msgs' }), el('div', { id: 'pending', class: 'msgs' })),
    el('button', { id: 'jump', class: 'jump', hidden: true, 'aria-label': 'Jump to the latest', onclick: () => { chat.stick = true; pin(); } }, icon('down', 20)),
    el('div', { id: 'dock', class: 'dock' }, el('div', { id: 'cnotice' }), el('div', { id: 'cards' }), el('div', { id: 'queue' }), el('div', { id: 'composer', class: 'composer' })));
  comp = null;
  const sc = document.getElementById('scroll');
  // "following" = the reader is at the bottom; a scroll up lets go, a scroll back down picks it up again. Layout growth never lets go.
  sc.addEventListener('scroll', () => {
    if (!chat) return;
    chat.stick = sc.scrollHeight - sc.scrollTop - sc.clientHeight < 80;
    if (chat.stick) document.getElementById('jump').hidden = true;
  }, { passive: true });
  const dock = document.getElementById('dock');
  observer = new ResizeObserver(() => {   // a taller message, the dock growing, the keyboard, a picture that loaded
    $app.style.setProperty('--dock', dock.offsetHeight + 'px');   // the jump button sits above the dock
    if (chat?.stick) pin();
  });
  observer.observe(sc);
  observer.observe(document.getElementById('msgs'));
  observer.observe(document.getElementById('dock'));
  paintHeader(); paintStrip(); paintMsgs(); paintCards(); paintNotice(); paintQueue(); paintComposer();
}
function pin() { const sc = document.getElementById('scroll'); if (sc) sc.scrollTop = sc.scrollHeight; }
function paintHeader() {
  const box = document.getElementById('hdrbox');
  if (!box || !chat) return;
  const s = chat.session;
  const sub = [projectName(s?.projectId), chat.run?.agent || s?.model || chat.run?.model].filter(Boolean).join(' · ');
  box.replaceChildren(header(s?.title || 'Chat', sub, goBack));
  paintConn();
}
function paintStrip() {
  const box = document.getElementById('strip');
  if (!box || !chat) return;
  const r = chat.run;
  const w = waitsOf(chat.id);
  let node = null;
  if (chat.deleted) node = el('div', { class: 'tone-err' }, el('span', {}, 'This chat was deleted.'));
  else if (w.asks.length || w.guards.length || w.other.length) node = el('div', { class: 'tone-warn' }, el('span', { class: 'dot wait' }), el('span', {}, 'Waits for your answer below'));
  else if (r && (r.status === 'running' || r.status === 'queued' || (r.status === 'yielded'))) {
    node = el('div', { class: r.status === 'queued' ? 'tone-warn' : 'tone-run' }, el('span', { class: 'dot ' + (r.status === 'queued' ? 'wait' : 'run') }),
      el('span', { class: 'grow' }, r.status === 'queued' ? `Waiting for a free ${r.agent || 'agent'} slot` : activityText(r),
        r.startedAt ? el('span', { class: 'dur', 'data-since': new Date(r.startedAt).getTime() }, ' · ' + duration(Date.now() - new Date(r.startedAt).getTime())) : null),
      twoTap('Stop', 'Stop?', () => rpc('agent.abort', { sessionId: chat.id }).catch((e) => toast(e.message))));
  } else if (r && (r.status === 'failed' || r.error)) node = el('div', { class: 'tone-err' }, el('span', {}, 'The last run failed: ' + (r.error || r.status)));   // a chat's run that failed goes back to idle with its error
  // offline, the banner says it already
  else if (chat.error && conn.state === 'live') node = el('div', { class: 'tone-err' }, el('span', { class: 'grow' }, 'Could not load the messages: ' + chat.error), el('button', { class: 'small', onclick: () => chat.resync() }, 'Retry'));
  box.hidden = !node;
  box.replaceChildren(...(node ? [node] : []));
}

// the transcript: items built from the window and the live answer; a node is drawn again only when what it shows changed
const ids = new WeakMap();
let idSeq = 0;
const oid = (o) => { if (!o) return 0; let v = ids.get(o); if (!v) ids.set(o, v = ++idSeq); return v; };
let paintKeep = false;
const paintMsgsSoon = soon(() => { const k = paintKeep; paintKeep = false; drawMsgs(k); });
function paintMsgs(keepPos) { if (keepPos) paintKeep = true; paintMsgsSoon(); }
function drawMsgs(keepPos) {
  const box = document.getElementById('msgs');
  const sc = document.getElementById('scroll');
  if (!box || !chat) return;
  const c = chat;
  const prevH = sc.scrollHeight;
  document.getElementById('older').replaceChildren(...(c.hasMore ? [el('div', { class: 'more' }, el('button', { class: 'link', onclick: (e) => { e.target.disabled = true; c.loadOlder().catch((x) => toast(x.message)); } }, 'Load older messages'))] : []));
  const items = withLive(buildItems(c.messages, c.atStart), c.live, c.tools);
  const next = new Map();
  const nodes = [];
  for (const it of items) {
    const sig = itemSig(it, c);
    let hit = c.cache.get(it.key);
    if (!hit || hit.sig !== sig) hit = { sig, node: drawItem(it, c) };
    next.set(it.key, hit);
    nodes.push(hit.node);
  }
  c.cache = next;
  if (!c.loaded && !c.error) nodes.push(el('div', { class: 'empty' }, 'Loading…'));
  else if (c.loaded && !items.length && !c.pending.length) nodes.push(el('div', { class: 'empty' }, 'No messages yet.'));
  const same = box.childNodes.length === nodes.length && nodes.every((n, i) => box.childNodes[i] === n);
  if (!same) box.replaceChildren(...nodes);
  drawPending(c);
  if (keepPos) sc.scrollTop += sc.scrollHeight - prevH;
  else if (c.stick) pin();
  else if (sc.scrollHeight > prevH) document.getElementById('jump').hidden = false;
}
function itemSig(it, c) {
  switch (it.kind) {
    case 'text': return 't' + it.text.length + (it.live ? 'L' : '') + oid(it.msg);
    case 'steps': return (needsYou(c.id) ? 'w' : '') + it.steps.map((s) => s.key + (s.kind === 'tool' ? (s.result ? (s.result.isError ? 'E' : 'R') : (c.tools.get(s.call.id)?.status ?? '-') + (chatBusy(c) ? 'b' : '')) + s.call.name : s.text.length)).join('|');
    default: return it.kind + oid(it.msg);
  }
}
function drawItem(it, c) {
  switch (it.kind) {
    case 'user': return drawUser(it);
    case 'text': return el('div', { class: 'md' + (it.live ? ' live' : '') }, markdown(it.text));
    case 'steps': return drawSteps(it, c);
    case 'images': return el('div', { class: 'imgs' }, it.images.map((p) => thumbNode(p)));
    case 'notice': return drawNotice(it, c);
    case 'status': return el('div', { class: 'stop tone-' + it.view.tone }, el('b', {}, it.view.text), it.view.detail ? el('div', { class: 'detail' }, it.view.detail) : null);
    default: return el('div');
  }
}
function drawUser(it) {
  const long = it.from && it.text.length > 900;
  const body = el('div', { class: 'body' + (long ? ' clamp' : '') }, it.text);
  return el('div', { class: 'msg user' + (it.from ? ' from' : '') },
    it.from ? el('div', { class: 'meta' }, it.from) : null,
    it.text ? body : null,
    long ? el('button', { class: 'link small', onclick: (e) => { body.classList.toggle('clamp'); e.target.textContent = body.classList.contains('clamp') ? 'Show all' : 'Show less'; } }, 'Show all') : null,
    it.images.length ? el('div', { class: 'imgs' }, it.images.map((p) => thumbNode(p))) : null,
    el('div', { class: 'foot' }, it.tag ? el('span', { class: 'tag' }, it.tag) : null, clock(it.msg.createdAt)));
}
function drawNotice(it, c) {
  const v = it.view;
  const body = el('div', { class: 'notice-body' + (v.readable ? ' md' : '') });
  const fill = () => { if (!body.childNodes.length) body.append(v.readable ? markdown(v.text) : v.text); };
  const box = el('div', { class: 'notice tone-' + v.tone + (c.open.has(it.key) ? ' open' : '') });
  const pill = el('button', { class: 'pill', 'aria-expanded': String(c.open.has(it.key)), onclick: () => {
    const open = !box.classList.contains('open');
    box.classList.toggle('open', open);
    pill.setAttribute('aria-expanded', String(open));
    if (open) { c.open.add(it.key); fill(); } else c.open.delete(it.key);
  } }, el('span', { class: 'lbl' }, v.label), v.preview && v.preview !== v.label ? el('span', { class: 'pv' }, v.preview) : null, icon('chev', 14));
  if (c.open.has(it.key)) fill();
  box.append(pill, body);
  return box;
}
function drawSteps(it, c) {
  const tools = it.steps.filter((s) => s.kind === 'tool');
  const counts = new Map();
  for (const s of tools) counts.set(s.call.name, (counts.get(s.call.name) ?? 0) + 1);
  const failed = tools.filter((s) => s.result?.isError).length;
  // a chat that waits for the person has ended its run at that call (ask_user, plan_submit…): it is not running
  const running = !needsYou(c.id) && tools.find((s) => !s.result && c.tools.get(s.call.id)?.status === 'running');
  const summary = tools.length ? [...counts].map(([n, k]) => (k > 1 ? `${n} ×${k}` : n)).join(' · ') : 'Thinking';
  const det = el('details', { class: 'steps' + (failed ? ' has-err' : '') + (running ? ' running' : ''), open: c.open.has(it.key),
    ontoggle: () => { det.open ? c.open.add(it.key) : c.open.delete(it.key); } });
  det.append(el('summary', {},
    el('span', { class: 'si ' + (running ? 'run' : failed ? 'err' : 'ok') }),
    el('span', { class: 'grow' }, el('span', { class: 'what' }, summary + (failed ? ` · ${failed} failed` : '')),
      running && callSubject(running.call) ? el('span', { class: 'now' }, callSubject(running.call)) : null),
    running ? el('span', { class: 'dur', 'data-since': c.tools.get(running.call.id).startedAt }, duration(Date.now() - c.tools.get(running.call.id).startedAt)) : null,
    icon('chev', 14)));
  det.append(el('div', { class: 'steps-body' }, it.steps.map((s) => (s.kind === 'think' ? drawThink(s, c) : drawTool(s, c)))));
  return det;
}
function drawThink(s, c) {
  const d = el('details', { class: 'step think', open: c.open.has(s.key), ontoggle: () => { d.open ? c.open.add(s.key) : c.open.delete(s.key); } },
    el('summary', {}, el('span', { class: 'si think' }), el('b', {}, s.live ? 'Thinking…' : 'Thinking'), el('span', { class: 'subj' }, s.text.replace(/\s+/g, ' ').trim().slice(0, 90))),
    el('div', { class: 'thought' }, s.text));
  return d;
}
function drawTool(s, c) {
  const t = c.tools.get(s.call.id);
  const state = s.result ? (s.result.isError ? 'err' : 'ok') : t?.status === 'running' && !needsYou(c.id) ? 'run' : chatBusy(c) || s.live || needsYou(c.id) ? 'wait' : 'none';
  const subj = callSubject(s.call);
  const body = el('div', { class: 'step-body' });
  const fill = () => {
    if (body.childNodes.length) return;
    const args = parseArgs(s.call);
    const argText = Object.keys(args).length ? JSON.stringify(args, null, 1) : '';
    if (argText) body.append(el('pre', { class: 'args' }, argText.length > 3000 ? argText.slice(0, 3000) + '\n…' : argText));
    const out = s.result ? (s.result.content ?? '') : state === 'run' ? '(running…)' : state === 'wait' ? '(waiting to run)' : '(no result)';
    body.append(el('pre', { class: 'out' + (s.result?.isError ? ' err' : '') }, out.length > 6000 ? out.slice(0, 6000) + `\n… (${out.length} characters)` : out || '(no output)'));
  };
  const d = el('details', { class: 'step tool st-' + state, open: c.open.has(s.key), ontoggle: () => { if (d.open) { c.open.add(s.key); fill(); } else c.open.delete(s.key); } },
    el('summary', {}, el('span', { class: 'si ' + state }), el('b', {}, s.call.name), subj ? el('span', { class: 'subj' }, subj) : null,
      state === 'run' ? el('span', { class: 'dur', 'data-since': t.startedAt }, duration(Date.now() - t.startedAt)) : t?.durationMs != null && s.result ? el('span', { class: 'dur' }, duration(t.durationMs)) : null),
    body);
  if (c.open.has(s.key)) fill();
  return d;
}
function drawPending(c) {
  const box = document.getElementById('pending');
  if (!box) return;
  box.replaceChildren(...c.pending.map((p) => {
    const st = p.state === 'sending' ? el('span', {}, el('span', { class: 'spin' }), ' Sending…')
      : p.state === 'sent' ? el('span', {}, '✓ Sent' + (c.run?.status === 'queued' ? ' · waiting for a free agent' : ''))
        : el('span', { class: 'err-text' }, (p.state === 'unsure' ? 'No answer in time; it may have arrived: ' : 'Not sent: ') + p.error);
    const actions = p.state === 'failed' || p.state === 'unsure'
      ? [el('button', { class: 'small', onclick: () => c.retry(p) }, p.state === 'unsure' ? 'Send again' : 'Retry'),
        el('button', { class: 'small', onclick: () => { if (comp && !comp.ta.value) { comp.ta.value = p.text; saveDraft(); grow(); } c.discard(p); } }, 'Edit'),
        el('button', { class: 'small ghost', onclick: () => c.discard(p) }, 'Discard')]
      : [];
    return el('div', { class: 'msg user pending st-' + p.state }, p.text ? el('div', { class: 'body' }, p.text) : null,
      p.images.length ? el('div', { class: 'imgs' }, p.images.map((i) => thumbNode(i))) : null,
      el('div', { class: 'foot' }, st), actions.length ? el('div', { class: 'acts' }, actions) : null);
  }));
}

function paintNotice() {
  const box = document.getElementById('cnotice');
  if (!box || !chat) return;
  const n = chat.notice;
  box.replaceChildren(...(n ? [el('div', { class: 'banner tone-' + (n.level === 'error' ? 'err' : n.level === 'warn' || n.level === 'warning' ? 'warn' : 'info') },
    el('span', {}, n.text), el('button', { class: 'icon small', 'aria-label': 'Dismiss', onclick: () => chat.dropNotice(0) }, icon('x', 16)))] : []));
}
function paintQueue() {
  const box = document.getElementById('queue');
  if (!box || !chat) return;
  const own = chat.queue.filter((q) => (q.source ?? 'user') === 'user');
  box.replaceChildren(...own.map((q) => el('div', { class: 'chip' }, el('span', { class: 'tag' }, q.mode === 'steer' ? 'next step' : 'after the run'),
    el('span', { class: 'grow' }, q.text), el('button', { class: 'icon small', 'aria-label': 'Take it back', onclick: async () => {
      try {
        await rpc('agent.dequeue', { sessionId: chat.id, id: q.id });
        if (comp && !comp.ta.value) { comp.ta.value = q.text; saveDraft(); grow(); }
      } catch (e) { toast(e.message); }
    } }, icon('x', 16)))));
}

// cards: what the chat waits for. Drawn again only when the set changes, so an answer being typed survives other events
function paintCards() {
  const box = document.getElementById('cards');
  if (!box || !chat) return;
  const w = waitsOf(chat.id);
  const sig = w.guards.map((g) => g.approvalId).join() + '|' + w.asks.map((a) => a.id).join() + '|' + w.other.map((x) => x.id).join();
  if (sig === box.dataset.sig) return;
  box.dataset.sig = sig;
  box.replaceChildren(...w.guards.map(guardCard), ...w.asks.map(askCard), ...w.other.map(waitCard));
}
function card(title, ...kids) { return el('div', { class: 'card' }, el('h3', {}, title), ...kids); }
function guardCard(g) {
  const err = el('div', { class: 'err-text' });
  const send = (allow, scope) => rpc('guard.answer', { approvalId: g.approvalId, allow, scope })
    .then(() => { S.guards = S.guards.filter((x) => x.approvalId !== g.approvalId); paintNeeds(); })
    .catch((e) => { err.textContent = e.message; });
  return card('Approval needed · ' + g.tool, el('div', { class: 'subj' }, g.subject), g.rule ? el('div', { class: 'dim small' }, 'Rule: ' + g.rule) : null,
    g.opinion ? el('div', { class: 'dim small' }, g.opinion.harmless ? 'Second opinion: looks harmless' : 'Second opinion: not clearly harmless') : null,
    el('div', { class: 'opts' }, el('button', { class: 'primary', onclick: () => send(true, 'once') }, 'Allow once'), el('button', { onclick: () => send(true, 'session') }, 'Allow in this chat'), el('button', { class: 'danger', onclick: () => send(false) }, 'Deny')), err);
}
function askCard(a) {
  const picks = (a.questions || []).map(() => new Set());
  const text = el('input', { placeholder: 'Or answer in your own words' });
  const err = el('div', { class: 'err-text' });
  const qs = (a.questions || []).map((q, i) => el('div', { class: 'q' }, el('div', { class: 'qt' }, q.question), el('div', { class: 'opts' }, (q.options || []).map((o) => {
    const b = el('button', { onclick: () => {
      if (q.multiple) { picks[i].has(o.label) ? picks[i].delete(o.label) : picks[i].add(o.label); b.classList.toggle('sel'); }
      else { picks[i] = new Set([o.label]); [...b.parentNode.children].forEach((x) => x.classList.toggle('sel', x === b)); }
    } }, o.label, o.description ? el('span', { class: 'od' }, o.description) : null);
    return b;
  }))));
  const send = el('button', { class: 'primary', onclick: async () => {
    const answers = picks.map((s) => [...s]);
    if (!answers.some((x) => x.length) && !text.value.trim()) return;
    send.disabled = true; err.textContent = '';
    try {
      await rpc('ask.answer', { id: a.id, answers: answers.some((x) => x.length) ? answers : undefined, text: text.value.trim() || undefined });
      S.asks = S.asks.filter((x) => x.id !== a.id);
      paintNeeds();
    } catch (e) { err.textContent = e.message; send.disabled = false; }
  } }, 'Answer');
  return card((a.agentName || 'The agent') + ' asks', qs, text, send, err);
}
function waitCard(w) {
  const err = el('div', { class: 'err-text' });
  const done = () => { S.waits = S.waits.filter((x) => x.id !== w.id); paintNeeds(); refreshWaits(); };
  if (w.kind === 'plan') {
    const planId = w.data?.planId;
    const view = el('div', { class: 'plan md', hidden: true });
    const feedback = el('textarea', { rows: 3, placeholder: 'What should change?', hidden: true });
    const act = (decision) => async (e) => {
      if (decision === 'revise' && feedback.hidden) { feedback.hidden = false; feedback.focus(); return; }
      if (decision === 'revise' && !feedback.value.trim()) return;
      e.target.disabled = true; err.textContent = '';
      try { await rpc('plan.answer', { planId, decision, feedback: decision === 'revise' ? feedback.value.trim() : undefined }); done(); }
      catch (x) { err.textContent = x.message; e.target.disabled = false; }
    };
    const show = el('button', { class: 'link', onclick: async () => {
      if (!view.hidden) { view.hidden = true; show.textContent = 'Read the plan'; return; }
      show.textContent = 'Hide the plan';
      view.hidden = false;
      if (!view.childNodes.length) {
        view.append('Loading…');
        try { const p = await rpc('plan.get', { planId }); view.replaceChildren(markdown(p?.markdown || '(empty)')); } catch (x) { view.replaceChildren(el('span', { class: 'err-text' }, x.message)); }
      }
    } }, 'Read the plan');
    return card('Plan ready' + (w.data?.title ? ': ' + w.data.title : ''), show, view, feedback,
      el('div', { class: 'opts' }, el('button', { class: 'primary', onclick: act('approve') }, 'Approve'), el('button', { onclick: act('revise') }, 'Revise…'), el('button', { class: 'danger', onclick: act('cancel') }, 'Cancel plan')), err);
  }
  if (w.kind === 'plan-enter') {
    const act = (enter) => async (e) => {
      e.target.disabled = true; err.textContent = '';
      try { await rpc('plan.enterAnswer', { id: w.id, enter }); done(); } catch (x) { err.textContent = x.message; e.target.disabled = false; }
    };
    return card('Plan first?', w.data?.reason ? el('div', {}, w.data.reason) : null,
      el('div', { class: 'opts' }, el('button', { class: 'primary', onclick: act(true) }, 'Plan first'), el('button', { onclick: act(false) }, 'No, go ahead')), err);
  }
  return card('Waits for you', el('div', {}, (w.activity ? w.activity.charAt(0).toUpperCase() + w.activity.slice(1) : w.kind) + '.'),
    el('div', { class: 'dim small' }, 'This one is answered on the computer, or by writing a message here.'));
}

// The composer is built once per chat and only updated afterwards: rebuilding the textarea on every event closed the keyboard.
let comp = null;
const draftKey = () => 'draft:' + chat?.id;
function saveDraft() { if (comp && chat) store.set(draftKey(), comp.ta.value); }
function grow() { if (!comp) return; const ta = comp.ta; ta.style.height = 'auto'; ta.style.height = Math.min(ta.scrollHeight, window.innerHeight * 0.4) + 'px'; }
function buildComposer(box) {
  const ta = el('textarea', { rows: 1, placeholder: 'Message', enterkeyhint: 'send', value: store.get(draftKey(), '') });
  ta.addEventListener('input', () => { grow(); saveDraft(); paintSend(); });
  // the keyboard's key says "send", so it sends; Shift+Enter (a physical keyboard) is the new line
  let shift = false;
  ta.addEventListener('keydown', (e) => { shift = e.shiftKey; if (e.key === 'Enter' && !e.shiftKey && !e.isComposing) { e.preventDefault(); send.click(); } });
  ta.addEventListener('beforeinput', (e) => { if (e.inputType === 'insertLineBreak' && !shift) { e.preventDefault(); send.click(); } });   // soft keyboards that do not report the Enter key
  const file = picker((img) => { images.push(img); paintComposer(true); paintSend(); });
  const images = [];
  const send = el('button', { class: 'send', 'aria-label': 'Send', onclick: () => {
    const text = ta.value.trim();
    if ((!text && !images.length) || !chat || chat.deleted) return;
    chat.send(text, images.splice(0));
    ta.value = ''; saveDraft(); grow(); paintComposer(true); paintSend();
  } }, icon('send', 22));
  const modeBar = el('div', { class: 'mode' });
  const thumbs = el('div', { class: 'thumbs' });
  box.replaceChildren(modeBar, thumbs, el('div', { class: 'bar' }, el('button', { class: 'icon', onclick: () => file.click(), 'aria-label': 'Attach a picture' }, icon('clip', 22)), file, ta, send));
  comp = { box, ta, send, modeBar, thumbs, images, sig: '' };
  requestAnimationFrame(grow);
  paintSend();
}
function paintSend() { if (comp) comp.send.disabled = !comp.ta.value.trim() && !comp.images.length; }
function paintComposer(force) {
  const box = document.getElementById('composer');
  if (!box || !chat) return;
  if (!comp || comp.box !== box) buildComposer(box);
  const busy = chatBusy(chat);
  const mode = store.get('busyMode', 'steer');
  comp.ta.disabled = chat.deleted;
  comp.ta.placeholder = chat.deleted ? 'This chat was deleted' : busy ? (mode === 'steer' ? 'Message (read at its next step)' : 'Message (sent after this run)') : 'Message';
  const sig = (busy ? 'b' + mode : 'i') + '|' + comp.images.length;
  if (sig === comp.sig && !force) return;   // nothing the person can see changed: leave the buttons (and the keyboard) alone
  comp.sig = sig;
  comp.modeBar.hidden = !busy;
  comp.modeBar.replaceChildren(...(busy ? [el('span', { class: 'dim' }, 'While it works:'), ...['steer', 'queue'].map((m) => el('button', { class: 'seg' + (mode === m ? ' on' : ''), onclick: () => { store.set('busyMode', m); paintComposer(); } }, m === 'steer' ? 'Steer' : 'Queue'))] : []));
  comp.thumbs.hidden = !comp.images.length;
  comp.thumbs.replaceChildren(...comp.images.map((i, n) => thumbNode(i, () => { comp.images.splice(n, 1); paintComposer(true); paintSend(); })));
}

// ---------------------------------------------------------------- pictures
function picker(onImage) {
  const file = el('input', { type: 'file', accept: 'image/*', multiple: true, hidden: true });
  file.addEventListener('change', async () => {
    for (const f of file.files) { try { onImage(await shrink(f)); } catch { toast('That file is not a picture.'); } }
    file.value = '';
  });
  return file;
}
function thumbNode(p, onRemove) {
  if (!p.data) return el('span', { class: 'tag' }, 'picture');
  const src = 'data:' + (p.mediaType || 'image/png') + ';base64,' + p.data;
  const img = el('img', { src, alt: '', loading: 'lazy', onclick: () => lightbox(src) });
  return onRemove ? el('span', { class: 'thumb' }, img, el('button', { class: 'icon x', 'aria-label': 'Remove', onclick: onRemove }, icon('x', 14))) : img;
}
function lightbox(src) {
  const box = el('div', { class: 'lightbox', onclick: () => box.remove() }, el('img', { src, alt: '' }));
  document.body.append(box);
}
// pictures are shrunk before they travel: a phone photo is several MB
function shrink(f) {
  return new Promise((resolve, reject) => {
    const img = new Image();
    const url = URL.createObjectURL(f);
    img.onload = () => {
      const k = Math.min(1, 1600 / Math.max(img.width, img.height));
      const c = document.createElement('canvas');
      c.width = Math.round(img.width * k); c.height = Math.round(img.height * k);
      c.getContext('2d').drawImage(img, 0, 0, c.width, c.height);
      URL.revokeObjectURL(url);
      resolve({ mediaType: 'image/jpeg', data: c.toDataURL('image/jpeg', 0.82).split(',')[1] });
    };
    img.onerror = () => { URL.revokeObjectURL(url); reject(new Error('not an image')); };
    img.src = url;
  });
}

// ---------------------------------------------------------------- markdown → DOM (text nodes only)
function markdown(text) {
  const frag = document.createDocumentFragment();
  for (const b of mdBlocks(text)) frag.append(mdBlock(b));
  return frag;
}
function mdBlock(b) {
  switch (b.t) {
    case 'code': return el('pre', {}, el('code', {}, b.text));
    case 'h': return el('div', { class: 'h h' + b.level }, inline(b.text));
    case 'list': return el(b.ordered ? 'ol' : 'ul', { start: b.ordered && b.start !== 1 ? b.start : null }, b.items.map((i) => el('li', { class: i.depth ? 'd' + i.depth : null }, inline(i.text))));
    case 'quote': return el('blockquote', {}, markdown(b.text));
    case 'table': return el('div', { class: 'tbl' }, el('table', {}, el('thead', {}, el('tr', {}, b.head.map((c) => el('th', {}, inline(c))))), el('tbody', {}, b.rows.map((r) => el('tr', {}, r.map((c) => el('td', {}, inline(c))))))));
    case 'hr': return el('hr');
    default: return el('p', {}, inline(b.text));
  }
}
function inline(text) {
  return mdInline(text).map((t) => (t.t === 'text' ? t.v : t.t === 'code' ? el('code', {}, t.v) : t.t === 'b' ? el('strong', {}, t.v) : t.t === 'i' ? el('em', {}, t.v)
    : t.t === 's' ? el('s', {}, t.v) : el('a', { href: t.href, target: '_blank', rel: 'noopener noreferrer' }, t.v)));
}

// ---------------------------------------------------------------- start
function start() {
  if (/pair=/.test(location.hash)) history.replaceState(null, '', location.pathname);   // the one-time code does not stay in the address
  conn.start();
  route();
  loadProjects().then(() => { if (S.view === 'list') paintList(); else if (chat) paintHeader(); }).catch(() => {});
}
async function boot(attempt = 0) {
  const m = /pair=([A-Za-z0-9-]+)/.exec(location.hash);
  if (m) {
    try {
      const r = await api('/api/pair', { code: m[1], name: store.get('name', '') || (/iPhone|iPad/.test(navigator.userAgent) ? 'iPhone' : /Android/.test(navigator.userAgent) ? 'Android phone' : 'Phone') });
      S.device = r.device;
      return start();
    } catch (e) { history.replaceState(null, '', location.pathname); return renderPair(e.message); }
  }
  try {
    const me = await api('/api/me');
    S.device = me.device;
    start();
  } catch (e) {
    if (e.status === 401) return renderPair();
    // NetPI restarting or out of reach is not "not paired": say so and keep trying
    const retry = () => { clearTimeout(bootTimer); boot(attempt + 1); };
    renderBootError(e, retry);
    bootTimer = setTimeout(retry, backoff(attempt + 1));
  }
}
let bootTimer = 0;
boot();
window.addEventListener('popstate', route);
document.addEventListener('visibilitychange', () => { if (!document.hidden) conn.wake(); });
window.addEventListener('pageshow', (e) => { if (e.persisted) conn.wake(); });
window.addEventListener('online', () => conn.now('The network is back'));
window.addEventListener('offline', () => paintConn());
// once a second: the running timers, the banner's countdown, and a stale "back again" note
setInterval(() => {
  if (document.hidden) return;
  const now = Date.now();
  for (const n of document.querySelectorAll('[data-since]')) {
    const v = duration(now - Number(n.dataset.since));
    n.textContent = n.textContent.startsWith(' · ') ? ' · ' + v : v;
  }
  if (conn.state !== 'live' || (conn.backAt && now - conn.backAt < 4000)) paintConn();
}, 1000);
