// The Webhooks tab's pure helpers (plugins/NetPI.Webhooks/ui/model.js): a row's state, the form's agent and hooks and
// back, and the lines the row and the detail show.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import {
  rowState, agentToForm, formToAgent, limitsLine, hookToForm, formToHook, hookLine, incidentLine, curlLine,
} from '../../plugins/NetPI.Webhooks/ui/model.js';

test('rowState: off is paused, or proposed when an agent added it', () => {
  assert.equal(rowState({ enabled: false }).key, 'paused');
  const proposed = rowState({ enabled: false, proposedBy: 'session:x' });
  assert.equal(proposed.key, 'proposed');
  assert.equal(proposed.tone, 'warn');
});

test('rowState: the last delivery decides the dot', () => {
  const tone = (lastStatus, extra = {}) => rowState({ enabled: true, lastStatus, ...extra }).tone;
  assert.equal(tone('started'), 'ok');
  assert.equal(tone('appended'), 'ok');
  assert.equal(tone('held'), 'ok');
  assert.equal(tone('duplicate'), 'idle', 'a repeat is not an error');
  assert.equal(tone('skipped'), 'idle');
  assert.equal(tone('failed'), 'err');
  assert.equal(tone(undefined), 'ok');
  assert.equal(rowState({ enabled: true }).key, 'idle');
  assert.match(rowState({ enabled: true, lastStatus: 'failed', lastReason: 'boom' }).label, /boom/);
});

test('the agent choice round-trips through the form', () => {
  for (const a of ['', 'any', 'free', 'local']) assert.equal(formToAgent(agentToForm(a)), a);
  assert.deepEqual(agentToForm('a,b'), { agentChoice: '__list', agentList: 'a,b' });
  assert.equal(formToAgent({ agentChoice: '__list', agentList: ' a , b ,, ' }), 'a,b');
  assert.deepEqual(agentToForm(null), { agentChoice: '', agentList: '' });
});

test('limitsLine: the timeout and the fuse', () => {
  assert.equal(limitsLine({ timeoutMinutes: 15, maxChatsPerHour: 60 }), 'timeout 15 min · fuse 60/h');
  assert.equal(limitsLine({ timeoutMinutes: 0 }), 'no timeout');
});

test('a hook as form fields and back; an empty command clears it', () => {
  assert.deepEqual(hookToForm({ command: 'ssh', args: ['a', 'b'] }), { command: 'ssh', args: 'a\nb' });
  assert.deepEqual(hookToForm(null), { command: '', args: '' });
  assert.deepEqual(formToHook(' ssh ', 'a\n\n b '), { command: 'ssh', args: ['a', 'b'] });
  assert.deepEqual(formToHook('ssh', ''), { command: 'ssh' });
  assert.deepEqual(formToHook('  ', 'a'), {});
  assert.equal(hookLine({ command: 'ssh', args: ['a', 'b'] }), 'ssh a b');
  assert.equal(hookLine({ command: 'x' }), 'x');
});

test('incidentLine: an open conversation and one still collecting', () => {
  const now = Date.parse('2026-10-14T12:00:00Z');
  assert.equal(incidentLine({ key: 'nuc', state: 'open', alertCount: 1 }, now), 'nuc · open · 1 item');
  assert.equal(incidentLine({ key: 'nuc', state: 'open', alertCount: 3, awaiting: true }, now), 'nuc · open · 3 items · chat working');
  assert.equal(incidentLine({ key: 'nuc', state: 'open', verdict: 'OK' }, now), 'nuc · open · 1 item · verdict OK');
  const collecting = { key: 'x', state: 'collecting', alertCount: 2, startsAt: new Date(now + 30_000).toISOString() };
  assert.equal(incidentLine(collecting, now), 'x · collecting, chat starts in 30 s · 2 items');
  assert.equal(incidentLine({ ...collecting, waiting: 'no agent' }, now), 'x · waiting to start: no agent');
});

test('curlLine: a POST with the bearer token', () => {
  assert.equal(
    curlLine('http://host:7480/hook/', 'alerts', 'tok'),
    `curl -X POST http://host:7480/hook/alerts -H "Authorization: Bearer tok" -H "Content-Type: application/json" -d '{"message":"test"}'`,
  );
});
