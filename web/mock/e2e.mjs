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
