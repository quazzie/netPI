#!/usr/bin/env node
// Journey: a person corrects a chat while it works. Enter while the agent runs steers it (a steer chip until the agent
// reads it), Alt+Enter queues a follow-up (a queued chip until the run gets to it), and Esc stops an answer, keeping
// what was written so far. Each answer waits at a MockLlm gate, so the running state lasts until the journey has seen it.
//
//   node tests/NetPI.E2E/ui/chat-control.mjs --url http://127.0.0.1:7470 --token e2e-token --mock <url>
import { run, check, shot, expectEventually, openApp, openSession, rpc, send, gates, STAMP, composerOf, messageBox, lastAnswer, MODEL, PROBE } from './lib.mjs';

const TITLE = `Control ${STAMP}`;
const chatSession = await rpc('sessions.create', { title: TITLE });
await send(chatSession.id, 'hello [s:echo]'); // a chat with no messages is not listed

await run('chat-control', async ({ page }) => {
  await openApp(page);
  const chat = await openSession(page, chatSession.id, TITLE);
  const box = messageBox(page);
  const state = () => composerOf(page).getAttribute('data-state', PROBE);
  const chips = () => page.getByTestId('queue-chip').evaluateAll((els) => els.map((e) => e.dataset.mode));
  const answer = () => lastAnswer(chat);

  // ---- steer: Enter while the agent runs
  const S = `steer-${STAMP}`;
  await box.fill(`First question [s:echo gate=${S}]`);
  await box.press('Enter');
  await gates.waiting(S);
  await expectEventually('the composer shows the run', state, { pass: (s) => s === 'running' });
  await box.fill('Read this first [s:say text=STEER-READ]');
  await box.press('Enter');
  await expectEventually('Enter while it runs puts a steer chip above the composer', chips, { pass: (m) => m.includes('steer') });
  await shot(page, 'ui-control-01-steer');
  await gates.release(S);
  await expectEventually('the agent reads the steer and answers it', answer, { pass: (t) => t.includes('STEER-READ'), timeout: MODEL });
  await expectEventually('the run ends with no chip left', async () => `${await state()} ${(await chips()).length}`, { pass: (s) => s === 'idle 0', timeout: MODEL });
  check('the steer is in the chat as the user wrote it', (await chat.locator('[data-kind="user"]').allInnerTexts()).some((t) => t.includes('Read this first')));

  // ---- queue: Alt+Enter while the agent runs
  const Q = `queue-${STAMP}`;
  await box.fill(`Second question [s:echo gate=${Q}]`);
  await box.press('Enter');
  await gates.waiting(Q);
  await box.fill('Afterwards, this [s:say text=QUEUED-READ]');
  await box.press('Alt+Enter');
  await expectEventually('Alt+Enter while it runs puts a queued chip above the composer', chips, { pass: (m) => m.includes('queue') });
  await shot(page, 'ui-control-02-queued');
  await gates.release(Q);
  await expectEventually('the queued message runs after the answer and is answered', answer, { pass: (t) => t.includes('QUEUED-READ'), timeout: MODEL });
  await expectEventually('the run ends with no chip left', async () => `${await state()} ${(await chips()).length}`, { pass: (s) => s === 'idle 0', timeout: MODEL });

  // ---- stop: Esc while an answer streams
  await box.fill('Long answer please [s:slow ms=30000]');
  await box.press('Enter');
  await expectEventually('a slow answer streams', answer, { pass: (t) => t.includes('part 2'), timeout: MODEL, stop: true });
  await box.press('Escape');
  await expectEventually('Esc stops the run', state, { pass: (s) => s === 'idle', timeout: MODEL });
  const kept = await answer();
  check('what was written before the stop stays, and the rest never comes', kept.includes('part 2') && !kept.includes('SLOW-DONE'), kept.slice(-120));
  await shot(page, 'ui-control-03-stopped');
});
