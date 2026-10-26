#!/usr/bin/env node
// Journey: the phone page, walked through once at a phone's size. It pairs from the Phone tab's link, starts a chat
// from the New chat form, gets the answer streamed so far back after a reload mid-answer (agent.stream), shows the
// harness notices, says so when its listener goes away and connects again by itself when it is back, shows a failed run
// in the chat, the status strip and the chats list, and opens the Agents view. The listener is switched on for the
// journey on a free port and off again after it; the phone it paired is unpaired.
//
//   node tests/NetPI.E2E/ui/phone.mjs --url http://127.0.0.1:7470 --token e2e-token --mock <url>
import net from 'node:net';
import { run, check, shot, expectEventually, until, rpc, gates, STAMP, MODEL, PROBE } from './lib.mjs';

const port = await new Promise((resolve) => {
  const s = net.createServer().listen(0, '127.0.0.1', () => { const p = s.address().port; s.close(() => resolve(p)); });
});
const devicesBefore = new Set((await rpc('phone.devices')).map((d) => d.id));
const listening = (on) => until(`the phone listener ${on ? 'up' : 'down'}`, async () => (await rpc('phone.status')).listening === on, 30_000);

await run('phone', async ({ page, errors }) => {
  try {
    await rpc('settings.set', { path: 'phone.port', value: port });
    await rpc('settings.set', { path: 'phone.enabled', value: true });
    await listening(true);
    const view = () => page.locator('#app').getAttribute('data-view', PROBE);
    const conn = () => page.getByRole('button', { name: 'Connection' }).getAttribute('data-state', PROBE);
    const strip = () => page.locator('#strip').getAttribute('data-state', PROBE);
    const live = page.locator('[data-kind="text"][data-live]');
    const stored = page.locator('[data-kind="text"]:not([data-live])');

    // ---- pair from the link the Phone tab shows
    const pair = await rpc('phone.pair');
    await page.goto(pair.links.find((l) => l.includes(`:${port}`)) ?? pair.links[0]);
    await expectEventually('the link pairs the phone: the chats list shows', view, { pass: (v) => v === 'list', stop: true });
    await expectEventually('the page is connected', conn, { pass: (s) => s === 'live', stop: true });

    // ---- a new chat from the form; its answer waits at a gate after its first words
    const G = `phone-${STAMP}`;
    await page.getByRole('button', { name: 'New chat' }).click();
    await page.getByRole('textbox', { name: 'Task' }).fill(`Phone walkthrough ${STAMP} [s:say text="The phone page is fine now. PHONE-WALK-DONE" gate=${G}]`);
    await page.getByRole('button', { name: 'Start' }).click();
    const id = await expectEventually('Start opens the new chat', () => decodeURIComponent(new URL(page.url()).hash).match(/^#chat=(.+)$/)?.[1], { stop: true });
    await gates.waiting(G);
    await expectEventually('the answer streams in, and the strip says it is working', async () => `${await live.count()} ${await strip()}`, { pass: (s) => /^[1-9]\d* running$/.test(s), timeout: MODEL });
    await expectEventually('the harness notices are rows in the chat', () => page.locator('[data-kind="notice"]').count(), { pass: (n) => n > 0 });

    // ---- a reload mid-answer brings back what has streamed so far
    await page.reload();
    await expectEventually('after a reload mid-answer the streamed text is back', () => live.innerText(PROBE), { pass: (t) => t.trim().length > 0, stop: true });
    await shot(page, 'ui-phone-01-reloaded-mid-answer');
    await gates.release(G);
    await expectEventually('the stored answer replaces the streamed one', async () => `${await live.count()} ${await stored.last().innerText(PROBE)}`,
      { pass: (t) => t.startsWith('0 ') && t.includes('PHONE-WALK-DONE'), timeout: MODEL });
    await expectEventually('the strip goes when the run ends', strip, { pass: (s) => s === '' });

    // ---- the listener goes away and comes back: the page says so, and reconnects by itself
    const quiet = errors.length;
    await rpc('settings.set', { path: 'phone.enabled', value: false });
    await listening(false);
    await expectEventually('the page notices the lost connection', conn, { pass: (s) => s === 'retrying' || s === 'offline', timeout: 30_000 });
    await expectEventually('a banner says it is not connected', () => page.getByTestId('banner').isVisible());
    await shot(page, 'ui-phone-02-disconnected');
    await rpc('settings.set', { path: 'phone.enabled', value: true });
    await listening(true);
    await expectEventually('it connects again by itself', conn, { pass: (s) => s === 'live', timeout: 30_000, stop: true });
    // shown for 2.5 s from the moment it is live, which the line above has just seen; a stray "null" once followed it
    await expectEventually('the banner says it is connected again, and nothing else', () => page.getByTestId('banner').innerText(PROBE),
      { pass: (t) => t.trim() === 'Connected again · up to date', timeout: 2_000 });
    // the page's own requests while the listener was off failed, as they should: the test's doing, not the page's errors
    for (let i = errors.length - 1; i >= quiet; i--) if (/Failed to load resource/.test(errors[i])) errors.splice(i, 1);

    // ---- a run that fails after the reconnect: the chat and the strip say so
    const box = page.getByRole('textbox', { name: 'Message' });
    await box.fill('[s:fail] and now break');
    await box.press('Enter');
    await expectEventually('the error is a row in the chat', () => page.locator('[data-tone="err"]').count(), { pass: (n) => n > 0, timeout: MODEL });
    await expectEventually('the strip says the last run failed', strip, { pass: (s) => s === 'failed' });
    await shot(page, 'ui-phone-03-failed');

    // ---- the list shows the chat as failed; the Agents view opens
    await page.getByRole('button', { name: 'Back' }).click();
    await expectEventually('back at the list, the chat is a failed row', () => page.locator(`[data-session="${id}"]`).getAttribute('data-state', PROBE), { pass: (s) => s === 'failed' });
    await page.getByRole('button', { name: 'Agents' }).click();
    await expectEventually('the Agents view opens', view, { pass: (v) => v === 'work' });
    check('the chat was started from the phone', (await rpc('sessions.get', { id }))?.meta?.origin === 'phone');
  } finally {
    await page.goto('about:blank');   // a page left open would try to reconnect to the listener switched off below, and log it
    await rpc('settings.set', { path: 'phone.enabled', value: null });
    await rpc('settings.set', { path: 'phone.port', value: null });
    for (const d of await rpc('phone.devices')) if (!devicesBefore.has(d.id)) await rpc('phone.revoke', { id: d.id });
  }
}, { viewport: { width: 390, height: 844 }, keepsSettings: true });
