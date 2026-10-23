// The events catchUpStream holds while it waits (web/src/lib/heldEvents.js): past the limit, consecutive deltas of one kind
// and consecutive output chunks of one call are merged, so a long wait does not build an unbounded array.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { holdEvent } from '../src/lib/heldEvents.js';

const fill = (n) => Array.from({ length: n }, (_, i) => ['message.added', { i }, i]);

test('below the limit every event is kept as it came', () => {
  const held = [];
  holdEvent(held, 'stream.delta', { kind: 'text', text: 'a' }, 1, 3);
  holdEvent(held, 'stream.delta', { kind: 'text', text: 'b' }, 2, 3);
  assert.equal(held.length, 2);
});

test('past the limit, deltas of one kind join the one before and keep the later seq', () => {
  const held = fill(4);
  holdEvent(held, 'stream.delta', { kind: 'text', text: 'a' }, 10, 3);
  holdEvent(held, 'stream.delta', { kind: 'text', text: 'b' }, 11, 3);
  holdEvent(held, 'stream.delta', { kind: 'thinking', text: 'c' }, 12, 3);
  assert.equal(held.length, 6);
  assert.deepEqual(held[4], ['stream.delta', { kind: 'text', text: 'ab' }, 11]);
  assert.deepEqual(held[5], ['stream.delta', { kind: 'thinking', text: 'c' }, 12]);
});

test('past the limit, output chunks of one call join, those of another call do not', () => {
  const held = fill(4);
  holdEvent(held, 'tool.output', { callId: 'c1', chunk: 'x' }, 10, 3);
  holdEvent(held, 'tool.output', { callId: 'c1', chunk: 'y' }, 11, 3);
  holdEvent(held, 'tool.output', { callId: 'c2', chunk: 'z' }, 12, 3);
  assert.equal(held.length, 6);
  assert.deepEqual(held[4], ['tool.output', { callId: 'c1', chunk: 'xy' }, 11]);
  assert.deepEqual(held[5], ['tool.output', { callId: 'c2', chunk: 'z' }, 12]);
});

test('past the limit, other events between two chunks keep them apart', () => {
  const held = fill(4);
  holdEvent(held, 'tool.output', { callId: 'c1', chunk: 'x' }, 10, 3);
  holdEvent(held, 'tool.end', { callId: 'c1' }, 11, 3);
  holdEvent(held, 'tool.output', { callId: 'c1', chunk: 'y' }, 12, 3);
  assert.deepEqual(held.slice(4).map((h) => h[0]), ['tool.output', 'tool.end', 'tool.output']);
});

test('the merge does not touch the event object it joined', () => {
  const held = fill(4);
  const first = { callId: 'c1', chunk: 'x' };
  holdEvent(held, 'tool.output', first, 10, 3);
  holdEvent(held, 'tool.output', { callId: 'c1', chunk: 'y' }, 11, 3);
  assert.equal(held.length, 5);
  holdEvent(held, 'tool.output', { callId: 'c1', chunk: 'z' }, 12, 3);
  assert.equal(held.length, 5);
  assert.equal(first.chunk, 'x');
});
