// The Autonomy tab's pure helpers (plugins/NetPI.Autonomy/ui/model.js): undo and refusal states, the count on the
// Refused view, and the rows' second lines.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { checkpointFiles, undoState, refusalState, openRefusals, whoLine, refusalWhy } from '../../plugins/NetPI.Autonomy/ui/model.js';

test('a journal row can be undone only when it kept a copy, and only once', () => {
  assert.equal(undoState({ id: 'j1' }), 'none');
  assert.equal(undoState({ checkpoint: { id: 'c1', path: 'a' } }), 'ready');
  assert.equal(undoState({ checkpoint: { id: 'c1', path: 'a', undoneAt: '2026-10-23T10:00:00Z' } }), 'undone');
  const many = { checkpoint: { files: [{ id: 'c1', path: 'a' }, { id: 'c2', path: 'b', undoneAt: 'x' }] } };
  assert.deepEqual(checkpointFiles(many).map((f) => f.path), ['a', 'b']);
  assert.equal(undoState(many), 'undone', 'one file undone is the whole change undone');
});

test('a refusal is grantable until granted; what outside text may never do is never', () => {
  assert.equal(refusalState({ grantable: true }), 'grantable');
  assert.equal(refusalState({ grantable: true, grantedMs: 1 }), 'granted');
  assert.equal(refusalState({ grantable: false, why: 'tainted' }), 'never');
  assert.equal(openRefusals([{ grantable: true }, { grantable: true, grantedMs: 1 }, { grantable: false }]), 1);
  assert.equal(openRefusals(null), 0);
});

test('the second line names the origin, and the chat only when its title says something else', () => {
  assert.equal(whoLine({ label: 'schedule nightly', title: 'Scheduled: nightly' }), 'schedule nightly');
  assert.equal(whoLine({ label: 'webhook Checkmk', title: 'Disk full on nas' }), 'webhook Checkmk · Disk full on nas');
  assert.equal(whoLine({ origin: 'user' }), 'user');
  assert.equal(refusalWhy({ why: 'ask', rule: 'ask: ^git push' }), 'asks first: ask: ^git push');
  assert.match(refusalWhy({ why: 'tainted', rule: 'browser: click' }), /outside text \(browser: click\)/);
});
