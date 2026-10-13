#!/usr/bin/env node
// UI test for moving side-panel tabs against a REAL netpi-server + MockLlm (test "ui.panel-tabs").
// Drags a right-panel tab onto the left strip, then back to a place between the right tabs; checks the panels, the
// stored layout (localStorage and the host's ui.state) and a reload from the host copy alone. Restores the layout it
// found.
//
//   node tests/NetPI.E2E/ui/panel-tabs.mjs --url http://127.0.0.1:7470 --token e2e-token [--out dir]
// Prints one JSON line at the end: { ok, checks: [{name, ok, detail}], errors: [...] }.
import { run, check, shot, openApp, rpc, until } from './lib.mjs';

const KEY = 'netpi.layout';
const titles = (page, side) => page.locator(`.panel.${side} .strip-tab .strip-label`).allTextContents();
const hostLayout = () => rpc('ui.state.get', { key: KEY }); // the stored JSON, or null

const before = await rpc('ui.state.get', { key: KEY }).catch(() => null);

await run('panel-tabs', async ({ page }) => {
  try {
    await openApp(page);
    await page.locator('.panel.right .strip-tab').first().waitFor({ timeout: 15_000 });
    const right0 = await titles(page, 'right');
    const left0 = await titles(page, 'left');
    check('both panels have tabs', right0.length >= 2 && left0.length >= 1, `left ${left0} · right ${right0}`);
    const moved = right0[0];

    // right → left, dropped below the last left tab
    await page.locator('.panel.right .strip-tab', { hasText: moved }).dragTo(page.locator('.panel.left .strip'), {
      targetPosition: { x: 10, y: (await page.locator('.panel.left .strip').boundingBox()).height - 10 },
    });
    const left1 = await titles(page, 'left');
    check('the tab moved to the end of the left strip', left1.at(-1) === moved && left1.length === left0.length + 1, left1.join(', '));
    check('the tab left the right strip', !(await titles(page, 'right')).includes(moved));
    check('the moved tab shows in its new panel', (await page.locator('.panel.left .strip-tab', { hasText: moved }).getAttribute('aria-selected')) === 'true');
    await shot(page, 'ui-panel-tabs-01-moved-left');

    const stored = await page.evaluate((k) => JSON.parse(localStorage.getItem(k)), KEY);
    check('the arrangement is in localStorage', stored?.tabs?.left?.length === left1.length);
    const onHost = await until('the host copy', async () => ((await hostLayout())?.tabs?.left?.length === left1.length ? true : null), 5_000).catch(() => null);
    check('the arrangement reaches the host (ui.state)', !!onHost);

    // reload with only the host copy: it comes back from ui.state
    await page.evaluate((k) => localStorage.removeItem(k), KEY);
    await page.reload();
    await page.locator('.panel.left .strip-tab', { hasText: moved }).waitFor({ timeout: 10_000 }).catch(() => {});
    check('after a reload the tab is still on the left', (await titles(page, 'left')).includes(moved), (await titles(page, 'left')).join(', '));

    // left → right, between the first and second right tab
    const r = await page.locator('.panel.right .strip-tab').first().boundingBox();
    await page.locator('.panel.left .strip-tab', { hasText: moved }).dragTo(page.locator('.panel.right .strip'), {
      targetPosition: { x: 10, y: r.y + r.height - (await page.locator('.panel.right .strip').boundingBox()).y - 2 },
    });
    const right2 = await titles(page, 'right');
    check('dropped back on the right, after its first tab', right2[1] === moved && right2.length === right0.length, right2.join(', '));
    check('the left strip is as it was', (await titles(page, 'left')).join() === left0.join());
    await shot(page, 'ui-panel-tabs-02-moved-back');
  } finally {
    await rpc('ui.state.set', { key: KEY, value: before }).catch(() => {});
  }
});
