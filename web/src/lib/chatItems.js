// Chat rendering pipeline, step 1: ChatMessage[] (a window of the session) → render items.
//
//   user          { kind:'user', key, msg }
//   text          { kind:'text', key, msg, text, last }              assistant markdown (last = last text of msg)
//   steps         { kind:'steps', key, steps:[…], startMs, endMs }    consecutive thinking / tool rows
//     step        { kind:'thinking', key, part, msg } | { kind:'tool', key, call, result, resultMsg, msg }
//   images        { kind:'images', key, msg, images }                 assistant image parts
//   shown         { kind:'shown', key, call, result, msg }            an image the agent showed (show_image): not in a steps group
//   notice        { kind:'notice', key, msg }                         role notice / summary
//   status        { kind:'status', key, msg }                         assistant stopped with error/aborted/length
//
// Items keep their identity across rebuilds while their inputs are unchanged (messages are immutable
// objects: `message.updated` replaces the object), so keyed {#each} blocks only touch what changed.
import { toMs } from './format.js';

function same(a, b) {
  if (a === b) return true;
  if (!a || !b || a.length !== b.length) return false;
  for (let i = 0; i < a.length; i++) if (a[i] !== b[i]) return false;
  return true;
}

export function createItemBuilder() {
  let prev = new Map();

  return function build(messages) {
    const next = new Map();
    const stable = (item, deps) => {
      const old = prev.get(item.key);
      if (old && same(old._deps, deps)) {
        next.set(item.key, old);
        return old;
      }
      item._deps = deps;
      next.set(item.key, item);
      return item;
    };

    // pair tool results with their calls
    const results = new Map();
    for (const m of messages) {
      if (m.role !== 'tool') continue;
      for (const p of m.parts) if (p.type === 'tool_result') results.set(p.callId, { part: p, msg: m });
    }

    const items = [];
    let steps = null;
    let groupKey = null;
    let startMs = 0;
    let endMs = 0;

    const flush = () => {
      if (!steps) return;
      items.push(stable({ kind: 'steps', key: groupKey, steps, startMs, endMs }, steps));
      steps = null;
    };
    const addStep = (step, deps, m, partIdx) => {
      if (!steps) {
        steps = [];
        groupKey = `g${m.id}.${partIdx}`;
        startMs = Infinity;
        endMs = 0;
      }
      const created = toMs(m.createdAt);
      const s = created - (m.durationMs ?? 0);
      if (s < startMs) startMs = s;
      if (created > endMs) endMs = created;
      steps.push(stable(step, deps));
    };

    for (const m of messages) {
      const kindMeta = m.meta?.kind;
      switch (m.role) {
        case 'user':
          flush();
          items.push(stable({ kind: 'user', key: `u${m.id}`, msg: m }, [m]));
          break;
        case 'assistant': {
          const parts = m.parts;
          let lastText = -1;
          for (let i = parts.length - 1; i >= 0; i--)
            if (parts[i].type === 'text' && parts[i].text?.trim()) {
              lastText = i;
              break;
            }
          let imgs = null;
          for (let i = 0; i < parts.length; i++) {
            const p = parts[i];
            if (p.type === 'thinking') {
              if (!p.text && !p.redacted) continue;
              addStep({ kind: 'thinking', key: `k${m.id}.${i}`, part: p, msg: m }, [p], m, i);
            } else if (p.type === 'tool_call') {
              const r = results.get(p.id);
              if (r && p.name === 'show_image' && !r.part.isError && r.part.details?.data) {
                flush();
                items.push(stable({ kind: 'shown', key: `v${p.id}`, call: p, result: r.part, msg: m }, [p, r.part]));
              } else if (r) {
                addStep(
                  { kind: 'tool', key: `c${p.id}`, call: p, result: r.part, resultMsg: r.msg, msg: m },
                  [p, r.part],
                  m,
                  i,
                );
                const rc = toMs(r.msg.createdAt);
                if (rc > endMs) endMs = rc;
              } else {
                addStep({ kind: 'tool', key: `c${p.id}`, call: p, result: null, resultMsg: null, msg: m }, [p, null], m, i);
              }
            } else if (p.type === 'text') {
              if (!p.text?.trim()) continue;
              flush();
              items.push(stable({ kind: 'text', key: `t${m.id}.${i}`, msg: m, text: p.text, last: i === lastText }, [m]));
            } else if (p.type === 'image') {
              (imgs ??= []).push(p);
            }
          }
          if (imgs) {
            flush();
            items.push(stable({ kind: 'images', key: `i${m.id}`, msg: m, images: imgs }, [m]));
          }
          const sr = m.stopReason;
          if (sr === 'error' || sr === 'aborted' || sr === 'length' || sr === 'content_filter') {
            flush();
            items.push(stable({ kind: 'status', key: `s${m.id}`, msg: m }, [m]));
          }
          break;
        }
        case 'tool':
          // results are rendered with their calls; orphans (call outside the window) are skipped
          break;
        case 'notice':
        case 'summary':
          flush();
          if (kindMeta === 'steer' || kindMeta === 'queued') items.push(stable({ kind: 'user', key: `u${m.id}`, msg: m }, [m]));
          else items.push(stable({ kind: 'notice', key: `n${m.id}`, msg: m }, [m]));
          break;
        default:
          break;
      }
    }
    flush();
    prev = next;
    return items;
  };
}

/**
 * The message being streamed, as render items laid out the way the finished message will be: thinking and tool calls
 * are steps that join the open steps group, text is a text item. Tool steps carry their final key (c<callId>), so their
 * rows stay in place when message.added replaces the stream; nothing changes height at that moment.
 *   stream step  { kind:'thinking', key:'k.stream', part, msg:null, stream } | { kind:'tool', …, preparing:true }
 *   stream text  { kind:'text', key:'t.stream', text, msg:null, last:true, stream }
 */
export function withStream(items, stream) {
  if (!stream?.active || (!stream.thinking && !stream.text.trim() && !stream.tools.length)) return items;
  const out = items.slice();
  let group = out.at(-1)?.kind === 'steps' ? out.pop() : null;
  let groups = 0; // groups the stream opens: thinking, then text, then tool calls makes two
  const push = (step) => {
    group = group
      ? { ...group, steps: [...group.steps, step] }
      : { kind: 'steps', key: `g.stream.${groups++}`, steps: [step], startMs: stream.startedAt, endMs: 0 };
  };
  const flush = () => {
    if (group) out.push(group);
    group = null;
  };
  if (stream.thinking) push({ kind: 'thinking', key: 'k.stream', part: { type: 'thinking', text: stream.thinking }, msg: null, stream });
  if (stream.text.trim()) {
    flush();
    out.push({ kind: 'text', key: 't.stream', text: stream.text, msg: null, last: true, stream });
  }
  for (const t of stream.tools)
    push({ kind: 'tool', key: `c${t.callId}`, call: { type: 'tool_call', id: t.callId, name: t.name }, result: null, resultMsg: null, msg: null, preparing: true });
  flush();
  return out;
}

/** Aggregate tool counts for a collapsed steps header: [{ name, count }] in first-seen order. */
export function stepCounts(steps) {
  const map = new Map();
  let thinking = 0;
  for (const s of steps) {
    if (s.kind === 'thinking') thinking++;
    else map.set(s.call.name, (map.get(s.call.name) ?? 0) + 1);
  }
  return { tools: [...map].map(([name, count]) => ({ name, count })), thinking };
}
