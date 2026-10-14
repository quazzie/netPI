#!/usr/bin/env node
// Journey: a person starts a new chat from the start screen in a project. With no chat open the start screen shows;
// its chevron picks the project and starts a chat in it; the first answer makes the chat a row in the sessions list;
// and back on the start screen its main button names that project and starts the next chat there too.
//
//   node tests/NetPI.E2E/ui/new-chat.mjs --url http://127.0.0.1:7470 --token e2e-token --mock <url> --work <dir>
import { run, check, shot, expectEventually, openApp, showTab, rpc, newDir, STAMP, composerOf, messageBox, lastAnswer, MODEL, PROBE } from './lib.mjs';

const dir = newDir(`new-chat-${STAMP}`);
const project = await rpc('projects.create', { name: `new-chat-${STAMP}`, path: dir });

await run('new-chat', async ({ page }) => {
  await openApp(page);
  const welcome = page.getByTestId('welcome');
  await expectEventually('with no chat open, the start screen shows', () => welcome.isVisible(), { stop: true });
  const activeTab = () => page.locator('[role="tab"][data-tab][aria-selected="true"]').getAttribute('data-tab', PROBE);
  const composerProject = () => composerOf(page).getByRole('button', { name: 'Project', exact: true }).getAttribute('data-project', PROBE);

  // ---- the chevron: pick the project, a chat starts in it
  await welcome.getByTestId('pick-project').click();
  await page.getByRole('dialog').locator(`[data-project="${project.id}"]`).click();
  const id = await expectEventually('picking the project starts a chat', activeTab, { pass: (t) => t?.startsWith('ses_'), stop: true });
  await expectEventually('the chat is in the project', composerProject, { pass: (p) => p === project.id });
  await shot(page, 'ui-new-chat-01-started');

  // ---- the first answer makes it a chat in the list
  await messageBox(page).fill('hello [s:echo]');
  await messageBox(page).press('Enter');
  const chat = page.locator(`[data-testid="chat"][data-session="${id}"]`);
  await expectEventually('the answer comes', () => lastAnswer(chat), { pass: (t) => t.includes('ECHO-DONE'), timeout: MODEL });
  const sessions = await showTab(page, 'core/sessions');
  await expectEventually('the chat is a row in the sessions list', () => sessions.locator(`[data-session="${id}"]`).isVisible());
  check('the server has it in the project', (await rpc('sessions.get', { id }))?.projectId === project.id);
  await shot(page, 'ui-new-chat-02-listed');

  // ---- back on the start screen, the main button starts the next chat in the same project
  await page.locator(`[role="tab"][data-tab="${id}"]`).getByRole('button', { name: 'Close tab' }).click();
  await expectEventually('closing the chat shows the start screen again', () => welcome.isVisible());
  await expectEventually('its main button now starts in that project', () => welcome.getByTestId('start-in-project').getAttribute('data-project', PROBE), { pass: (p) => p === project.id });
  await welcome.getByTestId('start-in-project').click();
  await expectEventually('it starts a new chat', activeTab, { pass: (t) => t?.startsWith('ses_') && t !== id });
  await expectEventually('in the same project', composerProject, { pass: (p) => p === project.id });
}, { freshTabs: true });
