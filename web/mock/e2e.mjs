#!/usr/bin/env node
// End-to-end walkthrough against the mock server: drives the UI with Playwright, takes screenshots
// (1600×1000), and reports console errors + timings. By default it starts its own mock server on :7432
// (MOCK_SPEED=3) so it can also test reconnects; pass --url to use a running server instead.
//
//   npm run build && npm run e2e
//
// FROZEN: no new section or check goes here. A new UI test is a script in tests/NetPI.E2E/ui against the real server;
// these sections move there one by one (docs/plans/2026-10-11-retire-mock-ui-suite.md).
//   node web/mock/e2e.mjs [--out dir] [--only <section>,<section>] [--url http://127.0.0.1:7431] [--no-dev]
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
// Sections: each log('name') header below starts one. --only runs just the named sections (a name, or a part of one, case-insensitive):
// every other section is skipped with its setup, it is not merely left unreported. --list prints the names. A name that matches
// nothing is an error (exit 2) before anything starts, so a typo cannot be a green empty run. The header pattern takes the
// end of the line as \r?\n, or a section written as `if (…) { log(…)` is invisible to --only on a CRLF checkout (Windows).
const SECTIONS = [...fs.readFileSync(fileURLToPath(import.meta.url), 'utf8').matchAll(/^(?:if \(!EXTERNAL[^\n]*\{\r?\n  )?log\('([^'\n]+)'\);/gm)].map((m) => m[1]);
if (argv.includes('--list')) {
  console.log(SECTIONS.join('\n'));
  process.exit(0);
}
function resolveOnly(spec) {
  const out = new Set();
  const bad = [];
  // one whole name (several contain commas), or several separated by | (or by commas when none of them has one)
  const parts = SECTIONS.includes(spec.trim()) ? [spec.trim()] : spec.includes('|') ? spec.split('|') : spec.split(',');
  for (const raw of parts.map((s) => s.trim()).filter(Boolean)) {
    const hits = SECTIONS.includes(raw) ? [raw] : SECTIONS.filter((s) => s.toLowerCase().includes(raw.toLowerCase()));
    if (!hits.length) bad.push(raw);
    for (const h of hits) out.add(h);
  }
  if (bad.length || !out.size) {
    console.error(`--only: no section matches ${bad.map((b) => `'${b}'`).join(', ') || '(nothing given)'}. Sections:\n  ${SECTIONS.join('\n  ')}`);
    process.exit(2);
  }
  return out;
}
// NEEDS: sections that build on state an earlier section leaves behind (an open chat, tabs, a project). A focused run first runs
// those, unreported (checks and screenshots follow ONLY), then the section. Keep this list honest: `node web/mock/e2e.mjs --only <name>`
// for every section is the audit that proves each one stands on its own plus what is listed here.
const NEEDS = {
  "composer popups": ["new session + agent run"],
  "popups survive a chat update": ["new session + agent run"],
  "composer: narrow widths": ["new session + agent run"],
  "tabs": ["new session + agent run"],
  "plan mode: the pill, the plan card and its decisions": ["new session + agent run"],
  "guardrails: a tool call waits for your OK": ["new session + agent run"],
  "settings": ["new session + agent run"],
  "images": ["new session + agent run"],
  "budget: chat cost, Work tab, a chat stopped by the budget": ["new session + agent run"],
};
const ONLY = argVal('--only') ? resolveOnly(argVal('--only')) : null;
const RUN = ONLY ? new Set(ONLY) : null;
if (RUN) for (const n of RUN) for (const need of NEEDS[n] ?? []) RUN.add(need);
const want = (name) => !ONLY || RUN.has(name);
const EXTERNAL = argVal('--url');
const PORT = Number(argVal('--port') ?? 7432); // a different port per run lets focused runs go side by side
const BASE = EXTERNAL ?? `http://127.0.0.1:${PORT}`;
const rpcCall = (method, params = {}) =>
  fetch(`${BASE}/api/rpc/${method}`, { method: 'POST', headers: { 'X-NetPI-Token': 'dev', 'content-type': 'application/json' }, body: JSON.stringify(params) }).then((r) => r.json());
fs.mkdirSync(OUT, { recursive: true });

const results = [];
let section = ''; // the current section header: what --only gates the checks by
const log = (...a) => {
  // a top-level single-string header names the section; the "  ✓ …" and "  📸 …" lines and the final summary don't
  if (a.length === 1 && typeof a[0] === 'string' && !a[0].startsWith(' ') && !a[0].startsWith('\n')) section = a[0];
  console.log(...a);
};
const shot = async (page, name) => {
  if (ONLY && !ONLY.has(section)) return;
  await page.screenshot({ path: path.join(OUT, `${name}.png`) });
  log(`  📸 ${name}.png`);
};
function check(name, ok, detail = '') {
  if (ONLY && !ONLY.has(section)) return; // --only: report the chosen sections' checks only
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
// When the run dies on an exception (a wait that timed out), leave what the page looked like: a failure that only shows up late in a
// long run has to be readable without running it again. FAILED.png and FAILED.txt (the section, the page's text, console errors).
// what the page itself did, for that file: the rpc frames it sent and when its socket closed (Playwright sees both), and every toast
// that showed (an error toast is gone long before the failure screenshot is taken)
const pageWs = [];
const stamp = () => new Date().toISOString().slice(11, 23);
// the page's own rpc calls, answered or not: a check waits for the reload an event caused (pageRpcDone) instead of guessing a delay
const pageRpc = [];
page.on('websocket', (ws) => {
  pageWs.push(`${stamp()} socket opened ${ws.url()}`);
  ws.on('framesent', (f) => {
    const p = String(f.payload);
    if (p.includes('"t":"rpc"')) { try { const m = JSON.parse(p); pageWs.push(`${stamp()} sent rpc ${m.id} ${m.m}`); pageRpc.push({ id: m.id, m: m.m, answered: false }); } catch {} }
  });
  ws.on('framereceived', (f) => {
    const p = String(f.payload);
    if (p.includes('"t":"res"')) { try { const id = JSON.parse(p).id; const c = pageRpc.findLast((r) => r.id === id); if (c) c.answered = true; } catch {} }
  });
  ws.on('close', () => pageWs.push(`${stamp()} socket closed`));
  ws.on('socketerror', (e) => pageWs.push(`${stamp()} socket error ${e}`));
});
await page.addInitScript(() => {
  window.__toasts = [];
  new MutationObserver((records) => {
    for (const r of records) for (const n of r.addedNodes) {
      const t = n.nodeType === 1 ? (n.matches?.('.toast') ? n : n.querySelector?.('.toast')) : null;
      if (t) window.__toasts.push(`${new Date().toISOString().slice(11, 23)} ${t.innerText}`);
    }
  }).observe(document, { childList: true, subtree: true });
});
process.on('uncaughtException', async (e) => {
  console.error(e);
  try {
    await page.screenshot({ path: path.join(OUT, 'FAILED.png') });
    const text = await page.locator('body').innerText().catch(() => '');
    // what the mock saw: the last rpc frames it received and its clients, so a frame that never arrived shows
    const frames = await rpcCall('mock.wsLog').then((r) => JSON.stringify(r)).catch(() => '(mock.wsLog unavailable)');
    fs.writeFileSync(path.join(OUT, 'FAILED.txt'), `${e?.stack ?? e}\n\nsection: ${section}\nurl: ${page.url()}\ntabs: ${await page.locator('.topbar .tab').allInnerTexts().then((t) => t.join(' | ')).catch(() => '?')}\n\nconsole errors:\n${errors.join('\n')}\n\ntoasts shown:\n${(await page.evaluate(() => window.__toasts ?? []).catch(() => [])).join('\n')}\n\nthe page's rpc frames (last 40) and socket events:\n${pageWs.slice(-40).join('\n')}\n\nmock rpc frames and clients:\n${frames}\n\npage text:\n${text}\n`);
    console.error(`evidence: ${path.join(OUT, 'FAILED.png')} and FAILED.txt`);
  } catch {}
  process.exit(1);
});
await page.evaluate(() => localStorage.clear());
await page.goto(`${BASE}/`);
await page.waitForSelector('.welcome .np-btn-primary', { timeout: 10_000 });
await page.waitForTimeout(300);
await shot(page, '01-welcome');

// ------------------------------------------------------------------ helpers every section uses (locators and a function: no section state)
// the context ring: hover tooltip, and a press for the breakdown
const ring = page.locator('.composer .ring');
const ta = page.locator('.composer textarea');
const handle = page.locator('.panel.right .resizer');
const think = page.locator('.thinking .line').first();
const edit = page.locator('.tool', { has: page.locator('.label', { hasText: 'Edit' }) }).last();
// ------------------------------------------------------------------ built-in plugin tabs (Work, Ideas, Diagnostics, Files)
/** Select a strip tab without toggling the panel closed when it is already the active one. */
async function openStripTab(side, name) {
  const t = page.locator(`.panel.${side} .strip-tab`, { hasText: name });
  if ((await t.getAttribute('aria-selected')) !== 'true') await t.click();
  if (!(await page.locator(`.panel.${side}.open`).count())) await t.click();
}
/**
 * Open a new chat tab (Ctrl+T) and wait until it is the one showing. Waiting for `.intro` alone passes at once when the tab we were on is
 * itself an empty chat: the next fill then lands in the OLD tab's composer while the new session is still being created, and the Enter
 * that follows hits the new, empty one, so the message is never sent (what killed the live-thinking step of "web tools" after certain
 * earlier sections: the page sent sessions.create and agents.use, and no agent.send).
 */
async function newTab() {
  const before = await page.locator('.topbar .tab').count();
  await page.keyboard.press('Control+t');
  await page.waitForFunction((n) => document.querySelectorAll('.topbar .tab').length > n, before, { timeout: 10_000 });
  await page.waitForSelector('.intro');
}
/** The Sessions tab's search box (the Projects tab, once visited, stays in the panel with a search box of its own). */
const sessionSearch = () => page.locator('.panel.left').getByPlaceholder('Search sessions');
/**
 * A chat's row in the Sessions list, shown. Only Today's group is open by default (Yesterday, Previous 7 days and Earlier are folded), so a
 * row that is not shown is found through the session search, which shows matches inside folded groups. clearSessionSearch() undoes that.
 */
async function sessionRow(title) {
  await openStripTab('left', 'Sessions');
  const row = page.locator('.panel.left .srow', { hasText: title }).first();
  if (await row.isVisible()) return row;
  await sessionSearch().fill(title);
  await row.waitFor({ timeout: 5_000 });
  return row;
}
const clearSessionSearch = () => sessionSearch().fill('');
/** Wait until the page has called `method` since `since` (pageRpc.length taken before the change) and had its answer. */
async function pageRpcDone(method, since, timeout = 10_000) {
  for (const end = Date.now() + timeout; Date.now() < end; await sleep(50))
    if (pageRpc.slice(since).some((r) => r.m === method && r.answered)) return true;
  return false;
}
/** Open a chat by its title from the Sessions list (see sessionRow), leaving the search empty. */
async function openSession(title) {
  await (await sessionRow(title)).click();
  await clearSessionSearch();
}
const right = page.locator('.panel.right > .body');

// ------------------------------------------------------------------ long session: render time + pruning
if (want('long session')) {
log('long session');
var t0 = Date.now();
await page.locator('.srow', { hasText: 'Lane scheduler hardening' }).first().click();
await page.waitForSelector('.content .item[data-kind="text"]');
var openMs = Date.now() - t0;
var perf = await page.evaluate(async () => {
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
var hasNewer = await page.locator('.earlier button', { hasText: 'jump to latest' }).count();
// render cost of the full (capped) window: switch to another session and back (the store is cached)
var msgsInWindow = await page.locator('.content .item').count();
await newTab();
var switchMs = await page.evaluate(async () => {
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
  // load newer pages forward with afterSeq (idea-t6odez): the window's newest message moves on, nothing is repeated
  const lastKey = () => page.evaluate(() => [...document.querySelectorAll('.content .item')].at(-1)?.dataset.key ?? null);
  const keyBefore = await lastKey();
  await page.locator('.earlier button', { hasText: 'Load newer' }).click();
  await page.waitForFunction((k) => [...document.querySelectorAll('.content .item')].at(-1)?.dataset.key !== k, keyBefore, { timeout: 5_000 }).catch(() => {});
  const keys = await page.evaluate(() => [...document.querySelectorAll('.content .item')].map((e) => e.dataset.key));
  check('load newer pages forward', keys.at(-1) !== keyBefore && new Set(keys).size === keys.length, `${keyBefore} → ${keys.at(-1)}, ${keys.length} items`);
  const jump = page.locator('.earlier button', { hasText: 'jump to latest' });
  if (await jump.count()) await jump.click();
  await page.waitForTimeout(400);
}
var domCount = await page.locator('.content .item').count();
check('back to latest', !(await page.locator('.earlier button', { hasText: 'jump to latest' }).count()), `${domCount} items`);

var ringLabel = await ring.getAttribute('aria-label');
check('context ring reads the session context', /tokens? \(/.test(ringLabel), ringLabel);
await ring.click();
await page.waitForSelector('.popover .cx');
await page.waitForTimeout(250);
var cx = await page.locator('.popover .cx').innerText();
check('context breakdown popout', /Used/.test(cx) && /Window/.test(cx) && /Free/.test(cx) && /System prompt/.test(cx), cx.replace(/\n/g, ' | '));
await shot(page, '03b-context-ring');
await page.keyboard.press('Escape');
await page.waitForTimeout(150);
check('Esc closes the context popout', (await page.locator('.popover .cx').count()) === 0);

// the popout's compaction row and its "Compact now". This chat's window holds 200 of ~330 messages, so the count
// is a lower bound and says so.
await ring.click();
await page.waitForSelector('.popover .cx');
await page.waitForTimeout(250);
var cRow = page.locator('.popover .cx .row', { hasText: 'Compactions' });
var cTitle = (await cRow.getAttribute('title')) ?? '';
var cText = await cRow.locator('b').innerText();
check('the popout counts compactions as a lower bound', cText === '1+' && /not loaded/.test(cTitle), `${cText} — ${cTitle}`);
var cRead = await page.locator('.popover .cx .read').innerText();
check('the newest compaction reads what it did', /96k → 48k · auto/.test(cRead), cRead);
var ringBefore = await ring.getAttribute('aria-label');
await page.locator('.popover .cx .foot button', { hasText: 'Compact now' }).click();
await page
  .waitForFunction(() => [...document.querySelectorAll('.popover .cx .row')].some((r) => /Compactions/.test(r.innerText) && /2\+/.test(r.innerText)), null, { timeout: 15_000 })
  .catch(() => {});
var cAfter = await cRow.locator('b').innerText();
check('Compact now compacts and the count follows', cAfter === '2+', cAfter);
var readAfter = await page.locator('.popover .cx .read').innerText();
check('the reading follows the newest compaction', /48k → 17k · manual/.test(readAfter), readAfter);
var ringAfter = await ring.getAttribute('aria-label');
check('the ring follows session.context after a compaction', ringBefore !== ringAfter, `${ringBefore} → ${ringAfter}`);
var compactToast = (await page.evaluate(() => window.__toasts ?? [])).at(-1) ?? '';
check('the result of a manual compaction is a toast', /Context compacted/.test(compactToast), compactToast);
await shot(page, '03c-compactions');
await page.keyboard.press('Escape');
await page.waitForTimeout(150);

}
// ------------------------------------------------------------------ new session + streaming
if (want('new session + agent run')) {
log('new session + agent run');
await newTab();
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
// a manual compaction is refused while the agent runs, and the popout's button says so before it can be pressed
{
  const sid = await page.locator('.topbar .tab.active').getAttribute('data-tab');
  const refused = await rpcCall('compaction.run', { sessionId: sid });
  check('a manual compaction is refused while the agent runs', refused?.error?.code === 'busy', JSON.stringify(refused?.error ?? null));
  await ring.click();
  await page.waitForSelector('.popover .cx');
  const fresh = await page.locator('.popover .cx .row', { hasText: 'Compactions' }).locator('b').innerText();
  check('a fresh chat has compacted nothing', fresh === '0', fresh);
  const compactBtn = page.locator('.popover .cx .foot button', { hasText: 'Compact now' });
  const busyTip = (await compactBtn.getAttribute('title')) ?? '';
  check('Compact now is disabled while the agent runs', (await compactBtn.isDisabled()) && /wait for it to finish/.test(busyTip), busyTip);
  await shot(page, '04b-compact-while-busy');
  await page.keyboard.press('Escape');
  await page.waitForTimeout(150);
}
await page.waitForSelector('.tool[data-status="ok"]', { timeout: 15000 });

// steer + queue while running
await ta.fill('Also make Release() return early for unknown pools.');
await ta.press('Enter');
await ta.fill('Afterwards, write a one-line changelog entry.');
await ta.press('Alt+Enter');
await page.waitForSelector('.queue .chip', { timeout: 3000 }).catch(() => {});
var chips = await page.locator('.queue .chip').count();
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
var banner = await page.locator('.banner').count();
check('retry banner (agent.notice)', banner > 0);
// stream.reset came just before the banner: the words streamed before the retry are discarded, not shown in front of the
// second attempt (the mock streams "Now I will build the solution to make sure", then resets)
check('stream.reset drops the text streamed before the retry', !(await page.locator('.content:visible').innerText()).includes('Now I will build the solution'));

// live bash output
await page.locator('.tool[data-status="running"]', { hasText: 'Bash' }).waitFor({ timeout: 20000 }).catch(() => {});
await page.locator('.tool .tail').waitFor({ timeout: 3000 }).catch(() => {});
await page.waitForTimeout(250);
await shot(page, '06-live-bash');
var tail = await page.locator('.tool .tail').count();
check('live tool output tail', tail > 0 || (await page.locator('.tool[data-status="ok"]', { hasText: 'Bash' }).count()) > 0);

// wait for the run (and queued follow-up) to finish
await page.waitForFunction(() => !document.querySelector('.composer.running'), null, { timeout: 90_000 });
await page.waitForTimeout(600);
await shot(page, '07-run-finished');
check('retry banner cleared after run', (await page.locator('.banner').count()) === 0);
check('the text discarded by stream.reset is in no message', !(await page.locator('.content:visible').innerText()).includes('Now I will build the solution'));
var pinned = await page.locator('.scroller').evaluate((el) => el.scrollHeight - el.scrollTop - el.clientHeight);
check('stayed pinned to bottom during run', pinned < 60, `${Math.round(pinned)}px from bottom`);
var collapsed = await page.locator('.group.collapsible').count();
check('finished steps group collapsed', collapsed > 0, `${collapsed} group(s)`);

// expand group, thinking, diff, bash
var closedGroups = page.locator('.group.collapsible .head[aria-expanded="false"]');
for (let n = 0; n < 10 && (await closedGroups.count()); n++) await closedGroups.first().click();
await page.waitForTimeout(150);
for (const label of ['Edit', 'Bash']) {
  const row = page.locator('.tool .line', { hasText: label }).last();
  if (await row.count()) await row.click();
}
if (await think.count()) await think.click();
await page.waitForTimeout(300);
if (await edit.count()) await edit.scrollIntoViewIfNeeded();
await page.waitForTimeout(200);
await shot(page, '08-expanded-tools');
check('diff view rendered', (await page.locator('.diff .dl.add').count()) > 0);
var turnLines = await page.locator('.group .turn').allInnerTexts();
check(
  'model turns: time to the first token and cache reuse under each turn',
  turnLines.length > 0 && turnLines.every((t) => /first token \d/.test(t) && /\d+% cached/.test(t)),
  turnLines[0],
);
var answerInfo = await page.locator('.assistant .foot .info').last().innerText();
check('the answer footer has its turn numbers too', /first token \d/.test(answerInfo) && /\d+% cached/.test(answerInfo), answerInfo);

}
// ------------------------------------------------------------------ showcase session (subagents, notices)
if (want('showcase session')) {
log('showcase session');
await page.locator('.srow', { hasText: 'Fix streaming reconnect bug' }).first().click();
await page.waitForTimeout(400);
await page.locator('.group.collapsible .head').first().click();
await page.waitForTimeout(150);
var spawnRow = page.locator('.tool .line', { hasText: 'Agent' }).first();
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

  // an edit of several files: the row says how many and which, the diff has a header per file
  // (finished groups of more than three steps are folded: open them first)
  for (let i = 0; i < 10; i++) {
    const folded = page.locator('.content:visible .group.collapsible:not(.open) .head');
    if (!(await folded.count())) break;
    await folded.first().click();
    await page.waitForTimeout(100);
  }
  const multi = page.locator('.content:visible .tool', { hasText: '2 files' }).first();
  check('an edit of several files names them on its row', (await multi.count()) === 1 && (await multi.locator('.line').innerText()).includes('rpc.svelte.js'));
  await multi.locator('.line').click();
  await page.waitForTimeout(150);
  const heads = await multi.locator('.dl.file').allInnerTexts();
  check('…and its diff has a header per file', heads.length === 2 && heads[0].includes('rpc.svelte.js') && heads[1].includes('PROTOCOL.md'), heads.join(' | '));
  await shot(page, '09d-edit-several-files');

  // a shell command that changed a file: the files it changed, and its diff on request
  const sed = page.locator('.content:visible .tool', { hasText: 'RECONNECT_MS' }).first();
  await sed.locator('.line').click();
  await page.waitForTimeout(150);
  check('a shell command lists the files it changed', (await sed.locator('.changes .cf').allInnerTexts()).some((x) => x.includes('rpc.svelte.js')));
  await sed.locator('.changes button', { hasText: 'Diff' }).click();
  await page.waitForTimeout(100);
  check('…and shows its diff on request', (await sed.locator('.diff .dl.add').innerText()).includes('RECONNECT_MS = 250'));
  await shot(page, '09e-shell-changed-files');
}

}
// ------------------------------------------------------------------ popups: agent picker, commands, mentions
if (want('composer popups')) {
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
  // "Any available": no agent of its own, each run takes whichever is free
  await agentBtn.click();
  await page.locator('.popover .opt[data-agent="any"]').click();
  await page.waitForTimeout(250);
  check('"Any available" is a choice the button shows', (await agentBtn.innerText()).includes('Any available'), await agentBtn.innerText());
  await agentBtn.click();
  check('and the picker marks it', (await page.locator('.popover .opt[data-agent="any"].current').count()) === 1);
  await page.locator('.popover .opt[data-agent="qwen"]').click();
  await page.waitForTimeout(200);
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
var skillOpts = await page.locator('.popup .opt .cmd').allInnerTexts();
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

}

// ------------------------------------------------------------------ a popup that stays open while the chat moves underneath it
if (want('popups survive a chat update')) {
log('popups survive a chat update');
{
  // The bug: the menu closed on any scroll in the window, and a chat that grows re-pins its list to the bottom, so a
  // reply arriving under an open menu took it down. Scrolling has to move a popup with its anchor, not dismiss it.
  // The popover half below is the same rule for the composer's pickers: it already ignored scrolls, but it also never
  // followed its button, so it was left hanging where the button used to be.
  await page.setViewportSize({ width: 1280, height: 520 });   // a short window: a few rows overflow the chat list
  await openStripTab('left', 'Projects');
  const favRow = page.locator('.panel.left .prow').first();
  await favRow.hover();
  await favRow.locator('button[title^="Add to favorites"]').click();
  await page.waitForTimeout(200);
  check('the top bar has its project chevron', (await page.locator('.topbar .quick-more').count()) === 1);

  await ta.fill('Keep the popups open while this answer streams.');
  await ta.press('Enter');
  await page.waitForFunction(() => !document.querySelector('.composer.running'), null, { timeout: 60_000 });
  await page.waitForTimeout(400);
  const overflows = await page.locator('.scroller').evaluate((el) => el.scrollHeight > el.clientHeight + 4).catch(() => false);

  // the chevron's project picker (a Popover, since 7a9d4a1: all projects, search, favorites on top): open it mid-run, so
  // every streamed chunk re-pins the chat under it
  await ta.fill('Second answer, streamed under an open picker.');
  await ta.press('Enter');
  await page.waitForSelector('.composer.running', { timeout: 5000 }).catch(() => {});
  await page.locator('.topbar .quick-more').click();
  const opened = await page.waitForSelector('.popover', { timeout: 3000 }).then(() => true).catch(() => false);
  check('the chevron opens the project picker', opened);
  const anchorGap = async () => {
    // where the picker sits against its button (null once it is gone: every check below reports, not the first one)
    const a = await page.locator('.topbar .quick-more').boundingBox();
    const m = await page.locator('.popover').first().boundingBox().catch(() => null);
    if (!a || !m) return null;
    return { dx: Math.round(m.x - a.x), dy: Math.round(m.y - (a.y + a.height)) };
  };
  const before = await anchorGap();
  await page.waitForFunction(() => !document.querySelector('.composer.running'), null, { timeout: 60_000 });
  await page.waitForTimeout(300);
  const stillOpen = opened && (await page.locator('.popover').count()) === 1;
  check('the chat list was big enough to scroll (the case the bug needed)', overflows);
  check('a reply streaming under the open picker does not close it', stillOpen);
  const after = await anchorGap();
  check('the picker stays attached to the button that opened it', !!after && !!before && Math.abs(after.dx - before.dx) < 4 && Math.abs(after.dy - before.dy) < 4,
    stillOpen ? `moved ${after.dx - before.dx}px across, ${after.dy - before.dy}px down` : 'the picker was gone');

  // a scroll the user makes in the chat keeps it open too
  if (stillOpen) {
    await page.mouse.move(640, 260);
    await page.mouse.wheel(0, -400);
    await page.waitForTimeout(300);
    check('scrolling the chat under the picker keeps it open', (await page.locator('.popover').count()) === 1);
    await shot(page, '12b-menu-survives');
    await page.keyboard.press('Escape');
    await page.waitForTimeout(150);
    check('Esc still closes the picker', (await page.locator('.popover').count()) === 0);
  } else {
    check('scrolling the chat under the picker keeps it open', false, 'the picker had already closed');
    await shot(page, '12b-menu-survives');
    await page.keyboard.press('Escape');
  }

  // the composer's project picker (a Popover): same rule, and it has to keep up with its button
  const projBtn = page.locator('.composer button[aria-label="Project"]').first();
  if (await projBtn.count()) {
    await projBtn.click();
    const pop = page.locator('.popover');
    if (await pop.first().waitFor({ timeout: 3000 }).then(() => true).catch(() => false)) {
      const popAt = async () => {
        const a = await projBtn.boundingBox();
        const p = await pop.first().boundingBox().catch(() => null);
        if (!a || !p) return null;
        return { dx: Math.round(p.x - a.x), gap: Math.round(a.y - (p.y + p.height)) }; // placement puts the bottom 6px above the button
      };
      const pBefore = await popAt();
      check('the project picker opens above its button', !!pBefore && Math.abs(pBefore.gap - 6) < 4, `${pBefore?.gap}px above`);
      await ta.fill('A third answer, streamed under the open project picker.');
      await ta.press('Enter');
      await page.waitForFunction(() => !document.querySelector('.composer.running'), null, { timeout: 60_000 });
      await page.waitForTimeout(300);
      const popOpen = (await page.locator('.popover').count()) === 1;
      check('a reply streaming under the project picker does not close it', popOpen);
      const pAfter = await popAt();
      check('the project picker stays on its button', popOpen && !!pBefore && !!pAfter && Math.abs(pAfter.gap - pBefore.gap) < 4 && Math.abs(pAfter.dx - pBefore.dx) < 4,
        popOpen ? `${pBefore.dx}px across / ${pBefore.gap}px above → ${pAfter.dx}px across / ${pAfter.gap}px above` : 'the popover had closed');
      await page.keyboard.press('Escape');
      await ta.fill('');
    } else check('the project picker opens', false);
  }
  await page.setViewportSize({ width: 1600, height: 1000 });
  // leave the state the sections after this one expect: the favorite is gone again (the next one stars a project itself)
  await openStripTab('left', 'Projects');
  const stillFav = page.locator('.panel.left .prow').first();
  await stillFav.hover();
  await stillFav.locator('button[title^="Remove from favorites"]').click();
  await page.waitForTimeout(200);
  await stillFav.hover();
  check('the favorite is given back (the next section stars its own)', (await stillFav.locator('button[title^="Add to favorites"]').count()) === 1);
  await ta.fill('');
}

}
// ------------------------------------------------------------------ tabs
if (want('tabs')) {
log('tabs');
var tabsBefore = await page.locator('.topbar .tab').count();
await page.keyboard.press('Control+Tab');
await page.waitForTimeout(150);
var firstTab = page.locator('.topbar .tab').first();
await firstTab.click({ button: 'middle' });
await page.waitForTimeout(150);
var tabsAfter = await page.locator('.topbar .tab').count();
check('middle-click closes tab', tabsAfter === tabsBefore - 1, `${tabsBefore} → ${tabsAfter}`);

}
// ------------------------------------------------------------------ tab dots: a run that ends (or fails) while another tab is in front
if (want('tab dots: a run that ends behind another tab')) {
log('tab dots: a run that ends behind another tab');
{
  // the dot on a background tab: unread when its run ended, error when the run failed (an error notice, agent.status failed)
  const TITLE = 'hi, a quick check of the dots'; // the first words title the chat; "hi"/"quick" make the mock answer at once
  const tabOf = (title) => page.locator('.topbar .tab', { has: page.locator('.tab-title', { hasText: title }) }).first();
  const dotOf = async (title) => (await tabOf(title).locator('.np-dot').getAttribute('data-status')) ?? 'none';
  const dotIs = (title, status) =>
    page
      .waitForFunction(
        ([t, s]) => [...document.querySelectorAll('.topbar .tab')].find((x) => x.querySelector('.tab-title')?.textContent.includes(t))?.querySelector('.np-dot')?.dataset.status === s,
        [title, status],
        { timeout: 15_000 },
      )
      .then(() => true)
      .catch(() => false);
  const closeEmptyTabs = async () => {
    for (let i = 0; i < 4 && (await page.locator('.topbar .tab', { hasText: 'New session' }).count()); i++) await page.locator('.topbar .tab', { hasText: 'New session' }).first().locator('.tab-close').click();
  };
  await rpcCall('mock.thinkDelay', { ms: 1500 }); // the thinking phase lasts long enough to bring another tab in front
  try {
    await newTab();
    await ta.fill(TITLE);
    await ta.press('Enter');
    await page.waitForSelector('.thinking.live', { timeout: 5000 }).catch(() => {});
    await newTab(); // an empty chat in front: the first one runs behind it
    check('tab dots: a chat running behind another tab shows running', (await dotOf('quick check of the dots')) === 'running', await dotOf('quick check of the dots'));
    await rpcCall('mock.thinkDelay', { ms: 0 });
    await dotIs('quick check of the dots', 'unread');
    check('tab dots: a run that ended behind another tab leaves the unread dot', (await dotOf('quick check of the dots')) === 'unread', await dotOf('quick check of the dots'));
    await tabOf('quick check of the dots').click();
    await page.waitForTimeout(300);
    check('tab dots: opening the tab clears it', (await dotOf('quick check of the dots')) === 'idle', await dotOf('quick check of the dots'));
    // a run that fails behind another tab: the error dot (the run stays the failed one, so opening the tab keeps it)
    await rpcCall('mock.thinkDelay', { ms: 1500 });
    await ta.fill('[error] once more');
    await ta.press('Enter');
    await page.waitForSelector('.thinking.live', { timeout: 5000 }).catch(() => {});
    await newTab();
    await rpcCall('mock.thinkDelay', { ms: 0 });
    await dotIs('quick check of the dots', 'error');
    check('tab dots: a run that failed behind another tab shows the error dot', (await dotOf('quick check of the dots')) === 'error', await dotOf('quick check of the dots'));
    await tabOf('quick check of the dots').click();
    await page.waitForTimeout(300);
    const notice = page.locator('.content:visible .item[data-kind="notice"]', { hasText: 'HTTP 503' });
    check('tab dots: the failure is an error notice in the chat, request id and all', (await notice.count()) === 1 && /req_e2e_503/.test(await notice.innerText()));
    await shot(page, '10b-tab-dots');
  } finally {
    await rpcCall('mock.thinkDelay', { ms: 0 });
    await closeEmptyTabs();
  }
}

}
if (want('plugin tab: Work')) {
log('plugin tab: Work');
{
  await openStripTab('right', 'Work');
  await page.waitForSelector('.plugin-root .work .pool', { timeout: 10_000 });
  await page.waitForTimeout(400);
  const qwen = page.locator('.work .pool[data-agent="qwen"]');
  const summary = async () => (await page.locator('.work .summary').innerText()).replace(/\s+/g, ' ');
  check('work: one row per instance, busy or free', (await qwen.locator('.slot').count()) === 2 && (await qwen.locator('.slot.busy').count()) === 2);
  check('work: the summary counts working, waiting and free', /2 working/.test(await summary()) && /1 waiting/.test(await summary()) && /0 free/.test(await summary()), await summary());
  check('work: the waiting runs are one line under the agent', (await qwen.locator('.waiting').count()) === 1 && /1 waiting/.test(await qwen.locator('.waiting').innerText()));
  const heightBefore = Math.round((await page.locator('.work').boundingBox()).height);
  await qwen.locator('.waiting').click();
  const waitRow = page.locator('.work .pop .row', { hasText: 'reviewer: check backoff math' });
  check('work: the waiting line opens who waits in a floating list, and the tab does not grow', (await waitRow.count()) === 1 && Math.round((await page.locator('.work').boundingBox()).height) === heightBefore);
  check('work: the gap before the longest wait is there', (await qwen.locator('.waiting .longest').evaluate((e) => parseFloat(getComputedStyle(e).marginLeft))) >= 4);
  await waitRow.locator('button[aria-label^="Cancel this run"]').click(); // agent.abort: the mock has no run to stop, so it says so
  await page.waitForTimeout(500);
  check('work: an x on a waiting run asks the runtime to cancel it (agent.abort)', (await page.locator('.np-toast, .toast', { hasText: 'was not waiting any more' }).count()) >= 1, (await page.locator('.np-toasts, .toasts').allInnerTexts().catch(() => [])).join(' | '));
  await page.keyboard.press('Escape');
  check('work: the old Runs, Model capacity, Physical owners and Idea checks sections are gone', (await page.locator('.work .node').count()) === 0 && (await page.locator('.work .np-section-label[title="Runs"]').count()) === 0 && (await page.locator('.work [data-resource]').count()) === 0 && (await page.locator('.work [data-lease]').count()) === 0 && (await page.locator('.work .np-section-label[title="Model capacity"]').count()) === 0 && (await page.locator('.work .np-section-label[title="Physical owners"]').count()) === 0 && (await page.locator('.work .np-section-label[title="Idea checks"]').count()) === 0);
  const ownerNames = await qwen.locator('.slot.busy .name').allInnerTexts();
  check('work: a top-level lane owner shows its session title, not "main"; a subagent its name',
    ownerNames.includes('Index docs for semantic search') && !ownerNames.includes('main') && ownerNames.includes('surveyor'), ownerNames.join(' | '));
  check('work: a subagent says whose work it is', /for Refactor provider retry policy/.test(await qwen.locator('.slot.busy', { hasText: 'surveyor' }).innerText()));
  // a chat whose turn ended while its subagents work is not done: the Work tab, its row and its tab say what it waits for
  await rpcCall('mock.agentStatus', { sessionId: 'ses_bg', patch: { status: 'idle', activity: null } });
  await page.waitForTimeout(300);
  const waitingChat = page.locator('.work .waiting-chat');
  check('work: a chat whose subagents still work is listed as waiting for them',
    (await waitingChat.count()) === 1 && /Refactor provider retry policy/.test(await waitingChat.innerText()) && /waiting for 2 agents/.test(await waitingChat.innerText()),
    (await waitingChat.allInnerTexts()).join(' | '));
  const bgRow = page.locator('.srow[data-session="ses_bg"]');
  check('sessions: its row says it waits for its subagents, with the waiting dot',
    /waiting for 2 agents/.test(await bgRow.locator('.waiting').innerText().catch(() => '')) && (await bgRow.locator('.np-dot').getAttribute('data-status')) === 'waiting');
  await shot(page, '15b-work-waiting-chat');
  await rpcCall('mock.agentStatus', { sessionId: 'ses_bg', patch: { status: 'yielded', activity: 'waiting for 2 agents' } });
  await page.waitForTimeout(300);
  check('work: …and not while it runs or waits inside a tool call', (await waitingChat.count()) === 0);
  // the shared clock ticks: a live elapsed time moves on its own (a clock nobody is told about stays at its first value)
  const elapsed = () => page.locator('.work .np-elapsed').allInnerTexts();
  const e0 = await elapsed();
  await page.waitForTimeout(2300);
  const e1 = await elapsed();
  check('work: a running elapsed time ticks', e0.length > 0 && e0.join('|') !== e1.join('|'), `${e0.join(' ')} → ${e1.join(' ')}`);
  // the agents are always listed; an inactive one says why; each has a switch
  const gemma = page.locator('.work .pool[data-agent="gemma"]');
  check('work: an agent whose model is not loaded is listed as such', (await gemma.locator('.st').innerText()) === 'not loaded' && /isn't loaded/.test(await gemma.locator('.second').innerText()));
  await gemma.locator('input.np-switch').click();
  await page.waitForTimeout(300);
  check('work: the switch takes an agent off (agents.setEnabled)', (await rpcCall('settings.get')).settings.agents?.gemma?.disabled === true && (await gemma.locator('.st').innerText()) === 'off');
  await gemma.locator('input.np-switch').click();
  await page.waitForTimeout(300);
  check('work: and back on', !(await rpcCall('settings.get')).settings.agents?.gemma?.disabled);
  // fixed slots: a job ending, or another taking its place, moves nothing in the tab
  const geometry = () => page.evaluate(() => {
    const top = (e) => Math.round(e.getBoundingClientRect().top);
    return {
      pools: [...document.querySelectorAll('.work .pool')].map((p) => [top(p), Math.round(p.getBoundingClientRect().height)]),
      rows: [...document.querySelectorAll('.work .pool[data-agent="qwen"] .slot')].map((r) => [top(r), Math.round(r.getBoundingClientRect().height)]),
      sections: [...document.querySelectorAll('.work .np-section-title')].map(top),
    };
  });
  const rowNames = () => qwen.locator('.slot').evaluateAll((els) => els.map((e) => (e.classList.contains('free') ? 'free' : e.querySelector('.name')?.textContent)));
  const g0 = await geometry();
  const surveyor = await rpcCall('mock.workRelease', { sessionId: 'ses_bg_explore' }); // the subagent ends: the waiting reviewer takes its slot
  await page.waitForTimeout(500);
  const g1 = await geometry();
  check('work: a waiter takes the slot that ended, in the same row, and nothing moves', JSON.stringify(g0) === JSON.stringify(g1) && (await rowNames())[0] === 'reviewer', JSON.stringify([g0, g1, await rowNames()]));
  check('work: the waiting line stays, saying no one waits', /no one waiting/.test(await qwen.innerText()) && (await qwen.locator('.waiting').count()) === 0);
  const index = await rpcCall('mock.workRelease', { sessionId: 'ses_index' }); // nobody waits: its row turns into a free one, in place
  await page.waitForTimeout(500);
  const g2 = await geometry();
  check('work: a slot that ended turns free in place, the same height as a busy one, and nothing moves', JSON.stringify(g0) === JSON.stringify(g2) && (await rowNames()).join() === 'reviewer,free', JSON.stringify([g0, g2, await rowNames()]));
  check('work: the summary and the agent say what is free', /1 working/.test(await summary()) && /1 free/.test(await summary()) && (await qwen.locator('.st').innerText()) === '1 free', await summary());
  // put the pools back for the checks that follow: both jobs running, the reviewer waiting again
  await rpcCall('mock.workRelease', { sessionId: 'ses_bg_review' });
  await rpcCall('mock.workTake', { pool: surveyor.pool, owner: surveyor.owner });
  await rpcCall('mock.workTake', { pool: index.pool, owner: index.owner });
  await rpcCall('mock.workTake', { pool: surveyor.pool, owner: surveyor.promotedOwner, waiting: true });
  await page.waitForTimeout(500);
  check('work: the pools are back as they were', (await rowNames()).join() === 'surveyor,Index docs for semantic search' && /1 waiting/.test(await summary()), (await rowNames()).join() + ' | ' + (await summary()));
  // runs that may go to any of several agents wait in nobody's queue: one line, always there, so it moves nothing
  check('work: the line for runs waiting for any agent is there when no one waits', /no one waiting for any agent/.test(await page.locator('.work .unassigned').innerText()));
  const geo0 = await geometry();
  await rpcCall('mock.workSetUnassigned', { items: [{ agentId: 'ag_wait', sessionId: 'ses_bg_review', label: 'waiter', since: new Date(Date.now() - 90_000).toISOString() }] });
  await page.waitForTimeout(500);
  const geo1 = await geometry();
  check('work: a run waiting for any agent is one line, counted as waiting, and nothing moves', /1 waiting for any agent/.test(await page.locator('.work .unassigned').innerText()) && /2 waiting/.test(await summary()) && JSON.stringify(geo0) === JSON.stringify(geo1), JSON.stringify([geo0, geo1]) + ' ' + (await summary()));
  await rpcCall('mock.workSetUnassigned', { items: [] });
  await page.waitForTimeout(300);

  check('work: a foreground command shows on the row of the chat that runs it, not in a list that changes size', /\$ python scripts\/embed\.py docs\//.test(await qwen.locator('.slot.busy', { hasText: 'Index docs for semantic search' }).innerText()) && (await page.locator('.work .proc', { hasText: 'embed.py' }).count()) === 0);
  check('work: what the agent has done is on the agent', /412 calls/.test(await qwen.locator('.use').innerText()) && /1\.2M/.test(await qwen.locator('.use').innerText()), await qwen.locator('.use').innerText());
  check('work: there is no Usage today section any more', (await page.locator('.work .np-section-label[title="Usage today"]').count()) === 0);
  const openSection = async (title) => {
    const sec = page.locator('.work .np-section', { has: page.locator('.np-section-title', { hasText: title }) });
    await sec.waitFor({ timeout: 5000 });
    const toggle = sec.locator('.np-section-toggle');
    if ((await toggle.getAttribute('aria-expanded')) !== 'true') await toggle.click(); // the sections are collapsed until opened
    return sec;
  };
  await openSection('Background');
  await shot(page, '25-work-tab');
  // expand the background process and watch its output grow (processes.output + live process.output)
  const proc = page.locator('.work .proc', { hasText: 'npm run dev' }).first();
  await proc.locator('.row').click();
  await page.waitForSelector('.work .proc .out', { timeout: 5000 }).catch(() => {});
  const len0 = (await proc.locator('.out').innerText().catch(() => '')).length;
  await page.waitForFunction((n) => (document.querySelector('.work .proc .out')?.innerText ?? '').length > n, len0, { timeout: 8000 }).catch(() => {});
  const len1 = (await proc.locator('.out').innerText().catch(() => '')).length;
  check('work: process output tail + live chunks', len0 > 0 && len1 > len0, `${len0} → ${len1} chars`);
  await right.screenshot({ path: path.join(OUT, '26-work-process.png') });
  await proc.locator('.row').click();
  // collapse a row while its first processes.output tail is still in flight: the abandoned open must
  // not leave a live subscription + 2 s poll timer behind (a closed row kept asking for tails)
  const emb = ((await rpcCall('processes.list')) ?? []).find((p) => p.command?.includes('npm run dev'));
  const tailCalls = async () => ((await rpcCall('mock.procStats')) ?? {})[emb?.id]?.tailCalls ?? 0;
  await rpcCall('mock.procTailDelay', { ms: 1000 });
  try {
    const base = await tailCalls();
    await proc.locator('.row').click(); // open: the tail starts, delayed 1000 ms
    await page.waitForTimeout(200); // inside the fetchTail() await window
    await proc.locator('.row').click(); // collapse
    await page.waitForTimeout(1500); // the in-flight tail settles (its one request is expected)
    const calls0 = await tailCalls();
    await page.waitForTimeout(3000); // a leaked 2 s poll would add another tail request in here
    const calls1 = await tailCalls();
    check('work: a collapse mid-fetch leaves no tail polling behind', !!emb && calls0 - base >= 1 && calls1 === calls0, `${base} → ${calls0} → ${calls1} processes.output calls${emb ? '' : ' (npm run dev not found)'}`);
  } finally {
    await rpcCall('mock.procTailDelay', { ms: 0 });
  }
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
  await page.locator('.work .slot.busy', { hasText: 'surveyor' }).first().click();
  await page.waitForTimeout(400);
  check('work: a busy slot opens its session', (await page.locator('.topbar .tab.active[data-tab="ses_bg_explore"]').count()) > 0, await page.locator('.topbar .tab.active').innerText().catch(() => ''));
  await openSession('Lane scheduler hardening');
}

}
if (want('plugin tab: Usage')) {
log('plugin tab: Usage');
{
  await openStripTab('right', 'Usage');
  const u = page.locator('.panel.right .usage');
  await u.locator('.top .big').waitFor({ timeout: 10_000 }).catch(() => {});
  check('usage: the period total is the first thing the tab says', ((await u.locator('.top .big').innerText().catch(() => '')) ?? '').trim() === '$0.68');
  check('usage: the last 30 days are a bar each', (await u.locator('.chart .bar').count()) === 30);
  check('usage: every agent and model is a row with its cost', (await u.locator('.row', { hasText: 'anthropic' }).count()) >= 2 && (await u.locator('.row', { hasText: 'qwen' }).first().innerText()).includes('free'));
  const picker = u.locator('.head select');
  check('usage: the picker lists the periods, the current one first', (await picker.locator('option').count()) === 3 && ((await picker.locator('option').first().innerText()) ?? '').includes('now'));
  await u.locator('.chat').first().waitFor({ timeout: 10_000 }).catch(() => {});
  check('usage: the chats that cost most are listed, most expensive first', (await u.locator('.chat').count()) >= 3 && ((await u.locator('.chat .val').first().innerText()) ?? '').includes('$0.31'));
  await shot(page, '50-usage-tab');
  // another period: its own total, without the limit and today that only the current period has
  const before = (await u.locator('.top .big').innerText()).trim();
  await picker.selectOption({ index: 1 });
  await page.waitForFunction((b) => document.querySelector('.panel.right .usage .top .big')?.textContent.trim() !== b, before, { timeout: 10_000 }).catch(() => {});
  const after = (await u.locator('.top .big').innerText()).trim();
  check('usage: another period shows its own total', after !== before && after.startsWith('$'), `${before} → ${after}`);
  check('usage: and no "today" line, which belongs to the current period', (await u.locator('.top .figs', { hasText: 'today' }).count()) === 0);
  await u.locator('.month').first().click(); // the months list chooses the period too: back to the current one
  await page.waitForFunction((b) => document.querySelector('.panel.right .usage .top .big')?.textContent.trim() === b, before, { timeout: 10_000 }).catch(() => {});
  check('usage: a month in the list is a click away', ((await u.locator('.top .big').innerText()) ?? '').trim() === before);
  const title = (await u.locator('.chat .name').first().innerText()).trim();
  await u.locator('.chat').first().click();
  await page.waitForTimeout(400);
  check('usage: a chat in the ranking opens', (await page.locator('.topbar .tab.active').innerText()).includes(title.slice(0, 12)), title);
  await openStripTab('right', 'Work');
}

}
if (want('ask_user: questions in the chat')) {
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

}
if (want('plan mode: the pill, the plan card and its decisions')) {
log('plan mode: the pill, the plan card and its decisions');
{
  await page.locator('.srow', { hasText: 'Fix streaming reconnect bug' }).first().click();
  await page.waitForTimeout(300);
  const pill = page.locator('.composer .bar button.pick[data-state]');
  const card = page.locator('.item[data-kind="plan"] .card').last();
  const settled = () => page.waitForFunction(() => !document.querySelector('.composer.running'), null, { timeout: 15_000 }).catch(() => {});
  check('plan: the composer has a Plan pill, off', (await pill.getAttribute('data-state')) === 'off' && (await pill.innerText()).trim() === 'Plan');
  await pill.click();
  await page.waitForFunction(() => document.querySelector('.composer .bar button.pick[data-state]')?.dataset.state === 'planning', null, { timeout: 5000 }).catch(() => {});
  check('plan: clicking it puts the chat in plan mode (Planning)', (await pill.getAttribute('data-state')) === 'planning' && (await pill.innerText()).trim() === 'Planning');

  await ta.fill('go');
  await ta.press('Enter');
  await card.waitFor({ timeout: 15_000 }).catch(() => {});
  check('plan: the plan is a card of its own in the chat, with its steps and files', (await card.locator('.steps li').count()) === 3 && (await card.locator('.files li').count()) === 2);
  await card.locator('.foot button', { hasText: 'Revise' }).waitFor({ timeout: 5000 }).catch(() => {}); // the buttons come once the plugin says the plan waits
  check('plan: the card has the decisions', (await card.locator('.foot button', { hasText: 'Approve in new chat' }).count()) === 1 && (await card.locator('.foot button', { hasText: 'Revise' }).count()) === 1);
  check('plan: the pill says the plan is ready, and the tab says the chat waits for you', (await pill.getAttribute('data-state')) === 'awaiting' && (await page.locator('.tab.active .np-dot[data-status="asking"]').count()) === 1);
  await shot(page, '48-plan-card');

  // revise: the feedback goes back, the next revision is a new card and the first becomes one line
  await card.locator('.foot button', { hasText: 'Revise' }).click();
  await card.locator('textarea').fill('also log the reconnect');
  await card.locator('.foot button', { hasText: 'Send changes' }).click();
  await page.waitForFunction(() => document.querySelector('.item[data-kind="plan"] .card .rev')?.textContent.includes('revision 2'), null, { timeout: 15_000 }).catch(() => {});
  const second = page.locator('.item[data-kind="plan"] .card').last();
  check('plan: revise answers with a second revision that has the change', (await second.locator('.steps li').count()) === 4 && (await second.innerText()).includes('also log the reconnect'));
  check('plan: the first revision is one line: changes asked', (await page.locator('.item[data-kind="plan"] button.line').first().innerText()).includes('changes asked: also log the reconnect'));

  // save as idea stays on the card; approve ends plan mode
  await second.locator('.link', { hasText: 'Save as idea' }).click();
  await page.waitForTimeout(300);
  check('plan: save as idea does not decide', (await page.locator('.item[data-kind="plan"] .card').count()) === 1);
  await second.locator('.foot button', { hasText: /^Approve$/ }).click();
  await settled();
  // the card is a line once the plan's record says approved (the answer, then plan.changed → plan.list), which is not
  // what the run's end says: wait for the line itself
  await page
    .waitForFunction(() => [...document.querySelectorAll('.item[data-kind="plan"] button.line')].at(-1)?.textContent.includes('approved'), null, { timeout: 5000 })
    .catch(() => {});
  const approved = page.locator('.item[data-kind="plan"] button.line').last();
  check('plan: approved, the card is one line', (await approved.innerText()).includes('approved'));
  check('plan: the pill says the plan was approved', (await pill.getAttribute('data-state')) === 'approved');
  await approved.click();
  check('plan: the line opens to the plan and the idea it was saved as', (await page.locator('.item[data-kind="plan"] .opened').last().innerText()).includes('idea-mock-plan'));

  // the agent offers plan mode: not now, then yes; cancel leaves the mode
  await ta.fill('[planenter] a bigger change');
  await ta.press('Enter');
  const offer = page.locator('.item[data-kind="planenter"] .card').last();
  await offer.waitFor({ timeout: 15_000 }).catch(() => {});
  check('plan: the offer is a card with the reason and two answers', (await offer.locator('.foot button').count()) === 2 && (await offer.innerText()).includes('reconnect path'));
  await offer.locator('.foot button', { hasText: 'Not now' }).click();
  await settled();
  check('plan: declined, the offer is one line', (await page.locator('.item[data-kind="planenter"] .line').last().innerText()).includes('not now'));
  await ta.fill('[planenter] again');
  await ta.press('Enter');
  await page.locator('.item[data-kind="planenter"] .card').last().waitFor({ timeout: 15_000 }).catch(() => {});
  await page.locator('.item[data-kind="planenter"] .card .foot button', { hasText: 'Enter plan mode' }).last().click();
  await card.waitFor({ timeout: 15_000 }).catch(() => {});
  check('plan: entering the mode lets the plan follow', (await pill.getAttribute('data-state')) === 'awaiting');
  await page.locator('.item[data-kind="plan"] .card .link', { hasText: 'Cancel' }).last().click();
  await settled();
  check('plan: cancelled, the pill is off again', (await pill.getAttribute('data-state')) === 'off');
  check('plan: the cancelled plan is one line', (await page.locator('.item[data-kind="plan"] button.line').last().innerText()).includes('cancelled'));

  // approve in a new chat: the new chat takes this tab's place and starts from the plan; this chat is archived
  await ta.fill('[plan] once more');
  await ta.press('Enter');
  await card.waitFor({ timeout: 15_000 }).catch(() => {});
  await page.locator('.item[data-kind="plan"] .card .foot button', { hasText: 'Approve in new chat' }).last().click();
  await page.waitForFunction(() => [...document.querySelectorAll('.tab')].some((t) => t.textContent.includes('Plan: Re-subscribe')), null, { timeout: 15_000 }).catch(() => {});
  const tabs = await page.locator('.tab').allInnerTexts();
  check('plan: the new chat took the plan chat\'s tab', tabs.some((t) => t.includes('Plan: Re-subscribe')) && !tabs.some((t) => t.includes('Fix streaming reconnect bug')), tabs.join(' | '));
  await page
    .waitForFunction(() => [...document.querySelectorAll('.content')].some((c) => c.offsetParent && c.textContent.includes('Carry out this approved plan')), null, { timeout: 15_000 })
    .catch(() => {});
  const first = page.locator('.item[data-kind="user"]', { hasText: 'Carry out this approved plan' }).last();
  check('plan: the new chat starts from the plan', (await first.count()) === 1 && (await first.innerText()).includes('Re-subscribe to the open chats'));
  await shot(page, '49-plan-new-chat');
  await settled();
  // leave the seeded state as it was: the plan chat is not archived and the new one is gone
  const all = (await rpcCall('sessions.list', { includeArchived: true })) ?? [];
  const old = all.find((x) => x.title === 'Fix streaming reconnect bug');
  if (old) await rpcCall('sessions.update', { id: old.id, archived: false });
  for (const x of all.filter((y) => y.title?.startsWith('Plan: Re-subscribe'))) await rpcCall('sessions.delete', { id: x.id });
  await page.locator('.srow', { hasText: 'Fix streaming reconnect bug' }).first().click().catch(() => {});
  await page.waitForTimeout(300);
}

}
if (want('guardrails: a tool call waits for your OK')) {
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

}
if (want('fork: a new chat from a message')) {
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

}
// ------------------------------------------------------------------ the composer's bar as the chat column narrows (idea-ziqr1i)
if (want('composer: narrow widths')) {
log('composer: narrow widths');
{
  // The bar holds three buttons, five pickers, the plan pill, the cost, the context ring and send: as the composer
  // narrows they give up their labels, then the typed-command buttons, the cost and the separator. Nothing may push
  // past the composer's edge, and send stays on screen, at every width a side panel or a small window leaves.
  const probe = () =>
    page.evaluate(() => {
      const bar = document.querySelector('.composer .bar');
      if (!bar) return null;
      const box = bar.getBoundingClientRect();
      const send = bar.querySelector('.send')?.getBoundingClientRect();
      const clipped = [...bar.children]
        .filter((c) => c.offsetWidth > 0)
        .filter((c) => c.getBoundingClientRect().right > box.right + 1 || (c.scrollWidth > c.clientWidth + 1 && !c.matches('.pick, .pick *')))
        .map((c) => c.getAttribute('aria-label') || c.className.split(' ')[0]);
      return { width: Math.round(box.width), overflow: bar.scrollWidth - bar.clientWidth, sendInside: !!send && send.right <= box.right + 1 && send.width > 0, clipped };
    });
  const rows = [];
  const bad = [];
  for (const w of [1100, 900, 760, 640, 560, 480, 420, 380, 340]) {
    await page.setViewportSize({ width: w, height: 800 });
    await page.waitForTimeout(200);
    const r = await probe();
    if (!r) { bad.push(`${w}px: no composer bar`); continue; }
    rows.push(`${w}→${r.width}px${r.overflow > 1 ? ` +${r.overflow}` : ''}`);
    if (r.overflow > 1 || !r.sendInside || r.clipped.length) bad.push(`window ${w}px (bar ${r.width}px): overflow ${r.overflow}px, send ${r.sendInside ? 'in' : 'OUT'}${r.clipped.length ? `, past the edge: ${r.clipped.join(', ')}` : ''}`);
    if (w === 420) await shot(page, '33-composer-narrow');
  }
  check('composer: nothing in the bar overflows, and send stays inside, from a wide window down to 340px', bad.length === 0, bad.join(' | ') || rows.join(', '));
  await page.setViewportSize({ width: 1600, height: 1000 });
  await page.waitForTimeout(200);
}
}
// ------------------------------------------------------------------ what chats cost, the budget
if (want('budget: chat cost, Work tab, a chat stopped by the budget')) {
log('budget: chat cost, Work tab, a chat stopped by the budget');
{
  // the seeded chat that used a paid model (with a subagent) shows what it cost. Open it here instead of relying on the section
  // before having left it open (the same chat: a no-op in a full run, and what lets this section run alone).
  await rpcCall('settings.set', { path: 'budget.monthlyUsd', value: 50 }); // the limit the settings section sets, so this one can run alone
  await openStripTab('left', 'Sessions');
  await page.locator('.srow', { hasText: 'Lane scheduler hardening' }).first().click();
  await page.waitForSelector('.composer .cost', { timeout: 3000 }).catch(() => {});
  const cost = page.locator('.composer .cost');
  check('a chat shows what it cost, with its subagents', (await cost.count()) === 1 && (await cost.innerText()) === '$0.68', (await cost.count()) ? await cost.innerText() : 'none');
  check('the cost tooltip splits the chat from its subagents', ((await cost.getAttribute('title')) ?? '').includes('with its subagents $0.68'));

  // the Work tab: this month against the budget set above ($50)
  await openStripTab('right', 'Work');
  await page.waitForSelector('.work .usage.budget', { timeout: 5000 }).catch(() => {});
  await page.waitForFunction(() => (document.querySelector('.work .usage.budget')?.innerText ?? '').replace(/ /g, ' ').includes('$0.68 / $50'), null, { timeout: 5000 }).catch(() => {});
  const wb = page.locator('.work .usage.budget');
  check('the Work tab shows this month against the budget', (await wb.count()) === 1 && (await wb.innerText()).replace(/\u00a0/g, ' ').includes('$0.68 / $50'), (await wb.count()) ? await wb.innerText() : 'none');

  // Keep the already-loaded tab visible: mounting it after a change would hide a missing usage.changed event.
  await rpcCall('settings.set', { path: 'budget.monthlyUsd', value: 75 });
  await page.waitForFunction(() => (document.querySelector('.work .usage.budget')?.innerText ?? '').replace(/ /g, ' ').includes('$0.68 / $75'), null, { timeout: 3000 }).catch(() => {});
  check('a monthly limit change refreshes the open Work tab', (await wb.innerText()).replace(/\u00a0/g, ' ').includes('$0.68 / $75'), await wb.innerText());
  await rpcCall('settings.set', { path: 'budget.dailyUsd', value: 5 });
  await page.waitForFunction(() => (document.querySelector('.work .usage.budget .umeta')?.innerText ?? '').replace(/ /g, ' ').includes('/ $5'), null, { timeout: 3000 }).catch(() => {});
  check('a daily limit change refreshes the open Work tab', (await wb.locator('.umeta').innerText()).replace(/\u00a0/g, ' ').includes('/ $5'), await wb.innerText());
  const settings = (await rpcCall('settings.get')).settings;
  await rpcCall('settings.replace', { settings: { ...settings, budget: { ...settings.budget, monthlyUsd: 50, dailyUsd: null } } });
  await page.waitForFunction(() => {
    const text = (document.querySelector('.work .usage.budget')?.innerText ?? '').replace(/ /g, ' ');
    const daily = document.querySelector('.work .usage.budget .umeta')?.innerText ?? '';
    return text.includes('$0.68 / $50') && !daily.includes('/');
  }, null, { timeout: 3000 }).catch(() => {});
  check('a settings replacement refreshes the budget and removes the daily cap', (await wb.locator('.uline').innerText()).replace(/\u00a0/g, ' ').includes('$0.68 / $50') && !(await wb.locator('.umeta').innerText()).includes('/'), await wb.innerText());

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

}
// ------------------------------------------------------------------ settings + light theme
if (want('settings')) {
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
// poll what the check counts: the summarizer takes a moment, so a fixed pause here was a race (and the waitForSelector
// that used to stand here matched a different row at once)
var summaryRow = page.locator('.notice .label', { hasText: 'Summary' });
for (let i = 0; i < 60 && !(await summaryRow.count()); i++) await page.waitForTimeout(250);
check('/compact (server rpc command) adds a summary', (await summaryRow.count()) > 0,
  (await page.evaluate(() => window.__toasts ?? [])).slice(-1).join(''));

}
// ------------------------------------------------------------------ tab drag & drop
if (want('drag to reorder tabs')) {
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

}
// ------------------------------------------------------------------ abort
if (want('abort')) {
log('abort');
await newTab();
await ta.fill('Why does the agent scheduler throw when a pool is missing?');
await ta.press('Enter');
await page.waitForSelector('.composer.running', { timeout: 5000 });
await page.waitForSelector('.thinking.live', { timeout: 5000 });
await page.waitForTimeout(400);
await ta.press('Escape');
await page.waitForFunction(() => !document.querySelector('.composer.running'), null, { timeout: 10_000 }).catch(() => {});
await page.waitForTimeout(300);
var aborted = (await page.locator('.status[data-reason="aborted"]').count()) + (await page.locator('.notice', { hasText: 'aborted' }).count());
check('Esc aborts the run', aborted > 0);
await shot(page, '20-aborted');

}
// ------------------------------------------------------------------ image attachments
if (want('images')) {
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

}
// ------------------------------------------------------------------ composer drafts
if (want('composer drafts')) {
log('composer drafts');
{
  // Unsent composer state must not live in the per-chat cache (a 5-store LRU, chat.svelte.js): attach an
  // image, open five more chats so this session's store is evicted, come back — the chip must still be
  // there. The mock's sessions.messages counter proves the store was rebuilt on return, so a pass cannot
  // come from a still-live store.
  await newTab();
  const sid = await page.locator('.topbar .tab.active').getAttribute('data-tab');
  const loads = async () => ((await rpcCall('mock.msgLoads')) ?? {})[sid] ?? 0;
  for (let i = 0; i < 20 && !(await loads()); i++) await sleep(100); // the new chat's first page
  const base = await loads();
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
  await page.locator('.composer input[type=file]').setInputFiles({ name: 'draft.png', mimeType: 'image/png', buffer: Buffer.from(png, 'base64') });
  await page.waitForSelector('.composer .thumb');
  check('draft: an attached image shows as a chip', (await page.locator('.composer .thumb').count()) === 1);
  const evictors = [];
  for (let i = 0; i < 5; i++) {
    const n = await page.locator('.topbar .tab').count();
    await page.keyboard.press('Control+t');
    // wait for the new tab itself: the previous empty chat's .intro is still in the DOM while the switch lags
    await page.waitForFunction((m) => document.querySelectorAll('.topbar .tab').length === m + 1, n, { timeout: 5000 });
    await page.waitForSelector('.intro');
    evictors.push(await page.locator('.topbar .tab.active').getAttribute('data-tab'));
  }
  await page.locator(`.topbar .tab[data-tab="${sid}"]`).click();
  const back = await loads();
  check('draft: the chat store was evicted (a fresh load on return)', back > base, `sessions.messages ${base} → ${back} for ${sid}`);
  check('draft: the attachment chip survives cache eviction', (await page.locator('.composer .thumb').count()) === 1);
  await shot(page, '21b-draft-chip-after-eviction');
  // the bound: more attachments than DRAFT_IMAGES_MAX keep the newest (drafts.svelte.js)
  const many = Array.from({ length: 8 }, (_, i) => ({ name: `img-${i}.png`, mimeType: 'image/png', buffer: Buffer.from(png, 'base64') }));
  await page.locator('.composer input[type=file]').setInputFiles(many);
  await page.waitForFunction(() => document.querySelectorAll('.composer .thumb').length === 6, null, { timeout: 5000 }).catch(() => {});
  const titles = await page.locator('.composer .thumb').evaluateAll((els) => els.map((e) => e.title));
  check('draft: a draft keeps at most six attachments, the newest', titles.length === 6 && titles[0] === 'img-2.png' && titles.at(-1) === 'img-7.png', titles.join(' '));
  // discard: removing a chip releases it from the draft
  await page.locator('.composer .thumb-x').first().click();
  check('draft: removing a chip clears it', (await page.locator('.composer .thumb').count()) === 5);
  // the five empty chats were only here to evict the store: close them again
  for (const id of evictors) await page.locator(`.topbar .tab[data-tab="${id}"] .tab-close`).click();
  await page.waitForTimeout(200);
}

}
// ------------------------------------------------------------------ web tools, todo plan, tools notice, file links
if (want('web tools, todo plan, tools notice, file links')) {
log('web tools, todo plan, tools notice, file links');
{
  await newTab();
  // hold the live thinking line: at MOCK_SPEED=3 the whole thinking stream is ~300 ms, and a waitForSelector
  // whose rAF-driven polls stall under load can miss that window entirely (mock.thinkDelay stretches it).
  // Released in a finally: a failure in this block must not leave the rest of the run streaming in slow motion.
  await rpcCall('mock.thinkDelay', { ms: 1000 });
  try {
  await ta.fill('[web] Why does the demo page break? Check the Svelte docs.');
  await ta.press('Enter');
  // thinking can be opened while it streams; later answers start open and their finished rows stay open
  await page.waitForSelector('.thinking.live .line', { timeout: 10_000 });
  await page.locator('.thinking.live .line').click();
  await page.waitForSelector('.thinking.live .body', { timeout: 3000 }).catch(() => {});
  check('streaming thinking opens while it streams', (await page.locator('.thinking.live .body').count()) === 1);
  await shot(page, '24b-live-thinking');
  } finally {
  await rpcCall('mock.thinkDelay', { ms: 0 });
  }
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

  // links to files open with the operating system, not inside the app — but only after the user says so, because the
  // path came from the model and whatever program it is associated with will run
  const links = page.locator('.md a.file-link');
  check('file links in the answer are marked', (await links.count()) === 2, String(await links.count()));
  const before = page.url();
  const openedBefore = (await rpcCall('mock.filesOpened')).length;
  await links.first().click();
  const ask = page.locator('[role="dialog"][aria-label="Open this file?"]');
  await ask.waitFor({ timeout: 5000 }).catch(() => {});
  check('clicking a file link asks before opening it', (await ask.count()) === 1, String(await ask.count()));
  check('nothing is opened while the question is open', (await rpcCall('mock.filesOpened')).length === openedBefore);
  await ask.locator('button', { hasText: /^Open$/ }).click();
  await page.waitForTimeout(300);
  const opened = await rpcCall('mock.filesOpened');
  check('a confirmed file link asks the host to open it', opened.some((p) => p.replace(/\\/g, '/').endsWith('web/src/App.svelte')), JSON.stringify(opened));
  check('the confirmed link opens it as a deliberate pick (user: true)', (await rpcCall('mock.filesOpenCalls')).at(-1)?.user === true, JSON.stringify((await rpcCall('mock.filesOpenCalls')).at(-1)));
  check('the app does not navigate away', page.url() === before && (await page.locator('.composer textarea').count()) === 1);
  check('web links still open in a new window', (await page.locator('.md a[target="_blank"]', { hasText: 'docs' }).count()) === 1);

  // an answer is text the model may have copied off a page: its raw HTML stays text and its images stay links
  check('a remote image in an answer is a link, not a request', (await page.locator('.md img[src^="http"]').count()) === 0 && (await page.locator('.md a.md-image', { hasText: 'pixel' }).count()) === 1);
  check('raw HTML in an answer is shown, not rendered', (await page.locator('.md a[style], .md [style]').count()) === 0 && (await page.locator('.md', { hasText: 'position:fixed' }).count()) === 1);
  check('a forged copy button in an answer is text', (await page.locator('.md .code-copy').count()) === 0 && (await page.locator('.md', { hasText: 'code-copy' }).count()) === 1);
}

}
// ------------------------------------------------------------------ ssh tools (reuse the shell, read and diff views)
if (want('ssh tools')) {
log('ssh tools');
{
  await newTab();
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

}
// ------------------------------------------------------------------ fast steps: the chat never jumps while the agent works
if (want('fast steps: layout stability')) {
log('fast steps: layout stability');
{
  const size = page.viewportSize();
  await page.setViewportSize({ width: 1280, height: 560 });
  await newTab();
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

}
// ------------------------------------------------------------------ provider tools: each result shows when its call finishes
if (want('provider tools: results live')) {
log('provider tools: results live');
{
  // the Claude CLI runs its own tools inside one model call; their tool messages are stored only after the assistant message,
  // at the call's end. tool.result brings each result the moment the call finishes: the row is done, with its body, mid-call,
  // and the stored messages change nothing on screen (the mock holds the stream, then the tool messages, so both can be seen)
  await newTab();
  const sid = await page.locator('.topbar .tab.active').getAttribute('data-tab');
  await ta.fill('[cli hold=6000 stored=3000] Look at the tree.');
  await ta.press('Enter');
  const bashRow = page.locator('.tool', { has: page.locator('.summary', { hasText: 'git status --short' }) });
  await page.waitForSelector('.composer.running', { timeout: 5000 }).catch(() => {});
  await page.waitForFunction(() => [...document.querySelectorAll('.tool')].filter((t) => t.dataset.status === 'ok').length >= 2, null, { timeout: 15_000 });
  const stored = async () => ((await rpcCall('sessions.messages', { id: sid })).messages ?? []).map((m) => m.role);
  const midRoles = await stored();
  const streaming = (await page.locator('.composer.running').count()) > 0;
  check('provider tools: both rows are done while the model call still runs', streaming && !midRoles.includes('assistant') && !midRoles.includes('tool'), `running ${streaming}; stored: ${midRoles.join(',')}`);
  await bashRow.locator('.line').click();
  const body = await bashRow.locator('.body').innerText().catch(() => '');
  check('provider tools: the result is in the row before anything is stored', body.includes('notes.txt'), JSON.stringify(body.slice(0, 120)));
  const snap = () =>
    page.evaluate(() =>
      [...document.querySelectorAll('.tool')].map((t) => ({ s: t.dataset.status, top: Math.round(t.getBoundingClientRect().top), h: Math.round(t.getBoundingClientRect().height), body: t.querySelector('.body')?.innerText ?? '' })),
    );
  const before = await snap();
  await page.waitForFunction(() => !document.querySelector('.composer.running'), null, { timeout: 30_000 });
  await page.waitForTimeout(400);
  const endRoles = await stored();
  const after = await snap();
  check('provider tools: the tool messages are stored after the assistant message', endRoles.slice(-3).join(',') === 'assistant,tool,tool', endRoles.join(','));
  check('provider tools: the stored messages change nothing on screen', JSON.stringify(before) === JSON.stringify(after), `${JSON.stringify(before)} → ${JSON.stringify(after)}`);
  await shot(page, '27b-provider-tools-live');
}

}
// ------------------------------------------------------------------ compaction banner: kept until the model answers again
if (want('compaction banner')) {
log('compaction banner');
{
  await newTab();
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

}
// ------------------------------------------------------------------ goals: /goal, the strip, pause, resume, achieved
if (want('goals')) {
log('goals');
{
  await newTab();
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

}
// ------------------------------------------------------------------ plugin hot reload + load error
if (want('plugin tab hot reload / error')) {
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

}
// ------------------------------------------------------------------ notifications: what the UI asks the desktop app for
// A page of its own with a stand-in for the desktop app's bridge (chrome.webview), so no other section sees it.
if (want('notifications')) {
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

}
// ------------------------------------------------------------------ reconnect
if (!EXTERNAL && want('reconnect')) {
  log('reconnect');
  // a run that ends while the browser is offline: its stream.end is lost with the socket, so on reconnect the UI
  // must reconcile its transient state against the server truth (the run is done) and not keep the partial stream
  {
    await newTab();
    await ta.fill('Why does the agent scheduler throw when a pool is missing? Make it fail with a clear message.');
    await ta.press('Enter');
    // drop the link as soon as the answer is streaming: between stream.start and message.added the partial stream
    // is the only place it lives (it is cleared by the message that replaces it), so the drop has to land there
    await page.waitForSelector('.thinking.live', { timeout: 15_000 });
    const sid = await page.locator('.topbar .tab.active').getAttribute('data-tab');
    check('the run is streaming before the drop',
      (await page.locator('.thinking.live').count() + (await page.locator('.assistant.live').count())) > 0);
    // the mock drops the WebSocket link (the server keeps running, its events are just lost)
    await rpcCall('mock.offline', { ms: 60_000 });
    await page.waitForSelector('.conn[data-status="reconnecting"]', { timeout: 5_000 }).catch(() => {});
    check('connection indicator shows reconnecting after the drop', (await page.locator('.conn[data-status="reconnecting"]').count()) > 0);
    // the run keeps going on the server and ends while the socket is down
    let busy = true;
    for (let i = 0; busy && i < 120; i++) {
      busy = (await rpcCall('runs.list')).some((a) => a.sessionId === sid && ['running', 'queued', 'yielded'].includes(a.status));
      if (busy) await sleep(500);
    }
    check('the run finishes while the browser is offline', !busy);
    const msgs = (await rpcCall('sessions.messages', { id: sid, limit: 6 })).messages;
    check('its final answer is persisted server-side', msgs.length > 0 && msgs[msgs.length - 1].role === 'assistant');
    await rpcCall('mock.online');
    await page.waitForSelector('.conn[data-status="open"]', { timeout: 30_000 });
    // loadAll (reconnect) is done once the re-fetched agents leave the composer's running state; the reconcile runs inside it
    await page.waitForFunction(() => !document.querySelector('.composer.running'), null, { timeout: 30_000 });
    await page.locator('.content .item[data-kind="text"]', { hasText: 'unknown-pool handling' }).waitFor({ timeout: 10_000 });
    await page.waitForTimeout(400);
    check('on reconnect the finished run\'s stale partial stream is gone',
      (await page.locator('.thinking.live').count() + (await page.locator('.assistant.live').count())) === 0,
      `live blocks: thinking ${await page.locator('.thinking.live').count()}, text ${await page.locator('.assistant.live').count()}`);
    check('…and the persisted final answer is shown', (await page.locator('.content .item[data-kind="text"]', { hasText: 'unknown-pool handling' }).count()) > 0);
    await shot(page, '24a-run-ended-while-offline');
  }
  await openStripTab('left', 'Sessions');
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
if (!EXTERNAL && !argv.includes('--no-dev') && want('vite dev server')) {
  log('vite dev server');
  const vite = spawn(process.execPath, [path.join(repo, 'node_modules/vite/bin/vite.js'), '--config', 'web/vite.config.js', '--port', '5199', '--strictPort', '--host', '127.0.0.1'], {
    cwd: repo,
    env: { ...process.env, NETPI_URL: BASE },
    stdio: ['ignore', 'pipe', 'pipe'],
  });
  // its own words when it does not start (a second suite run on this machine holds the fixed port 5199, for one)
  let viteErr = '';
  vite.stderr.on('data', (d) => (viteErr += d));
  const why = () => {
    const lines = viteErr.replace(/\x1b\[[0-9;]*m/g, '').split('\n').map((l) => l.trim()).filter((l) => l && !l.startsWith('at '));
    return lines.length ? `: ${lines.slice(0, 2).join(' ')}` : '';
  };
  try {
    await new Promise((resolve, reject) => {
      const t = setTimeout(() => reject(new Error(`vite did not start${why()}`)), 20_000);
      vite.stdout.on('data', (d) => /Local:|ready in/.test(String(d)) && (clearTimeout(t), resolve()));
      vite.on('exit', (code) => (clearTimeout(t), reject(new Error(`vite exited (${code})${why()}`))));
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

// ------------------------------------------------------------------ a snapshot answer must not undo what it missed
if (want('a snapshot answer must not undo what it missed')) {
log('a snapshot answer must not undo what it missed');
{
  // The chat window is read as a whole (sessions.messages), but the answer is the page as the server read it
  // when the request went out: a message committed in between is already on screen when that older page lands,
  // and replacing the window with it loses the message (and everything the run streamed after it).
  // mock.messagesHold is that window: the page is read, a message is published while the answer is held, and
  // the page answers last. This chat is opened for the first time here, so opening it is what reads the page.
  const target = (await rpcCall('sessions.list', { includeSubagents: true })).find((s) => s.title === 'AiProxy model catalog aliases');
  const row = await sessionRow('AiProxy model catalog aliases');
  const loads = async () => ((await rpcCall('mock.msgLoads')) ?? {})[target.id] ?? 0;
  const before = await loads();
  await rpcCall('mock.messagesHold', { id: target.id, ms: 700, text: 'Committed while the page was being read.' });
  await row.click();
  await clearSessionSearch();
  const late = page.locator('.content:visible .item[data-kind="text"]', { hasText: 'Committed while the page was being read' });
  await late.waitFor({ timeout: 10_000 }).catch(() => {});
  check('a message published while the page loads is not dropped by the older page', (await late.count()) === 1,
    `sessions.messages ${before} → ${await loads()} for ${target.id}`);
  check('…and the page it answered with is still the window (not replaced by the one message)',
    (await page.locator('.content:visible .item[data-kind="text"]', { hasText: 'candidate list' }).count()) > 0,
    `${await page.locator('.content:visible .item').count()} items in the window`);
  await shot(page, '30-snapshot-vs-event');
}
}

// ------------------------------------------------------------------ deleting a chat leaves nothing of it behind
if (want('deleting a chat leaves nothing of it behind')) {
log('deleting a chat leaves nothing of it behind');
{
  // removeSessionLocal drops what a session left in the window's own maps (app.agents, app.context,
  // app.workspaces among them) — a deleted chat, a subagent's included, otherwise keeps an entry in every one of
  // them for the life of the window. That is memory only, so this drives the path with the maps full (a chat
  // that ran, so it has a run state, a context and a resolved workspace) and checks the window afterwards.
  await openStripTab('left', 'Sessions');
  await newTab();
  await ta.fill('A short chat that will be deleted.');
  await ta.press('Enter');
  await page.waitForFunction(() => !document.querySelector('.composer.running'), null, { timeout: 20_000 }).catch(() => {});
  const sid = await page.locator('.topbar .tab.active').getAttribute('data-tab');
  const mine = page.locator('.srow', { hasText: 'A short chat that will be deleted' }).first(); // the title is the message without its final period
  await mine.focus();
  await page.keyboard.press('Delete');
  await page.locator('.dialog button', { hasText: /^Delete$/ }).click();
  await mine.waitFor({ state: 'detached', timeout: 10_000 }).catch(() => {});
  check('a deleted chat: its row and its tab are gone',
    (await mine.count()) === 0 && (await page.locator(`.topbar .tab[data-tab="${sid}"]`).count()) === 0,
    `tab ${sid} left: ${await page.locator(`.topbar .tab[data-tab="${sid}"]`).count()}`);
  // a subagent row of a seeded chat: the parent keeps the other one
  const parent = page.locator('.srow', { hasText: 'Fix streaming reconnect bug' }).first();
  const sub = page.locator('.srow', { hasText: 'tester: reproduce disconnect' }).first();
  // the toggle folds the subagents again when an earlier section left them open
  if (!(await sub.isVisible())) await parent.locator('.kids').click();
  // the parent's title is in its forks' rows too ("… (fork)", from the fork section): count them before, not expect one
  const parents = await page.locator('.srow', { hasText: 'Fix streaming reconnect bug' }).count();
  await sub.focus();
  await page.keyboard.press('Delete');
  await page.locator('.dialog button', { hasText: /^Delete$/ }).click();
  await sub.waitFor({ state: 'detached', timeout: 10_000 }).catch(() => {});
  check('a deleted subagent: its parent and the other subagent stay',
    (await sub.count()) === 0
      && (await page.locator('.srow', { hasText: 'Fix streaming reconnect bug' }).count()) === parents
      && (await page.locator('.srow', { hasText: 'explorer: map websocket handlers' }).count()) === 1,
    `sub ${await sub.count()}, parent ${await page.locator('.srow', { hasText: 'Fix streaming reconnect bug' }).count()}, explorer ${await page.locator('.srow', { hasText: 'explorer: map websocket handlers' }).count()}`);
  await shot(page, '30b-deleted-chat');
}
}

// ------------------------------------------------------------------ capability removal and recovery
if (!EXTERNAL && want('capability removal and recovery')) {
log('capability removal and recovery');
  await newTab();
  const draft = 'Keep this draft while the executor is unavailable.';
  await ta.fill(draft);
  try {
    await rpcCall('mock.capabilities', { missing: ['agent.send', 'runs.list', 'context.preview', 'compaction.run'] });
    await page.getByText('Execution unavailable — enable Runtime to send. Your draft is kept.').waitFor();
    check('composer keeps the draft when execution disappears', await ta.inputValue() === draft);
    check('sending is disabled without an executor', await page.locator('.composer button.send').isDisabled());
    await openStripTab('right', 'Diagnostics');
    await page.locator('.plugin-root').getByRole('button', { name: 'Context', exact: true }).click();
    await page.locator('.plugin-root').getByText('Context preview unavailable', { exact: false }).waitFor();
    check('diagnostics keeps instruction discovery without context preview', await page.locator('.plugin-root').getByText('AGENTS.md', { exact: true }).count() > 0);
    // the popout keeps its rows without the plugin, but has nothing to compact with
    await ring.click();
    await page.waitForSelector('.popover .cx');
    const noCompact = page.locator('.popover .cx .foot button', { hasText: 'Compact now' });
    await noCompact.waitFor({ state: 'detached', timeout: 5_000 }).catch(() => {});
    check('the popout hides Compact now when the plugin is not loaded', (await noCompact.count()) === 0, `${await noCompact.count()} button(s)`);
    await shot(page, '26c-no-compaction-plugin');
    await page.keyboard.press('Escape');
    await page.waitForTimeout(150);
    await rpcCall('mock.capabilities', { missing: [] });
    await page.waitForFunction(() => !document.querySelector('.composer button.send')?.disabled);
    check('execution recovery keeps the draft and restores Send', await ta.inputValue() === draft);
  } finally { await rpcCall('mock.capabilities', { missing: [] }); }
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
