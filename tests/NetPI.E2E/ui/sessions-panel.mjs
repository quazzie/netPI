#!/usr/bin/env node
// The side panels and the sessions list against a REAL netpi-server (test "ui.sessions-panel", moved from the mock suite's
// sections "panels + plugin tabs", "narrow side panels", "sessions: the archived section lists what a newest-first window
// hides" and "sessions: collapsible groups (defaults, persistence, search)").
//
//   node tests/NetPI.E2E/ui/sessions-panel.mjs --url http://127.0.0.1:7470 --token e2e-token --work <dir> [--out dir]
import { run, check, shot, openApp, openStripTab, rpc, send, newDir, STAMP } from './lib.mjs';

const project = await rpc('projects.create', { name: `panel-${STAMP}`, path: newDir(`panel-${STAMP}`) });
const base = await rpc('sessions.create', { title: `Base ${STAMP}` });
await send(base.id, 'hello [s:echo]');
// The archived section's case: one very old archive, 59 newer ones, then 201 active chats newer than all of them, so a
// newest-200 window of active + archived holds no archive. A fork with messages is stored at once (no model call), and
// an update moves a chat's time, so each one is archived before the next is made.
const made = [];
const fork = async (title, archived) => {
  const f = await rpc('sessions.fork', { id: base.id });
  made.push(f.id);
  await rpc('sessions.update', { id: f.id, title, ...(archived ? { archived: true } : {}) });
};
const HIDDEN = `Old archive ${STAMP}, hidden by 200 actives`;
await fork(HIDDEN, true);
for (let i = 1; i <= 59; i++) await fork(`Archive ${i} ${STAMP}`, true);
for (let i = 1; i <= 201; i++) await fork(`Active ${i} ${STAMP}`, false);

await run('sessions-panel', async ({ page, browser, socket }) => {
  try {
    await openApp(page);

    // ---- panels + plugin tabs
    await openStripTab(page, 'right', 'Diagnostics');
    await openStripTab(page, 'right', 'Work');
    const handle = page.locator('.panel.right .resizer');
    const hb = await handle.boundingBox();
    await page.mouse.move(hb.x + 3, hb.y + 300);
    await page.mouse.down();
    await page.mouse.move(hb.x - 80, hb.y + 300, { steps: 5 });
    await page.mouse.up();
    const w = await page.locator('.panel.right .body').evaluate((el) => el.getBoundingClientRect().width);
    check('resize right panel', w > 400, `${Math.round(w)}px`);
    await page.locator('.panel.left .strip-tab[aria-selected="true"]').click();
    await page.waitForTimeout(150);
    check('collapse left panel', (await page.locator('.panel.left.open').count()) === 0);
    await shot(page, 'ui-panels-01');
    // favorites live at the top of the chevron's project picker, not as buttons in the top bar
    await openStripTab(page, 'left', 'Projects');
    const row = page.locator('.panel.left .prow', { hasText: project.name }).first();
    const toggleFav = async (title) => {
      await row.hover(); // the row's action buttons appear on hover
      await row.locator(`button[title^="${title}"]`).click();
    };
    check('no fav buttons in the top bar', (await page.locator('.topbar .fav').count()) === 0);
    await toggleFav('Add to favorites');
    await page.waitForTimeout(200);
    await page.locator('.topbar .quick-more').click();
    const opened = await page.locator('.popover .item').first().waitFor({ timeout: 5_000 }).then(() => true).catch(() => false);
    const head = opened ? ((await page.locator('.popover .section').first().textContent().catch(() => '')) ?? '').trim() : ''; // textContent: CSS uppercases it
    const first = opened ? (await page.locator('.popover .item .name').first().innerText().catch(() => '')).trim() : '';
    check("favorites head the chevron's project picker", head === 'Favorites' && first === project.name, `${head}: ${first}`);
    await page.keyboard.press('Escape');
    await page.waitForTimeout(150);
    await toggleFav('Remove from favorites');
    await page.waitForTimeout(200);
    await page.locator('.topbar .quick-more').click();
    await page.locator('.popover .item').first().waitFor({ timeout: 5_000 }).catch(() => {});
    check('no favorites → no Favorites section in the picker', (await page.locator('.popover .section', { hasText: 'Favorites' }).count()) === 0);
    await page.keyboard.press('Escape');
    await page.locator('.panel.right .resizer').dblclick();

    // ---- narrow side panels: both at ~230px (the user's layout is 230–340px), no tab overflows sideways
    const dragTo = async (side, width) => {
      const h = await page.locator(`.panel.${side} .resizer`).boundingBox();
      const body = await page.locator(`.panel.${side} > .body`).boundingBox();
      const target = side === 'right' ? body.x + body.width - width : body.x + width;
      await page.mouse.move(h.x + 3, h.y + 200);
      await page.mouse.down();
      await page.mouse.move(target, h.y + 200, { steps: 6 });
      await page.mouse.up();
    };
    await openStripTab(page, 'left', 'Sessions');
    await dragTo('right', 230);
    await dragTo('left', 230);
    const overflow = () =>
      page.evaluate(() =>
        [...document.querySelectorAll('.panel > .body')]
          .filter((b) => !b.hidden)
          .flatMap((b) => [...b.querySelectorAll('.pane:not([hidden]) .mount, .pane:not([hidden]) .list, .pane:not([hidden]) .tab-root')])
          .filter((el) => el.scrollWidth > el.clientWidth + 1)
          .map((el) => `${el.className} ${el.scrollWidth}>${el.clientWidth}`));
    const nw = Math.round((await page.locator('.panel.right > .body').boundingBox()).width);
    const bad = [];
    for (const [side, tab] of [['right', 'Work'], ['right', 'Ideas'], ['right', 'Diagnostics'], ['right', 'MCP'], ['left', 'Sessions'], ['left', 'Projects'], ['left', 'Files']]) {
      await openStripTab(page, side, tab);
      await page.waitForTimeout(tab === 'Diagnostics' ? 600 : 300);
      const o = await overflow();
      if (o.length) bad.push(`${tab}: ${o.join(', ')}`);
      if (tab === 'Diagnostics' && !(await page.locator('.diag .np-seg').evaluate((el) => el.scrollWidth <= el.clientWidth + 1).catch(() => true)))
        bad.push('Diagnostics: view switcher clipped');
    }
    check(`narrow panels (${nw}px): no sideways overflow in any tab`, nw <= 240 && bad.length === 0, bad.join(' | '));
    await openStripTab(page, 'right', 'Work');
    await openStripTab(page, 'left', 'Sessions');
    await shot(page, 'ui-panels-02-narrow');
    await page.locator('.panel.right .resizer').dblclick();
    await page.locator('.panel.left .resizer').dblclick();

    // ---- the archived section lists what a newest-first window hides
    const windowed = await rpc('sessions.list', { includeArchived: true, limit: 200 });
    check('setup: 201 newer actives push the old archive out of the newest-200 window', windowed.length === 200 && windowed.every((s) => !s.archived),
      `${windowed.length} rows, archives in the window: ${windowed.filter((s) => s.archived).length}`);
    const mark = socket.mark();
    await page.locator('.panel.left .foot .link', { hasText: 'Show archived' }).click();
    await page.locator('.srow.archived').first().waitFor({ timeout: 10_000 }).catch(() => {});
    const asks = () => socket.sent('sessions.list', mark).filter((f) => f.msg?.p?.archivedOnly);
    check('archived section: the archives are listed from an archivedOnly query', (await page.locator('.srow.archived').count()) === 50 && asks().length > 0,
      `archived rows: ${await page.locator('.srow.archived').count()}; archivedOnly calls: ${asks().length}`);
    check('the hidden one is not in the first page (it is the oldest, 60 archives deep)', (await page.locator('.srow.archived', { hasText: HIDDEN }).count()) === 0);
    const more = page.locator('.panel.left .list button', { hasText: 'Show more' });
    check('archived section: 60 archives offer a second page', (await more.count()) === 1, `archived rows now: ${await page.locator('.srow.archived').count()}`);
    if (await more.count()) {
      await more.first().click();
      await page.locator('.srow.archived', { hasText: HIDDEN }).waitFor({ timeout: 10_000 }).catch(() => {});
    }
    check('…and paging brings the hidden archive', (await page.locator('.srow.archived', { hasText: HIDDEN }).count()) > 0,
      `archived rows: ${await page.locator('.srow.archived').count()}; archivedOnly calls: ${asks().length}`);
    await shot(page, 'ui-panels-03-archived');
    const hide = page.locator('.panel.left .foot .link', { hasText: 'Hide archived' });
    if (await hide.count()) await hide.click();

    // ---- collapsible groups: defaults, persistence, search
    const label = (name) => page.locator('.panel.left .list .np-section-label', { hasText: name });
    const section = (p, name) => p.locator('.panel.left .list section.np-section', { has: p.locator('.np-section-label', { hasText: name }) });
    // the collapsed state is a class on the section element itself (a locator only sees its descendants)
    const collapsed = (p, name) => p.locator('.panel.left .list section.np-section.np-section-collapsed', { has: p.locator('.np-section-label', { hasText: name }) });
    const search = page.locator('.panel.left').getByPlaceholder('Search sessions');
    const stored = () => page.evaluate(() => Object.fromEntries(Object.keys(localStorage).filter((k) => k.startsWith('np.section.sessions.')).map((k) => [k, localStorage.getItem(k)])));
    await label('Today').waitFor({ timeout: 10_000 });
    check('defaults: Today is open', (await section(page, 'Today').locator('.srow').count()) > 0, `rows: ${await section(page, 'Today').locator('.srow').count()}`);
    // A server cannot make a chat old, so the default of the older groups is seen from ten days on: a page whose clock is
    // ahead puts every chat in Earlier, and a fresh browser context has no stored state.
    const later = await browser.newContext({ viewport: { width: 1600, height: 1000 }, colorScheme: 'dark' });
    try {
      const p2 = await later.newPage();
      await p2.clock.install({ time: Date.now() + 10 * 86_400_000 });
      await openApp(p2);
      await openStripTab(p2, 'left', 'Sessions');
      await p2.locator('.panel.left .list .np-section-label', { hasText: 'Earlier' }).waitFor({ timeout: 10_000 }).catch(() => {});
      check('defaults: the older groups are closed', (await collapsed(p2, 'Earlier').count()) === 1 && (await section(p2, 'Earlier').locator('.srow').count()) === 0,
        `Earlier rows: ${await section(p2, 'Earlier').locator('.srow').count()}`);
    } finally {
      await later.close();
    }
    // collapsing an open-by-default group stores the state per key
    await section(page, 'Today').locator('.np-section-toggle').click();
    check('collapsing hides the rows', (await collapsed(page, 'Today').count()) === 1 && (await section(page, 'Today').locator('.srow').count()) === 0);
    const before = await stored();
    check('the state is stored in np.section.sessions.*', before['np.section.sessions.today'] === '0', JSON.stringify(before));
    // and it survives a reload
    await page.reload();
    await page.waitForSelector('.panel.left', { timeout: 15_000 });
    await openStripTab(page, 'left', 'Sessions');
    await label('Today').waitFor({ timeout: 10_000 });
    check('after a reload, the collapsed group is still collapsed', (await collapsed(page, 'Today').count()) === 1);
    // a search shows its matches inside closed groups, without touching the stored state
    await search.fill(`Base ${STAMP}`);
    await page.locator('.srow', { hasText: `Base ${STAMP}` }).first().waitFor({ timeout: 5_000 }).catch(() => {});
    check('a match inside a closed group is shown', (await section(page, 'Today').locator('.srow', { hasText: `Base ${STAMP}` }).count()) === 1
      && (await section(page, 'Today').locator('.np-section-toggle').count()) === 0);
    check('the search wrote no state', JSON.stringify(await stored()) === JSON.stringify(before), JSON.stringify(await stored()));
    await shot(page, 'ui-panels-04-search');
    await search.fill('');
    await page.waitForFunction(() => document.querySelector('.panel.left .list section.np-section.np-section-collapsed'), null, { timeout: 5_000 }).catch(() => {});
    check('clearing the search returns the stored state', (await collapsed(page, 'Today').count()) === 1 && (await section(page, 'Today').locator('.srow').count()) === 0);
    await section(page, 'Today').locator('.np-section-toggle').click();
    check('the group opens again on purpose', (await section(page, 'Today').locator('.srow').count()) > 0);
  } finally {
    for (const id of [...made, base.id]) await rpc('sessions.delete', { id }).catch(() => {});
    await rpc('projects.delete', { id: project.id }).catch(() => {});
  }
}, { viewport: { width: 1600, height: 1000 }, freshTabs: true, tap: true });
