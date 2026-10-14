// The Schedules tab's pure helpers (plugins/NetPI.Schedules/ui/model.js): the row's state and labels, the form's cadence
// and back, and the skip reasons, read from the runner's own source so a reworded reason fails here.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import {
  cadenceToForm, formToCadence, cadenceChanged, describeNext, ago, rowState, reasonKind, reasonLabel, summarise,
} from '../../plugins/NetPI.Schedules/ui/model.js';

const NOW = Date.parse('2026-10-14T12:00:00Z');
const iso = (msFromNow) => new Date(NOW + msFromNow).toISOString();
const MIN = 60_000;
const HOUR = 60 * MIN;

test('rowState: the first match wins, in the order the row reads', () => {
  const none = new Set();
  assert.equal(rowState({ finishedAt: iso(-HOUR), enabled: true }, none, NOW).key, 'finished');
  assert.equal(rowState({ enabled: false, proposedBy: 'session:x' }, none, NOW).key, 'proposed');
  assert.equal(rowState({ enabled: false }, none, NOW).key, 'paused');
  assert.equal(rowState({ enabled: true, lastSessionId: 's1', lastStatus: 'failed' }, new Set(['s1']), NOW).key, 'running');
  assert.equal(rowState({ enabled: true, lastStatus: 'missed' }, none, NOW).key, 'missed');
  assert.equal(rowState({ enabled: true, lastStatus: 'ran', lastRunAt: iso(-3 * HOUR) }, none, NOW).label, 'ran 3 h ago');
  assert.equal(rowState({ enabled: true, nextRunAt: iso(-10 * MIN) }, none, NOW).key, 'overdue');
  assert.equal(rowState({ enabled: true }, none, NOW).key, 'idle');
});

test('rowState: a failed run carries a short reason, a skipped one its chip', () => {
  const failed = rowState({ enabled: true, lastStatus: 'failed', lastReason: 'x'.repeat(100) }, new Set(), NOW);
  assert.equal(failed.tone, 'error');
  assert.ok(failed.label.length <= 'failed: '.length + 40, failed.label);
  const skipped = rowState({ enabled: true, lastStatus: 'skipped', lastReason: 'the budget is exhausted (budget.status)' }, new Set(), NOW);
  assert.equal(skipped.label, 'skipped: budget');
});

test('describeNext: the row\'s right-hand label', () => {
  assert.equal(describeNext({ finishedAt: iso(0) }, NOW), 'finished');
  assert.equal(describeNext({ enabled: false }, NOW), 'paused');
  assert.equal(describeNext({ enabled: false, proposedBy: 'x' }, NOW), 'proposed');
  assert.equal(describeNext({ enabled: true }, NOW), '–');
  assert.equal(describeNext({ enabled: true, nextRunAt: 'not a date' }, NOW), '–');
  assert.equal(describeNext({ enabled: true, nextRunAt: iso(-5 * MIN) }, NOW), 'overdue');
  assert.equal(describeNext({ enabled: true, nextRunAt: iso(30_000) }, NOW), 'now');
  assert.equal(describeNext({ enabled: true, nextRunAt: iso(20 * MIN) }, NOW), 'in 20 m');
  assert.equal(describeNext({ enabled: true, nextRunAt: iso(3 * HOUR) }, NOW), 'in 3 h');
  assert.match(describeNext({ enabled: true, nextRunAt: iso(30 * HOUR) }, NOW), /^tomorrow \d\d:\d\d$/);
  assert.equal(describeNext({ enabled: true, nextRunAt: iso(5 * 24 * HOUR) }, NOW), 'in 5 d');
});

test('ago: a relative age, empty for nothing', () => {
  assert.equal(ago(null, NOW), '');
  assert.equal(ago('nope', NOW), '');
  assert.equal(ago(iso(-MIN), NOW), 'just now');
  assert.equal(ago(iso(-30 * MIN), NOW), '30 m ago');
  assert.equal(ago(iso(-2 * 24 * HOUR), NOW), '2 d ago');
});

test('the form\'s cadence round-trips, and an empty zone is left out', () => {
  for (const c of [{ kind: 'daily', time: '07:30', tz: 'Europe/Stockholm' }, { kind: 'every', minutes: 15 }, { kind: 'once', at: '2026-10-20T07:30:00.000Z' }]) {
    const back = formToCadence(cadenceToForm(c));
    assert.equal(cadenceChanged(c, back), false, JSON.stringify({ c, back }));
  }
  assert.deepEqual(formToCadence({ kind: 'daily', time: '', tz: '' }), { kind: 'daily', time: '09:00' });
  assert.deepEqual(formToCadence({ kind: 'every', minutes: '0' }), { kind: 'every', minutes: 60 });
  assert.deepEqual(formToCadence({ kind: 'every', minutes: '0.4' }), { kind: 'every', minutes: 1 });
});

test('cadenceChanged sees a real change only', () => {
  assert.equal(cadenceChanged({ kind: 'daily' }, { kind: 'daily', time: '09:00' }), false);
  assert.equal(cadenceChanged({ kind: 'daily', time: '09:00' }, { kind: 'daily', time: '09:00', tz: 'UTC' }), true);
  assert.equal(cadenceChanged({ kind: 'every', minutes: 5 }, { kind: 'every', minutes: '5' }), false);
  assert.equal(cadenceChanged({ kind: 'every', minutes: 5 }, { kind: 'daily' }), true);
  assert.equal(cadenceChanged({ kind: 'once', at: '2026-10-20T07:30:00Z' }, { kind: 'once', at: '2026-10-20T07:30:00.000Z' }), false);
});

test('summarise: one line of at most 80 characters', () => {
  assert.equal(summarise({ prompt: 'a\n  b' }), 'a b');
  assert.equal(summarise({ prompt: 'x'.repeat(100) }).length, 80);
  assert.equal(summarise(null), '');
});

// Every reason ScheduleRunner.CheckAsync can return (its `return "…"` and `return $"…"` lines, holes filled with a number) and the
// one for a start already under way: each has a chip of its own. A reason reworded on the server turns into "other".
test('reasonKind knows every reason the runner returns', () => {
  const src = fs.readFileSync(new URL('../../plugins/NetPI.Schedules/ScheduleRunner.cs', import.meta.url), 'utf8');
  const from = src.indexOf('Task<string?> CheckAsync(');
  assert.ok(from >= 0, 'ScheduleRunner.CheckAsync is not where this test reads it');
  const body = src.slice(from, src.indexOf('StartingReason', from));
  const reasons = [...body.matchAll(/return\s+\$?"([^"]+)"/g)].map((m) => m[1].replace(/\{[^}]+\}/g, '42'));
  reasons.push(/StartingReason\s*=\s*"([^"]+)"/.exec(src)[1]);
  assert.ok(reasons.length >= 9, `found ${reasons.length} reasons in ScheduleRunner.cs`);
  for (const r of reasons) assert.notEqual(reasonKind(r), 'other', r);
  assert.equal(reasonKind('some reworded reason'), 'other');
});

test('reasonKind sorts the reasons into their chips', () => {
  assert.equal(reasonKind('the previous run (ses_x) is still going'), 'running');
  assert.equal(reasonKind('the budget is exhausted (budget.status)'), 'budget');
  assert.equal(reasonKind('the agent local is unavailable: the model is not loaded'), 'agent');
  assert.equal(reasonKind('no agent is available'), 'agent');
  assert.equal(reasonKind('the project prj_1 no longer exists'), 'project');
  assert.equal(reasonKind('it already ran 48 times in the last 24 hours (schedules.maxRunsPerDay)'), 'cap');
  assert.equal(reasonKind('no agent runtime is running (the netpi.runtime plugin)'), 'runtime');
  assert.equal(reasonLabel('runtime'), 'no runtime');
  assert.equal(reasonLabel('running'), 'still running');
});
