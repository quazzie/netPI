// UI test: the Schedules tab (idea-1y1174's UI half). Mounts the committed bundle with a stub ctx that
// answers schedules.*, runs.list, projects.list and agents.list, and checks the row states, the row actions,
// the edit form's update payload, the event-driven reload, and the narrow-panel overflow.
//
//   node tests/NetPI.E2E/ui/schedules-tab.mjs
// Prints one JSON line at the end: { ok, checks: [{name, ok, detail}], errors: [...] }.
import http from 'node:http';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { chromium } from 'playwright-core';
import { hostUi } from './host.mjs';
import { reasonKind } from '../../../plugins/NetPI.Schedules/ui/model.js';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../../..');
const bundle = fs.readFileSync(path.join(root, 'plugins/NetPI.Schedules/wwwroot/ui.js'));
const host = hostUi(root);

const now = Date.now();
const iso = (ms) => new Date(ms).toISOString();
const schedule = (o) => ({
  enabled: true,
  projectId: null,
  agent: '',
  createdAt: iso(now - 86_400_000),
  updatedAt: iso(now - 3_600_000),
  ...o,
});
const fixtures = () => [
  schedule({
    id: 'sch_ran', name: 'Ran one', prompt: 'Summarise the day', when: 'daily at 07:30 UTC',
    cadence: { kind: 'daily', time: '07:30', tz: 'UTC' },
    nextRunAt: iso(now + 3_600_000), lastRunAt: iso(now - 7_200_000), lastStatus: 'ran', lastSessionId: 'ses_ran',
  }),
  schedule({
    id: 'sch_skip', name: 'Skipped', prompt: 'p', when: 'every 60 min',
    cadence: { kind: 'every', minutes: 60 }, nextRunAt: iso(now + 3_600_000),
    lastStatus: 'skipped', lastReason: 'the budget is exhausted (budget.status)',
  }),
  schedule({
    id: 'sch_miss', name: 'Missed', prompt: 'p', when: 'daily at 07:30 UTC',
    cadence: { kind: 'daily', time: '07:30', tz: 'UTC' }, nextRunAt: iso(now + 3_600_000), lastStatus: 'missed',
  }),
  schedule({
    id: 'sch_live', name: 'Live', prompt: 'p', when: 'every 60 min',
    cadence: { kind: 'every', minutes: 60 }, nextRunAt: iso(now + 3_600_000),
    lastStatus: 'ran', lastSessionId: 'ses_live',
  }),
  schedule({
    id: 'sch_pause', name: 'Paused', prompt: 'p', enabled: false, when: 'every 60 min',
    cadence: { kind: 'every', minutes: 60 },
  }),
  schedule({
    id: 'sch_once', name: 'Once done', prompt: 'p', enabled: false, finishedAt: iso(now - 1_000),
    when: 'once at …', cadence: { kind: 'once', at: iso(now - 1_000) },
  }),
];

const page = (empty) => `<!doctype html><html><head>${host.head}<style>
  body {margin:0;background:#191919;color:#ddd;font-family:system-ui}
  .fixture-panel { position:fixed; inset:0 0 0 auto; width:300px; height:900px; overflow:auto; border-left:1px solid #333; z-index:9999; background:#191919 }
</style></head><body><div id="app" hidden></div><div class="fixture-panel"><div id="fixture"></div></div><script type="module">
import {mount} from '/ui.js';
window.__calls = [];
window.__listCalls = 0;
window.__subs = [];
window.__empty = ${empty};
const schedules = () => window.__empty ? [] : ${JSON.stringify(fixtures())};
const ctx = {
  on: (ev, fn) => { window.__subs.push([ev, fn]); return () => {}; },
  rpc: async (method, params) => {
    if (method === 'schedules.list') { window.__listCalls++; return schedules(); }
    if (method === 'runs.list') return [{ sessionId: 'ses_live', status: 'running' }];
    if (method === 'projects.list') return [{ id: 'prj_1', name: 'demo' }];
    if (method === 'agents.list') return [{ key: 'local', available: true, free: true }];
    window.__calls.push([method, params]);
    if (method === 'schedules.run') return { status: 'ran', sessionId: 'ses_new' };
    if (method === 'schedules.update') {
      const s = schedules().find((x) => x.id === params.id);
      return s ? { ...s, ...params } : null;
    }
    return null;
  },
  app: {
    get activeSessionId() { return 'ses_1'; },
    get activeProject() { return { id: 'prj_1' }; },
    onChange: () => () => {},
    insertText: () => {},
    toast: () => {},
    openSession: (id) => { window.__opened = id; },
  },
};
mount(document.getElementById('fixture'), ctx);
window.__fire = (ev, d) => window.__subs.filter(([e]) => e === ev).forEach(([, fn]) => fn(d ?? {}));
</script></body></html>`;

const server = http.createServer((req, res) => {
  if (host.serve(req, res)) return;
  res.setHeader('Content-Type', req.url === '/ui.js' ? 'text/javascript' : 'text/html');
  res.end(req.url === '/ui.js' ? bundle : page(req.url.includes('empty')));
});
await new Promise((resolve) => server.listen(0, '127.0.0.1', resolve));

const checks = [];
const check = (name, ok, detail = '') => {
  checks.push({ name, ok: !!ok, detail: String(detail) });
  console.log(`  ${ok ? 'ok  ' : 'FAIL'} ${name}${detail ? ` (${detail})` : ''}`);
};
const errors = [];
let browser;
try {
  try {
    browser = await chromium.launch({ args: ['--disable-features=msWindowTabManagerPublic'] });
  } catch {
    browser = await chromium.launch({ channel: 'msedge', args: ['--disable-features=msWindowTabManagerPublic'] });
  }
  const tab = await browser.newPage({ viewport: { width: 700, height: 1000 } });
  tab.on('pageerror', (e) => errors.push(String(e)));
  tab.on('console', (m) => { if (m.type() === 'error') errors.push(`[console] ${m.text()}`); });

  // --- the empty state
  await tab.goto(`http://127.0.0.1:${server.address().port}/empty`);
  await tab.waitForSelector('.schedules .np-empty', { timeout: 15_000 });
  check('the empty state says what a schedule is', (await tab.locator('.schedules .np-empty').innerText()).includes('No schedules yet'));
  check('the empty state offers a new schedule', (await tab.locator('.schedules button', { hasText: 'New schedule' }).count()) === 1);
  await tab.locator('.schedules button', { hasText: 'New schedule' }).click();
  await tab.waitForSelector('.form .f-actions button', { timeout: 5_000 });
  check('the form opens from the empty state', (await tab.locator('.form .f-title').innerText()) === 'New schedule');

  // --- the rows and their states
  await tab.goto(`http://127.0.0.1:${server.address().port}/`);
  await tab.waitForSelector('.schedules .sch', { timeout: 15_000 });
  await tab.waitForFunction(() => document.querySelectorAll('.schedules .sch').length === 6, null, { timeout: 5_000 });
  const row = (name) => tab.locator('.schedules .sch', { hasText: name });
  // the state label (ran/skipped/…) sits on the meta line, so read the whole row
  const labels = async (n) => (await row(n).innerText()).replace(/\n/g, ' ');
  check('a ran row shows its last run', (await labels('Ran one')).includes('ran'), await labels('Ran one'));
  check('a skipped row shows the budget chip', (await labels('Skipped')).includes('skipped: budget'), await labels('Skipped'));
  check('a missed row says missed', (await labels('Missed')).includes('missed'), await labels('Missed'));
  check('a live run shows running', (await labels('Live')).includes('running'), await labels('Live'));
  check('a paused row says paused', (await labels('Paused')).includes('paused'), await labels('Paused'));
  check('a finished once says finished', (await labels('Once done')).includes('finished'), await labels('Once done'));

  // --- pause calls schedules.update {id, enabled:false} (hover first: the actions are pointer-events:none until the row is hovered)
  const ranRow = row('Ran one');
  await ranRow.hover();
  await ranRow.locator('.np-hover-actions button').nth(1).click();
  await tab.waitForFunction(() => window.__calls.some(([m, p]) => m === 'schedules.update' && p.id === 'sch_ran' && p.enabled === false), null, { timeout: 5_000 });
  check('pause calls schedules.update {id, enabled:false}', true);

  // --- resume is disabled on a finished once
  check('resume is disabled on a finished once', (await row('Once done').locator('.np-hover-actions button').nth(1).isDisabled()) === true);

  // --- delete needs the confirm click
  const skipRow = row('Skipped');
  await skipRow.hover();
  const trash = skipRow.locator('.np-confirm');
  await trash.click();
  check('the first delete click arms it', (await trash.innerText()).includes('Delete?'));
  await trash.click();
  await tab.waitForFunction(() => window.__calls.some(([m, p]) => m === 'schedules.delete' && p.id === 'sch_skip'), null, { timeout: 5_000 });
  check('the second click deletes', true);

  // --- the edit form sends only what changed, and enabled explicitly
  const listBefore = await tab.evaluate(() => window.__listCalls);
  await ranRow.hover();
  await ranRow.locator('.np-hover-actions button').nth(2).click();
  await tab.waitForSelector('.form .f-actions button', { timeout: 5_000 });
  await tab.locator('.form textarea').fill('Summarise the day, better');
  await tab.locator('.form .f-actions button', { hasText: 'Save' }).click();
  await tab.waitForFunction(
    () => window.__calls.some(([m, p]) => m === 'schedules.update' && p.id === 'sch_ran' && p.prompt === 'Summarise the day, better'),
    null,
    { timeout: 5_000 },
  );
  const update = await tab.evaluate(() => window.__calls.filter(([m]) => m === 'schedules.update').at(-1)[1]);
  check('the edit update sends {id, prompt, enabled}', update.prompt === 'Summarise the day, better' && update.enabled === true, JSON.stringify(update));
  check('…and no cadence (unchanged)', !('cadence' in update), JSON.stringify(update));

  // --- a schedules.changed event reloads once
  await tab.evaluate(() => window.__fire('schedules.changed', {}));
  await tab.waitForFunction((lb) => window.__listCalls > lb, listBefore, { timeout: 5_000 });
  check('schedules.changed triggers a reload', true);

  // --- no horizontal overflow at 230px
  const narrow = await browser.newPage({ viewport: { width: 230, height: 900 } });
  narrow.on('pageerror', (e) => errors.push(String(e)));
  await narrow.goto(`http://127.0.0.1:${server.address().port}/`);
  await narrow.waitForSelector('.schedules .sch', { timeout: 15_000 });
  const overflow = await narrow.evaluate(() => {
    const p = document.querySelector('.fixture-panel');
    p.style.width = '230px';
    return document.documentElement.scrollWidth <= 231 && p.scrollWidth <= p.clientWidth + 1;
  });
  check('no horizontal overflow at 230px', overflow);
  await narrow.close();
} catch (e) {
  check('the fixture ran', false, String(e?.message ?? e));
} finally {
  await browser?.close();
  server.close();
}

// --- reasonKind against the literal strings ScheduleRunner.CheckAsync returns
const reasons = [
  ['the previous run (ses_x) is still going', 'running'],
  ['the budget is exhausted (budget.status)', 'budget'],
  ['the agent local does not exist', 'agent'],
  ['the agent local is disabled', 'agent'],
  ['the agent local is unavailable: the model is not loaded', 'agent'],
  ['no agent is available', 'agent'],
  ['the project prj_1 no longer exists', 'project'],
  ['it already ran 48 times in the last 24 hours (schedules.maxRunsPerDay)', 'cap'],
  ['no agent runtime is running (the netpi.runtime plugin)', 'runtime'],
  ['some reworded reason', 'other'],
];
for (const [r, want] of reasons) {
  check(`reasonKind: ${r}`, reasonKind(r) === want, `got ${reasonKind(r)}`);
}

const failed = checks.filter((c) => !c.ok);
console.log(JSON.stringify({ ok: failed.length === 0 && errors.length === 0, checks, errors }));
process.exit(failed.length === 0 && errors.length === 0 ? 0 : 1);
