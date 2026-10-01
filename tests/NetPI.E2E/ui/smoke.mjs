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
// hold=1500: the mock keeps its first answer's stream open after the first chunk, so "streaming rows shown" looks at a state
// that is there for 1.5 s instead of racing one that is over in ~100 ms (a loaded machine missed it)
const TEXT = arg('--text', 'Please edit the notes [s:tools file=notes.txt old=alpha new=ALPHA-UI hold=1500]');
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
    return await pw.chromium.launch({ args: ["--disable-features=msWindowTabManagerPublic"] });
  } catch (e) {
    if (!String(e?.message).includes("Executable doesn't exist")) throw e;
    for (const channel of process.platform === 'win32' ? ['msedge', 'chrome'] : ['chrome', 'msedge']) {
      try {
        const b = await pw.chromium.launch({ channel, args: ["--disable-features=msWindowTabManagerPublic"] });
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

  // A pasted image larger than one WebSocket message is shrunk to fit instead of being cut off mid-send
  // (idea-8hfc3m): a noise PNG of ~4 MB used to be attached as it was, and the send then died on the 2 MB limit.
  const pasted = await page.evaluate(async () => {
    const canvas = document.createElement('canvas');
    canvas.width = 1600;
    canvas.height = 1200;
    const ctx = canvas.getContext('2d');
    const img = ctx.createImageData(canvas.width, canvas.height);
    for (let i = 0; i < img.data.length; i += 4) {           // real noise: a pattern would compress away
      img.data[i] = Math.random() * 256;
      img.data[i + 1] = Math.random() * 256;
      img.data[i + 2] = Math.random() * 256;
      img.data[i + 3] = 255;
    }
    ctx.putImageData(img, 0, 0);
    const blob = await new Promise((r) => canvas.toBlob(r, 'image/png'));
    const dt = new DataTransfer();
    dt.items.add(new File([blob], 'pasted.png', { type: 'image/png' }));
    document.querySelector('.composer textarea').dispatchEvent(
      new ClipboardEvent('paste', { clipboardData: dt, bubbles: true, cancelable: true }),
    );
    return blob.size;
  });
  const thumb = await page.waitForSelector('.composer .thumbs .thumb img', { timeout: 20_000 }).catch(() => null);
  check('a pasted image is attached', !!thumb);
  if (thumb) {
    const src = (await thumb.getAttribute('src')) ?? '';
    const kb = Math.round(src.length / 1024);
    check(`the attached image fits one message (${kb} KB of ${Math.round(pasted / 1024)} KB pasted)`, src.length < 1.2 * 1024 * 1024);
    check(`the attached image was shrunk (${Math.round(pasted / 1024)} KB → ${kb} KB)`, src.length < pasted);
    const toast = await page.locator('.toasts .toast', { hasText: 'shrunk' }).count().catch(() => 0);
    check('the composer says it shrank the image', toast > 0);
    await shot(page, 'ui-01b-image');
    await page.locator('.composer .thumbs .thumb-x').first().click();   // leave the composer as the rest of the run expects it
    check('the attachment can be removed again', (await page.locator('.composer .thumbs .thumb').count()) === 0);
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
  // ---- Ideas tab: the calm overview (idea-43oruq) — status groups, one line per idea, the rest on click
  if (await openStripTab('right', 'Ideas')) {
    await page.waitForTimeout(900);
    const pane = page.locator('.panel.right > .body .pane:not([hidden])');
    const text = (await pane.innerText().catch(() => '')).replace(/\s+/g, ' ');
    check('ideas grouped by status', /IN-PROGRESS/i.test(text) && /OPEN/i.test(text), text.slice(0, 140));
    check('a closed idea is its title only', text.includes('Smoke idea B') && !text.includes('SMOKE-SUMMARY-B'), text.slice(0, 140));
    await pane.locator('.card .main', { hasText: 'Smoke idea A' }).first().click();
    await page.waitForTimeout(400);
    const open = await pane.innerText().catch(() => '');
    check('opening an idea reveals summary, meta and sections', open.includes('SMOKE-SUMMARY-A') && /in-progress/.test(open), open.slice(0, 140).replace(/\s+/g, ' '));
    await shot(page, 'ui-07b-ideas-open');
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
  // Keep worker streams open independently of mock speed, then capture the panel text in the
  // same browser evaluation that observes both workers and a full pool. Screenshots can outlast
  // a live state; never read the assertion's evidence only after taking one.
  await send('Square 1..3 in parallel [s:spawn n=3 delay=2500 workerhold=3000]');
  const workText = await page.waitForFunction(() => {
    const t = document.querySelector('.panel.right > .body')?.innerText ?? '';
    return t.includes('worker-1') && t.includes('worker-3') && /2\s*\/\s*2/.test(t) ? t : null;
  }, null, { timeout: 15_000 }).then((handle) => handle.jsonValue()).catch(() => '');
  check('work tab lists the running workers', workText.includes('worker-1') && workText.includes('worker-3'), workText.slice(0, 160).replace(/\s+/g, ' '));
  check('work tab shows the qwen pool full (2/2)', /2\s*\/\s*2/.test(workText));
  await shot(page, 'ui-10-subagents-running');
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

  // Backup controls against the real plugin (not the UI mock).
  await page.keyboard.press('Control+,');
  await page.locator('.nav button', { hasText: 'Data & backups' }).click();
  const beforeBackups = await page.locator('.dialog .backup').count();
  await page.getByRole('button', { name: 'Back up now', exact: true }).click();
  await page.waitForFunction((before) => document.querySelectorAll('.dialog .backup').length > before, beforeBackups, { timeout: 30000 });
  check('backup settings create a snapshot', await page.locator('.dialog .backup').count() > beforeBackups);
  await page.locator('.dialog .backup').first().getByRole('button', { name: 'Verify', exact: true }).click();
  await page.getByText('Backup checksums verified', { exact: true }).waitFor({ timeout: 10000 });
  check('backup settings verify the snapshot', true);
  await shot(page, 'ui-15-backups');
  await page.keyboard.press('Escape');

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
