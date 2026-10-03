#!/usr/bin/env node
// What the projects panel and the projects dialog share (web/src/lib/projects.js), and the three views of the
// dialog that use it (the split idea-g26991 asked for). The module was written out twice before, which is how a
// count and a dialog could drift apart.
//
//   node web/scripts/check-projects.mjs
//
//   1 the counts a project row shows: its own chats, not an archived one, not a subagent's
//   2 the filter and the order, and that listing never reorders the state it was given
//   3 the remove flow: declined, accepted, and a delete the host refused
//   4 the three views render against stubbed state: rows and counts, the filter, the two forms, and what a failing
//     rpc leaves on screen
//
// 1-3 need only Node: the module imports the app's state, so it is loaded here against stubs (the two specifiers
// are rewritten to stand-ins that record what they were asked). 4 is a headless browser, like the other UI checks.
import fs from 'node:fs/promises';
import http from 'node:http';
import os from 'node:os';
import path from 'node:path';
import { pathToFileURL } from 'node:url';
import { compile, compileModule } from 'svelte/compiler';
import { loadPlaywright, launchBrowser } from '../mock/pw.mjs';

const root = path.resolve(path.dirname(new URL(import.meta.url).pathname).replace(/^\//, ''), '../..');

const APP_STUB = `export const app = { projects: [], sessions: [] };
export const deleted = [];
export async function deleteProject(id) {
  deleted.push(id);
  if (String(id).startsWith('boom')) throw new Error('the host said no');
}`;

const UI_STUB = `export const toasts = [];
export const asked = [];
export const answers = { value: true };
export function setAnswer(v) { answers.value = v; }
export function toast(text, level = 'info') { toasts.push({ text, level }); }
export function confirmDialog(opts) { asked.push(opts); return Promise.resolve(answers.value); }`;

/** The module, with its two state imports pointed at the stubs, in a directory it can be imported from. */
async function loadProjectsModule() {
  const src = await fs.readFile(path.join(root, 'web/src/lib/projects.js'), 'utf8');
  const stubbed = src.replace("'./state/app.svelte.js'", "'./app.stub.js'").replace("'./state/ui.svelte.js'", "'./ui.stub.js'");
  if (stubbed.includes('./state/')) throw new Error('projects.js imports state the check cannot stub; the copy is stale');
  const dir = await fs.mkdtemp(path.join(os.tmpdir(), 'netpi-projects-'));
  await fs.writeFile(path.join(dir, 'projects.js'), stubbed);
  await fs.writeFile(path.join(dir, 'app.stub.js'), APP_STUB);
  await fs.writeFile(path.join(dir, 'ui.stub.js'), UI_STUB);
  const url = (file) => pathToFileURL(path.join(dir, file)).href;
  return { projects: await import(url('projects.js')), app: await import(url('app.stub.js')), ui: await import(url('ui.stub.js')) };
}

const { projects, app, ui } = await loadProjectsModule();
const results = [];
const check = (what, ok, detail = '') => results.push({ what, ok, detail });

// the counts a project row shows
const session = (id, projectId, extra = {}) => ({ id, projectId, ...extra });
const counts = projects.projectCounts([
  session('a', 'p1'),
  session('b', 'p1'),
  session('c', 'p1', { archived: true }),
  session('d', 'p1', { parentSessionId: 'b' }),
  session('e', 'p2'),
  session('f', null),
]);
check('a project counts its own chats', counts.get('p1') === 2, `${counts.get('p1')}`);
check('an archived chat and a subagent are not counted', counts.get('p2') === 1 && !counts.has('none'), [...counts].join(' '));
check('a chat without a project counts for nothing', counts.size === 2, `${counts.size} projects`);

// the filter and the order
const fixtures = [
  { id: 'old', name: 'Alpha', path: 'C:/src/alpha', updatedAt: '2026-01-01T00:00:00Z' },
  { id: 'used', name: 'Beta', path: 'C:/src/beta', updatedAt: '2026-01-02T00:00:00Z', lastUsedAt: '2026-03-01T00:00:00Z' },
  { id: 'newer', name: 'Gamma', path: 'C:/src/gamma', updatedAt: '2026-02-01T00:00:00Z' },
];
const ids = (q) => projects.matchingProjects(fixtures, q).map((p) => p.id).join(',');
check('an empty filter lists everything, most recently used first', ids('') === 'used,newer,old', ids(''));
check('the name matches, whatever its case', ids('BETA') === 'used', ids('BETA'));
check('the path matches too', ids('src/gamma') === 'newer', ids('src/gamma'));
check('nothing matches an unknown filter', ids('zzz') === '', ids('zzz'));
check('listing does not reorder the state it was given', fixtures.map((p) => p.id).join(',') === 'old,used,newer', fixtures.map((p) => p.id).join(','));

// the remove flow, shared by the panel and the dialog
ui.setAnswer(false);
ui.toasts.length = 0;
app.deleted.length = 0;
const declined = await projects.removeProject({ id: 'p1', name: 'Alpha', path: 'C:/src/alpha' });
check('a declined confirm removes nothing', declined === false && app.deleted.length === 0 && ui.toasts.length === 0, `deleted ${app.deleted.length}`);
check('the confirm says what is and is not touched', /Files in C:\/src\/alpha are not touched; its sessions are kept/.test(ui.asked[0]?.message ?? '') && ui.asked[0]?.danger === true, JSON.stringify(ui.asked[0]));
ui.setAnswer(true);
const removed = await projects.removeProject({ id: 'p1', name: 'Alpha', path: 'C:/src/alpha' });
check('an accepted confirm removes the project and says so', removed === true && app.deleted[0] === 'p1' && ui.toasts[0]?.text.includes('Alpha'), JSON.stringify(ui.toasts));
const rejected = await projects.removeProject({ id: 'boom', name: 'Beta', path: 'C:/src/beta' });
check("a delete that fails reports the host's message", rejected === false && ui.toasts.at(-1)?.level === 'error' && ui.toasts.at(-1)?.text === 'the host said no', JSON.stringify(ui.toasts.at(-1)));

// --- the three views of the projects dialog, which share that module (idea-g26991) -------------------------
// The views are rendered in a headless browser against stubbed state and rpc: what is checked here is the wiring
// the split introduced — the filter, the counts, the form fields, and what a failing rpc leaves on screen.
const VIEWS = {
  'ProjectsListView.svelte': {
    '../../lib/state/app.svelte.js': '/stub/app.js',
    '../../lib/kit/Icon.svelte': '/stub/Icon.svelte',
    '../../lib/kit/IconButton.svelte': '/stub/IconButton.svelte',
  },
  'ProjectsNewView.svelte': { '../../lib/kit/Icon.svelte': '/stub/Icon.svelte' },
  'ProjectsEditView.svelte': {
    '../../lib/state/app.svelte.js': '/stub/app.js',
    '../../lib/rpc.svelte.js': '/stub/rpc.js',
    '../../lib/kit/Icon.svelte': '/stub/Icon.svelte',
    '../../lib/kit/IconButton.svelte': '/stub/IconButton.svelte',
    '../../lib/kit/TimeAgo.svelte': '/stub/TimeAgo.svelte',
    '../../lib/kit/host.js': '/stub/host.js',
  },
  'projects.js': { './state/app.svelte.js': '/stub/app.js', './state/ui.svelte.js': '/stub/ui.js' },
};

/** The kit components and the app state a view imports, as small stand-ins that record what they were asked. */
const STUBS = {
  'Icon.svelte': `<script>let { name = '' } = $props();</script><span class="icon" data-icon={name}></span>`,
  'IconButton.svelte': `<script>let { icon = '', title = '', onclick, disabled = false } = $props();</script><button {title} {disabled} onclick={onclick}><i class="icon" data-icon={icon}></i></button>`,
  'TimeAgo.svelte': `<script>let { time } = $props();</script><span class="time">{time}</span>`,
  'app.js': `export const app = {
  info: { home: 'C:/src' },
  projects: [
    { id: 'p1', name: 'Alpha', path: 'C:/src/alpha', updatedAt: '2026-01-01T00:00:00Z' },
    { id: 'p2', name: 'Beta', path: 'C:/src/beta', updatedAt: '2026-01-02T00:00:00Z', lastUsedAt: '2026-03-01T00:00:00Z' },
  ],
  sessions: [
    { id: 's1', projectId: 'p1', title: 'First', updatedAt: '2026-01-05T00:00:00Z' },
    { id: 's2', projectId: 'p1', title: 'Second', updatedAt: '2026-01-06T00:00:00Z' },
    { id: 's3', projectId: 'p1', title: 'Old', updatedAt: '2026-01-07T00:00:00Z', archived: true },
    { id: 's4', projectId: 'p1', title: 'Sub', updatedAt: '2026-01-08T00:00:00Z', parentSessionId: 's1' },
    { id: 's5', projectId: 'p2', title: 'Beta chat', updatedAt: '2026-01-09T00:00:00Z' },
  ],
};
export async function deleteProject() {}`,
  'rpc.js': `export async function rpc(method) { throw new Error('no host for ' + method); }`,
  'ui.js': `export const toasts = []; export function toast(t, l = 'info') { toasts.push({ t, l }); } export function confirmDialog() { return Promise.resolve(true); }`,
  'host.js': `export const desktop = { available: false, revealPath() {}, openExternal() {} };`,
};

/** svelte's own sources, served as the browser sees them: one runtime, its bare specifiers mapped to files. */
const IMPORTS = {
  'esm-env': '/esm-env.js',
  clsx: '/vendor/clsx.mjs',
  svelte: '/src/index-client.js',
  'svelte/internal/client': '/src/internal/client/index.js',
  'svelte/reactivity': '/src/reactivity/index-client.js',
  'svelte/internal/disclose-version': '/src/internal/disclose-version.js',
  '#client/constants': '/src/internal/client/constants.js',
};

const PAGE = `<!doctype html><meta charset="utf-8"><body><div id="root"></div>
<script type="importmap">${JSON.stringify({ imports: IMPORTS })}</script>
<script type="module">
import { mount } from 'svelte';
const root = document.getElementById('root');
window.show = async (view, props) => {
  const { default: View } = await import('/view/' + view);
  root.replaceChildren();
  const el = document.createElement('div');
  root.append(el);
  mount(View, { target: el, props });
  return el;
};
</script>`;

const server = http.createServer(async (req, res) => {
  const url = new URL(req.url, 'http://x').pathname;
  const send = (type, body) => {
    res.writeHead(200, { 'content-type': type });
    res.end(body);
  };
  /** A source file of the app, compiled the way the build compiles it and with its imports served from here. */
  const compileFile = async (file, name) => {
    const src = await fs.readFile(path.join(root, 'web/src', file), 'utf8');
    const opts = { generate: 'client', filename: name, runes: true };
    const code = (name.endsWith('.svelte') ? compile(src, { ...opts, css: 'injected' }) : compileModule(src, opts)).js.code;
    const stubs = VIEWS[name] ?? {};
    // a relative import is served from here: a stub where there is one, else the lib sibling it names
    // (only inside a `from '…'`, so the injected CSS — full of `.selector { … }` strings — is left alone)
    return code.replace(/(from\s*)(['"])(\.[^'"]*)\2/g, (_, from, q, spec) => `${from}${q}${stubs[spec] ?? `/lib/${path.basename(spec)}`}${q}`);
  };
  try {
    if (url === '/') return send('text/html', PAGE);
    if (url === '/esm-env.js') return send('text/javascript', 'export const BROWSER = true;\nexport const DEV = false;\n');
    if (url.startsWith('/src/')) return send('text/javascript', await fs.readFile(path.join(root, 'node_modules/svelte', url.slice(1)), 'utf8'));
    if (url === '/vendor/clsx.mjs') return send('text/javascript', await fs.readFile(path.join(root, 'node_modules/clsx/dist/clsx.mjs'), 'utf8'));
    if (url.startsWith('/view/')) {
      const name = path.basename(url);
      return send('text/javascript', await compileFile(`components/modals/${name}`, name));
    }
    if (url.startsWith('/lib/')) {
      const rel = url.slice('/lib/'.length);
      return send('text/javascript', await compileFile(`lib/${rel}`, path.basename(rel)));
    }
    if (url.startsWith('/stub/')) {
      const name = path.basename(url);
      if (name.endsWith('.svelte')) return send('text/javascript', compile(STUBS[name], { generate: 'client', css: 'injected', filename: name, runes: true }).js.code);
      return send('text/javascript', STUBS[name]);
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
await page.waitForFunction(() => typeof window.show === 'function', null, { timeout: 5000 }).catch(() => {
  console.error('the page did not start:', pageErrors.join(' | ') || '(no error)');
  process.exit(2);
});

// the list: a row per project with the sessions that count, most recently used first, and the shared filter
const list = await page.evaluateHandle(() => window.show('ProjectsListView.svelte', { onnew: () => {}, onedit: () => {}, onremove: () => {}, onstart: () => {} }));
await page.waitForTimeout(50);
const rows = await list.evaluate((el) => [...el.querySelectorAll('.prow')].map((r) => r.querySelector('.pname').textContent.replace(/\s+/g, ' ').trim()));
check('the list has a row per project, most recently used first', rows.length === 2 && /Beta/.test(rows[0]) && /Alpha/.test(rows[1]), JSON.stringify(rows));
check("a row counts the project's own chats only", /Alpha\s*2 sessions/.test(rows[1]), JSON.stringify(rows[1]));
await list.evaluate((el) => {
  const input = el.querySelector('.search input');
  input.value = 'alph';
  input.dispatchEvent(new Event('input', { bubbles: true }));
});
await page.waitForTimeout(30);
const filtered = await list.evaluate((el) => [...el.querySelectorAll('.prow .pname')].map((n) => n.textContent));
check('the filter narrows the list', filtered.length === 1 && /Alpha/.test(filtered[0]), JSON.stringify(filtered));

// the create form: a folder, a name, the hint the caller asked for, and the folder the browse picked
const created = await page.evaluateHandle(() => window.show('ProjectsNewView.svelte', { form: { name: '', path: '', create: false }, hint: 'A new session starts in it.', onsubmit: () => {}, onbrowse: () => {} }));
await page.waitForTimeout(30);
const form = await created.evaluate((el) => ({
  hint: el.querySelector('.hint')?.textContent,
  path: el.querySelector('.pathrow input')?.placeholder,
  name: el.querySelectorAll('.field')[1]?.querySelector('input')?.placeholder,
}));
check('the create form asks for a folder and a name, with the caller\'s hint', form.hint === 'A new session starts in it.' && !!form.path && !!form.name, JSON.stringify(form));

// the project view: its fields, its chats, and what a failing rpc leaves on screen instead of a spinner
const shown = await page.evaluateHandle(async () => {
  const { app } = await import('/stub/app.js');
  const el = await window.show('ProjectsEditView.svelte', {
    project: app.projects[0],
    form: { name: 'Alpha', path: 'C:/src/alpha', create: false },
    onsubmit: () => {},
    onbrowse: () => {},
    onprofile: () => {},
    onstart: () => {},
    onopen: () => {},
  });
  await new Promise((r) => setTimeout(r, 50));
  return el;
});
const edit = await shown.evaluate((el) => ({
  name: el.querySelector('input.np-input')?.value,
  sessions: [...el.querySelectorAll('.sess .sname')].map((n) => n.textContent),
  count: el.querySelector('.np-section-count')?.textContent,
  files: [...el.querySelectorAll('.hint')].map((n) => n.textContent),
  skills: el.querySelectorAll('.skname').length,
}));
check('the project view opens on the name input, with the project\'s own values', edit.name === 'Alpha', JSON.stringify(edit.name));
check('it lists the project\'s chats and how many', edit.sessions.length === 2 && edit.count === '2', JSON.stringify(edit.sessions));
check('an instruction file list that cannot be read says so', edit.files.some((t) => /Unavailable: no host for agentsmd\.list/.test(t)), JSON.stringify(edit.files));
check('no skills section without the skills plugin', edit.skills === 0, `${edit.skills} skills`);
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
