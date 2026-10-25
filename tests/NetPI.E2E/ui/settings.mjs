#!/usr/bin/env node
// Journey: a person changes their settings. In Settings → Runs they open Run limits in place, change a number, see it
// saved, reset it, get a value out of range refused, and go back; they find the same setting with the search, change it in
// the results, see it under Changed and reset it there; the command palette opens the dialog on another setting, in its
// section; Back (the arrow, Alt+Left, the mouse back button) walks back through pages, sections and a search. In Agents & budget they add an agent from the model search, give it one instance, and
// remove it again after confirming. While it is switched off, the composer's agent picker hides it ("Any available"
// stays), and switching it on brings it back. What the page saved is read back from the server (settings.get). And a
// chat's profile picker stays open with its profiles while the settings change, even when the reload that follows fails
// (it used to empty itself on any failed profiles.list and unmount the open picker). Everything it changes is changed
// back; run's keepsSettings checks that it is.
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

  // ---- a number in its section, opened in place: saved, reset, a value out of range refused
  await dialog.locator('[data-page="runs"]').click();
  await dialog.locator('[data-section-row="agents"]').click();
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
  await dialog.getByTestId('settings-back').click();
  await expectEventually('Back leaves the section for its page', () => dialog.locator('[data-section-row="agents"]').count(), { pass: (n) => n === 1 });

  // ---- Back walks the way it came: pages, sections, a search; the arrow, Alt+Left and the mouse's back button
  const onRuns = () => dialog.locator('[data-section-row="agents"]').count();
  const onTools = () => dialog.getByTestId('tool-exposure-row').count();
  await dialog.locator('[data-page="tools"]').click();
  await dialog.locator('[data-section-row="web"]').click();
  await expectEventually('a section of another page opens in place', () => dialog.locator('[data-setting="web.search.count"]').count(), { pass: (n) => n === 1 });
  await dialog.getByTestId('settings-back').click();
  await expectEventually('the arrow goes back to the page it came from', onTools, { pass: (n) => n === 1 });
  await page.keyboard.press('Alt+ArrowLeft');
  await expectEventually('Alt+Left goes back to the page before that', onRuns, { pass: (n) => n === 1 });
  await dialog.getByTestId('settings-search').fill('fetch timeout');
  await expectEventually('a search', () => dialog.getByTestId('settings-results').locator('[data-setting="web.fetch.timeoutSeconds"]').count(), { pass: (n) => n === 1 });
  await dialog.getByTestId('settings-results').getByRole('button', { name: /Web › Fetch/ }).click();
  await expectEventually('its breadcrumb opens the section', () => dialog.getByTestId('settings-results').count(), { pass: (n) => n === 0 });
  await dialog.getByTestId('settings-content').dispatchEvent('mouseup', { button: 3 });
  await expectEventually("the mouse's back button returns to the results", () => dialog.getByTestId('settings-results').locator('[data-setting="web.fetch.timeoutSeconds"]').count(), { pass: (n) => n === 1 });
  await dialog.getByTestId('settings-back').click();
  await expectEventually('…and from the search to where it started', onRuns, { pass: (n) => n === 1 });
  check('the search is cleared', (await dialog.getByTestId('settings-search').inputValue()) === '');

  // ---- the search: found by a word of its label, changed in the results, listed under Changed, reset there
  const search = dialog.getByTestId('settings-search');
  const results = dialog.getByTestId('settings-results');
  await search.fill('model calls');
  const hit = results.locator('[data-setting="agent.maxTurns"]');
  await expectEventually('the search finds the setting by its label', () => hit.count(), { pass: (n) => n === 1 });
  await hit.getByRole('textbox').fill('250');
  await hit.getByRole('textbox').press('Enter');
  await expectEventually('a change in the results is saved', async () => (await settings()).agent?.maxTurns, { pass: (v) => v === 250 });
  await search.fill('');
  await dialog.getByTestId('filter-changed').click();
  const changed = results.locator('[data-setting="agent.maxTurns"]');
  await expectEventually('Changed lists it', () => changed.count(), { pass: (n) => n === 1 });
  await shot(page, 'ui-settings-01b-changed');
  await changed.getByTestId('reset-setting').click();
  await expectEventually('…and resets it', async () => (await settings()).agent?.maxTurns, { pass: (v) => v === undefined });
  await dialog.getByTestId('filter-changed').click();

  // ---- the command palette opens the dialog on a setting, in its section
  await page.keyboard.press('Escape');
  await expectEventually('Esc closes the settings', () => dialog.count(), { pass: (n) => n === 0 });
  await page.keyboard.press('Control+k');
  const palette = page.getByRole('dialog', { name: 'Command palette' });
  await expectEventually('Ctrl+K opens the palette', () => palette.isVisible(), { stop: true, quiet: true });
  await palette.getByRole('textbox').fill('Silence between tokens');
  // its settings come with settings.schema, which the palette asks for when it opens
  const option = palette.getByRole('button', { name: /^Silence between tokens/ });
  await expectEventually('the palette lists the setting by its label', () => option.count(), { pass: (n) => n === 1 });
  await option.click();
  await expectEventually('the palette opens the settings on that field', () => dialog.locator('[data-setting="retry.stallTimeoutSeconds"]').count(), { pass: (n) => n === 1 });
  check('…in its section, opened in place', (await dialog.getByTestId('settings-back').count()) === 1);

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

    // ---- the composer's agent picker hides the agent while it is switched off
    await page.keyboard.press('Escape'); // the agent dialog, then the settings behind it
    await page.keyboard.press('Escape');
    const agentPick = composerOf(page).getByRole('button', { name: 'Agent', exact: true });
    await agentPick.click();
    // scoped to the picker's popover: the Work tab (and the settings' agents page) list agents with data-agent too
    const pop = page.getByTestId('agent-picker');
    const pickRow = () => pop.locator(`[data-agent="${id}"]`).count();
    const serverDisabled = () => rpc('agents.list').then((l) => (l ?? []).find((a) => a.key === id)?.disabled);
    await expectEventually("the new agent is in the composer's agent picker", pickRow, { pass: (n) => n === 1 });
    await rpc('agents.setEnabled', { id, enabled: false });
    // the server side first: quick off/on toggles coalesce into one agents.changed, and the UI may not see the middle
    await expectEventually('the server has the agent off', serverDisabled, { pass: (v) => v === true });
    await expectEventually('switching it off takes it out of the picker', pickRow, { pass: (n) => n === 0 });
    check('“Any available” stays while every agent is hidden', (await pop.locator('[data-agent="any"]').count()) === 1);
    await shot(page, 'ui-settings-03-picker-hidden');
    await rpc('agents.setEnabled', { id, enabled: true });
    await expectEventually('the server has the agent on', serverDisabled, { pass: (v) => v === false });
    await expectEventually('switching it on brings it back', pickRow, { pass: (n) => n === 1 });
    await page.keyboard.press('Escape'); // close the picker
    await page.keyboard.press('Control+,'); // the dialog went with the earlier Escapes: back into the settings
    await expectEventually('the settings dialog is open again', () => dialog.isVisible());
    await dialog.locator('[data-page="agents"]').click();
    await dialog.locator(`[data-agent="${id}"]`).click(); // the row opens its dialog
    await expectEventually("the agent's row opens its dialog again", () => page.getByRole('dialog', { name: `Agent ${id}` }).count(), { pass: (n) => n === 1 });

    await agentDialog.getByTestId('remove-agent').click();
    await page.getByTestId('confirm').click();
    await expectEventually('removed after the confirm, it is gone from the settings', async () => Object.keys((await settings()).agents ?? {}), { pass: (keys) => !keys.includes(id) });
    await expectEventually('…and from the list', () => dialog.locator(`[data-agent="${id}"]`).count(), { pass: (n) => n === 0 });
  } finally {
    if (id) await rpc('settings.set', { path: `agents.${id}`, value: null });
  }

  // ---- a number field on a paid model: a comma decimal saves, a refusal is marked and reverts
  // (the QWEN agent is on a local model, whose price/cap fields are hidden; the mock catalog's paid model shows them)
  const PAID = 'anthropic/claude-haiku-4-5';
  await dialog.getByTestId('add-agent').click();
  await page.locator(`[role="option"][data-model="${PAID}"]`).click();
  const paid = await expectEventually('choosing a paid model adds an agent on it', async () =>
    Object.entries((await settings()).agents ?? {}).find(([k, v]) => !before.includes(k) && v?.model === PAID)?.[0], { stop: true });
  try {
    const paidDialog = page.getByRole('dialog', { name: `Agent ${paid}` });
    await expectEventually('its dialog opens on the paid agent', () => paidDialog.isVisible());
    const cap = paidDialog.getByRole('textbox', { name: 'Daily cap' });
    await cap.fill('0,5');
    await cap.press('Tab'); // the field saves on change
    await expectEventually('a comma decimal is saved as a number', async () => (await settings()).agents?.[paid]?.budget?.limitUsd, { pass: (v) => v === 0.5 });
    await expectEventually('…and the field shows it', () => cap.inputValue(), { pass: (v) => v === '0.5' });
    await cap.fill('abc');
    await cap.press('Tab');
    await expectEventually('a refused value is marked in the field', () => cap.getAttribute('aria-invalid', PROBE), { pass: (v) => v === 'true' });
    check('…it says why, and the cap keeps its value', (await paidDialog.getByText('Must be a number.').count()) === 1 && (await settings()).agents?.[paid]?.budget?.limitUsd === 0.5);
    await shot(page, 'ui-settings-02b-cap');
    await paidDialog.getByRole('button', { name: 'Done' }).click();
    await expectEventually('…and is gone with the dialog', () => paidDialog.count(), { pass: (n) => n === 0 });
    await dialog.locator(`[data-agent="${paid}"]`).click(); // the row opens its dialog
    await expectEventually('reopened, the cap reads its saved value', () => paidDialog.getByRole('textbox', { name: 'Daily cap' }).inputValue(), { pass: (v) => v === '0.5' });
    await paidDialog.getByTestId('remove-agent').click();
    await page.getByTestId('confirm').click();
    await expectEventually('removed after the confirm, it is gone from the settings', async () => Object.keys((await settings()).agents ?? {}), { pass: (keys) => !keys.includes(paid) });
  } finally {
    if (paid) await rpc('settings.set', { path: `agents.${paid}`, value: null });
  }
}, { keepsSettings: true, tap: true });
