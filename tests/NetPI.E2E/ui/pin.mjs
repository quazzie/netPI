#!/usr/bin/env node
// UI test for pinned sessions against a REAL netpi-server + MockLlm (started by the C# E2E suite, test "ui.pinned-sessions").
// Pins a prepared session from the sessions panel and checks the "Pinned" group tops the list above the recency groups
// with the pinned row first; then unpins it and checks the group goes away and the row returns to its recency group.
//
//   node tests/NetPI.E2E/ui/pin.mjs --url http://127.0.0.1:7470 --token e2e-token --session "Pin me" [--out dir]
// Prints one JSON line at the end: { ok, checks: [{name, ok, detail}], errors: [...] }.
import { run, arg, check, shot, openApp } from './lib.mjs';

const SESSION = arg('session', 'Pin me');

await run('pin', async ({ page }) => {
  await openApp(page);
  const sessionsTab = page.locator('.panel.left .strip-tab', { hasText: 'Sessions' });
  if ((await sessionsTab.count()) && (await sessionsTab.getAttribute('aria-selected')) !== 'true') await sessionsTab.click();
  const row = page.locator('.srow', { hasText: SESSION }).first();
  await row.waitFor({ timeout: 15_000 });
  check('session listed in the sessions panel', true);

  // no pinned group before anything is pinned
  check('no Pinned group before pinning', (await page.locator('.list .np-section-label', { hasText: 'Pinned' }).count()) === 0);

  // hover opens the row's actions; the pin button toggles the session
  await row.hover();
  await row.getByRole('button', { name: 'Pin to top' }).click();
  await page.locator('.list .np-section-label', { hasText: 'Pinned' }).first().waitFor({ timeout: 5_000 });
  const labels = await page.locator('.list .np-section-label').allTextContents();
  check('a Pinned group tops the list above the recency groups', labels[0].toLowerCase() === 'pinned' && labels[1].toLowerCase() === 'today', labels.join(', '));
  const pinnedRow = page.locator('.list section.np-section', { has: page.locator('.np-section-label', { hasText: 'Pinned' }) }).locator('.srow');
  check('the pinned row leads the list', (await pinnedRow.count()) === 1 && (await pinnedRow.first().innerText()).includes(SESSION));
  check('the pinned row shows the unpin action', (await pinnedRow.getByRole('button', { name: 'Unpin' }).count()) === 1);
  await shot(page, 'ui-pin-01-pinned');

  // unpin: the group goes away and the row is back in its recency group
  await pinnedRow.first().hover();
  await pinnedRow.first().getByRole('button', { name: 'Unpin' }).click();
  const gone = await page.locator('.list .np-section-label', { hasText: 'Pinned' }).first().waitFor({ state: 'detached', timeout: 5_000 }).then(() => true).catch(() => false);
  check('unpinning removes the Pinned group', gone);
  const labels2 = await page.locator('.list .np-section-label').allTextContents();
  check('the row is back in its recency group', labels2[0].toLowerCase() === 'today' && (await page.locator('.srow', { hasText: SESSION }).count()) === 1, labels2.join(', '));
  await shot(page, 'ui-pin-02-unpinned');
});
