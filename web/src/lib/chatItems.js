// Chat rendering pipeline, step 1: ChatMessage[] (a window of the session) → render items.
//
//   user          { kind:'user', key, msg }
//   text          { kind:'text', key, msg, text, last }              assistant markdown (last = last text of msg)
//   steps         { kind:'steps', key, steps:[…], startMs, endMs }    consecutive thinking / tool rows
//     step        { kind:'thinking', key, part, msg } | { kind:'tool', key, call, result, resultMsg, msg }
//   images        { kind:'images', key, msg, images }                 assistant image parts
//   shown         { kind:'shown', key, call, result, msg }            an image the agent showed (show_image): not in a steps group
//   artifacts     { kind:'artifacts', key, files:[{ path, name, caption?, how }] }  the files the turn wrote, edited or handed over (artifact): chips at the end of the turn
//   ask           { kind:'ask', key, call, result, msg }              questions for the user (ask_user): a card of its own, not a step
//   plan          { kind:'plan', key, call, result, msg }             the plan the agent submitted (plan_submit): a card of its own
//   planenter     { kind:'planenter', key, call, result, msg }        the agent's offer of plan mode (plan_enter): a card of its own
//   schedule      { kind:'schedule', key, call, result, msg }         a schedule the agent proposed (schedules add): a card of its own (allow / discard)
//   notice        { kind:'notice', key, msg }                         role notice / summary
//   prompt        { kind:'prompt', key, prompt }                      a system prompt the session was sent (context.prompts):
//                                                                     the first at the top, a later one after the message it followed
//   status        { kind:'status', key, msg }                         assistant stopped with error/aborted/length
//
// Items keep their identity across rebuilds while their inputs are unchanged (messages are immutable
// objects: `message.updated` replaces the object), so keyed {#each} blocks only touch what changed.
import { toMs } from './format.js';
import { effectiveCall, liveEffectiveCall, parseArgs, pathArg, scheduleProposal } from './tools.js';

function same(a, b) {
  if (a === b) return true;
  if (!a || !b || a.length !== b.length) return false;
  for (let i = 0; i < a.length; i++) if (a[i] !== b[i]) return false;
  return true;
}

/**
 * The first turn's setup notices (meta.setup: working directory, instructions, the skill catalog; made at the chat's first
 * model call, stored after the first user message) are shown before that message, as the model reads them (ContextOrder
 * in the runtime). Other notices (a skill loaded with /skill:name) stay after it; notices stored before the first
 * message (an idea or a project the user added before sending) keep their place first.
 */
export function firstTurnNoticesFirst(messages) {
  const setup = (m) => m?.role === 'notice' && m.meta?.setup === true;
  const after = (m) => m?.role === 'notice' && !['steer', 'queued'].includes(m.meta?.kind);
  let first = 0;
  while (first < messages.length && messages[first]?.role === 'notice') first++;
  if (messages[first]?.role !== 'user') return messages;
  let end = first + 1;
  while (end < messages.length && after(messages[end])) end++;
  const run = messages.slice(first + 1, end);
  if (!run.some(setup)) return messages;
  return [...messages.slice(0, first), ...run.filter(setup), messages[first], ...run.filter((m) => !setup(m)), ...messages.slice(end)];
}

export function createItemBuilder() {
  let prev = new Map();

  /** atStart: the window begins with the session's first message (the first system prompt goes above it). */
  return function build(messages, prompts = [], atStart = false) {
    if (atStart) messages = firstTurnNoticesFirst(messages);
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
    // the first prompt only when the window starts at the session's start; a later one only when it falls inside the window
    const first = messages[0]?.seq ?? 0;
    const pending = prompts
      .filter((p) => (p.version === 1 ? atStart : atStart || p.afterSeq >= first - 1))
      .sort((a, b) => a.version - b.version);
    const promptItem = (p) => stable({ kind: 'prompt', key: `pr${p.version}`, prompt: p }, [p]);
    let steps = null;
    let groupKey = null;
    let startMs = 0;
    let endMs = 0;

    // files the current turn wrote, edited or handed over with the artifact tool, listed once the turn ends (a user message or the end)
    let files = new Map();
    let turnKey = '';
    const noteFile = (call, result, m) => {
      if (!result || result.isError) return;
      let path;
      let caption;
      let how;
      if (call.name === 'artifact') {
        path = result.details?.path ?? pathArg(parseArgs(call));
        caption = result.details?.caption;
        how = 'artifact';
      } else if (call.name === 'write' || call.name === 'edit') {
        path = pathArg(parseArgs(call));
        how = call.name === 'write' ? 'written' : 'edited';
      } else return;
      if (typeof path !== 'string' || !path) return;
      const key = path.replaceAll('\\', '/').toLowerCase();
      const old = files.get(key);
      if (!turnKey) turnKey = `${m.id}`;
      // an artifact call's caption wins; a file both written and edited keeps its first how unless handed over
      files.set(key, { path, name: path.split(/[\\/]/).pop(), caption: caption ?? old?.caption, how: old?.how ?? how });
    };
    const flushFiles = () => {
      if (!files.size) return;
      const list = [...files.values()];
      items.push(stable({ kind: 'artifacts', key: `f${turnKey}`, files: list }, list.flatMap((f) => [f.path, f.caption, f.how])));
      files = new Map();
      turnKey = '';
    };
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
      // a system prompt sent before this message (the first one: before everything)
      while (pending.length && (pending[0].version === 1 || pending[0].afterSeq < m.seq)) {
        flush();
        items.push(promptItem(pending.shift()));
      }
      const kindMeta = m.meta?.kind;
      switch (m.role) {
        case 'user':
          flush();
          flushFiles();
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
            let p = parts[i];
            if (p.type === 'thinking') {
              if (!p.text && !p.redacted) continue;
              addStep({ kind: 'thinking', key: `k${m.id}.${i}`, part: p, msg: m }, [p], m, i);
            } else if (p.type === 'tool_call') {
              // a call_tool call is the tool it ran: every card, view and count below goes by the inner name and arguments
              p = effectiveCall(p);
              const r = results.get(p.id);
              if (p.name === 'ask_user') {
                flush();
                items.push(stable({ kind: 'ask', key: `a${p.id}`, call: p, result: r?.part ?? null, msg: m }, [p, r?.part ?? null]));
              } else if (p.name === 'plan_submit' || p.name === 'plan_enter') {
                flush();
                items.push(stable({ kind: p.name === 'plan_submit' ? 'plan' : 'planenter', key: `${p.name === 'plan_submit' ? 'pl' : 'pe'}${p.id}`, call: p, result: r?.part ?? null, msg: m }, [p, r?.part ?? null]));
              } else if (p.name === 'schedules' && scheduleProposal(parseArgs(p))) {
                flush();
                items.push(stable({ kind: 'schedule', key: `sc${p.id}`, call: p, result: r?.part ?? null, msg: m }, [p, r?.part ?? null]));
              } else if (r && p.name === 'show_image' && !r.part.isError && r.part.details?.data) {
                flush();
                items.push(stable({ kind: 'shown', key: `v${p.id}`, call: p, result: r.part, msg: m }, [p, r.part]));
              } else if (r) {
                noteFile(p, r.part, m);
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
            items.push(stable({ kind: 'status', key: `st${m.id}`, msg: m }, [m]));
          }
          break;
        }
        case 'tool':
          // results are rendered with their calls; orphans (call outside the window) are skipped
          break;
        case 'notice':
        case 'summary':
          flush();
          if (kindMeta === 'steer' || kindMeta === 'queued') flushFiles();
          if (kindMeta === 'steer' || kindMeta === 'queued') items.push(stable({ kind: 'user', key: `u${m.id}`, msg: m }, [m]));
          else items.push(stable({ kind: 'notice', key: `n${m.id}`, msg: m }, [m]));
          break;
        default:
          break;
      }
    }
    flush();
    flushFiles();
    // sent after the last loaded message (the window starts at the session's first message, or a later version)
    for (const p of pending) if (!messages.length || p.afterSeq >= messages[messages.length - 1].seq) items.push(promptItem(p));
    prev = next;
    return items;
  };
}

/**
 * The message being streamed, as render items laid out the way the finished message will be: thinking and tool calls
 * are steps that join the open steps group, text is a text item, in the order they arrived (stream.segments: a provider
 * that runs its own loop streams text, tool, text… in one call, idea-xg7ayy). Tool steps carry their final key
 * (c<callId>), so their rows stay in place when message.added replaces the stream; nothing changes height at that moment.
 *   stream step  { kind:'thinking', key:'k.stream.<i>', part, msg:null, stream: <its timing> } | { kind:'tool', …, preparing:true }
 *   stream text  { kind:'text', key:'t.stream.<i>', text, msg:null, last, stream, done } (done: a later segment follows it)
 *   stream ask   { kind:'ask', key:'a<callId>', call, result:null, msg:null, preparing:true }  (ask_user: its card, not a step)
 */
export function withStream(items, stream, live = null) {
  if (!stream?.active || (!stream.thinking && !stream.text.trim() && !stream.tools.length)) return items;
  const out = items.slice();
  // the artifacts chips after the last steps group are hidden while the run is live: they must not keep the stream's
  // steps from joining that group (they come back when the message is stored)
  if (out.at(-1)?.kind === 'artifacts' && out.at(-2)?.kind === 'steps') out.pop();
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
  const segs = stream.segments ?? [];
  const lastText = segs.findLastIndex((s) => s.kind === 'text' && s.text.trim());
  segs.forEach((s, i) => {
    if (s.kind === 'thinking') {
      push({ kind: 'thinking', key: `k.stream.${i}`, part: { type: 'thinking', text: s.text }, msg: null, stream: s.timing });
      return;
    }
    if (s.kind === 'text') {
      if (!s.text.trim()) return;
      flush();
      out.push({ kind: 'text', key: `t.stream.${i}`, text: s.text, msg: null, last: i === lastText, stream, done: i !== segs.length - 1 });
      return;
    }
    const t = s;
    // a call_tool call is named by the tool it runs once tool.start has said which (the stream only knows call_tool)
    const call = liveEffectiveCall({ type: 'tool_call', id: t.callId, name: t.name }, t.name === 'call_tool' ? live?.get(t.callId) : null);
    if (call.name === 'ask_user') {
      flush();
      out.push({ kind: 'ask', key: `a${t.callId}`, call, result: null, msg: null, preparing: true });
    } else if (call.name === 'plan_submit' || call.name === 'plan_enter') {
      flush();
      out.push({ kind: call.name === 'plan_submit' ? 'plan' : 'planenter', key: `${call.name === 'plan_submit' ? 'pl' : 'pe'}${t.callId}`, call, result: null, msg: null, preparing: true });
    } else push({ kind: 'tool', key: `c${t.callId}`, call, result: null, resultMsg: null, msg: null, preparing: true });
  });
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
