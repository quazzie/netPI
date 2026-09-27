#!/usr/bin/env node
// End-to-end walkthrough against the mock server: drives the UI with Playwright, takes screenshots
// (1600×1000), and reports console errors + timings. By default it starts its own mock server on :7432
// (MOCK_SPEED=3) so it can also test reconnects; pass --url to use a running server instead.
//
//   npm run build && npm run e2e
//   node web/mock/e2e.mjs [--out dir] [--only shot,shot] [--url http://127.0.0.1:7431] [--no-dev]
import fs from 'node:fs';
import path from 'node:path';
import { spawn } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { openApp } from './pw.mjs';

const here = path.dirname(fileURLToPath(import.meta.url));
const repo = path.resolve(here, '../..');
const argv = process.argv.slice(2);
const argVal = (n) => (argv.indexOf(n) >= 0 ? argv[argv.indexOf(n) + 1] : null);
const OUT = argVal('--out') ? path.resolve(argVal('--out')) : path.join(here, 'screenshots');
const ONLY = argVal('--only') ? new Set(argVal('--only').split(',')) : null;
const EXTERNAL = argVal('--url');
const PORT = 7432;
const BASE = EXTERNAL ?? `http://127.0.0.1:${PORT}`;
const rpcCall = (method, params = {}) =>
  fetch(`${BASE}/api/rpc/${method}`, { method: 'POST', headers: { 'X-NetPI-Token': 'dev', 'content-type': 'application/json' }, body: JSON.stringify(params) }).then((r) => r.json());
fs.mkdirSync(OUT, { recursive: true });

const results = [];
const log = (...a) => console.log(...a);
const shot = async (page, name) => {
  if (ONLY && !ONLY.has(name)) return;
  await page.screenshot({ path: path.join(OUT, `${name}.png`) });
  log(`  📸 ${name}.png`);
};
function check(name, ok, detail = '') {
  results.push({ name, ok, detail });
  log(`  ${ok ? '✓' : '✗'} ${name}${detail ? ` — ${detail}` : ''}`);
}
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

// ------------------------------------------------------------------ mock server lifecycle
let server = null;
async function startServer() {
  server = spawn(process.execPath, [path.join(here, 'server.mjs'), '--port', String(PORT)], {
    env: { ...process.env, MOCK_SPEED: process.env.MOCK_SPEED ?? '3' },
    stdio: ['ignore', 'pipe', 'inherit'],
  });
  await new Promise((resolve, reject) => {
    server.stdout.on('data', (d) => String(d).includes('mock server on') && resolve());
    server.on('exit', (c) => reject(new Error(`mock server exited (${c})`)));
  });
}
async function stopServer() {
  if (!server) return;
  const s = server;
  server = null;
  s.kill();
  await new Promise((r) => s.once('exit', r));
}
if (!EXTERNAL) {
  if (!fs.existsSync(path.join(repo, 'web/mock/sample-plugin/wwwroot/ui.js'))) {
    log('building the sample plugin…');
    await new Promise((r) => spawn(process.execPath, [path.join(repo, 'web/scripts/build-plugins.mjs'), '--only', 'web/mock/sample-plugin'], { cwd: repo, stdio: 'inherit' }).on('exit', r));
  }
  await startServer();
}
process.on('exit', () => server?.kill());

// start from the seeded mock state and a clean UI state
await fetch(`${BASE}/api/rpc/mock.reset`, { method: 'POST', headers: { 'X-NetPI-Token': 'dev' }, body: '{}' });
const { browser, page, errors } = await openApp({ url: `${BASE}/?token=dev` });
await page.evaluate(() => localStorage.clear());
await page.goto(`${BASE}/`);
await page.waitForSelector('.welcome .np-btn-primary', { timeout: 10_000 });
await page.waitForTimeout(300);
await shot(page, '01-welcome');

// ------------------------------------------------------------------ long session: render time + pruning
log('long session');
let t0 = Date.now();
await page.locator('.srow', { hasText: 'Lane scheduler hardening' }).first().click();
await page.waitForSelector('.content .item[data-kind="text"]');
const openMs = Date.now() - t0;
const perf = await page.evaluate(async () => {
  // time a full re-render of the list: switch away and back (store is cached → pure render cost)
  const t = performance.now();
  await new Promise((r) => requestAnimationFrame(() => requestAnimationFrame(r)));
  return { items: document.querySelectorAll('.content .item').length, frame: performance.now() - t };
});
check('open long session', true, `${openMs}ms to first paint of 60 messages (${perf.items} items)`);
await page.waitForTimeout(300);
await shot(page, '02-long-session');

// load earlier ×3 (60 → 240 messages; cap 200 → window drops the newest)
for (let i = 0; i < 4; i++) {
  const btn = page.locator('.earlier button', { hasText: 'Load earlier' });
  if (!(await btn.count())) break;
  // scroll to the top first (clicking the button would do it anyway), then measure the anchor
  await page.locator('.scroller').evaluate((el) => (el.scrollTop = 0));
  await page.waitForTimeout(80);
  const before = await page.evaluate(() => {
    const first = document.querySelector('.content .item');
    return { key: first.dataset.key, top: first.getBoundingClientRect().top };
  });
  const renderMs = await page.evaluate(async (k) => {
    const t = performance.now();
    [...document.querySelectorAll('.earlier button')].find((b) => b.textContent.includes('Load earlier')).click();
    await new Promise((r) => {
      const tick = () => (document.querySelector('.content .item')?.dataset.key !== k ? r() : requestAnimationFrame(tick));
      tick();
    });
    await new Promise((r) => requestAnimationFrame(() => setTimeout(r, 0)));
    return performance.now() - t;
  }, before.key);
  await page.waitForTimeout(100);
  const after = await page.evaluate((k) => {
    const el = document.querySelector(`.content .item[data-key="${k}"]`);
    return { top: el?.getBoundingClientRect().top ?? null, items: document.querySelectorAll('.content .item').length, msgs: document.querySelectorAll('.content .item').length };
  }, before.key);
  check(`load earlier #${i + 1}`, after.top != null && Math.abs(after.top - before.top) < 4, `${Math.round(renderMs)}ms (rpc + render), anchor moved ${after.top == null ? '?' : Math.round(after.top - before.top)}px, ${after.items} items in DOM`);
}
const hasNewer = await page.locator('.earlier button', { hasText: 'jump to latest' }).count();
// render cost of the full (capped) window: switch to another session and back (the store is cached)
const msgsInWindow = await page.locator('.content .item').count();
await page.keyboard.press('Control+t');
await page.waitForSelector('.intro');
const switchMs = await page.evaluate(async () => {
  const tab = [...document.querySelectorAll('.topbar .tab')].find((t) => t.textContent.includes('Lane scheduler'));
  const t = performance.now();
  tab.click();
  await new Promise((r) => {
    const tick = () => (document.querySelectorAll('.content .item').length > 80 ? r() : requestAnimationFrame(tick));
    tick();
  });
  await new Promise((r) => requestAnimationFrame(() => setTimeout(r, 0))); // include style/layout/paint
  return performance.now() - t;
});
check('re-render 200-message window on tab switch', switchMs < 400, `${Math.round(switchMs)}ms for ${msgsInWindow} items`);
await page.locator('.topbar .tab', { hasText: 'New session' }).locator('.tab-close').click();
check('window capped (newer messages dropped)', hasNewer > 0);
await page.locator('.scroller').evaluate((el) => (el.scrollTop = 0));
await page.waitForTimeout(150);
await shot(page, '03-load-earlier');
if (hasNewer) {
  await page.locator('.earlier button', { hasText: 'jump to latest' }).click();
  await page.waitForTimeout(400);
}
const domCount = await page.locator('.content .item').count();
check('back to latest', !(await page.locator('.earlier button', { hasText: 'jump to latest' }).count()), `${domCount} items`);

// ------------------------------------------------------------------ new session + streaming
log('new session + agent run');
await page.keyboard.press('Control+t');
await page.waitForSelector('.intro');
const ta = page.locator('.composer textarea');
await ta.fill('Why does the agent scheduler throw when a pool is missing? Make it fail with a clear message.');
await ta.press('Enter');
await page.waitForSelector('.thinking.live', { timeout: 5000 });
await page.waitForTimeout(700);
await shot(page, '04-streaming-thinking');
// one status line above the composer while it runs, none in the chat; the first model call brings the system prompt row
check('the run status line is above the composer', (await page.locator('.dock .run-status').count()) === 1 && (await page.locator('.messages .working, .content .working').count()) === 0,
  await page.locator('.dock .run-status').innerText().catch(() => ''));
await page.waitForSelector('.item[data-kind="prompt"]', { timeout: 3000 }).catch(() => {});
check('the system prompt row appears after the first message is sent', (await page.locator('.item[data-kind="prompt"]').count()) === 1);
await page.waitForSelector('.tool[data-status="ok"]', { timeout: 15000 });

// steer + queue while running
await ta.fill('Also make Release() return early for unknown pools.');
await ta.press('Enter');
await ta.fill('Afterwards, write a one-line changelog entry.');
await ta.press('Alt+Enter');
await page.waitForSelector('.queue .chip', { timeout: 3000 }).catch(() => {});
const chips = await page.locator('.queue .chip').count();
check('queue chips while running', chips >= 1, `${chips} chip(s)`);
{
  // a subagent's report waiting for the agent is internal: never a chip you could remove
  const sid = await page.locator('.topbar .tab.active').getAttribute('data-tab');
  await rpcCall('mock.queueInternal', { sessionId: sid });
  await page.waitForTimeout(300);
  const queued = await rpcCall('agent.queue', { sessionId: sid });
  const shown = await page.locator('.queue .chip').allInnerTexts();
  check('an internal queued input is not shown as a chip',
    queued.some((q) => q.source === 'agent:agt_x') && !shown.some((t) => t.includes('agent-result')), `queued: ${queued.map((q) => q.source).join(', ')}; chips: ${shown.join(' | ')}`);
}
await page.waitForSelector('.banner', { timeout: 8000 }).catch(() => {});
await shot(page, '05-steer-queue-retry');
const banner = await page.locator('.banner').count();
check('retry banner (agent.notice)', banner > 0);

// live bash output
await page.locator('.tool[data-status="running"]', { hasText: 'Bash' }).waitFor({ timeout: 20000 }).catch(() => {});
await page.locator('.tool .tail').waitFor({ timeout: 3000 }).catch(() => {});
await page.waitForTimeout(250);
await shot(page, '06-live-bash');
const tail = await page.locator('.tool .tail').count();
check('live tool output tail', tail > 0 || (await page.locator('.tool[data-status="ok"]', { hasText: 'Bash' }).count()) > 0);

// wait for the run (and queued follow-up) to finish
await page.waitForFunction(() => !document.querySelector('.composer.running'), null, { timeout: 90_000 });
await page.waitForTimeout(600);
await shot(page, '07-run-finished');
check('retry banner cleared after run', (await page.locator('.banner').count()) === 0);
const pinned = await page.locator('.scroller').evaluate((el) => el.scrollHeight - el.scrollTop - el.clientHeight);
check('stayed pinned to bottom during run', pinned < 60, `${Math.round(pinned)}px from bottom`);
const collapsed = await page.locator('.group.collapsible').count();
check('finished steps group collapsed', collapsed > 0, `${collapsed} group(s)`);

// expand group, thinking, diff, bash
const closedGroups = page.locator('.group.collapsible .head[aria-expanded="false"]');
for (let n = 0; n < 10 && (await closedGroups.count()); n++) await closedGroups.first().click();
await page.waitForTimeout(150);
for (const label of ['Edit', 'Bash']) {
  const row = page.locator('.tool .line', { hasText: label }).last();
  if (await row.count()) await row.click();
}
const think = page.locator('.thinking .line').first();
if (await think.count()) await think.click();
await page.waitForTimeout(300);
const edit = page.locator('.tool', { has: page.locator('.label', { hasText: 'Edit' }) }).last();
if (await edit.count()) await edit.scrollIntoViewIfNeeded();
await page.waitForTimeout(200);
await shot(page, '08-expanded-tools');
check('diff view rendered', (await page.locator('.diff .dl.add').count()) > 0);
const turnLines = await page.locator('.group .turn').allInnerTexts();
check(
  'model turns: time to the first token and cache reuse under each turn',
  turnLines.length > 0 && turnLines.every((t) => /first token \d/.test(t) && /\d+% cached/.test(t)),
  turnLines[0],
);
const answerInfo = await page.locator('.assistant .foot .info').last().innerText();
check('the answer footer has its turn numbers too', /first token \d/.test(answerInfo) && /\d+% cached/.test(answerInfo), answerInfo);

// ------------------------------------------------------------------ showcase session (subagents, notices)
log('showcase session');
await page.locator('.srow', { hasText: 'Fix streaming reconnect bug' }).first().click();
await page.waitForTimeout(400);
await page.locator('.group.collapsible .head').first().click();
await page.waitForTimeout(150);
const spawnRow = page.locator('.tool .line', { hasText: 'Agent' }).first();
if (await spawnRow.count()) await spawnRow.click();
await page.locator('.srow .kids').first().click();
await page.waitForTimeout(250);
await shot(page, '09-subagents-notices');
{
  // what the model got: the system prompt (with its tools) first, and every notice opens to exactly what was sent
  const first = page.locator('.content:visible .item').first();
  check('the system prompt row comes first', (await first.getAttribute('data-kind')) === 'prompt');
  await first.locator('.pill').click();
  const raw = await first.locator('.sent .raw').innerText();
  check('it opens to the prompt as sent', raw.startsWith('You are a coding agent'), raw.slice(0, 60));
  const toolRows = first.locator('.sent .tool');
  check('with the tools sent with it', (await toolRows.count()) > 5);
  await toolRows.first().click();
  check('a tool opens to its definition', (await first.locator('.tjson').innerText()).includes('"parameters"'));
  await shot(page, '09b-system-prompt');
  await first.locator('.pill').click();

  const nudge = page.locator('.content:visible .item[data-kind="notice"]', { hasText: 'Nudge' }).first();
  await nudge.locator('.pill').click();
  const sent = await nudge.locator('.sent .raw').innerText();
  check('a notice opens to what was sent', sent.startsWith('<system-notice kind="nudge">') && sent.trim().endsWith('</system-notice>'), sent.slice(0, 60));
  const report = page.locator('.content:visible .item[data-kind="notice"]', { hasText: 'explorer finished' }).first();
  await report.locator('.pill').click();
  check('a report opens rendered', (await report.locator('.sent .md').count()) === 1);
  await report.locator('.sent .tb', { hasText: 'As sent' }).click();
  check('…and as sent on request', (await report.locator('.sent .raw').innerText()).startsWith('<system-notice kind="agent-result">'));
  await shot(page, '09c-notice-as-sent');
}

// ------------------------------------------------------------------ popups: agent picker, commands, mentions
log('composer popups');
{
  const sid0 = await page.evaluate(() => location.hash);
  const agentBtn = page.locator('.composer button[aria-label="Agent"]');
  await agentBtn.click();
  await page.waitForSelector('.popover .agent-pop', { timeout: 3000 }).catch(() => {});
  await page.waitForTimeout(200);
  await shot(page, '10-agent-picker');
  const gemmaOpt = page.locator('.popover .opt[data-agent="gemma"]');
  check('the agent picker lists the agents with their state', (await page.locator('.popover .opt[data-agent="qwen"]').count()) === 1 && /isn't loaded/.test(await gemmaOpt.innerText()), await gemmaOpt.innerText().catch(() => ''));
  await gemmaOpt.click();
  await page.waitForTimeout(250);
  check('choosing an agent shows it on the button', (await agentBtn.innerText()).includes('gemma'));
  // New agent…: the model list, then the chat runs on the new agent
  await agentBtn.click();
  await page.locator('.popover .foot button', { hasText: 'New agent' }).click();
  await page.locator('.popover input[aria-label="Filter models"]').fill('gemma');
  await page.keyboard.press('Enter');
  await page.waitForTimeout(400);
  const made = (await rpcCall('settings.get')).settings.agents ?? {};
  check('"New agent…" sets up an agent on the model and runs the chat on it', !!made['gemma-4'] && (await agentBtn.innerText()).includes('gemma-4'),
    `agents: ${Object.keys(made).join(', ')}; button: ${await agentBtn.innerText()}; model menus open: ${await page.locator('.popover input[aria-label="Filter models"]').count()}`);
  if (await page.locator('.popover input[aria-label="Filter models"]').count()) await page.keyboard.press('Escape');
  await agentBtn.click();
  await page.locator('.popover .opt[data-agent="qwen"]').click();
  await page.waitForTimeout(200);
  check('back on qwen', (await agentBtn.innerText()).includes('qwen'), sid0);
  await rpcCall('settings.set', { path: 'agents.gemma-4', value: null });
}
await ta.fill('');
await ta.type('/');
await page.waitForTimeout(150);
await shot(page, '11-commands');
check('command popup', (await page.locator('.popup .opt').count()) >= 7);
await ta.fill('');
await ta.type('/skill');
await page.waitForTimeout(300);
const skillOpts = await page.locator('.popup .opt .cmd').allInnerTexts();
check("the session's skills in the command popup, user-only ones too", skillOpts.includes('/skill:release-notes') && skillOpts.includes('/skill:handoff'), skillOpts.join(' '));
await ta.press('Enter');
check('accepting a skill inserts /skill:name', /^\/skill:[a-z-]+ $/.test(await ta.inputValue()), await ta.inputValue());
await ta.fill('');
await ta.type('Look at @Lane');
await page.waitForSelector('.popup .file', { timeout: 4000 }).catch(() => {});
await page.waitForTimeout(250);
await shot(page, '12-mentions');
check('mention popup (files.search)', (await page.locator('.popup .file').count()) > 0);
await page.keyboard.press('Escape');
await ta.fill('');

// ------------------------------------------------------------------ tabs
log('tabs');
const tabsBefore = await page.locator('.topbar .tab').count();
await page.keyboard.press('Control+Tab');
await page.waitForTimeout(150);
const firstTab = page.locator('.topbar .tab').first();
await firstTab.click({ button: 'middle' });
await page.waitForTimeout(150);
const tabsAfter = await page.locator('.topbar .tab').count();
check('middle-click closes tab', tabsAfter === tabsBefore - 1, `${tabsBefore} → ${tabsAfter}`);

// ------------------------------------------------------------------ panels + plugin tabs
log('panels + plugin tabs');
await page.locator('.panel.right .strip-tab', { hasText: 'Events' }).click();
await page.waitForTimeout(500);
await shot(page, '13-plugin-events-tab');
await page.locator('.panel.right .strip-tab', { hasText: 'Sample' }).click();
await page.waitForTimeout(300);
// resize right panel
const handle = page.locator('.panel.right .resizer');
const hb = await handle.boundingBox();
await page.mouse.move(hb.x + 3, hb.y + 300);
await page.mouse.down();
await page.mouse.move(hb.x - 80, hb.y + 300, { steps: 5 });
await page.mouse.up();
const w = await page.locator('.panel.right .body').evaluate((el) => el.getBoundingClientRect().width);
check('resize right panel', w > 400, `${Math.round(w)}px`);
// collapse left panel by clicking the active strip tab
await page.locator('.panel.left .strip-tab[aria-selected="true"]').click();
await page.waitForTimeout(150);
check('collapse left panel', (await page.locator('.panel.left.open').count()) === 0);
await shot(page, '14-panels');
await page.locator('.panel.left .strip-tab', { hasText: 'Projects' }).click();
await page.waitForTimeout(200);
await shot(page, '15-projects');

// ------------------------------------------------------------------ built-in plugin tabs (Work, Ideas, Diagnostics, Files)
/** Select a strip tab without toggling the panel closed when it is already the active one. */
async function openStripTab(side, name) {
  const t = page.locator(`.panel.${side} .strip-tab`, { hasText: name });
  if ((await t.getAttribute('aria-selected')) !== 'true') await t.click();
  if (!(await page.locator(`.panel.${side}.open`).count())) await t.click();
}
const right = page.locator('.panel.right > .body');
const leftBody = page.locator('.panel.left > .body');
await openStripTab('left', 'Sessions');
await page.locator('.srow', { hasText: 'Lane scheduler hardening' }).first().click();
await page.waitForTimeout(200);

log('plugin tab: Work');
{
  await openStripTab('right', 'Work');
  await page.waitForSelector('.plugin-root .work .pool', { timeout: 10_000 });
  await page.waitForTimeout(400);
  const qwen = page.locator('.work .pool', { hasText: 'qwen3.8-27b' }).first();
  const nums = (await qwen.locator('.nums').innerText().catch(() => '')).replace(/\s+/g, '');
  check('work: qwen pool 2/2 busy', nums.includes('2/2'), nums);
  check('work: pool shows queued waiter', (await qwen.locator('.owner.waiting').count()) > 0);
  const ownerNames = await qwen.locator('.owner .name').allInnerTexts();
  check('work: a top-level lane owner shows its session title, not "main"',
    ownerNames.includes('Index docs for semantic search') && !ownerNames.includes('main') && ownerNames.includes('surveyor'), ownerNames.join(' | '));
  // the agents are always listed; an inactive one says why; each has a switch
  const gemma = page.locator('.work .pool[data-agent="gemma"]');
  check('work: an agent whose model is not loaded is listed as such', (await gemma.locator('.st').innerText()) === 'not loaded' && /isn't loaded/.test(await gemma.locator('.why').innerText()));
  await gemma.locator('input.np-switch').click();
  await page.waitForTimeout(300);
  check('work: the switch takes an agent off (agents.setEnabled)', (await rpcCall('settings.get')).settings.agents?.gemma?.disabled === true && (await gemma.locator('.st').innerText()) === 'off');
  await gemma.locator('input.np-switch').click();
  await page.waitForTimeout(300);
  check('work: and back on', !(await rpcCall('settings.get')).settings.agents?.gemma?.disabled);
  const nodes = await page.locator('.work .node').count();
  check('work: agent tree incl. subagents', nodes >= 3 && (await page.locator('.work .node .kids .node').count()) > 0, `${nodes} nodes`);
  await shot(page, '25-work-tab');
  // expand the foreground process and watch its output grow (processes.output + live process.output)
  const proc = page.locator('.work .proc', { hasText: 'embed.py' }).first();
  await proc.locator('.row').click();
  await page.waitForSelector('.work .proc .out', { timeout: 5000 }).catch(() => {});
  const len0 = (await proc.locator('.out').innerText().catch(() => '')).length;
  await page.waitForTimeout(2200);
  const len1 = (await proc.locator('.out').innerText().catch(() => '')).length;
  check('work: process output tail + live chunks', len0 > 0 && len1 > len0, `${len0} → ${len1} chars`);
  await right.screenshot({ path: path.join(OUT, '26-work-process.png') });
  await proc.locator('.row').click();
  // kill the background dev server (two-step confirm button)
  const dev = page.locator('.work .proc', { hasText: 'npm run dev' }).first();
  const kill = dev.locator('button[aria-label="Kill process tree"]');
  await dev.hover(); // row actions appear on hover / focus
  await kill.click();
  await kill.click();
  await page.waitForTimeout(600);
  const killed = ((await rpcCall('processes.list')) ?? []).find((p) => p.command?.includes('npm run dev'));
  check('work: kill process (processes.kill)', killed?.status === 'killed', killed?.status);
  // clicking an agent opens its session
  await page.locator('.work .node .row', { hasText: 'surveyor' }).first().click();
  await page.waitForTimeout(400);
  check('work: agent click opens its session', (await page.locator('.topbar .tab.active[data-tab="ses_bg_explore"]').count()) > 0, await page.locator('.topbar .tab.active').innerText().catch(() => ''));
  await page.locator('.srow', { hasText: 'Lane scheduler hardening' }).first().click();
}

log('plugin tab: Ideas');
{
  await openStripTab('right', 'Ideas');
  await page.waitForSelector('.plugin-root .ideas .card', { timeout: 10_000 });
  const cards = () => page.locator('.ideas .card');
  const n0 = await cards().count();
  check('ideas: project backlog listed', n0 >= 4, `${n0} active ideas`);
  // a short list still fills the panel: the footer sits at its bottom, not under the last card
  const footGap = await page.evaluate(() => {
    const foot = document.querySelector('.plugin-root .ideas .foot');
    const mount = foot?.closest('.mount');
    return foot && mount ? Math.round(mount.getBoundingClientRect().bottom - foot.getBoundingClientRect().bottom) : null;
  });
  check('ideas: the tab fills the panel (footer at the bottom)', footGap !== null && Math.abs(footGap) <= 4, `gap ${footGap}px`);
  await cards().first().locator('.main').click();
  await page.waitForTimeout(250);
  check('ideas: expanded idea renders sections', (await cards().first().locator('.sec').count()) > 0);
  await shot(page, '27-ideas-tab');
  // status via the pill menu
  await cards().nth(1).locator('.status').click();
  await page.locator('.np-menu .np-menu-item', { hasText: 'in-progress' }).click();
  await page.waitForTimeout(500);
  check('ideas: status change', /in-progress/.test(await cards().nth(1).locator('.status').innerText()));
  // new idea (prepended + expanded), then add a section to it
  await page.locator('.ideas .scope button[title="New idea"]').click();
  await page.locator('.ideas .new input').first().fill('Keyboard shortcuts cheat sheet');
  await page.locator('.ideas .new input.tags').fill('ui, docs');
  await page.locator('.ideas .new button', { hasText: 'Add idea' }).click();
  await page.waitForTimeout(500);
  const first = cards().first();
  check('ideas: new idea added on top', (await first.locator('.title').innerText()) === 'Keyboard shortcuts cheat sheet');
  if ((await first.locator('.main').getAttribute('aria-expanded')) !== 'true') await first.locator('.main').click();
  await first.locator('.actions button[title="Add section"]').click();
  await first.locator('.sed select').selectOption('todo');
  await first.locator('.sed textarea').fill('- [ ] list shortcuts\n- [ ] render a table');
  await first.locator('.sed button', { hasText: 'Add section' }).click();
  await page.waitForTimeout(500);
  check('ideas: section added', (await first.locator('.sec').count()) === 1);
  // send to chat → composer
  await first.locator('.actions button[title^="Insert a prompt"]').click();
  await page.waitForTimeout(300);
  check('ideas: send to chat fills the composer', (await ta.inputValue()).includes('# Keyboard shortcuts cheat sheet'));
  await ta.fill('');
  // reorder, then delete through the host confirm dialog (both in the ⋯ menu)
  const moreMenu = async (card, item) => {
    await card.locator('.actions button[title="More actions"]').click();
    await page.locator('.np-menu .np-menu-item', { hasText: item }).click();
  };
  await moreMenu(first, 'Move down');
  await page.waitForTimeout(500);
  check('ideas: move down (ideas.reorder)', (await cards().nth(1).locator('.title').innerText()) === 'Keyboard shortcuts cheat sheet');
  await moreMenu(cards().nth(1), 'Delete idea');
  await page.locator('.dialog button', { hasText: /^Delete$/ }).click();
  await page.waitForTimeout(500);
  check('ideas: delete with confirm', (await page.locator('.ideas .card', { hasText: 'Keyboard shortcuts cheat sheet' }).count()) === 0);
  // tag filter (menu; the selected tag shows as a removable chip) + an external change (ideas.changed) refetches
  await page.locator('.ideas .filters button[title="Filter by tag"]').click();
  await page.locator('.np-menu .np-menu-item', { hasText: '#aiproxy' }).click();
  await page.keyboard.press('Escape');
  await page.waitForTimeout(200);
  const nTag = await cards().count();
  check('ideas: tag filter', nTag > 0 && nTag < n0, `${nTag} of ${n0}`);
  await page.locator('.ideas .tags .np-chip', { hasText: '#aiproxy' }).click();
  check('ideas: removing the tag chip clears the filter', (await cards().count()) >= n0);
  const projectId = (await rpcCall('projects.list')).find((p) => p.name === 'netpi')?.id;
  if (projectId) await rpcCall('ideas.add', { projectId, idea: { title: 'Added over RPC', tags: ['rpc'] } });
  await page.waitForSelector('.ideas .card:has-text("Added over RPC")', { timeout: 3000 }).catch(() => {});
  check('ideas: refetch on ideas.changed', (await page.locator('.ideas .card', { hasText: 'Added over RPC' }).count()) > 0);
}

log('ideas: recall on the first message (the chip above the composer)');
{
  await rpcCall('ideas.add', { projectId: 'global', idea: { title: 'Nudge counter reset after a good answer', summary: 'Reset the nudge counter.' } });
  await page.keyboard.press('Control+t');
  await page.waitForTimeout(500);
  const chip = page.locator('[aria-label="Matching idea"]');
  await ta.fill('hi');
  await page.waitForTimeout(1400);
  check('recall: no chip for a short or unrelated first message', (await chip.count()) === 0);
  await ta.fill('the nudge counter should reset after a good answer from the agent');
  await chip.waitFor({ timeout: 4000 }).catch(() => {});
  check('recall: a matching idea shows a chip while the first message is typed', /Nudge counter reset/.test(await chip.innerText().catch(() => '')));
  await shot(page, '27b-idea-chip');
  await chip.locator('button', { hasText: 'Add' }).click();
  // the notice is collapsed (its label shows); the chat's messages hold it with the idea's text
  await page.waitForSelector('.notice:has-text("Idea from the backlog")', { timeout: 3000 }).catch(() => {});
  const sid = await page.locator('.topbar .tab.active').getAttribute('data-tab');
  const msgs = (await rpcCall('sessions.messages', { id: sid }))?.messages ?? [];
  const note = msgs.find((m) => m.role === 'notice' && m.meta?.kind === 'idea');
  check(
    'recall: Add puts the idea into the chat as a notice',
    (await page.locator('.notice', { hasText: 'Idea from the backlog' }).count()) > 0 && /Nudge counter reset/.test(note?.parts?.[0]?.text ?? ''),
  );
  check('recall: the chip goes away after Add', (await chip.count()) === 0);
  await ta.fill('');
  await page.locator('.topbar .tab.active .tab-close').click();
  await page.waitForTimeout(200);
  await page.locator('.srow', { hasText: 'Lane scheduler hardening' }).first().click();
  await page.waitForTimeout(300);
}

log('plugin tab: Diagnostics');
{
  await openStripTab('right', 'Diagnostics');
  await page.waitForSelector('.plugin-root .diag', { timeout: 10_000 });
  const view = (v) => page.locator(`.diag .views button[data-value="${v.toLowerCase()}"]`).click();
  await view('Plugins');
  await page.waitForTimeout(300);
  check('diagnostics: failed plugin listed first with its error', /failed/.test(await page.locator('.diag .pl').first().innerText()) && (await page.locator('.diag .pl .error').count()) > 0);
  await shot(page, '28-diagnostics-plugins');
  const row = page.locator('.diag .pl', { hasText: 'netpi.retry' }).first();
  const loads = async () => (await rpcCall('plugins.list')).find((p) => p.id === 'netpi.retry')?.loadCount;
  const before = await loads();
  await row.hover();
  await row.locator('button[title^="Reload"]').click();
  await page.waitForTimeout(900);
  check('diagnostics: reload plugin', (await loads()) === before + 1);
  await view('Tools');
  await page.waitForTimeout(200);
  const shadowedRows = await page.locator('.diag .tool.shadowed').count();
  await page.locator('.diag .np-chip', { hasText: 'shadowed' }).click();
  check('diagnostics: shadowed tool registration (toggle hides it)', shadowedRows > 0 && (await page.locator('.diag .tool.shadowed').count()) === 0);
  await page.locator('.diag .np-chip', { hasText: 'shadowed' }).click();
  await view('RPC');
  await page.waitForTimeout(200);
  await view('Events');
  await page.waitForTimeout(1500);
  await page.locator('.diag .ev').first().click();
  await page.locator('.diag .detail').waitFor({ timeout: 3000 }).catch(() => {});
  check('diagnostics: live events + payload', (await page.locator('.diag .ev').count()) > 5 && (await page.locator('.diag .detail').count()) > 0);
  await right.screenshot({ path: path.join(OUT, '29-diagnostics-events.png') });
  await view('Logs');
  await page.waitForTimeout(400);
  check('diagnostics: logs', (await page.locator('.diag .log').count()) > 3);
  await view('Context');
  await page.waitForSelector('.diag .prompt', { timeout: 5000 }).catch(() => {});
  check('diagnostics: context preview', (await page.locator('.diag .prompt').count()) > 0);
  check("diagnostics: the session's skills", (await page.locator('.diag .sname').allInnerTexts()).includes('release-notes'));
  await right.screenshot({ path: path.join(OUT, '30-diagnostics-context.png') });
  await view('Plugins');
}

log('plugin tab: Files');
{
  await openStripTab('left', 'Files');
  await page.waitForSelector('.plugin-root .files .frow', { timeout: 10_000 });
  const dir = async (name) => {
    await page.locator('.files .frow .fname', { hasText: new RegExp(`^${name}$`) }).last().click();
    await page.waitForTimeout(250);
  };
  await dir('web');
  await dir('src');
  await dir('lib');
  await ta.fill('');
  await page.locator('.files .frow .fname', { hasText: /^markdown\.js$/ }).click();
  await page.waitForTimeout(250);
  const opened = (await rpcCall('mock.filesOpened')).map((p) => p.replaceAll('\\', '/'));
  check('files: a click opens the file (files.open)', opened.at(-1)?.endsWith('web/src/lib/markdown.js'), opened.at(-1));
  check('files: a click inserts nothing', (await ta.inputValue()) === '', await ta.inputValue());
  const mdRow = page.locator('.files .frow', { has: page.locator('.fname', { hasText: /^markdown\.js$/ }) });
  await mdRow.hover();
  await mdRow.locator('.act[title^="Insert @"]').click();
  check('files: @ inserts a mention', (await ta.inputValue()) === '@web/src/lib/markdown.js ', await ta.inputValue());
  await ta.fill('');
  const gitLine = page.locator('.files .foot.git');
  await gitLine.waitFor({ timeout: 5000 }).catch(() => {});
  const gitText = (await gitLine.count()) ? await gitLine.innerText() : '';
  const ahead = (await gitLine.locator('.ab').count()) ? (await gitLine.locator('.ab').innerText()).trim() : '';
  check('files: the git line (files.git)', /^main[\s\S]*4 files[\s\S]*\+60[\s\S]*−400/.test(gitText) && ahead === '2', `${gitText} (ahead ${ahead})`);
  await gitLine.click();
  await page.waitForTimeout(250);
  check('files: the git line lists the changed files', (await page.locator('.files .frow.change').count()) === 4);
  check('files: a deleted file is struck through', (await page.locator('.files .frow.change.gone', { hasText: 'OLD-NOTES.md' }).count()) === 1);
  await shot(page, '31-files-changes');
  await gitLine.click();
  await page.waitForTimeout(250);
  await page.locator('.files .frow .fname', { hasText: /^icons\.js$/ }).click({ button: 'right' });
  await page.waitForSelector('.np-menu', { timeout: 2000 }).catch(() => {});
  check('files: context menu', (await page.locator('.np-menu .np-menu-item', { hasText: 'Copy relative path' }).count()) > 0);
  await page.waitForTimeout(250); // menu fade-in
  await shot(page, '31-files-tab');
  await page.keyboard.press('Escape');
  await page.locator('.files .np-search input').fill('chatitems');
  await page.waitForTimeout(500);
  check('files: search (files.search)', (await page.locator('.files .frow', { hasText: 'chatItems.js' }).count()) > 0);
  await page.locator('.files .np-search input').fill('');
  await ta.fill('');
  await openStripTab('left', 'Sessions');
}

log('ask_user: questions in the chat');
{
  await page.locator('.srow', { hasText: 'Fix streaming reconnect bug' }).first().click();
  await page.waitForTimeout(300);
  await ta.fill('[ask2] fix the reconnect');
  await ta.press('Enter');
  const card = page.locator('.item[data-kind="ask"] .card').last();
  await card.waitFor({ timeout: 15_000 }).catch(() => {});
  const kinds = await page.locator('.content:visible .item').evaluateAll((els) => els.slice(-2).map((e) => e.dataset.kind));
  check('ask_user: the questions come after the message that explains them', kinds.join() === 'text,ask', kinds.join());
  check('ask_user: the message box answers', ((await ta.getAttribute('placeholder')) ?? '').startsWith('Answer the question'));
  check('ask_user: the chat tab shows that a question waits', (await page.locator('.tab.active .np-dot[data-status="asking"]').count()) === 1);
  await card.locator('.opt', { hasText: 'Rewrite the client' }).click();
  await card.locator('.opt', { hasText: 'unit' }).click();
  await shot(page, '47-ask-user');
  await card.locator('.foot button', { hasText: 'Send' }).click();
  const line = page.locator('.item[data-kind="ask"] button.line').last();
  await line.waitFor({ timeout: 5000 }).catch(() => {});
  check('ask_user: answered, the card is one line: question → answer', ((await line.count()) ? await line.innerText() : '').includes('Rewrite the client'));
  await page.waitForFunction(() => !document.querySelector('.composer.running'), null, { timeout: 15_000 }).catch(() => {});
  await ta.fill('[ask] again');
  await ta.press('Enter');
  await page.locator('.item[data-kind="ask"] .card').last().waitFor({ timeout: 15_000 }).catch(() => {});
  await ta.fill('Neither: a heartbeat');
  await ta.press('Enter');
  await page.waitForFunction(() => !document.querySelector('.item[data-kind="ask"] .card'), null, { timeout: 5000 }).catch(() => {});
  check(
    "ask_user: typed in the message box, the answer is the user's own words",
    (await page.locator('.item[data-kind="ask"] button.line').last().innerText()).includes('Neither: a heartbeat'),
  );
  await page.waitForFunction(() => !document.querySelector('.composer.running'), null, { timeout: 15_000 }).catch(() => {});
}

log('guardrails: a tool call waits for your OK');
{
  await ta.fill('[guard] push it');
  await ta.press('Enter');
  const bar = page.locator('.tool .approve').last();
  await bar.waitFor({ timeout: 15_000 }).catch(() => {});
  check('guardrails: the tool row asks for your OK', (await bar.count()) === 1 && (await bar.innerText()).includes('ask: ^git'));
  check('guardrails: the chat tab shows that something waits', (await page.locator('.tab.active .np-dot[data-status="asking"]').count()) === 1);
  await shot(page, '48-guardrail-approval');
  await bar.locator('button', { hasText: /^Allow$/ }).click();
  await page.waitForFunction(() => !document.querySelector('.composer.running'), null, { timeout: 15_000 }).catch(() => {});
  check('guardrails: allowed, it ran', (await page.locator('.content:visible .item').last().innerText()).includes('Pushed'));
  // allowed for this chat: the next push runs without asking, its row says why
  const idle = () => page.waitForFunction(() => !document.querySelector('.composer.running'), null, { timeout: 15_000 }).catch(() => {});
  await ta.fill('[guard] push it');
  await ta.press('Enter');
  const bar2 = page.locator('.tool .approve').last();
  await bar2.waitFor({ timeout: 15_000 }).catch(() => {});
  await bar2.locator('button', { hasText: 'Allow in this chat' }).click();
  await idle();
  const asksBefore = await page.locator('.tool .approve').count();
  await ta.fill('[guard] push it');
  await ta.press('Enter');
  await page.waitForTimeout(800);
  await idle();
  check('guardrails: allowed in this chat, the next push does not ask', asksBefore === 0 && (await page.locator('.tool .approve').count()) === 0);
  check("guardrails: its row says it was allowed in this chat", (await page.locator('.tool .state', { hasText: 'allowed in this chat' }).count()) > 0);
}

log('fork: a new chat from a message');
{
  await page.locator('.srow', { hasText: 'Fix streaming reconnect bug' }).first().click();
  await page.waitForTimeout(300);
  const answer = page.locator('.assistant', { has: page.locator('.foot') }).last();
  await answer.hover();
  await answer.locator('.foot button[title^="Fork"]').click();
  await page.waitForTimeout(600);
  check('fork: after an answer, the fork opens in a tab of its own', /\(fork/.test(await page.locator('.tab.active').innerText()));
  const crumb = page.locator('.header .crumb');
  check('fork: its header leads back to the original', ((await crumb.count()) ? await crumb.innerText() : '').includes('Fix streaming reconnect bug'));
  await crumb.click();
  await page.waitForTimeout(400);
  const first = page.locator('.user', { hasText: 'After a reconnect' }).first();
  await first.hover();
  await first.locator('button[title^="Fork"]').click();
  await page.waitForTimeout(600);
  check('fork: before a user message, its text waits in the new chat\'s box', (await ta.inputValue()).startsWith('After a reconnect'));
  await ta.fill('');
}

log('narrow side panels');
{
  // drag both panels to ~230px (the user's layout is 230–340px) and check every tab for sideways overflow
  const dragTo = async (side, width) => {
    const h = await page.locator(`.panel.${side} .resizer`).boundingBox();
    const body = await page.locator(`.panel.${side} > .body`).boundingBox();
    const target = side === 'right' ? body.x + body.width - width : body.x + width;
    await page.mouse.move(h.x + 3, h.y + 200);
    await page.mouse.down();
    await page.mouse.move(target, h.y + 200, { steps: 6 });
    await page.mouse.up();
  };
  await dragTo('right', 230);
  await dragTo('left', 230);
  const overflow = () =>
    page.evaluate(() =>
      [...document.querySelectorAll('.panel > .body')]
        .filter((b) => !b.hidden)
        .flatMap((b) => [...b.querySelectorAll('.pane:not([hidden]) .mount, .pane:not([hidden]) .list, .pane:not([hidden]) .tab-root')])
        .filter((el) => el.scrollWidth > el.clientWidth + 1)
        .map((el) => `${el.className} ${el.scrollWidth}>${el.clientWidth}`),
    );
  const w = Math.round((await page.locator('.panel.right > .body').boundingBox()).width);
  const bad = [];
  for (const [side, tab] of [['right', 'Work'], ['right', 'Ideas'], ['right', 'Diagnostics'], ['left', 'Sessions'], ['left', 'Projects'], ['left', 'Files']]) {
    await openStripTab(side, tab);
    await page.waitForTimeout(tab === 'Diagnostics' ? 600 : 300);
    const o = await overflow();
    if (o.length) bad.push(`${tab}: ${o.join(', ')}`);
    if (tab === 'Diagnostics') {
      const seg = await page.locator('.diag .np-seg').evaluate((el) => el.scrollWidth <= el.clientWidth + 1);
      if (!seg) bad.push('Diagnostics: view switcher clipped');
    }
  }
  check(`narrow panels (${w}px): no sideways overflow in any tab`, w <= 240 && bad.length === 0, bad.join(' | '));
  await openStripTab('right', 'Work');
  await openStripTab('left', 'Sessions');
  await page.waitForTimeout(300);
  await shot(page, '32-narrow-panels');
  await page.locator('.panel.right .resizer').dblclick();
  await page.locator('.panel.left .resizer').dblclick();
}

log('project picker + new session project');
{
  const projects = await rpcCall('projects.list');
  const idOf = (name) => projects.find((p) => p.name === name)?.id;
  await page.locator('.srow', { hasText: 'Scratch' }).first().click();
  await page.waitForTimeout(400);
  const pick = async (chip, name) => {
    await chip.click();
    await page.waitForSelector('.popover .item', { timeout: 3000 });
    await page.locator('.popover .item', { hasText: name }).first().click();
    await page.waitForTimeout(500);
  };
  // the project picker lives in the composer bar, after the profile (e8a3636)
  const projBtn = page.locator('.composer button[aria-label="Project"]');
  await pick(projBtn, 'website');
  let s = await rpcCall('sessions.get', { id: 'ses_scratch' });
  check('project picker (composer) attaches the project', s.projectId === idOf('website'), String(s.projectId));
  check('project picker (composer) posts the project notice', (await page.locator('.notice', { hasText: 'website' }).count()) > 0);
  await pick(projBtn, 'aiproxy');
  s = await rpcCall('sessions.get', { id: 'ses_scratch' });
  check('project picker (composer) changes the project again', s.projectId === idOf('aiproxy'), String(s.projectId));
  check('project picker (composer) posts the second notice', (await page.locator('.notice', { hasText: 'aiproxy' }).count()) > 0);
  check('the composer button shows the attached project', /aiproxy/.test(await projBtn.innerText()));
  check('no project chip left in the top bar or the chat header', (await page.locator('.topbar .chip, .header .chip').count()) === 0);
  // a new session starts in the active session's project
  await page.locator('.panel.left .head button[title^="New session"]').click();
  await page.waitForTimeout(500);
  const newId = await page.locator('.topbar .tab.active').getAttribute('data-tab');
  const ns = await rpcCall('sessions.get', { id: newId });
  check('new session inherits the active project', ns.projectId === idOf('aiproxy'), String(ns.projectId));
  // …and, with no session open, the project last worked in
  // close the other tabs first so no other session becomes active on the way
  while (await page.locator('.topbar .tab:not(.active)').count()) await page.locator('.topbar .tab:not(.active) .tab-close').first().click();
  await page.locator('.topbar .tab.active .tab-close').click();
  await page.waitForTimeout(200);
  await page.keyboard.press('Control+t');
  await page.waitForTimeout(500);
  const id2 = await page.locator('.topbar .tab.active').getAttribute('data-tab');
  const s2 = id2 ? await rpcCall('sessions.get', { id: id2 }) : null;
  check('new session with no tab open uses the last project', s2?.projectId === idOf('aiproxy'), String(s2?.projectId));
  await page.locator('.srow', { hasText: 'Lane scheduler hardening' }).first().click();
  await page.waitForTimeout(300);
}

log('start screen: the project new sessions start in');
{
  const projects = await rpcCall('projects.list');
  const idOf = (name) => projects.find((p) => p.name === name)?.id;
  while (await page.locator('.topbar .tab').count()) await page.locator('.topbar .tab .tab-close').first().click();
  await page.waitForSelector('.welcome .target');
  const chip = page.locator('.welcome .target');
  const last = await page.evaluate(() => JSON.parse(localStorage.getItem('netpi.lastProject') ?? 'null'));
  const lastName = projects.find((p) => p.id === last)?.name ?? 'No project';
  check('start screen shows the project last worked in', (await chip.innerText()).includes(lastName), (await chip.innerText()) + ' vs ' + lastName);
  await chip.click();
  await page.waitForSelector('.popover .item');
  await page.waitForTimeout(300);
  await shot(page, '12b-start-project-picker');
  await page.locator('.popover .item', { hasText: 'website' }).first().click();
  await page.waitForTimeout(300);
  check('choosing a project on the start screen creates no session', (await page.locator('.topbar .tab').count()) === 0 && (await page.locator('.welcome').count()) === 1);
  check('the start screen shows the chosen project', /website/.test(await chip.innerText()), await chip.innerText());
  check('the start screen shows its folder', /website/.test(await page.locator('.welcome .where').innerText()), await page.locator('.welcome .where').innerText());
  await page.locator('.welcome .np-btn-primary').click();
  await page.waitForTimeout(500);
  const id = await page.locator('.topbar .tab.active').getAttribute('data-tab');
  const s = id ? await rpcCall('sessions.get', { id }) : null;
  check('New session starts in the chosen project', s?.projectId === idOf('website'), String(s?.projectId));
}

log('projects dialog');
{
  // from the top bar picker of the session just created in "website"
  await page.locator('.composer button[aria-label="Project"]').click();
  await page.waitForSelector('.popover .manage');
  check('the picker offers to edit the attached project', (await page.locator('.popover .manage', { hasText: 'Edit “website”' }).count()) === 1);
  await page.locator('.popover .manage', { hasText: 'Manage projects' }).click();
  await page.waitForSelector('.projects-dialog');
  check('Manage projects opens the projects dialog', (await page.locator('.projects-dialog .prow').count()) >= 3);
  await page.waitForTimeout(200);
  await shot(page, '22a-projects-dialog');
  await page.locator('.projects-dialog .prow', { hasText: 'website' }).locator('.pmain').click();
  await page.waitForSelector('.projects-dialog .frow', { timeout: 3000 }).catch(() => {});
  check('the project view lists its sessions', (await page.locator('.projects-dialog .sess').count()) >= 1);
  check('the project view lists the instruction files', (await page.locator('.projects-dialog .frow').count()) >= 1);
  await shot(page, '22b-project-edit');
  const name = page.locator('.projects-dialog input.np-input').first();
  await name.fill('website-renamed');
  await page.locator('.projects-dialog .np-btn-primary', { hasText: 'Save' }).click();
  await page.waitForTimeout(300);
  check('Save renames the project', (await rpcCall('projects.list')).some((p) => p.name === 'website-renamed'));
  check('the composer project button follows the rename', /website-renamed/.test(await page.locator('.composer button[aria-label="Project"]').innerText()));
  await name.fill('website');
  await page.locator('.projects-dialog .np-btn-primary', { hasText: 'Save' }).click();
  await page.waitForTimeout(200);
  await page.locator('.projects-dialog .backlink').click();
  check('Back returns to the list', (await page.locator('.projects-dialog .prow').count()) >= 3);
  // a confirm over the dialog: Esc closes only the confirm
  await page.locator('.projects-dialog .prow', { hasText: 'website' }).locator('button[title="Remove"]').click();
  await page.waitForSelector('.dialog >> text=Remove project?');
  await page.keyboard.press('Escape');
  await page.waitForTimeout(200);
  check('Esc closes only the top dialog', (await page.locator('.dialog').count()) === 1 && (await page.locator('.projects-dialog').count()) === 1);
  await page.keyboard.press('Escape');
  await page.waitForTimeout(200);
  check('Esc then closes the projects dialog', (await page.locator('.projects-dialog').count()) === 0);
  check('the project was not removed', (await rpcCall('projects.list')).some((p) => p.name === 'website'));
  // "New project…" in the attach picker: the new project is attached to the session
  await page.locator('.composer button[aria-label="Project"]').click();
  await page.waitForSelector('.popover .manage');
  await page.locator('.popover .manage', { hasText: 'New project' }).click();
  await page.waitForSelector('.projects-dialog');
  await page.locator('.projects-dialog .pathrow input').fill(path.join(repo, 'web', 'mock'));
  await page.locator('.projects-dialog .np-btn-primary', { hasText: 'Create project' }).click();
  await page.waitForTimeout(500);
  const sid = await page.locator('.topbar .tab.active').getAttribute('data-tab');
  const cur = await rpcCall('sessions.get', { id: sid });
  const created = (await rpcCall('projects.list')).find((p) => p.name === 'mock');
  check('a project made from the picker is attached to the session', !!created && cur.projectId === created.id, String(cur.projectId));
  check('the chip shows the new project', /mock/.test(await page.locator('.composer button[aria-label="Project"]').innerText()));
  await page.locator('.srow', { hasText: 'Lane scheduler hardening' }).first().click();
  await page.waitForTimeout(300);
}

// ------------------------------------------------------------------ settings as controls: fields, agents, budget, tools
log('settings: controls, agents, budget, tools');
{
  await page.keyboard.press('Control+,');
  await page.waitForSelector('.dialog');
  await page.locator('.nav button', { hasText: 'Runs' }).click();
  const turnsField = page.locator('.field', { hasText: 'Model calls per run' });
  const turns = turnsField.locator('input');
  check('an unset setting shows its default', (await turns.getAttribute('placeholder')) === '200');
  await turns.fill('300');
  await turns.press('Enter');
  await page.waitForTimeout(300);
  check('a changed field is saved (settings.set)', (await rpcCall('settings.get')).settings.agent?.maxTurns === 300);
  await turnsField.locator('.reset').click();
  await page.waitForTimeout(300);
  check('Reset removes the setting again', (await rpcCall('settings.get')).settings.agent?.maxTurns === undefined);
  await turns.fill('0');
  await turns.press('Enter');
  await page.waitForTimeout(200);
  check('a number out of range is refused, not saved', (await turnsField.locator('.bad-msg').count()) === 1 && (await rpcCall('settings.get')).settings.agent?.maxTurns === undefined);

  await page.locator('.nav button', { hasText: 'Agents & budget' }).click();
  check('the agents are rows, an inactive one says so', (await page.locator('.setting-row[data-agent="qwen"]').count()) === 1 && /not loaded/.test(await page.locator('.setting-row[data-agent="gemma"]').innerText()));
  // Add agent: the searchable model list, then the agent's own dialog
  await page.locator('.dialog .agents .add button', { hasText: 'Add agent' }).click();
  await page.locator('.popover input[aria-label="Filter models"]').fill('sonnet');
  await page.keyboard.press('Enter');
  const agentDialog = page.locator('.dialog.agent-dialog');
  await agentDialog.waitFor({ timeout: 3000 }).catch(() => {});
  check('an agent is added from the model search and opens in its dialog, with its price', (await agentDialog.count()) === 1 && (await agentDialog.locator('.facts').innerText()).includes('$3 / $15 per Mtok'));
  await agentDialog.locator('textarea.use').fill('Costs money: only for hard problems.');
  await agentDialog.locator('textarea.use').blur();
  await page.waitForTimeout(300);
  check('the agent note is saved', (await rpcCall('settings.get')).settings.agents?.['claude-sonnet-4-6']?.use === 'Costs money: only for hard problems.');
  await agentDialog.locator('button[aria-label="Model"]').click();
  await page.waitForSelector('.popover input[aria-label="Filter models"]');
  await page.waitForTimeout(300);
  await shot(page, '40a-agent-dialog');
  await page.keyboard.press('Escape');
  await page.waitForTimeout(200);
  check('Esc closes the model list, not the agent dialog', (await agentDialog.count()) === 1 && (await page.locator('.popover input[aria-label="Filter models"]').count()) === 0);
  // switched off in the dialog, then a new name
  await agentDialog.locator('input.np-switch').click();
  await page.waitForTimeout(300);
  check('the dialog switches an agent off', (await rpcCall('settings.get')).settings.agents?.['claude-sonnet-4-6']?.disabled === true);
  await agentDialog.locator('input.np-switch').click();
  await page.waitForTimeout(200);
  const nameInput = agentDialog.locator('input[aria-label="Name"]');
  await nameInput.fill('Sonnet');
  await nameInput.press('Enter');
  await page.waitForTimeout(400);
  const agentsNow = (await rpcCall('settings.get')).settings.agents ?? {};
  check('a new name moves the agent', !!agentsNow.sonnet && !agentsNow['claude-sonnet-4-6'] && agentsNow.sonnet.use === 'Costs money: only for hard problems.' && !agentsNow.sonnet.disabled);
  check('the dialog follows the new name', (await agentDialog.locator('.title, h2, header').first().innerText().catch(() => '')).includes('sonnet') || (await agentDialog.innerText()).includes('Agent sonnet'));
  await agentDialog.locator('.foot button', { hasText: 'Done' }).click();
  await page.waitForTimeout(200);
  const agentRow = page.locator('.setting-row[data-agent="sonnet"]');
  check('the agent is one row: model, instances, price', (await agentRow.count()) === 1 && /1 instance/.test(await agentRow.innerText()) && (await agentRow.innerText()).includes('$3 / $15'));
  const monthly = page.locator('.field', { hasText: 'Monthly budget' }).locator('input');
  await monthly.fill('50');
  await monthly.press('Enter');
  await page.waitForTimeout(400);
  check('the budget shows this month against the limit', (await page.locator('.dialog .budget').innerText()).includes('of $50'));
  await shot(page, '40-settings-agents');

  // Models: one row per provider, its options in a dialog
  await page.locator('.nav button', { hasText: 'Models' }).click();
  await page.waitForTimeout(200);
  check('a page shows only its own settings (not the About details)', (await page.locator('.dialog .about').count()) === 0);
  check('providers are rows', (await page.locator('.setting-row[data-section="aiproxy"]').count()) === 1 && (await page.locator('.setting-row[data-section="openrouter"]').count()) === 1);
  await page.locator('.setting-row[data-section="openrouter"]').click();
  const sectionDialog = page.locator('.dialog.section-dialog');
  await sectionDialog.waitFor({ timeout: 3000 }).catch(() => {});
  check('a provider row opens its options', (await sectionDialog.locator('.field', { hasText: 'API key' }).count()) === 1);
  await page.waitForTimeout(300);
  await shot(page, '41a-provider-dialog');
  await page.keyboard.press('Escape');
  await page.waitForTimeout(200);
  check('Esc closes only the provider dialog', (await sectionDialog.count()) === 0 && (await page.locator('.dialog').count()) === 1);

  // Context: the real built-in texts, not "built in"
  await page.locator('.nav button', { hasText: 'Context' }).click();
  const identity = page.locator('.field', { hasText: 'Identity' }).locator('textarea');
  check('the identity field shows the built-in text', (await identity.inputValue()).startsWith('You are a coding agent running in NetPI'));
  check('no field says "built in"', !(await page.locator('.dialog .layout > .content').innerText()).toLowerCase().includes('built in'));
  await identity.fill('You are a careful engineer.');
  await identity.blur();
  await page.waitForTimeout(300);
  check('an edited built-in text is saved', (await rpcCall('settings.get')).settings.context?.customPrompt === 'You are a careful engineer.');
  await page.locator('.field', { hasText: 'Identity' }).locator('.reset').click();
  await page.waitForTimeout(300);
  check('Reset brings the built-in text back', (await rpcCall('settings.get')).settings.context?.customPrompt === undefined && (await identity.inputValue()).startsWith('You are a coding agent'));

  await page.locator('.nav').getByRole('button', { name: 'Tools', exact: true }).click();
  await page.waitForTimeout(300);
  await page.locator('.setting-row[data-section="shell"]').click();
  await sectionDialog.waitFor({ timeout: 3000 }).catch(() => {});
  check('a tool row opens its options', (await sectionDialog.locator('.field', { hasText: 'Default timeout' }).count()) === 1);
  check('found paths show as the real path', ((await sectionDialog.locator('.field', { hasText: 'bash' }).locator('input').getAttribute('placeholder')) ?? '').includes('bash.exe'));
  await page.keyboard.press('Escape');
  await page.waitForTimeout(200);
  check('the Tools page has the tool options, not the plugin switches', (await page.locator('.plugin').count()) === 0);
  await page.locator('.nav button', { hasText: 'Plugins' }).click();
  await page.waitForTimeout(200);
  const shell = page.locator('.plugin[data-plugin="netpi.tools.shell"]');
  check('each plugin lists the tools it brings', (await shell.count()) === 1 && (await shell.locator('.ptools').innerText()).includes('bash'));
  check('no global switch per tool any more', (await page.getByRole('checkbox', { name: 'bash', exact: true }).count()) === 0);
  await shot(page, '41-settings-plugins');
  await page.keyboard.press('Escape');
  await page.waitForTimeout(200);
}

// ------------------------------------------------------------------ the tools of one chat
log('chat tools: switched per session');
{
  await page.keyboard.press('Control+t');
  await page.waitForTimeout(500);
  const sid = await page.locator('.topbar .tab.active').getAttribute('data-tab');
  const btn = page.locator('button[aria-label="Tools for this chat"]');
  await btn.click();
  await page.waitForFunction(() => document.querySelector('.tools-pop .help')?.innerText.trim(), null, { timeout: 3000 }).catch(() => {});
  check('a new chat: the menu says what switching off does', (await page.locator('.tools-pop .help').innerText()).includes('not sent to the agent'));
  await page.locator('.tools-pop').getByRole('checkbox', { name: 'bash', exact: true }).uncheck();
  await page.waitForTimeout(300);
  check('a tool switched off is saved for this chat', ((await rpcCall('agent.tools', { sessionId: sid })).off ?? []).includes('bash'));
  check('the button counts the tools that are off', (await btn.innerText()).includes('1 off'));
  await shot(page, '42-chat-tools');
  await page.keyboard.press('Escape');
  await page.waitForTimeout(200);

  const ta = page.locator('.composer textarea');
  await ta.fill('[fast] hello');
  await ta.press('Enter');
  await page.waitForSelector('.composer.running', { timeout: 5000 }).catch(() => {});
  await page.waitForFunction(() => !document.querySelector('.composer.running'), null, { timeout: 60_000 });
  await btn.click();
  await page.waitForFunction(() => document.querySelector('.tools-pop .help')?.innerText.includes('re-reads this chat'), null, { timeout: 3000 }).catch(() => {});
  check('a started chat: the menu says a change re-reads the chat', (await page.locator('.tools-pop .help').innerText()).includes('re-reads this chat'), await page.locator('.tools-pop .help').innerText());
  await page.locator('.tools-pop .foot button', { hasText: 'All on' }).click();
  await page.waitForTimeout(300);
  check('All on switches them back', ((await rpcCall('agent.tools', { sessionId: sid })).off ?? []).length === 0 && !(await btn.innerText()).includes('off'));
  await page.keyboard.press('Escape');
  await page.locator('.srow', { hasText: 'Lane scheduler hardening' }).first().click();
  await page.waitForTimeout(300);
}

// ------------------------------------------------------------------ what chats cost, the budget
log('budget: chat cost, Work tab, a chat stopped by the budget');
{
  // the seeded chat that used a paid model (with a subagent) shows what it cost
  await page.waitForSelector('.composer .cost', { timeout: 3000 }).catch(() => {});
  const cost = page.locator('.composer .cost');
  check('a chat shows what it cost, with its subagents', (await cost.count()) === 1 && (await cost.innerText()) === '$0.68', (await cost.count()) ? await cost.innerText() : 'none');
  check('the cost tooltip splits the chat from its subagents', ((await cost.getAttribute('title')) ?? '').includes('with its subagents $0.68'));

  // the Work tab: this month against the budget set above ($50)
  await openStripTab('right', 'Work');
  await page.waitForSelector('.work .usage.budget', { timeout: 5000 }).catch(() => {});
  const wb = page.locator('.work .usage.budget');
  check('the Work tab shows this month against the budget', (await wb.count()) === 1 && (await wb.innerText()).replace(/\u00a0/g, ' ').includes('$0.68 / $50'), (await wb.count()) ? await wb.innerText() : 'none');

  // a chat the budget stopped (budget.onLimit "ask") offers to go over
  await page.keyboard.press('Control+t');
  await page.waitForTimeout(500);
  const sid = await page.locator('.topbar .tab.active').getAttribute('data-tab');
  const ta = page.locator('.composer textarea');
  await ta.fill('[budget] Summarize the repo with the paid model.');
  await ta.press('Enter');
  const go = page.locator('.notice button.link', { hasText: 'Let this chat go over' });
  await go.waitFor({ timeout: 5000 }).catch(() => {});
  check('a budget stop offers to let the chat go over', (await go.count()) === 1);
  await shot(page, '43-budget-notice');
  await go.click();
  await page.locator('.dialog button', { hasText: /^Go over$/ }).click();
  await page.waitForTimeout(500);
  const allowed = (await rpcCall('sessions.get', { id: sid })).meta?.budgetAllowedFrom;
  check('going over is recorded for the chat, and the offer goes', !!allowed && (await go.count()) === 0, String(allowed));
  check('the chat is told to continue', (await page.locator('.notice', { hasText: 'go over the budget' }).count()) === 1);

  // the top bar shows the budget once it needs attention, and opens its settings
  const pill = page.locator('.budget-pill');
  check('no budget pill below the warning level', (await pill.count()) === 0);
  await rpcCall('settings.set', { path: 'budget.monthlyUsd', value: 0.5 });
  await pill.waitFor({ timeout: 3000 }).catch(() => {});
  check('a spent budget shows in the top bar', (await pill.count()) === 1 && ((await pill.getAttribute('class')) ?? '').includes('spent'));
  await shot(page, '44-budget-pill');
  await pill.click();
  await page.waitForSelector('.dialog');
  check('the pill opens the budget settings', (await page.locator('.nav button.active').innerText()).includes('Agents & budget'));
  await page.keyboard.press('Escape');
  await rpcCall('settings.set', { path: 'budget.monthlyUsd', value: 50 });
  await page.waitForTimeout(400);
  check('under the warning level again, the pill goes', (await pill.count()) === 0);
  await page.locator('.srow', { hasText: 'Lane scheduler hardening' }).first().click();
  await page.waitForTimeout(300);
}

// ------------------------------------------------------------------ profiles
log('profiles: settings, a project default, per chat');
{
  // a profile from the settings: a name, the instructions, tools switched off with checkboxes
  await page.keyboard.press('Control+,');
  await page.waitForSelector('.dialog');
  await page.locator('.nav button', { hasText: 'Profiles' }).click();
  await page.locator('.profiles .add input').fill('Admin');
  await page.locator('.profiles .add button').click();
  const profileDialog = page.locator('.dialog.profile-dialog');
  await profileDialog.waitFor({ timeout: 3000 }).catch(() => {});
  check('a profile is added by its name and opens in its dialog', (await profileDialog.count()) === 1);
  const prompt = profileDialog.locator('textarea.prompt');
  check('its instructions start as the real opening text', (await prompt.inputValue()).startsWith('You are a coding agent running in NetPI'));
  await prompt.fill('You are a system administrator.');
  await prompt.blur();
  await profileDialog.getByRole('checkbox', { name: 'bash', exact: true }).uncheck();
  await page.waitForTimeout(400);
  const saved = (await rpcCall('settings.get')).settings.profiles?.admin;
  check('its instructions and switched-off tools are saved', saved?.prompt === 'You are a system administrator.' && (saved?.toolsOff ?? []).includes('bash'), JSON.stringify(saved));
  await page.waitForTimeout(300);
  await shot(page, '45-profile-dialog');
  await profileDialog.locator('.foot button', { hasText: 'Done' }).click();
  await page.waitForTimeout(200);
  const profileRow = page.locator('.setting-row[data-profile="admin"]');
  check('the profile is one row: its opening line and its tools', (await profileRow.count()) === 1 && (await profileRow.innerText()).includes('You are a system administrator.') && /\d+ of \d+ tools/.test(await profileRow.innerText()));
  await shot(page, '45-settings-profiles');
  await page.keyboard.press('Escape');
  await page.waitForTimeout(200);

  // the project's default, from the Projects dialog
  await page.locator('.composer button[aria-label="Project"]').click();
  await page.locator('.popover .manage', { hasText: 'Edit “netpi”' }).click();
  await page.waitForSelector('.projects-dialog');
  await page.locator('.projects-dialog .skname').first().waitFor({ timeout: 3000 }).catch(() => {});
  const projectSkills = await page.locator('.projects-dialog .skname').allInnerTexts();
  check('the project dialog lists its skills and their problems', projectSkills[0] === 'svelte-tab' && (await page.locator('.projects-dialog .problem').count()) === 1, projectSkills.join(' '));
  const select = page.getByRole('combobox', { name: 'Profile of new sessions' });
  await select.waitFor({ timeout: 3000 }).catch(() => {});
  await select.selectOption('admin');
  await page.waitForTimeout(300);
  const netpi = (await rpcCall('projects.list')).find((p) => p.name === 'netpi');
  check('a project gets a default profile', netpi?.meta?.profile === 'admin', JSON.stringify(netpi?.meta));
  await page.keyboard.press('Escape');
  await page.waitForTimeout(200);

  // a new chat in the project: no profile until its first message (the project's default arrives with it); the picker is free before it
  await page.keyboard.press('Control+t');
  await page.waitForTimeout(500);
  const sid = await page.locator('.topbar .tab.active').getAttribute('data-tab');
  let s = await rpcCall('sessions.get', { id: sid });
  const picker = page.locator('button[aria-label="Profile"]');
  check('an empty chat has no profile yet (the default comes with its first message)', s.meta?.profile == null && (await picker.innerText()).includes('No profile'));
  await picker.click();
  await page.waitForSelector('.profile-pop');
  check('before the first message the change is free', (await page.locator('.profile-pop .help').innerText()).includes('free'));
  await page.keyboard.press('Escape');
  await page.waitForTimeout(200);

  // the first message materializes the chat: the project's default profile is applied, the model is told
  const ta = page.locator('.composer textarea');
  await ta.fill('hello');
  await ta.press('Enter');
  await page.waitForSelector('.composer.running', { timeout: 5000 }).catch(() => {});
  await page.waitForFunction(() => !document.querySelector('.composer.running'), null, { timeout: 60_000 });
  s = await rpcCall('sessions.get', { id: sid });
  check('the first message gets the default profile of the project', s.meta?.profile === 'admin' && (s.meta?.toolsOff ?? []).includes('bash'));
  check('the tools button shows what the profile switched off', (await page.locator('button[aria-label="Tools for this chat"]').innerText()).includes('1 off'));
  await picker.click();
  await page.waitForSelector('.profile-pop');
  check('in a started chat the picker says the chat is read again', (await page.locator('.profile-pop .help').innerText()).includes('re-reads this chat'));
  await shot(page, '46-chat-profile');
  await page.locator('.profile-pop .opt', { hasText: 'No profile' }).click();
  await page.waitForTimeout(400);
  check('the switch is announced in the chat', (await page.locator('.notice', { hasText: "profile away" }).count()) === 1);

  // back to how it was for the rest of the walkthrough
  await rpcCall('projects.update', { id: netpi.id, meta: { profile: null } });
  await rpcCall('settings.set', { path: 'profiles', value: null });
  await page.locator('.srow', { hasText: 'Lane scheduler hardening' }).first().click();
  await page.waitForTimeout(300);
}

// ------------------------------------------------------------------ settings + light theme
log('settings');
await page.keyboard.press('Control+,');
await page.waitForSelector('.dialog');
await page.waitForTimeout(300);
await shot(page, '16-settings');
await page.locator('.nav button', { hasText: 'settings.json' }).click();
await page.waitForSelector('.editor');
await page.waitForTimeout(200);
await shot(page, '17-settings-json');
await page.locator('.nav button', { hasText: 'General' }).click();
await page.locator('.np-seg button', { hasText: 'Light' }).click();
await page.keyboard.press('Escape');
await page.waitForTimeout(300);
await shot(page, '18-light-theme');
await page.keyboard.press('Control+,');
await page.locator('.np-seg button', { hasText: 'Dark' }).click();
await page.keyboard.press('Escape');

// palette
await page.keyboard.press('Control+k');
await page.waitForSelector('.palette');
await page.keyboard.type('lane');
await page.waitForTimeout(300);
await shot(page, '19-palette');
await page.keyboard.press('Escape');

// project picker (composer bar), help, a server slash command
await page.locator('.composer button[aria-label="Project"]').click();
await page.waitForSelector('.popover');
await page.waitForTimeout(300);
await shot(page, '19b-project-picker');
await page.keyboard.press('Escape');
await page.keyboard.press('Control+/');
await page.waitForSelector('.dialog');
await page.waitForTimeout(300);
await shot(page, '19c-help');
await page.keyboard.press('Escape');
await ta.fill('/compact');
await ta.press('Enter'); // accepts the popup entry
await page.waitForTimeout(100);
if ((await ta.inputValue()).startsWith('/compact')) await ta.press('Enter');
await page.waitForSelector('.notice', { hasText: 'Summary' }).catch(() => {});
await page.waitForTimeout(300);
check('/compact (server rpc command) adds a summary', (await page.locator('.notice .label', { hasText: 'Summary' }).count()) > 0);

// ------------------------------------------------------------------ tab drag & drop
log('drag to reorder tabs');
{
  const titles = async () => page.locator('.topbar .tab .tab-title').allTextContents();
  const before = await titles();
  if (before.length >= 2) {
    await page.locator('.topbar .tab').nth(1).dragTo(page.locator('.topbar .tab').nth(0), { targetPosition: { x: 5, y: 10 } });
    await page.waitForTimeout(150);
    const after = await titles();
    check('drag reorders tabs', after[0] === before[1], `${before.join(' | ')} → ${after.join(' | ')}`);
  }
}

// ------------------------------------------------------------------ abort
log('abort');
await page.keyboard.press('Control+t');
await page.waitForSelector('.intro');
await ta.fill('Why does the agent scheduler throw when a pool is missing?');
await ta.press('Enter');
await page.waitForSelector('.composer.running', { timeout: 5000 });
await page.waitForSelector('.thinking.live', { timeout: 5000 });
await page.waitForTimeout(400);
await ta.press('Escape');
await page.waitForFunction(() => !document.querySelector('.composer.running'), null, { timeout: 10_000 }).catch(() => {});
await page.waitForTimeout(300);
const aborted = (await page.locator('.status[data-reason="aborted"]').count()) + (await page.locator('.notice', { hasText: 'aborted' }).count());
check('Esc aborts the run', aborted > 0);
await shot(page, '20-aborted');

// ------------------------------------------------------------------ image attachments
log('images');
{
  // a 48×32 PNG (solid accent-ish color) generated on the fly
  const png = await page.evaluate(async () => {
    const c = document.createElement('canvas');
    c.width = 48;
    c.height = 32;
    const g = c.getContext('2d');
    const grad = g.createLinearGradient(0, 0, 48, 32);
    grad.addColorStop(0, '#7c93ff');
    grad.addColorStop(1, '#4cc38a');
    g.fillStyle = grad;
    g.fillRect(0, 0, 48, 32);
    return c.toDataURL('image/png').split(',')[1];
  });
  await page.locator('.composer input[type=file]').setInputFiles({ name: 'screenshot.png', mimeType: 'image/png', buffer: Buffer.from(png, 'base64') });
  await page.waitForSelector('.composer .thumb');
  await ta.fill('Quick question about this screenshot — short answer please.');
  await shot(page, '21-image-attached');
  await ta.press('Enter');
  await page.waitForSelector('.user .img img', { timeout: 5000 }).catch(() => {});
  check('user message shows the image', (await page.locator('.user .img img').count()) > 0);
  await page.waitForFunction(() => !document.querySelector('.composer.running'), null, { timeout: 20_000 }).catch(() => {});
}

// ------------------------------------------------------------------ web tools, todo plan, tools notice, file links
log('web tools, todo plan, tools notice, file links');
{
  await page.keyboard.press('Control+t');
  await page.waitForSelector('.intro');
  await ta.fill('[web] Why does the demo page break? Check the Svelte docs.');
  await ta.press('Enter');
  // thinking can be opened while it streams; later answers start open and their finished rows stay open
  await page.waitForSelector('.thinking.live .line', { timeout: 10_000 });
  await page.locator('.thinking.live .line').click();
  await page.waitForSelector('.thinking.live .body', { timeout: 3000 }).catch(() => {});
  check('streaming thinking opens while it streams', (await page.locator('.thinking.live .body').count()) === 1);
  await shot(page, '24b-live-thinking');
  await page.waitForSelector('.dock .strip', { timeout: 15_000 });
  await page.waitForTimeout(400);
  const gap = await page.locator('.scroller').evaluate((el) => el.scrollHeight - el.scrollTop - el.clientHeight);
  check('the chat stays at the bottom when the plan strip appears', gap < 48, `${Math.round(gap)}px from the bottom`);
  const bar = await page.locator('.dock .strip .bar').innerText();
  check('the plan shows above the composer while the agent works', /\b\d\/3\b/.test(bar), bar.replace(/\s+/g, ' '));
  await page.locator('.dock .strip .bar').click();
  await page.waitForTimeout(250);
  check('the plan opens as a checklist', (await page.locator('.dock .strip .todo li').count()) === 3);
  await shot(page, '25-todo-strip');
  await page.waitForFunction(() => !document.querySelector('.composer.running'), null, { timeout: 60_000 });
  await page.waitForTimeout(300);
  check('the plan hides once every item is done', (await page.locator('.dock .strip').count()) === 0);
  check('finished thinking of an opened stream stays open', (await page.locator('.thinking.open .body').count()) >= 1);
  const shown = page.locator('.item[data-kind="shown"]');
  check('show_image puts the image in the chat, outside the steps', (await shown.locator('img').count()) === 1 && (await shown.innerText()).includes('The demo as it renders now'));
  await shown.locator('button.frame').click();
  await page.waitForSelector('.lb', { timeout: 3000 }).catch(() => {});
  check('clicking the shown image opens it full size', (await page.locator('.lb').count()) === 1);
  await page.keyboard.press('Escape');
  await page.waitForTimeout(200);
  if (await page.locator('.lb').count()) await page.locator('.lb').click();
  check('a tools notice says which tools are new', (await page.locator('.notice', { hasText: 'Tools changed' }).count()) === 1);

  const closed = page.locator('.group.collapsible .head[aria-expanded="false"]');
  for (let n = 0; n < 12 && (await closed.count()); n++) await closed.first().click();
  await page.waitForTimeout(150);
  for (const label of ['Web search', 'Fetch', 'Screenshot']) {
    const row = page.locator('.tool .line', { hasText: label }).last();
    if (await row.count()) await row.click();
  }
  await page.locator('.tool .line', { hasText: 'Todo' }).first().click();
  await page.waitForTimeout(300);
  check('web_search lists its results', (await page.locator('.tool .hits .hit').count()) === 3);
  check('web_fetch shows the page title and text', (await page.locator('.tool .page .title', { hasText: '$state' }).count()) === 1 && (await page.locator('.tool .page .box').innerText()).includes('reactive state'));
  check('screenshot shows the image and the console error', (await page.locator('.tool img.shot').count()) === 1 && (await page.locator('.tool .box', { hasText: 'count is undefined' }).count()) === 1);
  check('todo_write shows a checklist', (await page.locator('.tool .todo li').count()) === 3);
  const badges = await page.locator('.tool .np-badge').allInnerTexts();
  check('badges: results, size, errors, progress', ['3 results', '1 console error', '0/3'].every((b) => badges.includes(b)), badges.join(' | '));
  await page.locator('.tool', { has: page.locator('.label', { hasText: 'Screenshot' }) }).last().scrollIntoViewIfNeeded();
  await shot(page, '26-web-tools');

  // links to files open with the operating system, not inside the app
  const links = page.locator('.md a.file-link');
  check('file links in the answer are marked', (await links.count()) === 2, String(await links.count()));
  const before = page.url();
  await links.first().click();
  await page.waitForTimeout(300);
  const opened = await rpcCall('mock.filesOpened');
  check('clicking a file link asks the host to open it', opened.some((p) => p.replace(/\\/g, '/').endsWith('web/src/App.svelte')), JSON.stringify(opened));
  check('the app does not navigate away', page.url() === before && (await page.locator('.composer textarea').count()) === 1);
  check('web links still open in a new window', (await page.locator('.md a[target="_blank"]', { hasText: 'docs' }).count()) === 1);
}

// ------------------------------------------------------------------ ssh tools (reuse the shell, read and diff views)
log('ssh tools');
{
  await page.keyboard.press('Control+t');
  await page.waitForSelector('.intro');
  await ta.fill('[ssh] Make the demo site on nuc listen on 8080.');
  await ta.press('Enter');
  await page.waitForSelector('.composer.running', { timeout: 5000 }).catch(() => {});
  await page.waitForFunction(() => !document.querySelector('.composer.running'), null, { timeout: 60_000 });
  await page.waitForTimeout(300);
  const closed = page.locator('.group.collapsible .head[aria-expanded="false"]');
  for (let n = 0; n < 12 && (await closed.count()); n++) await closed.first().click();
  await page.waitForTimeout(150);
  for (const label of [/^SSH$/, /^SSH read$/, /^SSH edit$/]) {
    const row = page.locator('.tool .line[aria-expanded="false"]', { has: page.locator('.label', { hasText: label }) }).last();
    if (await row.count()) await row.click();
  }
  await page.waitForTimeout(300);
  check('ssh_run shows the host as the prompt, and the output', (await page.locator('.tool .shell .prompt', { hasText: 'nuc$' }).count()) === 1 && (await page.locator('.tool .shell .out').last().innerText()).includes('active'));
  check('ssh_read shows the remote file', (await page.locator('.tool .read .path', { hasText: 'nuc:/etc/nginx/sites-enabled/demo.conf' }).count()) === 1 && (await page.locator('.tool .read .code').last().innerText()).includes('server_name demo.local;'));
  check('ssh_edit shows the diff', (await page.locator('.tool .diff .dl.add', { hasText: 'listen 8080;' }).count()) === 1 && (await page.locator('.tool .diff .dl.del', { hasText: 'listen 80;' }).count()) === 1);
  const summaries = await page.locator('.tool .summary').allInnerTexts();
  check('the summaries name the host', summaries.includes('nuc · cd /srv/demo') && summaries.includes('nuc:/etc/nginx/sites-enabled/demo.conf') && summaries.includes('nuc:/var/log/nginx/error.log → logs/nuc-error.log'), summaries.join(' | '));
  const badges = await page.locator('.tool .np-badge, .tool .diffbadge').allInnerTexts();
  check('badges: exit code and the diff size', badges.includes('exit 0') && badges.some((b) => b.replace(/\s+/g, ' ') === '+1 −1'), badges.join(' | '));
  check('remote files get no "Open file" button', (await page.locator('.tool .openfile').count()) === 0);
  await page.locator('.tool', { has: page.locator('.label', { hasText: /^SSH edit$/ }) }).last().scrollIntoViewIfNeeded();
  await shot(page, '26c-ssh-tools');
}

// ------------------------------------------------------------------ fast steps: the chat never jumps while the agent works
log('fast steps: layout stability');
{
  const size = page.viewportSize();
  await page.setViewportSize({ width: 1280, height: 560 });
  await page.keyboard.press('Control+t');
  await page.waitForSelector('.intro');
  // the new user message's position every frame: pinned to the bottom, it may only move up while the chat grows
  const runFast = async (text) => {
    await ta.fill(text);
    await page.evaluate(() => {
      const users = document.querySelectorAll('.item[data-kind="user"]').length;
      const rec = (window.__rec = { frames: [], on: true });
      // a text's baseline: a zero-size inline-block at its end sits on it
      const baseline = (el) => {
        const p = document.createElement('span');
        p.style.cssText = 'display:inline-block;width:0;height:0';
        el.appendChild(p);
        const y = p.getBoundingClientRect().bottom;
        p.remove();
        return y;
      };
      (function tick() {
        const all = document.querySelectorAll('.item[data-kind="user"]');
        const u = all.length > users ? all[all.length - 1] : null;
        // the folded line: its step label, the step's (smaller, mono) summary and "N steps" on one baseline
        const lbl = document.querySelector('.group .latest .lbl');
        const sum = document.querySelector('.group .latest .sum');
        const cnt = lbl?.closest('.head')?.querySelector('.count');
        const off = lbl && sum && cnt ? Math.max(Math.abs(baseline(sum) - baseline(lbl)), Math.abs(baseline(lbl) - baseline(cnt))) : null;
        rec.frames.push({ top: u ? u.getBoundingClientRect().top : null, busy: !!document.querySelector('.composer.running'), latest: document.querySelector('.group .latest')?.innerText ?? null, off });
        if (rec.on) requestAnimationFrame(tick);
      })();
    });
    await ta.press('Enter');
    await page.waitForSelector('.composer.running', { timeout: 5000 }).catch(() => {});
    await page.waitForFunction(() => !document.querySelector('.composer.running'), null, { timeout: 60_000 });
    await page.waitForTimeout(500);
    return page.evaluate(() => {
      const f = window.__rec.frames;
      window.__rec.on = false;
      const downs = { run: [], end: [] };
      let busySeen = false;
      for (let i = 1; i < f.length; i++) {
        const a = f[i - 1];
        const b = f[i];
        if (b.busy) busySeen = true;
        if (a.top == null || b.top == null || b.top - a.top <= 1) continue;
        (busySeen && !b.busy ? downs.end : downs.run).push(Math.round(b.top - a.top));
      }
      const offs = f.map((x) => x.off).filter((x) => x != null);
      return { frames: f.length, downs, latest: [...new Set(f.map((x) => x.latest).filter(Boolean))], offFrames: offs.length, maxOff: offs.length ? Math.max(...offs) : null };
    });
  };
  await runFast('[fast] Fill the chat first.');
  const done = await runFast('[fast] Again, measured.');
  check('fast steps never push the chat down while the agent works', done.frames > 30 && done.downs.run.length === 0, `${done.frames} frames; down: ${done.downs.run.join(', ') || 'none'}`);

  await page.keyboard.press('Control+,');
  await page.waitForSelector('.dialog');
  await page.locator('.np-seg button', { hasText: 'Folded' }).click();
  await page.keyboard.press('Escape');
  await page.waitForTimeout(200);
  const folded = await runFast('[fast] Folded from the start.');
  check('folded steps: nothing moves down, during the run or when it ends', folded.frames > 30 && folded.downs.run.length + folded.downs.end.length === 0, `run: ${folded.downs.run.join(', ') || 'none'}; end: ${folded.downs.end.join(', ') || 'none'}`);
  check('folded steps: the line shows the latest step while the agent works', folded.latest.some((l) => /Bash|Read|Edit|Grep|Find|Thinking/.test(l)), folded.latest.slice(0, 4).join(' | '));
  check('folded steps: the step, its command and "N steps" share one baseline', folded.offFrames > 0 && folded.maxOff < 0.5, `${folded.offFrames} frames, off by up to ${folded.maxOff?.toFixed(2)}px`);
  const wrapped = await page.evaluate(() =>
    [...document.querySelectorAll('.group.collapsible .head')].filter((h) => [...h.querySelectorAll('.count, .took')].some((e) => e.getClientRects().length > 1 || e.offsetHeight > 20)).length,
  );
  check('folded steps: "N steps · time" stays on one line', wrapped === 0, `${wrapped} wrapped`);
  const lastRun = page.locator('.item[data-kind="steps"]');
  check('folded steps: every group of this run is one line', (await page.locator('.group .steps').count()) === 0 && (await lastRun.count()) > 0);
  await shot(page, '27-folded-steps');
  await page.keyboard.press('Control+,');
  await page.waitForSelector('.dialog');
  await page.locator('.np-seg button', { hasText: 'Fold when done' }).click();
  await page.keyboard.press('Escape');
  await page.waitForTimeout(200);
  await page.setViewportSize(size);
}

// ------------------------------------------------------------------ compaction banner: kept until the model answers again
log('compaction banner');
{
  await page.keyboard.press('Control+t');
  await page.waitForSelector('.intro');
  await ta.fill('hi');
  await ta.press('Enter');
  await page.waitForSelector('.composer.running', { timeout: 5000 }).catch(() => {});
  await page.waitForFunction(() => !document.querySelector('.composer.running'), null, { timeout: 30_000 });
  await ta.fill('[compact] Keep going.');
  await ta.press('Enter');
  const texts = async () => (await page.locator('.banner .banner-text').allInnerTexts()).join(' | ');
  await page.locator('.banner', { hasText: 'Compacting context' }).waitFor({ timeout: 5000 });
  await page.waitForTimeout(6500); // longer than a transient banner lasts; the mock's summarizer takes 7 s
  const summarizing = await texts();
  check('compaction banner: kept while the summary is written', summarizing.includes('Compacting context'), summarizing || 'no banner');
  await page.locator('.banner', { hasText: 'Context compacted' }).waitFor({ timeout: 10_000 });
  await page.waitForTimeout(2000); // the model call has started (a stream.start); its first token comes after 3 s
  const reading = await texts();
  check('compaction banner: kept until the model answers again', reading.includes('Context compacted'), reading || 'no banner');
  await shot(page, '28-compaction-banner');
  await page.getByText('Picking up from the summary').first().waitFor({ timeout: 10_000 });
  await page.waitForTimeout(300);
  const after = await texts();
  check('compaction banner: gone once the model answers', !after.includes('Context compacted'), after);
  await page.waitForFunction(() => !document.querySelector('.composer.running'), null, { timeout: 30_000 });
}

// ------------------------------------------------------------------ goals: /goal, the strip, pause, resume, achieved
log('goals');
{
  await page.keyboard.press('Control+t');
  await page.waitForSelector('.intro');
  await ta.fill('/goal Make the demo page render again');
  await ta.press('Enter');
  await page.waitForSelector('.goal[data-status="active"]', { timeout: 5000 });
  check('/goal sets the goal and the strip shows it', (await page.locator('.goal .obj').innerText()).includes('Make the demo page render again'));
  await page.waitForSelector('.composer.running', { timeout: 5000 }).catch(() => {});
  await page.locator('.goal .act[title^="Pause"]').click();
  await page.waitForSelector('.goal[data-status="paused"]', { timeout: 5000 });
  await page.waitForFunction(() => !document.querySelector('.composer.running'), null, { timeout: 30_000 });
  await page.waitForTimeout(400);
  check(
    'pausing lets the current pass finish and starts no new one',
    (await page.locator('.notice', { hasText: 'automatic continuation' }).count()) === 0 && (await page.locator('.goal .reason').innerText()).includes('Paused by the user'),
  );
  await shot(page, '28-goal-paused');
  await page.locator('.goal .act[title="Resume"]').click();
  await page.waitForSelector('.goal[data-status="complete"]', { timeout: 30_000 });
  await page.waitForFunction(() => !document.querySelector('.composer.running'), null, { timeout: 30_000 });
  await page.waitForTimeout(300);
  const goalNotices = await page.locator('.notice', { has: page.locator('.label', { hasText: /^Goal$/ }) }).count();
  check('goal notices: set, resumed, one automatic continuation', goalNotices === 3, String(goalNotices));
  check('the goal_update row shows complete', (await page.locator('.tool .np-badge', { hasText: 'complete' }).count()) >= 1);
  check(
    'the strip says achieved, with the summary',
    (await page.locator('.goal .state').innerText()) === 'Achieved' && (await page.locator('.goal .reason').innerText()).includes('The page renders'),
  );
  await shot(page, '29-goal-achieved');
  await page.locator('.goal .act[title="Dismiss"]').click();
  await page.waitForTimeout(300);
  check('dismissing removes the strip', (await page.locator('.goal').count()) === 0);
}

// ------------------------------------------------------------------ folder picker (fs.dirs) + add project
log('projects: folder picker');
await page.locator('.panel.left .strip-tab', { hasText: 'Projects' }).click();
if (!(await page.locator('.panel.left.open').count())) await page.locator('.panel.left .strip-tab', { hasText: 'Projects' }).click();
await page.locator('.panel.left .head .np-icon-btn[title="Add project"]').click();
await page.waitForSelector('.projects-dialog');
check('Add project opens the project dialog', (await page.locator('.projects-dialog .head .title').innerText()) === 'New project');
await page.locator('.projects-dialog .pathrow .np-btn').first().click();
await page.waitForSelector('.picker .bar input', { timeout: 5000 });
await page.keyboard.press('Escape');
await page.waitForTimeout(200);
check('Esc closes the folder picker, not the project dialog', (await page.locator('.picker').count()) === 0 && (await page.locator('.projects-dialog').count()) === 1);
await page.locator('.projects-dialog .pathrow .np-btn').first().click();
await page.waitForSelector('.picker .bar input', { timeout: 5000 });
await page.waitForTimeout(200);
await page.locator('.picker .bar input').fill(repo);
await page.locator('.picker .bar input').press('Enter');
await page.waitForTimeout(300);
await page.locator('.picker .dir', { hasText: 'web' }).first().click();
await shot(page, '22-folder-picker');
await page.locator('.dialog .np-btn-primary', { hasText: 'Select folder' }).click();
const picked = await page.locator('.projects-dialog .pathrow input').inputValue();
check('folder picker fills the path', picked.endsWith('/web') || picked.endsWith('\\web'), picked);
check('the folder name becomes the project name', (await page.locator('.projects-dialog input.np-input:not(.np-mono)').inputValue()) === 'web');
await page.locator('.projects-dialog .np-btn-primary', { hasText: 'Create project' }).click();
await page.waitForTimeout(300);
check('project added', (await page.locator('.panel.left .prow .pname', { hasText: 'web' }).count()) > 0);
check('the project dialog closed', (await page.locator('.projects-dialog').count()) === 0);

// ------------------------------------------------------------------ plugin hot reload + load error
log('plugin tab hot reload / error');
{
  await openStripTab('right', 'Sample');
  await page.waitForSelector('.plugin-root .sample');
  // several plugin tabs stay mounted (hidden); watch the sample's root
  await page.evaluate(() => (window.__e2eRoot = document.querySelector('.plugin-root:has(.sample)')));
  const uiJs = path.join(repo, 'web/mock/sample-plugin/wwwroot/ui.js');
  const touch = () => {
    const t = new Date();
    fs.utimesSync(uiJs, t, t);
  };
  {
    // the mock polls the bundle's mtime (fs.watchFile, 500ms); touch again if a poll was missed
    for (let i = 0; i < 2; i++) {
      touch();
      const ok = await page.waitForFunction(() => window.__e2eRoot && !window.__e2eRoot.isConnected, null, { timeout: 4000 }).then(() => true).catch(() => false);
      if (ok) break;
      await sleep(600);
    }
    await page.waitForSelector('.plugin-root .sample', { timeout: 5000 }).catch(() => {});
    const remounted = await page.evaluate(() => !window.__e2eRoot.isConnected && !!document.querySelector('.plugin-root .sample'));
    check('plugin tab remounts on new version (ui.changed)', remounted);

    await page.route('**/plugins/netpi.sample/ui.js*', (r) => r.fulfill({ status: 500, body: 'boom' }));
    await sleep(1100); // mtime has 1ms resolution but the watcher polls every 500ms
    touch();
    await page.waitForSelector('.plugin-tab .state.error', { timeout: 5000 }).catch(() => {});
    check('plugin load error state', (await page.locator('.plugin-tab .state.error').count()) > 0);
    await shot(page, '23-plugin-error');
    await page.unroute('**/plugins/netpi.sample/ui.js*');
    await page.locator('.plugin-tab .state.error button', { hasText: 'Retry' }).first().click();
    await page.waitForSelector('.plugin-root .sample', { timeout: 5000 }).catch(() => {});
    check('plugin retry after error', (await page.locator('.plugin-root .sample').count()) > 0);
  }
}

// ------------------------------------------------------------------ notifications: what the UI asks the desktop app for
// A page of its own with a stand-in for the desktop app's bridge (chrome.webview), so no other section sees it.
log('notifications');
{
  const p = await page.context().newPage();
  await p.addInitScript(() => {
    const listeners = [];
    window.__posted = [];
    window.chrome = window.chrome || {};
    window.chrome.webview = {
      postMessage: (m) => window.__posted.push(JSON.parse(JSON.stringify(m))),
      addEventListener: (type, f) => type === 'message' && listeners.push(f),
      emit: (data) => listeners.forEach((f) => f({ data })),
    };
  });
  await p.goto(`${BASE}/?token=dev`);
  await p.waitForSelector('.srow', { timeout: 15_000 }).catch(() => {});
  const box = p.locator('textarea').first();
  const run = async (text) => {
    await box.fill(text);
    await box.press('Enter');
    await p.waitForSelector('.composer.running', { timeout: 5000 }).catch(() => {});
    await p.waitForFunction(() => !document.querySelector('.composer.running'), null, { timeout: 60_000 });
    await p.waitForTimeout(2200); // the UI waits 1.5 s: a chat that goes on by itself says nothing
  };
  await p.keyboard.press('Control+t');
  await p.waitForSelector('.intro');
  await run('[fast] notify me');
  const done = await p.evaluate(() => window.__posted.filter((m) => m.type === 'notify'));
  check('notifications: a finished run asks for one', done.length === 1 && done[0].body === 'Finished' && /notify me/.test(done[0].title), JSON.stringify(done));
  await p.keyboard.press('Control+t');
  await p.waitForSelector('.intro');
  await run('[budget] Summarize the repo with the paid model.');
  const budget = await p.evaluate(() => window.__posted.filter((m) => m.type === 'notify').slice(1));
  check('notifications: the budget ask is one, without a "Finished" after it', budget.length === 1 && /budget/.test(budget[0].body), JSON.stringify(budget));
  // clicking the notification (the desktop app says openSession) opens that chat
  await p.keyboard.press('Control+t');
  await p.waitForSelector('.intro');
  await p.evaluate((sessionId) => window.chrome.webview.emit({ type: 'openSession', sessionId }), done[0]?.sessionId);
  await p.waitForTimeout(500);
  check('notifications: a click opens the chat', (await p.locator('.topbar .tab.active').getAttribute('data-tab')) === done[0]?.sessionId);
  await p.close();
}

// ------------------------------------------------------------------ reconnect
if (!EXTERNAL) {
  log('reconnect');
  await page.locator('.panel.left .strip-tab', { hasText: 'Sessions' }).click();
  // Forks sort ahead of the original, and disappear from the mock's memory when it restarts.
  await page.locator('.srow', { has: page.locator('.title', { hasText: /^Fix streaming reconnect bug$/ }) }).click();
  await page.waitForTimeout(300);
  await stopServer();
  await page.waitForSelector('.conn[data-status="reconnecting"]', { timeout: 5000 }).catch(() => {});
  check('connection indicator shows reconnecting', (await page.locator('.conn[data-status="reconnecting"]').count()) > 0);
  await shot(page, '24-reconnecting');
  await startServer();
  await page.waitForSelector('.conn[data-status="open"]', { timeout: 15_000 }).catch(() => {});
  check('reconnects after the server restarts', (await page.locator('.conn[data-status="open"]').count()) > 0);
  // the session is refetched and scoped events flow again (re-subscribed)
  await ta.fill('Quick check after reconnect — short answer.');
  await ta.press('Enter');
  const streamed = await page.waitForSelector('.composer.running', { timeout: 8000 }).then(() => true).catch(() => false);
  check('events flow after reconnect (resubscribed)', streamed);
  await page.waitForFunction(() => !document.querySelector('.composer.running'), null, { timeout: 20_000 }).catch(() => {});
}

// ------------------------------------------------------------------ vite dev server (proxy + ?token=)
if (!EXTERNAL && !argv.includes('--no-dev')) {
  log('vite dev server');
  const vite = spawn(process.execPath, [path.join(repo, 'node_modules/vite/bin/vite.js'), '--config', 'web/vite.config.js', '--port', '5199', '--strictPort', '--host', '127.0.0.1'], {
    cwd: repo,
    env: { ...process.env, NETPI_URL: BASE },
    stdio: ['ignore', 'pipe', 'pipe'],
  });
  try {
    await new Promise((resolve, reject) => {
      const t = setTimeout(() => reject(new Error('vite did not start')), 20_000);
      vite.stdout.on('data', (d) => /Local:|ready in/.test(String(d)) && (clearTimeout(t), resolve()));
    });
    const dev = await openApp({ url: 'http://127.0.0.1:5199/?token=dev' });
    await dev.page.waitForSelector('.srow', { timeout: 15_000 }).catch(() => {});
    const rows = await dev.page.locator('.srow').count();
    const url = dev.page.url();
    check('dev server: proxied ws + ?token= auth', rows > 0 && !url.includes('token='), `${rows} sessions, url ${url}`);
    // plugin bundles are fetched through the proxy with the dev cookie (whichever plugin tab the saved layout shows)
    await dev.page.waitForSelector('.plugin-root > *', { timeout: 10_000 }).catch(() => {});
    check('dev server: plugin tab loads through the proxy', (await dev.page.locator('.plugin-root > *').count()) > 0);
    const devErrors = dev.errors.filter((e) => !/favicon|\[vite\]/.test(e));
    check('dev server: no console errors', devErrors.length === 0, devErrors.slice(0, 3).join(' | '));
    await dev.browser.close();
  } catch (e) {
    check('dev server', false, e.message);
  } finally {
    vite.kill();
  }
}

// ------------------------------------------------------------------ summary
// expected noise from the deliberate plugin 500 and the server restart
const EXPECTED = /favicon|Failed to fetch dynamically imported module|failed to load tab|status of 500|WebSocket connection to|ERR_CONNECTION_REFUSED/;
const bad = errors.filter((e) => !EXPECTED.test(e));
check('no console errors', bad.length === 0, bad.slice(0, 5).join(' | '));
await browser.close();
await stopServer();
const failed = results.filter((r) => !r.ok);
log(`\n${results.length - failed.length}/${results.length} checks passed`);
process.exit(failed.length ? 1 : 0);
