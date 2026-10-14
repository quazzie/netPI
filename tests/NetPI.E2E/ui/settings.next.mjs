#!/usr/bin/env node
// Journey: a person changes their settings. In Settings → Runs they change a number, see it saved, reset it, and get a
// value out of range refused; in Agents & budget they add an agent from the model search, give it one instance, and
// remove it again after confirming. What the page saved is read back from the server (settings.get). And a chat's
// profile picker stays open with its profiles while the settings change, even when the reload that follows fails (it
// used to empty itself on any failed profiles.list and unmount the open picker). Everything it changes is changed back;
// run's keepsSettings checks that it is.
//
//   node tests/NetPI.E2E/ui/settings.mjs --url http://127.0.0.1:7470 --token e2e-token --mock <url>
import { run, check, shot, expectEventually, openApp, openSession, rpc, send, STAMP, composerOf, PROBE } from './lib.mjs';

const QWEN = 'aiproxy/qwen3.8-27b';
const settings = async () => (await rpc('settings.get')).settings;

const PROFILE = `p-${STAMP}`;
const TITLE = `Profiles ${STAMP}`;
const session = await rpc('sessions.create', { title: TITLE });
await send(session.id, 'hello [s:echo]'); // a chat with no messages is not listed

await run('settings', async ({ page, socket }) => {
  await openApp(page);

  // ---- a chat's profile picker while the settings change
  await rpc('settings.set', { path: `profiles.${PROFILE}`, value: { name: `Profile ${STAMP}`, prompt: 'You are a careful reviewer.' } });
  try {
    await openSession(page, session.id, TITLE);
    const picker = page.getByTestId('profile-picker');
    await expectEventually('the composer has a profile picker once a profile exists', () => composerOf(page).getByRole('button', { name: 'Profile', exact: true }).count(), { pass: (n) => n === 1 });
    await composerOf(page).getByRole('button', { name: 'Profile', exact: true }).click();
    await expectEventually('it opens on the profiles', () => picker.getByTestId('profile-option').count(), { pass: (n) => n > 0 });
    const mark = socket.mark();
    const failing = socket.intercept((f) => f.t === 'rpc' && f.m === 'profiles.list', (f) => ({ t: 'res', id: f.id, e: { code: 'unavailable', message: 'failed by the test' } }));
    await rpc('settings.set', { path: 'agent.maxTurns', value: 301 }); // a settings change makes the picker load its list again
    await expectEventually('the change makes the picker reload, and the reload fails', () => socket.frames.slice(mark).some((f) => f.intercepted && f.m === 'profiles.list'));
    await page.evaluate(() => new Promise((r) => requestAnimationFrame(() => requestAnimationFrame(r)))); // the page applies the failed answer
    check('a failed reload leaves the picker open with its profiles', (await picker.getByTestId('profile-option').count()) > 0);
    failing();
    await rpc('settings.set', { path: 'agent.maxTurns', value: null });
    await page.keyboard.press('Escape');
    await shot(page, 'ui-settings-00-profile');
  } finally {
    await rpc('settings.set', { path: `profiles.${PROFILE}`, value: null });
  }
  await page.keyboard.press('Control+,');
  const dialog = page.getByRole('dialog', { name: 'Settings' });
  await expectEventually('Ctrl+, opens the settings', () => dialog.isVisible(), { stop: true, quiet: true });

  // ---- a number: saved, reset, a value out of range refused
  await dialog.locator('[data-page="Agents"]').click();
  const field = dialog.locator('[data-setting="agent.maxTurns"]');
  const input = field.getByRole('textbox');
  await input.fill('300');
  await input.press('Enter');
  await expectEventually('a changed number is saved', async () => (await settings()).agent?.maxTurns, { pass: (v) => v === 300 });
  await field.getByTestId('reset-setting').click();
  await expectEventually('Reset removes the setting, so the default applies again', async () => (await settings()).agent?.maxTurns, { pass: (v) => v === undefined });
  await input.fill('0');
  await input.press('Enter');
  await expectEventually('a value out of range is marked in the field', () => input.getAttribute('aria-invalid', PROBE), { pass: (v) => v === 'true' });
  check('…and not saved', (await settings()).agent?.maxTurns === undefined, JSON.stringify((await settings()).agent ?? {}));
  await input.fill('');
  await input.press('Enter');
  await shot(page, 'ui-settings-01-field');

  // ---- an agent: added from the model search, edited, removed
  const before = Object.keys((await settings()).agents ?? {});
  await dialog.locator('[data-page="agents"]').click();
  await dialog.getByTestId('add-agent').click();
  await page.locator(`[role="option"][data-model="${QWEN}"]`).click();
  const id = await expectEventually('choosing a model adds an agent on it', async () =>
    Object.entries((await settings()).agents ?? {}).find(([k, v]) => !before.includes(k) && v?.model === QWEN)?.[0], { stop: true });
  try {
    const agentDialog = page.getByRole('dialog', { name: `Agent ${id}` });
    await expectEventually('its dialog opens on the new agent', () => agentDialog.isVisible());
    await agentDialog.getByRole('textbox', { name: 'Instances' }).fill('1');
    await agentDialog.getByRole('textbox', { name: 'Instances' }).press('Tab'); // the field saves on change
    await expectEventually('its instances are saved', async () => (await settings()).agents?.[id]?.instances, { pass: (v) => v === 1 });
    await shot(page, 'ui-settings-02-agent');
    await agentDialog.getByTestId('remove-agent').click();
    await page.getByTestId('confirm').click();
    await expectEventually('removed after the confirm, it is gone from the settings', async () => Object.keys((await settings()).agents ?? {}), { pass: (keys) => !keys.includes(id) });
    await expectEventually('…and from the list', () => dialog.locator(`[data-agent="${id}"]`).count(), { pass: (n) => n === 0 });
  } finally {
    if (id) await rpc('settings.set', { path: `agents.${id}`, value: null });
  }
}, { keepsSettings: true, tap: true });
