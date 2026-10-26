// The phone page's pure parts (plugins/NetPI.Phone/wwwroot/phone/lib.js): the event-stream parser and reconnect timing,
// the message window merge (the race that once put a chat's oldest messages under its newest), the catch-up of a live
// answer, sends shown until the chat shows them, what messages and notices become, and markdown as blocks.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import {
  SseParser, backoff, mergeCatchUp, mergeMessages, lastSeq, settlePending, buildItems, setupFirst, noticeView, stopView,
  userFrom, effectiveCall, callSubject, liveApply, newLive, toolsApply, withLive, mdBlocks, mdInline, activityText, httpProblem,
} from '../../plugins/NetPI.Phone/wwwroot/phone/lib.js';

const msg = (id, role, parts, extra = {}) => ({ id, seq: id, role, parts, createdAt: '2026-10-26T10:00:00Z', ...extra });
const text = (t) => ({ type: 'text', text: t });

test('the SSE parser joins frames split across chunks, CRLF included, and reports comments', () => {
  const p = new SseParser();
  const out = [...p.push(': ok\n\ndata: {"a"'), ...p.push(':1}\n\n: hb\r'), ...p.push('\n\ndata: x\r\ndata: y\r\n\r\n'), ...p.push('data:z\n')];
  assert.deepEqual(out, [
    { kind: 'comment', text: 'ok' }, { kind: 'data', data: '{"a":1}' }, { kind: 'comment', text: 'hb' }, { kind: 'data', data: 'x\ny' },
  ]);
  assert.deepEqual(p.push('\n'), [{ kind: 'data', data: 'z' }], 'a frame is complete only at its blank line');
});

test('reconnect waits grow from half a second to ten and stay there', () => {
  const mid = () => 0.5;
  assert.deepEqual([1, 2, 3, 4, 5, 6, 9].map((n) => backoff(n, mid)), [500, 1000, 2000, 4000, 8000, 10000, 10000]);
  assert.ok(backoff(1, () => 0) >= 400 && backoff(1, () => 1) <= 600, 'jitter is ±20 %');
  assert.match(httpProblem(502), /restarting/, 'a 502 from the proxy is said as NetPI restarting or off');
});

test('a page merged into the window stays in seq order, one entry per message (the reload race)', () => {
  // the newest 3 were loaded; a read with afterSeq 0 that answered late brings the oldest ones
  const tail = [msg(8, 'user', [text('h')]), msg(9, 'assistant', [text('i')]), msg(10, 'user', [text('j')])];
  const oldest = [msg(1, 'user', [text('a')]), msg(2, 'assistant', [text('b')]), msg(9, 'assistant', [text('i, edited')])];
  const merged = mergeMessages(tail, oldest);
  assert.deepEqual(merged.map((m) => m.seq), [1, 2, 8, 9, 10]);
  assert.equal(merged.find((m) => m.id === 9).parts[0].text, 'i, edited', 'the newer copy of a message wins');
  assert.equal(lastSeq(merged), 10, 'the window still ends at its newest message');
  assert.equal(mergeMessages(tail, []), tail, 'nothing new: the same array');
});

test('the catch-up applies agent.stream\'s copy, then only what came after it', () => {
  const snap = { seq: 20, events: [{ type: 'stream.start', d: {}, seq: 11 }, { type: 'stream.delta', d: { text: 'Hel' }, seq: 12 }] };
  const held = [
    { type: 'stream.delta', data: { text: 'Hel' }, seq: 12 },      // in the copy: dropped
    { type: 'stream.delta', data: { text: 'lo' }, seq: 21 },       // after it: kept
    { type: 'stream.delta', data: { text: '?' } },                 // no seq: cannot be placed, dropped
    { type: 'agent.notice', data: { text: 'retrying' }, seq: 15 }, // not a stream event: kept
  ];
  const out = mergeCatchUp(snap, held);
  assert.deepEqual(out.map((e) => [e.type, e.seq]), [['stream.start', 11], ['stream.delta', 12], ['agent.notice', 15], ['stream.delta', 21]]);
  assert.equal(mergeCatchUp(null, held), held, 'no copy (an older listener): what arrived is what there is');
  let live = newLive();
  for (const e of out) live = liveApply(live, e.type, e.data ?? {});
  assert.equal(live.segs.map((s) => s.text).join(''), 'Hello');
});

test('a send stays shown until the chat stores it or the queue holds it; a failed one stays', () => {
  const pending = [
    { key: 'a', text: 'run the tests', state: 'sent', afterSeq: 5 },
    { key: 'b', text: 'and then deploy', state: 'sent', afterSeq: 5 },
    { key: 'c', text: 'oops', state: 'failed', afterSeq: 5 },
    { key: 'd', text: 'maybe', state: 'unsure', afterSeq: 5 },
    { key: 'e', text: 'old', state: 'sent', afterSeq: 9 },
  ];
  const messages = [msg(4, 'user', [text('old')]), msg(6, 'user', [text('run the tests')]), msg(7, 'notice', [text('maybe')], { meta: { kind: 'steer' } })];
  const queue = [{ id: 'q1', text: 'and then deploy', mode: 'queue' }];
  assert.deepEqual(settlePending(pending, messages, queue).map((p) => p.key), ['c', 'e'],
    'stored after the send (a, and d as a steer), queued (b): gone; failed (c) and one stored before the send (e): stay');
});

test('messages become items: steps across messages, call_tool as its tool, notices, steers, stops, a webhook\'s message', () => {
  const messages = [
    msg(1, 'user', [text('Alert!')], { meta: { source: 'system', webhook: 'w1', webhookName: 'Vakt alerts' } }),
    msg(2, 'assistant', [{ type: 'thinking', text: 'hmm' }, { type: 'tool_call', id: 'c1', name: 'call_tool', arguments: '{"name":"bash","arguments":{"command":"ls -la"}}' }]),
    msg(3, 'tool', [{ type: 'tool_result', callId: 'c1', content: 'ok', isError: false }]),
    msg(4, 'assistant', [{ type: 'tool_call', id: 'c2', name: 'read', arguments: '{"path":"a.txt"}' }, text('Done.')]),
    msg(5, 'notice', [text('More arrived')], { meta: { kind: 'steer' } }),
    msg(6, 'notice', [text('Left behind: 2 worktrees\nand more')], { meta: { kind: 'tidy' } }),
    msg(7, 'assistant', [text('partial')], { stopReason: 'error', meta: { error: 'HTTP 500 req_123' } }),
  ];
  const items = buildItems(messages);
  assert.deepEqual(items.map((i) => i.kind), ['user', 'steps', 'text', 'user', 'notice', 'text', 'status']);
  assert.equal(items[0].from, 'Webhook · Vakt alerts');
  const steps = items[1];
  assert.equal(steps.key, 'gk2.0', 'a group is keyed by its first step');
  assert.deepEqual(steps.steps.map((s) => s.kind === 'tool' ? s.call.name : 'think'), ['think', 'bash', 'read'], 'thinking and calls of two messages, one group');
  assert.equal(steps.steps[1].result.content, 'ok');
  assert.equal(steps.steps[2].result, null, 'a call without a result yet');
  assert.equal(callSubject(steps.steps[1].call), 'ls -la');
  assert.equal(items[3].tag, 'steered');
  assert.deepEqual([items[4].view.label, items[4].view.tone, items[4].view.preview], ['Left behind', 'warn', '2 worktrees'], 'the label is not repeated in the preview');
  assert.equal(items[6].view.detail, 'HTTP 500 req_123', 'the error and its id are shown');
});

test('notices and stops are labelled like the desktop\'s', () => {
  assert.equal(noticeView(msg(1, 'notice', [text('x')], { meta: { kind: 'agent-result', agentName: 'Worker 2' } })).label, 'Worker 2 finished');
  assert.equal(noticeView(msg(1, 'summary', [text('x')])).label, 'Summary of earlier conversation');
  assert.equal(noticeView(msg(1, 'notice', [text('x')], { meta: { kind: 'error' } })).tone, 'err');
  assert.equal(noticeView(msg(1, 'notice', [text('x')], { meta: { kind: 'brand-new' } })).label, 'Brand new', 'an unknown kind still gets a label');
  assert.equal(stopView(msg(1, 'assistant', [], { stopReason: 'stop' })), null);
  assert.equal(stopView(msg(1, 'assistant', [], { stopReason: 'aborted' })).text, 'Stopped');
  assert.equal(userFrom(msg(1, 'user', [], { meta: { source: 'system', schedule: 's', scheduleName: 'Weekly' } })), 'Schedule · Weekly');
  assert.equal(userFrom(msg(1, 'user', [])), null);
  assert.equal(effectiveCall({ name: 'call_tool', arguments: '{}' }).name, 'call_tool', 'call_tool without a name stays itself');
  assert.equal(activityText({ activity: 'tool: bash', status: 'running' }), 'Running bash');
  assert.equal(activityText({ status: 'queued' }), 'Waiting for a free agent');
});

test('the first turn\'s setup notices go before the first message, as the model reads them', () => {
  const m = [msg(1, 'user', [text('hi')]), msg(2, 'notice', [text('cwd')], { meta: { kind: 'project', setup: true } }), msg(3, 'notice', [text('skill')], { meta: { kind: 'skill' } }), msg(4, 'assistant', [text('yo')])];
  assert.deepEqual(setupFirst(m).map((x) => x.id), [2, 1, 3, 4]);
});

test('the live answer reads in arrival order and its calls join the last steps group', () => {
  let live = newLive();
  for (const [type, d] of [['stream.start', {}], ['stream.delta', { kind: 'thinking', text: 'think ' }], ['stream.delta', { kind: 'thinking', text: 'more' }],
    ['stream.delta', { kind: 'text', text: 'Looking' }], ['stream.tool', { callId: 'c9', name: 'call_tool' }]]) live = liveApply(live, type, d);
  assert.deepEqual(live.segs.map((s) => s.kind), ['think', 'text', 'tool']);
  const tools = toolsApply(new Map(), 'tool.start', { callId: 'c9', name: 'call_tool', arguments: '{"name":"grep","arguments":{"pattern":"x"}}', startedAt: 5 });
  const stored = buildItems([msg(1, 'assistant', [{ type: 'tool_call', id: 'c1', name: 'read', arguments: '{}' }])]);
  const items = withLive(stored, live, tools);
  assert.deepEqual(items.map((i) => i.kind), ['steps', 'text', 'steps']);
  assert.equal(items[0].key, 'gcc1', 'the stored group keeps its key while the live thinking joins it');
  assert.deepEqual(items[0].steps.map((s) => s.kind), ['tool', 'think']);
  assert.equal(items[2].steps[0].call.name, 'grep', 'a live call_tool is named by the tool it runs');
  toolsApply(tools, 'tool.end', { callId: 'c9', isError: true, durationMs: 40 });
  assert.equal(tools.get('c9').status, 'error');
  assert.equal(withLive(stored, liveApply(live, 'stream.reset', {}), tools), stored, 'a reset stream shows nothing yet');
});

test('markdown: blocks, lists that do not run together, tables, and only safe links', () => {
  const b = mdBlocks('# Title\n\npara\nline two\n- a\n  - b\n1. x\n2. y\n\n| a | b |\n|---|---|\n| 1 | 2 |\n\n```js\nconst x = 1;\n```\n> said\n---');
  assert.deepEqual(b.map((x) => x.t), ['h', 'p', 'list', 'list', 'table', 'code', 'quote', 'hr']);
  assert.equal(b[1].text, 'para\nline two');
  assert.deepEqual(b[2].items, [{ text: 'a', depth: 0 }, { text: 'b', depth: 1 }]);
  assert.equal(b[3].ordered, true);
  assert.deepEqual(b[4].rows, [['1', '2']]);
  assert.deepEqual(b[5], { t: 'code', lang: 'js', text: 'const x = 1;' });
  const i = mdInline('a **b** `c` my_var_name *d* [e](https://x.y) [f](javascript:alert(1)) see https://z.io/p.');
  assert.deepEqual(i.filter((t) => t.t !== 'text').map((t) => [t.t, t.v]), [['b', 'b'], ['code', 'c'], ['i', 'd'], ['a', 'e'], ['a', 'https://z.io/p']]);
  assert.ok(i.some((t) => t.t === 'text' && t.v.includes('my_var_name')), 'snake_case is not emphasis');
  assert.ok(i.some((t) => t.t === 'text' && t.v.includes('[f](javascript:alert(1))')), 'a javascript: link stays text');
});
