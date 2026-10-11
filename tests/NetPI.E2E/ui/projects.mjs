#!/usr/bin/env node
// Projects in the UI against a REAL netpi-server (test "ui.projects", moved from the mock suite's sections "project picker +
// new session project", "start screen: the project new sessions start in", "projects dialog" and "projects: folder
// picker"). The start screen's split button itself is ui.start-project.
//
//   node tests/NetPI.E2E/ui/projects.mjs --url http://127.0.0.1:7470 --token e2e-token --work <dir> [--out dir]
import fs from 'node:fs';
import path from 'node:path';
import { run, check, shot, openApp, openSession, rpc, send, newDir, STAMP } from './lib.mjs';

const SITE = `site-${STAMP}`;
const API = `api-${STAMP}`;
const siteDir = newDir(SITE);
fs.writeFileSync(path.join(siteDir, 'AGENTS.md'), '# Site\n\nHow to work on the site.\n');
const site = await rpc('projects.create', { name: SITE, path: siteDir });
const api = await rpc('projects.create', { name: API, path: newDir(API) });
const pickRoot = newDir(`pick-${STAMP}`);
const WEB = `web-${STAMP}`;
fs.mkdirSync(path.join(pickRoot, WEB));
const MADE = `made-${STAMP}`;
const madeDir = newDir(MADE);
// a session with no messages is transient (not listed): the ones the test opens from the list get one
const scratch = await rpc('sessions.create', { title: `Scratch ${STAMP}` });
await send(scratch.id, 'hello [s:echo]');
const siteChat = await rpc('sessions.create', { title: `Site chat ${STAMP}`, projectId: site.id });
await send(siteChat.id, 'hello [s:echo]');

const projectOf = async (page) => {
  const id = await page.locator('.topbar .tab.active').getAttribute('data-tab').catch(() => null);
  // the server leaves a null projectId out of the JSON
  return id ? ((await rpc('sessions.get', { id })).projectId ?? null) : undefined;
};
const closeAllTabs = async (page) => {
  for (let i = 0; i < 30 && (await page.locator('.topbar .tab').count()); i++) {
    const id = await page.locator('.topbar .tab').first().getAttribute('data-tab');
    await page.locator(`.topbar .tab[data-tab="${id}"] .tab-close`).click();
    await page.locator(`.topbar .tab[data-tab="${id}"]`).waitFor({ state: 'detached', timeout: 10_000 });
  }
};
// A new chat opens in a tab of its own: wait for the active tab to be another one than before.
const newActiveTab = async (page, before) =>
  page.waitForFunction((b) => {
    const id = document.querySelector('.topbar .tab.active')?.getAttribute('data-tab');
    return id && id !== b;
  }, before, { timeout: 10_000 }).catch(() => {});
const activeTab = (page) => page.locator('.topbar .tab.active').getAttribute('data-tab').catch(() => null);

await run('projects', async ({ page }) => {
  try {
    await openApp(page);

    // ---- project picker + new session project: the picker lives in the composer bar, after the profile
    await openSession(page, `Scratch ${STAMP}`);
    const projBtn = page.locator('.composer button[aria-label="Project"]');
    const pick = async (name) => {
      await projBtn.click();
      await page.locator('.popover .item', { hasText: name }).first().click();
      await page.waitForFunction((n) => document.querySelector('.composer button[aria-label="Project"]')?.innerText.includes(n), name, { timeout: 5_000 }).catch(() => {});
    };
    await pick(SITE);
    check('project picker (composer) attaches the project', (await rpc('sessions.get', { id: scratch.id })).projectId === site.id);
    await page.locator('.notice', { hasText: SITE }).first().waitFor({ timeout: 5_000 }).catch(() => {});
    check('project picker (composer) posts the project notice', (await page.locator('.notice', { hasText: SITE }).count()) > 0);
    await pick(API);
    check('project picker (composer) changes the project again', (await rpc('sessions.get', { id: scratch.id })).projectId === api.id);
    await page.locator('.notice', { hasText: API }).first().waitFor({ timeout: 5_000 }).catch(() => {});
    check('project picker (composer) posts the second notice', (await page.locator('.notice', { hasText: API }).count()) > 0);
    check('the composer button shows the attached project', (await projBtn.innerText()).includes(API), await projBtn.innerText());
    check('no project chip left in the top bar or the chat header', (await page.locator('.topbar .chip, .header .chip').count()) === 0);
    // the plus button always starts a session without a project, even with a project session active
    let before = await activeTab(page);
    await page.locator('.panel.left .head button[title^="New session"]').click();
    await newActiveTab(page, before);
    check('new session from the plus button gets no project', (await projectOf(page)) === null, String(await projectOf(page)));
    // …and with no session open still none: the last project only preselects the pickers
    await closeAllTabs(page);
    await page.keyboard.press('Control+t');
    await newActiveTab(page, null);
    check('new session with no tab open gets no project', (await projectOf(page)) === null, String(await projectOf(page)));

    // ---- start screen: the project new sessions start in (the split button's halves are ui.start-project)
    await closeAllTabs(page);
    const chip = page.locator('.welcome .split .target');
    const more = page.locator('.welcome .split .target-more');
    await chip.waitFor({ timeout: 10_000 });
    await more.click();
    await page.locator('.popover .item', { hasText: SITE }).first().click();
    await newActiveTab(page, null);
    check('picking a project on the start screen starts a session in it', (await projectOf(page)) === site.id);
    await closeAllTabs(page);
    await chip.waitFor({ timeout: 10_000 });
    const last = await page.evaluate(() => JSON.parse(localStorage.getItem('netpi.lastProject') ?? 'null'));
    check('the last project is still remembered', last === site.id, String(last));
    check('start screen shows the project last worked in', (await chip.innerText()).includes(SITE), await chip.innerText());
    await page.locator('.welcome .np-btn-primary').click();
    await newActiveTab(page, null);
    check('the start screen plus button starts without a project', (await projectOf(page)) === null, String(await projectOf(page)));
    await closeAllTabs(page);
    await chip.waitFor({ timeout: 10_000 });
    await page.keyboard.press('Control+t');
    await newActiveTab(page, null);
    check('a new session with no tab open starts without a project', (await projectOf(page)) === null, String(await projectOf(page)));
    await closeAllTabs(page);
    await chip.waitFor({ timeout: 10_000 });
    // the chat just closed had no project, so the name half shows none: the chevron picks it again
    await more.click();
    await page.locator('.popover .item', { hasText: SITE }).first().click();
    await newActiveTab(page, null);
    check('the start screen recreates a session in the chosen project', (await projectOf(page)) === site.id);

    // ---- projects dialog, from the picker of that session
    await projBtn.click();
    await page.locator('.popover .manage').first().waitFor({ timeout: 5_000 });
    check('the picker offers to edit the attached project', (await page.locator('.popover .manage', { hasText: `Edit “${SITE}”` }).count()) === 1);
    await page.locator('.popover .manage', { hasText: 'Manage projects' }).click();
    await page.locator('.projects-dialog').waitFor({ timeout: 5_000 });
    check('Manage projects opens the projects dialog', (await page.locator('.projects-dialog .prow').count()) >= 2);
    // the dialog owns the keyboard: Tab stays in it, and the app behind it neither takes a click nor a shortcut
    await page.keyboard.press('Tab');
    check('Tab stays inside the open dialog', await page.evaluate(() => !!document.activeElement?.closest('.dialog')));
    check('the app behind the dialog is inert', await page.evaluate(() => document.querySelector('.app > .main')?.inert === true));
    const tabsBehind = await page.locator('.topbar .tab').count();
    await page.keyboard.press('Control+t');
    await page.waitForTimeout(300);
    check('Ctrl+T behind the dialog does not open a new chat', (await page.locator('.topbar .tab').count()) === tabsBehind);
    await shot(page, 'ui-projects-01-dialog');
    await page.locator('.projects-dialog .prow', { hasText: SITE }).locator('.pmain').click();
    await page.locator('.projects-dialog .frow').first().waitFor({ timeout: 5_000 }).catch(() => {});
    await page.locator('.projects-dialog .sess').first().waitFor({ timeout: 5_000 }).catch(() => {});
    check('the project view lists its sessions', (await page.locator('.projects-dialog .sess').count()) >= 1);
    check('the project view lists the instruction files', (await page.locator('.projects-dialog .frow').count()) >= 1);
    await shot(page, 'ui-projects-02-edit');
    const name = page.locator('.projects-dialog input.np-input').first();
    await name.fill(`${SITE}-renamed`);
    await page.locator('.projects-dialog .np-btn-primary', { hasText: 'Save' }).click();
    await page.waitForFunction((n) => document.querySelector('.composer button[aria-label="Project"]')?.innerText.includes(n), `${SITE}-renamed`, { timeout: 5_000 }).catch(() => {});
    check('Save renames the project', (await rpc('projects.list')).some((p) => p.name === `${SITE}-renamed`));
    check('the composer project button follows the rename', (await projBtn.innerText()).includes(`${SITE}-renamed`), await projBtn.innerText());
    await name.fill(SITE);
    await page.locator('.projects-dialog .np-btn-primary', { hasText: 'Save' }).click();
    await page.waitForFunction((n) => document.querySelector('.composer button[aria-label="Project"]')?.innerText.trim().endsWith(n), SITE, { timeout: 5_000 }).catch(() => {});
    await page.locator('.projects-dialog .backlink').click();
    check('Back returns to the list', (await page.locator('.projects-dialog .prow').count()) >= 2);
    // a confirm over the dialog: Esc closes only the confirm
    await page.locator('.projects-dialog .prow', { hasText: SITE }).locator('button[title="Remove"]').click();
    await page.locator('.dialog', { hasText: 'Remove project?' }).waitFor({ timeout: 5_000 });
    await page.keyboard.press('Escape');
    await page.waitForTimeout(200);
    check('Esc closes only the top dialog', (await page.locator('.dialog').count()) === 1 && (await page.locator('.projects-dialog').count()) === 1);
    await page.keyboard.press('Escape');
    await page.waitForTimeout(200);
    check('Esc then closes the projects dialog', (await page.locator('.projects-dialog').count()) === 0);
    check('the project was not removed', (await rpc('projects.list')).some((p) => p.id === site.id));
    // "New project…" in the attach picker: the new project is attached to the session
    await projBtn.click();
    await page.locator('.popover .manage', { hasText: 'New project' }).click();
    await page.locator('.projects-dialog').waitFor({ timeout: 5_000 });
    await page.locator('.projects-dialog .pathrow input').fill(madeDir);
    await page.locator('.projects-dialog .np-btn-primary', { hasText: 'Create project' }).click();
    await page.waitForFunction((n) => document.querySelector('.composer button[aria-label="Project"]')?.innerText.includes(n), MADE, { timeout: 5_000 }).catch(() => {});
    const made = (await rpc('projects.list')).find((p) => p.name === MADE);
    check('a project made from the picker is attached to the session', !!made && (await projectOf(page)) === made.id, `${made?.id} vs ${await projectOf(page)}`);
    check('the chip shows the new project', (await projBtn.innerText()).includes(MADE), await projBtn.innerText());

    // ---- projects: folder picker (fs.dirs) + add project
    const projectsTab = page.locator('.panel.left .strip-tab', { hasText: 'Projects' });
    await projectsTab.click();
    if (!(await page.locator('.panel.left.open').count())) await projectsTab.click();
    await page.locator('.panel.left .head .np-icon-btn[title="Add project"]').click();
    await page.locator('.projects-dialog').waitFor({ timeout: 5_000 });
    check('Add project opens the project dialog', (await page.locator('.projects-dialog .head .title').innerText()) === 'New project');
    await page.locator('.projects-dialog .pathrow .np-btn').first().click();
    await page.locator('.picker .bar input').waitFor({ timeout: 5_000 });
    await page.keyboard.press('Escape');
    await page.waitForTimeout(200);
    check('Esc closes the folder picker, not the project dialog', (await page.locator('.picker').count()) === 0 && (await page.locator('.projects-dialog').count()) === 1);
    await page.locator('.projects-dialog .pathrow .np-btn').first().click();
    await page.locator('.picker .bar input').waitFor({ timeout: 5_000 });
    // the picker opens on a folder it lists first, and that answer sets the path field: typing before it lands is overwritten
    await page.waitForFunction(() => document.querySelector('.picker .bar input')?.value, null, { timeout: 5_000 });
    await page.locator('.picker .bar input').fill(pickRoot);
    await page.locator('.picker .bar input').press('Enter');
    await page.locator('.picker .dir', { hasText: WEB }).first().click();
    await shot(page, 'ui-projects-03-folder-picker');
    await page.locator('.dialog .np-btn-primary', { hasText: 'Select folder' }).click();
    const picked = await page.locator('.projects-dialog .pathrow input').inputValue();
    check('folder picker fills the path', picked.endsWith(`/${WEB}`) || picked.endsWith(`\\${WEB}`), picked);
    check('the folder name becomes the project name', (await page.locator('.projects-dialog input.np-input:not(.np-mono)').inputValue()) === WEB);
    await page.locator('.projects-dialog .np-btn-primary', { hasText: 'Create project' }).click();
    await page.locator('.panel.left .prow .pname', { hasText: WEB }).first().waitFor({ timeout: 5_000 }).catch(() => {});
    check('project added', (await page.locator('.panel.left .prow .pname', { hasText: WEB }).count()) > 0);
    check('the project dialog closed', (await page.locator('.projects-dialog').count()) === 0);
  } finally {
    // a shared server keeps what a test made: take it out again
    for (const p of await rpc('projects.list'))
      if (p.name.endsWith(STAMP) || p.name === `${SITE}-renamed`) await rpc('projects.delete', { id: p.id }).catch(() => {});
    for (const s of [scratch, siteChat]) await rpc('sessions.delete', { id: s.id }).catch(() => {});
  }
}, { viewport: { width: 1600, height: 1000 }, freshTabs: true });
