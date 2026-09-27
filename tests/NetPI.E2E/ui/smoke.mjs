#!/usr/bin/env node
// UI smoke test against a REAL netpi-server + MockLlm (started by the C# E2E suite, test "ui: …").
// Opens the app, sends a [s:tools] message in a prepared session, watches streaming + tool rows, opens the plugin tabs,
// takes screenshots and fails on console errors.
//
//   node tests/NetPI.E2E/ui/smoke.mjs --url http://127.0.0.1:7470 --token e2e-token --session "UI smoke" [--out dir]
// Prints one JSON line at the end: { ok, checks: [{name, ok, detail}], errors: [...] }.
import fs from 'node:fs';
import path from 'node:path';
import { execSync } from 'node:child_process';
import { fileURLToPath, pathToFileURL } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const argv = process.argv.slice(2);
const arg = (n, d) => (argv.indexOf(n) >= 0 ? argv[argv.indexOf(n) + 1] : d);
const BASE = arg('--url', 'http://127.0.0.1:7470');
const TOKEN = arg('--token', 'e2e-token');
const SESSION = arg('--session', 'UI smoke');
const TEXT = arg('--text', 'Please edit the notes [s:tools file=notes.txt old=alpha new=ALPHA-UI]');
const OUT = path.resolve(arg('--out', path.join(here, '..', 'screenshots')));
fs.mkdirSync(OUT, { recursive: true });

const checks = [];
const check = (name, ok, detail = '') => {
  checks.push({ name, ok: !!ok, detail: String(detail) });
  console.log(`  ${ok ? 'ok  ' : 'FAIL'} ${name}${detail ? ` (${detail})` : ''}`);
};
const shot = async (page, name) => {
  await page.screenshot({ path: path.join(OUT, `${name}.png`) });
  console.log(`  screenshot ${name}.png`);
};

async function loadPlaywright() {
  for (const name of ['playwright', 'playwright-core']) {
    try { return await import(name); } catch {}
  }
  const root = execSync('npm root -g', { encoding: 'utf8' }).trim();
  for (const name of ['playwright', 'playwright-core']) {
    try {
      const mod = await import(pathToFileURL(path.join(root, name, 'index.mjs')).href);
      return mod.default ?? mod;
    } catch {}
  }
  throw new Error('Playwright not found (npm i -g playwright)');
}

/** Playwright's own Chromium, else an installed Edge/Chrome when that build is not downloaded (Edge ships with Windows). */
async function launchBrowser(pw) {
  try {
    return await pw.chromium.launch();
  } catch (e) {
    if (!String(e?.message).includes("Executable doesn't exist")) throw e;
    for (const channel of process.platform === 'win32' ? ['msedge', 'chrome'] : ['chrome', 'msedge']) {
      try {
        const b = await pw.chromium.launch({ channel });
        console.log(`  browser: ${channel} (Playwright's Chromium build is not installed)`);
        return b;
      } catch {}
    }
    throw e;
  }
}

const pw = await loadPlaywright();
const browser = await launchBrowser(pw);
const errors = [];
let exitCode = 0;
try {
  const context = await browser.newContext({ viewport: { width: 1600, height: 1000 }, colorScheme: 'dark' });
  const page = await context.newPage();
  page.on('console', (m) => { if (m.type() === 'error') errors.push(`[console] ${m.text()}`); });
  page.on('pageerror', (e) => errors.push(`[pageerror] ${e.message}`));
  page.on('requestfailed', (r) => { if (!r.url().includes('/ws')) errors.push(`[requestfailed] ${r.url()} ${r.failure()?.errorText}`); });
  page.on('response', (r) => { if (r.status() >= 400) errors.push(`[http ${r.status()}] ${r.url()}`); });

  await page.goto(`${BASE}/?token=${encodeURIComponent(TOKEN)}`);
  await page.waitForSelector('.panel.left', { timeout: 15_000 });
  // open the prepared session from the sessions list
  const sessionsTab = page.locator('.panel.left .strip-tab', { hasText: 'Sessions' });
  if ((await sessionsTab.count()) && (await sessionsTab.getAttribute('aria-selected')) !== 'true') await sessionsTab.click();
  const row = page.locator('.srow', { hasText: SESSION }).first();
  await row.waitFor({ timeout: 15_000 });
  check('session listed in the sessions panel', true);
  await row.click();
  const ta = page.locator('.composer textarea');
  await ta.waitFor({ timeout: 10_000 });
  await page.waitForTimeout(300);
  await shot(page, 'ui-01-session');

  // the project picker lives in the composer bar (after the profile button)
  const projBtn = page.locator('.composer .bar button', { hasText: 'ui-demo' });
  check('composer shows the session project', (await projBtn.count()) > 0);
  if (await projBtn.count()) {
    await projBtn.first().click();
    const pop = page.locator('.popover', { has: page.locator('input[placeholder="Attach project…"]') });
    const popOk = await pop.first().waitFor({ timeout: 5_000 }).then(() => true).catch(() => false);
    check('project picker opens above the composer', popOk);
    if (popOk) {
      check('picker lists the current project', (await pop.first().innerText()).includes('ui-demo'));
    }
    await page.keyboard.press('Escape');
    await page.waitForTimeout(200);
    check('project picker closed (Esc)', (await page.locator('.popover').count()) === 0);
  }

  await ta.fill(TEXT);
  await ta.press('Enter');
  // the streamed answer is drawn as the chat's own rows while it streams (web/src/lib/chatItems.js withStream)
  const streamed = await page.waitForSelector('.item[data-stream], .thinking.live, .assistant.live, .tool[data-status="preparing"]', { timeout: 10_000 }).then(() => true).catch(() => false);
  check('streaming rows shown', streamed);
  await shot(page, 'ui-02-streaming');
  const toolOk = await page.waitForSelector('.tool[data-status="ok"]', { timeout: 20_000 }).then(() => true).catch(() => false);
  check('tool row finished (data-status=ok)', toolOk);
  await shot(page, 'ui-03-tools');
  const finished = await page.waitForFunction(() => !document.querySelector('.composer.running'), null, { timeout: 60_000 }).then(() => true).catch(() => false);
  check('run finished (composer idle)', finished);
  await page.waitForTimeout(800);
  await shot(page, 'ui-04-finished');

  const content = await page.locator('.scroller > .content').innerText().catch(() => '');
  check('final answer rendered', content.includes('TOOLS-DONE'), content.slice(-200).replace(/\s+/g, ' '));
  check('user message rendered', content.includes('Please edit the notes'));
  check('no leftover streaming rows', (await page.locator('.item[data-stream], .thinking.live, .assistant.live, .tool[data-status="preparing"]').count()) === 0);

  // expand collapsed step groups and the edit tool → diff
  const closed = page.locator('.group.collapsible .head[aria-expanded="false"]');
  for (let n = 0; n < 10 && (await closed.count()); n++) await closed.first().click();
  await page.waitForTimeout(200);
  const toolRows = await page.locator('.tool').count();
  check('4 tool rows (ls, read, edit, write)', toolRows >= 4, `${toolRows} rows`);
  const errRows = await page.locator('.tool[data-status="error"]').count();
  check('no tool rows in error state', errRows === 0, `${errRows} errors`);
  const editLine = page.locator('.tool .line', { hasText: 'Edit' }).last();
  if (await editLine.count()) {
    await editLine.click();
    await page.waitForTimeout(300);
  }
  check('edit diff rendered', (await page.locator('.diff .dl.add').count()) > 0);
  await shot(page, 'ui-05-expanded');

  // plugin tabs against the real server
  async function openStripTab(side, name) {
    const t = page.locator(`.panel.${side} .strip-tab`, { hasText: name });
    if (!(await t.count())) return false;
    if ((await t.getAttribute('aria-selected')) !== 'true') await t.click();
    if (!(await page.locator(`.panel.${side}.open`).count())) await t.click();
    return true;
  }
  for (const [name, file] of [['Work', 'ui-06-work'], ['Ideas', 'ui-07-ideas'], ['Diagnostics', 'ui-08-diagnostics']]) {
    const opened = await openStripTab('right', name);
    check(`plugin tab ${name} present`, opened);
    if (!opened) continue;
    await page.waitForTimeout(900);
    const text = await page.locator('.panel.right > .body').innerText().catch(() => '');
    check(`plugin tab ${name} rendered content`, text.trim().length > 20, `${text.trim().length} chars`);
    await shot(page, file);
  }
  const filesOpened = await openStripTab('left', 'Files');
  if (filesOpened) {
    await page.waitForTimeout(700);
    const text = await page.locator('.panel.left > .body').innerText().catch(() => '');
    check('files tab lists the project', text.includes('notes.txt'), text.slice(0, 120).replace(/\s+/g, ' '));
    await shot(page, 'ui-09-files');
  }

  // ---- live flows against the real server: subagents in the Work tab, steering, Esc abort, a retried stream
  const contentNow = () => page.locator('.scroller > .content').innerText().catch(() => '');
  const idle = (ms = 60_000) => page.waitForFunction(() => !document.querySelector('.composer.running'), null, { timeout: ms }).then(() => true).catch(() => false);
  const send = async (text, key = 'Enter') => { await ta.fill(text); await ta.press(key); };
  await openStripTab('left', 'Sessions');

  await openStripTab('right', 'Work');
  await send('Square 1..3 in parallel [s:spawn n=3 delay=2500]');
  const workers = await page.waitForFunction(() => {
    const t = document.querySelector('.panel.right > .body')?.innerText ?? '';
    return t.includes('worker-1') && t.includes('worker-3');
  }, null, { timeout: 15_000 }).then(() => true).catch(() => false);
  await page.waitForTimeout(700);
  await shot(page, 'ui-10-subagents-running');
  const workText = await page.locator('.panel.right > .body').innerText().catch(() => '');
  check('work tab lists the running workers', workers, workText.slice(0, 160).replace(/\s+/g, ' '));
  check('work tab shows the qwen pool full (2/2)', /2\s*\/\s*2/.test(workText));
  check('subagent run finished', await idle());
  await page.waitForTimeout(500);
  check('orchestrator summary rendered', (await contentNow()).includes('SPAWN-DONE'));
  await shot(page, 'ui-11-subagents-done');

  await send('Run the commands [s:slowtools]');
  const firstTool = await page.waitForSelector('.tool[data-status="running"]', { timeout: 15_000 }).then(() => true).catch(() => false);
  check('running tool row shown', firstTool);
  await send('Stop and read this first [s:echo]'); // Enter while running = steer
  check('steered run finished', await idle());
  await page.waitForTimeout(500);
  const afterSteer = await contentNow();
  check('steer message and answer rendered', afterSteer.includes('Stop and read this first') && afterSteer.includes('ECHO-DONE'));
  await shot(page, 'ui-12-steered');

  await send('Long answer please [s:slow ms=8000]');
  const partial = await page.waitForFunction(() => (document.querySelector('.scroller > .content')?.innerText ?? '').includes('part 3'), null, { timeout: 15_000 }).then(() => true).catch(() => false);
  check('slow answer streaming', partial);
  await ta.press('Escape');
  check('Esc stops the run', await idle(10_000));
  await page.waitForTimeout(500);
  const afterAbort = await contentNow();
  check('partial answer kept after Esc', afterAbort.includes('part 3') && !afterAbort.includes('SLOW-DONE'));
  await shot(page, 'ui-13-aborted');

  await send('Answer despite a flaky connection [s:drop]');
  // idle() right after Enter can pass before the composer switches to running: wait for the run to start first
  await page.waitForSelector('.composer.running', { timeout: 10_000 }).catch(() => {});
  check('retried run finished', await idle());
  await page.waitForTimeout(500);
  const afterDrop = await contentNow();
  check('retried answer shown', afterDrop.includes('DROP-RECOVERED'));
  check('discarded partial stream not shown (stream.reset)', !afterDrop.includes('connection drops in the middle'));
  await shot(page, 'ui-14-retried');

  check('no console/page errors', errors.length === 0, errors.slice(0, 5).join(' | '));
  await context.close();
} catch (e) {
  errors.push(`[exception] ${e?.stack ?? e}`);
  check('smoke script ran to the end', false, e?.message ?? e);
} finally {
  await browser.close();
}
const ok = checks.every((c) => c.ok);
if (!ok) exitCode = 1;
console.log(JSON.stringify({ ok, checks, errors }));
process.exit(exitCode);
