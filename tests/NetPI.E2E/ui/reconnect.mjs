#!/usr/bin/env node
// Journey: the page loses its connection to the server and gets it back. The top bar says it is reconnecting; a run that
// finished while the page was away shows its stored answer and no half-written one; an answer still streaming continues
// with nothing missing from the part that streamed meanwhile; and a run started after the reconnect reaches the page
// (it subscribed again). The socket is dropped and restored by the test (tapSocket), the server keeps running.
//
//   node tests/NetPI.E2E/ui/reconnect.mjs --url http://127.0.0.1:7470 --token e2e-token --mock <url>
import { run, shot, expectEventually, openApp, openSession, rpc, send, gates, waitIdle, until, STAMP, composerOf, messageBox, lastAnswer, MODEL, PROBE } from './lib.mjs';

const TITLE = `Reconnect ${STAMP}`;
const session = await rpc('sessions.create', { title: TITLE });
await send(session.id, 'hello [s:echo]'); // a chat with no messages is not listed
const runs = async () => (await rpc('agent.get', { sessionId: session.id }))?.runs ?? 0;
/** The answer the server has streamed so far for the run in flight (agent.stream), as the page would show it. */
const serverText = async () => ((await rpc('agent.stream', { sessionId: session.id }))?.events ?? [])
  .filter((e) => e.type === 'stream.delta' && e.d?.kind === 'text').map((e) => e.d.text).join('');
const SLOW = 'Slow answer:' + Array.from({ length: 24 }, (_, i) => ` part ${i + 1}.`).join('') + ' SLOW-DONE';

await run('reconnect', async ({ page, socket }) => {
  await openApp(page);
  const chat = await openSession(page, session.id, TITLE);
  const box = messageBox(page);
  const conn = () => page.getByTestId('connection').getAttribute('data-status', PROBE);
  const state = () => composerOf(page).getAttribute('data-state', PROBE);
  const live = () => chat.locator('[data-kind="text"][data-stream]').innerText(PROBE).then((t) => t.replace(/\s+/g, ' ').trim());

  // ---- a run that ends while the page is away: its stream.end is lost with the socket
  const G = `offline-${STAMP}`;
  let before = await runs();
  await box.fill(`Make it fail clearly. [s:say text="The scheduler names the missing pool now." gate=${G}]`);
  await box.press('Enter');
  await gates.waiting(G);
  await expectEventually('the answer is streaming before the connection goes', () => chat.locator('[data-stream]').count(), { pass: (n) => n > 0, stop: true });
  await socket.drop();
  await expectEventually('the top bar says the page is reconnecting', conn, { pass: (s) => s !== 'open' });
  await shot(page, 'ui-reconnect-01-down');
  await gates.release(G);
  await waitIdle(session.id, before + 1, 30_000); // the run ends on the server, unheard
  await socket.restore();
  await expectEventually('the page connects again', conn, { pass: (s) => s === 'open', timeout: 30_000, stop: true });
  await expectEventually('the finished run shows as finished', state, { pass: (s) => s === 'idle' });
  await expectEventually('its stored answer shows, and no half-written one', async () => `${await chat.locator('[data-stream]').count()} ${await lastAnswer(chat)}`,
    { pass: (t) => t.startsWith('0 ') && t.includes('names the missing pool now') });
  await shot(page, 'ui-reconnect-02-ended-offline');

  // ---- an answer still streaming: what streamed while the page was away is not missing
  before = await runs();
  await box.fill('Take your time. [s:slow ms=20000]');
  await box.press('Enter');
  const seen = await expectEventually('a slow answer streams', live, { pass: (t) => t.includes('part 2.'), timeout: MODEL, stop: true });
  await socket.drop();
  await until('more of the answer to stream while the page is away', async () => (await serverText()).length > seen.length + 30, 15_000);
  await socket.restore();
  await expectEventually('the page connects again', conn, { pass: (s) => s === 'open', timeout: 30_000 });
  await expectEventually('the answer goes on from where the server is, with no gap', live,
    { pass: (t) => t.length > seen.length + 30 && SLOW.startsWith(t), timeout: MODEL });
  await waitIdle(session.id, before + 1, 30_000);
  await expectEventually('and ends whole', () => lastAnswer(chat), { pass: (t) => t.replace(/\s+/g, ' ').includes(SLOW), timeout: MODEL });

  // ---- after a reconnect the page is subscribed again: a new run's events reach it
  await socket.drop();
  await expectEventually('the top bar says the page is reconnecting', conn, { pass: (s) => s !== 'open' });
  await socket.restore();
  await expectEventually('the page connects again', conn, { pass: (s) => s === 'open', timeout: 30_000, stop: true });
  const A = `after-${STAMP}`;
  await box.fill(`A quick check. [s:say text=STILL-HERE gate=${A}]`);
  await box.press('Enter');
  await gates.waiting(A);
  await expectEventually('a run started after the reconnect shows as running', state, { pass: (s) => s === 'running' });
  await gates.release(A);
  await expectEventually('and its answer arrives', () => lastAnswer(chat), { pass: (t) => t.includes('STILL-HERE'), timeout: MODEL });
  await expectEventually('the run ends with nothing left streaming', async () => `${await state()} ${await chat.locator('[data-stream]').count()}`, { pass: (s) => s === 'idle 0', timeout: MODEL });
}, { tap: true });
