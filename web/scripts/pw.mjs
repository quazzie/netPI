// Playwright for the UI checks (web/scripts/check-*.mjs, bench-stream.mjs) without making it a dependency: a local
// install first (playwright-core is a devDependency), then the global npm root.
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

/**
 * The browser every check runs in: NETPI_BROWSER (the path of a Chromium-based executable: a preinstalled Chromium of
 * another revision, a Chrome somewhere unusual) first, else Playwright's own Chromium, else an installed Edge/Chrome
 * when that build is not downloaded (Edge ships with Windows).
 */
export async function launchBrowser(pw) {
  const executablePath = process.env.NETPI_BROWSER?.trim();
  if (executablePath) {
    const b = await pw.chromium.launch({ executablePath });
    console.log(`  browser: ${executablePath} (NETPI_BROWSER)`);
    return b;
  }
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

/**
 * Poll `probe` until `pass(value)` holds — the checks' way of "wait for a state" (a fixed sleep is a flake: too short on a
 * loaded machine, too long on an idle one; the journeys' expectEventually is the same idea). A probe that throws (an
 * element that is not there yet) counts as "not yet". Returns the value it saw, so the caller's check fails with it when
 * `timeout` ms (default 5000) come up. A negative guarantee (nothing should happen) has no state to poll for and keeps a
 * bounded sleep instead.
 */
export async function settle(probe, pass, timeout = 5000, every = 25) {
  const end = Date.now() + timeout;
  let last;
  for (;;) {
    try {
      last = await probe();
      if (pass(last)) return last;
    } catch {}
    if (Date.now() >= end) return last;
    await new Promise((r) => setTimeout(r, every));
  }
}
