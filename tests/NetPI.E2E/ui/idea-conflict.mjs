// UI test: the Ideas tab saves with the revision the editor was opened on, and an open editor survives a status change.
// The editor under test is the card's section editor: the idea's own fields are edited in the host's idea dialog (Ctrl+I, the
// card's pencil), which web/mock/e2e.mjs drives against the mock server, conflict included.
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
  await card.locator('.main').click();
  await card.locator('.sec-acts button[title="Edit section"]').click();
  const summary = card.locator('.sed textarea');
  await summary.fill('my own section text');
  check('the editor opened', (await card.locator('.sed').count()) === 1);

  // The agent rewrites the idea while the editor is open: the list refetches, the card carries the new revision.
  await page.evaluate(() => window.__agentEdit('idea-ed01', 'the agent rewrote this'));
  await page.waitForFunction(() => window.__idea('idea-ed01').revision === 6, null, { timeout: 5_000 });
  await page.waitForTimeout(300);

  await card.locator('.sed button', { hasText: 'Save section' }).click();
  await page.waitForSelector('.conflict', { timeout: 5_000 });
  const sent = await page.evaluate(() => window.__updates);
  const first = sent[0] ?? {};
  check('the save carries the revision the editor was opened on', first.expectedRevision === 5, JSON.stringify(first.expectedRevision ?? null));
  const toasts = await page.evaluate(() => window.__toasts);
  check('the refused save says the idea changed somewhere else',
    toasts.some((t) => /changed somewhere else/i.test(t.message)), toasts.map((t) => t.message).join(' | '));
  const line = await page.locator('.conflict').innerText();
  check('the tab names the idea it would not overwrite', /idea-ed01/.test(line), line.replace(/\s+/g, ' ').slice(0, 90));
  check('what was typed is still in the editor', (await summary.inputValue()) === 'my own section text');
  const after = await page.evaluate(() => window.__idea('idea-ed01'));
  check("the agent's change survived the refused save", after.summary === 'the agent rewrote this' && after.revision === 6,
    `${after.summary} @${after.revision}`);

  // A fresh editor, opened on what is there now, saves normally (and the notice goes away).
  await card.locator('.sed button', { hasText: 'Cancel' }).click();
  await card.locator('.sec-acts button[title="Edit section"]').click();
  await card.locator('.sed textarea').fill('my own section text, applied twice');
  await card.locator('.sed button', { hasText: 'Save section' }).click();
  await page.waitForTimeout(500);
  const second = sent.length > 1 ? sent[1] : (await page.evaluate(() => window.__updates))[1];
  check('a save from a fresh editor carries the current revision', second?.expectedRevision === 6, JSON.stringify(second?.expectedRevision ?? null));
  check('the editor closed after the save that committed', (await card.locator('.sed').count()) === 0);
  check('the conflict notice is gone', (await page.locator('.conflict').count()) === 0);

  // The card is moved to another status group by an action taken while an editor is open: the card itself must move,
  // not be destroyed and made again, so the editor and its text stay.
  await card.locator('.sec-acts button[title="Edit section"]').click();
  await card.locator('.sed textarea').fill('typed, then moved');
  await card.locator('.status').click();
  await page.locator('.np-menu .np-menu-item', { hasText: 'in-progress' }).click();
  await page.waitForTimeout(500);
  const moved = page.locator('.ideas .card', { hasText: 'Editor under test' });
  check('the idea moved to the group its new status belongs to',
    /in-progress/.test(await moved.locator('.status').innerText()), await moved.locator('.status').innerText());
  check('the open editor came along with the card', (await moved.locator('.sed textarea').inputValue()) === 'typed, then moved',
    await moved.locator('.sed textarea').inputValue().catch(() => '(no editor)'));
  const sentAfterMove = await page.evaluate(() => window.__updates);
  check('the one-line status change did not claim a revision', sentAfterMove[2]?.expectedRevision === undefined,
    JSON.stringify(sentAfterMove[2] ?? null));
} catch (e) {
  check('the fixture ran', false, String(e?.message ?? e));
} finally {
  await browser?.close();
  server.close();
}

const failed = checks.filter((c) => !c.ok);
console.log(JSON.stringify({ ok: failed.length === 0 && errors.length === 0, checks, errors }));
process.exit(failed.length === 0 && errors.length === 0 ? 0 : 1);