// The chat header's origin and taint chips (web/src/lib/origin.js): read from the meta the webhook and schedule plugins
// write, inherited by subagents, null for the owner's own chats.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { chatOrigin } from '../src/lib/origin.js';

const sessions = new Map();
const add = (s) => (sessions.set(s.id, s), s);
const byId = (id) => sessions.get(id);

test("the owner's chats and their subagents have no origin chip", () => {
  const mine = add({ id: 'a', title: 'mine', meta: {} });
  const helper = add({ id: 'b', parentSessionId: 'a', meta: { autonomy: { tainted: true } } });
  assert.equal(chatOrigin(mine, byId), null);
  assert.equal(chatOrigin(helper, byId), null, 'taint alone is no origin: the owner watches their chats');
  assert.equal(chatOrigin({ id: 'x' }, byId), null, 'no meta at all');
});

test("a webhook's chat is tainted from the start; a schedule's after an external tool", () => {
  const hook = add({ id: 'w', title: 'Disk full', meta: { webhook: 'wh1', webhookName: 'Checkmk' } });
  assert.deepEqual(chatOrigin(hook, byId), { kind: 'webhook', name: 'Checkmk', label: 'webhook Checkmk', tainted: true, taintedBy: 'webhook payload' });
  const sched = add({ id: 's', title: 'Scheduled: nightly', meta: { schedule: 'sch1' } });
  assert.deepEqual(chatOrigin(sched, byId), { kind: 'schedule', name: 'nightly', label: 'schedule nightly', tainted: false, taintedBy: null });
  sched.meta.autonomy = { tainted: true, taintedBy: 'web_fetch' };
  assert.equal(chatOrigin(sched, byId).taintedBy, 'web_fetch');
  assert.equal(chatOrigin({ id: 'n', title: 'Other', meta: { schedule: 'sch9' } }, byId).name, 'sch9', 'the id when the title is not the runner\'s');
});

test('a subagent works for its parent\'s origin and is tainted by its own read or its parent\'s', () => {
  add({ id: 'root', title: 'Scheduled: backups', meta: { schedule: 'sch2' } });
  const child = add({ id: 'c', parentSessionId: 'root', meta: {} });
  const grandchild = add({ id: 'g', parentSessionId: 'c', meta: { autonomy: { tainted: true, taintedBy: 'browser' } } });
  assert.equal(chatOrigin(child, byId).label, 'schedule backups');
  assert.equal(chatOrigin(child, byId).tainted, false);
  assert.equal(chatOrigin(grandchild, byId).taintedBy, 'browser');
  assert.equal(chatOrigin({ id: 'o', parentSessionId: 'not-loaded' }, byId), null, 'a parent the page has not loaded: no chip');
});
