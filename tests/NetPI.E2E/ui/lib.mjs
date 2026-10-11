// What the browser tests against a REAL netpi-server + MockLlm share (the scripts in this folder, started by
// UiTests.RunUiScript): the arguments, checks and screenshots, the browser and its error listeners, the server's RPC over
// HTTP, a tap on the page's WebSocket, MockLlm's gates, and the one JSON line at the end the C# side reads:
// { ok, checks: [{ name, ok, detail }], errors: [...] }.
//
//   import { run, arg, check, shot } from './lib.mjs';
//   await run('pin', async ({ page }) => { … check('…', ok); await shot(page, 'ui-pin-01'); … });
//
// Arguments every script gets from the wrapper: --url (the server), --token, --mock (MockLlm), --app (the server's app
// folder, a copy the test may change), --work (a folder for the project directories a script makes), --out (the
// screenshot folder); a script reads its own with arg('name').
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { execSync } from 'node:child_process';
import { fileURLToPath, pathToFileURL } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const argv = process.argv.slice(2);

/** The value after `--name` on the command line, else `fallback`. */
export const arg = (name, fallback) => {
  const i = argv.indexOf(`--${name}`);
  return i >= 0 && i + 1 < argv.length ? argv[i + 1] : fallback;
};
export const BASE = arg('url', 'http://127.0.0.1:7470');
export const TOKEN = arg('token', 'e2e-token');
export const MOCK = arg('mock', '');
export const APP = arg('app', '');
export const OUT = path.resolve(arg('out', path.join(here, '..', 'screenshots')));
export const WORK = path.resolve(arg('work', path.join(os.tmpdir(), 'netpi-ui')));
/** Unique per run: a shared server keeps projects, sessions and settings between the tests of a shard and between -Repeat runs. */
export const STAMP = Math.random().toString(36).slice(2, 8);

/** A new folder under WORK (made with its parents). */
export function newDir(name) {
  const dir = path.join(WORK, name);
  fs.mkdirSync(dir, { recursive: true });
  return dir;
}

export const checks = [];
/** Records one check and prints it; every failed one is reported at the end, so a run shows all its problems at once. */
export function check(name, ok, detail = '') {
  checks.push({ name, ok: !!ok, detail: String(detail) });
  console.log(`  ${ok ? 'ok  ' : 'FAIL'} ${name}${detail ? ` (${detail})` : ''}`);
}

export async function shot(page, name) {
  fs.mkdirSync(OUT, { recursive: true });
  await page.screenshot({ path: path.join(OUT, `${name}.png`) });
  console.log(`  screenshot ${name}.png`);
}

/** One call to the server's RPC over HTTP, as the UI's fallback makes it: the result, or an Error with the server's message
 * (and its code and HTTP status). */
export async function rpc(method, params = {}) {
  const res = await fetch(`${BASE}/api/rpc/${encodeURIComponent(method)}`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', 'X-NetPI-Token': TOKEN },
    body: JSON.stringify(params),
  });
  const text = await res.text();
  let body = null;
  try { body = text ? JSON.parse(text) : null; } catch { body = text; }
  if (!res.ok) throw Object.assign(new Error(`${method}: HTTP ${res.status} ${body?.error?.message ?? text}`), { code: body?.error?.code ?? null, status: res.status });
  return body;
}

/** Polls `probe` until it returns something truthy (and returns it), or throws after `timeout` ms naming `what`. */
export const until = async (what, probe, timeout, every = 100) => {
  const end = Date.now() + timeout;
  for (;;) {
    const v = await probe();
    if (v) return v;
    if (Date.now() > end) throw new Error(`timed out after ${timeout} ms waiting for ${what}`);
    await new Promise((r) => setTimeout(r, every));
  }
};

const IDLE = new Set(['idle', 'completed', 'failed', 'cancelled']);
/** Waits until the session's agent has finished at least `minRuns` runs and is not running (agent.get, polled). */
export const waitIdle = (sessionId, minRuns = 1, timeout = 60_000) =>
  until(`the agent of ${sessionId} to finish run ${minRuns}`, async () => {
    const a = await rpc('agent.get', { sessionId });
    return a && a.runs >= minRuns && IDLE.has(a.status) ? a : null;
  }, timeout);

/** Sends `text` to a session (agent.send) and waits for that run to end. Returns the agent. */
export async function send(sessionId, text, timeout = 60_000) {
  const before = (await rpc('agent.get', { sessionId }))?.runs ?? 0;
  await rpc('agent.send', { sessionId, text });
  return waitIdle(sessionId, before + 1, timeout);
}

/** MockLlm's gates (gate=<name> in a scenario tag holds a model call until it is released; see docs/TESTING.md). */
export const gates = {
  async release(name) {
    const res = await fetch(`${MOCK}/_release?gate=${encodeURIComponent(name)}`, { method: 'POST' });
    if (!res.ok) throw new Error(`release ${name}: HTTP ${res.status}`);
    return res.json();
  },
  /** Waits until a call is held at the gate: the run is in flight and stays there. */
  waiting: (name, timeout = 15_000) =>
    until(`a model call at gate ${name}`, async () => {
      const list = await (await fetch(`${MOCK}/_gates`)).json();
      return list.some((g) => g.name === name && g.waiting > 0);
    }, timeout),
};

/** Opens the app with the token and waits for it to be up. */
export async function openApp(page) {
  await page.goto(`${BASE}/?token=${encodeURIComponent(TOKEN)}`);
  await page.waitForSelector('.panel.left', { timeout: 15_000 });
}

/** Selects a side panel's tab (left: Sessions, Projects, …; right: Work, Ideas, …) and opens the panel, without closing it
 * when that tab is already the one showing (a click on the active tab folds the panel). */
export async function openStripTab(page, side, name) {
  const t = page.locator(`.panel.${side} .strip-tab`, { hasText: name });
  // Shortly after a load the app restores the panel's last tab from the server, which can undo a click made before it:
  // the tab has to still be the one showing a moment later.
  for (let i = 0; i < 4; i++) {
    if ((await t.getAttribute('aria-selected')) !== 'true') await t.click();
    if (!(await page.locator(`.panel.${side}.open`).count())) await t.click();
    await page.waitForTimeout(250);
    if ((await t.getAttribute('aria-selected')) === 'true' && (await page.locator(`.panel.${side}.open`).count())) return;
  }
}

/** Opens a session from the sessions panel by (a part of) its title. A row that is not shown (a folded group, a long list)
 * is found through the session search, which shows matches inside folded groups; the search is cleared again. */
export async function openSession(page, title) {
  await openStripTab(page, 'left', 'Sessions');
  const row = page.locator('.panel.left .srow', { hasText: title }).first();
  await row.waitFor({ state: 'attached', timeout: 15_000 }).catch(() => {});
  const search = page.locator('.panel.left').getByPlaceholder('Search sessions');
  const searched = !(await row.isVisible());
  if (searched) await search.fill(title);
  await row.waitFor({ timeout: 15_000 });
  await row.click();
  if (searched) await search.fill('');
  await page.locator('.composer textarea').waitFor({ timeout: 10_000 });
}

// Wait until the chat shows `marker` (what the next check reads), not for a fixed pause: a pause is too long on an idle
// machine and too short on a loaded one. A marker that never comes leaves the check after it to fail with the text it saw.
export const chatShows = (page, marker, ms = 10_000) =>
  page.waitForFunction((m) => (document.querySelector('.scroller > .content')?.innerText ?? '').includes(m), marker, { timeout: ms }).catch(() => {});
// The same for the right panel's open tab: its own text, once it is more than the "Loading…" placeholder (and holds `includes`).
export const paneShows = (page, includes = '', ms = 10_000) =>
  page.waitForFunction((want) => {
    const t = (document.querySelector('.panel.right > .body .pane:not([hidden])')?.innerText ?? '').trim();
    return t.length > 20 && t.includes(want);
  }, includes, { timeout: ms }).catch(() => {});

/**
 * Routes the page's WebSocket (/ws) through the test. Every frame is recorded both ways; an answer from the server can be
 * held back and let go later while the frames after it pass; and the connection can be dropped and refused until
 * restore(), which is the reconnect path without restarting the server (context.setOffline does not close a socket that
 * is already open). Install it before the page opens the app.
 * A frame: { n, dir: 'out' | 'in', t: 'rpc' | 'res' | 'ev' | …, id, m (the method of a call, and of the call an answer
 * answers), type (event), sid, msg (parsed), at (when the tap saw it), deliveredAt (an answer or event: when the page got
 * it) }. Holding the answers to one method: socket.hold((f) => f.t === 'res' && f.m === 'x').
 */
export async function tapSocket(page) {
  const frames = [];
  const holds = [];
  const delays = [];
  const intercepts = [];
  const live = new Set();
  const methods = new Map(); // call id → method, so an answer frame names the call it answers
  let down = false;
  const record = (dir, data) => {
    let msg = null;
    try { msg = JSON.parse(String(data)); } catch {}
    const f = { n: frames.length, dir, t: msg?.t, id: msg?.id, m: msg?.m, type: msg?.type, sid: msg?.sid, msg, at: Date.now(), deliveredAt: null };
    if (f.t === 'rpc') methods.set(f.id, f.m);
    else if (f.t === 'res') f.m = methods.get(f.id);
    frames.push(f);
    return f;
  };
  await page.routeWebSocket(/\/ws(\?|$)/, async (ws) => {
    // Closed before the handler returns, the page socket never fires "open": a refused connection, as the UI sees one.
    if (down) { await ws.close({ code: 4000, reason: 'dropped by the test' }); return; }
    const server = ws.connectToServer();
    const pair = { ws, server };
    live.add(pair);
    const deliver = (f, data) => {
      if (!live.has(pair)) return;
      f.deliveredAt = Date.now();
      ws.send(data);
    };
    ws.onMessage((data) => {
      const f = record('out', data);
      // an intercepted call never reaches the server: the test answers it (or leaves it unanswered)
      const icp = intercepts.find((i) => i.match(f));
      if (!icp) return server.send(data);
      f.intercepted = true;
      const answer = icp.answer?.(f);
      if (answer) deliver(record('in', JSON.stringify(answer)), JSON.stringify(answer));
    });
    server.onMessage((data) => {
      const f = record('in', data);
      const hold = holds.find((h) => h.match(f));
      const delay = delays.find((d) => d.match(f));
      if (hold) hold.queue.push(() => deliver(f, data));
      else if (delay) setTimeout(() => deliver(f, data), delay.ms);
      else deliver(f, data);
    });
    ws.onClose((code, reason) => { live.delete(pair); server.close({ code, reason }).catch(() => {}); });
    server.onClose((code, reason) => { live.delete(pair); ws.close({ code, reason }).catch(() => {}); });
  });
  const since = (n) => frames.slice(n);
  return {
    frames,
    /** A position in the frame list, for `since`. */
    mark: () => frames.length,
    /** The rpc calls of `method` the page sent (after a mark). */
    sent: (method, n = 0) => since(n).filter((f) => f.dir === 'out' && f.t === 'rpc' && f.m === method),
    /** The events of `type` the page received (after a mark). */
    events: (type, n = 0) => since(n).filter((f) => f.dir === 'in' && f.t === 'ev' && f.type === type),
    /** The server's answer to a call frame, once it came. */
    answer: (call) => frames.find((f) => f.dir === 'in' && f.t === 'res' && f.id === call.id) ?? null,
    waitSent: (method, n = 0, timeout = 10_000) =>
      until(`the page to call ${method}`, () => frames.slice(n).find((f) => f.dir === 'out' && f.t === 'rpc' && f.m === method), timeout, 25),
    /** Holds back every server frame `match` accepts until release(); the frames it does not accept pass meanwhile. */
    hold(match) {
      const h = { match, queue: [] };
      holds.push(h);
      return {
        get held() { return h.queue.length; },
        release() {
          const i = holds.indexOf(h);
          if (i >= 0) holds.splice(i, 1);
          for (const deliver of h.queue.splice(0)) deliver();
        },
      };
    },
    /** Delays every server frame `match` accepts by `ms` (each on its own; the others pass). Returns a function that ends it. */
    delay(match, ms) {
      const d = { match, ms };
      delays.push(d);
      return () => { const i = delays.indexOf(d); if (i >= 0) delays.splice(i, 1); };
    },
    /** Keeps every page frame `match` accepts from the server; `answer(frame)` returns the frame the page gets instead
     * (e.g. { t: 'res', id: f.id, r: true }), or nothing for no answer. Returns a function that ends it. */
    intercept(match, answer) {
      const i = { match, answer };
      intercepts.push(i);
      return () => { const k = intercepts.indexOf(i); if (k >= 0) intercepts.splice(k, 1); };
    },
    /** Calls of `method` (after a mark) whose answer has not reached the page yet. */
    inFlight: (method, n = 0) => since(n).filter((f) => f.dir === 'out' && f.t === 'rpc' && f.m === method
      && !frames.some((a) => a.dir === 'in' && a.t === 'res' && a.id === f.id && a.deliveredAt != null)).length,
    /** The most calls of `method` (after a mark) that were ever in flight at once, from the page's side. */
    maxInFlight(method, n = 0) {
      const marks = [];
      for (const c of since(n).filter((f) => f.dir === 'out' && f.t === 'rpc' && f.m === method)) {
        marks.push([c.at, 1]);
        const a = frames.find((x) => x.dir === 'in' && x.t === 'res' && x.id === c.id && x.deliveredAt != null);
        if (a) marks.push([a.deliveredAt, -1]);
      }
      marks.sort((x, y) => x[0] - y[0] || x[1] - y[1]);
      let now = 0;
      let max = 0;
      for (const [, d] of marks) max = Math.max(max, (now += d));
      return max;
    },
    /** Closes the page's connection and refuses new ones until restore(); with http (the default) the UI's HTTP fallback fails too. */
    async drop({ http = true } = {}) {
      down = true;
      if (http) await page.route('**/api/rpc/**', (r) => r.abort('internetdisconnected'));
      for (const { ws, server } of [...live]) {
        live.clear();
        await ws.close({ code: 4000, reason: 'dropped by the test' }).catch(() => {});
        await server.close().catch(() => {});
      }
    },
    async restore() {
      down = false;
      await page.unroute('**/api/rpc/**').catch(() => {});
    },
  };
}

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
  throw new Error('Playwright not found (npm ci, or npm i -g playwright)');
}

/** Playwright's own Chromium, else an installed Edge/Chrome when that build is not downloaded (Edge ships with Windows). */
async function launchBrowser(pw) {
  const args = ['--disable-features=msWindowTabManagerPublic'];
  try {
    return await pw.chromium.launch({ args });
  } catch (e) {
    if (!String(e?.message).includes("Executable doesn't exist")) throw e;
    for (const channel of process.platform === 'win32' ? ['msedge', 'chrome'] : ['chrome', 'msedge']) {
      try {
        const b = await pw.chromium.launch({ channel, args });
        console.log(`  browser: ${channel} (Playwright's Chromium build is not installed)`);
        return b;
      } catch {}
    }
    throw e;
  }
}

/**
 * Runs a test body in a fresh browser and prints the result line, then exits (1 when a check failed). After the body it
 * adds "no console/page errors"; an exception in it is the failed check "<name> script ran to the end".
 *   viewport  the page size (default 1200×900)
 *   http      also count failed requests (except the socket) and HTTP answers ≥ 400 as errors
 *   freshTabs start without the chat tabs earlier tests on this server left open (ui.state netpi.openTabs)
 *   tap       install tapSocket before anything opens; the body gets it as `socket`
 *   keepsSettings  the settings document must be the same after the body as before it (the check "the settings are as the
 *             test found them"); when it is not, the one from before is put back, so the next test on the shard starts clean
 * The body gets { page, context, browser, errors, socket }.
 */
export async function run(name, body, { viewport = { width: 1200, height: 900 }, http = false, freshTabs = false, tap = false, keepsSettings = false } = {}) {
  fs.mkdirSync(OUT, { recursive: true });
  const settingsBefore = keepsSettings ? (await rpc('settings.get')).settings : null;
  const pw = await loadPlaywright();
  const browser = await launchBrowser(pw);
  const errors = [];
  try {
    const context = await browser.newContext({ viewport, colorScheme: 'dark' });
    const page = await context.newPage();
    page.on('console', (m) => { if (m.type() === 'error') errors.push(`[console] ${m.text()}`); });
    page.on('pageerror', (e) => errors.push(`[pageerror] ${e.message}`));
    if (http) {
      page.on('requestfailed', (r) => { if (!r.url().includes('/ws')) errors.push(`[requestfailed] ${r.url()} ${r.failure()?.errorText}`); });
      page.on('response', (r) => { if (r.status() >= 400) errors.push(`[http ${r.status()}] ${r.url()}`); });
    }
    const socket = tap ? await tapSocket(page) : null;
    // A fresh browser restores the chat tabs earlier tests on this server left open: on a shared shard that was a dozen
    // chats to load, and closing them one by one took up to 15 s.
    if (freshTabs) await rpc('ui.state.set', { key: 'netpi.openTabs', value: null });
    await body({ page, context, browser, errors, socket });
    check('no console/page errors', errors.length === 0, errors.slice(0, 5).join(' | '));
    await context.close();
  } catch (e) {
    errors.push(`[exception] ${e?.stack ?? e}`);
    check(`${name} script ran to the end`, false, e?.message ?? e);
  } finally {
    await browser.close();
  }
  if (settingsBefore) {
    const changed = diff(settingsBefore, (await rpc('settings.get')).settings);
    check('the settings are as the test found them', changed.length === 0, changed.slice(0, 8).join(', '));
    if (changed.length) await rpc('settings.replace', { settings: settingsBefore });
  }
  report(errors);
}

/** The dotted paths where two JSON values differ. An empty object is no value: settings.set with null removes a key and
 * leaves its parent behind, so a setting made and removed again is no change. */
function diff(a, b, at = '') {
  const obj = (v) => v !== null && typeof v === 'object' && !Array.isArray(v);
  const none = (v) => v === undefined || (obj(v) && Object.values(v).every(none));
  if (none(a) && none(b)) return [];
  if (obj(a) && obj(b)) {
    const keys = [...new Set([...Object.keys(a), ...Object.keys(b)])];
    return keys.flatMap((k) => diff(a[k], b[k], at ? `${at}.${k}` : k));
  }
  return JSON.stringify(a) === JSON.stringify(b) ? [] : [at || '(the document)'];
}

/** Prints the result line the C# side reads and exits: 0 when there were checks and every one passed. */
export function report(errors = []) {
  const ok = checks.length > 0 && checks.every((c) => c.ok);
  console.log(JSON.stringify({ ok, checks, errors }));
  process.exit(ok ? 0 : 1);
}
