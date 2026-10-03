// UI test: the Ideas tab opens the host's idea dialog on the idea's current revision (the dialog's own save, conflict
// included, is driven by web/mock/e2e.mjs against the mock server), a one-line status change claims no revision, and the
// play button starts a chat on the idea.
//
// `api.update` fell back to the list's current revision, so `expectedRevision` always matched what the server had and
// the conflict check could never fire: a save from an editor that had been open while the agent rewrote the idea
// silently overwrote it. And because the cards lived in one `{#each}` per status group, an idea that changed status
// was destroyed and made again, so the open editor and everything typed into it vanished (idea-c3hihl).
//
// The tab is mounted here the way PluginTabHost mounts it: the committed bundle with a stub ctx whose `ideas.update`
// enforces the same `expectedRevision` rule as the server, so the stub records what the tab sent and refuses a stale
// save. The agent's edit is the fixture bumping the revision and publishing `ideas.changed`.
//
//   node tests/NetPI.E2E/ui/idea-conflict.mjs
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
// A side panel, wide enough that the status menu is not clipped by it: the cards are opened in this test, and a menu
// positioned inside a panel is clipped by that panel.
const html = `<!doctype html><html><head>${host.head}<style>
  body {margin:0;background:#191919;color:#ddd;font-family:system-ui}
  .fixture-panel { position:fixed; inset:0 0 0 auto; width:520px; height:900px; overflow:auto; border-left:1px solid #333; z-index:9999; background:#191919 }
</style></head><body><div id="app" hidden></div><div class="fixture-panel"><div id="fixture"></div></div><script type="module">
import {mount} from '/ui.js';
const now = () => new Date().toISOString();
const ideas = [
  {id:'idea-ed01', title:'Editor under test', summary:'the first summary', status:'open', priority:'high', tags:['ui'],
   revision:5, createdBy:'user', createdAt:now(), updatedAt:now(), ord:1, sections:[{id:'sec-ed01',kind:'note',title:'Findings',content:'a note'}],
   images:[], commits:[], sessions:[]},
  {id:'idea-ot02', title:'Another idea', summary:'elsewhere', status:'planned', priority:'medium', tags:[],
   revision:2, createdBy:'agent:1', createdAt:now(), updatedAt:now(), ord:2, sections:[], images:[], commits:[], sessions:[]},
];
const copy = (v) => JSON.parse(JSON.stringify(v));
window.__updates = [];   // every ideas.update the tab sent
window.__toasts = [];    // every toast the tab raised
window.__opened = [];    // every openIdea the tab asked the host for
window.__started = [];   // every startChat
window.__opened = [];    // every openIdea the tab asked the host for
window.__started = [];   // every startChat
window.__events = {};    // what the tab subscribed to, so the fixture can publish ideas.changed
window.__idea = (id) => ideas.find((i) => i.id === id);
// The agent writes the idea in another window: a new revision, then the event every window refetches on.
window.__agentEdit = (id, summary) => {
  const idea = window.__idea(id);
  idea.summary = summary;
  idea.revision += 1;
  idea.updatedAt = now();
  for (const fn of window.__events['ideas.changed'] ?? []) fn({ reason: 'updated' });
};
const ctx = {
  on: (name, fn) => { (window.__events[name] ??= []).push(fn); return () => {}; },
  rpc: async (method, params) => {
    if (method === 'ideas.list') return {exists:true, storage:{database:'netpi.db', scope:'netpi.ideas', schemaVersion:2}, ideas:copy(ideas)};
    if (method === 'projects.list') return [];
    if (method === 'ideas.suggestions') return {suggestions:[]};
    if (method === 'ideas.update') {
      window.__updates.push(copy(params));
      const idea = window.__idea(params.id);
      if (!idea) throw new Error('not_found: no such idea');
      // The server's own rule (IdeasRepository.Update), in the shape the tab reads.
      if (params.expectedRevision != null && params.expectedRevision !== idea.revision)
        throw new Error(\`conflict: Idea \${idea.id} changed since you read it (it is at revision \${idea.revision}, your copy is \${params.expectedRevision}). Reload it and apply your change again.\`);
      const { updateSections, ...fields } = params.patch;
      Object.assign(idea, fields);
      for (const u of updateSections ?? []) Object.assign(idea.sections.find((x) => x.id === u.id) ?? {}, u);
      idea.revision += 1;
      idea.updatedAt = now();
      return copy(idea);
    }
    return null;
  },
  app: {
    get activeSessionId() { return 'ses_1'; },
    get activeProject() { return null; },
    onChange: () => () => {},
    insertText: () => {},
    openIdea: (opts) => window.__opened.push(copy(opts)),
    startChat: (opts) => window.__started.push(copy(opts)),
    toast: (message, kind) => window.__toasts.push({message, kind: kind ?? 'info'}),
    openSession: () => {},
  },
};
mount(document.getElementById('fixture'), ctx);
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
  const page = await browser.newPage({ viewport: { width: 700, height: 1000 } });
  page.on('pageerror', (e) => errors.push(String(e)));
  page.on('console', (m) => {
    if (m.type() === 'error') errors.push(`[console] ${m.text()}`);
  });

  await page.goto(`http://127.0.0.1:${server.address().port}`);
  await page.waitForSelector('.ideas .card', { timeout: 15_000 });
  const card = page.locator('.ideas .card', { hasText: 'Editor under test' });

  // A card is a title and a short summary: the whole idea is in the host's idea dialog, which the card opens on the
  // idea as the list has it right now (the dialog captures that revision and saves against it).
  check('a card shows its summary', /the first summary/.test(await card.innerText()));
  await card.locator('.main').click();
  const opened = await page.evaluate(() => window.__opened);
  check('a click opens the idea dialog on the idea', opened.length === 1 && opened[0].idea?.id === 'idea-ed01' && !opened[0].view,
    JSON.stringify(opened[0] ?? null));
  check('the dialog is opened on the revision the list had', opened[0]?.idea?.revision === 5, String(opened[0]?.idea?.revision));

  // The agent rewrites the idea: the list refetches, and the next open carries the new revision, not the old one.
  await page.evaluate(() => window.__agentEdit('idea-ed01', 'the agent rewrote this'));
  await page.waitForFunction(() => window.__idea('idea-ed01').revision === 6, null, { timeout: 5_000 });
  await card.getByText('the agent rewrote this').waitFor({ timeout: 5_000 });
  await card.locator('.main').click();
  const reopened = await page.evaluate(() => window.__opened);
  check('a second open carries the new revision', reopened[1]?.idea?.revision === 6 && reopened[1]?.idea?.summary === 'the agent rewrote this',
    JSON.stringify(reopened[1]?.idea?.revision ?? null));

  // The play button starts a chat on the idea at once, in the idea's project (none here).
  await card.locator('button[title="Start a new chat on this idea"]').click();
  const started = await page.evaluate(() => window.__started);
  check('the play button starts a chat on the idea', started.length === 1 && /idea-ed01/.test(started[0].text) && started[0].title === 'Editor under test' && started[0].projectId === null,
    JSON.stringify(started[0] ?? null));

  // A one-line status change claims no revision (it applies to the idea as it is) and the card moves to its new group.
  await card.locator('button[title="Status and actions"]').click();
  await page.locator('.np-menu .np-menu-item', { hasText: 'Status: in-progress' }).click();
  await page.waitForTimeout(500);
  const sentNow = await page.evaluate(() => window.__updates);
  check('the one-line status change did not claim a revision', sentNow[0]?.expectedRevision === undefined && sentNow[0]?.patch?.status === 'in-progress',
    JSON.stringify(sentNow[0] ?? null));
  check('the idea moved to the group its new status belongs to',
    (await page.locator('.ghead[data-tone] .gname', { hasText: 'in-progress' }).count()) === 1);
} catch (e) {
  check('the fixture ran', false, String(e?.message ?? e));
} finally {
  await browser?.close();
  server.close();
}

const failed = checks.filter((c) => !c.ok);
console.log(JSON.stringify({ ok: failed.length === 0 && errors.length === 0, checks, errors }));
process.exit(failed.length === 0 && errors.length === 0 ? 0 : 1);