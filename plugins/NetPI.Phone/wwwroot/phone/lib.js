// The phone page's pure parts (no DOM, no network): the event-stream parser, reconnect timing, the message window merge,
// what a chat shows for its messages (items) and for the answer streaming in (the live state), and markdown as blocks.
// app.js draws them; web/test/phone-lib.test.js pins them.

export const isActive = (status) => status === 'running' || status === 'queued' || status === 'yielded';

// ---------------------------------------------------------------- the event stream

/** Server-sent events read from a fetch body: push text as it arrives, get back { kind: 'data', data } and { kind: 'comment', text }. */
export class SseParser {
  #buf = '';
  #data = [];
  push(text) {
    this.#buf += text;
    const out = [];
    for (;;) {
      const i = this.#buf.search(/[\r\n]/);
      if (i < 0) break;
      // a \r at the very end may be the first half of \r\n: wait for the next chunk
      if (this.#buf[i] === '\r' && i + 1 === this.#buf.length) break;
      const line = this.#buf.slice(0, i);
      this.#buf = this.#buf.slice(i + (this.#buf[i] === '\r' && this.#buf[i + 1] === '\n' ? 2 : 1));
      if (line === '') {
        if (this.#data.length) out.push({ kind: 'data', data: this.#data.join('\n') });
        this.#data = [];
      } else if (line.startsWith(':')) out.push({ kind: 'comment', text: line.slice(1).trim() });
      else {
        const c = line.indexOf(':');
        const field = c < 0 ? line : line.slice(0, c);
        let value = c < 0 ? '' : line.slice(c + 1);
        if (value.startsWith(' ')) value = value.slice(1);
        if (field === 'data') this.#data.push(value);
      }
    }
    return out;
  }
}

/** The wait before reconnect attempt n (1, 2, …): 0.5 s doubling to 10 s, ±20 % so phones do not come back in step. */
export function backoff(attempt, rand = Math.random) {
  const base = Math.min(10_000, 500 * 2 ** Math.max(0, attempt - 1));
  return Math.round(base * (0.8 + 0.4 * rand()));
}

/** What an HTTP status without a readable error body means here (a 502 is the reverse proxy: NetPI is restarting or off). */
export function httpProblem(status) {
  if (status === 502 || status === 504) return `NetPI is not answering (${status} from the proxy): it is restarting, or the PC is off`;
  if (status === 503) return 'NetPI is restarting (503)';
  if (status === 403) return 'This address may not use the phone page (403)';
  if (status === 429) return 'Too many failed attempts from this address: wait a few minutes (429)';
  if (status === 401) return 'This phone is not paired (401)';
  return `NetPI answered ${status}`;
}

/**
 * The events agent.stream's copy and the live stream both hold: the copy wins up to its seq, what came after it is applied
 * after it. A held stream or tool event without a seq cannot be placed, and the copy is newer than the request: dropped.
 */
export function mergeCatchUp(snap, held) {
  if (!snap) return held;
  const copied = (type) => type.startsWith('stream.') || type.startsWith('tool.');
  const copy = (snap.events ?? []).map((e) => ({ type: e.type, data: e.d ?? {}, seq: e.seq ?? 0, ts: e.ts }));
  const later = held.filter((e) => !copied(e.type) || (e.seq && e.seq > (snap.seq ?? 0)));
  return copy.concat(later).sort((a, b) => (a.seq || 0) - (b.seq || 0));
}

// ---------------------------------------------------------------- the message window

/** The window with a page merged in: one entry per message id (the newer copy wins), ascending by seq. */
export function mergeMessages(list, incoming) {
  if (!incoming?.length) return list;
  const byId = new Map(list.map((m) => [m.id, m]));
  for (const m of incoming) byId.set(m.id, m);
  return [...byId.values()].sort((a, b) => a.seq - b.seq);
}

export const lastSeq = (list) => (list.length ? list[list.length - 1].seq : 0);

const textOf = (m) => (m.parts ?? []).filter((p) => p.type === 'text').map((p) => p.text ?? '').join('\n');
const isUserish = (m) => m.role === 'user' || (m.role === 'notice' && (m.meta?.kind === 'steer' || m.meta?.kind === 'queued'));

/**
 * Sends shown in the chat until the chat itself shows them: a sent one (or one whose answer did not come back in time) goes
 * once a message the person wrote with its text is stored after the send, or the input waits in the queue, which shows it.
 * One that is sending or failed stays.
 */
export function settlePending(pending, messages, queue) {
  return pending.filter((p) => {
    if (p.state !== 'sent' && p.state !== 'unsure') return true;
    const text = (p.text ?? '').trim();
    const stored = messages.some((m) => m.seq > p.afterSeq && isUserish(m)
      && (text ? textOf(m).trim() === text : (m.parts ?? []).some((x) => x.type === 'image')));
    const waiting = text && queue.some((q) => (q.text ?? '').trim() === text);
    return !stored && !waiting;
  });
}

// ---------------------------------------------------------------- tool calls

export function parseArgs(call) {
  const a = call?.arguments;
  if (a && typeof a === 'object') return a;
  try {
    const v = JSON.parse(a || '{}');
    return v && typeof v === 'object' ? v : {};
  } catch {
    return {};   // still streaming, or not JSON
  }
}

/** A call_tool call is the tool it ran: its row goes by the inner name and arguments. */
export function effectiveCall(call) {
  if (!call || String(call.name).toLowerCase() !== 'call_tool') return call;
  const outer = parseArgs(call);
  const name = typeof outer.name === 'string' ? outer.name.trim() : '';
  if (!name) return call;
  return { ...call, name, arguments: outer.arguments ?? '{}', via: 'call_tool' };
}

/** The one argument that says what a call is about (its command, path, query…), on one line. */
export function callSubject(call) {
  const a = parseArgs(call);
  const v = a.command ?? a.path ?? a.file ?? a.files?.[0]?.path ?? a.query ?? a.pattern ?? a.url ?? a.description ?? a.task
    ?? a.action ?? Object.values(a).find((x) => typeof x === 'string');
  return typeof v === 'string' ? v.replace(/\s+/g, ' ').trim().slice(0, 140) : '';
}

// ---------------------------------------------------------------- what a message shows

// the desktop's notice kinds (NoticeRow.svelte): a label and a tone each
const KINDS = {
  project: { tone: 'info', label: 'Working directory' },
  instructions: { tone: 'info', label: 'Instructions' },
  skills: { tone: 'info', label: 'Skills' },
  skill: { tone: 'accent' },
  tools: { tone: 'info', label: 'Tools changed' },
  todo: { tone: 'info', label: 'Todo list' },
  goal: { tone: 'accent', label: 'Goal' },
  idea: { tone: 'accent', label: 'Idea from the backlog' },
  budget: { tone: 'warn', label: 'Budget' },
  profile: { tone: 'accent', label: 'Profile' },
  nudge: { tone: 'warn', label: 'Nudge' },
  loop: { tone: 'warn', label: 'Loop check' },
  'agent-message': { tone: 'accent' },
  'agent-result': { tone: 'ok' },
  compaction: { tone: 'info', label: 'Context compacted' },
  summary: { tone: 'info', label: 'Summary of earlier conversation' },
  retry: { tone: 'warn', label: 'Retry' },
  error: { tone: 'err', label: 'Error' },
  tidy: { tone: 'warn', label: 'Left behind' },
  'git-commit': { tone: 'info', label: 'Commit' },
  'workspace-retire': { tone: 'info', label: 'Worktree' },
  info: { tone: 'muted', label: 'Notice' },
};

/** A harness notice (role notice) or a compaction summary (role summary) as its row: { kind, tone, label, preview, text, readable }. */
export function noticeView(m) {
  const kind = m.role === 'summary' ? 'summary' : (m.meta?.kind ?? 'info');
  const k = KINDS[kind] ?? { tone: 'muted' };
  const text = textOf(m);
  const who = m.meta?.agentName ?? m.meta?.from ?? m.meta?.name ?? null;
  const label = k.label
    ?? (kind === 'agent-message' ? `Message from ${who ?? 'an agent'}`
      : kind === 'agent-result' ? `${who ?? 'Subagent'} finished`
        : kind === 'skill' ? (m.meta?.missing ? `No skill “${m.meta?.skill ?? ''}”` : `Skill: ${m.meta?.skill ?? ''}`)
          : kind.charAt(0).toUpperCase() + kind.slice(1).replace(/-/g, ' '));
  let first = (text.split('\n').find((l) => l.trim()) ?? '').trim();
  if (first.toLowerCase().startsWith(label.toLowerCase())) first = first.slice(label.length).replace(/^[\s:·-]+/, '');   // "Skills: …" under the label Skills
  const preview = first.length > 160 ? first.slice(0, 159) + '…' : first;
  return { kind, tone: k.tone, label, preview, text, readable: kind === 'agent-result' || kind === 'agent-message' || kind === 'summary' };
}

/** The footer of an assistant message that did not stop normally, or null. */
export function stopView(m) {
  const sr = m.stopReason;
  if (sr !== 'error' && sr !== 'aborted' && sr !== 'length' && sr !== 'content_filter') return null;
  const text = sr === 'aborted' ? 'Stopped' : sr === 'length' ? 'The answer hit the max-tokens limit'
    : sr === 'content_filter' ? 'Blocked by the provider’s content filter' : 'The model call failed';
  return { tone: sr === 'error' ? 'err' : sr === 'aborted' ? 'muted' : 'warn', text, detail: m.meta?.error ?? m.meta?.message ?? null };
}

/** Where a message the person did not type came from (a webhook, a schedule, an agent), or null. */
export function userFrom(m) {
  const meta = m.meta ?? {};
  if (!meta.source || meta.source === 'user') return null;
  if (meta.webhook) return 'Webhook' + (meta.webhookName ? ' · ' + meta.webhookName : '');
  if (meta.schedule) return 'Schedule' + (meta.scheduleName ? ' · ' + meta.scheduleName : '');
  if (String(meta.source).startsWith('agent:')) return 'From an agent';
  return meta.source === 'system' ? 'From NetPI' : String(meta.source);
}

/**
 * The first turn's setup notices (meta.setup) go before the first user message, as the model reads them (the desktop's
 * firstTurnNoticesFirst); other notices keep their place.
 */
export function setupFirst(messages) {
  const setup = (m) => m?.role === 'notice' && m.meta?.setup === true;
  const after = (m) => m?.role === 'notice' && m.meta?.kind !== 'steer' && m.meta?.kind !== 'queued';
  let first = 0;
  while (first < messages.length && messages[first]?.role === 'notice') first++;
  if (messages[first]?.role !== 'user') return messages;
  let end = first + 1;
  while (end < messages.length && after(messages[end])) end++;
  const run = messages.slice(first + 1, end);
  if (!run.some(setup)) return messages;
  return [...messages.slice(0, first), ...run.filter(setup), messages[first], ...run.filter((m) => !setup(m)), ...messages.slice(end)];
}

/**
 * Messages → what the chat shows, in order:
 *   { kind: 'user', key, msg, text, images, tag, from }   what the person (or a webhook, a schedule) wrote; tag: steered / queued
 *   { kind: 'text', key, text, msg }                      assistant markdown
 *   { kind: 'steps', key, steps }                         consecutive thinking and tool calls, across messages
 *     { kind: 'think', key, text } | { kind: 'tool', key, call, result }
 *   { kind: 'images', key, images, msg }                  pictures in an answer
 *   { kind: 'notice', key, msg, view }                    a harness notice or a summary
 *   { kind: 'status', key, msg, view }                    an answer that stopped with an error, aborted, cut off
 * A steps group is keyed by its first step, so it keeps its key (and whether it is open) while it grows.
 */
export function buildItems(messages, atStart = false) {
  if (atStart) messages = setupFirst(messages);
  const results = new Map();
  for (const m of messages) if (m.role === 'tool') for (const p of m.parts ?? []) if (p.type === 'tool_result') results.set(p.callId, p);
  const items = [];
  let steps = null;
  const flush = () => {
    if (steps) items.push(steps);
    steps = null;
  };
  const step = (s) => {
    if (!steps) steps = { kind: 'steps', key: 'g' + s.key, steps: [] };
    steps.steps.push(s);
  };
  for (const m of messages) {
    if (m.role === 'user' || (m.role === 'notice' && (m.meta?.kind === 'steer' || m.meta?.kind === 'queued'))) {
      flush();
      const tag = m.meta?.kind === 'steer' || m.meta?.delivery === 'steer' ? 'steered' : m.meta?.kind === 'queued' ? 'queued' : null;
      items.push({ kind: 'user', key: 'u' + m.id, msg: m, text: textOf(m), images: (m.parts ?? []).filter((p) => p.type === 'image'), tag, from: userFrom(m) });
    } else if (m.role === 'assistant') {
      const imgs = [];
      (m.parts ?? []).forEach((p, i) => {
        if (p.type === 'thinking') {
          if (p.text?.trim()) step({ kind: 'think', key: `k${m.id}.${i}`, text: p.text });
        } else if (p.type === 'tool_call') {
          const call = effectiveCall(p);
          step({ kind: 'tool', key: 'c' + p.id, call, result: results.get(p.id) ?? null });
        } else if (p.type === 'text') {
          if (!p.text?.trim()) return;
          flush();
          items.push({ kind: 'text', key: `t${m.id}.${i}`, text: p.text, msg: m });
        } else if (p.type === 'image') imgs.push(p);
      });
      if (imgs.length) {
        flush();
        items.push({ kind: 'images', key: 'i' + m.id, images: imgs, msg: m });
      }
      const view = stopView(m);
      if (view) {
        flush();
        items.push({ kind: 'status', key: 's' + m.id, msg: m, view });
      }
    } else if (m.role === 'notice' || m.role === 'summary') {
      flush();
      items.push({ kind: 'notice', key: 'n' + m.id, msg: m, view: noticeView(m) });
    }
    // role tool: the results are shown with their calls
  }
  flush();
  return items;
}

// ---------------------------------------------------------------- the answer streaming in

export const newLive = () => ({ active: false, ended: false, segs: [], startedAt: 0 });

/**
 * The live answer after one stream event: segments in the order they arrive (thinking, text, a tool call…), so the
 * answer reads as the stored message will. Returns a new object; the old one is left as it was.
 */
export function liveApply(live, type, d, now = Date.now()) {
  switch (type) {
    case 'stream.start':
      return { active: true, ended: false, segs: [], startedAt: now };
    case 'stream.delta': {
      const kind = d?.kind === 'thinking' ? 'think' : 'text';
      const t = d?.text ?? '';
      const base = live.active ? live : { ...newLive(), active: true, startedAt: now };
      if (!t) return base;
      const segs = base.segs.slice();
      const last = segs[segs.length - 1];
      if (last?.kind === kind) segs[segs.length - 1] = { ...last, text: last.text + t };
      else segs.push({ kind, text: t });
      return { ...base, segs };
    }
    case 'stream.tool': {
      const base = live.active ? live : { ...newLive(), active: true, startedAt: now };
      return { ...base, segs: [...base.segs, { kind: 'tool', callId: d?.callId, name: d?.name }] };
    }
    case 'stream.reset':
      return { ...live, segs: [] };
    case 'stream.end':
      return { ...live, ended: true };
    default:
      return live;
  }
}

/** The calls running now, from tool.start / tool.end: callId → { name, args, startedAt, status, durationMs }. Changes the map. */
export function toolsApply(tools, type, d, now = Date.now()) {
  if (!d?.callId) return tools;
  if (type === 'tool.start') tools.set(d.callId, { name: d.name, args: d.arguments, startedAt: d.startedAt ?? now, status: 'running' });
  else if (type === 'tool.end') {
    const t = tools.get(d.callId) ?? { name: d.name, startedAt: now };
    tools.set(d.callId, { ...t, status: d.isError ? 'error' : 'done', durationMs: d.durationMs ?? now - t.startedAt });
  }
  if (tools.size > 80) tools.delete(tools.keys().next().value);
  return tools;
}

/** The items with the live answer after them: its thinking and calls join the last steps group, its text is a text item. */
export function withLive(items, live, tools) {
  if (!live?.active || !live.segs.length) return items;
  const out = items.slice();
  let group = out[out.length - 1]?.kind === 'steps' ? out.pop() : null;
  let n = 0;
  const push = (s) => {
    group = group ? { ...group, steps: [...group.steps, s] } : { kind: 'steps', key: 'g.live.' + n++, steps: [s] };
  };
  const flush = () => {
    if (group) out.push(group);
    group = null;
  };
  live.segs.forEach((s, i) => {
    if (s.kind === 'think') push({ kind: 'think', key: 'k.live.' + i, text: s.text, live: true });
    else if (s.kind === 'text') {
      if (!s.text.trim()) return;
      flush();
      out.push({ kind: 'text', key: 't.live.' + i, text: s.text, live: true });
    } else {
      const t = tools?.get(s.callId);
      const call = effectiveCall({ type: 'tool_call', id: s.callId, name: t?.name ?? s.name, arguments: t?.args ?? '{}' });
      push({ kind: 'tool', key: 'c' + s.callId, call, result: null, live: true });
    }
  });
  flush();
  return out;
}

// ---------------------------------------------------------------- markdown (as data; app.js makes text nodes of it)

const FENCE = /^\s{0,3}(`{3,}|~{3,})\s*([\w+#.-]*)/;
const BULLET = /^(\s*)[-*+]\s+(.*)$/;
const NUMBER = /^(\s*)(\d{1,9})[.)]\s+(.*)$/;
const RULE = /^\s{0,3}([-*_])(\s*\1){2,}\s*$/;
const TABLE_SEP = /^\s*\|?\s*:?-{2,}:?\s*(\|\s*:?-{2,}:?\s*)*\|?\s*$/;
const cells = (line) => line.trim().replace(/^\|/, '').replace(/\|$/, '').split('|').map((c) => c.trim());

/**
 * Markdown → blocks: { t: 'code', lang, text } | { t: 'h', level, text } | { t: 'p', text } | { t: 'list', ordered, start,
 * items: { text, depth }[] } | { t: 'quote', text } | { t: 'table', head, rows } | { t: 'hr' }. The text of a block is
 * inline markdown (mdInline).
 */
export function mdBlocks(src) {
  const lines = String(src ?? '').replace(/\r\n?/g, '\n').split('\n');
  const out = [];
  const starts = (l, next) => FENCE.test(l) || /^\s{0,3}#{1,6}\s/.test(l) || RULE.test(l) || /^\s{0,3}>/.test(l) || BULLET.test(l) || NUMBER.test(l)
    || (l.includes('|') && next !== undefined && TABLE_SEP.test(next));
  for (let i = 0; i < lines.length;) {
    const line = lines[i];
    const fence = FENCE.exec(line);
    if (fence) {
      const body = [];
      i++;
      while (i < lines.length && !lines[i].trimStart().startsWith(fence[1])) body.push(lines[i++]);
      i++;
      out.push({ t: 'code', lang: fence[2] || '', text: body.join('\n') });
      continue;
    }
    if (!line.trim()) { i++; continue; }
    const h = /^\s{0,3}(#{1,6})\s+(.*?)\s*#*\s*$/.exec(line);
    if (h) { out.push({ t: 'h', level: h[1].length, text: h[2] }); i++; continue; }
    if (RULE.test(line)) { out.push({ t: 'hr' }); i++; continue; }
    if (/^\s{0,3}>/.test(line)) {
      const body = [];
      while (i < lines.length && /^\s{0,3}>/.test(lines[i])) body.push(lines[i++].replace(/^\s{0,3}>\s?/, ''));
      out.push({ t: 'quote', text: body.join('\n') });
      continue;
    }
    if (line.includes('|') && i + 1 < lines.length && TABLE_SEP.test(lines[i + 1])) {
      const head = cells(line);
      const rows = [];
      i += 2;
      while (i < lines.length && lines[i].includes('|') && lines[i].trim()) rows.push(cells(lines[i++]));
      out.push({ t: 'table', head, rows });
      continue;
    }
    const b = BULLET.exec(line);
    const n = NUMBER.exec(line);
    if (b || n) {
      const ordered = !b;
      const items = [];
      const base = (b ?? n)[1].length;
      while (i < lines.length) {
        const l = lines[i];
        const lb = BULLET.exec(l);
        const ln = NUMBER.exec(l);
        const m = lb ?? ln;
        const depth = m ? Math.max(0, Math.min(3, Math.round((m[1].length - base) / 2))) : 0;
        if (m && depth === 0 && !lb !== ordered) break;   // a numbered list right after a bulleted one is a list of its own
        if (m) {
          items.push({ text: lb ? lb[2] : ln[3], depth });
          i++;
        } else if (l.trim() && /^\s+/.test(l) && items.length) {
          items[items.length - 1].text += '\n' + l.trim();   // a continuation line of the item above
          i++;
        } else break;
      }
      out.push({ t: 'list', ordered, start: n ? Number(n[2]) : 1, items });
      continue;
    }
    const para = [line];
    i++;
    while (i < lines.length && lines[i].trim() && !starts(lines[i], lines[i + 1])) para.push(lines[i++]);
    out.push({ t: 'p', text: para.join('\n') });
  }
  return out;
}

/** Inline markdown → { t: 'text' | 'code' | 'b' | 'i' | 's', v } and { t: 'a', v, href } (http(s) and mailto links only). */
export function mdInline(text) {
  // no lookbehind (older Safari cannot parse it, and one syntax error is a blank page): an emphasis inside a word is put back as text below
  const re = /(`+)([^`]|[^`][\s\S]*?[^`])\1(?!`)|\[([^\]\n]+)\]\(([^)\s]+)(?:\s+"[^"]*")?\)|\*\*([^*\n]+?)\*\*|__([^_\n]+?)__|~~([^~\n]+?)~~|\*([^*\s](?:[^*\n]*[^*\s])?)\*|_([^_\s](?:[^_\n]*[^_\s])?)_|(https?:\/\/[^\s<>()]+[^\s<>().,;:!?'"\]])/g;
  const src = String(text ?? '');
  const out = [];
  const word = (c) => !!c && /\w/.test(c);
  const safe = (href) => (/^(https?:|mailto:)/i.test(href) ? href : null);
  let at = 0;
  const plain = (v) => {
    if (out[out.length - 1]?.t === 'text') out[out.length - 1].v += v;
    else out.push({ t: 'text', v });
  };
  for (const m of src.matchAll(re)) {
    if (m.index > at) plain(src.slice(at, m.index));
    const inWord = word(src[m.index - 1]) || word(src[m.index + m[0].length]);
    if (m[1]) out.push({ t: 'code', v: m[2].trim() || m[2] });
    else if (m[3]) {
      const href = safe(m[4]);
      if (href) out.push({ t: 'a', v: m[3], href });
      else plain(m[0]);
    } else if (m[5] ?? m[6]) out.push({ t: 'b', v: m[5] ?? m[6] });
    else if (m[7]) out.push({ t: 's', v: m[7] });
    else if (m[8] ?? m[9]) {
      if (inWord) plain(m[0]);   // snake_case_names, 2*3*4
      else out.push({ t: 'i', v: m[8] ?? m[9] });
    } else if (m[10]) out.push({ t: 'a', v: m[10], href: m[10] });
    at = m.index + m[0].length;
  }
  if (at < src.length) plain(src.slice(at));
  return out;
}

// ---------------------------------------------------------------- small formats

export function ago(iso, now = Date.now()) {
  const s = (now - new Date(iso).getTime()) / 1000;
  if (!(s >= 0)) return '';
  if (s < 60) return 'now';
  if (s < 3600) return Math.floor(s / 60) + ' min';
  if (s < 86400) return Math.floor(s / 3600) + ' h';
  return Math.floor(s / 86400) + ' d';
}

export function duration(ms) {
  const s = Math.max(0, Math.round(ms / 1000));
  if (s < 60) return s + ' s';
  if (s < 3600) return Math.floor(s / 60) + ' min ' + String(s % 60).padStart(2, '0') + ' s';
  return Math.floor(s / 3600) + ' h ' + String(Math.floor(s / 60) % 60).padStart(2, '0') + ' min';
}

export const tokens = (n) => (n >= 1e6 ? (n / 1e6).toFixed(1) + 'M' : n >= 1e3 ? Math.round(n / 1e3) + 'k' : String(n || 0));

/** A run's activity as a sentence: "tool: bash" → "Running bash". */
export function activityText(run) {
  const a = run?.activity ?? '';
  if (/^tool:\s*/.test(a)) return 'Running ' + a.replace(/^tool:\s*/, '');
  if (a) return a.charAt(0).toUpperCase() + a.slice(1);
  return run?.status === 'queued' ? 'Waiting for a free agent' : 'Working';
}
