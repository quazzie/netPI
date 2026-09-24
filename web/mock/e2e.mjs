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
await ta.fill('Why does the lane scheduler throw when a pool is missing? Make it fail with a clear message.');
await ta.press('Enter');
await page.waitForSelector('.streaming .thinking', { timeout: 5000 });
await page.waitForTimeout(700);
await shot(page, '04-streaming-thinking');
await page.waitForSelector('.tool[data-status="ok"]', { timeout: 15000 });

// steer + queue while running
await ta.fill('Also make Release() return early for unknown pools.');
await ta.press('Enter');
await ta.fill('Afterwards, write a one-line changelog entry.');
await ta.press('Alt+Enter');
await page.waitForSelector('.queue .chip', { timeout: 3000 }).catch(() => {});
const chips = await page.locator('.queue .chip').count();
check('queue chips while running', chips >= 1, `${chips} chip(s)`);
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

// ------------------------------------------------------------------ popups: model picker, commands, mentions
log('composer popups');
await page.locator('.composer .pick').first().click();
await page.waitForTimeout(200);
await shot(page, '10-model-picker');
await page.keyboard.press('Escape');
await ta.fill('');
await ta.type('/');
await page.waitForTimeout(150);
await shot(page, '11-commands');
check('command popup', (await page.locator('.popup .opt').count()) >= 7);
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
const rpcCall = (method, params = {}) =>
  fetch(`${BASE}/api/rpc/${method}`, { method: 'POST', headers: { 'X-NetPI-Token': 'dev', 'content-type': 'application/json' }, body: JSON.stringify(params) }).then((r) => r.json());
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
  check('files: click inserts an @mention', (await ta.inputValue()) === '@web/src/lib/markdown.js ', await ta.inputValue());
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
  await pick(page.locator('.topbar .chip'), 'website');
  let s = await rpcCall('sessions.get', { id: 'ses_scratch' });
  check('project picker (top bar) attaches the project', s.projectId === idOf('website'), String(s.projectId));
  check('project picker (top bar) posts the project notice', (await page.locator('.notice', { hasText: 'website' }).count()) > 0);
  await pick(page.locator('.header .chip'), 'aiproxy');
  s = await rpcCall('sessions.get', { id: 'ses_scratch' });
  check('project picker (chat header) attaches the project', s.projectId === idOf('aiproxy'), String(s.projectId));
  check('project picker (chat header) posts the project notice', (await page.locator('.notice', { hasText: 'aiproxy' }).count()) > 0);
  check('chips show the attached project', /aiproxy/.test(await page.locator('.topbar .chip').innerText()) && /aiproxy/.test(await page.locator('.header .chip').innerText()));
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
  await page.locator('.topbar .chip').click();
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
  check('the top bar chip follows the rename', /website-renamed/.test(await page.locator('.topbar .chip').innerText()));
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
  await page.locator('.topbar .chip').click();
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
  check('the chip shows the new project', /mock/.test(await page.locator('.topbar .chip').innerText()));
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

// project picker (top bar chip), help, a server slash command
await page.locator('.topbar .chip').click();
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
await ta.fill('Why does the lane scheduler throw when a pool is missing?');
await ta.press('Enter');
await page.waitForSelector('.composer.running', { timeout: 5000 });
await page.waitForSelector('.streaming', { timeout: 5000 });
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
  await page.waitForSelector('.dock .strip', { timeout: 15_000 });
  const bar = await page.locator('.dock .strip .bar').innerText();
  check('the plan shows above the composer while the agent works', /\b\d\/3\b/.test(bar), bar.replace(/\s+/g, ' '));
  await page.locator('.dock .strip .bar').click();
  await page.waitForTimeout(250);
  check('the plan opens as a checklist', (await page.locator('.dock .strip .todo li').count()) === 3);
  await shot(page, '25-todo-strip');
  await page.waitForFunction(() => !document.querySelector('.composer.running'), null, { timeout: 60_000 });
  await page.waitForTimeout(300);
  check('the plan hides once every item is done', (await page.locator('.dock .strip').count()) === 0);
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

// ------------------------------------------------------------------ reconnect
if (!EXTERNAL) {
  log('reconnect');
  await page.locator('.panel.left .strip-tab', { hasText: 'Sessions' }).click();
  await page.locator('.srow', { hasText: 'Fix streaming reconnect bug' }).first().click();
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
  const streamed = await page.waitForSelector('.streaming, .composer.running', { timeout: 8000 }).then(() => true).catch(() => false);
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
