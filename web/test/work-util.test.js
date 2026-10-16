// The Work tab's pure helpers (plugins/NetPI.Work/ui/util.js): the row a running job keeps, a run's priority level, and
// the short forms of a model and a command.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { placeSlots, priorityLevel, shortModel, shortCommand, firstLine, upsert, agentDot } from '../../plugins/NetPI.Work/ui/util.js';

const job = (leaseId, extra = {}) => ({ leaseId, agentId: `a-${leaseId}`, since: '2026-10-14T10:00:00Z', ...extra });
const ids = (rows) => rows.map((r) => r?.leaseId ?? null);

test('placeSlots: an empty agent shows one empty row per instance', () => {
  assert.deepEqual(placeSlots([], [], 2), [null, null]);
});

test('placeSlots: new jobs take the first free rows', () => {
  assert.deepEqual(ids(placeSlots([], [job('x'), job('y')], 3)), ['x', 'y', null]);
});

test('placeSlots: a running job keeps its row when the one above it finishes', () => {
  const first = placeSlots([], [job('x'), job('y')], 2);
  assert.deepEqual(ids(placeSlots(first, [job('y')], 2)), [null, 'y']);
});

test('placeSlots: a new job fills the hole a finished one left', () => {
  const holed = placeSlots(placeSlots([], [job('x'), job('y')], 2), [job('y')], 2);
  assert.deepEqual(ids(placeSlots(holed, [job('y'), job('z')], 2)), ['z', 'y']);
});

test('placeSlots: the row shows the newest copy of its job', () => {
  const rows = placeSlots(placeSlots([], [job('x', { status: 'running' })], 1), [job('x', { status: 'yielded' })], 1);
  assert.equal(rows[0].status, 'yielded');
});

test('placeSlots: rows beyond the capacity last only while something runs in them', () => {
  const busy = placeSlots([], [job('x'), job('y'), job('z')], 3);
  const reduced = placeSlots(busy, [job('x'), job('z')], 1);
  assert.deepEqual(ids(reduced), ['x', null, 'z']);
  assert.deepEqual(ids(placeSlots(reduced, [job('x')], 1)), ['x']);
});

test('placeSlots: a job without a lease id is keyed by its agent and start', () => {
  const o = { agentId: 'w1', since: 's1' };
  const rows = placeSlots(placeSlots([], [o, { agentId: 'w2', since: 's2' }], 2), [{ agentId: 'w2', since: 's2' }], 2);
  assert.equal(rows[0], null);
  assert.equal(rows[1].agentId, 'w2');
});

test('priorityLevel follows RunPriority (low -100, normal 0, high 100, critical 200; requeued +50)', () => {
  assert.equal(priorityLevel(-100), 'low');
  assert.equal(priorityLevel(-1), 'low');
  assert.equal(priorityLevel(0), 'normal');
  assert.equal(priorityLevel(undefined), 'normal');
  assert.equal(priorityLevel('garbage'), 'normal');
  assert.equal(priorityLevel(50), 'normal');
  assert.equal(priorityLevel(100), 'high');
  assert.equal(priorityLevel(150), 'high');
  assert.equal(priorityLevel(200), 'critical');
  assert.equal(priorityLevel(250), 'critical');
});

test('shortModel drops the provider', () => {
  assert.equal(shortModel('aiproxy/qwen3.8-27b'), 'qwen3.8-27b');
  assert.equal(shortModel('qwen3.8-27b'), 'qwen3.8-27b');
  assert.equal(shortModel(''), '');
  assert.equal(shortModel(null), '');
});

test('shortCommand: the first line without the cd an agent puts in front', () => {
  assert.equal(shortCommand('cd /repo && npm test'), 'npm test');
  assert.equal(shortCommand('cd "C:\\a b" ; cd sub && dotnet   build\nmore'), 'dotnet build');
  assert.equal(shortCommand('\n\n  ls -la  '), 'ls -la');
  assert.equal(shortCommand(null), '');
});

test('firstLine: the first non-empty line, cut with an ellipsis', () => {
  assert.equal(firstLine('\n  \nhello\nworld'), 'hello');
  assert.equal(firstLine('x'.repeat(10), 5), 'xxxx…');
  assert.equal(firstLine(''), '');
});

test('upsert replaces by key, else puts the item first', () => {
  const list = [{ id: 1, v: 'a' }, { id: 2, v: 'b' }];
  assert.deepEqual(upsert(list, { id: 2, v: 'B' }), [{ id: 1, v: 'a' }, { id: 2, v: 'B' }]);
  assert.deepEqual(upsert(list, { id: 3, v: 'c' }).map((x) => x.id), [3, 1, 2]);
  assert.equal(upsert(list, null), list);
  assert.deepEqual(upsert(null, { id: 1 }), [{ id: 1 }]);
});

test('agentDot maps a failed agent to the error dot', () => {
  assert.equal(agentDot('failed'), 'error');
  assert.equal(agentDot('cancelled'), 'cancelled');
  assert.equal(agentDot('running'), 'running');
});

test('ACTIVE: the Work tab\'s copy of the host\'s busy run statuses', async () => {
  const { BUSY } = await import('../src/lib/runStatus.js');
  const { ACTIVE } = await import('../../plugins/NetPI.Work/ui/util.js');
  assert.deepEqual([...ACTIVE].sort(), [...BUSY].sort());
});
