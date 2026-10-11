#!/usr/bin/env node
// UI test for the start screen's split project button, against a REAL netpi-server + MockLlm
// (started by the C# E2E suite, test "ui.start-project").
// The button is one control with two halves: the name starts a session in the project shown, the chevron picks
// another one. The old single button only opened the picker, so both halves are checked: the chevron picks without
// starting anything, and the name starts in the project named -- a session with no project would be the quiet
// failure this guards against.
//
//   node tests/NetPI.E2E/ui/start-project.mjs --url http://127.0.0.1:7470 --token e2e-token --project e2e-split-abc123 [--out dir]
// Prints one JSON line at the end: { ok, checks: [{name, ok, detail}], errors: [...] }.
import { run, arg, check, shot, BASE, TOKEN } from './lib.mjs';

const PROJECT = arg('project', '');

await run('start-project', async ({ page }) => {
  await page.goto(`${BASE}/?token=${encodeURIComponent(TOKEN)}`);
  await page.waitForSelector('.topbar', { timeout: 15_000 });

  // The start screen shows with no chat open: close whatever tab is still there (one another window opened since the
  // reset above). Each tab by its own id, so a chat that arrives mid-loop is never closed twice.
  const tabs = page.locator('.topbar .tab');
  for (let i = 0, n = await tabs.count(); i < 30 && n > 0; i++) {
    const first = page.locator('.topbar .tab').first();
    const id = await first.getAttribute('data-tab');
    await page.locator(`.topbar .tab[data-tab="${id}"] .tab-close`).click();
    await page.locator(`.topbar .tab[data-tab="${id}"]`).waitFor({ state: 'detached', timeout: 10_000 });
    n = await page.locator('.topbar .tab').count();
  }
  const welcome = page.locator('.welcome');
  await welcome.waitFor({ timeout: 15_000 });
  check('the start screen is showing with no chat open', await welcome.isVisible());

  // the split button: two halves, the name one carrying the project
  const main = page.locator('.welcome .split .target');
  const more = page.locator('.welcome .split .target-more');
  // exactly one of each half, waited for instead of counted on the spot: a count() takes no time, and a locator that
  // resolves to more than one element throws at once, so a screen still settling failed the check with both halves
  // already in place (the ledger's first two failures of this test)
  const halvesOk = await page
    .waitForFunction(
      () => document.querySelectorAll('.welcome .split .target').length === 1 && document.querySelectorAll('.welcome .split .target-more').length === 1,
      undefined,
      { timeout: 10_000 },
    )
    .then(() => true, () => false);
  check('the project button is a split button (name half + chevron half)', halvesOk,
    await page.evaluate(() => `${document.querySelectorAll('.welcome .split .target').length} name halves, ${document.querySelectorAll('.welcome .split .target-more').length} chevron halves`));
  check('the name half starts a session in the project shown', (await main.getAttribute('title') ?? '').startsWith('Start a session in'),
    await main.getAttribute('title'));
  check('the chevron half picks a project', (await more.getAttribute('title')) === 'Pick another project', await more.getAttribute('title'));

  // the chevron half: the picker, and no session -- picking a project here is what the old single button did, and it
  // must not have started anything on its own
  const tabsBefore = await page.locator('.topbar .tab').count();
  await more.click();
  const pop = page.locator('.popover', { has: page.locator('input[placeholder="Start a session in…"]') });
  const popOk = await pop.first().waitFor({ timeout: 10_000 }).then(() => true).catch(() => false);
  check('the chevron opens the project picker', popOk);
  await shot(page, 'ui-split-01-picker');
  if (popOk) {
    check('the picker offers the project to pick', (await pop.first().innerText()).includes(PROJECT));
    check('the chevron started no session', (await page.locator('.topbar .tab').count()) === tabsBefore && await welcome.isVisible());
    // picking from it starts a session in that project, and the button then targets it
    await pop.first().locator('.item', { hasText: PROJECT }).first().click();
  } else {
    check('the picker offers the project to pick', false, 'no picker');
    check('the chevron started no session', false, 'no picker');
    check('picking a project starts a session in it', false, 'no picker');
  }

  // the session the picker started: the composer chip names its project (the chat's own row is not what is read: a
  // session with no message is transient, and the chip is the control the user reads)
  const chip = page.locator('.composer .bar button', { hasText: PROJECT }).first();
  const chipOk = await chip.waitFor({ timeout: 15_000 }).then(() => true).catch(() => false);
  check('picking a project starts a session in it', chipOk, chipOk ? await chip.innerText() : 'no composer chip with the project name');

  // back to the start screen: the button now targets the project just used
  const started = page.locator('.topbar .tab.active');
  await started.waitFor({ timeout: 10_000 });
  const sid = await started.getAttribute('data-tab');
  await page.locator(`.topbar .tab[data-tab="${sid}"] .tab-close`).click();
  await welcome.waitFor({ timeout: 10_000 });
  check('the button targets the project picked', (await main.innerText()).includes(PROJECT), await main.innerText());
  check('the hint under it is the project folder', (await page.locator('.welcome .where').innerText()).trim().length > 0);
  await shot(page, 'ui-split-03-target');

  // the name half: a session in the project shown, not the picker
  await main.click();
  const chip2 = page.locator('.composer .bar button', { hasText: PROJECT }).first();
  const chip2Ok = await chip2.waitFor({ timeout: 15_000 }).then(() => true).catch(() => false);
  check('the name half starts a session in the project shown', chip2Ok, chip2Ok ? await chip2.innerText() : 'no composer chip with the project name');
  check('the name half did not open the picker', (await page.locator('.popover').count()) === 0);
  await shot(page, 'ui-split-02-started');
}, { freshTabs: true });
