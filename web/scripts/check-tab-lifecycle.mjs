#!/usr/bin/env node
// What every plugin tab's mount and refresh loop is now built on (web/src/lib/kit/tab.js, refresh.svelte.js) — the
// properties the hand-rolled versions did not have, each of which a tab already got wrong once:
//
//   node web/scripts/check-tab-lifecycle.mjs
//
//   1 createTab mounts, forwards onShow/onHide to the component's setVisible, and unmounts
//   2 one load on mount, and events coalesced into one
//   3 a load slower than the poll interval never has a second one in flight (a poll armed by a clock stacks them)
//   4 a hidden tab loads nothing: events only mark it stale, and showing it loads once
//   5 nothing runs after unmount
//
// It serves the two kit modules (compiled the way the build compiles them) and a probe component to a headless browser
// and drives them with a fake ctx. Exit code 0 when all five hold, 1 with the failures listed.
import fs from 'node:fs/promises';
import http from 'node:http';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { compile, compileModule } from 'svelte/compiler';
import { loadPlaywright, launchBrowser } from '../mock/pw.mjs';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const KIT = path.join(root, 'web/src/lib/kit');

const PROBE = `<script>
  import { useRefresh } from '/refresh.js';
  // what createTab hands a tab: ctx and nothing else, so the knobs ride on the ctx
  let { ctx } = $props();
  const tab = useRefresh(ctx, { load: () => ctx.load(), events: ['probe.changed'], pollMs: ctx.pollMs, delayMs: ctx.delayMs });
  export function setVisible(v) { tab.setVisible(v); }
  window.tab = tab;
</script>
<p>probe</p>`;

/** A fake ctx: an event bus and a load that takes `loadMs`, counting calls and how many were ever in flight. */
const CTX = `
window.makeCtx = (loadMs, pollMs, delayMs) => {
  const handlers = new Map();
  const stats = { calls: 0, peak: 0, inFlight: 0 };
  return {
    stats,
    pollMs,
    delayMs,
    on(pattern, h) {
      if (!handlers.has(pattern)) handlers.set(pattern, []);
      handlers.get(pattern).push(h);
      return () => {
        const list = handlers.get(pattern) ?? [];
        const i = list.indexOf(h);
        if (i >= 0) list.splice(i, 1);
      };
    },
    fire(pattern) {
      for (const h of [...(handlers.get(pattern) ?? [])]) h();
    },
    handlers,
    load: async () => {
      stats.calls++;
      stats.inFlight++;
      if (stats.inFlight > stats.peak) stats.peak = stats.inFlight;
      await new Promise((r) => setTimeout(r, loadMs));
      stats.inFlight--;
    },
  };
}`;

/** svelte's own sources, served as the browser sees them: one runtime, its bare specifiers mapped to files. */
const IMPORTS = {
  'esm-env': '/esm-env.js',
  clsx: '/vendor/clsx.mjs',
  svelte: '/src/index-client.js',
  'svelte/internal/client': '/src/internal/client/index.js',
  'svelte/internal/disclose-version': '/src/internal/disclose-version.js',
  '#client/constants': '/src/internal/client/constants.js',
};

const PAGE = `<!doctype html><meta charset="utf-8"><body><div id="root"></div>
<script type="importmap">${JSON.stringify({ imports: IMPORTS })}</script>
<script type="module">
import { mount, unmount } from 'svelte';
import Probe from '/probe.js';
import { createTab } from '/tab.js';
${CTX}
window.openTab = (ctx) => {
  const el = document.createElement('div');
  document.getElementById('root').replaceChildren(el);
  const view = createTab(Probe)(el, ctx);
  window.close = () => {
    view.unmount();
    document.getElementById('root').replaceChildren();
  };
  window.host = view;
  return view;
};
</script>`;

const server = http.createServer(async (req, res) => {
  const url = new URL(req.url, 'http://x').pathname;
  const send = (type, body) => {
    res.writeHead(200, { 'content-type': type });
    res.end(body);
  };
  try {
    if (url === '/') return send('text/html', PAGE);
    if (url === '/esm-env.js') return send('text/javascript', 'export const BROWSER = true;\nexport const DEV = false;\n');
    if (url.startsWith('/src/')) {
      return send('text/javascript', await fs.readFile(path.join(root, 'node_modules/svelte', url.slice(1)), 'utf8'));
    }
    if (url === '/vendor/clsx.mjs') return send('text/javascript', await fs.readFile(path.join(root, 'node_modules/clsx/dist/clsx.mjs'), 'utf8'));
    if (url === '/probe.js') return send('text/javascript', compile(PROBE, { generate: 'client', filename: 'Probe.svelte' }).js.code);
    if (url === '/refresh.js' || url === '/tab.js') {
      const file = url === '/refresh.js' ? 'refresh.svelte.js' : 'tab.js';
      const src = await fs.readFile(path.join(KIT, file), 'utf8');
      return send('text/javascript', compileModule(src, { generate: 'client', filename: file }).js.code);
    }
  } catch (e) {
    res.writeHead(500);
    return res.end(String(e));
  }
  if (url === '/favicon.ico') {
    res.writeHead(204);
    return res.end();
  }
  res.writeHead(404);
  res.end();
});
await new Promise((r) => server.listen(0, '127.0.0.1', r));

const pw = await loadPlaywright();
const browser = await launchBrowser(pw);
const page = await browser.newPage();
const pageErrors = [];
page.on('pageerror', (e) => pageErrors.push(e.message));
page.on('console', (m) => m.type() === 'error' && pageErrors.push(m.text()));
await page.goto(`http://127.0.0.1:${server.address().port}/`);
await page.waitForFunction(() => typeof window.makeCtx === 'function', null, { timeout: 5000 }).catch(() => {
  console.error('the page did not start:', pageErrors.join(' | ') || '(no error)');
  process.exit(2);
});

const results = [];
const check = (what, ok, detail) => results.push({ what, ok, detail: detail ?? '' });

/** A fresh tab on a ctx whose loads take loadMs, polling every pollMs and coalescing events over delayMs. */
const open = (loadMs, pollMs, delayMs) =>
  page.evaluate(([ms, poll, delay]) => {
    window.close?.();
    window.ctx = window.makeCtx(ms, poll, delay);
    window.openTab(window.ctx);
  }, [loadMs, pollMs, delayMs]);

// 1 + 2: one load on mount, and the events that land in one window are one load
await open(10, 0, 30);
await page.waitForTimeout(120);
const calls = await page.evaluate(() => window.ctx.stats.calls);
check('one load on mount', calls === 1, `calls=${calls}`);
await page.evaluate(() => {
  window.ctx.fire('probe.changed');
  window.ctx.fire('probe.changed');
  window.ctx.fire('probe.changed');
});
await page.waitForTimeout(150);
const afterEvents = await page.evaluate(() => window.ctx.stats.calls);
check('three events in one window are one load', afterEvents === calls + 1, `calls ${calls} → ${afterEvents}`);

// 3: a load slower than the poll interval never has a second one in flight
await open(80, 20, 20);
await page.waitForTimeout(400);
const polled = await page.evaluate(() => window.ctx.stats);
check('the poll keeps loading', polled.calls > 2, `calls=${polled.calls}`);
check('a load slower than the poll never has a second in flight', polled.peak === 1, `peak=${polled.peak}`);

// 3b: a request that arrives while a load runs is one more load after it (what the running one read may be what the request
// just made stale), never the answer of the load that started first, and several requests in that window are still one
await open(80, 0, 20);
await page.waitForTimeout(150);
const midLoad = await page.evaluate(async () => {
  const before = window.ctx.stats.calls;
  const first = window.tab.refresh();
  await new Promise((r) => setTimeout(r, 20)); // the load is running now
  const second = window.tab.refresh();
  window.tab.refresh();
  await Promise.all([first, second]);
  return { loads: window.ctx.stats.calls - before, peak: window.ctx.stats.peak, settled: window.ctx.stats.inFlight };
});
check('a request during a load is one more load after it', midLoad.loads === 2, `loads=${midLoad.loads}`);
check('and still never two in flight', midLoad.peak === 1 && midLoad.settled === 0, `peak=${midLoad.peak} inFlight=${midLoad.settled}`);

// 4: a hidden tab loads nothing; showing it loads once, however many events arrived meanwhile
await open(10, 0, 30);
await page.waitForTimeout(120);
const hidden = await page.evaluate(async () => {
  window.host.onHide();
  const before = window.ctx.stats.calls;
  for (let i = 0; i < 5; i++) window.ctx.fire('probe.changed');
  await new Promise((r) => setTimeout(r, 150));
  const whileHidden = window.ctx.stats.calls;
  const visible = window.tab.visible;
  window.host.onShow();
  await new Promise((r) => setTimeout(r, 150));
  return { before, whileHidden, afterShow: window.ctx.stats.calls, visible };
});
check('the helper reports the visibility', hidden.visible === false, `visible=${hidden.visible}`);
check('a hidden tab loads nothing', hidden.whileHidden === hidden.before, `${hidden.before} → ${hidden.whileHidden}`);
check('showing it loads once', hidden.afterShow === hidden.before + 1, `${hidden.before} → ${hidden.afterShow}`);

// 5: nothing runs after unmount
const after = await page.evaluate(async () => {
  window.host.onHide();
  window.close();
  const before = window.ctx.stats.calls;
  window.ctx.fire('probe.changed');
  await new Promise((r) => setTimeout(r, 150));
  return { before, after: window.ctx.stats.calls, alive: window.tab.alive, handlers: window.ctx.handlers.get('probe.changed').length };
});
check('nothing runs after unmount', after.after === after.before, `${after.before} → ${after.after}`);
check('the subscriptions are gone with it', after.handlers === 0, `handlers=${after.handlers}`);
check('a destroyed tab is not alive', after.alive === false, `alive=${after.alive}`);
check('no page errors', pageErrors.length === 0, pageErrors.join('; '));

await browser.close();
server.close();

let failed = 0;
for (const r of results) {
  if (!r.ok) failed++;
  console.log(`${r.ok ? 'ok  ' : 'FAIL'}  ${r.what}${r.detail ? `  (${r.detail})` : ''}`);
}
console.log(failed ? `${failed} of ${results.length} checks failed` : `all ${results.length} checks passed`);
process.exit(failed ? 1 : 0);