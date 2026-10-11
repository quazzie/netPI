#!/usr/bin/env node
// UI test: the page's link to the server, on a real server — a plugin tab that remounts when its bundle changes (and
// says so when it cannot load, until Retry), a run that ends while the socket is down (tapSocket.drop/restore: on
// reconnect the stale partial answer goes and the stored one shows), reconnecting and resubscribing, the composer and
// the Diagnostics tab while the runtime, context and compaction plugins are off (plugins.setEnabled) and back, and the
// notifications the UI asks the desktop app for (a stand-in chrome.webview records them).
//
//   node tests/NetPI.E2E/ui/connection.mjs --url http://127.0.0.1:7470 --token e2e-token --mock http://127.0.0.1:7479 --app <app dir> --work <dir>
// Prints one JSON line at the end: { ok, checks: [{name, ok, detail}], errors: [...] }.
import fs from 'node:fs';
import path from 'node:path';
import { run, check, shot, openApp, openStripTab, openSession, rpc, send, gates, newDir, until, waitIdle, APP, STAMP } from './lib.mjs';

const QWEN = 'aiproxy/qwen3.8-27b';
const CLAUDE = 'anthropic/claude-haiku-4-5';
const PAID = `wn-${STAMP}`;
// what [s:slow] streams, part by part (tests/MockLlm Scenarios.Slow)
const SLOW = `Slow answer:${Array.from({ length: 24 }, (_, i) => ` part ${i + 1}.`).join('')} SLOW-DONE`;

await run('connection', async ({ page, context, socket, errors }) => {
  const dir = newDir(`conn-${STAMP}`);
  fs.writeFileSync(path.join(dir, 'AGENTS.md'), '# Working here\n\nKeep answers short.\n');
  const project = await rpc('projects.create', { name: `conn-${STAMP}`, path: dir });
  const before = (await rpc('settings.get')).settings;
  const sessions = [];
  const chat = async (title, model = QWEN) => {
    const s = await rpc('sessions.create', { title, projectId: project.id, model });
    sessions.push(s.id);
    return s.id;
  };
  const bundle = path.join(APP, 'plugins', 'NetPI.Agents', 'wwwroot', 'ui.js');
  const original = fs.readFileSync(bundle);
  const gate = `conn-${STAMP}`;
  try {
    await openApp(page);

    // ---- a plugin tab follows its bundle: a new version remounts it (ui.changed), a bundle that fails says so, Retry loads
    await openStripTab(page, 'right', 'Usage');
    await page.waitForSelector('.plugin-root > .usage', { timeout: 10_000 });
    await page.evaluate(() => (window.__e2eRoot = document.querySelector('.plugin-root:has(> .usage)')));
    const change = () => fs.appendFileSync(bundle, `\n// e2e ${Date.now()}\n`); // a changed file: the same bytes are no new version
    change();
    await page.waitForFunction(() => window.__e2eRoot && !window.__e2eRoot.isConnected, null, { timeout: 10_000 }).catch(() => {});
    await page.waitForSelector('.plugin-root > .usage', { timeout: 10_000 }).catch(() => {});
    check('a plugin tab remounts when its bundle changes (ui.changed)', await page.evaluate(() => !window.__e2eRoot.isConnected && !!document.querySelector('.plugin-root > .usage')));
    await page.route('**/plugins/netpi.agents/ui.js*', (r) => r.fulfill({ status: 500, body: 'boom' }));
    change();
    await page.waitForSelector('.plugin-tab .state.error', { timeout: 10_000 }).catch(() => {});
    check('a bundle that does not load shows the error state', (await page.locator('.plugin-tab .state.error').count()) > 0);
    await shot(page, 'ui-conn-01-plugin-error');
    await page.unroute('**/plugins/netpi.agents/ui.js*');
    await page.locator('.plugin-tab .state.error button', { hasText: 'Retry' }).first().click();
    await page.waitForSelector('.plugin-root > .usage', { timeout: 10_000 }).catch(() => {});
    check('Retry loads it again', (await page.locator('.plugin-root > .usage').count()) > 0);
    fs.writeFileSync(bundle, original);
    // the failed bundle's two console errors are the ones this part expects
    for (let i = errors.length - 1; i >= 0; i--) if (/status of 500|failed to load tab usage/.test(errors[i])) errors.splice(i, 1);

    // ---- a run that ends while the page is offline: its stream.end is lost with the socket, so on reconnect the page
    // has to set its partial answer against what the server says (the run is done) and not keep it
    const title = `Missing pool ${STAMP}`;
    const sid = await chat(title);
    await send(sid, 'The scheduler throws on a missing pool. [s:say text="Looking."]');
    await openSession(page, title);
    await rpc('agent.send', { sessionId: sid, text: `Make it fail with a clear message. [s:say text="The scheduler names the missing pool now: unknown-pool handling." gate=${gate}]` });
    await gates.waiting(gate);
    await page.waitForSelector('.assistant.live, .thinking.live', { timeout: 10_000 }).catch(() => {});
    check('the run is streaming before the drop', (await page.locator('.assistant.live, .thinking.live').count()) > 0);
    await socket.drop();
    await page.waitForSelector('.conn[data-status="reconnecting"]', { timeout: 10_000 }).catch(() => {});
    check('the connection shows reconnecting after the drop', (await page.locator('.conn[data-status="reconnecting"]').count()) > 0);
    await gates.release(gate);
    const done = await waitIdle(sid, 2, 30_000).catch(() => null);
    check('the run finishes while the page is offline', !!done);
    const msgs = (await rpc('sessions.messages', { id: sid })).messages;
    check('its answer is stored on the server', msgs.at(-1)?.role === 'assistant');
    await socket.restore();
    await page.waitForSelector('.conn[data-status="open"]', { timeout: 30_000 }).catch(() => {});
    await page.waitForFunction(() => !document.querySelector('.composer.running'), null, { timeout: 30_000 }).catch(() => {});
    const answer = page.locator('.content .item[data-kind="text"]', { hasText: 'unknown-pool handling' });
    await answer.first().waitFor({ timeout: 10_000 }).catch(() => {});
    await page.waitForTimeout(400);
    check('on reconnect the finished run\'s stale partial answer is gone', (await page.locator('.assistant.live, .thinking.live').count()) === 0, `${await page.locator('.assistant.live, .thinking.live').count()} live blocks`);
    check('…and the stored answer shows', (await answer.count()) === 1);
    await shot(page, 'ui-conn-02-ended-offline');

    // ---- the answer so far (agent.stream): a chat opened while its answer streams shows it from its start, and a page that
    // lost its connection mid-answer shows it without the part that streamed meanwhile missing
    const live = page.locator('.assistant.live');
    const liveText = async () => ((await live.innerText().catch(() => '')) ?? '').replace(/\s+/g, ' ').trim();
    const midTitle = `Mid-answer ${STAMP}`;
    const mid = await chat(midTitle);
    await send(mid, 'Hello. [s:say]');
    await rpc('agent.send', { sessionId: mid, text: 'Take your time. [s:slow ms=8000]' });
    await until('a few parts streamed', async () => ((await rpc('agent.get', { sessionId: mid }))?.activity ?? '') === 'writing', 10_000).catch(() => {});
    await new Promise((r) => setTimeout(r, 1500)); // the chat is not open: its parts so far go by unseen
    await openSession(page, midTitle);
    await live.waitFor({ timeout: 5000 }).catch(() => {});
    const opened = await liveText();
    check('a chat opened while its answer streams shows the answer from its start', opened.startsWith('Slow answer: part 1.') && SLOW.startsWith(opened), JSON.stringify(opened.slice(0, 80)));
    await waitIdle(mid, 2, 30_000).catch(() => {});

    await rpc('agent.send', { sessionId: mid, text: 'Once more. [s:slow ms=8000]' });
    await until('the answer streaming in the open chat', async () => (await liveText()).includes('part 3.'), 10_000).catch(() => {});
    const before = await liveText();
    await socket.drop();
    await new Promise((r) => setTimeout(r, 1500)); // parts stream while the page cannot hear them
    await socket.restore();
    await page.waitForSelector('.conn[data-status="open"]', { timeout: 30_000 }).catch(() => {});
    await page.waitForTimeout(800);
    const after = await liveText();
    check('after a reconnect mid-answer the answer has no gap: what streamed meanwhile is there',
      after.length > before.length && after.startsWith('Slow answer: part 1.') && SLOW.startsWith(after), `${JSON.stringify(before.slice(-30))} → ${JSON.stringify(after.slice(-60))}`);
    await waitIdle(mid, 3, 30_000).catch(() => {});

    // ---- a connection that goes and comes back: the page reconnects and subscribes again, so a run's events reach it
    await socket.drop();
    await page.waitForSelector('.conn[data-status="reconnecting"]', { timeout: 10_000 }).catch(() => {});
    check('the connection shows reconnecting', (await page.locator('.conn[data-status="reconnecting"]').count()) > 0);
    await shot(page, 'ui-conn-03-reconnecting');
    await socket.restore();
    await page.waitForSelector('.conn[data-status="open"]', { timeout: 30_000 }).catch(() => {});
    check('it reconnects', (await page.locator('.conn[data-status="open"]').count()) > 0);
    const ta = page.locator('.composer textarea');
    await ta.fill(`A quick check after the reconnect. [s:say text="Still here." gate=${gate}-2]`);
    await ta.press('Enter');
    const streamed = await page.waitForSelector('.composer.running', { timeout: 10_000 }).then(() => true).catch(() => false);
    check('a run\'s events reach the page after the reconnect (subscribed again)', streamed);
    await gates.release(`${gate}-2`);
    await waitIdle(sid, 3, 30_000).catch(() => {});
    await page.waitForFunction(() => !document.querySelector('.composer.running'), null, { timeout: 15_000 }).catch(() => {});

    // ---- without the runtime, the context and the compaction plugins: the draft stays, sending waits for the runtime
    const draft = 'Keep this draft while the runtime is off.';
    await ta.fill(draft);
    for (const id of ['netpi.runtime', 'netpi.context', 'netpi.compaction']) await rpc('plugins.setEnabled', { id, enabled: false });
    await page.getByText('Execution unavailable — enable Runtime to send. Your draft is kept.').waitFor({ timeout: 10_000 }).catch(() => {});
    check('the composer keeps the draft when execution goes', (await ta.inputValue()) === draft && (await page.getByText('Execution unavailable — enable Runtime to send.').count()) === 1);
    check('sending is disabled without the runtime', await page.locator('.composer button.send').isDisabled());
    await openStripTab(page, 'right', 'Diagnostics');
    await page.locator('.plugin-root').getByRole('button', { name: 'Context', exact: true }).click();
    await page.locator('.plugin-root').getByText('Context preview unavailable', { exact: false }).waitFor({ timeout: 10_000 }).catch(() => {});
    check('Diagnostics still finds the instruction files without the context preview',
      (await page.locator('.plugin-root').getByText('Context preview unavailable', { exact: false }).count()) > 0 && (await page.locator('.plugin-root').getByText('AGENTS.md', { exact: true }).count()) > 0);
    await page.locator('.composer .ring').click();
    await page.waitForSelector('.popover .cx', { timeout: 5000 }).catch(() => {});
    const compact = page.locator('.popover .cx .foot button', { hasText: 'Compact now' });
    await compact.waitFor({ state: 'detached', timeout: 5000 }).catch(() => {});
    check('the context popout has no "Compact now" without the compaction plugin', (await page.locator('.popover .cx').count()) === 1 && (await compact.count()) === 0, `${await compact.count()} button(s)`);
    await shot(page, 'ui-conn-04-no-runtime');
    await page.keyboard.press('Escape');
    for (const id of ['netpi.runtime', 'netpi.context', 'netpi.compaction']) await rpc('plugins.setEnabled', { id, enabled: true });
    await page.waitForFunction(() => document.querySelector('.composer button.send') && !document.querySelector('.composer button.send').disabled, null, { timeout: 15_000 }).catch(() => {});
    check('with the runtime back, Send works again and the draft is still there', !(await page.locator('.composer button.send').isDisabled()) && (await ta.inputValue()) === draft);
    await ta.fill('');
    await rpc('settings.set', { path: 'plugins', value: before.plugins ?? null });

    // ---- notifications: what the page asks the desktop app for (a stand-in for its chrome.webview bridge records them)
    const p = await context.newPage();
    await p.addInitScript(() => {
      const listeners = [];
      window.__posted = [];
      window.chrome = window.chrome || {};
      window.chrome.webview = {
        postMessage: (m) => window.__posted.push(JSON.parse(JSON.stringify(m))),
        addEventListener: (type, f) => type === 'message' && listeners.push(f),
        emit: (data) => listeners.forEach((f) => f({ data })),
      };
    });
    await openApp(p);
    const posted = (sessionId) => p.evaluate((id) => window.__posted.filter((m) => m.type === 'notify' && m.sessionId === id), sessionId);
    const notes = `Notify me ${STAMP}`;
    const notified = await chat(notes);
    await send(notified, 'Tell me when you are done. [s:say]');
    await until('the notification', async () => (await posted(notified)).length > 0, 10_000).catch(() => {});
    await p.waitForTimeout(500);
    const finished = await posted(notified);
    check('notifications: a finished run asks for one', finished.length === 1 && finished[0].body === 'Finished' && finished[0].title.includes(notes), JSON.stringify(finished));

    // a chat the budget stopped asks to go over: that is the one notification, no "Finished" after it. In the open chat its
    // notice says so; a chat that is not open sends this page no notice (it subscribes to its open tabs), and the run's
    // error says it
    await rpc('settings.set', { path: `agents.${PAID}`, value: { model: CLAUDE, cost: { input: 30, output: 150 }, use: 'the e2e connection test: paid' } });
    const overTitle = `Over budget ${STAMP}`;
    const over = await chat(overTitle, CLAUDE);
    await send(over, 'Hello. [s:say]'); // a paid call while there is no limit: the chat is listed, and opens
    await openSession(p, overTitle);
    await rpc('settings.set', { path: 'budget', value: { ...(before.budget ?? {}), monthlyUsd: 0.000001, onLimit: 'ask' } });
    await p.waitForTimeout(2000); // the "Finished" of the first answer is out
    const seen = (await posted(over)).length;
    await send(over, 'Summarize the repo with the paid model. [s:say]');
    await until('the budget notification', async () => (await posted(over)).length > seen, 10_000).catch(() => {});
    await p.waitForTimeout(2500); // a "Finished" would come 1.5 s after the run ends
    const asked = (await posted(over)).slice(seen);
    check('notifications: the budget ask in an open chat is one, without a "Finished" after it', asked.length === 1 && /budget/i.test(asked[0].body) && !asked[0].body.startsWith('Failed'), JSON.stringify(asked));
    const away = await chat(`Over budget too ${STAMP}`, CLAUDE);
    await send(away, 'Summarize the docs with the paid model. [s:say]');
    await until('the notification of a chat that is not open', async () => (await posted(away)).length > 0, 10_000).catch(() => {});
    await p.waitForTimeout(500);
    const told = await posted(away);
    check('notifications: a chat that is not open says its run stopped, and why, not "Finished"', told.length === 1 && told[0].body.startsWith('Failed: ') && /budget/i.test(told[0].body), JSON.stringify(told));

    // the desktop app says which notification was clicked: that chat opens
    await p.evaluate((sessionId) => window.chrome.webview.emit({ type: 'openSession', sessionId }), notified);
    await p.waitForFunction((id) => !!document.querySelector(`.topbar .tab.active[data-tab="${id}"]`), notified, { timeout: 5000 }).catch(() => {});
    check('notifications: a click opens the chat', (await p.locator('.topbar .tab.active').getAttribute('data-tab')) === notified);
    await p.close();
  } finally {
    fs.writeFileSync(bundle, original);
    await gates.release(gate).catch(() => {});
    await gates.release(`${gate}-2`).catch(() => {});
    await socket.restore().catch(() => {});
    for (const id of ['netpi.runtime', 'netpi.context', 'netpi.compaction']) await rpc('plugins.setEnabled', { id, enabled: true }).catch(() => {});
    for (const id of sessions) await rpc('agent.abort', { sessionId: id }).catch(() => {});
    for (const id of sessions) await rpc('sessions.delete', { id }).catch(() => {});
    await rpc('projects.delete', { id: project.id }).catch(() => {});
    await rpc('settings.set', { path: 'plugins', value: before.plugins ?? null }).catch(() => {});
    await rpc('settings.set', { path: 'budget', value: before.budget ?? null }).catch(() => {});
    await rpc('settings.set', { path: `agents.${PAID}`, value: null }).catch(() => {});
  }
}, { tap: true, keepsSettings: true });
