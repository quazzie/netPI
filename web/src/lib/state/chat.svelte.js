// Per-session chat state: a window of messages (paged, capped), the in-progress stream, live tool output,
// the input queue and transient notices. Stores are kept in a small LRU so recently used tabs switch
// instantly; only the active session is rendered.
import { SvelteMap } from 'svelte/reactivity';
import { rpc } from '../rpc.svelte.js';
import { nextFrame, flushNow } from '../frame.js';
import { draftImages, dropDraft, setDraftImages, touchDraft } from './drafts.svelte.js';

export const PAGE = 60;
export const CAP = 200; // max messages kept in the window (≈ DOM cap)
const SLACK = 20; // trim only when this far over the cap (avoids trimming on every message)
const OUTPUT_CAP = 200_000; // live tool output kept per call (tail)
const LRU_MAX = 5;

/** Live state of a running (or just finished) tool call, fed by tool.start/output/end. */
export class LiveTool {
  output = $state('');
  status = $state('running'); // running | done | error
  durationMs = $state(null);
  truncated = $state(false);
  startedAt = Date.now();
  name = '';
  label = '';
  #buf = '';
  #flush = () => {
    this.output = this.#buf;
  };

  constructor(d) {
    this.name = d?.name ?? '';
    this.label = d?.label ?? '';
  }
  append(chunk) {
    this.#buf += chunk;
    if (this.#buf.length > OUTPUT_CAP) {
      // keep the tail, starting at a line boundary
      let cut = this.#buf.length - OUTPUT_CAP;
      const nl = this.#buf.indexOf('\n', cut);
      if (nl > 0 && nl - cut < 2000) cut = nl + 1;
      this.#buf = this.#buf.slice(cut);
      this.truncated = true;
    }
    nextFrame(this.#flush);
  }
  end(d) {
    flushNow(this.#flush);
    this.status = d?.isError ? 'error' : 'done';
    this.durationMs = d?.durationMs ?? Date.now() - this.startedAt;
  }
}

/** The assistant message currently being streamed. */
export class StreamState {
  active = $state(false);
  ended = $state(false);
  text = $state('');
  thinking = $state('');
  tools = $state.raw([]); // [{ callId, name }]
  model = $state(null);
  agentId = null;
  startedAt = $state(0);
  thinkingStartedAt = $state(0);
  thinkingEndedAt = $state(0);
  #text = '';
  #thinking = '';
  #flush = () => {
    this.text = this.#text;
    this.thinking = this.#thinking;
  };

  start(d) {
    this.clear();
    this.active = true;
    this.model = d?.model ?? null;
    this.agentId = d?.agentId ?? null;
    this.startedAt = Date.now();
  }
  delta(d) {
    if (!this.active) this.start({});
    if (d.kind === 'thinking') {
      if (!this.thinkingStartedAt) this.thinkingStartedAt = Date.now();
      this.#thinking += d.text ?? '';
    } else {
      if (this.thinkingStartedAt && !this.thinkingEndedAt) this.thinkingEndedAt = Date.now();
      this.#text += d.text ?? '';
    }
    nextFrame(this.#flush);
  }
  tool(d) {
    if (!this.active) this.start({});
    if (this.thinkingStartedAt && !this.thinkingEndedAt) this.thinkingEndedAt = Date.now();
    this.tools = [...this.tools, { callId: d.callId, name: d.name }];
  }
  reset() {
    this.#text = '';
    this.#thinking = '';
    flushNow(this.#flush);
    this.text = '';
    this.thinking = '';
    this.tools = [];
    this.thinkingStartedAt = 0;
    this.thinkingEndedAt = 0;
  }
  end() {
    flushNow(this.#flush);
    if (this.thinkingStartedAt && !this.thinkingEndedAt) this.thinkingEndedAt = Date.now();
    this.ended = true;
  }
  clear() {
    this.#text = '';
    this.#thinking = '';
    flushNow(this.#flush);
    this.active = false;
    this.ended = false;
    this.text = '';
    this.thinking = '';
    this.tools = [];
    this.model = null;
    this.startedAt = 0;
    this.thinkingStartedAt = 0;
    this.thinkingEndedAt = 0;
  }
}

export class ChatStore {
  id;
  messages = $state.raw([]);
  hasMore = $state(false);
  hasNewer = $state(false); // window does not reach the latest message (after "load earlier" past the cap)
  newerCount = $state(0);
  loading = $state(false);
  loadingEarlier = $state(false);
  error = $state(null);
  loaded = $state(false);
  stale = false;
  stream = new StreamState();
  live = new SvelteMap(); // callId -> LiveTool
  queue = $state.raw([]);
  // the system prompts the session was sent, with their tools (context.prompts): the chat shows them as rows
  prompts = $state.raw([]);
  // the person's own queued inputs: a subagent's report or a harness notice waiting for the agent is internal, never a chip
  ownQueue = $derived(this.queue.filter((q) => (q.source ?? 'user') === 'user'));
  notice = $state(null); // { level, text, ts, kept } — kept: no timer, the answer or the run's end clears it
  pendingUser = $state.raw(null); // optimistic user message while agent.send is in flight
  expanded = new SvelteMap(); // UI memory: item key -> boolean
  // the user opened (true) or closed (false) the thinking of a streaming answer: later streams in this chat start that
  // way too, and so do the finished thinking rows of those answers; null = follow the "Expand thinking" preference
  liveThinkingOpen = $state(null);
  // composer: the text persists in localStorage (saveDraft); the image blobs live in the draft store
  // (drafts.svelte.js), app-lifetime and bounded there — the store only shows them, so both survive
  // this store's eviction
  draft = $state('');
  get images() {
    return draftImages(this.id);
  }
  set images(v) {
    setDraftImages(this.id, v);
  }
  // scroll memory (restored when the tab is shown again)
  scroll = null;

  #endTimer = 0;
  #noticeTimer = 0;
  #loadSeq = 0;
  #msgEvents = null; // message events that landed while a load was in flight (its page cannot have them)
  #queueRev = 0; // bumped by every queue change, so a snapshot taken before one cannot undo it

  constructor(id) {
    this.id = id;
    try {
      this.draft = localStorage.getItem(`netpi.draft.${id}`) ?? '';
    } catch {}
    // rebuilding the store is the session being (re)opened: keep a draft with blobs ahead of the eviction order
    if (draftImages(id).length) touchDraft(id);
  }

  saveDraft() {
    try {
      if (this.draft) localStorage.setItem(`netpi.draft.${this.id}`, this.draft);
      else localStorage.removeItem(`netpi.draft.${this.id}`);
    } catch {}
  }

  async load() {
    const seq = ++this.#loadSeq;
    this.loading = true;
    this.error = null;
    // The answer is the page the server read when the request went out, so a message published since then is
    // applied to the window first and that older page would drop it. Hold those events and apply them over the
    // page (in the finally) — appending is by id, so a message that is in both stays one entry.
    this.#msgEvents = [];
    const qrev = this.#queueRev; // the queue snapshot below must not undo a removal made while it was in flight
    try {
      const res = await rpc('sessions.messages', { id: this.id, limit: PAGE });
      if (seq !== this.#loadSeq) return;
      this.messages = res?.messages ?? [];
      this.hasMore = !!res?.hasMore;
      this.hasNewer = false;
      this.newerCount = 0;
      this.loaded = true;
      this.stale = false;
      this.scroll = null;
    } catch (e) {
      if (seq === this.#loadSeq) this.error = e?.message ?? String(e);
    } finally {
      // a load that started after this one holds the buffer now, and replays it over its own page
      if (seq === this.#loadSeq) {
        const held = this.#msgEvents;
        this.#msgEvents = null;
        this.loading = false;
        for (const [type, d] of held) this.handle(type, d);
      }
    }
    rpc('agent.queue', { sessionId: this.id }, { timeout: 8000 })
      .then((items) => {
        if (qrev === this.#queueRev && Array.isArray(items)) this.queue = items;
      })
      .catch(() => {});
    this.loadPrompts();
  }

  /** The queue with one of the person's own inputs gone (sent or dequeued): a snapshot taken before this must
   *  not bring it back — the chip's send button would then do nothing, the server having no such item. */
  dropQueued(id) {
    this.#queueRev++;
    this.queue = this.queue.filter((q) => q.id !== id);
  }

  /** The system prompts sent so far (none without the context plugin). */
  loadPrompts() {
    rpc('context.prompts', { sessionId: this.id }, { timeout: 8000 })
      .then((r) => (this.prompts = Array.isArray(r?.prompts) ? r.prompts : []))
      .catch(() => {});
  }

  async loadEarlier() {
    if (this.loadingEarlier || !this.hasMore || !this.messages.length) return false;
    this.loadingEarlier = true;
    try {
      const res = await rpc('sessions.messages', { id: this.id, beforeSeq: this.messages[0].seq, limit: PAGE });
      const older = (res?.messages ?? []).filter((m) => m.seq < this.messages[0].seq);
      let next = older.concat(this.messages);
      if (next.length > CAP) {
        next = next.slice(0, CAP);
        this.hasNewer = true;
      }
      this.messages = next;
      this.hasMore = !!res?.hasMore;
      return older.length > 0;
    } catch (e) {
      this.error = e?.message ?? String(e);
      return false;
    } finally {
      this.loadingEarlier = false;
    }
  }

  /** Drop the window and reload the latest page (used when the window no longer reaches the tail). */
  jumpToLatest() {
    return this.load();
  }

  #append(msg) {
    if (this.hasNewer) {
      this.newerCount++;
      return;
    }
    const msgs = this.messages;
    const idx = findIndexById(msgs, msg.id);
    let next;
    if (idx >= 0) {
      next = msgs.slice();
      next[idx] = msg;
    } else if (!msgs.length || msgs[msgs.length - 1].seq <= msg.seq) {
      next = msgs.concat(msg);
    } else {
      next = msgs.concat(msg).sort((a, b) => a.seq - b.seq);
    }
    if (next.length > CAP + SLACK) {
      next = next.slice(next.length - CAP);
      this.hasMore = true;
    }
    this.messages = next;
  }

  /** Scoped event for this session. */
  handle(type, d) {
    // the message window is read as a whole: what lands during a load is newer than the page it waits for
    if (this.#msgEvents && (type === 'message.added' || type === 'message.updated')) {
      this.#msgEvents.push([type, d]);
      return;
    }
    switch (type) {
      case 'message.added': {
        const m = d.message;
        if (!m) return;
        if (m.role === 'assistant' && this.liveThinkingOpen != null)
          (m.parts ?? []).forEach((p, i) => p.type === 'thinking' && this.expanded.set(`k${m.id}.${i}`, this.liveThinkingOpen));
        this.#append(m);
        if (m.role === 'assistant') {
          clearTimeout(this.#endTimer);
          this.stream.clear();
        } else if (m.role === 'user' || (m.role === 'notice' && m.meta?.kind === 'steer')) {
          this.pendingUser = null;
        }
        break;
      }
      case 'message.updated': {
        const m = d.message;
        if (!m) return;
        const idx = findIndexById(this.messages, m.id);
        if (idx >= 0) {
          const next = this.messages.slice();
          next[idx] = m;
          this.messages = next;
        }
        break;
      }
      case 'context.prompt':
        this.loadPrompts();
        break;
      case 'messages.compacted': {
        const upTo = d.upToSeq;
        this.messages = this.messages.map((m) => (m.seq <= upTo && !m.compacted ? { ...m, compacted: true } : m));
        break;
      }
      case 'stream.start':
        clearTimeout(this.#endTimer);
        this.stream.start(d);
        // a transient notice (e.g. "retrying in 3s…") is stale once the model streams again; a kept one waits for the answer
        if (this.notice && this.notice.level !== 'error' && !this.notice.kept) {
          clearTimeout(this.#noticeTimer);
          this.#noticeTimer = setTimeout(() => (this.notice = null), 1500);
        }
        break;
      case 'stream.delta':
        this.stream.delta(d);
        this.#answered();
        break;
      case 'stream.tool':
        this.stream.tool(d);
        this.#answered();
        break;
      case 'stream.reset':
        this.stream.reset();
        break;
      case 'stream.end':
        this.stream.end();
        // the final message.added normally replaces the stream block right away
        clearTimeout(this.#endTimer);
        this.#endTimer = setTimeout(() => this.stream.ended && this.stream.clear(), 2500);
        break;
      case 'tool.start': {
        this.#liveFor(d);
        break;
      }
      case 'tool.output': {
        // the start can be missed (a reconnect, an event lost): creating here, through the same capped path
        this.#liveFor(d).append(d.chunk ?? '');
        break;
      }
      case 'tool.end':
        this.live.get(d.callId)?.end(d);
        break;
      case 'agent.queue':
        this.#queueRev++;
        this.queue = d.items ?? [];
        break;
      case 'agent.notice': {
        // a compaction's banner is kept while the summary is written, and after one inside a run until the model answers
        // again (the summarizer and then the first token can each take minutes); a /compact in an idle chat has no turn
        // after it, so its result goes like any notice
        const kept = d.kind === 'compaction' && (d.phase === 'start' || (d.phase === 'done' && d.mode !== 'manual'));
        this.notice = { level: d.level ?? 'info', text: d.text ?? '', ts: Date.now(), kept };
        clearTimeout(this.#noticeTimer);
        if (!kept) this.#noticeTimer = setTimeout(() => (this.notice = null), d.level === 'error' ? 12_000 : 6_000);
        break;
      }
      default:
        break;
    }
  }

  /** The LiveTool behind a tool event, created on first sight: every creation goes through this one path, so the
   *  map (each LiveTool holds up to 200 KB of output) never grows past its cap — a tool.output alone could
   *  otherwise add entries without eviction. */
  #liveFor(d) {
    let t = this.live.get(d.callId);
    if (!t) {
      t = new LiveTool(d);
      this.live.set(d.callId, t);
      if (this.live.size > 60) this.live.delete(this.live.keys().next().value);
    }
    return t;
  }

  /** The model answers again: a kept banner (the compaction before this call) has done its job. */
  #answered() {
    if (this.notice?.kept) this.notice = null;
  }

  /** The agent run ended: drop any leftover transient state. */
  runEnded() {
    if (this.stream.active && this.stream.ended) this.stream.clear();
    if (this.notice && this.notice.level !== 'error') this.notice = null;
    for (const t of this.live.values()) if (t.status === 'running') t.status = 'done';
  }

  /** Reconcile the transient state against the server's truth for this session's run: a run that ended while we
   *  were disconnected never delivered stream.end, so an active stream under a finished run is stale and is
   *  cleared — a run that is still busy keeps streaming, and its live state stays. */
  reconcile(runBusy) {
    if (runBusy) return;
    if (this.stream.active) this.stream.clear();
    if (this.notice && this.notice.level !== 'error') this.notice = null;
    for (const t of this.live.values()) if (t.status === 'running') t.status = 'done';
  }

  dispose() {
    clearTimeout(this.#endTimer);
    clearTimeout(this.#noticeTimer);
  }
}

function findIndexById(msgs, id) {
  // new/updated messages are almost always near the end
  for (let i = msgs.length - 1; i >= 0; i--) if (msgs[i].id === id) return i;
  return -1;
}

// ------------------------------------------------------------------------------------------ LRU cache

const cache = new Map();

/** Get (or create) the store for a session and mark it most recently used. */
export function getChat(id) {
  let c = cache.get(id);
  if (c) {
    cache.delete(id);
    cache.set(id, c);
    return c;
  }
  c = new ChatStore(id);
  cache.set(id, c);
  if (cache.size > LRU_MAX) {
    for (const [k, v] of cache) {
      if (cache.size <= LRU_MAX) break;
      if (k === id || v.stream.active) continue; // keep streaming sessions
      v.dispose();
      cache.delete(k);
    }
  }
  return c;
}

/** Existing store or undefined (events for uncached sessions are ignored; they reload on open). */
export function peekChat(id) {
  return cache.get(id);
}

export function dropChat(id) {
  const c = cache.get(id);
  if (c) {
    c.dispose();
    cache.delete(id);
  }
  dropDraft(id); // the session is deleted: its draft blobs are released too
}

export function allChats() {
  return cache.values();
}
