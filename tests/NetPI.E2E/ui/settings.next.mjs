#!/usr/bin/env node
// Journey: a person changes their settings. In Settings → Runs they change a number, see it saved, reset it, and get a
// value out of range refused; in Agents & budget they add an agent from the model search, give it one instance, and
// remove it again after confirming. What the page saved is read back from the server (settings.get). Everything it
// changes is changed back; run's keepsSettings checks that it is.
//
//   node tests/NetPI.E2E/ui/settings.mjs --url http://127.0.0.1:7470 --token e2e-token --mock <url>
import { run, check, shot, expectEventually, openApp, rpc, PROBE } from './lib.mjs';

const QWEN = 'aiproxy/qwen3.8-27b';
const settings = async () => (await rpc('settings.get')).settings;

await run('settings', async ({ page }) => {
  await openApp(page);
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
}, { keepsSettings: true });
