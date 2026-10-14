#!/usr/bin/env node
// Journey: a person watches the agent work. The thinking is there while the model thinks and opens to show it; a shell
// command's output arrives in its row while it runs, and the row ends with all of it; a stream that dropped is retried,
// and what it had written before the drop is not left in the chat. The thinking waits at a MockLlm gate and the command
// waits for a file the journey writes, so each live state lasts until it has been seen.
//
//   node tests/NetPI.E2E/ui/tools.mjs --url http://127.0.0.1:7470 --token e2e-token --mock <url> --work <dir>
import fs from 'node:fs';
import path from 'node:path';
import { run, check, shot, expectEventually, openApp, openSession, rpc, send, gates, newDir, STAMP, composerOf, messageBox, lastAnswer, unfoldSteps, MODEL, PROBE } from './lib.mjs';

const dir = newDir(`tools-${STAMP}`);
const project = await rpc('projects.create', { name: `tools-${STAMP}`, path: dir });
const TITLE = `Tools ${STAMP}`;
const session = await rpc('sessions.create', { title: TITLE, projectId: project.id });
await send(session.id, 'hello [s:echo]'); // a chat with no messages is not listed

await run('tools', async ({ page }) => {
  await openApp(page);
  const chat = await openSession(page, session.id, TITLE);
  const box = messageBox(page);
  const idle = () => expectEventually('the run ends', () => composerOf(page).getAttribute('data-state', PROBE), { pass: (s) => s === 'idle', timeout: MODEL, quiet: true, stop: true });

  // ---- thinking while it streams
  const T = `think-${STAMP}`;
  await box.fill(`Think about it [s:echo gate=${T}]`);
  await box.press('Enter');
  await gates.waiting(T);
  const thinking = chat.getByTestId('thinking').last();
  await expectEventually('the thinking shows while the model thinks', () => thinking.getAttribute('data-state', PROBE), { pass: (s) => s === 'live' });
  await thinking.locator('button[aria-expanded]').click();
  await expectEventually('the live thinking opens to show what it has so far', () => thinking.getByTestId('thinking-body').innerText(PROBE), { pass: (t) => t.trim().length > 0 });
  await shot(page, 'ui-tools-01-thinking');
  await gates.release(T);
  await idle();
  await unfoldSteps(chat); // a finished run's steps fold
  await expectEventually('the thinking is done when the answer is', () => thinking.getAttribute('data-state', PROBE), { pass: (s) => s === 'done' });

  // ---- a shell command's output while it runs
  const flag = `.release-${STAMP}`;
  await box.fill(`Index the docs [s:fgwait file=${flag}]`);
  await box.press('Enter');
  const bash = chat.locator('[data-tool="bash"]').last();
  await expectEventually('the command runs in its row', () => bash.getAttribute('data-status', PROBE), { pass: (s) => s === 'running', timeout: MODEL, stop: true });
  await expectEventually('its output arrives while it runs', () => bash.getByTestId('tool-tail').innerText(PROBE), { pass: (t) => t.includes('indexing docs/') });
  await shot(page, 'ui-tools-02-live-output');
  fs.writeFileSync(path.join(dir, flag), '');
  await idle();
  await expectEventually('the answer follows the command', () => lastAnswer(chat), { pass: (t) => t.includes('FGWAIT-DONE'), timeout: MODEL });
  // the finished run's steps fold: open them, then the command's row
  await unfoldSteps(chat);
  await expectEventually('the row ended with the command', () => bash.getAttribute('data-status', PROBE), { pass: (s) => s === 'ok' });
  await bash.locator('button[aria-expanded]').first().click();
  await expectEventually('the finished row holds all of the output', () => bash.getByTestId('shell-output').innerText(PROBE), { pass: (t) => t.includes('indexing docs/') && t.includes('indexed') });

  // ---- a stream that drops is retried
  await box.fill('Answer despite a flaky connection [s:drop]');
  await box.press('Enter');
  await expectEventually('the answer comes after the retry', () => lastAnswer(chat), { pass: (t) => t.includes('DROP-RECOVERED'), timeout: MODEL });
  await idle();
  const all = (await chat.locator('[data-kind="text"]').allInnerTexts()).join('\n');
  check('what the dropped stream had written is not left in the chat', !all.includes('connection drops in the middle'));
  await shot(page, 'ui-tools-03-retried');
});
