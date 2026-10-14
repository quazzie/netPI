#!/usr/bin/env node
// Journey: a person opens a chat and has the agent edit a file. They see the answer stream in, the tool rows run and
// finish, the edit's diff, the final answer, and every side-panel tab still opens. The C# side (ui.chat) made the project
// and the chat, and checks the file on disk afterwards.
//
//   node tests/NetPI.E2E/ui/chat.mjs --url http://127.0.0.1:7470 --token e2e-token --mock <url> --session-id <id> --session <title>
import { run, arg, check, shot, expectEventually, openApp, openSession, showTab, composerOf, messageBox, lastAnswer, unfoldSteps, MODEL, PROBE } from './lib.mjs';

const ID = arg('session-id');
const TITLE = arg('session');
// hold=1500: the mock keeps its first answer open for 1.5 s after the first chunk, so the streaming state is there to be
// seen instead of over in ~100 ms
const TEXT = 'Please edit the notes [s:tools file=notes.txt old=alpha new=ALPHA-UI hold=1500]';

await run('chat', async ({ page }) => {
  await openApp(page);
  const chat = await openSession(page, ID, TITLE);
  await shot(page, 'ui-chat-01-open');

  await messageBox(page).fill(TEXT);
  await messageBox(page).press('Enter');
  await expectEventually('the answer streams into the chat while the model writes', () => chat.locator('[data-stream]').count(), { pass: (n) => n > 0, timeout: MODEL });
  await shot(page, 'ui-chat-02-streaming');
  await expectEventually('a tool row runs and finishes', () => chat.locator('[data-status="ok"]').count(), { pass: (n) => n > 0, timeout: MODEL });
  await expectEventually('the run ends: the composer is idle again', () => composerOf(page).getAttribute('data-state', PROBE), { pass: (s) => s === 'idle', timeout: MODEL, stop: true });
  await expectEventually('the final answer is shown', () => lastAnswer(chat), { pass: (t) => t.includes('TOOLS-DONE') });
  await expectEventually('nothing is left streaming', () => chat.locator('[data-stream]').count(), { pass: (n) => n === 0 });
  check('the user message is shown', (await chat.locator('[data-kind="user"]').allInnerTexts()).some((t) => t.includes('Please edit the notes')));

  // a finished run's steps fold: open them as a person would, then the edit's row
  await unfoldSteps(chat);
  const tools = await chat.locator('[data-tool]').evaluateAll((rows) => rows.map((r) => `${r.dataset.tool}:${r.dataset.status}`));
  check('the four tool calls are rows that finished (ls, read, edit, write)', ['ls', 'read', 'edit', 'write'].every((t) => tools.includes(`${t}:ok`)), tools.join(' '));
  check('no tool row ended in an error', !tools.some((t) => t.endsWith(':error')), tools.join(' '));
  await chat.locator('[data-tool="edit"] button[aria-expanded]').first().click();
  await expectEventually('the edit shows its diff', () => chat.locator('[data-testid="diff"] [data-row="add"]').count(), { pass: (n) => n > 0 });
  await shot(page, 'ui-chat-03-diff');

  // every side-panel tab opens: a plugin's tab mounts its bundle (a bundle that no longer loads is its error state),
  // a built-in one shows its content
  const keys = await page.locator('[data-tab-key]').evaluateAll((tabs) => tabs.map((t) => t.dataset.tabKey));
  check('the side panels have their tabs', keys.includes('core/sessions') && keys.includes('netpi.work/work'), keys.join(', '));
  for (const key of keys) {
    const pane = await showTab(page, key);
    await expectEventually(`the ${key} tab opens`, async () => {
      const plugin = pane.getByTestId('plugin-tab');
      if (await plugin.count()) return plugin.getAttribute('data-state', PROBE);
      return (await pane.innerText(PROBE)).trim() ? 'ready' : 'empty';
    }, { pass: (s) => s === 'ready' });
  }
  await shot(page, 'ui-chat-04-tabs');
}, { viewport: { width: 1400, height: 950 }, http: true });
