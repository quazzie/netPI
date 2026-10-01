#!/usr/bin/env node
// End-to-end walkthrough against the mock server: drives the UI with Playwright, takes screenshots
// (1600×1000), and reports console errors + timings. By default it starts its own mock server on :7432
// (MOCK_SPEED=3) so it can also test reconnects; pass --url to use a running server instead.
//
//   npm run build && npm run e2e
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
// nothing is an error (exit 2) before anything starts, so a typo cannot be a green empty run.
const SECTIONS = [...fs.readFileSync(fileURLToPath(import.meta.url), 'utf8').matchAll(/^(?:if \(!EXTERNAL[^\n]*\{\n  )?log\('([^'\n]+)'\);/gm)].map((m) => m[1]);
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
  "tabs": ["new session + agent run"],
  "plugin tab: Files": ["new session + agent run"],
  "guardrails: a tool call waits for your OK": ["new session + agent run"],
  "plugin tab: Diagnostics": ["new session + agent run"],
  "settings": ["new session + agent run"],
  "images": ["new session + agent run"],
  "projects dialog": ["new session + agent run","start screen: the project new sessions start in"],
  "plugin tab: Ideas": ["new session + agent run","panels + plugin tabs"],
  "budget: chat cost, Work tab, a chat stopped by the budget": ["new session + agent run"],
  "profiles: settings, a project default, per chat": ["long session","new session + agent run"],
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
page.on('websocket', (ws) => {
  pageWs.push(`${stamp()} socket opened ${ws.url()}`);
  ws.on('framesent', (f) => {
    const p = String(f.payload);
    if (p.includes('"t":"rpc"')) { try { const m = JSON.parse(p); pageWs.push(`${stamp()} sent rpc ${m.id} ${m.m}`); } catch {} }
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
const right = page.locator('.panel.right > .body');
const leftBody = page.locator('.panel.left > .body');

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
  await page.locator('.earlier button', { hasText: 'jump to latest' }).click();
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
  check('a favorite project gives the top bar its chevron', (await page.locator('.topbar .quick-more').count()) === 1);

  await ta.fill('Keep the popups open while this answer streams.');
  await ta.press('Enter');
  await page.waitForFunction(() => !document.querySelector('.composer.running'), null, { timeout: 60_000 });
  await page.waitForTimeout(400);
  const overflows = await page.locator('.scroller').evaluate((el) => el.scrollHeight > el.clientHeight + 4).catch(() => false);

  // the chevron menu (a Menu): open it mid-run, so every streamed chunk re-pins the chat under it
  await ta.fill('Second answer, streamed under an open menu.');
  await ta.press('Enter');
  await page.waitForSelector('.composer.running', { timeout: 5000 }).catch(() => {});
  await page.locator('.topbar .quick-more').click();
  const opened = await page.waitForSelector('.np-menu', { timeout: 3000 }).then(() => true).catch(() => false);
  check('the chevron menu opens', opened);
  const anchorGap = async () => {
    // the gap the menu keeps to its button (null once the menu is gone: every check below reports, not the first one)
    const a = await page.locator('.topbar .quick-more').boundingBox();
    const m = await page.locator('.np-menu').boundingBox().catch(() => null);
    if (!a || !m) return null;
    return { dx: Math.round(m.x - (a.x + a.width - 220)), dy: Math.round(m.y - (a.y + a.height)) };
  };
  const before = await anchorGap();
  await page.waitForFunction(() => !document.querySelector('.composer.running'), null, { timeout: 60_000 });
  await page.waitForTimeout(300);
  const stillOpen = opened && (await page.locator('.np-menu').count()) === 1;
  check('the chat list was big enough to scroll (the case the bug needed)', overflows);
  check('a reply streaming under an open menu does not close it', stillOpen);
  const after = await anchorGap();
  check('the menu stays attached to the button that opened it', !!after && !!before && Math.abs(after.dx - before.dx) < 4 && Math.abs(after.dy - before.dy) < 4,
    stillOpen ? `moved ${after.dx - before.dx}px across, ${after.dy - before.dy}px down` : 'the menu was gone');

  // a scroll the user makes in the chat keeps it open too, and one that carries the anchor away still closes it
  if (stillOpen) {
    await page.mouse.move(640, 260);
    await page.mouse.wheel(0, -400);
    await page.waitForTimeout(300);
    check('scrolling the chat under the menu keeps it open', (await page.locator('.np-menu').count()) === 1);
    await shot(page, '12b-menu-survives');
    await page.keyboard.press('Escape');
    await page.waitForTimeout(150);
    check('Esc still closes the menu', (await page.locator('.np-menu').count()) === 0);
  } else {
    check('scrolling the chat under the menu keeps it open', false, 'the menu had already closed');
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
  check('the favorite is given back (the next section stars its own)', (await page.locator('.topbar .quick-more').count()) === 0);
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
// ------------------------------------------------------------------ panels + plugin tabs
if (want('panels + plugin tabs')) {
log('panels + plugin tabs');
await page.locator('.panel.right .strip-tab', { hasText: 'Events' }).click();
await page.waitForTimeout(500);
await shot(page, '13-plugin-events-tab');
await page.locator('.panel.right .strip-tab', { hasText: 'Sample' }).click();
await page.waitForTimeout(300);
// resize right panel
var hb = await handle.boundingBox();
await page.mouse.move(hb.x + 3, hb.y + 300);
await page.mouse.down();
await page.mouse.move(hb.x - 80, hb.y + 300, { steps: 5 });
await page.mouse.up();
var w = await page.locator('.panel.right .body').evaluate((el) => el.getBoundingClientRect().width);
check('resize right panel', w > 400, `${Math.round(w)}px`);
// collapse left panel by clicking the active strip tab
await page.locator('.panel.left .strip-tab[aria-selected="true"]').click();
await page.waitForTimeout(150);
check('collapse left panel', (await page.locator('.panel.left.open').count()) === 0);
await shot(page, '14-panels');
await page.locator('.panel.left .strip-tab', { hasText: 'Projects' }).click();
await page.waitForTimeout(200);
await shot(page, '15-projects');
// favorites live in the + menu, not as buttons in the top bar
var firstProject = page.locator('.panel.left .prow').first();
var projectName = (await firstProject.locator('.pname .np-ellipsis').innerText()).trim();
var toggleFav = async (row, title) => {
  await row.hover(); // the row's action buttons appear on hover
  await row.locator(`button[title^="${title}"]`).click();
};
check('no fav buttons in the top bar', (await page.locator('.topbar .fav').count()) === 0);
await toggleFav(firstProject, 'Add to favorites');
await page.waitForTimeout(200);
await page.locator('.topbar .quick-more').click();
await page.waitForSelector('.np-menu .np-menu-item');
var favItems = await page.locator('.np-menu .np-menu-item').filter({ hasText: projectName }).count();
check('favorites are in the + menu', favItems > 0, `${await page.locator('.np-menu .np-menu-header').innerText()}: ${projectName} (${favItems})`);
await page.keyboard.press('Escape');
await page.waitForTimeout(150);
await toggleFav(page.locator('.panel.left .prow').first(), 'Remove from favorites');
await page.waitForTimeout(200);
check('no favorites → no chevron', (await page.locator('.topbar .quick-more').count()) === 0);

await openStripTab('left', 'Sessions');
await page.locator('.srow', { hasText: 'Lane scheduler hardening' }).first().click();
await page.waitForTimeout(200);

}
// ------------------------------------------------------------------ the archived section: an archive 200 newer actives would hide
if (want('sessions: the archived section lists what a newest-first window hides')) {
log('sessions: the archived section lists what a newest-first window hides');
{
  // 201 active sessions (newest, one per hour back) + 59 newer archives, then one very old archive: in the old
  // "Show archived" query shape (the newest 200 active + archived, filtered locally) the old archive was outside
  // the window, so the panel said "Nothing archived".
  const HIDDEN = 'Old archive, hidden by 200 actives';
  const bulk = await rpcCall('mock.seedMany', { active: 201, archived: 59, title: HIDDEN });
  try {
    // the setup really hides it: the newest-200 window of active + archived holds no archive
    const window = await rpcCall('sessions.list', { includeArchived: true, limit: 200 });
    check('setup: 201 newer actives push the old archive out of the newest-200 window',
      window.length === 200 && window.every((s) => !s.archived),
      `${window.length} rows, archives in window: ${window.filter((s) => s.archived).length}`);

    const askForArchived = page.locator('.panel.left .foot .link', { hasText: 'Show archived' });
    await askForArchived.click();
    await page.locator('.srow.archived').first().waitFor({ timeout: 5000 }).catch(() => {});
    const asks = (await rpcCall('mock.listCalls')).filter((c) => c.archivedOnly);
    check('archived section: the archives are listed from an archivedOnly query',
      (await page.locator('.srow.archived').count()) === 50 && asks.length > 0,
      `archived rows: ${await page.locator('.srow.archived').count()}; archivedOnly calls: ${JSON.stringify(asks)}`);
    check('the hidden one is not in the first page (it is the oldest, 60 archives deep)',
      (await page.locator('.srow.archived', { hasText: HIDDEN }).count()) === 0);

    // 60 archives over 50-row pages: the hidden one is the oldest, so it is on page 2
    const more = page.locator('.panel.left .list button', { hasText: 'Show more' });
    check('archived section: 60 archives offer a second page', (await more.count()) === 1, `archived rows now: ${await page.locator('.srow.archived').count()}`);
    if (await more.count()) {
      await more.first().click();
      await page.locator('.srow.archived', { hasText: HIDDEN }).waitFor({ timeout: 5000 }).catch(() => {});
    }
    const asks2 = (await rpcCall('mock.listCalls')).filter((c) => c.archivedOnly);
    check('…and paging brings the hidden archive',
      (await page.locator('.srow.archived', { hasText: HIDDEN }).count()) > 0,
      `archived rows: ${await page.locator('.srow.archived').count()}; archivedOnly calls: ${JSON.stringify(asks2)}`);
    await shot(page, '15b-archived-hidden');
    // back to the plain list (the panel keeps its place for the later sections)
    const hide = page.locator('.panel.left .foot .link', { hasText: 'Hide archived' });
    if (await hide.count()) await hide.click();
    await page.waitForTimeout(150);
  } finally {
    await rpcCall('mock.clearMany');
  }
}

}
if (want('plugin tab: Work')) {
log('plugin tab: Work');
{
  await openStripTab('right', 'Work');
  await page.waitForSelector('.plugin-root .work .pool', { timeout: 10_000 });
  await page.waitForTimeout(400);
  const qwen = page.locator('.work .pool', { hasText: 'qwen3.8-27b' }).first();
  const nums = (await qwen.locator('.nums').innerText().catch(() => '')).replace(/\s+/g, '');
  check('work: qwen pool 2/2 busy', nums.includes('2/2'), nums);
  check('work: pool shows queued waiter', (await qwen.locator('.owner.waiting').count()) > 0);
  const physical = page.locator('.work [data-resource="local:aiproxy/qwen3.8-27b"]');
  check('work: physical model capacity appears once', await physical.count() === 1 && (await physical.innerText()).includes('2/2'));
  check('work: dropped background checks show their reason', (await page.locator('.work [data-background-work="mock-verifier-dropped"]').innerText()).includes('Model capacity did not open before the deadline'));
  const retiring = page.locator('.work [data-lease="mock-retiring"]');
  check('work: retiring inference explains pending provider cancellation', (await retiring.innerText()).includes('Retiring executor') && (await retiring.innerText()).includes('waiting for provider'));
  await retiring.getByRole('button', { name: 'Inspect model call' }).click();
  await page.locator('.work [role="status"]').filter({ hasText: '30000 ms' }).waitFor();
  check('work: physical owner resolves its stable model call', (await page.locator('.work [role="status"]').innerText()).includes('running'));
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
  // collapse a row while its first processes.output tail is still in flight: the abandoned open must
  // not leave a live subscription + 2 s poll timer behind (a closed row kept asking for tails)
  const emb = ((await rpcCall('processes.list')) ?? []).find((p) => p.command?.includes('embed.py'));
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
    check('work: a collapse mid-fetch leaves no tail polling behind', !!emb && calls0 - base >= 1 && calls1 === calls0, `${base} → ${calls0} → ${calls1} processes.output calls${emb ? '' : ' (embed.py not found)'}`);
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
  await page.locator('.work .node .row', { hasText: 'surveyor' }).first().click();
  await page.waitForTimeout(400);
  check('work: agent click opens its session', (await page.locator('.topbar .tab.active[data-tab="ses_bg_explore"]').count()) > 0, await page.locator('.topbar .tab.active').innerText().catch(() => ''));
  await page.locator('.srow', { hasText: 'Lane scheduler hardening' }).first().click();
}

}
if (want('plugin tab: Ideas')) {
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
  // status via the pill menu. The pill lives in the card's open body (idea-43oruq: a closed card is a title and
  // nothing else), so the card has to be open before the pill exists. This step had been clicking it on a closed
  // card, which is why the mock e2e died here: nothing in CI runs `npm run e2e`.
  if ((await cards().nth(1).locator('.main').getAttribute('aria-expanded')) !== 'true') await cards().nth(1).locator('.main').click();
  await cards().nth(1).locator('.status').click();
  await page.locator('.np-menu .np-menu-item', { hasText: 'in-progress' }).click();
  await page.waitForTimeout(500);
  check('ideas: status change', /in-progress/.test(await cards().nth(1).locator('.status').innerText()));
  // New idea, then a section on it. The list is grouped by status (in-progress, planned, open — idea-43oruq), so
  // "on top" means first *in its own group*, and every step below names the card instead of taking cards().first():
  // the step above just moved a card into the group that renders above open, which is what made this block rot.
  const cardTitled = (t) => page.locator('.ideas .card', { hasText: t }).first();
  await page.locator('.ideas .scope button[title="New idea"]').click();
  await page.locator('.ideas .new input').first().fill('Keyboard shortcuts cheat sheet');
  await page.locator('.ideas .new input.tags').fill('ui, docs');
  // An image attached while filing: pasted, shrunk to fit, stored by the host, and kept on the idea (idea-hai71q).
  const ref = await rpcCall('ideas.addImage', { data: 'aGVsbG8=', mediaType: 'image/png', name: 'tiny.png' });
  check('ideas: the host stores an image and returns a reference', /idea-images\/img-/.test(ref?.path ?? ''), JSON.stringify(ref));
  await page.evaluate(() => {
    const canvas = document.createElement('canvas');
    canvas.width = 1200;
    canvas.height = 900;
    const ctx = canvas.getContext('2d');
    const img = ctx.createImageData(canvas.width, canvas.height);
    for (let i = 0; i < img.data.length; i += 4) {
      img.data[i] = Math.random() * 256;
      img.data[i + 1] = Math.random() * 256;
      img.data[i + 2] = Math.random() * 256;
      img.data[i + 3] = 255;
    }
    ctx.putImageData(img, 0, 0);
    return canvas.toBlob((blob) => {
      const dt = new DataTransfer();
      dt.items.add(new File([blob], 'shot.png', { type: 'image/png' }));
      document.querySelector('.ideas .new textarea').dispatchEvent(new ClipboardEvent('paste', { clipboardData: dt, bubbles: true, cancelable: true }));
    }, 'image/png');
  });
  const thumb = await page.waitForSelector('.ideas .new .thumbs img', { timeout: 15_000 }).catch(() => null);
  // An attach that refuses says so in the form (a note) or as a toast; both are the diagnosis when this fails.
  const note = await page.locator('.ideas .new .note').allInnerTexts().catch(() => []);
  const attachToast = await page.locator('.np-toasts .np-toast, .toasts .toast').allInnerTexts().catch(() => []);
  check('ideas: a pasted image is attached while filing', !!thumb, [...note, ...attachToast].join('; '));
  await page.locator('.ideas .new button', { hasText: 'Add idea' }).click();
  await page.waitForTimeout(600);
  const newCard = cardTitled('Keyboard shortcuts cheat sheet');
  // The first card of the open group, whichever groups happen to sit above it.
  const firstOpen = await cards().evaluateAll((els) => {
    const open = els.find((e) => e.dataset.status === 'open');
    return open?.querySelector('.title')?.textContent?.trim() ?? null;
  });
  check('ideas: new idea added on top of the open ones', (await newCard.count()) === 1 && firstOpen === 'Keyboard shortcuts cheat sheet', `first open: ${firstOpen}`);
  // It joins the list collapsed, so filing several in a row does not push the previous one out of view (idea-qrp60h).
  check('ideas: the new card joins the list collapsed', (await newCard.locator('.main').getAttribute('aria-expanded')) === 'false');
  if ((await newCard.locator('.main').getAttribute('aria-expanded')) !== 'true') await newCard.locator('.main').click();
  // Open now, so the card's body (and its images) exist.
  check('ideas: the card shows the attached image once open', (await newCard.locator('.shots img').count()) === 1);
  const shotSrc = (await newCard.locator('.shots img').getAttribute('src').catch(() => '')) ?? '';
  check('ideas: the image is fetched back from the host, not kept in the list', shotSrc.startsWith('data:image/'), shotSrc.slice(0, 24));
  const prompt = await rpcCall('ideas.toPrompt', { id: (await newCard.locator('.info').innerText()).trim().split(' ')[0] });
  check('ideas: the full text hands the image to whoever works on it', /idea-images\/img-/.test(prompt), prompt?.split('\n').find((l) => l.startsWith('- ')) ?? 'no image line');
  await newCard.locator('.shots button[title="Remove image"]').click();
  await page.waitForTimeout(400);
  check('ideas: removing the image takes it off the card', (await newCard.locator('.shots').count()) === 0);
  await newCard.locator('.actions button[title="Add section"]').click();
  await newCard.locator('.sed select').selectOption('todo');
  await newCard.locator('.sed textarea').fill('- [ ] list shortcuts\n- [ ] render a table');
  await newCard.locator('.sed button', { hasText: 'Add section' }).click();
  await page.waitForTimeout(500);
  check('ideas: section added', (await newCard.locator('.sec').count()) === 1);
  // reorder, then delete through the host confirm dialog (both in the ⋯ menu)
  const moreMenu = async (card, item) => {
    await card.locator('.actions button[title="More actions"]').click();
    await page.locator('.np-menu .np-menu-item', { hasText: item }).click();
  };
  // send to chat → a pointer in the composer, not the idea's text (the agent reads the idea itself)
  await newCard.locator('.actions button[title^="Stage a pointer"]').click();
  await page.waitForTimeout(300);
  const ideaId = (await newCard.locator('.info').innerText()).trim().split(' ')[0];
  const staged = await ta.inputValue();
  check('ideas: send to chat stages a pointer, not the idea', staged.includes(ideaId) && !staged.includes('# Keyboard shortcuts cheat sheet'), staged);
  // …and the full text is one menu item away
  await moreMenu(newCard, 'Insert the full text');
  await page.waitForTimeout(300);
  check('ideas: insert the full text (ideas.toPrompt)', (await ta.inputValue()).includes('# Keyboard shortcuts cheat sheet'));
  await ta.fill('');
  // Move down moves within the group, so the card that was below it in the open group is now above it.
  const openBefore = await cards().evaluateAll((els) => els.filter((e) => e.dataset.status === 'open').map((e) => e.querySelector('.title')?.textContent?.trim()));
  await moreMenu(newCard, 'Move down');
  await page.waitForTimeout(500);
  const openAfter = await cards().evaluateAll((els) => els.filter((e) => e.dataset.status === 'open').map((e) => e.querySelector('.title')?.textContent?.trim()));
  check('ideas: move down (ideas.reorder)', openAfter.indexOf('Keyboard shortcuts cheat sheet') === openBefore.indexOf('Keyboard shortcuts cheat sheet') + 1,
    `${openBefore.join(' / ')} → ${openAfter.join(' / ')}`);
  await moreMenu(cardTitled('Keyboard shortcuts cheat sheet'), 'Delete idea');
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

}
if (want('ideas: recall on the first message (the chip above the composer)')) {
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

}
if (want('ideas: save on tab close (the card above the composer)')) {
log('ideas: save on tab close (the card above the composer)');
{
  // A chat with two user turns, told to leave a plan behind when its tab closes.
  await page.keyboard.press('Control+t');
  await page.waitForTimeout(400);
  await ta.fill('the prefill scheduler should run short prompts first');
  await page.keyboard.press('Enter');
  await page.waitForTimeout(700);
  await ta.fill('and measure it before we change anything');
  await page.keyboard.press('Enter');
  await page.waitForTimeout(700);
  const sid = await page.locator('.topbar .tab.active').getAttribute('data-tab');
  const armed = await rpcCall('mock.closeLeavesPlan', { phrase: 'prefill scheduler', title: 'Prefill: short prompts first' });
  check('save check: the mock was told to leave a plan (armed the closed chat)', armed === sid, `armed=${armed} chat=${sid}`);

  // Closing the tab never waits for the check: the card arrives behind it, above the composer of whatever chat is
  // open (the chat it came from is closed), so a new tab gives it somewhere to land.
  await page.locator('.topbar .tab.active .tab-close').click();
  await page.keyboard.press('Control+t');
  const card = page.locator('[aria-label="Unsaved plan from a closed chat"]');
  await card.waitFor({ timeout: 5000 }).catch(() => {});
  check('save check: a card appears after the tab closes', (await card.count()) === 1, await card.innerText().catch(() => ''));
  check('save check: the card names the closed chat', /prefill scheduler/.test(await card.innerText().catch(() => '')));
  await shot(page, '27c-idea-card');

  // Edit it, then save: the idea lands in the backlog with the chat on it.
  await card.locator('button', { hasText: 'Edit' }).click();
  await card.locator('input[aria-label="Idea title"]').fill('Prefill: short prompts first (measured)');
  await card.locator('button', { hasText: 'Save' }).click();
  await page.waitForTimeout(400);
  check('save check: the card goes away after Save', (await card.count()) === 0);
  const list = (await rpcCall('ideas.list'))?.ideas ?? [];
  const saved = list.find((i) => /Prefill: short prompts first/.test(i.title));
  check('save check: Save writes the idea with the closed chat on it', !!saved && saved.sessions?.length === 1, saved?.sessions?.[0]?.sessionId ?? 'no idea');

  // The commit check's card: an idea already in the backlog, and only its status in question. It goes to the same
  // project the tab is showing — the `projectId` above is scoped to that block, so resolve the name again here.
  const netpi = (await rpcCall('projects.list')).find((p) => p.name === 'netpi')?.id;
  await rpcCall('ideas.add', { projectId: netpi, idea: { title: 'Retry: shorter notices', summary: 'One line, no ids.' } });
  await rpcCall('mock.commitFinishesIdea', { phrase: 'Retry: shorter' });
  const doneCard = page.locator('[aria-label="An idea a commit may have finished"]');
  await doneCard.waitFor({ timeout: 5000 }).catch(() => {});
  check('commit check: a card offers an idea a commit may have finished', (await doneCard.count()) === 1, await doneCard.innerText().catch(() => ''));
  check('commit check: the card names the idea and its commit',
    /Retry: shorter notices/.test(await doneCard.innerText().catch(() => '')) && /measured/.test(await doneCard.innerText().catch(() => '')));
  await shot(page, '27d-idea-done-card');
  await doneCard.locator('button', { hasText: 'Mark done' }).click();
  await page.waitForTimeout(400);
  check('commit check: the card goes away after Mark done', (await doneCard.count()) === 0);
  const afterDone = (await rpcCall('ideas.list'))?.ideas ?? [];
  const closed = afterDone.find((i) => /Retry: shorter notices/.test(i.title));
  check('commit check: Mark done closes the idea and keeps it in place', closed?.status === 'done', `${closed?.status} ${closed?.id}`);

  // A second chat closes with nothing to save: no card, and the backlog is untouched. Counted here rather than
  // reused from the snapshot above: the commit-check block added an idea since then, so that count was stale.
  const before = ((await rpcCall('ideas.list'))?.ideas ?? []).length;
  await ta.fill('thanks, that all worked out');
  await page.keyboard.press('Enter');
  await page.waitForTimeout(600);
  await ta.fill('and the deploy is done too');
  await page.keyboard.press('Enter');
  await page.waitForTimeout(600);
  await page.locator('.topbar .tab.active .tab-close').click();
  await page.keyboard.press('Control+t');
  await page.waitForTimeout(900);
  check('save check: a chat with nothing unsaved leaves no card', (await card.count()) === 0);
  check('save check: and nothing is added to the backlog', ((await rpcCall('ideas.list'))?.ideas ?? []).length === before);
}

}
if (want('plugin tab: Diagnostics')) {
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
  // the tool-set history: a plugin reload took two tools away, with its cause
  await page.locator('.diag .np-section-title', { hasText: 'Tool changes' }).locator('button').click();
  await page.waitForTimeout(300);
  const changes = await page.locator('.diag .change').allInnerTexts();
  check('diagnostics: tool changes name the cause and the plugin',
    changes.some((t) => t.includes('plugin-reload') && t.includes('netpi.tools.shell') && t.includes('bash')));
  await right.screenshot({ path: path.join(OUT, '30-diagnostics-context.png') });

  // the Calls view polls diag.calls every 2 s: with a slow answer the next poll must not stack on top of the
  // in-flight one (the mock counts in-flight requests, so a pass cannot come from a quiet server)
  log('  diagnostics: the Calls view polls single-flight'); // indented: a sub-header must not start a new section (--only would drop the checks after it)
  {
    await rpcCall('mock.diagCallsDelay', { ms: 2500 }); // slower than the 2 s poll interval
    await view('Calls');
    await page.locator('.diag .call').first().waitFor({ timeout: 6000 });
    check('diagnostics: the Calls view lists the model calls', (await page.locator('.diag .call').count()) === 3);
    await page.waitForTimeout(9500); // several poll cycles with the slow answer in flight
    const stats = await rpcCall('mock.diagCallsStats');
    await rpcCall('mock.diagCallsDelay', { ms: 0 });
    check(
      'diagnostics: a slow diag.calls never stacks a second request on top',
      stats.served >= 2 && stats.maxInFlight === 1,
      `${stats.served} polls, peak in flight ${stats.maxInFlight}`,
    );
  }
  // leaving the view while a diag.calls is still pending: its answer must not start another poll (the destroyed
  // view's timer chain used to live on, polling for a view nobody sees)
  log('  diagnostics: a destroyed Calls view stops polling');
  {
    await rpcCall('mock.diagCallsDelay', { ms: 2500 });
    await view('Calls');
    for (let i = 0; i < 40 && (await rpcCall('mock.diagCallsStats')).inFlight < 1; i++) await page.waitForTimeout(50);
    check('diagnostics: the Calls view has a diag.calls in flight', (await rpcCall('mock.diagCallsStats')).inFlight === 1);
    await view('Plugins'); // destroys the Calls view with the request still pending
    const before = await rpcCall('mock.diagCallsStats');
    await page.waitForTimeout(9000); // the pending answer lands, then two poll intervals and a slow answer would have passed
    const after = await rpcCall('mock.diagCallsStats');
    await rpcCall('mock.diagCallsDelay', { ms: 0 });
    check(
      'diagnostics: a destroyed Calls view starts no further diag.calls',
      after.served - before.served <= 1 && after.inFlight === 0,
      `${after.served - before.served} answers after leaving (the pending one is allowed), ${after.inFlight} in flight`,
    );
  }
  await view('Plugins');
}

}
if (want('plugin tab: Files')) {
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

  // A workspace switch mid-fetch: the old workspace's late answers must not apply, and an open search must rerun
  log('  files: a workspace switch drops late answers from the old workspace'); // indented: see the Calls sub-headers
  {
    // a second, real workspace: the mock's own folder as a project (its top level is not the repo root's)
    const ALT = path.join(repo, 'web', 'mock');
    const alt = await rpcCall('projects.create', { name: 'altws', path: ALT });
    const sid = await page.locator('.topbar .tab.active').getAttribute('data-tab');
    const norm = (p) => p.toLowerCase().replaceAll('\\', '/');
    const calls = () => rpcCall('mock.filesCalls');
    const lastIdx = async (m, root) => (await calls()).reduce((i, c, j) => (c.m === m && norm(c.root) === norm(root) ? j : i), -1);
    const row = (name) => page.locator('.files .frow .fname', { hasText: new RegExp(`^${name.replace(/\./g, '\\.')}$`) });
    const flatRow = (name) => page.locator('.files .frow.flat .fname', { hasText: new RegExp(`^${name.replace(/\./g, '\\.')}$`) });
    try {
      // (1) the old workspace's listing is in flight when the tab switches; its answer arrives last
      await rpcCall('mock.filesDelay', { ms: 2500 });
      await page.locator('.files .head button[title="Refresh"]').click();
      await page.waitForTimeout(300); // inside the 2500 ms window
      await rpcCall('mock.filesDelay', { ms: 100 }); // read when the new workspace's requests are handled
      await rpcCall('sessions.setProject', { id: sid, projectId: alt.id }); // the tab's workspace → web/mock
      const n0 = (await calls()).length;
      for (let i = 0; i < 60; i++) {
        if ((await calls()).slice(n0).some((c) => c.m === 'files.list' && norm(c.root) === norm(repo))) break; // the late answer was served
        await page.waitForTimeout(100);
      }
      await page.waitForTimeout(400); // the UI has had the late answer in hand
      const iAlt = await lastIdx('files.list', ALT);
      const iRepo = await lastIdx('files.list', repo);
      const rootShown = norm(await page.locator('.files .head .rpath').innerText());
      check(
        'files: the late old-workspace listing is not applied',
        rootShown === norm(ALT) && (await row('e2e.mjs').count()) > 0 && (await row('fake-openai.mjs').count()) > 0 && (await row('AGENTS.md').count()) === 0,
        `root now: ${rootShown || '∅'}`,
      );
      check("files: the old workspace's late answer really arrived, after the new one's",
        iRepo > iAlt >= 0, `served: new @${iAlt}, late old @${iRepo} of ${(await calls()).length}`);
      // (2) the search the user left open: a workspace switch reruns it for the new workspace, not drops it
      await rpcCall('mock.filesDelay', { ms: 0 });
      await page.locator('.files .np-search input').fill('readme');
      await page.waitForTimeout(700); // debounce + the search in this workspace: no hits here
      const n1 = (await calls()).length;
      const netpi = (await rpcCall('projects.list')).find((p) => p.name === 'netpi');
      await rpcCall('sessions.setProject', { id: sid, projectId: netpi.id }); // the same query, the other workspace
      await page
        .waitForFunction(() => [...document.querySelectorAll('.files .frow.flat .fname')].some((e) => e.textContent === 'README.md'), null, { timeout: 5000 })
        .catch(() => {});
      const reruns = (await calls()).slice(n1).filter((c) => c.m === 'files.search');
      check(
        'files: a workspace switch reruns the open search for the new workspace',
        (await flatRow('README.md').count()) > 0 && reruns.some((c) => norm(c.root) === norm(repo)),
        `searches after the switch: ${reruns.map((c) => (norm(c.root) === norm(repo) ? 'repo' : 'alt')).join(', ') || 'none'}`,
      );
      await page.locator('.files .np-search input').fill('');
    } finally {
      await rpcCall('mock.filesDelay', { ms: 0 });
    }
    await rpcCall('projects.delete', { id: alt.id }); // keep the later project sections on the seeded state
  }
  await openStripTab('left', 'Sessions');
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
if (want('narrow side panels')) {
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

}
if (want('project picker + new session project')) {
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

}
if (want('start screen: the project new sessions start in')) {
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

}
if (want('projects dialog')) {
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

}
// ------------------------------------------------------------------ settings as controls: fields, agents, budget, tools
if (want('settings: controls, agents, budget, tools')) {
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

}
// ------------------------------------------------------------------ the tools of one chat
if (want('chat tools: switched per session')) {
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
// ------------------------------------------------------------------ profiles
if (want('profiles: settings, a project default, per chat')) {
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
await page.waitForSelector('.notice', { hasText: 'Summary' }).catch(() => {});
await page.waitForTimeout(300);
check('/compact (server rpc command) adds a summary', (await page.locator('.notice .label', { hasText: 'Summary' }).count()) > 0);

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
// ------------------------------------------------------------------ folder picker (fs.dirs) + add project
if (want('projects: folder picker')) {
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
var picked = await page.locator('.projects-dialog .pathrow input').inputValue();
check('folder picker fills the path', picked.endsWith('/web') || picked.endsWith('\\web'), picked);
check('the folder name becomes the project name', (await page.locator('.projects-dialog input.np-input:not(.np-mono)').inputValue()) === 'web');
await page.locator('.projects-dialog .np-btn-primary', { hasText: 'Create project' }).click();
await page.waitForTimeout(300);
check('project added', (await page.locator('.panel.left .prow .pname', { hasText: 'web' }).count()) > 0);
check('the project dialog closed', (await page.locator('.projects-dialog').count()) === 0);

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
if (!EXTERNAL && !argv.includes('--no-dev') && want('vite dev server')) {
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

if (!EXTERNAL && want('capability removal and recovery')) {
log('capability removal and recovery');
  await newTab();
  const draft = 'Keep this draft while the executor is unavailable.';
  await ta.fill(draft);
  try {
    await rpcCall('mock.capabilities', { missing: ['agent.send', 'runs.list', 'context.preview'] });
    await page.getByText('Execution unavailable — enable Runtime to send. Your draft is kept.').waitFor();
    check('composer keeps the draft when execution disappears', await ta.inputValue() === draft);
    check('sending is disabled without an executor', await page.locator('.composer button.send').isDisabled());
    await openStripTab('right', 'Diagnostics');
    await page.locator('.plugin-root').getByRole('button', { name: 'Context', exact: true }).click();
    await page.locator('.plugin-root').getByText('Context preview unavailable', { exact: false }).waitFor();
    check('diagnostics keeps instruction discovery without context preview', await page.locator('.plugin-root').getByText('AGENTS.md', { exact: true }).count() > 0);
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
