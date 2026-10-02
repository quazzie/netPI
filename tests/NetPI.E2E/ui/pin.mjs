#!/usr/bin/env node
// UI test for pinned sessions against a REAL netpi-server + MockLlm (started by the C# E2E suite, test "ui.pinned-sessions").
// Pins a prepared session from the sessions panel and checks the "Pinned" group tops the list above the recency groups
// with the pinned row first; then unpins it and checks the group goes away and the row returns to its recency group.
//
//   node tests/NetPI.E2E/ui/pin.mjs --url http://127.0.0.1:7470 --token e2e-token --session "Pin me" [--out dir]
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
const SESSION = arg('--session', 'Pin me');
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
  const row = page.locator('.srow', { hasText: SESSION }).first();
  await row.waitFor({ timeout: 15_000 });
  check('session listed in the sessions panel', true);

  // no pinned group before anything is pinned
  check('no Pinned group before pinning', (await page.locator('.list > .glabel', { hasText: 'Pinned' }).count()) === 0);

  // hover opens the row's actions; the pin button toggles the session
  await row.hover();
  await row.getByRole('button', { name: 'Pin to top' }).click();
  await page.locator('.list > .glabel', { hasText: 'Pinned' }).first().waitFor({ timeout: 5_000 });
  const labels = await page.locator('.list > .glabel').allInnerTexts();
  check('a Pinned group tops the list above the recency groups', labels[0].toLowerCase() === 'pinned' && labels[1].toLowerCase() === 'today', labels.join(', '));
  const pinnedRow = page.locator('.list > .glabel:has-text("Pinned") + .srow');
  check('the pinned row leads the list', (await pinnedRow.count()) === 1 && (await pinnedRow.first().innerText()).includes(SESSION));
  check('the pinned row shows the unpin action', (await pinnedRow.getByRole('button', { name: 'Unpin' }).count()) === 1);
  await shot(page, 'ui-pin-01-pinned');

  // unpin: the group goes away and the row is back in its recency group
  await pinnedRow.first().hover();
  await pinnedRow.first().getByRole('button', { name: 'Unpin' }).click();
  const gone = await page.locator('.list > .glabel', { hasText: 'Pinned' }).first().waitFor({ state: 'detached', timeout: 5_000 }).then(() => true).catch(() => false);
  check('unpinning removes the Pinned group', gone);
  const labels2 = await page.locator('.list > .glabel').allInnerTexts();
  check('the row is back in its recency group', labels2[0].toLowerCase() === 'today' && (await page.locator('.srow', { hasText: SESSION }).count()) === 1, labels2.join(', '));
  await shot(page, 'ui-pin-02-unpinned');

  check('no console/page errors', errors.length === 0, errors.slice(0, 5).join(' | '));
  await context.close();
} catch (e) {
  errors.push(`[exception] ${e?.stack ?? e}`);
  check('pin script ran to the end', false, e?.message ?? e);
} finally {
  await browser.close();
}
const ok = checks.every((c) => c.ok);
if (!ok) exitCode = 1;
console.log(JSON.stringify({ ok, checks, errors }));
process.exit(exitCode);
