#!/usr/bin/env node
// UI test for closing a chat that never got a message, against a REAL netpi-server + MockLlm
// (started by the C# E2E suite, test "ui.close-empty-session").
// A session is materialized by its first message, so a chat closed before that was never stored: closing its tab must
// take it out of the sessions panel too, or a "New session" row the server will never list again sits there for the rest
// of the window. The seeded chat -- which has a message -- keeps its row when its tab is closed.
//
//   node tests/NetPI.E2E/ui/close-empty-session.mjs --url http://127.0.0.1:7470 --token e2e-token --session "Keep me" [--out dir]
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
const SESSION = arg('--session', 'Keep me');
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
  const context = await browser.newContext({ viewport: { width: 1200, height: 900 }, colorScheme: 'dark' });
  const page = await context.newPage();
  page.on('console', (m) => { if (m.type() === 'error') errors.push(`[console] ${m.text()}`); });
  page.on('pageerror', (e) => errors.push(`[pageerror] ${e.message}`));

  await page.goto(`${BASE}/?token=${encodeURIComponent(TOKEN)}`);
  await page.waitForSelector('.panel.left', { timeout: 15_000 });
  const sessionsTab = page.locator('.panel.left .strip-tab', { hasText: 'Sessions' });
  if ((await sessionsTab.count()) && (await sessionsTab.getAttribute('aria-selected')) !== 'true') await sessionsTab.click();

  const seeded = page.locator('.panel.left .srow', { hasText: SESSION }).first();
  await seeded.waitFor({ timeout: 15_000 });

  // Identity, never a total: this browser starts from the tabs another window left in the host's ui.state, and the other
  // tests on this server open and close chats of their own, so any count moves under the test. The chat under test is
  // its row by session id (the tab's data-tab is the session id).
  const rowOf = (id) => page.locator(`.panel.left .srow[data-session="${id}"]`);

  // a new chat: it opens a tab and shows in the list while it is open
  await page.locator('.topbar .new-tab').click();
  const active = page.locator('.topbar .tab.active');
  await active.waitFor({ timeout: 15_000 });
  // pin the tab by its session id: once it is closed the next tab becomes active, and a locator on ".active" would
  // follow it and wait forever for the wrong element to go away
  const sid = await active.getAttribute('data-tab');
  const mine = page.locator(`.topbar .tab[data-tab="${sid}"]`);
  await rowOf(sid).waitFor({ timeout: 10_000 });
  check('a new chat is listed while its tab is open', true, sid);
  await shot(page, 'ui-close-empty-01-open');

  // closing it: the tab goes, and with it the row the server would never list again
  await mine.locator('.tab-close').click();
  await mine.waitFor({ state: 'detached', timeout: 10_000 });
  const gone = await rowOf(sid).waitFor({ state: 'detached', timeout: 10_000 }).then(() => true).catch(() => false);
  check('closing a chat with no messages takes its row out of the list', gone, `${await rowOf(sid).count()} rows for ${sid}, want 0`);
  check('the seeded chat is still listed', (await page.locator('.panel.left .srow', { hasText: SESSION }).count()) === 1);
  await shot(page, 'ui-close-empty-02-closed');

  // the other half: a chat that has a message is a session, and closing its tab keeps its row
  await seeded.click();
  const openTab = page.locator('.topbar .tab.active');
  await openTab.waitFor({ timeout: 15_000 });
  const keepTab = page.locator(`.topbar .tab[data-tab="${await openTab.getAttribute('data-tab')}"]`);
  check('the seeded chat opened in a tab', (await keepTab.innerText()).includes(SESSION), await keepTab.innerText());
  await keepTab.locator('.tab-close').click();
  await keepTab.waitFor({ state: 'detached', timeout: 10_000 });
  check('closing a chat with messages keeps its row', (await page.locator('.panel.left .srow', { hasText: SESSION }).count()) === 1);

  check('no console/page errors', errors.length === 0, errors.slice(0, 5).join(' | '));
  await context.close();
} catch (e) {
  errors.push(`[exception] ${e?.stack ?? e}`);
  check('close-empty-session script ran to the end', false, e?.message ?? e);
} finally {
  await browser.close();
}
const ok = checks.every((c) => c.ok);
if (!ok) exitCode = 1;
console.log(JSON.stringify({ ok, checks, errors }));
process.exit(exitCode);