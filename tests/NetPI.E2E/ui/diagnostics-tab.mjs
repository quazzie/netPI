#!/usr/bin/env node
// The Diagnostics tab against a REAL netpi-server (test "ui.diagnostics-tab", moved from the mock suite's section "plugin
// tab: Diagnostics"). A plugin that fails to load is a folder with a DLL that is not one, written into the server's app
// folder (--app, the test's own copy) and taken out again.
//
//   node tests/NetPI.E2E/ui/diagnostics-tab.mjs --url http://127.0.0.1:7470 --token e2e-token --app <dir> --work <dir> [--out dir]
import fs from 'node:fs';
import path from 'node:path';
import { run, check, shot, openApp, openSession, openStripTab, rpc, send, newDir, APP, STAMP } from './lib.mjs';

const projDir = newDir(`diag-${STAMP}`);
fs.mkdirSync(path.join(projDir, '.netpi', 'skills', 'release-notes'), { recursive: true });
fs.writeFileSync(path.join(projDir, '.netpi', 'skills', 'release-notes', 'SKILL.md'), '---\nname: release-notes\ndescription: Write the release notes.\n---\n\nSteps.\n');
const project = await rpc('projects.create', { name: `diag-${STAMP}`, path: projDir });
const chat = await rpc('sessions.create', { title: `Diag chat ${STAMP}`, projectId: project.id });
await send(chat.id, 'hello [s:echo]');
// the tool-set history: the shell plugin switched off between two model calls takes its tools away, with the cause
await rpc('plugins.setEnabled', { id: 'netpi.tools.shell', enabled: false });
try {
  await send(chat.id, 'again [s:echo]');
} finally {
  await rpc('plugins.setEnabled', { id: 'netpi.tools.shell', enabled: true });
}
const broken = path.join(APP, 'plugins', `E2eBroken${STAMP}`);
fs.mkdirSync(broken, { recursive: true });
fs.writeFileSync(path.join(broken, `E2eBroken${STAMP}.dll`), 'not an assembly');
await rpc('plugins.rescan');

await run('diagnostics-tab', async ({ page, socket }) => {
  try {
    await openApp(page);
    await openSession(page, `Diag chat ${STAMP}`);
    await openStripTab(page, 'right', 'Diagnostics');
    await page.locator('.plugin-root .diag').waitFor({ timeout: 10_000 });
    const view = (v) => page.locator(`.diag .views button[data-value="${v.toLowerCase()}"]`).click();
    await view('Plugins');
    await page.locator('.diag .pl').first().waitFor({ timeout: 5_000 });
    check('diagnostics: failed plugin listed first with its error', /failed/.test(await page.locator('.diag .pl').first().innerText()) && (await page.locator('.diag .pl .error').count()) > 0,
      (await page.locator('.diag .pl').first().innerText()).replace(/\s+/g, ' ').slice(0, 160));
    await shot(page, 'ui-diag-01-plugins');
    const row = page.locator('.diag .pl', { hasText: 'netpi.retry' }).first();
    const loads = async () => (await rpc('plugins.list')).find((p) => p.id === 'netpi.retry')?.loadCount;
    const before = await loads();
    await row.hover();
    await row.locator('button[title^="Reload"]').click();
    for (let i = 0; i < 50 && (await loads()) === before; i++) await page.waitForTimeout(100);
    check('diagnostics: reload plugin', (await loads()) === before + 1, `${before} → ${await loads()}`);
    await view('RPC');
    await view('Events');
    await page.waitForFunction(() => document.querySelectorAll('.diag .ev').length > 5, null, { timeout: 10_000 }).catch(() => {});
    await page.locator('.diag .ev').first().click();
    await page.locator('.diag .detail').waitFor({ timeout: 5_000 }).catch(() => {});
    check('diagnostics: live events + payload', (await page.locator('.diag .ev').count()) > 5 && (await page.locator('.diag .detail').count()) > 0);
    await view('Logs');
    await page.waitForFunction(() => document.querySelectorAll('.diag .log').length > 3, null, { timeout: 5_000 }).catch(() => {});
    check('diagnostics: logs', (await page.locator('.diag .log').count()) > 3);
    await view('Context');
    await page.locator('.diag .prompt').waitFor({ timeout: 10_000 }).catch(() => {});
    check('diagnostics: context preview', (await page.locator('.diag .prompt').count()) > 0);
    await page.locator('.diag .sname').first().waitFor({ timeout: 5_000 }).catch(() => {});
    check("diagnostics: the session's skills", (await page.locator('.diag .sname').allInnerTexts()).includes('release-notes'), (await page.locator('.diag .sname').allInnerTexts()).join(' '));
    await page.locator('.diag .np-section-title', { hasText: 'Tool changes' }).locator('button').click();
    await page.locator('.diag .change').first().waitFor({ timeout: 5_000 }).catch(() => {});
    const changes = await page.locator('.diag .change').allInnerTexts();
    check('diagnostics: tool changes name the cause and the plugin', changes.some((t) => t.includes('settings') && t.includes('netpi.tools.shell') && t.includes('bash')), changes.join(' | '));
    await shot(page, 'ui-diag-02-context');

    // The Calls view polls diag.calls every 2 s: with a slow answer the next poll must not stack on top of the one in
    // flight. Each answer is held 2.5 s on its way to the page, longer than the poll interval.
    const slow = socket.delay((f) => f.t === 'res' && f.m === 'diag.calls', 2500);
    try {
      let mark = socket.mark();
      await view('Calls');
      await page.locator('.diag .call').first().waitFor({ timeout: 10_000 });
      check('diagnostics: the Calls view lists the model calls', (await page.locator('.diag .call').count()) >= 2, `${await page.locator('.diag .call').count()} calls`);
      await page.waitForTimeout(9500); // several poll cycles with the slow answer in flight
      const served = socket.sent('diag.calls', mark).filter((c) => socket.answer(c)?.deliveredAt).length;
      check('diagnostics: a slow diag.calls never stacks a second request on top', served >= 2 && socket.maxInFlight('diag.calls', mark) === 1,
        `${served} polls, peak in flight ${socket.maxInFlight('diag.calls', mark)}`);
      // leaving the view while a diag.calls is pending: its answer must not start another poll
      await view('Plugins');
      await page.waitForTimeout(3000); // whatever was pending lands
      mark = socket.mark();
      await view('Calls');
      await page.waitForFunction(() => true);
      for (let i = 0; i < 40 && socket.inFlight('diag.calls', mark) < 1; i++) await page.waitForTimeout(50);
      check('diagnostics: the Calls view has a diag.calls in flight', socket.inFlight('diag.calls', mark) === 1);
      await view('Plugins'); // destroys the Calls view with the request still pending
      const left = socket.mark();
      await page.waitForTimeout(9000); // the pending answer lands, then two poll intervals and a slow answer would have passed
      check('diagnostics: a destroyed Calls view starts no further diag.calls', socket.sent('diag.calls', left).length === 0 && socket.inFlight('diag.calls', mark) === 0,
        `${socket.sent('diag.calls', left).length} calls after leaving, ${socket.inFlight('diag.calls', mark)} in flight`);
    } finally {
      slow();
    }
  } finally {
    fs.rmSync(broken, { recursive: true, force: true });
    await rpc('plugins.rescan').catch(() => {});
    await rpc('sessions.delete', { id: chat.id }).catch(() => {});
    await rpc('projects.delete', { id: project.id }).catch(() => {});
  }
}, { viewport: { width: 1600, height: 1000 }, freshTabs: true, tap: true });
