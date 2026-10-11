#!/usr/bin/env node
// The Files tab against a REAL netpi-server (test "ui.files-tab", moved from the mock suite's section "plugin tab: Files").
// files.open would hand a file to the desktop's program: the test answers those calls itself and reads what was asked.
//
//   node tests/NetPI.E2E/ui/files-tab.mjs --url http://127.0.0.1:7470 --token e2e-token --work <dir> [--out dir]
import fs from 'node:fs';
import path from 'node:path';
import { execFileSync } from 'node:child_process';
import { run, check, shot, openApp, openSession, openStripTab, rpc, send, newDir, STAMP } from './lib.mjs';

// a git project two commits ahead of its upstream, with four changes: two edits, a deletion and a new file
const git = (cwd, ...args) => execFileSync('git', args, { cwd, stdio: 'pipe', encoding: 'utf8' });
const origin = newDir(`origin-${STAMP}.git`);
git(origin, 'init', '--bare', '-q', '-b', 'main');
const main = newDir(`files-${STAMP}`);
const write = (rel, text) => {
  fs.mkdirSync(path.dirname(path.join(main, rel)), { recursive: true });
  fs.writeFileSync(path.join(main, rel), text);
};
git(main, 'init', '-q', '-b', 'main');
git(main, 'config', 'user.name', 'e2e');
git(main, 'config', 'user.email', 'e2e@example.invalid');
git(main, 'config', 'core.autocrlf', 'false');
write('AGENTS.md', '# Files test\n');
write('README.md', '# Readme\n');
write('OLD-NOTES.md', Array.from({ length: 40 }, (_, i) => `old note ${i}`).join('\n') + '\n');
write('web/src/lib/markdown.js', 'export const md = 1;\n');
write('web/src/lib/icons.js', 'export const icons = {};\n');
write('web/src/lib/chatItems.js', 'export const items = [];\n');
git(main, 'add', '-A');
git(main, 'commit', '-q', '-m', 'first');
git(main, 'remote', 'add', 'origin', origin);
git(main, 'push', '-q', '-u', 'origin', 'main');
for (const n of [2, 3]) {
  write(`web/src/lib/step${n}.js`, `export const step = ${n};\n`);
  git(main, 'add', '-A');
  git(main, 'commit', '-q', '-m', `step ${n}`);
}
write('web/src/lib/markdown.js', Array.from({ length: 20 }, (_, i) => `export const line${i} = ${i};`).join('\n') + '\n');
write('web/src/lib/icons.js', 'export const icons = { a: 1 };\n');
fs.rmSync(path.join(main, 'OLD-NOTES.md'));
write('NEW.md', '# New\n');
// a second workspace to switch to: no AGENTS.md, no README
const ALT = newDir(`files-alt-${STAMP}`);
fs.writeFileSync(path.join(ALT, 'alt-one.txt'), 'one\n');
fs.writeFileSync(path.join(ALT, 'alt-two.txt'), 'two\n');

const project = await rpc('projects.create', { name: `files-${STAMP}`, path: main });
const alt = await rpc('projects.create', { name: `files-alt-${STAMP}`, path: ALT });
const chat = await rpc('sessions.create', { title: `Files chat ${STAMP}`, projectId: project.id });
await send(chat.id, 'hello [s:echo]');

const norm = (p) => String(p ?? '').toLowerCase().replaceAll('\\', '/').replace(/\/$/, '');

await run('files-tab', async ({ page, socket }) => {
  // files.open hands the file to the operating system: answered here, never sent
  const noOpen = socket.intercept((f) => f.t === 'rpc' && f.m === 'files.open', (f) => ({ t: 'res', id: f.id, r: true }));
  try {
    await openApp(page);
    await openSession(page, `Files chat ${STAMP}`);
    const ta = page.locator('.composer textarea');
    await openStripTab(page, 'left', 'Files');
    await page.locator('.plugin-root .files .frow').first().waitFor({ timeout: 10_000 });
    const dir = async (name) => {
      await page.locator('.files .frow .fname', { hasText: new RegExp(`^${name}$`) }).last().click();
      await page.locator('.files .frow .fname').first().waitFor();
      await page.waitForTimeout(250);
    };
    await dir('web');
    await dir('src');
    await dir('lib');
    const mdName = page.locator('.files .frow .fname', { hasText: /^markdown\.js$/ });
    await mdName.waitFor({ timeout: 5_000 });
    const opens = () => socket.sent('files.open');
    const before = opens().length;
    // one click only selects, like the file manager: opening a file runs its program, so it takes a double click
    await mdName.click();
    await page.waitForTimeout(300);
    check('files: one click does not open the file', opens().length === before, JSON.stringify(opens().at(-1)?.msg?.p));
    check('files: one click selects the row', (await page.locator('.files .frow[aria-selected="true"]').count()) === 1);
    await mdName.dblclick();
    await page.waitForFunction((n) => true, null);
    for (let i = 0; i < 40 && opens().length === before; i++) await page.waitForTimeout(50);
    const asked = opens().at(-1)?.msg?.p;
    check('files: a double click opens the file (files.open)', norm(asked?.path).endsWith('web/src/lib/markdown.js'), asked?.path);
    // files.open cannot see who is calling; user: true is how a deliberate pick asks for the file's default program
    check('files: the double click says the user picked it (user: true)', asked?.user === true, JSON.stringify(asked));
    check('files: a click inserts nothing', (await ta.inputValue()) === '', await ta.inputValue());
    const mdRow = page.locator('.files .frow', { has: page.locator('.fname', { hasText: /^markdown\.js$/ }) });
    await mdRow.hover();
    await mdRow.locator('.act[title^="Insert @"]').click();
    check('files: @ inserts a mention', (await ta.inputValue()) === '@web/src/lib/markdown.js ', await ta.inputValue());
    await ta.fill('');
    const gitLine = page.locator('.files .foot.git');
    await gitLine.waitFor({ timeout: 10_000 }).catch(() => {});
    const gitText = (await gitLine.count()) ? await gitLine.innerText() : '';
    const ahead = (await gitLine.locator('.ab').count()) ? (await gitLine.locator('.ab').innerText()).trim() : '';
    check('files: the git line (files.git)', /^main[\s\S]*4 files[\s\S]*\+\d+[\s\S]*−\d+/.test(gitText) && ahead === '2', `${gitText.replace(/\s+/g, ' ')} (ahead ${ahead})`);
    await gitLine.click();
    await page.locator('.files .frow.change').first().waitFor({ timeout: 5_000 }).catch(() => {});
    check('files: the git line lists the changed files', (await page.locator('.files .frow.change').count()) === 4, `${await page.locator('.files .frow.change').count()} rows`);
    check('files: a deleted file is struck through', (await page.locator('.files .frow.change.gone', { hasText: 'OLD-NOTES.md' }).count()) === 1);
    await shot(page, 'ui-files-01-changes');
    await gitLine.click();
    await page.locator('.files .frow .fname', { hasText: /^icons\.js$/ }).click({ button: 'right' });
    await page.locator('.np-menu').waitFor({ timeout: 5_000 }).catch(() => {});
    check('files: context menu', (await page.locator('.np-menu .np-menu-item', { hasText: 'Copy relative path' }).count()) > 0);
    await page.keyboard.press('Escape');
    await page.locator('.files .np-search input').fill('chatitems');
    await page.locator('.files .frow', { hasText: 'chatItems.js' }).first().waitFor({ timeout: 5_000 }).catch(() => {});
    check('files: search (files.search)', (await page.locator('.files .frow', { hasText: 'chatItems.js' }).count()) > 0);
    await page.locator('.files .np-search input').fill('');

    // one files.scope per workspace, not per message: a run appends messages (a session.updated each) while the tab shows
    let mark = socket.mark();
    await ta.fill('hi [s:echo]');
    await ta.press('Enter');
    await page.locator('.composer.running').waitFor({ timeout: 5_000 }).catch(() => {});
    await page.waitForFunction(() => !document.querySelector('.composer.running'), null, { timeout: 30_000 }).catch(() => {});
    await page.waitForTimeout(400);
    check('files: a run that appends messages costs no files.scope', socket.sent('files.scope', mark).length === 0, `${socket.sent('files.scope', mark).length} files.scope call(s) during the run`);

    // A workspace switch mid-fetch: the old workspace's late answer must not apply, and an open search must rerun.
    // (1) the old workspace's listing is in flight when the tab switches; its answer reaches the page 2.5 s later
    let late = null;
    const lateOld = socket.delay((f) => {
      if (f.t !== 'res' || f.m !== 'files.list') return false;
      late ??= f.id;
      return f.id === late;
    }, 2500);
    try {
      mark = socket.mark();
      await page.locator('.files .head button[title="Refresh"]').click();
      await socket.waitSent('files.list', mark);
      const oldCall = socket.sent('files.list', mark)[0];
      await page.waitForTimeout(300); // inside the 2500 ms window
      const switched = socket.mark();
      await rpc('sessions.setProject', { id: chat.id, projectId: alt.id });
      for (let i = 0; i < 80 && !socket.answer(oldCall)?.deliveredAt; i++) await page.waitForTimeout(100);
      await page.waitForTimeout(400); // the UI has had the late answer in hand
      const newCall = socket.sent('files.list', switched).find((c) => c.id !== oldCall.id && socket.answer(c)?.deliveredAt);
      const rootShown = norm(await page.locator('.files .head .rpath').innerText());
      check('files: the late old-workspace listing is not applied',
        rootShown === norm(ALT) && (await page.locator('.files .frow .fname', { hasText: 'alt-one.txt' }).count()) > 0 && (await page.locator('.files .frow .fname', { hasText: /^AGENTS\.md$/ }).count()) === 0,
        `root now: ${rootShown || '∅'}`);
      check("files: the old workspace's late answer really arrived, after the new one's",
        !!newCall && socket.answer(oldCall)?.deliveredAt > socket.answer(newCall).deliveredAt,
        `old delivered ${socket.answer(oldCall)?.deliveredAt}, new ${newCall ? socket.answer(newCall).deliveredAt : 'none'}`);
      check('files: the project change re-keyed the tab through files.scope (the workspace identity)', socket.sent('files.scope', switched).length > 0,
        socket.frames.slice(switched).filter((f) => f.dir === 'out' && f.m?.startsWith('files.')).map((f) => f.m).join(', ') || 'no files.* call');
    } finally {
      lateOld();
    }
    // (2) the search the user left open: a workspace switch reruns it for the new workspace, not drops it
    await page.locator('.files .np-search input').fill('readme');
    await page.waitForTimeout(700); // debounce + the search in this workspace: no hits here
    mark = socket.mark();
    await rpc('sessions.setProject', { id: chat.id, projectId: project.id });
    await page.waitForFunction(() => [...document.querySelectorAll('.files .frow.flat .fname')].some((e) => e.textContent === 'README.md'), null, { timeout: 5_000 }).catch(() => {});
    check('files: a workspace switch reruns the open search for the new workspace',
      (await page.locator('.files .frow.flat .fname', { hasText: /^README\.md$/ }).count()) > 0 && socket.sent('files.search', mark).length > 0,
      `searches after the switch: ${socket.sent('files.search', mark).length}`);
    await page.locator('.files .np-search input').fill('');
    await openStripTab(page, 'left', 'Sessions');
  } finally {
    noOpen();
    await rpc('sessions.delete', { id: chat.id }).catch(() => {});
    await rpc('projects.delete', { id: project.id }).catch(() => {});
    await rpc('projects.delete', { id: alt.id }).catch(() => {});
  }
}, { viewport: { width: 1600, height: 1000 }, freshTabs: true, tap: true });
