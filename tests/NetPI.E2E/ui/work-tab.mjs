#!/usr/bin/env node
// UI test: the Work tab (right panel) — the "Model capacity" and "Physical owners" sections are gone, the
// "Idea checks" section is still there, and clicking an agent's name in the Agents section opens
// Settings → Agents with that agent's dialog on top.
//
// The agent is seeded by the C# test (ui.work-tab) before this script runs.
//
//   node tests/NetPI.E2E/ui/work-tab.mjs --url http://127.0.0.1:7470 --token e2e-token --agent e2e-wt-abc123
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
const AGENT = arg('--agent', 'e2e-wt');
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
    try {
      return await import(name);
    } catch {}
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
    return await pw.chromium.launch({ args: ['--disable-features=msWindowTabManagerPublic'] });
  } catch (e) {
    if (!String(e?.message).includes("Executable doesn't exist")) throw e;
    for (const channel of process.platform === 'win32' ? ['msedge', 'chrome'] : ['chrome', 'msedge']) {
      try {
        const b = await pw.chromium.launch({ channel, args: ['--disable-features=msWindowTabManagerPublic'] });
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
  page.on('console', (m) => {
    if (m.type() === 'error') errors.push(`[console] ${m.text()}`);
  });
  page.on('pageerror', (e) => errors.push(`[pageerror] ${e.message}`));

  await page.goto(`${BASE}/?token=${encodeURIComponent(TOKEN)}`);
  await page.waitForSelector('.panel.left', { timeout: 15_000 });

  // open the Work tab in the right panel (toggle it open when it is the strip's selection but the panel is shut)
  const tab = page.locator('.panel.right .strip-tab', { hasText: 'Work' });
  if ((await tab.getAttribute('aria-selected')) !== 'true') await tab.click();
  if (!(await page.locator('.panel.right.open').count())) await tab.click();

  // the tab's sections are titled spans (a title attribute each); the two removed ones must not exist
  await page.waitForSelector('.panel.right .np-section-label[title="Agents"]', { timeout: 15_000 });
  await page.waitForTimeout(500);
  check('the Work tab has no Model capacity section', (await page.locator('.panel.right .np-section-label[title="Model capacity"]').count()) === 0);
  check('the Work tab has no Physical owners section', (await page.locator('.panel.right .np-section-label[title="Physical owners"]').count()) === 0);
  // the section that used to be here moved to the Diagnostics tab's Ideas view: it must not render on the Work tab
  await page.waitForTimeout(500);
  check('the Work tab has no Idea checks section', (await page.locator('.panel.right .np-section-label[title="Idea checks"]').count()) === 0);
  const labelTitles = await page.locator('.panel.right .np-section-label').evaluateAll((els) => els.map((e) => e.getAttribute('title')));
  check('the Work tab keeps its other sections', ['Agents', 'Runs', 'Processes', 'Usage today'].every((t) => labelTitles.includes(t)), `labels: ${labelTitles.join(', ')}`);

  // the agent the C# side seeded, listed by name in the Agents section
  const nameBtn = page.locator('.panel.right button.key', { hasText: AGENT });
  await nameBtn.first().waitFor({ timeout: 15_000 });
  check('the agent is listed in the Agents section', true);

  // clicking its name opens Settings → Agents with the agent's dialog on top
  await nameBtn.first().click();
  const dialog = page.locator('.dialog', { has: page.getByRole('button', { name: 'Remove agent' }) });
  const nameInput = dialog.locator('input[aria-label="Name"]');
  await nameInput.waitFor({ timeout: 10_000 });
  check('clicking the name opens the agent dialog in Settings → Agents', (await nameInput.inputValue()) === AGENT, `name input: ${await nameInput.inputValue().catch(() => '?')}`);
  await shot(page, 'ui-work-tab-01-agent-dialog');

  // close the dialog and the settings again; the Work tab keeps its state
  await dialog.getByRole('button', { name: 'Done' }).click();
  await page.keyboard.press('Escape');
  await page.waitForTimeout(300);
  await shot(page, 'ui-work-tab-02-after');

  check('no console/page errors', errors.length === 0, errors.slice(0, 5).join(' | '));
  await context.close();
} catch (e) {
  errors.push(`[exception] ${e?.stack ?? e}`);
  check('work-tab script ran to the end', false, e?.message ?? e);
} finally {
  await browser.close();
}
const ok = checks.every((c) => c.ok);
if (!ok) exitCode = 1;
console.log(JSON.stringify({ ok, checks, errors }));
process.exit(exitCode);
