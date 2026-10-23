#!/usr/bin/env node
// Journey: the agent asks the person something (ask_user). The question is a card in the chat that is still there after
// the page is reloaded; picking an option answers it and the run goes on with the answer; an answer typed in the message
// box answers it too; and a card with two questions (one of them multiple choice) is sent with its Send button.
//
//   node tests/NetPI.E2E/ui/asks.mjs --url http://127.0.0.1:7470 --token e2e-token --mock <url>
import { run, shot, expectEventually, openApp, openSession, rpc, send, STAMP, composerOf, messageBox, lastAnswer, MODEL, PROBE } from './lib.mjs';

const TITLE = `Asks ${STAMP}`;
const session = await rpc('sessions.create', { title: TITLE });
await send(session.id, 'hello [s:echo]'); // a chat with no messages is not listed

await run('asks', async ({ page }) => {
  await openApp(page);
  let chat = await openSession(page, session.id, TITLE);
  const box = messageBox(page);
  const card = () => chat.getByTestId('ask').last();
  const cardState = () => card().getAttribute('data-state', PROBE);
  const ends = (marker) => expectEventually('the run goes on with the answer', async () => `${await composerOf(page).getAttribute('data-state', PROBE)} ${await lastAnswer(chat)}`,
    { pass: (s) => s.startsWith('idle ') && s.includes('ASK-DONE') && s.includes(marker), timeout: MODEL });

  // ---- an option, after a reload
  await box.fill('Pick a way [s:ask]');
  await box.press('Enter');
  await expectEventually('the question is a card in the chat, waiting', cardState, { pass: (s) => s === 'waiting', timeout: MODEL, stop: true });
  await shot(page, 'ui-asks-01-card');
  await page.reload();
  await openApp(page);
  chat = await openSession(page, session.id, TITLE);
  await expectEventually('after a reload the card still waits for the answer', cardState, { pass: (s) => s === 'waiting', stop: true });
  await card().getByRole('radio', { name: 'Fast' }).click();
  await expectEventually('picking an option answers it', cardState, { pass: (s) => s === 'answered' });
  await ends('Fast');

  // ---- an answer in the person's own words
  await box.fill('Again [s:ask]');
  await box.press('Enter');
  await expectEventually('a new question waits', cardState, { pass: (s) => s === 'waiting', timeout: MODEL, stop: true });
  await box.fill('Neither, do it my way');
  await box.press('Enter');
  await expectEventually('an answer typed in the message box answers it', cardState, { pass: (s) => s === 'answered' });
  await ends('Neither, do it my way');

  // ---- two questions, one of them multiple choice: the Send button
  await box.fill('Two things [s:ask n=2]');
  await box.press('Enter');
  await expectEventually('a card with two questions waits', cardState, { pass: (s) => s === 'waiting', timeout: MODEL, stop: true });
  await card().getByRole('radio', { name: 'Patch the client' }).click();
  await card().getByRole('checkbox', { name: 'unit' }).click();
  await card().getByRole('checkbox', { name: 'e2e' }).click();
  await shot(page, 'ui-asks-02-two');
  await card().getByRole('button', { name: 'Send' }).click();
  await expectEventually('Send answers both', cardState, { pass: (s) => s === 'answered' });
  await ends('Patch the client');
  // the answered card opens to the questions and what was picked for each
  await card().click();
  await expectEventually('the answered card shows what was picked for each question', () => chat.locator('[data-kind="ask"]').last().innerText(PROBE),
    { pass: (t) => t.includes('Patch the client') && /unit/.test(t) && /e2e/.test(t) });
});
