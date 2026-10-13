// UI test: the Webhooks tab. Mounts the committed bundle with a stub ctx that answers webhooks.*, projects.list and
// agents.list, and checks the receiver banner, the row states, the add form's payload and the one-time token box, the
// "try a payload" preview, pause and the two-step delete, the event-driven reload, and the narrow-panel overflow.
//
//   node tests/NetPI.E2E/ui/webhooks-tab.mjs
// Prints one JSON line at the end: { ok, checks: [{name, ok, detail}], errors: [...] }.
import http from 'node:http';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { chromium } from 'playwright-core';
import { hostUi } from './host.mjs';
import { rowState, agentToForm, formToAgent, incidentLine, limitsLine, hookLine, hookToForm, formToHook } from '../../../plugins/NetPI.Webhooks/ui/model.js';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../../..');
const bundle = fs.readFileSync(path.join(root, 'plugins/NetPI.Webhooks/wwwroot/ui.js'));
const host = hostUi(root);

const now = Date.now();
const iso = (ms) => new Date(ms).toISOString();
const hook = (o) => ({
  enabled: true, projectId: 'prj_1', agent: 'any', prompt: 'Alert: {{service}} on {{host}}', tokenHint: 'ab12', path: `/hook/${o.id}`,
  maxChatsPerHour: 60, createdAt: iso(now - 86_400_000), updatedAt: iso(now - 3_600_000), ...o,
});
const fixtures = () => [
  hook({ id: 'whk_ok', name: 'Checkmk', lastAt: iso(now - 600_000), lastStatus: 'started', lastSessionId: 'ses_a', timeoutMinutes: 10,
    pre: { command: 'python', args: ['pre.py'] }, post: { command: 'ssh', args: ['server', 'ha-alert-json'] } }),
  hook({ id: 'whk_skip', name: 'Skipping', lastAt: iso(now - 60_000), lastStatus: 'duplicate', lastReason: 'the chat for nuc was just told this' }),
  hook({ id: 'whk_fail', name: 'Failing', lastAt: iso(now - 60_000), lastStatus: 'failed', lastReason: 'the model is not loaded' }),
  hook({ id: 'whk_off', name: 'Paused', enabled: false }),
  hook({ id: 'whk_prop', name: 'Proposed', enabled: false, proposedBy: 'session:ses_1' }),
];

const page = (empty, receiver) => `<!doctype html><html><head>${host.head}<style>
  body {margin:0;background:#191919;color:#ddd;font-family:system-ui}
  .fixture-panel { position:fixed; inset:0 0 0 auto; width:340px; height:1000px; overflow:auto; border-left:1px solid #333; z-index:9999; background:#191919 }
</style></head><body><div id="app" hidden></div><div class="fixture-panel"><div id="fixture"></div></div><script type="module">
import {mount} from '/ui.js';
window.__calls = [];
window.__listCalls = 0;
window.__subs = [];
const receiver = ${JSON.stringify(receiver)};
const hooks = () => ${empty ? '[]' : JSON.stringify(fixtures())};
const ctx = {
  on: (ev, fn) => { window.__subs.push([ev, fn]); return () => {}; },
  rpc: async (method, params) => {
    if (method === 'webhooks.list') { window.__listCalls++; return hooks(); }
    if (method === 'webhooks.status') return receiver;
    if (method === 'projects.list') return [{ id: 'prj_1', name: 'homelab' }];
    if (method === 'agents.list') return [{ key: 'local', available: true, free: true }, { key: 'paid', available: true, free: false }];
    if (method === 'profiles.list') return { defaultProfile: 'coder', profiles: [{ id: 'coder', name: 'Coder' }, { id: 'sysadmin', name: 'Sysadmin' }] };
    if (method === 'webhooks.incidents') return [
      { webhookId: params.id, key: 'nuc', state: 'open', alertCount: 3, sessionId: 'ses_inc', verdict: 'noise' },
      { webhookId: params.id, key: 'server', state: 'collecting', alertCount: 1, startsAt: new Date(Date.now() + 40000).toISOString() },
    ];
    if (method === 'webhooks.history') return [{ webhookId: params.id, at: new Date().toISOString(), status: 'appended', reason: null, summary: 'nuc / x is OK' }];
    window.__calls.push([method, params]);
    if (method === 'webhooks.add') return { id: 'whk_new', name: params.name, path: '/hook/whk_new', tokenHint: 'wxyz', token: 'whk_SECRET_TOKEN_wxyz', ...params };
    if (method === 'webhooks.test' && !params.run) return { action: 'collect', skip: null, key: 'nuc', agent: 'any', title: 'nuc / Kernel errors is CRIT', prompt: 'Alert: Kernel errors on nuc', note: null };
    if (method === 'webhooks.test') return { status: 'started', sessionId: 'ses_test' };
    if (method === 'webhooks.update') return { ...hooks().find((x) => x.id === params.id), ...params };
    return true;
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

const listening = { enabled: true, listening: true, address: '192.168.1.170', port: 7432, url: 'http://192.168.1.170:7432/hook/' };
const off = { enabled: false, listening: false };

const server = http.createServer((req, res) => {
  if (host.serve(req, res)) return;
  res.setHeader('Content-Type', req.url === '/ui.js' ? 'text/javascript' : 'text/html');
  res.end(req.url === '/ui.js' ? bundle : page(req.url.includes('empty'), req.url.includes('off') || req.url.includes('empty') ? off : listening));
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
  const tab = await browser.newPage({ viewport: { width: 700, height: 1100 } });
  tab.on('pageerror', (e) => errors.push(String(e)));
  tab.on('console', (m) => { if (m.type() === 'error') errors.push(`[console] ${m.text()}`); });
  const base = `http://127.0.0.1:${server.address().port}`;

  // --- the empty state and the receiver being off
  await tab.goto(`${base}/empty`);
  await tab.waitForSelector('.webhooks .np-empty', { timeout: 15_000 });
  check('the empty state says what a webhook is', (await tab.locator('.webhooks .np-empty').innerText()).includes('No webhooks yet'));
  check('an off receiver says so and where to turn it on', (await tab.locator('.webhooks .recv').innerText()).includes('Receiver is off'));
  await tab.locator('.webhooks button', { hasText: 'New webhook' }).click();
  await tab.waitForSelector('.form .f-actions button', { timeout: 5_000 });
  check('the form opens from the empty state', (await tab.locator('.form .np-strong').first().innerText()) === 'New webhook');

  // --- the rows, the receiver URL
  await tab.goto(`${base}/`);
  await tab.waitForSelector('.webhooks .hook', { timeout: 15_000 });
  await tab.waitForFunction(() => document.querySelectorAll('.webhooks .hook').length === 5, null, { timeout: 5_000 });
  check('a listening receiver shows its URL', (await tab.locator('.webhooks .recv').innerText()).includes('http://192.168.1.170:7432/hook/'));
  const row = (name) => tab.locator('.webhooks .hook', { hasText: name });
  check('a started row has an ok dot', (await row('Checkmk').getAttribute('data-tone')) === 'ok');
  check('a failed row has an error dot', (await row('Failing').getAttribute('data-tone')) === 'err');
  check('a proposed row says an agent proposed it', (await row('Proposed').innerText()).includes('proposed by an agent'));
  check('the meta line shows the token tail and the timeout', (await row('Checkmk').innerText()).includes('token …ab12') && (await row('Checkmk').innerText()).includes('timeout 10 min'));
  check('a duplicate row is idle, not an error', (await row('Skipping').getAttribute('data-tone')) === 'idle');

  // --- the detail: URL, template, preview
  await row('Checkmk').locator('.row').click();
  await tab.waitForSelector('.webhooks .detail', { timeout: 5_000 });
  check('the detail shows where to POST', (await row('Checkmk').locator('.url').innerText()) === 'http://192.168.1.170:7432/hook/whk_ok');
  await row('Checkmk').locator('button', { hasText: 'Preview' }).click();
  await tab.waitForSelector('.webhooks .preview', { timeout: 5_000 });
  check('Preview shows what would happen and the filled prompt', (await row('Checkmk').locator('.preview').innerText()).includes('Would open the incident “nuc”'));
  check('the detail lists the incidents with their state', (await row('Checkmk').innerText()).includes('nuc · open · 3 alerts') && (await row('Checkmk').innerText()).includes('server · collecting, chat starts in'));
  await row('Checkmk').locator('.inc-open').first().click();
  check('the detail shows the before and after commands', (await row('Checkmk').innerText()).includes('python pre.py') && (await row('Checkmk').innerText()).includes('ssh server ha-alert-json'));
  check('an incident shows the agent\'s verdict', (await row('Checkmk').innerText()).includes('verdict noise'));
  check('an incident row opens its chat', (await tab.evaluate(() => window.__opened)) === 'ses_inc');
  const previewCall = await tab.evaluate(() => window.__calls.filter(([m]) => m === 'webhooks.test').at(-1)[1]);
  check('Preview sends run:false with the parsed payload', previewCall.run === false && previewCall.payload.service === 'Kernel errors', JSON.stringify(previewCall));
  await row('Checkmk').locator('button', { hasText: 'Send' }).click();
  await tab.waitForFunction(() => window.__opened === 'ses_test', null, { timeout: 5_000 });
  check('Send delivers for real and opens the chat', true);
  await tab.waitForFunction(() => document.body.innerText.includes('nuc / x is OK'), null, { timeout: 5_000 }).catch(() => {});
  check('the delivery history lists a delivery with its status', (await row('Checkmk').innerText()).toLowerCase().includes('appended'));

  // --- pause, and the two-step delete
  await row('Skipping').hover();
  await row('Skipping').locator('.np-hover-actions button').nth(0).click();
  await tab.waitForFunction(() => window.__calls.some(([m, p]) => m === 'webhooks.update' && p.id === 'whk_skip' && p.enabled === false), null, { timeout: 5_000 });
  check('pause calls webhooks.update {id, enabled:false}', true);
  await row('Failing').hover();
  const trash = row('Failing').locator('.np-confirm').last();
  await trash.click();
  check('the first delete click arms it', (await trash.innerText()).includes('Delete?'));
  await trash.click();
  await tab.waitForFunction(() => window.__calls.some(([m, p]) => m === 'webhooks.delete' && p.id === 'whk_fail'), null, { timeout: 5_000 });
  check('the second click deletes', true);

  // --- a new token is shown once, with the example call
  await row('Paused').hover();
  const rekey = row('Paused').locator('.np-confirm').first();
  await rekey.click();
  await rekey.click();
  await tab.waitForFunction(() => window.__calls.some(([m]) => m === 'webhooks.rotateToken'), null, { timeout: 5_000 });
  check('re-keying calls webhooks.rotateToken', true);

  // --- add: the payload, then the token box
  await tab.locator('.webhooks button', { hasText: 'New' }).first().click();
  await tab.waitForSelector('.form .f-actions button', { timeout: 5_000 });
  await tab.locator('.form input[placeholder="e.g. Checkmk alerts"]').fill('Checkmk alerts');
  await tab.locator('.form select').nth(1).selectOption('sysadmin');
  await tab.locator('.form select').nth(2).selectOption('free');
  await tab.locator('.form input[placeholder="program, e.g. python"]').nth(1).fill('ssh');
  await tab.locator('.form textarea[placeholder="arguments, one per line"]').nth(1).fill('server\nx');
  await tab.locator('.form input[type=number][max="1440"]').fill('20');
  await tab.locator('.form .f-actions button', { hasText: 'Save' }).click();
  await tab.waitForSelector('.webhooks .token', { timeout: 5_000 });
  const add = await tab.evaluate(() => window.__calls.filter(([m]) => m === 'webhooks.add').at(-1)[1]);
  check('add sends the after command and leaves the empty before out', add.post?.command === 'ssh' && add.post.args.join(',') === 'server,x' && add.pre === undefined && add.timeoutMinutes === 20, JSON.stringify([add.pre, add.post, add.timeoutMinutes]));
  check('add sends name, agent free, profile and the fuse, and nothing about grouping or filters', add.name === 'Checkmk alerts' && add.agent === 'free' && add.profile === 'sysadmin' && add.maxChatsPerHour === 60 && add.group === undefined && add.match === undefined && add.title === undefined && add.alert === undefined, JSON.stringify(add));
  const tokenBox = await tab.locator('.webhooks .token').innerText();
  check('the token box shows the token once, with a curl line', tokenBox.includes('whk_SECRET_TOKEN_wxyz') && tokenBox.includes('curl -X POST'), tokenBox.slice(0, 80));
  await tab.locator('.webhooks .token button', { hasText: 'I have saved it' }).click();
  check('dismissing removes the token box', (await tab.locator('.webhooks .token').count()) === 0);

  // --- a missing name is told before saving
  await tab.locator('.webhooks button', { hasText: 'New' }).first().click();
  await tab.waitForSelector('.form .f-actions button', { timeout: 5_000 });
  await tab.locator('.form .f-actions button', { hasText: 'Save' }).click();
  await tab.waitForSelector('.form .f-err', { timeout: 5_000 });
  check('a missing name is refused in the form', (await tab.locator('.form .f-err').innerText()).includes('name is required'));
  check('the form has no filter, incident key or template boxes', (await tab.locator('.form').innerText()).split('\n').every((l) => !/Only when|Incident key|Each item in a batch|Message for a later item|Chat title/.test(l)));
  await tab.locator('.form .f-actions button', { hasText: 'Cancel' }).click();

  // --- webhooks.changed reloads
  const listBefore = await tab.evaluate(() => window.__listCalls);
  await tab.evaluate(() => window.__fire('webhooks.changed', {}));
  await tab.waitForFunction((lb) => window.__listCalls > lb, listBefore, { timeout: 5_000 });
  check('webhooks.changed triggers a reload', true);

  // --- no horizontal overflow at 230px
  const narrow = await browser.newPage({ viewport: { width: 230, height: 900 } });
  narrow.on('pageerror', (e) => errors.push(String(e)));
  await narrow.goto(`${base}/`);
  await narrow.waitForSelector('.webhooks .hook', { timeout: 15_000 });
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

// --- the model helpers
check('rowState: a disabled proposal', rowState({ enabled: false, proposedBy: 'session:x' }).key === 'proposed');
check('rowState: a failed delivery is an error', rowState({ enabled: true, lastStatus: 'failed' }).tone === 'err');
check('agent list round-trips', formToAgent({ agentChoice: '__list', agentList: ' a , b ' }) === 'a,b' && agentToForm('a,b').agentChoice === '__list');
check('incidentLine: open and collecting', incidentLine({ key: 'nuc', state: 'open', alertCount: 1 }) === 'nuc · open · 1 alert' && incidentLine({ key: 'x', state: 'collecting', alertCount: 2, startsAt: new Date(Date.now() + 30000).toISOString() }, Date.now()).includes('chat starts in'));
check('limitsLine: timeout and fuse', limitsLine({ timeoutMinutes: 15, maxChatsPerHour: 60 }) === 'timeout 15 min · fuse 60/h' && limitsLine({ timeoutMinutes: 0 }) === 'no timeout');
check('hookLine, hookToForm and formToHook', hookLine({ command: 'ssh', args: ['a', 'b'] }) === 'ssh a b' && hookLine({ command: 'x' }) === 'x' && formToHook(' ssh ', 'a\n\n b ').args.join(',') === 'a,b' && Object.keys(formToHook('', 'a')).length === 0 && hookToForm({ command: 'ssh', args: ['a', 'b'] }).args === 'a\nb' && hookToForm(null).command === '');

const failed = checks.filter((c) => !c.ok);
console.log(JSON.stringify({ ok: failed.length === 0 && errors.length === 0, checks, errors }));
process.exit(failed.length === 0 && errors.length === 0 ? 0 : 1);
