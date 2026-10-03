#!/usr/bin/env node
// idea-image.mjs: attach an image to a new idea in the Ideas tab — the first attach succeeds, then the next attach
// fails in the client (the file read inside prepareImage is broken for one call): the tab toasts the error and the
// Image button comes back. Before the fix the catch called an undefined `toast`, threw a ReferenceError, and the
// button stayed "Attaching…" forever (idea-r7j411).
//
//   node tests/NetPI.E2E/ui/idea-image.mjs --url http://127.0.0.1:7470 --token e2e-token --session "Idea image" [--out dir]
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
const SESSION = arg('--session', 'Idea image');
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

async function launchBrowser(pw) {
  try {
    return await pw.chromium.launch({ args: ['--disable-features=msWindowTabManagerPublic'] });
  } catch (e) {
    if (!String(e?.message).includes('Executable doesn\'t exist')) throw e;
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

// A 1x1 PNG: small enough that prepareImage passes it through untouched.
const PNG = Buffer.from(
  'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==',
  'base64',
);

const pw = await loadPlaywright();
const browser = await launchBrowser(pw);
const errors = [];
let exitCode = 0;
try {
  const context = await browser.newContext({ viewport: { width: 1600, height: 1000 }, colorScheme: 'dark' });
  const page = await context.newPage();
  page.on('console', (m) => { if (m.type() === 'error') errors.push(`[console] ${m.text()}`); });
  page.on('pageerror', (e) => errors.push(`[pageerror] ${e.message}`));
  page.on('response', (r) => { if (r.status() >= 400) errors.push(`[http ${r.status()}] ${r.url()}`); });

  await page.goto(`${BASE}/?token=${encodeURIComponent(TOKEN)}`);
  await page.waitForSelector('.panel.left', { timeout: 15_000 });

  // the prepared session, then the Ideas tab on the right
  const sessionsTab = page.locator('.panel.left .strip-tab', { hasText: 'Sessions' });
  if ((await sessionsTab.count()) && (await sessionsTab.getAttribute('aria-selected')) !== 'true') await sessionsTab.click();
  const row = page.locator('.srow', { hasText: SESSION }).first();
  await row.waitFor({ timeout: 15_000 });
  await row.click();
  await page.locator('.composer textarea').waitFor({ timeout: 10_000 });

  const ideasTab = page.locator('.panel.right .strip-tab', { hasText: 'Ideas' });
  if ((await ideasTab.count()) && (await ideasTab.getAttribute('aria-selected')) !== 'true') await ideasTab.click();
  if (!(await page.locator('.panel.right.open').count())) await ideasTab.click();
  await page.waitForTimeout(800);
  check('the Ideas tab opens', (await page.locator('.panel.right > .body .pane').count()) > 0);

  // the new-idea dialog (the host's, the one Ctrl+I opens)
  const newIdea = page.locator('.panel.right button[title^="New idea"]').first();
  await newIdea.click();
  const form = page.locator('.idea-dialog');
  await form.waitFor({ timeout: 15_000 }); // a condition, not a delay: it costs nothing when the tab is quick, and a busy machine (agents building beside the run) mounts it late
  await form.locator('.i-title').fill('Image attach probe');
  check('the new-idea dialog opens', true);

  // 1. a working attach: the thumbnail appears (the file was stored by the server)
  const fileA = path.join(OUT, 'idea-img-a.png');
  const fileB = path.join(OUT, 'idea-img-b.png');
  fs.writeFileSync(fileA, PNG);
  fs.writeFileSync(fileB, PNG);
  const fileInput = form.locator('input[type="file"]');
  await fileInput.setInputFiles([fileA]);
  const thumb = await page
    .waitForSelector('.idea-dialog .thumbs figure img', { timeout: 20_000 })
    .catch(() => null);
  check('the first image attaches', !!thumb);

  // 2. make the next attach fail in the client, inside prepareImage's read of the file: the catch in attachImage
  //    must toast the error and recover (the dialog's addFiles, since the form moved there). Before the fix that catch called an undefined `toast` — a ReferenceError
  //    that left the Image button stuck on "Attaching…" (idea-r7j411). A failed ideas.addImage RPC would not reach
  //    it: the call() wrapper swallows RPC errors and returns null.
  await page.evaluate(() => {
    const RealFileReader = window.FileReader;
    window.__realFileReader = RealFileReader;
    window.FileReader = class extends RealFileReader {
      readAsDataURL() {
        throw new Error('file read failed (test)');
      }
    };
  });
  await fileInput.setInputFiles([fileB]);

  // 3. the failure says so in a "Could not attach" toast
  const toast = await page
    .waitForSelector('.toasts .toast:has-text("Could not attach")', { timeout: 8_000 })
    .catch(() => null);
  const toastText = toast ? (await toast.innerText()) : '';
  check('the failed attach toasts its error', toastText.includes('Could not attach'), toastText.replace(/\s+/g, ' '));

  // 4. and the Image button comes back: whatever the attach did, `attaching` is reset on the failure path
  const recovered = await page
    .waitForFunction(
      () => {
        const b = document.querySelector('.idea-dialog .row button[title*="Attach"]');
        return !!b && !b.disabled && !/Attaching/.test(b.textContent || '');
      },
      null,
      { timeout: 8_000 },
    )
    .catch(() => false);
  check('the Image button is back after the failure', recovered);

  // 5. nothing on the page complained about the undefined helper
  check('no "toast is not defined" error on the page', !errors.some((e) => e.includes('toast is not defined')),
    errors.filter((e) => e.includes('toast')).join(' | '));
  check('the first image is still attached', (await form.locator('.thumbs figure').count()) === 1);

  // restore the reader for anything the page still does
  await page.evaluate(() => {
    if (window.__realFileReader) {
      window.FileReader = window.__realFileReader;
      delete window.__realFileReader;
    }
  });

  await shot(page, 'ui-idea-image');
} catch (e) {
  exitCode = 1;
  check('script ran to completion', false, String(e?.stack ?? e));
} finally {
  await browser.close();
}
const ok = exitCode === 0 && errors.length === 0 && checks.every((c) => c.ok);
console.log(JSON.stringify({ ok, checks, errors }));
process.exit(ok ? 0 : 1);
