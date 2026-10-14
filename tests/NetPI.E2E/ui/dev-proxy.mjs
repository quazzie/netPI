#!/usr/bin/env node
// UI test: the vite dev server (npm run dev) in front of a real server — it proxies the socket, the API and the plugin
// bundles to NETPI_URL, and ?token= signs the page in (and leaves the address).
//
//   node tests/NetPI.E2E/ui/dev-proxy.mjs --url http://127.0.0.1:7470 --token e2e-token
// Prints one JSON line at the end: { ok, checks: [{name, ok, detail}], errors: [...] }.
import fs from 'node:fs';
import net from 'node:net';
import path from 'node:path';
import { spawn } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { run, check, shot, openStripTab, BASE, TOKEN } from './lib.mjs';

const repo = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..', '..');
const viteBin = path.join(repo, 'node_modules', 'vite', 'bin', 'vite.js');
const freePort = () => new Promise((resolve, reject) => {
  const s = net.createServer().listen(0, '127.0.0.1', () => { const { port } = s.address(); s.close(() => resolve(port)); });
  s.on('error', reject);
});

await run('dev-proxy', async ({ page, errors }) => {
  // the dev server runs this checkout's web/vite.config.js, which reads this checkout's node_modules (svelte's version):
  // a git worktree has none until npm ci runs in it (build.ps1 does that when it builds the web), and the main
  // checkout's do not count. Say so, not vite's ENOENT.
  if (!fs.existsSync(viteBin)) throw new Error(`${repo} has no node_modules: run npm ci there (or build the web with build.ps1)`);
  const port = await freePort();
  const vite = spawn(process.execPath, [viteBin, '--config', 'web/vite.config.js', '--port', String(port), '--strictPort', '--host', '127.0.0.1'], {
    cwd: repo,
    env: { ...process.env, NETPI_URL: BASE },
    stdio: ['ignore', 'pipe', 'pipe'],
  });
  let said = '';
  vite.stdout.on('data', (d) => (said += d));
  vite.stderr.on('data', (d) => (said += d));
  try {
    await new Promise((resolve, reject) => {
      const t = setTimeout(() => reject(new Error(`vite did not start: ${said.slice(-400)}`)), 30_000);
      vite.stdout.on('data', () => /Local:|ready in/.test(said) && (clearTimeout(t), resolve()));
      vite.on('exit', (code) => (clearTimeout(t), reject(new Error(`vite exited (${code}): ${said.slice(-400)}`))));
    });
    await page.goto(`http://127.0.0.1:${port}/?token=${encodeURIComponent(TOKEN)}`);
    await page.waitForSelector('.conn[data-status="open"]', { timeout: 20_000 }).catch(() => {});
    check('dev server: the socket goes through the proxy, and ?token= signs the page in and leaves the address',
      (await page.locator('.conn[data-status="open"]').count()) === 1 && !page.url().includes('token='), page.url());
    await openStripTab(page, 'right', 'Work');
    await page.waitForSelector('.plugin-root .work', { timeout: 15_000 }).catch(() => {});
    check('dev server: a plugin tab loads through the proxy', (await page.locator('.plugin-root .work').count()) === 1);
    await shot(page, 'ui-dev-proxy');
    // vite's own client messages are not the app's
    for (let i = errors.length - 1; i >= 0; i--) if (/\[vite\]|favicon/.test(errors[i])) errors.splice(i, 1);
  } finally {
    vite.kill();
  }
});
