// What the projects panel and the projects dialog share (web/src/lib/projects.js): the counts a project row shows, the
// filter and order, and the remove flow. The module imports the app's state, so it is loaded against stubs (the two
// specifiers are rewritten to stand-ins that record what they were asked); the three views of the dialog on the module
// are checked by web/scripts/check-projects.mjs in a headless browser.
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { test } from 'node:test';
import assert from 'node:assert/strict';

const web = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');

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
  const src = await fs.readFile(path.join(web, 'src/lib/projects.js'), 'utf8');
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

test('a project counts its own chats, not an archived one and not a subagent\'s', () => {
  assert.equal(counts.get('p1'), 2);
  assert.equal(counts.get('p2'), 1);
  assert.equal(counts.size, 2, 'a chat without a project counts for nothing');
  assert.ok(!counts.has('none'));
});

// the filter and the order
const fixtures = [
  { id: 'old', name: 'Alpha', path: 'C:/src/alpha', updatedAt: '2026-01-01T00:00:00Z' },
  { id: 'used', name: 'Beta', path: 'C:/src/beta', updatedAt: '2026-01-02T00:00:00Z', lastUsedAt: '2026-03-01T00:00:00Z' },
  { id: 'newer', name: 'Gamma', path: 'C:/src/gamma', updatedAt: '2026-02-01T00:00:00Z' },
];
const ids = (q) => projects.matchingProjects(fixtures, q).map((p) => p.id).join(',');

test('an empty filter lists everything, most recently used first', () => {
  assert.equal(ids(''), 'used,newer,old');
});

test('the name and the path match, whatever their case; nothing matches an unknown filter', () => {
  assert.equal(ids('BETA'), 'used');
  assert.equal(ids('src/gamma'), 'newer');
  assert.equal(ids('zzz'), '');
});

test('listing does not reorder the state it was given', () => {
  projects.matchingProjects(fixtures, '');
  assert.deepEqual(fixtures.map((p) => p.id), ['old', 'used', 'newer']);
});

// the remove flow, shared by the panel and the dialog
const remove = (id, name, p) => projects.removeProject({ id, name, path: p });

test('a declined confirm removes nothing, and the confirm says what is and is not touched', async () => {
  ui.setAnswer(false);
  ui.toasts.length = 0;
  app.deleted.length = 0;
  ui.asked.length = 0;
  const declined = await remove('p1', 'Alpha', 'C:/src/alpha');
  assert.equal(declined, false);
  assert.equal(app.deleted.length, 0);
  assert.equal(ui.toasts.length, 0);
  assert.match(ui.asked[0].message, /Files in C:\/src\/alpha are not touched; its sessions are kept/);
  assert.equal(ui.asked[0].danger, true);
});

test('an accepted confirm removes the project and says so', async () => {
  ui.setAnswer(true);
  ui.toasts.length = 0;
  app.deleted.length = 0;
  const removed = await remove('p1', 'Alpha', 'C:/src/alpha');
  assert.equal(removed, true);
  assert.equal(app.deleted[0], 'p1');
  assert.match(ui.toasts.at(-1).text, /Alpha/);
});

test('a delete that fails reports the host\'s message', async () => {
  ui.setAnswer(true);
  ui.toasts.length = 0;
  const rejected = await remove('boom', 'Beta', 'C:/src/beta');
  assert.equal(rejected, false);
  assert.equal(ui.toasts.at(-1).level, 'error');
  assert.equal(ui.toasts.at(-1).text, 'the host said no');
});
