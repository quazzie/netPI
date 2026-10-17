// NetPI phone page: plain JS, no build. Talks to the plugin's own listener only (/api/*).
'use strict';
const $app = document.getElementById('app');
const state = {
  device: null, sessions: [], projects: [], models: [], runs: new Map(), asks: [], guards: [],
  view: 'list', sessionId: null, stick: true, open: new Set(), messages: [], hasMore: false, live: null, images: [], online: false, error: '',
};
const store = { get: (k, d) => { try { return localStorage.getItem(k) ?? d; } catch { return d; } }, set: (k, v) => { try { localStorage.setItem(k, v); } catch { /* private mode */ } } };

// ---------------------------------------------------------------- helpers
function el(tag, attrs, ...kids) {
  const n = document.createElement(tag);
  for (const [k, v] of Object.entries(attrs || {})) {
    if (v == null || v === false) continue;
    if (k === 'class') n.className = v;
    else if (k.startsWith('on')) n.addEventListener(k.slice(2), v);
    else if (k === 'value') n.value = v;
    else n.setAttribute(k, v === true ? '' : v);
  }
  for (const c of kids.flat()) if (c != null && c !== false) n.append(c.nodeType ? c : document.createTextNode(String(c)));
  return n;
}
async function api(path, body) {
  const res = await fetch(path, { method: body === undefined ? 'GET' : 'POST', headers: body === undefined ? {} : { 'Content-Type': 'application/json' }, body: body === undefined ? undefined : JSON.stringify(body), credentials: 'same-origin' });
  let j = null; try { j = await res.json(); } catch { /* not json */ }
  if (res.status === 401) { state.device = null; closeEvents(); render(); throw new Error(j?.error?.message || 'not paired'); }
  if (!res.ok) throw new Error(j?.error?.message || res.statusText);
  return j;
}
const rpc = async (m, p) => (await api('/api/rpc/' + m, p || {})).result;
function ago(iso) {
  const s = (Date.now() - new Date(iso).getTime()) / 1000;
  if (!(s >= 0)) return ''; if (s < 60) return 'now'; if (s < 3600) return Math.floor(s / 60) + ' min'; if (s < 86400) return Math.floor(s / 3600) + ' h';
  return Math.floor(s / 86400) + ' d';
}
const debounce = (fn, ms) => { let t; return (...a) => { clearTimeout(t); t = setTimeout(() => fn(...a), ms); }; };

// ---------------------------------------------------------------- markdown (escaped: text nodes only)
function inline(text) {
  const out = [];
  text.split(/(`[^`\n]+`)/).forEach((part) => {
    if (part.length > 2 && part.startsWith('`') && part.endsWith('`')) out.push(el('code', {}, part.slice(1, -1)));
    else part.split(/(\*\*[^*\n]+\*\*)/).forEach((p) => { if (p.startsWith('**') && p.endsWith('**') && p.length > 4) out.push(el('b', {}, p.slice(2, -2))); else if (p) out.push(p); });
  });
  return out;
}
function markdown(text) {
  const box = document.createDocumentFragment();
  text.split(/```[^\n]*\n?/).forEach((chunk, i) => {
    if (i % 2 === 1) box.append(el('pre', {}, el('code', {}, chunk.replace(/\n$/, ''))));
    else chunk.split(/\n{2,}/).forEach((p) => { if (p.trim()) box.append(el('p', {}, inline(p.trim()))); });
  });
  return box;
}

// ---------------------------------------------------------------- data
const activeStatus = (s) => s === 'running' || s === 'queued' || s === 'yielded';
function waitsFor(id) { return { asks: state.asks.filter((a) => a.sessionId === id), guards: state.guards.filter((g) => g.sessionId === id) }; }
function sessionState(id) {
  const w = waitsFor(id);
  if (w.asks.length || w.guards.length) return 'wait';
  return activeStatus(state.runs.get(id)?.status) ? 'run' : 'idle';
}
async function loadList() {
  const [sessions, runs, asks, guards] = await Promise.all([
    rpc('sessions.list', { limit: 80 }), rpc('runs.list', {}).catch(() => []), rpc('ask.pending', {}).catch(() => []), rpc('guard.pending', {}).catch(() => []),
  ]);
  state.sessions = sessions || []; state.asks = asks || []; state.guards = guards || [];
  state.runs = new Map((runs || []).filter((a) => !a.isSubagent).map((a) => [a.sessionId, a]));
}
async function loadProjects() {
  if (!state.projects.length) state.projects = (await rpc('projects.list', {}).catch(() => [])) || [];
  if (!state.models.length) { const m = await rpc('models.list', {}).catch(() => null); state.models = Array.isArray(m) ? m : (m?.models || []); }
}
const refreshList = debounce(async () => { try { await loadList(); if (state.view === 'list') render(); else if (state.view === 'chat') renderCards(); } catch { /* shown by the connection dot */ } }, 400);

// Where the page is lives in the address (#chat=ID, #new, nothing for the list): a reload comes back to the same chat and the phone's back button works.
const openChat = (id) => navigate('chat=' + encodeURIComponent(id));
function navigate(h) { history.pushState({ app: 1 }, '', '#' + h); route(); }
function goBack() { if (history.state?.app) history.back(); else { history.replaceState(null, '', location.pathname); route(); } }
function route() {
  if (!state.device) return;
  const h = location.hash.slice(1); const m = /^chat=(.+)$/.exec(h);
  if (m) return showChat(decodeURIComponent(m[1]));
  if (h === 'new') return showNew();
  state.view = 'list'; state.sessionId = null; closeChatState(); render();
  loadList().then(() => { if (state.view === 'list') render(); }).catch(() => {});
}
async function showChat(id) {
  state.view = 'chat'; state.sessionId = id; state.messages = []; state.live = null; state.images = []; state.hasMore = false; render();
  await loadMessages(true);
}
async function loadMessages(initial) {
  const id = state.sessionId; if (!id) return;
  const page = initial ? await rpc('sessions.messages', { id, limit: 40 }) : await rpc('sessions.messages', { id, afterSeq: lastSeq(), limit: 200 });
  if (id !== state.sessionId) return;
  const got = page?.messages || [];
  if (initial) { state.messages = got; state.hasMore = !!page?.hasMore; } else { const seen = new Set(state.messages.map((m) => m.seq)); for (const m of got) if (!seen.has(m.seq)) state.messages.push(m); else state.messages[state.messages.findIndex((x) => x.seq === m.seq)] = m; if (page?.hasMore) return loadMessages(false); }
  renderMessages(initial);
}
const lastSeq = () => state.messages.reduce((a, m) => Math.max(a, m.seq), 0);
async function loadOlder() {
  const id = state.sessionId; const first = state.messages[0]; if (!first) return;
  const page = await rpc('sessions.messages', { id, beforeSeq: first.seq, limit: 40 });
  if (id !== state.sessionId) return;
  state.messages = [...(page?.messages || []), ...state.messages]; state.hasMore = !!page?.hasMore; renderMessages(false, true);
}

// ---------------------------------------------------------------- events
let source = null;
function openEvents() {
  if (source) return;
  source = new EventSource('/api/events');
  source.onopen = () => { state.online = true; paintDot(); refreshList(); if (state.view === 'chat') loadMessages(false).catch(() => {}); };
  source.onerror = () => { state.online = false; paintDot(); };   // EventSource reconnects by itself; onopen resyncs
  source.onmessage = (m) => { try { onEvent(JSON.parse(m.data)); } catch { /* ignore a bad frame */ } };
}
function closeEvents() { source?.close(); source = null; state.online = false; }
function onEvent(e) {
  const d = e.data || {}; const sid = e.sessionId || d.sessionId;
  switch (e.type) {
    case 'resync': refreshList(); if (state.view === 'chat') loadMessages(false).catch(() => {}); return;
    case 'agent.status': if (d.agent) { state.runs.set(d.agent.sessionId, d.agent); if (state.view === 'list') render(); else if (d.agent.sessionId === state.sessionId) { renderHeader(); renderComposer(); } } return;
    case 'ask.asked': state.asks = [...state.asks.filter((a) => a.id !== d.id), d]; break;
    case 'ask.closed': state.asks = state.asks.filter((a) => a.id !== d.id); break;
    case 'guard.asked': state.guards = [...state.guards.filter((g) => g.approvalId !== d.approvalId), d]; break;
    case 'guard.closed': state.guards = state.guards.filter((g) => g.approvalId !== d.approvalId); break;
    case 'session.created': case 'session.updated': case 'session.deleted': refreshList(); return;
    case 'agent.queue': if (sid === state.sessionId) { state.queued = d.items || []; renderComposer(); } return;
    case 'stream.start': if (sid === state.sessionId) { state.live = { text: '', think: '' }; renderLive(); } return;
    case 'stream.delta': if (sid === state.sessionId && state.live && (d.kind === 'text' || d.kind === 'thinking')) { state.live[d.kind === 'text' ? 'text' : 'think'] += d.text || ''; renderLive(); } return;
    case 'stream.reset': if (sid === state.sessionId) { state.live = null; renderLive(); } return;
    case 'stream.end': if (sid === state.sessionId) loadMessages(false).then(() => { state.live = null; renderLive(); }).catch(() => {}); return;
    case 'message.added': case 'message.updated': case 'messages.compacted': if (sid === state.sessionId) loadMessages(false).catch(() => {}); return;
    default: return;
  }
  if (state.view === 'chat') { renderCards(); renderComposer(); } else if (state.view === 'list') render();
}

// ---------------------------------------------------------------- views
function paintDot() { const d = document.getElementById('conn'); if (d) d.className = 'dot ' + (state.online ? 'on' : 'off'); }
function header(title, sub, back, right) {
  return el('header', { id: 'hdr' }, back ? el('button', { class: 'ghost', onclick: back, 'aria-label': 'Back' }, '‹') : null,
    el('h1', {}, title, sub ? el('span', { class: 'sub' }, sub) : null), right, el('span', { id: 'conn', class: 'dot ' + (state.online ? 'on' : 'off'), title: state.online ? 'live' : 'reconnecting' }));
}
function mount(...nodes) { $app.replaceChildren(...nodes.filter(Boolean)); }

function renderPair(msg) {
  const code = el('input', { placeholder: 'Pairing code', autocapitalize: 'characters', autocomplete: 'off', spellcheck: 'false' });
  const name = el('input', { placeholder: 'Name this phone', value: store.get('name', '') });
  const err = el('div', { class: 'err-text' }, msg || '');
  const go = async () => {
    try { err.textContent = ''; store.set('name', name.value); const r = await api('/api/pair', { code: code.value, name: name.value }); state.device = r.device; await start(); }
    catch (e) { err.textContent = e.message; }
  };
  mount(header('NetPI'), el('div', { class: 'pair' }, el('p', {}, 'Open the Phone tab in NetPI on your computer, press “Pair a phone”, and type the code here (or open its link).'), code, name, el('button', { class: 'primary', onclick: go }, 'Pair'), err));
}

function render() {
  if (!state.device) return renderPair();
  if (state.view === 'list') return renderList();
  if (state.view === 'new') return renderNew();
  if (state.view === 'chat') return renderChat();
}

function renderList() {
  const rank = { wait: 0, run: 1, idle: 2 };
  const rows = state.sessions.filter((s) => s.kind !== 'subagent').map((s) => ({ s, st: sessionState(s.id) }));
  rows.sort((a, b) => rank[a.st] - rank[b.st] || (b.s.pinned - a.s.pinned) || new Date(b.s.updatedAt) - new Date(a.s.updatedAt));
  const proj = (id) => state.projects.find((p) => p.id === id)?.name;
  const nodes = []; let last = null;
  const labels = { wait: 'Needs you', run: 'Running', idle: 'Chats' };
  for (const { s, st } of rows) {
    if (st !== last) { nodes.push(el('div', { class: 'group' }, labels[st])); last = st; }
    nodes.push(el('div', { class: 'row', onclick: () => openChat(s.id) }, el('span', { class: 'dot ' + st }),
      el('div', { class: 't' }, el('b', {}, (s.pinned ? '📌 ' : '') + (s.title || 'Untitled')), el('span', {}, [proj(s.projectId), state.runs.get(s.id)?.activity && st !== 'idle' ? state.runs.get(s.id).activity : null].filter(Boolean).join(' · '))),
      el('time', {}, ago(s.updatedAt))));
  }
  mount(header('Chats', null, null, el('button', { class: 'ghost', onclick: () => { loadList().then(render).catch(() => {}); }, 'aria-label': 'Refresh' }, '⟳')),
    el('main', {}, rows.length ? nodes : el('div', { class: 'empty' }, 'No chats yet.')), el('button', { class: 'fab', onclick: () => navigate('new'), 'aria-label': 'New chat' }, '+'));
}

async function showNew() { state.view = 'new'; render(); await loadProjects().catch(() => {}); if (state.view === 'new') render(); }
function renderNew() {
  const project = el('select', {}, el('option', { value: '' }, 'No project'), state.projects.map((p) => el('option', { value: p.id, selected: p.id === store.get('lastProject', '') }, p.name)));
  const model = el('select', {}, el('option', { value: '' }, 'Default model'), state.models.map((m) => { const ref = m.ref || m.id || m; return el('option', { value: ref }, m.displayName || ref); }));
  const text = el('textarea', { rows: 6, placeholder: 'What should it do?' });
  const err = el('div', { class: 'err-text' });
  const go = el('button', { class: 'primary', onclick: async () => {
    if (!text.value.trim()) return;
    go.disabled = true; err.textContent = '';
    try {
      store.set('lastProject', project.value);
      const s = await rpc('sessions.create', { projectId: project.value || undefined, model: model.value || undefined });
      await rpc('agent.send', { sessionId: s.id, text: text.value.trim() });
      await loadList().catch(() => {});
      history.replaceState({ app: 1 }, '', '#chat=' + encodeURIComponent(s.id)); route();   // back from the new chat goes to the list, not to this form
    } catch (e) { err.textContent = e.message; go.disabled = false; }
  } }, 'Start');
  mount(header('New chat', null, goBack), el('main', {}, el('div', { class: 'pair' }, project, model, text, go, err)));
}

function renderChat() {
  mount(el('div', { id: 'hdrbox' }), el('main', { id: 'scroll' }, el('div', { class: 'msgs', id: 'msgs' })), el('div', { id: 'cards' }), el('div', { id: 'composer', class: 'composer' }));
  state.stick = true; state.cardsSig = null; comp = null;
  const sc = document.getElementById('scroll');
  // "following" = the reader is at the bottom; a scroll up lets go, a scroll back down picks it up again. Layout growth never lets go.
  sc.addEventListener('scroll', () => { state.stick = sc.scrollHeight - sc.scrollTop - sc.clientHeight < 80; }, { passive: true });
  const ro = new ResizeObserver(() => { if (state.stick) pin(); });   // a taller message, a growing composer, the keyboard, a picture that loaded
  ro.observe(sc); ro.observe(document.getElementById('msgs'));
  renderHeader(); renderMessages(true); renderCards(); renderComposer();
}
function pin() { const sc = document.getElementById('scroll'); if (sc) sc.scrollTop = sc.scrollHeight; }
function renderHeader() {
  const box = document.getElementById('hdrbox'); if (!box) return;
  const s = state.sessions.find((x) => x.id === state.sessionId);
  const run = state.runs.get(state.sessionId);
  box.replaceChildren(header(s?.title || 'Chat', activeStatus(run?.status) ? (run.activity || 'working…') : null, goBack));
}
function closeChatState() { state.live = null; state.images = []; }

function renderMessages(stick, keepPos) {
  const box = document.getElementById('msgs'); const scroller = document.getElementById('scroll'); if (!box) return;
  const prevH = scroller.scrollHeight;
  const results = new Map();
  for (const m of state.messages) for (const p of m.parts || []) if (p.type === 'tool_result') results.set(p.callId, p);
  const nodes = [];
  if (state.hasMore) nodes.push(el('div', { class: 'more' }, el('button', { onclick: () => loadOlder().catch(() => {}) }, 'Load older')));
  for (const m of state.messages) {
    if (m.role === 'tool') continue;
    const kids = [];
    for (const p of m.parts || []) {
      if (p.type === 'text') { if (m.role === 'user') kids.push(p.text); else kids.push(markdown(p.text)); }
      else if (p.type === 'thinking') { if (p.text && p.text.trim()) kids.push(thinkNode(p.text, m.seq + ':' + kids.length)); }
      else if (p.type === 'image') kids.push(el('div', {}, '🖼 image'));
      else if (p.type === 'tool_call') kids.push(toolNode(p, results.get(p.id)));
    }
    if (!kids.length) continue;
    nodes.push(el('div', { class: 'msg ' + m.role }, kids));
  }
  nodes.push(el('div', { class: 'msg assistant', id: 'live' }));
  box.replaceChildren(...nodes);
  renderLive();
  if (keepPos) scroller.scrollTop += scroller.scrollHeight - prevH; else if (stick || state.stick) { state.stick = true; pin(); requestAnimationFrame(pin); }
}
function toolNode(call, res) {
  let args = ''; try { const a = JSON.parse(call.arguments || '{}'); args = a.command || a.path || a.query || a.url || a.description || Object.values(a).find((v) => typeof v === 'string') || ''; } catch { /* partial json */ }
  const sum = el('summary', { class: res?.isError ? 'err' : '' }, '⚙ ' + call.name + (args ? ' — ' + String(args).replace(/\s+/g, ' ').slice(0, 90) : ''));
  const body = res ? res.content.slice(0, 4000) + (res.content.length > 4000 ? '\n… (' + res.content.length + ' characters)' : '') : '(running…)';
  return el('details', { class: 'tool', open: state.open.has(call.id), ontoggle: (e) => { e.target.open ? state.open.add(call.id) : state.open.delete(call.id); } }, sum, el('pre', {}, body || '(no output)'));
}
function thinkNode(text, key) {
  const k = 'think:' + key;
  return el('details', { class: 'think', open: state.open.has(k), ontoggle: (e) => { e.target.open ? state.open.add(k) : state.open.delete(k); } },
    el('summary', {}, '💭 Thinking'), el('div', { class: 'thought' }, text));
}
function renderLive() {
  const live = document.getElementById('live'); if (!live) return;
  live.replaceChildren(...(state.live ? [state.live.think ? thinkNode(state.live.think, 'live') : null, state.live.text ? markdown(state.live.text) : null].filter(Boolean) : []));
  if (state.stick) pin();
}

function renderCards() {
  const box = document.getElementById('cards'); if (!box) return;
  const w = waitsFor(state.sessionId); const nodes = [];
  const sig = w.guards.map((g) => g.approvalId).join() + '|' + w.asks.map((a) => a.id).join();
  if (sig === state.cardsSig) return;   // an answer being typed into a card must survive unrelated events
  state.cardsSig = sig;
  for (const g of w.guards) {
    const send = (allow, scope) => rpc('guard.answer', { approvalId: g.approvalId, allow, scope }).then(() => { state.guards = state.guards.filter((x) => x.approvalId !== g.approvalId); renderCards(); renderComposer(); }).catch((e) => alert(e.message));
    nodes.push(el('div', { class: 'card' }, el('h3', {}, 'Approval needed · ' + g.tool), el('div', { class: 'subj' }, g.subject), g.rule ? el('div', { class: 'row-dim' }, 'rule: ' + g.rule) : null,
      g.opinion ? el('div', { class: 'row-dim' }, g.opinion.harmless ? 'Second opinion: looks harmless' : 'Second opinion: not clearly harmless') : null,
      el('div', { class: 'opts' }, el('button', { class: 'primary', onclick: () => send(true, 'once') }, 'Allow once'), el('button', { onclick: () => send(true, 'session') }, 'Allow for this chat'), el('button', { class: 'danger', onclick: () => send(false) }, 'Deny'))));
  }
  for (const a of w.asks) nodes.push(askCard(a));
  box.replaceChildren(...nodes);
}
function askCard(a) {
  const picks = (a.questions || []).map(() => new Set());
  const text = el('input', { placeholder: 'Or answer in your own words' });
  const qs = (a.questions || []).map((q, i) => el('div', {}, el('div', { class: 'q' }, q.question), el('div', { class: 'opts' }, (q.options || []).map((o) => {
    const b = el('button', { title: o.description || '', onclick: () => { if (q.multiple) { picks[i].has(o.label) ? picks[i].delete(o.label) : picks[i].add(o.label); b.classList.toggle('sel'); } else { picks[i] = new Set([o.label]); [...b.parentNode.children].forEach((c) => c.classList.toggle('sel', c === b)); } } }, o.label);
    return b; }))));
  const send = el('button', { class: 'primary', onclick: async () => {
    const answers = picks.map((s) => [...s]);
    if (!answers.some((x) => x.length) && !text.value.trim()) return;
    send.disabled = true;
    try { await rpc('ask.answer', { id: a.id, answers: answers.some((x) => x.length) ? answers : undefined, text: text.value.trim() || undefined }); state.asks = state.asks.filter((x) => x.id !== a.id); renderCards(); renderComposer(); }
    catch (e) { alert(e.message); send.disabled = false; }
  } }, 'Answer');
  return el('div', { class: 'card' }, el('h3', {}, (a.agentName || 'The agent') + ' asks'), qs, text, send);
}

// The composer is built once per chat and only updated afterwards: rebuilding the textarea on every event closed the keyboard.
let comp = null;
const isBusy = () => { const w = waitsFor(state.sessionId); return activeStatus(state.runs.get(state.sessionId)?.status) && !w.asks.length && !w.guards.length; };
function buildComposer(box) {
  const ta = el('textarea', { rows: 1, placeholder: 'Message', enterkeyhint: 'send' });
  ta.addEventListener('input', () => { ta.style.height = 'auto'; ta.style.height = Math.min(ta.scrollHeight, window.innerHeight * 0.4) + 'px'; });
  // the keyboard's key says "send", so it sends; Shift+Enter (a physical keyboard) is the new line
  let shift = false;
  ta.addEventListener('keydown', (e) => { shift = e.shiftKey; if (e.key === 'Enter' && !e.shiftKey && !e.isComposing) { e.preventDefault(); send.click(); } });
  ta.addEventListener('beforeinput', (e) => { if (e.inputType === 'insertLineBreak' && !shift) { e.preventDefault(); send.click(); } });   // soft keyboards that do not report the Enter key
  const file = el('input', { type: 'file', accept: 'image/*', style: 'display:none', onchange: async () => { for (const f of file.files) { try { state.images.push(await shrink(f)); } catch { /* not an image */ } } file.value = ''; renderComposer(); } });
  const send = el('button', { class: 'primary', onclick: async () => {
    const text = ta.value.trim(); if (!text && !state.images.length) return;
    send.disabled = true; state.stick = true;
    try {
      await rpc('agent.send', { sessionId: state.sessionId, text, images: state.images.length ? state.images : undefined, mode: isBusy() ? store.get('mode', 'queue') : 'auto' });
      state.images = []; ta.value = ''; ta.style.height = 'auto'; await loadMessages(false).catch(() => {}); renderComposer();
    } catch (e) { alert(e.message); }
    send.disabled = false;
  } }, 'Send');
  const modeBar = el('div', { class: 'mode' }); const thumbs = el('div', { class: 'thumbs' });
  box.replaceChildren(modeBar, thumbs, el('div', { class: 'bar' }, el('button', { class: 'ghost', onclick: () => file.click(), 'aria-label': 'Attach a picture' }, '📎'), file, ta, send));
  comp = { box, ta, modeBar, thumbs, sig: '' };
}
function renderComposer() {
  const box = document.getElementById('composer'); if (!box) return;
  if (!comp || comp.box !== box) buildComposer(box);
  const busy = isBusy(); const mode = store.get('mode', 'queue');
  comp.ta.placeholder = busy ? 'Message (' + mode + ')' : 'Message';
  const sig = (busy ? 'b' + mode : 'i') + '|' + state.images.length;
  if (sig === comp.sig) return;   // nothing the user can see changed: leave the buttons (and the keyboard) alone
  comp.sig = sig;
  comp.modeBar.hidden = !busy;
  comp.modeBar.replaceChildren(...(busy ? ['While it works:', ...['queue', 'steer'].map((m) => el('button', { class: mode === m ? 'on' : '', onclick: () => { store.set('mode', m); renderComposer(); } }, m === 'queue' ? 'Queue' : 'Steer')),
    el('span', { style: 'flex:1' }), el('button', { class: 'danger', onclick: () => { rpc('agent.abort', { sessionId: state.sessionId }).catch((e) => alert(e.message)); } }, 'Stop')] : []));
  comp.thumbs.hidden = !state.images.length;
  comp.thumbs.replaceChildren(...state.images.map((i) => el('img', { src: 'data:' + i.mediaType + ';base64,' + i.data })));
}
// pictures are shrunk before they travel: a phone photo is several MB
function shrink(f) {
  return new Promise((resolve, reject) => {
    const img = new Image(); const url = URL.createObjectURL(f);
    img.onload = () => {
      const k = Math.min(1, 1600 / Math.max(img.width, img.height)); const c = document.createElement('canvas');
      c.width = Math.round(img.width * k); c.height = Math.round(img.height * k); c.getContext('2d').drawImage(img, 0, 0, c.width, c.height);
      URL.revokeObjectURL(url); resolve({ mediaType: 'image/jpeg', data: c.toDataURL('image/jpeg', 0.82).split(',')[1] });
    };
    img.onerror = () => { URL.revokeObjectURL(url); reject(new Error('not an image')); };
    img.src = url;
  });
}

// ---------------------------------------------------------------- start
async function start() {
  if (/pair=/.test(location.hash)) history.replaceState(null, '', location.pathname);   // the one-time code does not stay in the address
  try { await Promise.all([loadList(), loadProjects()]); } catch { /* the list shows empty; the dot says why */ }
  openEvents(); route();
}
(async function boot() {
  const m = /pair=([A-Za-z0-9-]+)/.exec(location.hash);
  if (m) {
    try { const r = await api('/api/pair', { code: m[1], name: store.get('name', '') || (/iPhone|iPad/.test(navigator.userAgent) ? 'iPhone' : /Android/.test(navigator.userAgent) ? 'Android phone' : 'Phone') }); state.device = r.device; return start(); }
    catch (e) { history.replaceState(null, '', location.pathname); return renderPair(e.message); }
  }
  try { const me = await api('/api/me'); state.device = me.device; await start(); } catch { renderPair(); }
})();
window.addEventListener('popstate', route);
document.addEventListener('visibilitychange', () => { if (!document.hidden && state.device) { if (!source) openEvents(); refreshList(); if (state.view === 'chat') loadMessages(false).catch(() => {}); } });
