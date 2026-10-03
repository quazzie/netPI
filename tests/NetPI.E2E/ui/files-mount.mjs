// UI test: the Files tab's mount lifecycle — does the teardown onMount returns actually run?
//
// FilesTab.svelte mounted itself with `onMount(async () => …)`. Svelte registers a cleanup only when the callback
// returns a function, and an async callback returns a Promise, so the teardown was unreachable: every remount left
// its `window` focus listener behind, and each one ran a real files.git RPC on the next focus while keeping the dead
// component graph alive (idea-1zs9go). Remounts are constant — every plugin load bumps UiVersion and PluginTabHost
// re-mounts when the signature changes.
//
// The tab is mounted here the way PluginTabHost mounts it: the committed bundle (so this also tests what ships) with a
// stub ctx, then unmounted through the handle the plugin returns. The focus event is dispatched on window, which is
// what a real focus does.
//
//   node tests/NetPI.E2E/ui/files-mount.mjs
// Prints one JSON line at the end: { ok, checks: [{name, ok, detail}], errors: [...] }.
import http from 'node:http';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { chromium } from 'playwright-core';
import { hostUi } from './host.mjs';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../../..');
const bundle = fs.readFileSync(path.join(root, 'plugins/NetPI.Tools.Files/wwwroot/ui.js'));
const host = hostUi(root);
// A side panel with the same overflow the real one has, so the tab lays out as it does in the app.
const html = `<!doctype html><html><head>${host.head}<style>
  body {margin:0;background:#191919;color:#ddd;font-family:system-ui}
  .fixture-panel { position:fixed; inset:0 0 0 auto; width:340px; height:400px; overflow:auto; border-left:1px solid #333; z-index:9999; background:#191919 }
</style></head><body><div id="app" hidden></div><div class="fixture-panel"><div id="fixture"></div></div><script type="module">
import {mount} from '/ui.js';
// Every method the tab calls is recorded, so the test can count files.git calls over the socket-free stub.
window.__calls = [];
const entry = {rel:'notes.txt', name:'notes.txt', isDir:false, ignored:false, path:'C:/p/notes.txt'};
const ctx = {
  on: () => () => {},
  rpc: async (method) => {
    window.__calls.push(method);
    if (method === 'files.scope') return {root:'C:/p', identity:'ws-1', version:1, branch:'main'};
    if (method === 'files.list') return {root:'C:/p', entries:[entry]};
    if (method === 'files.git') return {repo:'C:/p', branch:'main', files:[], added:0, deleted:0};
    return null;
  },
  app: {
    get activeSessionId() { return 'ses_1'; },
    get activeProject() { return {path:'C:/p', name:'p'}; },
    onChange: () => () => {},
    insertText: () => {},
    toast: () => {},
  },
};
let handle = mount(document.getElementById('fixture'), ctx);
// PluginTabHost.cleanup(): unmount the instance, then mount a fresh one on an empty container (a remount).
window.__unmount = () => handle.unmount();
window.__remount = () => {
  handle.unmount();
  const el = document.createElement('div');
  document.getElementById('fixture').replaceChildren(el);
  handle = mount(el, ctx);
};
window.__gitCalls = () => window.__calls.filter((m) => m === 'files.git').length;
</script></body></html>`;
const server = http.createServer((req, res) => {
  if (host.serve(req, res)) return;
  res.setHeader('Content-Type', req.url === '/ui.js' ? 'text/javascript' : 'text/html');
  res.end(req.url === '/ui.js' ? bundle : html);
});
await new Promise((resolve) => server.listen(0, '127.0.0.1', resolve));

const checks = [];
const check = (name, ok, detail = '') => {
  checks.push({ name, ok: !!ok, detail: String(detail) });
  console.log(`  ${ok ? 'ok  ' : 'FAIL'} ${name}${detail ? ` (${detail})` : ''}`);
};
const errors = [];
let browser;
try {
  try {
    browser = await chromium.launch({ args: ['--disable-features=msWindowTabManagerPublic'] });
  } catch {
    browser = await chromium.launch({ channel: 'msedge', args: ['--disable-features=msWindowTabManagerPublic'] });
  }
  const page = await browser.newPage();
  page.on('pageerror', (e) => errors.push(String(e)));
  page.on('console', (m) => {
    if (m.type() === 'error') errors.push(`[console] ${m.text()}`);
  });

  const git = () => page.evaluate(() => window.__gitCalls());
  // The focus handler debounces by 300ms before it calls files.git; give it room plus a slow call.
  const focus = async () => {
    await page.evaluate(() => window.dispatchEvent(new Event('focus')));
    await page.waitForTimeout(900);
  };

  await page.goto(`http://127.0.0.1:${server.address().port}`);
  await page.waitForFunction(() => window.__calls?.includes('files.list'), null, { timeout: 15_000 });
  const text = await page.locator('#fixture').innerText();
  check('the tab mounted and listed the workspace', text.includes('notes.txt'), text.replace(/\s+/g, ' ').slice(0, 80));

  const afterMount = await git();
  check('the first load asked for git', afterMount >= 1, `${afterMount} files.git`);
  await focus();
  const afterFocus = await git();
  check('a live tab reloads git when the window gets the focus back', afterFocus > afterMount, `${afterMount} -> ${afterFocus}`);

  // PluginTabHost's cleanup on a reload or on the tab going away.
  await page.evaluate(() => window.__unmount());
  await focus();
  const afterUnmount = await git();
  check('no files.git after the tab was unmounted', afterUnmount === afterFocus, `${afterFocus} -> ${afterUnmount}`);

  // Three reloads in a row: every dead instance used to leave a listener behind, so this is where N became N+1.
  for (let i = 0; i < 3; i++) await page.evaluate(() => window.__remount());
  await page.waitForTimeout(400);
  const afterRemounts = await git();
  await page.evaluate(() => window.__unmount());
  await focus();
  const afterAll = await git();
  check('three remounts leave no listener behind (no files.git after the last unmount)', afterAll === afterRemounts, `${afterRemounts} -> ${afterAll}`);
} catch (e) {
  check('the fixture ran', false, String(e?.message ?? e));
} finally {
  await browser?.close();
  server.close();
}

const failed = checks.filter((c) => !c.ok);
console.log(JSON.stringify({ ok: failed.length === 0 && errors.length === 0, checks, errors }));
process.exit(failed.length === 0 && errors.length === 0 ? 0 : 1);