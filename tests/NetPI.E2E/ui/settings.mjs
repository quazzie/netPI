#!/usr/bin/env node
// Settings in the UI against a REAL netpi-server (test "ui.settings", moved from the mock suite's sections "settings:
// controls, agents, budget, tools", "chat tools: switched per session" and "profiles: settings, a project default, per
// chat"). Everything it changes it changes back; the settings guard checks that it did.
//
//   node tests/NetPI.E2E/ui/settings.mjs --url http://127.0.0.1:7470 --token e2e-token --work <dir> [--out dir]
import fs from 'node:fs';
import path from 'node:path';
import { run, check, shot, openApp, openSession, rpc, send, newDir, STAMP } from './lib.mjs';

const settings = async () => (await rpc('settings.get')).settings;
const QWEN = `q-${STAMP}`;
const GEMMA = `g-${STAMP}`;
const SONNET = `sonnet-${STAMP}`;
const PROFILE = `admin-${STAMP}`;

// a project with a skill and a broken one (no frontmatter: an error the dialog lists)
const projDir = newDir(`prof-${STAMP}`);
const skill = (name, text) => {
  fs.mkdirSync(path.join(projDir, '.netpi', 'skills', name), { recursive: true });
  fs.writeFileSync(path.join(projDir, '.netpi', 'skills', name, 'SKILL.md'), text);
};
skill('svelte-tab', '---\nname: svelte-tab\ndescription: Build a plugin tab in Svelte.\n---\n\nSteps.\n');
skill('broken', 'No frontmatter here.\n');
const project = await rpc('projects.create', { name: `prof-${STAMP}`, path: projDir });
const projChat = await rpc('sessions.create', { title: `Profile chat ${STAMP}`, projectId: project.id });
await send(projChat.id, 'hello [s:echo]');
const made = [projChat.id];

const activeId = (page) => page.locator('.topbar .tab.active').getAttribute('data-tab').catch(() => null);
const closeAllTabs = async (page) => {
  for (let i = 0; i < 30 && (await page.locator('.topbar .tab').count()); i++) {
    const id = await page.locator('.topbar .tab').first().getAttribute('data-tab');
    await page.locator(`.topbar .tab[data-tab="${id}"] .tab-close`).click();
    await page.locator(`.topbar .tab[data-tab="${id}"]`).waitFor({ state: 'detached', timeout: 10_000 });
  }
};
const idle = (page) => page.waitForFunction(() => !document.querySelector('.composer.running'), null, { timeout: 60_000 });
const sendInComposer = async (page, text) => {
  await page.locator('.composer textarea').fill(text);
  await page.locator('.composer textarea').press('Enter');
  await page.locator('.composer.running').waitFor({ timeout: 5_000 }).catch(() => {});
  await idle(page);
};

await run('settings', async ({ page }) => {
  // two agents of our own: one whose model is loaded, one whose model is not
  await rpc('settings.set', { path: `agents.${QWEN}`, value: { model: 'aiproxy/qwen3.8-27b' } });
  await rpc('settings.set', { path: `agents.${GEMMA}`, value: { model: 'aiproxy/gemma-4' } });
  try {
    await openApp(page);

    // ---- settings: controls, agents, budget, tools
    await page.keyboard.press('Control+,');
    await page.locator('.dialog').first().waitFor({ timeout: 5_000 });
    await page.locator('.nav button', { hasText: 'Runs' }).click();
    const turnsField = page.locator('.field', { hasText: 'Model calls per run' });
    const turns = turnsField.locator('input');
    check('an unset setting shows its default', (await turns.getAttribute('placeholder')) === '200', await turns.getAttribute('placeholder'));
    await turns.fill('300');
    await turns.press('Enter');
    await page.waitForTimeout(300);
    check('a changed field is saved (settings.set)', (await settings()).agent?.maxTurns === 300);
    await turnsField.locator('.reset').click();
    await page.waitForTimeout(300);
    check('Reset removes the setting again', (await settings()).agent?.maxTurns === undefined);
    await turns.fill('0');
    await turns.press('Enter');
    await page.waitForTimeout(300);
    check('a number out of range is refused, not saved', (await turnsField.locator('.bad-msg').count()) === 1 && (await settings()).agent?.maxTurns === undefined);

    await page.locator('.nav button', { hasText: 'Agents & budget' }).click();
    await page.locator(`.setting-row[data-agent="${QWEN}"]`).waitFor({ timeout: 5_000 }).catch(() => {});
    check('the agents are rows, an inactive one says so', (await page.locator(`.setting-row[data-agent="${QWEN}"]`).count()) === 1
      && /not loaded/.test(await page.locator(`.setting-row[data-agent="${GEMMA}"]`).innerText()));
    // Add agent: the searchable model list, then the agent's own dialog
    await page.locator('.dialog .agents .add button', { hasText: 'Add agent' }).click();
    await page.locator('.popover input[aria-label="Filter models"]').fill('sonnet');
    await page.keyboard.press('Enter');
    const agentDialog = page.locator('.dialog.agent-dialog');
    await agentDialog.waitFor({ timeout: 5_000 }).catch(() => {});
    // The Anthropic API publishes no prices (a catalog price comes from a model's `pricing`, as OpenRouter's has): the
    // dialog says so, and a price set in it is the agent's
    const facts = async () => (await agentDialog.locator('.facts').innerText().catch(() => '')).replace(/\s+/g, ' ');
    check('an agent is added from the model search and opens in its dialog; a model with no catalog price says so', (await agentDialog.count()) === 1 && (await facts()).includes('price unknown'), await facts());
    const addedKey = Object.keys((await settings()).agents ?? {}).find((k) => k.includes('sonnet') && k !== SONNET);
    await agentDialog.locator('input[aria-label="Input price"]').fill('3');
    await agentDialog.locator('input[aria-label="Input price"]').press('Tab');
    await agentDialog.locator('input[aria-label="Output price"]').fill('15');
    await agentDialog.locator('input[aria-label="Output price"]').press('Tab');
    await page.waitForFunction(() => document.querySelector('.dialog.agent-dialog .facts')?.innerText.includes('per Mtok'), null, { timeout: 5_000 }).catch(() => {});
    const cost = (await settings()).agents?.[addedKey]?.cost;
    check('a price set in the dialog is saved and shown', cost?.input === 3 && cost?.output === 15 && (await facts()).includes('$3 / $15 per Mtok'), `${JSON.stringify(cost)} ${await facts()}`);
    await agentDialog.locator('textarea.use').fill('Costs money: only for hard problems.');
    await agentDialog.locator('textarea.use').blur();
    await page.waitForTimeout(300);
    check('the agent note is saved', (await settings()).agents?.[addedKey]?.use === 'Costs money: only for hard problems.', addedKey);
    await agentDialog.locator('button[aria-label="Model"]').click();
    await page.locator('.popover input[aria-label="Filter models"]').waitFor({ timeout: 5_000 });
    await shot(page, 'ui-settings-01-agent-dialog');
    await page.keyboard.press('Escape');
    await page.waitForTimeout(200);
    check('Esc closes the model list, not the agent dialog', (await agentDialog.count()) === 1 && (await page.locator('.popover input[aria-label="Filter models"]').count()) === 0);
    // switched off in the dialog, then a new name
    await agentDialog.locator('input.np-switch').click();
    await page.waitForTimeout(300);
    check('the dialog switches an agent off', (await settings()).agents?.[addedKey]?.disabled === true);
    await agentDialog.locator('input.np-switch').click();
    await page.waitForTimeout(300);
    const nameInput = agentDialog.locator('input[aria-label="Name"]');
    await nameInput.fill(SONNET);
    await nameInput.press('Enter');
    await page.waitForTimeout(400);
    const agentsNow = (await settings()).agents ?? {};
    check('a new name moves the agent', !!agentsNow[SONNET] && !agentsNow[addedKey] && agentsNow[SONNET].use === 'Costs money: only for hard problems.' && !agentsNow[SONNET].disabled,
      Object.keys(agentsNow).join(', '));
    check('the dialog follows the new name', (await agentDialog.innerText()).includes(SONNET));
    await agentDialog.locator('.foot button', { hasText: 'Done' }).click();
    await page.waitForTimeout(200);
    const agentRow = page.locator(`.setting-row[data-agent="${SONNET}"]`);
    const rowText = await agentRow.innerText().catch(() => '');
    check('the agent is one row: model, instances, price', (await agentRow.count()) === 1 && /1 instance/.test(rowText) && rowText.includes('$3 / $15'), rowText);
    const monthly = page.locator('.field', { hasText: 'Monthly budget' }).locator('input');
    await monthly.fill('50');
    await monthly.press('Enter');
    await page.waitForTimeout(400);
    check('the budget shows this month against the limit', (await page.locator('.dialog .budget').innerText()).includes('of $50'), await page.locator('.dialog .budget').innerText());
    await shot(page, 'ui-settings-02-agents');

    // Models: one row per provider, its options in a dialog
    await page.locator('.nav button', { hasText: 'Models' }).click();
    await page.waitForTimeout(200);
    check('a page shows only its own settings (not the About details)', (await page.locator('.dialog .about').count()) === 0);
    check('providers are rows', (await page.locator('.setting-row[data-section="aiproxy"]').count()) === 1 && (await page.locator('.setting-row[data-section="openrouter"]').count()) === 1);
    await page.locator('.setting-row[data-section="openrouter"]').click();
    const sectionDialog = page.locator('.dialog.section-dialog');
    await sectionDialog.waitFor({ timeout: 5_000 }).catch(() => {});
    check('a provider row opens its options', (await sectionDialog.locator('.field', { hasText: 'API key' }).count()) === 1);
    await page.keyboard.press('Escape');
    await page.waitForTimeout(200);
    check('Esc closes only the provider dialog', (await sectionDialog.count()) === 0 && (await page.locator('.dialog').count()) === 1);

    // Context: the real built-in texts, not "built in"
    await page.locator('.nav button', { hasText: 'Context' }).click();
    const identity = page.locator('.field', { hasText: 'Identity' }).locator('textarea');
    await identity.waitFor({ timeout: 5_000 });
    check('the identity field shows the built-in text', (await identity.inputValue()).startsWith('You are a coding agent running in NetPI'), (await identity.inputValue()).slice(0, 60));
    check('no field says "built in"', !(await page.locator('.dialog .layout > .content').innerText()).toLowerCase().includes('built in'));
    await identity.fill('You are a careful engineer.');
    await identity.blur();
    await page.waitForTimeout(300);
    check('an edited built-in text is saved', (await settings()).context?.customPrompt === 'You are a careful engineer.');
    await page.locator('.field', { hasText: 'Identity' }).locator('.reset').click();
    await page.waitForTimeout(300);
    check('Reset brings the built-in text back', (await settings()).context?.customPrompt === undefined && (await identity.inputValue()).startsWith('You are a coding agent'));

    await page.locator('.nav').getByRole('button', { name: 'Tools', exact: true }).click();
    await page.locator('.setting-row[data-section="shell"]').click();
    await sectionDialog.waitFor({ timeout: 5_000 }).catch(() => {});
    check('a tool row opens its options', (await sectionDialog.locator('.field', { hasText: 'Default timeout' }).count()) === 1);
    const bashPath = (await sectionDialog.locator('.field', { hasText: 'bash' }).first().locator('input').getAttribute('placeholder')) ?? '';
    check('found paths show as the real path', bashPath.includes('bash'), bashPath);
    await page.keyboard.press('Escape');
    await page.waitForTimeout(200);
    check('the Tools page has the tool options, not the plugin switches', (await page.locator('.plugin').count()) === 0);
    await page.locator('.nav button', { hasText: 'Plugins' }).click();
    const shell = page.locator('.plugin[data-plugin="netpi.tools.shell"]');
    await shell.waitFor({ timeout: 5_000 }).catch(() => {});
    check('each plugin lists the tools it brings', (await shell.count()) === 1 && (await shell.locator('.ptools').innerText()).includes('bash'));
    check('no global switch per tool any more', (await page.getByRole('checkbox', { name: 'bash', exact: true }).count()) === 0);
    await shot(page, 'ui-settings-03-plugins');
    await page.keyboard.press('Escape');
    await page.waitForTimeout(200);

    // ---- chat tools: switched per session
    await page.keyboard.press('Control+t');
    await page.waitForFunction(() => document.querySelector('.topbar .tab.active')?.getAttribute('data-tab'), null, { timeout: 10_000 });
    const sid = await activeId(page);
    made.push(sid);
    const btn = page.locator('button[aria-label="Tools for this chat"]');
    await btn.click();
    await page.waitForFunction(() => document.querySelector('.tools-pop .help')?.innerText.trim(), null, { timeout: 5_000 }).catch(() => {});
    check('a new chat: the menu says what switching off does', (await page.locator('.tools-pop .help').innerText()).includes('not sent to the agent'));
    await page.locator('.tools-pop').getByRole('checkbox', { name: 'bash', exact: true }).uncheck();
    await page.waitForTimeout(300);
    check('a tool switched off is saved for this chat', ((await rpc('agent.tools', { sessionId: sid })).off ?? []).includes('bash'));
    await page.waitForFunction(() => document.querySelector('button[aria-label="Tools for this chat"]')?.innerText.includes('off'), null, { timeout: 5_000 }).catch(() => {});
    check('the button counts the tools that are off', (await btn.innerText()).includes('1 off'), await btn.innerText());
    await shot(page, 'ui-settings-04-chat-tools');
    await page.keyboard.press('Escape');
    await page.waitForTimeout(200);
    await sendInComposer(page, 'hello [s:echo]');
    await btn.click();
    await page.waitForFunction(() => document.querySelector('.tools-pop .help')?.innerText.includes('re-reads this chat'), null, { timeout: 5_000 }).catch(() => {});
    check('a started chat: the menu says a change re-reads the chat', (await page.locator('.tools-pop .help').innerText()).includes('re-reads this chat'));
    await page.locator('.tools-pop .foot button', { hasText: 'All on' }).click();
    await page.waitForTimeout(300);
    check('All on switches them back', ((await rpc('agent.tools', { sessionId: sid })).off ?? []).length === 0 && !(await btn.innerText()).includes('off'));
    await page.keyboard.press('Escape');

    // ---- profiles: settings, a project default, per chat
    await page.keyboard.press('Control+,');
    await page.locator('.nav button', { hasText: 'Profiles' }).click();
    await page.locator('.profiles .add input').fill(PROFILE);
    await page.locator('.profiles .add button').click();
    const profileDialog = page.locator('.dialog.profile-dialog');
    await profileDialog.waitFor({ timeout: 5_000 }).catch(() => {});
    check('a profile is added by its name and opens in its dialog', (await profileDialog.count()) === 1);
    const prompt = profileDialog.locator('textarea.prompt');
    check('its instructions start as the real opening text', (await prompt.inputValue()).startsWith('You are a coding agent running in NetPI'));
    await prompt.fill('You are a system administrator.');
    await prompt.blur();
    await profileDialog.getByRole('checkbox', { name: 'bash', exact: true }).uncheck();
    await page.waitForTimeout(400);
    const saved = (await settings()).profiles?.[PROFILE];
    check('its instructions and switched-off tools are saved', saved?.prompt === 'You are a system administrator.' && (saved?.toolsOff ?? []).includes('bash'), JSON.stringify(saved));
    await shot(page, 'ui-settings-05-profile-dialog');
    await profileDialog.locator('.foot button', { hasText: 'Done' }).click();
    await page.waitForTimeout(200);
    const profileRow = page.locator(`.setting-row[data-profile="${PROFILE}"]`);
    const profileText = await profileRow.innerText().catch(() => '');
    check('the profile is one row: its opening line and its tools', (await profileRow.count()) === 1 && profileText.includes('You are a system administrator.') && /\d+ of \d+ tools/.test(profileText), profileText);
    await page.keyboard.press('Escape');
    await page.waitForTimeout(200);

    // a profile chosen before the first message shows on the button: such a chat is not announced (no session.updated),
    // so the picker follows what profiles.apply answers
    await page.keyboard.press('Control+t');
    await page.waitForFunction(() => document.querySelector('.topbar .tab.active')?.getAttribute('data-tab'), null, { timeout: 10_000 });
    made.push(await activeId(page));
    await page.locator('button[aria-label="Profile"]').click();
    await page.locator('.profile-pop .opt', { hasText: PROFILE }).click();
    await page.waitForFunction((p) => document.querySelector('button[aria-label="Profile"]')?.innerText.includes(p), PROFILE, { timeout: 5_000 }).catch(() => {});
    check('a profile chosen before the first message shows on the button', (await page.locator('button[aria-label="Profile"]').innerText()).includes(PROFILE), await page.locator('button[aria-label="Profile"]').innerText());

    // the project's default, from the projects dialog
    await openSession(page, `Profile chat ${STAMP}`);
    await page.locator('.composer button[aria-label="Project"]').click();
    await page.locator('.popover .manage', { hasText: `Edit “prof-${STAMP}”` }).click();
    await page.locator('.projects-dialog').waitFor({ timeout: 5_000 });
    await page.locator('.projects-dialog .skname').first().waitFor({ timeout: 5_000 }).catch(() => {});
    const projectSkills = await page.locator('.projects-dialog .skname').allInnerTexts();
    check('the project dialog lists its skills and their problems', projectSkills[0] === 'svelte-tab' && (await page.locator('.projects-dialog .problem').count()) === 1,
      `${projectSkills.join(' ')} / ${await page.locator('.projects-dialog .problem').count()} problems`);
    const select = page.getByRole('combobox', { name: 'Profile of new sessions' });
    await select.waitFor({ timeout: 5_000 }).catch(() => {});
    await select.selectOption(PROFILE);
    await page.waitForTimeout(300);
    const meta = (await rpc('projects.list')).find((p) => p.id === project.id)?.meta;
    check('a project gets a default profile', meta?.profile === PROFILE, JSON.stringify(meta));
    await page.keyboard.press('Escape');
    await page.waitForTimeout(200);

    // a new chat in the project (the start screen's chevron starts one there): no profile until its first message, when the
    // project's default comes with it; the picker is free before that
    await closeAllTabs(page);
    await page.locator('.welcome .target-more').click();
    await page.locator('.popover .item', { hasText: `prof-${STAMP}` }).first().click();
    await page.waitForFunction(() => document.querySelector('.topbar .tab.active')?.getAttribute('data-tab'), null, { timeout: 10_000 });
    const pid = await activeId(page);
    made.push(pid);
    const picker = page.locator('button[aria-label="Profile"]');
    let s = await rpc('sessions.get', { id: pid });
    check('an empty chat has no profile yet (the default comes with its first message)', s.meta?.profile == null && (await picker.innerText()).includes('No profile'), await picker.innerText());
    await picker.click();
    await page.locator('.profile-pop').waitFor({ timeout: 5_000 });
    check('before the first message the change is free', (await page.locator('.profile-pop .help').innerText()).includes('free'));
    await page.keyboard.press('Escape');
    await page.waitForTimeout(200);
    await sendInComposer(page, 'hello [s:echo]');
    s = await rpc('sessions.get', { id: pid });
    check('the first message gets the default profile of the project', s.meta?.profile === PROFILE && (s.meta?.toolsOff ?? []).includes('bash'), `${s.meta?.profile} ${s.meta?.toolsOff}`);
    check('the tools button shows what the profile switched off', (await page.locator('button[aria-label="Tools for this chat"]').innerText()).includes('1 off'));
    await picker.click();
    await page.locator('.profile-pop').waitFor({ timeout: 5_000 });
    check('in a started chat the picker says the chat is read again', (await page.locator('.profile-pop .help').innerText()).includes('re-reads this chat'));
    await shot(page, 'ui-settings-06-chat-profile');
    await page.locator('.profile-pop .opt', { hasText: 'No profile' }).click();
    await page.locator('.notice', { hasText: 'profile away' }).first().waitFor({ timeout: 5_000 }).catch(() => {});
    check('the switch is announced in the chat', (await page.locator('.notice', { hasText: 'profile away' }).count()) === 1);
  } finally {
    for (const k of [QWEN, GEMMA, SONNET]) await rpc('settings.set', { path: `agents.${k}`, value: null }).catch(() => {});
    for (const k of Object.keys((await settings()).agents ?? {})) if (k.includes('sonnet') && !k.startsWith('sonnet-')) await rpc('settings.set', { path: `agents.${k}`, value: null }).catch(() => {});
    await rpc('settings.set', { path: 'budget.monthlyUsd', value: null }).catch(() => {});
    await rpc('settings.set', { path: `profiles.${PROFILE}`, value: null }).catch(() => {});
    await rpc('projects.delete', { id: project.id }).catch(() => {});
    for (const id of made) if (id) await rpc('sessions.delete', { id }).catch(() => {});
  }
}, { viewport: { width: 1600, height: 1000 }, freshTabs: true, keepsSettings: true });
