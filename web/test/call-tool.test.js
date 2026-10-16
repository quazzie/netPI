// A deferred tool is called as call_tool { name, arguments } and stored that way; the chat shows it as the tool it ran
// (effectiveCall in src/lib/tools.js, applied where chatItems builds items).
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createItemBuilder, stepCounts, withStream } from '../src/lib/chatItems.js';
import { effectiveCall, parseArgs } from '../src/lib/tools.js';

let id = 0;
const wrap = (callId, name, args, asString = true) => ({
  type: 'tool_call',
  id: callId,
  name: 'call_tool',
  arguments: JSON.stringify({ name, arguments: asString ? args : JSON.stringify(args) }),
});
const assistant = (...parts) => ({ id: ++id, seq: id, role: 'assistant', createdAt: '2026-01-01T00:00:00Z', parts, stopReason: 'tool_use' });
const result = (callId, details = null, isError = false) => ({
  id: ++id, seq: id, role: 'tool', createdAt: '2026-01-01T00:00:01Z',
  parts: [{ type: 'tool_result', callId, name: 'call_tool', content: 'ok', isError, details }],
});
const build = (...messages) => createItemBuilder()(messages);

test('effectiveCall: inner name and arguments (object or JSON string), id kept, others untouched', () => {
  const c = effectiveCall(wrap('c1', 'todo_write', { items: ['a'] }));
  assert.equal(c.name, 'todo_write');
  assert.deepEqual(parseArgs(c), { items: ['a'] });
  assert.equal(c.id, 'c1');
  assert.equal(c.via, 'call_tool');
  assert.deepEqual(parseArgs(effectiveCall(wrap('c2', 'skill', { name: 'x' }, false))), { name: 'x' });
  const plain = { type: 'tool_call', id: 'c3', name: 'read', arguments: '{"path":"a"}' };
  assert.equal(effectiveCall(plain), plain);
  const broken = { type: 'tool_call', id: 'c4', name: 'call_tool', arguments: '{"x":1}' };
  assert.equal(effectiveCall(broken), broken);
  const w = wrap('c5', 'read', { path: 'a' });
  assert.equal(effectiveCall(w), effectiveCall(w), 'memoized: stable identity');
});

test('wrapped ask_user / plan_submit / plan_enter / show_image build their cards with the inner arguments', () => {
  const items = build(
    assistant(wrap('a1', 'ask_user', { questions: [{ q: 'x' }] }), wrap('a2', 'plan_submit', { plan: 'P' }), wrap('a3', 'plan_enter', { reason: 'r' })),
    result('a1'), result('a2'), result('a3'),
    assistant(wrap('a4', 'show_image', { source: 'x.png' })),
    result('a4', { data: 'AAA' }),
  );
  assert.deepEqual(items.map((i) => i.kind), ['ask', 'plan', 'planenter', 'shown']);
  assert.deepEqual(parseArgs(items[0].call), { questions: [{ q: 'x' }] });
  assert.deepEqual(parseArgs(items[1].call), { plan: 'P' });
  assert.deepEqual(parseArgs(items[2].call), { reason: 'r' });
  assert.deepEqual(parseArgs(items[3].call), { source: 'x.png' });
  assert.equal(items[0].call.id, 'a1');
});

test('a wrapped schedules add is a schedule card; a wrapped list is a plain step', () => {
  const items = build(
    assistant(wrap('s1', 'schedules', { action: 'add', prompt: 'p', cadence: { kind: 'daily' } })),
    result('s1'),
    assistant(wrap('s2', 'schedules', { action: 'list' })),
    result('s2'),
  );
  assert.deepEqual(items.map((i) => i.kind), ['schedule', 'steps']);
  assert.equal(parseArgs(items[0].call).action, 'add');
  assert.equal(items[1].steps[0].call.name, 'schedules');
});

test('wrapped write / edit / artifact produce artifact chips; stepCounts counts the inner name', () => {
  const items = build(
    assistant(wrap('w1', 'write', { path: 'a.txt', content: 'x' }), wrap('w2', 'edit', { path: 'b.txt' }), wrap('w3', 'artifact', { path: 'c.png' })),
    result('w1'), result('w2'), result('w3', { path: 'c.png', caption: 'cap' }),
    assistant(wrap('w4', 'write', { path: 'd.txt' })),
    result('w4', null, true),
  );
  const chips = items.find((i) => i.kind === 'artifacts');
  assert.deepEqual(chips.files.map((f) => [f.path, f.how, f.caption]), [
    ['a.txt', 'written', undefined],
    ['b.txt', 'edited', undefined],
    ['c.png', 'artifact', 'cap'],
  ]);
  const steps = items.find((i) => i.kind === 'steps');
  assert.deepEqual(stepCounts(steps.steps).tools, [{ name: 'write', count: 2 }, { name: 'edit', count: 1 }, { name: 'artifact', count: 1 }]);
});

test('a streaming call_tool call is the inner tool once tool.start named it', () => {
  const stream = { active: true, startedAt: 1, segments: [{ kind: 'tool', callId: 'p1', name: 'call_tool' }], text: '', thinking: '', tools: [{ callId: 'p1', name: 'call_tool' }] };
  const before = withStream([], stream, new Map());
  assert.equal(before[0].steps[0].call.name, 'call_tool');
  const live = new Map([['p1', { name: 'plan_submit', arguments: '{"plan":"P"}' }]]);
  const after = withStream([], stream, live);
  assert.equal(after[0].kind, 'plan');
  assert.equal(after[0].key, 'pp1');
  assert.deepEqual(parseArgs(after[0].call), { plan: 'P' });
});
