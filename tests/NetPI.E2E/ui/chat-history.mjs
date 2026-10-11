#!/usr/bin/env node
// A chat's history against a REAL netpi-server + MockLlm (test "ui.chat-history", moved from the mock suite's sections
// "long session", "a snapshot answer must not undo what it missed" and "deleting a chat leaves nothing of it behind").
// The long chat is one run of 110 cheap tool steps and a compaction (about 230 messages: more than the window's cap of 200).
//
//   node tests/NetPI.E2E/ui/chat-history.mjs --url http://127.0.0.1:7470 --token e2e-token [--out dir]
import { run, check, shot, openApp, openSession, openStripTab, rpc, send, STAMP } from './lib.mjs';

const LONG = `Long chat ${STAMP}`;
const long = await rpc('sessions.create', { title: LONG });
await send(long.id, 'fill the history [s:long n=110 tool=ls]', 120_000);
await rpc('compaction.run', { sessionId: long.id });
await send(long.id, 'after the compaction [s:echo]');
// the snapshot case: a chat opened for the first time, whose page is read while a message is committed
const SNAP = `Snapshot chat ${STAMP}`;
const snap = await rpc('sessions.create', { title: SNAP });
await send(snap.id, 'the candidate list, please [s:echo]');
const made = [long.id, snap.id];

await run('chat-history', async ({ page, socket }) => {
  try {
    await openApp(page);
    const ring = page.locator('.composer .ring');

    // ---- long session: the window pages back, caps at 200, pages forward and jumps to the latest
    const t0 = Date.now();
    await openSession(page, LONG);
    await page.locator('.content .item[data-kind="text"]').first().waitFor({ timeout: 10_000 });
    check('open long session', true, `${Date.now() - t0}ms to the first paint of ${await page.locator('.content .item').count()} items`);
    await shot(page, 'ui-history-01-long');
    for (let i = 0; i < 4; i++) {
      const btn = page.locator('.earlier button', { hasText: 'Load earlier' });
      if (!(await btn.count())) break;
      await page.locator('.scroller').evaluate((el) => (el.scrollTop = 0));
      await page.waitForTimeout(80);
      const before = await page.evaluate(() => {
        const first = document.querySelector('.content .item');
        return { key: first.dataset.key, top: first.getBoundingClientRect().top };
      });
      const renderMs = await page.evaluate(async (k) => {
        const t = performance.now();
        [...document.querySelectorAll('.earlier button')].find((b) => b.textContent.includes('Load earlier')).click();
        await new Promise((r) => {
          const tick = () => (document.querySelector('.content .item')?.dataset.key !== k ? r() : requestAnimationFrame(tick));
          tick();
        });
        await new Promise((r) => requestAnimationFrame(() => setTimeout(r, 0)));
        return performance.now() - t;
      }, before.key);
      await page.waitForTimeout(100);
      const after = await page.evaluate((k) => {
        const el = document.querySelector(`.content .item[data-key="${k}"]`);
        return { top: el?.getBoundingClientRect().top ?? null, items: document.querySelectorAll('.content .item').length };
      }, before.key);
      check(`load earlier #${i + 1}`, after.top != null && Math.abs(after.top - before.top) < 4,
        `${Math.round(renderMs)}ms (rpc + render), anchor moved ${after.top == null ? '?' : Math.round(after.top - before.top)}px, ${after.items} items in DOM`);
    }
    const hasNewer = await page.locator('.earlier button', { hasText: 'jump to latest' }).count();
    // the render cost of the full (capped) window: another tab and back (the chat's store is cached)
    const inWindow = await page.locator('.content .item').count();
    const before = await page.locator('.topbar .tab').count();
    await page.keyboard.press('Control+t');
    await page.waitForFunction((n) => document.querySelectorAll('.topbar .tab').length > n, before, { timeout: 10_000 });
    await page.locator('.intro').waitFor({ timeout: 10_000 });
    const switchMs = await page.evaluate(async (title) => {
      const tab = [...document.querySelectorAll('.topbar .tab')].find((t) => t.textContent.includes(title));
      const t = performance.now();
      tab.click();
      await new Promise((r) => {
        const tick = () => (document.querySelectorAll('.content .item').length > 80 ? r() : requestAnimationFrame(tick));
        tick();
      });
      await new Promise((r) => requestAnimationFrame(() => setTimeout(r, 0))); // style, layout and paint included
      return performance.now() - t;
    }, LONG);
    check('re-render 200-message window on tab switch', switchMs < 400, `${Math.round(switchMs)}ms for ${inWindow} items`);
    await page.locator('.topbar .tab', { hasText: 'New session' }).locator('.tab-close').click();
    check('window capped (newer messages dropped)', hasNewer > 0);
    await page.locator('.scroller').evaluate((el) => (el.scrollTop = 0));
    await page.waitForTimeout(150);
    await shot(page, 'ui-history-02-earlier');
    if (hasNewer) {
      // newer pages forward with afterSeq: the window's newest message moves on, nothing is repeated
      const lastKey = () => page.evaluate(() => [...document.querySelectorAll('.content .item')].at(-1)?.dataset.key ?? null);
      const keyBefore = await lastKey();
      await page.locator('.earlier button', { hasText: 'Load newer' }).click();
      await page.waitForFunction((k) => [...document.querySelectorAll('.content .item')].at(-1)?.dataset.key !== k, keyBefore, { timeout: 5_000 }).catch(() => {});
      const keys = await page.evaluate(() => [...document.querySelectorAll('.content .item')].map((e) => e.dataset.key));
      check('load newer pages forward', keys.at(-1) !== keyBefore && new Set(keys).size === keys.length, `${keyBefore} → ${keys.at(-1)}, ${keys.length} items`);
      const jump = page.locator('.earlier button', { hasText: 'jump to latest' });
      if (await jump.count()) await jump.click();
      await page.locator('.earlier button', { hasText: 'jump to latest' }).waitFor({ state: 'detached', timeout: 5_000 }).catch(() => {});
    } else check('load newer pages forward', false, 'the window was never capped');
    check('back to latest', !(await page.locator('.earlier button', { hasText: 'jump to latest' }).count()), `${await page.locator('.content .item').count()} items`);

    // the context ring and its popout, with the compactions
    await page.waitForFunction(() => /tokens? \(/.test(document.querySelector('.composer .ring')?.getAttribute('aria-label') ?? ''), null, { timeout: 5_000 }).catch(() => {});
    const ringLabel = await ring.getAttribute('aria-label');
    check('context ring reads the session context', /tokens? \(/.test(ringLabel ?? ''), ringLabel);
    await ring.click();
    await page.locator('.popover .cx').waitFor({ timeout: 5_000 });
    // the prompt's breakdown is read when the popout opens ("reading…" until then)
    await page.waitForFunction(() => /System prompt/.test(document.querySelector('.popover .cx')?.innerText ?? ''), null, { timeout: 10_000 }).catch(() => {});
    const cx = await page.locator('.popover .cx').innerText();
    check('context breakdown popout', /Used/.test(cx) && /Window/.test(cx) && /Free/.test(cx) && /System prompt/.test(cx), cx.replace(/\n/g, ' | '));
    await shot(page, 'ui-history-03-ring');
    await page.keyboard.press('Escape');
    await page.locator('.popover .cx').waitFor({ state: 'detached', timeout: 5_000 }).catch(() => {});
    check('Esc closes the context popout', (await page.locator('.popover .cx').count()) === 0);
    // the window holds 60 of ~230 messages, so the count is a lower bound and says so
    await ring.click();
    await page.locator('.popover .cx').waitFor({ timeout: 5_000 });
    const cRow = page.locator('.popover .cx .row', { hasText: 'Compactions' });
    const cTitle = (await cRow.getAttribute('title')) ?? '';
    const cText = await cRow.locator('b').innerText();
    check('the popout counts compactions as a lower bound', cText === '1+' && /not loaded/.test(cTitle), `${cText} — ${cTitle}`);
    const cRead = await page.locator('.popover .cx .read').innerText().catch(() => '');
    check('the newest compaction reads what it did', /\d+(\.\d+)?k → \d+(\.\d+)?k · manual/.test(cRead), cRead);
    const ringBefore = await ring.getAttribute('aria-label');
    await page.locator('.popover .cx .foot button', { hasText: 'Compact now' }).click();
    await page.waitForFunction(() => [...document.querySelectorAll('.popover .cx .row')].some((r) => /Compactions/.test(r.innerText) && /2\+/.test(r.innerText)), null, { timeout: 30_000 }).catch(() => {});
    const cAfter = await cRow.locator('b').innerText().catch(() => '');
    check('Compact now compacts and the count follows', cAfter === '2+', cAfter);
    const readAfter = await page.locator('.popover .cx .read').innerText().catch(() => '');
    check('the reading follows the newest compaction', readAfter !== cRead && /→ .* · manual/.test(readAfter), `${cRead} → ${readAfter}`);
    await page.waitForFunction((b) => document.querySelector('.composer .ring')?.getAttribute('aria-label') !== b, ringBefore, { timeout: 5_000 }).catch(() => {});
    const ringAfter = await ring.getAttribute('aria-label');
    check('the ring follows session.context after a compaction', ringBefore !== ringAfter, `${ringBefore} → ${ringAfter}`);
    const toasts = await page.locator('.np-toast, .toast').allInnerTexts().catch(() => []);
    check('the result of a manual compaction is a toast', toasts.some((t) => /Context compacted/.test(t)), toasts.join(' | '));
    await shot(page, 'ui-history-04-compactions');
    await page.keyboard.press('Escape');

    // ---- a snapshot answer must not undo what it missed: the chat's page (sessions.messages) is the page as the server
    // read it when the request went out; a message committed while the answer is on its way is on screen already
    const held = socket.hold((f) => f.t === 'res' && f.m === 'sessions.messages' && socket.frames.find((c) => c.dir === 'out' && c.id === f.id)?.msg?.p?.id === snap.id);
    const mark = socket.mark();
    const row = page.locator('.panel.left .srow', { hasText: SNAP }).first();
    await openStripTab(page, 'left', 'Sessions');
    await row.click();
    await socket.waitSent('sessions.messages', mark);
    await send(snap.id, 'Committed while the page was being read. [s:echo]');
    await page.waitForTimeout(300); // the run's events are in the page
    const heldAnswers = held.held;
    held.release();
    const late = page.locator('.content:visible .item[data-kind="text"]', { hasText: 'Committed while the page was being read' });
    await late.first().waitFor({ timeout: 10_000 }).catch(() => {});
    check('a message published while the page loads is not dropped by the older page', heldAnswers >= 1 && (await late.count()) === 1,
      `${heldAnswers} answer(s) held while the message was committed; sessions.messages calls for the chat: ${socket.sent('sessions.messages', mark).filter((f) => f.msg?.p?.id === snap.id).length}`);
    check('…and the page it answered with is still the window (not replaced by the one message)',
      (await page.locator('.content:visible .item[data-kind="text"]', { hasText: 'candidate list' }).count()) > 0, `${await page.locator('.content:visible .item').count()} items in the window`);
    await shot(page, 'ui-history-05-snapshot');

    // ---- deleting a chat leaves nothing of it behind (row, tab), and deleting a subagent keeps its parent and sibling
    const before2 = await page.locator('.topbar .tab').count();
    await page.keyboard.press('Control+t');
    await page.waitForFunction((n) => document.querySelectorAll('.topbar .tab').length > n, before2, { timeout: 10_000 });
    await page.locator('.composer textarea').fill(`A short chat ${STAMP} that will be deleted. [s:echo]`);
    await page.locator('.composer textarea').press('Enter');
    await page.waitForFunction(() => !document.querySelector('.composer.running'), null, { timeout: 30_000 }).catch(() => {});
    const sid = await page.locator('.topbar .tab.active').getAttribute('data-tab');
    made.push(sid);
    const mine = page.locator('.panel.left .srow', { hasText: `A short chat ${STAMP}` }).first();
    await mine.waitFor({ timeout: 10_000 });
    await mine.focus();
    await page.keyboard.press('Delete');
    await page.locator('.dialog button', { hasText: /^Delete$/ }).click();
    await mine.waitFor({ state: 'detached', timeout: 10_000 }).catch(() => {});
    check('a deleted chat: its row and its tab are gone', (await mine.count()) === 0 && (await page.locator(`.topbar .tab[data-tab="${sid}"]`).count()) === 0,
      `tab ${sid} left: ${await page.locator(`.topbar .tab[data-tab="${sid}"]`).count()}`);
    const PARENT = `Parent ${STAMP}`;
    const parent = await rpc('sessions.create', { title: PARENT });
    made.push(parent.id);
    await send(parent.id, 'split this [s:spawn n=2 delay=100]', 90_000);
    const kids = await rpc('sessions.list', { parentSessionId: parent.id, includeSubagents: true });
    const prow = page.locator('.panel.left .srow', { hasText: PARENT }).first();
    await prow.waitFor({ timeout: 10_000 });
    const subTitle = (k) => kids.find((s) => s.title?.includes(`worker-${k}`) || s.title?.includes(`#${k}`))?.title ?? kids[k - 1]?.title;
    const sub = page.locator('.panel.left .srow', { hasText: subTitle(1) }).first();
    if (!(await sub.isVisible())) await prow.locator('.kids').click();
    await sub.waitFor({ timeout: 10_000 });
    await sub.focus();
    await page.keyboard.press('Delete');
    await page.locator('.dialog button', { hasText: /^Delete$/ }).click();
    await sub.waitFor({ state: 'detached', timeout: 10_000 }).catch(() => {});
    check('a deleted subagent: its parent and the other subagent stay',
      (await sub.count()) === 0 && (await page.locator('.panel.left .srow', { hasText: PARENT }).count()) === 1
        && (await page.locator('.panel.left .srow', { hasText: subTitle(2) }).count()) === 1,
      `subagents: ${kids.map((k) => k.title).join(' | ')}`);
    await shot(page, 'ui-history-06-deleted');
  } finally {
    for (const id of made) if (id) await rpc('sessions.delete', { id }).catch(() => {});
  }
}, { viewport: { width: 1600, height: 1000 }, freshTabs: true, tap: true });
