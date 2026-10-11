#!/usr/bin/env node
// The composer and the chat tabs against a REAL netpi-server + MockLlm (test "ui.composer", moved from the mock suite's
// sections "composer popups", "popups survive a chat update", "tabs", "tab dots: a run that ends behind another tab",
// "composer: narrow widths", "drag to reorder tabs" and "composer drafts"). A reply that has to arrive while the test looks
// (a picker open over it, a tab behind another) is held at a MockLlm gate and released on cue.
//
//   node tests/NetPI.E2E/ui/composer.mjs --url http://127.0.0.1:7470 --token e2e-token --mock http://127.0.0.1:7479 --work <dir>
import fs from 'node:fs';
import path from 'node:path';
import { run, check, shot, openApp, openSession, openStripTab, rpc, send, gates, newDir, STAMP } from './lib.mjs';

const QWEN = `qwen-${STAMP}`;
const GEMMA = `gemma-${STAMP}`;
const dir = newDir(`comp-${STAMP}`);
const put = (rel, text) => {
  fs.mkdirSync(path.dirname(path.join(dir, rel)), { recursive: true });
  fs.writeFileSync(path.join(dir, rel), text);
};
put('.netpi/skills/release-notes/SKILL.md', '---\nname: release-notes\ndescription: Write the release notes.\n---\n\nSteps.\n');
put('.netpi/skills/handoff/SKILL.md', '---\nname: handoff\ndescription: Hand the work over.\ndisable-model-invocation: true\n---\n\nSteps.\n');
put('src/LaneScheduler.cs', 'class LaneScheduler {}\n');
put('src/LanePool.cs', 'class LanePool {}\n');
const project = await rpc('projects.create', { name: `comp-${STAMP}`, path: dir });
const chat = await rpc('sessions.create', { title: `Composer chat ${STAMP}`, projectId: project.id });
await send(chat.id, 'hello [s:echo]');
const made = [chat.id];

const activeId = (page) => page.locator('.topbar .tab.active').getAttribute('data-tab').catch(() => null);
/** Ctrl+T, and wait until the new tab is the one showing (an empty chat in front can take the fill otherwise). */
async function newTab(page) {
  const before = await page.locator('.topbar .tab').count();
  await page.keyboard.press('Control+t');
  await page.waitForFunction((n) => document.querySelectorAll('.topbar .tab').length > n, before, { timeout: 10_000 });
  await page.locator('.intro').waitFor({ timeout: 10_000 });
}
const idle = (page) => page.waitForFunction(() => !document.querySelector('.composer.running'), null, { timeout: 60_000 });

await run('composer', async ({ page, socket }) => {
  const settingsAgents = async () => Object.keys((await rpc('settings.get')).settings.agents ?? {});
  const agentsBefore = await settingsAgents();
  await rpc('settings.set', { path: `agents.${QWEN}`, value: { model: 'aiproxy/qwen3.8-27b' } });
  await rpc('settings.set', { path: `agents.${GEMMA}`, value: { model: 'aiproxy/gemma-4' } });
  try {
    await openApp(page);
    await openSession(page, `Composer chat ${STAMP}`);
    const ta = page.locator('.composer textarea');

    // ---- composer popups: the agent picker, commands and skills, mentions
    const agentBtn = page.locator('.composer button[aria-label="Agent"]');
    await agentBtn.click();
    await page.locator('.popover .agent-pop').waitFor({ timeout: 5_000 }).catch(() => {});
    const gemmaOpt = page.locator(`.popover .opt[data-agent="${GEMMA}"]`);
    check('the agent picker lists the agents with their state', (await page.locator(`.popover .opt[data-agent="${QWEN}"]`).count()) === 1 && /isn't loaded/.test(await gemmaOpt.innerText().catch(() => '')),
      await gemmaOpt.innerText().catch(() => ''));
    await shot(page, 'ui-composer-01-agents');
    await gemmaOpt.click();
    await page.waitForFunction((a) => document.querySelector('.composer button[aria-label="Agent"]')?.innerText.includes(a), GEMMA, { timeout: 5_000 }).catch(() => {});
    check('choosing an agent shows it on the button', (await agentBtn.innerText()).includes(GEMMA), await agentBtn.innerText());
    // New agent…: the model list, then the chat runs on the new agent
    await agentBtn.click();
    await page.locator('.popover .foot button', { hasText: 'New agent' }).click();
    await page.locator('.popover input[aria-label="Filter models"]').fill('gemma');
    await page.keyboard.press('Enter');
    let created = null;
    for (let i = 0; i < 30 && !created; i++) {
      await page.waitForTimeout(100);
      created = (await settingsAgents()).find((k) => k.startsWith('gemma-4') && !agentsBefore.includes(k));
    }
    await page.waitForFunction((a) => document.querySelector('.composer button[aria-label="Agent"]')?.innerText.includes(a), created ?? 'gemma-4', { timeout: 5_000 }).catch(() => {});
    check('"New agent…" sets up an agent on the model and runs the chat on it', !!created && (await agentBtn.innerText()).includes(created), `${created}; button: ${await agentBtn.innerText()}`);
    if (await page.locator('.popover input[aria-label="Filter models"]').count()) await page.keyboard.press('Escape');
    await agentBtn.click();
    await page.locator(`.popover .opt[data-agent="${QWEN}"]`).click();
    await page.waitForFunction((a) => document.querySelector('.composer button[aria-label="Agent"]')?.innerText.includes(a), QWEN, { timeout: 5_000 }).catch(() => {});
    check('back on qwen', (await agentBtn.innerText()).includes(QWEN), await agentBtn.innerText());
    // "Any available": no agent of its own, each run takes whichever is free
    await agentBtn.click();
    await page.locator('.popover .opt[data-agent="any"]').click();
    await page.waitForFunction(() => document.querySelector('.composer button[aria-label="Agent"]')?.innerText.includes('Any available'), null, { timeout: 5_000 }).catch(() => {});
    check('"Any available" is a choice the button shows', (await agentBtn.innerText()).includes('Any available'), await agentBtn.innerText());
    await agentBtn.click();
    check('and the picker marks it', (await page.locator('.popover .opt[data-agent="any"].current').count()) === 1);
    await page.locator(`.popover .opt[data-agent="${QWEN}"]`).click();
    await ta.fill('');
    await ta.type('/');
    await page.locator('.popup .opt').first().waitFor({ timeout: 5_000 }).catch(() => {});
    check('command popup', (await page.locator('.popup .opt').count()) >= 7, `${await page.locator('.popup .opt').count()} entries`);
    await ta.fill('');
    await ta.type('/skill');
    await page.locator('.popup .opt .cmd', { hasText: '/skill:' }).first().waitFor({ timeout: 5_000 }).catch(() => {});
    const skillOpts = await page.locator('.popup .opt .cmd').allInnerTexts();
    check("the session's skills in the command popup, user-only ones too", skillOpts.includes('/skill:release-notes') && skillOpts.includes('/skill:handoff'), skillOpts.join(' '));
    await ta.press('Enter');
    check('accepting a skill inserts /skill:name', /^\/skill:[a-z-]+ $/.test(await ta.inputValue()), await ta.inputValue());
    await ta.fill('');
    await ta.type('Look at @Lane');
    await page.locator('.popup .file').first().waitFor({ timeout: 5_000 }).catch(() => {});
    await shot(page, 'ui-composer-02-mentions');
    check('mention popup (files.search)', (await page.locator('.popup .file').count()) > 0);
    await page.keyboard.press('Escape');
    await ta.fill('');

    // ---- popups survive a chat update: a reply streaming under an open picker re-pins the chat; the picker stays
    await page.setViewportSize({ width: 1280, height: 520 }); // a short window: a few rows overflow the chat list
    await openStripTab(page, 'left', 'Projects');
    const favRow = page.locator('.panel.left .prow', { hasText: project.name }).first();
    await favRow.hover();
    await favRow.locator('button[title^="Add to favorites"]').click();
    await page.locator('.topbar .quick-more').waitFor({ timeout: 5_000 }).catch(() => {});
    check('the top bar has its project chevron', (await page.locator('.topbar .quick-more').count()) === 1);
    await ta.fill('Keep the popups open while this answer streams. [s:echo]');
    await ta.press('Enter');
    await page.locator('.composer.running').waitFor({ timeout: 5_000 }).catch(() => {});
    await idle(page);
    const overflows = await page.locator('.scroller').evaluate((el) => el.scrollHeight > el.clientHeight + 4).catch(() => false);
    const anchorGap = async () => {
      const a = await page.locator('.topbar .quick-more').boundingBox();
      const m = await page.locator('.popover').first().boundingBox().catch(() => null);
      if (!a || !m) return null;
      return { dx: Math.round(m.x - a.x), dy: Math.round(m.y - (a.y + a.height)) };
    };
    // the chevron's project picker, opened while the reply is held: it streams under the picker once released
    const g1 = `pop1-${STAMP}`;
    await ta.fill(`Second answer, streamed under an open picker. [s:echo gate=${g1}]`);
    await ta.press('Enter');
    await gates.waiting(g1);
    await page.locator('.topbar .quick-more').click();
    const opened = await page.locator('.popover').first().waitFor({ timeout: 5_000 }).then(() => true).catch(() => false);
    check('the chevron opens the project picker', opened);
    const before = await anchorGap();
    await gates.release(g1);
    await idle(page);
    await page.waitForTimeout(300);
    const stillOpen = opened && (await page.locator('.popover').count()) === 1;
    check('the chat list was big enough to scroll (the case the bug needed)', overflows);
    check('a reply streaming under the open picker does not close it', stillOpen);
    const after = await anchorGap();
    check('the picker stays attached to the button that opened it', !!after && !!before && Math.abs(after.dx - before.dx) < 4 && Math.abs(after.dy - before.dy) < 4,
      stillOpen ? `moved ${after?.dx - before?.dx}px across, ${after?.dy - before?.dy}px down` : 'the picker was gone');
    if (stillOpen) {
      await page.mouse.move(640, 260);
      await page.mouse.wheel(0, -400);
      await page.waitForTimeout(300);
      check('scrolling the chat under the picker keeps it open', (await page.locator('.popover').count()) === 1);
    } else check('scrolling the chat under the picker keeps it open', false, 'the picker had already closed');
    await shot(page, 'ui-composer-03-picker-survives');
    await page.keyboard.press('Escape');
    await page.waitForTimeout(150);
    check('Esc still closes the picker', (await page.locator('.popover').count()) === 0);
    // the composer's project picker (a Popover): same rule, and it has to keep up with its button
    const projBtn = page.locator('.composer button[aria-label="Project"]').first();
    await projBtn.click();
    const pop = page.locator('.popover');
    if (await pop.first().waitFor({ timeout: 5_000 }).then(() => true).catch(() => false)) {
      const popAt = async () => {
        const a = await projBtn.boundingBox();
        const p = await pop.first().boundingBox().catch(() => null);
        if (!a || !p) return null;
        return { dx: Math.round(p.x - a.x), gap: Math.round(a.y - (p.y + p.height)) }; // placement puts the bottom 6px above the button
      };
      const pBefore = await popAt();
      check('the project picker opens above its button', !!pBefore && Math.abs(pBefore.gap - 6) < 4, `${pBefore?.gap}px above`);
      const g2 = `pop2-${STAMP}`;
      await rpc('agent.send', { sessionId: chat.id, text: `A third answer, streamed under the open project picker. [s:echo gate=${g2}]` });
      await gates.waiting(g2);
      await gates.release(g2);
      await page.locator('.composer.running').waitFor({ timeout: 2_000 }).catch(() => {});
      await idle(page);
      await page.waitForTimeout(300);
      const popOpen = (await page.locator('.popover').count()) === 1;
      check('a reply streaming under the project picker does not close it', popOpen);
      const pAfter = await popAt();
      check('the project picker stays on its button', popOpen && !!pBefore && !!pAfter && Math.abs(pAfter.gap - pBefore.gap) < 4 && Math.abs(pAfter.dx - pBefore.dx) < 4,
        popOpen ? `${pBefore.dx}px across / ${pBefore.gap}px above → ${pAfter?.dx}px across / ${pAfter?.gap}px above` : 'the popover had closed');
      await page.keyboard.press('Escape');
    } else check('the project picker opens above its button', false, 'it did not open');
    await page.setViewportSize({ width: 1600, height: 1000 });
    await openStripTab(page, 'left', 'Projects');
    await favRow.hover();
    await favRow.locator('button[title^="Remove from favorites"]').click();
    await favRow.hover();
    await favRow.locator('button[title^="Add to favorites"]').waitFor({ timeout: 5_000 }).catch(() => {});
    check('the favorite is given back', (await favRow.locator('button[title^="Add to favorites"]').count()) === 1);
    await openStripTab(page, 'left', 'Sessions');

    // ---- tabs: middle-click closes one
    await newTab(page);
    const tabsBefore = await page.locator('.topbar .tab').count();
    await page.keyboard.press('Control+Tab');
    await page.waitForTimeout(150);
    await page.locator('.topbar .tab').first().click({ button: 'middle' });
    await page.waitForFunction((n) => document.querySelectorAll('.topbar .tab').length === n - 1, tabsBefore, { timeout: 5_000 }).catch(() => {});
    check('middle-click closes tab', (await page.locator('.topbar .tab').count()) === tabsBefore - 1, `${tabsBefore} → ${await page.locator('.topbar .tab').count()}`);

    // ---- tab dots: a run that ends (or fails) behind another tab
    const TITLE = `hi dots ${STAMP}`;
    const tabOf = () => page.locator('.topbar .tab', { has: page.locator('.tab-title', { hasText: TITLE }) }).first();
    const dot = async () => (await tabOf().locator('.np-dot').getAttribute('data-status').catch(() => null)) ?? 'none';
    const dotIs = (status) => page.waitForFunction(([t, s]) => [...document.querySelectorAll('.topbar .tab')]
      .find((x) => x.querySelector('.tab-title')?.textContent.includes(t))?.querySelector('.np-dot')?.dataset.status === s, [TITLE, status], { timeout: 15_000 }).catch(() => {});
    await newTab(page);
    const g3 = `dots-${STAMP}`;
    await ta.fill(`${TITLE} [s:echo gate=${g3}]`);
    await ta.press('Enter');
    await gates.waiting(g3);
    const dotsId = await activeId(page);
    made.push(dotsId);
    await page.waitForFunction((t) => [...document.querySelectorAll('.topbar .tab .tab-title')].some((x) => x.textContent.includes(t)), TITLE, { timeout: 10_000 }).catch(() => {});
    await newTab(page); // an empty chat in front: the first one runs behind it
    check('tab dots: a chat running behind another tab shows running', (await dot()) === 'running', await dot());
    await gates.release(g3);
    await dotIs('unread');
    check('tab dots: a run that ended behind another tab leaves the unread dot', (await dot()) === 'unread', await dot());
    await tabOf().click();
    await dotIs('idle');
    check('tab dots: opening the tab clears it', (await dot()) === 'idle', await dot());
    // a run that fails behind another tab: the error dot (the run stays the failed one, so opening the tab keeps it)
    await newTab(page);
    await rpc('agent.send', { sessionId: dotsId, text: '[s:fail] once more' });
    await dotIs('error');
    check('tab dots: a run that failed behind another tab shows the error dot', (await dot()) === 'error', await dot());
    await tabOf().click();
    // the chat says what failed; the provider's request id is kept out of it and on the call in Diagnostics (idea-qz1a5z)
    const notice = page.locator('.content:visible .item[data-kind="notice"]', { hasText: 'HTTP 400' });
    await notice.first().waitFor({ timeout: 5_000 }).catch(() => {});
    check('tab dots: the failure is an error notice in the chat, with the reason', (await notice.count()) === 1 && /rejected on purpose/.test(await notice.innerText()),
      await notice.first().innerText().catch(() => 'no notice'));
    await openStripTab(page, 'right', 'Diagnostics');
    await page.locator('.diag .views button[data-value="calls"]').click();
    const failedCall = page.locator('.diag .call[data-state="error"]', { hasText: 'rejected on purpose' }).first();
    await failedCall.waitFor({ timeout: 10_000 }).catch(() => {});
    await failedCall.click().catch(() => {});
    await page.waitForTimeout(500);
    const callText = await page.locator('.diag').innerText().catch(() => '');
    check("tab dots: the failed call in Diagnostics names the provider's request id", /req_mock_\d+/.test(callText), callText.match(/.{0,60}req_mock_\d+.{0,20}/)?.[0] ?? 'no request id');
    await page.locator('.diag .views button[data-value="plugins"]').click(); // the Calls view polls: leave it
    await openStripTab(page, 'right', 'Work');
    await shot(page, 'ui-composer-04-tab-dots');

    // ---- composer: narrow widths. The bar's buttons and pickers give up their labels as the composer narrows; nothing may
    // push past the composer's edge, and send stays on screen
    const probe = () => page.evaluate(() => {
      const bar = document.querySelector('.composer .bar');
      if (!bar) return null;
      const box = bar.getBoundingClientRect();
      const sendBox = bar.querySelector('.send')?.getBoundingClientRect();
      const clipped = [...bar.children]
        .filter((c) => c.offsetWidth > 0)
        .filter((c) => c.getBoundingClientRect().right > box.right + 1 || (c.scrollWidth > c.clientWidth + 1 && !c.matches('.pick, .pick *')))
        .map((c) => c.getAttribute('aria-label') || c.className.split(' ')[0]);
      return { width: Math.round(box.width), overflow: bar.scrollWidth - bar.clientWidth, sendInside: !!sendBox && sendBox.right <= box.right + 1 && sendBox.width > 0, clipped };
    });
    const rows = [];
    const bad = [];
    for (const w of [1100, 900, 760, 640, 560, 480, 420, 380, 340]) {
      await page.setViewportSize({ width: w, height: 800 });
      await page.waitForTimeout(200);
      const r = await probe();
      if (!r) { bad.push(`${w}px: no composer bar`); continue; }
      rows.push(`${w}→${r.width}px${r.overflow > 1 ? ` +${r.overflow}` : ''}`);
      if (r.overflow > 1 || !r.sendInside || r.clipped.length) bad.push(`window ${w}px (bar ${r.width}px): overflow ${r.overflow}px, send ${r.sendInside ? 'in' : 'OUT'}${r.clipped.length ? `, past the edge: ${r.clipped.join(', ')}` : ''}`);
    }
    check('composer: nothing in the bar overflows, and send stays inside, from a wide window down to 340px', bad.length === 0, bad.join(' | ') || rows.join(', '));
    await page.setViewportSize({ width: 1600, height: 1000 });

    // ---- drag to reorder tabs
    const titles = () => page.locator('.topbar .tab .tab-title').allTextContents();
    const order = await titles();
    await page.locator('.topbar .tab').nth(1).dragTo(page.locator('.topbar .tab').nth(0), { targetPosition: { x: 5, y: 10 } });
    await page.waitForTimeout(200);
    const reordered = await titles();
    check('drag reorders tabs', order.length >= 2 && reordered[0] === order[1], `${order.join(' | ')} → ${reordered.join(' | ')}`);

    // ---- composer drafts: unsent attachments live outside the per-chat cache (a 5-store LRU); a chat evicted from it
    // and loaded again on return still has them
    await newTab(page);
    const sid = await activeId(page);
    const loads = (n = 0) => socket.sent('sessions.messages', n).filter((f) => f.msg?.p?.id === sid).length;
    const png = await page.evaluate(() => {
      const c = document.createElement('canvas');
      c.width = 48;
      c.height = 32;
      const g = c.getContext('2d');
      g.fillStyle = '#7c93ff';
      g.fillRect(0, 0, 48, 32);
      return c.toDataURL('image/png').split(',')[1];
    });
    await page.locator('.composer input[type=file]').setInputFiles({ name: 'draft.png', mimeType: 'image/png', buffer: Buffer.from(png, 'base64') });
    await page.locator('.composer .thumb').waitFor({ timeout: 5_000 });
    check('draft: an attached image shows as a chip', (await page.locator('.composer .thumb').count()) === 1);
    const evictors = [];
    for (let i = 0; i < 5; i++) {
      await newTab(page);
      evictors.push(await activeId(page));
    }
    const mark = socket.mark();
    await page.locator(`.topbar .tab[data-tab="${sid}"]`).click();
    await page.locator('.composer .thumb').first().waitFor({ timeout: 5_000 }).catch(() => {});
    await page.waitForTimeout(300);
    check('draft: the chat store was evicted (a fresh load on return)', loads(mark) > 0, `sessions.messages for ${sid} on return: ${loads(mark)}`);
    check('draft: the attachment chip survives cache eviction', (await page.locator('.composer .thumb').count()) === 1);
    await shot(page, 'ui-composer-05-draft');
    const many = Array.from({ length: 8 }, (_, i) => ({ name: `img-${i}.png`, mimeType: 'image/png', buffer: Buffer.from(png, 'base64') }));
    await page.locator('.composer input[type=file]').setInputFiles(many);
    await page.waitForFunction(() => document.querySelectorAll('.composer .thumb').length === 6, null, { timeout: 5_000 }).catch(() => {});
    const thumbs = await page.locator('.composer .thumb').evaluateAll((els) => els.map((e) => e.title));
    check('draft: a draft keeps at most six attachments, the newest', thumbs.length === 6 && thumbs[0] === 'img-2.png' && thumbs.at(-1) === 'img-7.png', thumbs.join(' '));
    await page.locator('.composer .thumb-x').first().click();
    check('draft: removing a chip clears it', (await page.locator('.composer .thumb').count()) === 5);
    for (const id of evictors) await page.locator(`.topbar .tab[data-tab="${id}"] .tab-close`).click().catch(() => {});
  } finally {
    for (const k of await settingsAgents()) if (!agentsBefore.includes(k)) await rpc('settings.set', { path: `agents.${k}`, value: null }).catch(() => {});
    for (const id of made) if (id) await rpc('sessions.delete', { id }).catch(() => {});
    await rpc('projects.delete', { id: project.id }).catch(() => {});
  }
}, { viewport: { width: 1600, height: 1000 }, freshTabs: true, tap: true, keepsSettings: true });
