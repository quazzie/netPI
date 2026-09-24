// Resolve Playwright without making it a dependency: local install first, then the global npm root.
import { execSync } from 'node:child_process';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

export async function loadPlaywright() {
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
  throw new Error('Playwright not found (npm i -g playwright, or npm i -D playwright-core)');
}

/** Playwright's own Chromium, else an installed Edge/Chrome when that build is not downloaded (Edge ships with Windows). */
export async function launchBrowser(pw) {
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

/** Launch chromium and open the app. Collects console errors in `errors`. */
export async function openApp({ url = 'http://127.0.0.1:7431/?token=dev', width = 1600, height = 1000, theme } = {}) {
  const pw = await loadPlaywright();
  const browser = await launchBrowser(pw);
  const context = await browser.newContext({ viewport: { width, height }, deviceScaleFactor: 1, colorScheme: theme ?? 'dark' });
  const page = await context.newPage();
  const errors = [];
  page.on('console', (m) => {
    if (m.type() === 'error' || m.type() === 'warning') errors.push(`[${m.type()}] ${m.text()}`);
  });
  page.on('pageerror', (e) => errors.push(`[pageerror] ${e.message}`));
  await page.goto(url);
  return { pw, browser, context, page, errors };
}
