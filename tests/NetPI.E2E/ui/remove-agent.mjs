#!/usr/bin/env node
// UI test: Settings → Agents & budget → agent row → "Remove agent" → confirm. The confirmed removal must
// remove the agent — from the list AND from the settings document (settings.get), not just hide the row —
// and the page must actually dispatch settings.set { path: agents.<id>, value: null } and get an answer.
//
// The agent is seeded by the C# test (ui.remove-agent) before this script runs.
//
//   node tests/NetPI.E2E/ui/remove-agent.mjs --url http://127.0.0.1:7470 --token e2e-token --agent e2e-rm-abc123
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
const AGENT = arg('--agent', 'e2e-rm');
const OUT = path.resolve(arg('--out', path.join(here, '..', 'screenshots')));
fs.mkdirSync(OUT, { recursive: true });

const checks = [];
const check = (name, ok, detail = '') => {
  checks.push({ name, ok: !!ok, detail: String(detail) });
  console.log(`  ${ok ? 'ok  ' : 'FAIL'} ${name}${detail ? ` (${detail})` : ''}`);
};
// [dbg-*] console lines the page emits during the flow: evidence for a failed check
const dbg = [];
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
    if (m.type() === 'debug' && m.text().startsWith('[dbg-')) dbg.push(m.text());
    if (m.type() === 'error') errors.push(`[console] ${m.text()}`);
  });
  page.on('pageerror', (e) => errors.push(`[pageerror] ${e.message}`));

  // Every frame the page sends/receives on its WebSocket, parsed when JSON: the evidence for what the
  // remove flow actually put on the wire (and whether the host answered).
  const sent = [];
  const received = [];
  const record = (list, payload) => {
    try {
      const m = JSON.parse(String(payload));
      if (m.t === 'rpc') list.push({ id: m.id, m: m.m, p: m.p ?? null });
      else if (m.t === 'res') list.push({ id: m.id, m: m.m, res: m.r ?? null, e: m.e ?? null });
    } catch {}
  };
  page.on('websocket', (ws) => {
    ws.on('framesent', (f) => record(sent, f.payload));
    ws.on('framereceived', (f) => record(received, f.payload));
  });

  await page.goto(`${BASE}/?token=${encodeURIComponent(TOKEN)}`);
  await page.waitForSelector('.panel.left', { timeout: 15_000 });

  // Settings → Agents & budget. .agents scopes to the settings list: the Work tab renders [data-agent] pool rows too.
  await page.locator('button[title="Settings (Ctrl+,)"]').click();
  await page.locator('.nav button', { hasText: 'Agents & budget' }).click();
  const row = page.locator(`.agents [data-agent="${AGENT}"]`);
  await row.first().waitFor({ timeout: 15_000 });
  check('agent listed in the settings', true);

  // open the agent's dialog
  await row.first().click();
  const dialog = page.locator('.dialog', { has: page.getByRole('button', { name: 'Remove agent' }) });
  await dialog.getByRole('button', { name: 'Remove agent' }).waitFor({ timeout: 5_000 });
  check('agent dialog open', true);

  // "Remove agent" → confirm
  await dialog.getByRole('button', { name: 'Remove agent' }).click();
  const confirm = page.locator('.dialog', { has: page.getByRole('button', { name: 'Cancel' }) });
  await confirm.getByRole('button', { name: 'Remove', exact: true }).click();
  await confirm.waitFor({ state: 'detached', timeout: 5_000 }).catch(() => {});

  // give the RPC a moment (it must not need 30 s to time out)
  await page.waitForTimeout(2000);

  const setFrames = sent.filter((f) => f.m === 'settings.set' && f.p?.path === `agents.${AGENT}`);
  check(
    'the page sends settings.set { value: null } for the agent',
    setFrames.length === 1 && (setFrames[0].p.value ?? null) === null,
    `sent=${JSON.stringify(setFrames)}`
  );
  if (setFrames.length === 1) {
    const reply = received.find((r) => r.id === setFrames[0].id);
    check('the host answers the settings.set', reply !== undefined, reply ? JSON.stringify(reply) : 'no reply frame');
  }

  const rowGone = (await row.count()) === 0;
  check('the agent row is gone from the list', rowGone);

  // the real thing: the settings document no longer has the agent (a hidden row is not a removal)
  const res = await fetch(`${BASE}/api/rpc/settings.get`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', 'X-NetPI-Token': TOKEN },
    body: '{}',
  });
  const doc = (await res.json())?.settings ?? {};
  const keys = Object.keys(doc.agents ?? {});
  check('settings.get: the agent is removed from the document', !keys.includes(AGENT), `agents: ${keys.join(', ')}`);

  // a failure surfaces as a toast, not silence
  const toastText = await page.locator('.toast').allInnerTexts().catch(() => []);
  check('no error toast', !toastText.some((t) => /failed|error/i.test(t)), toastText.join(' | '));

  await shot(page, 'ui-remove-agent-01-after');

  check('no console/page errors', errors.length === 0, errors.slice(0, 5).join(' | '));
  await context.close();
} catch (e) {
  errors.push(`[exception] ${e?.stack ?? e}`);
  check('remove-agent script ran to the end', false, e?.message ?? e);
} finally {
  await browser.close();
}
const ok = checks.every((c) => c.ok);
if (!ok) exitCode = 1;
console.log(JSON.stringify({ ok, checks, errors, dbg }));
process.exit(exitCode);
