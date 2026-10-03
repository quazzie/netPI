// UI test: the Ideas tab's ≈ toggle (idea-61wg9p). With embeddings on the server (ideas.capabilities.embeddings) the
// search box can rank ideas by meaning (ideas.similar): the ideas the server returns, best first, within the same
// filters; without embeddings the toggle is not there; switching it off goes back to "every word must match".
//
// The tab is mounted the way PluginTabHost mounts it: the committed bundle with a stub ctx whose ideas.similar answers
// a fixed ranking for any text that mentions "clock".
//
//   node tests/NetPI.E2E/ui/idea-meaning.mjs
// Prints one JSON line at the end: { ok, checks: [{name, ok, detail}], errors: [...] }.
import http from 'node:http';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { chromium } from 'playwright-core';
import { hostUi } from './host.mjs';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../../..');
const bundle = fs.readFileSync(path.join(root, 'plugins/NetPI.Ideas/wwwroot/ui.js'));
const host = hostUi(root);
const page = (embeddings) => `<!doctype html><html><head>${host.head}<style>
  body {margin:0;background:#191919;color:#ddd;font-family:system-ui}
  .fixture-panel { position:fixed; inset:0 0 0 auto; width:300px; height:900px; overflow:auto; border-left:1px solid #333; z-index:9999; background:#191919 }
</style></head><body><div id="app" hidden></div><div class="fixture-panel"><div id="fixture"></div></div><script type="module">
import {mount} from '/ui.js';
const now = () => new Date().toISOString();
const idea = (id, title, summary, ord) => ({id, title, summary, status:'open', priority:'medium', tags:[], revision:1, createdBy:'user',
  createdAt:now(), updatedAt:now(), ord, sections:[], images:[], commits:[], sessions:[]});
const ideas = [
  idea('idea-aaa001', 'Radio buffer top-up', 'Songs per batch should refill when raised.', 1),
  idea('idea-bbb002', 'Collapsing a process row leaks a timer', 'The Work tab keeps a timer per collapsed row.', 2),
  idea('idea-ccc003', 'Session tab groups', 'Pinned and Today open by default.', 3),
];
window.__similar = [];
const ctx = {
  on: () => () => {},
  rpc: async (method, params) => {
    if (method === 'ideas.list') return {exists:true, storage:{database:'netpi.db', scope:'netpi.ideas', schemaVersion:2}, ideas:JSON.parse(JSON.stringify(ideas))};
    if (method === 'projects.list') return [];
    if (method === 'ideas.suggestions') return {suggestions:[]};
    if (method === 'ideas.capabilities') return {decisions:true, history:true, models:true, embeddings:${embeddings}};
    if (method === 'ideas.similar') {
      window.__similar.push(params);
      return /clock/.test(params.text)
        ? {available:true, ideas:[{id:'idea-bbb002', title:'', status:'open', score:0.81}, {id:'idea-ccc003', title:'', status:'open', score:0.52}]}
        : {available:true, ideas:[]};
    }
    return null;
  },
  app: {
    get activeSessionId() { return 'ses_1'; },
    get activeProject() { return null; },
    onChange: () => () => {},
    insertText: () => {},
    toast: () => {},
    openSession: () => {},
    openIdea: () => {},
  },
};
mount(document.getElementById('fixture'), ctx);
</script></body></html>`;
const server = http.createServer((req, res) => {
  if (host.serve(req, res)) return;
  res.setHeader('Content-Type', req.url === '/ui.js' ? 'text/javascript' : 'text/html');
  res.end(req.url === '/ui.js' ? bundle : page(!req.url.includes('off')));
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
  const tab = await browser.newPage({ viewport: { width: 700, height: 1000 } });
  tab.on('pageerror', (e) => errors.push(String(e)));
  tab.on('console', (m) => { if (m.type() === 'error') errors.push(`[console] ${m.text()}`); });
  const titles = async () => (await tab.locator('.ideas .card .main').allInnerTexts()).map((t) => t.trim());

  // Without embeddings: no toggle.
  await tab.goto(`http://127.0.0.1:${server.address().port}/off`);
  await tab.waitForSelector('.ideas .card', { timeout: 15_000 });
  await tab.waitForTimeout(300);
  check('without embeddings the ≈ toggle is not shown', (await tab.locator('.ideas button.meaning').count()) === 0);

  // With embeddings: the toggle, then a query no idea holds every word of.
  await tab.goto(`http://127.0.0.1:${server.address().port}/`);
  await tab.waitForSelector('.ideas .card', { timeout: 15_000 });
  await tab.waitForSelector('.ideas button.meaning', { timeout: 5_000 });
  const toggle = tab.locator('.ideas button.meaning');
  check('with embeddings the ≈ toggle is shown, off', (await toggle.getAttribute('aria-pressed')) === 'false');
  await tab.locator('.ideas .filters input').fill('a dripping clock');
  await tab.waitForTimeout(400);
  check('word search: no idea holds every word', (await tab.locator('.ideas .card').count()) === 0, (await titles()).join(' | '));

  await toggle.click();
  await tab.waitForFunction(() => document.querySelectorAll('.ideas .card').length === 2, null, { timeout: 5_000 });
  const ranked = await titles();
  check('≈: the ideas the server ranked, best first', ranked[0]?.includes('Collapsing a process row') && ranked[1]?.includes('Session tab groups'), ranked.join(' | '));
  const asked = await tab.evaluate(() => window.__similar.at(-1));
  check('≈ asks ideas.similar with the typed text', asked?.text === 'a dripping clock', JSON.stringify(asked));
  check('the placeholder says it searches by meaning', (await tab.locator('.ideas .filters input').getAttribute('placeholder')) === 'Search by meaning');

  await toggle.click();
  await tab.waitForTimeout(400);
  check('off again: back to word search', (await tab.locator('.ideas .card').count()) === 0, (await titles()).join(' | '));
  await tab.locator('.ideas .filters input').fill('');
  await tab.waitForTimeout(300);
  check('an empty search shows every idea', (await tab.locator('.ideas .card').count()) === 3);
} catch (e) {
  check('the fixture ran', false, String(e?.message ?? e));
} finally {
  await browser?.close();
  server.close();
}

const failed = checks.filter((c) => !c.ok);
console.log(JSON.stringify({ ok: failed.length === 0 && errors.length === 0, checks, errors }));
process.exit(failed.length === 0 && errors.length === 0 ? 0 : 1);
