#!/usr/bin/env node
// UI test: the Work tab (right panel) — the agents are one list at the top (no Agents/Runs sections), the "Model capacity",
// "Physical owners" and "Idea checks" sections are gone, and clicking an agent's name opens Settings → Agents with that
// agent's dialog on top.
//
// The agent is seeded by the C# test (ui.work-tab) before this script runs.
//
//   node tests/NetPI.E2E/ui/work-tab.mjs --url http://127.0.0.1:7470 --token e2e-token --agent e2e-wt-abc123
// Prints one JSON line at the end: { ok, checks: [{name, ok, detail}], errors: [...] }.
import { run, arg, check, shot, openApp } from './lib.mjs';

const AGENT = arg('agent', 'e2e-wt');

await run('work-tab', async ({ page }) => {
  await openApp(page);

  // open the Work tab in the right panel (toggle it open when it is the strip's selection but the panel is shut)
  const tab = page.locator('.panel.right .strip-tab', { hasText: 'Work' });
  if ((await tab.getAttribute('aria-selected')) !== 'true') await tab.click();
  if (!(await page.locator('.panel.right.open').count())) await tab.click();

  // the agents are the block at the top; the other sections are titled spans (a title attribute each)
  await page.waitForSelector('.panel.right .work .pool', { timeout: 15_000 });
  await page.waitForTimeout(500);
  check('the Work tab has no Model capacity section', (await page.locator('.panel.right .np-section-label[title="Model capacity"]').count()) === 0);
  check('the Work tab has no Physical owners section', (await page.locator('.panel.right .np-section-label[title="Physical owners"]').count()) === 0);
  // the section that used to be here moved to the Diagnostics tab's Ideas view: it must not render on the Work tab
  await page.waitForTimeout(500);
  check('the Work tab has no Idea checks section', (await page.locator('.panel.right .np-section-label[title="Idea checks"]').count()) === 0);
  const labelTitles = await page.locator('.panel.right .np-section-label').evaluateAll((els) => els.map((e) => e.getAttribute('title')));
  check('the Work tab has Background and Finished, and no Agents, Runs, Processes or Usage today section (usage is on each agent)', ['Background', 'Finished'].every((t) => labelTitles.includes(t)) && ['Agents', 'Runs', 'Processes', 'Usage today'].every((t) => !labelTitles.includes(t)), `labels: ${labelTitles.join(', ')}`);

  // the agent the C# side seeded, listed by name in the Agents section
  const nameBtn = page.locator('.panel.right button.key', { hasText: AGENT });
  await nameBtn.first().waitFor({ timeout: 15_000 });
  const pool = page.locator('.panel.right .pool', { hasText: AGENT }).first();
  const chip = (await pool.locator('.st').innerText()).trim();
  const rows = await pool.locator('.slot').count();
  // a usable agent has a row per instance (busy or free); one that cannot take work (its model is not loaded) has none, and says why
  check('the agent is listed with a state and, when it can take work, a row per instance', chip.length > 0 && (/free|full/.test(chip) ? rows >= 1 : rows === 0), `state "${chip}", ${rows} rows`);

  // clicking its name opens Settings → Agents with the agent's dialog on top
  await nameBtn.first().click();
  const dialog = page.locator('.dialog', { has: page.getByRole('button', { name: 'Remove agent' }) });
  const nameInput = dialog.locator('input[aria-label="Name"]');
  await nameInput.waitFor({ timeout: 10_000 });
  check('clicking the name opens the agent dialog in Settings → Agents', (await nameInput.inputValue()) === AGENT, `name input: ${await nameInput.inputValue().catch(() => '?')}`);
  await shot(page, 'ui-work-tab-01-agent-dialog');

  // close the dialog and the settings again; the Work tab keeps its state
  await dialog.getByRole('button', { name: 'Done' }).click();
  await page.keyboard.press('Escape');
  await page.waitForTimeout(300);
  await shot(page, 'ui-work-tab-02-after');
});
