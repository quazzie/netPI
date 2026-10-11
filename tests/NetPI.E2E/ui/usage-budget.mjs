#!/usr/bin/env node
// UI test: what the models cost, on a real server — the Usage tab (right panel), a chat's cost in the composer, the budget
// on the Work tab, a chat the budget stopped and the budget pill in the top bar. The test sets up a paid agent (a price on
// a Claude model the mock answers) and a free one on qwen, and the costs come from real calls. A second budget period
// comes from moving budget.resetDay: the current period is rolled up again from its calls and the old one keeps its own
// rows. The agents and the budget are put back after (keepsSettings).
//
//   node tests/NetPI.E2E/ui/usage-budget.mjs --url http://127.0.0.1:7470 --token e2e-token --work <dir>
// Prints one JSON line at the end: { ok, checks: [{name, ok, detail}], errors: [...] }.
import { run, check, shot, openApp, openStripTab, openSession, rpc, send, newDir, until, waitIdle, STAMP } from './lib.mjs';

const CLAUDE = 'anthropic/claude-haiku-4-5';
const QWEN = 'aiproxy/qwen3.8-27b';
const PAID = `wp-${STAMP}`;
const FREE = `wf-${STAMP}`;
const LANE = `Lane scheduler hardening ${STAMP}`;
const NOTES = `Summarize the changelog ${STAMP}`;

// web/src/lib/format.js usd(): the figures are checked as the page writes them
const usd = (v) => {
  if (v == null || !Number.isFinite(v)) return '';
  if (v === 0) return '$0';
  if (v >= 1 && Math.abs(v - Math.round(v)) < 0.005) return `$${Math.round(v)}`;
  return `$${v.toFixed(v >= 0.01 ? 2 : 4)}`;
};
const text = async (loc) => ((await loc.innerText().catch(() => '')) ?? '').replace(/ /g, ' ').trim();

await run('usage-budget', async ({ page }) => {
  const dir = newDir(`usage-${STAMP}`);
  const project = await rpc('projects.create', { name: `usage-${STAMP}`, path: dir });
  const before = (await rpc('settings.get')).settings;
  const sessions = [];
  const chat = async (title, model) => {
    const s = await rpc('sessions.create', { title, projectId: project.id, model });
    sessions.push(s.id);
    return s.id;
  };
  try {
    await rpc('settings.set', { path: `agents.${PAID}`, value: { model: CLAUDE, cost: { input: 30, output: 150 }, use: 'the e2e usage test: paid' } });
    await rpc('settings.set', { path: `agents.${FREE}`, value: { model: QWEN, use: 'the e2e usage test: free' } });

    // the costs: a chat with a subagent on the paid agent, a cheaper one, and one on the free agent
    const lane = await chat(LANE, CLAUDE);
    await send(lane, `Split the scheduler review. [s:spawn n=1 delay=100 agent=${PAID}]`, 60_000);
    const notes = await chat(NOTES, CLAUDE);
    await send(notes, 'Short, please. [s:say]');
    const local = await chat(`Local notes ${STAMP}`, QWEN);
    await send(local, '[s:say]');

    await openApp(page);
    await openStripTab(page, 'right', 'Usage');
    const u = page.locator('.panel.right .usage');
    await u.locator('.top .big').waitFor({ timeout: 10_000 });
    const history = async (period) => rpc('usage.history', { ...(period ? { period } : {}), days: 30 });
    const periodCost = (h) => Math.max(0, h.totals.costUsd - (h.period === h.current ? (h.budget?.reservedOrUnsettledUsd ?? 0) : 0));
    let h = await history();
    await until('the tab to show the period', async () => (await text(u.locator('.top .big'))) === usd(periodCost(h = await history())), 10_000).catch(() => {});
    check('usage: the period total is the first thing the tab says', (await text(u.locator('.top .big'))) === usd(periodCost(h)), `${await text(u.locator('.top .big'))} vs ${usd(periodCost(h))}`);
    check('usage: the last 30 days are a bar each', (await u.locator('.chart .bar').count()) === 30);
    const row = (section, name) => u.locator('.np-section', { has: page.locator('.np-section-title', { hasText: section }) }).locator('.row', { has: page.locator('.name', { hasText: name }) }).first();
    const paidCost = h.agents.find((a) => a.agent === PAID)?.costUsd ?? 0;
    check('usage: every agent and model is a row with its cost, a free one says so',
      paidCost > 0 && (await text(row('Agents', PAID).locator('.val'))) === usd(paidCost) && (await text(row('Agents', FREE).locator('.val'))) === 'free'
        && (await text(row('Models', 'claude-haiku-4-5').locator('.val'))).startsWith('$') && (await text(row('Models', 'qwen3.8-27b').locator('.val'))) === 'free',
      `${await text(row('Agents', PAID))} | ${await text(row('Agents', FREE))}`);
    await u.locator('.chat').first().waitFor({ timeout: 10_000 }).catch(() => {});
    const ranked = (await rpc('usage.chats', { period: h.period, limit: 8 })).chats;
    const shown = await u.locator('.chat .name').allInnerTexts();
    const laneCost = ranked.find((c) => c.sessionId === lane)?.costUsd ?? 0;
    check('usage: the chats that cost most are listed, most expensive first, a subagent\'s calls with its chat',
      shown.join('|') === ranked.map((c) => c.title || (c.deleted ? 'deleted chat' : 'untitled')).join('|') && shown.indexOf(LANE) >= 0
        && (shown.indexOf(NOTES) < 0 || shown.indexOf(LANE) < shown.indexOf(NOTES)) && (await text(u.locator('.chat', { hasText: LANE }).locator('.val'))) === usd(laneCost)
        && ranked.every((c, i) => i === 0 || ranked[i - 1].costUsd >= c.costUsd),
      shown.join(' | '));
    await shot(page, 'ui-usage-01-tab');

    // a second period: the month now starts on another day; the old period keeps its rows, and a call after the move
    // counts only in the new one
    const oldPeriod = h.current;
    const resetDay = (before.budget?.resetDay ?? 1) === 2 ? 3 : 2;
    await rpc('settings.set', { path: 'budget.resetDay', value: resetDay });
    await send(notes, 'And the release notes. [s:say]');
    await until('the new period in the tab', async () => {
      const now = await history();
      return now.current !== oldPeriod && (await text(u.locator('.top .big'))) === usd(periodCost(now)) && (await u.locator('.head select option').count()) >= 2;
    }, 15_000).catch(() => {});
    h = await history();
    const picker = u.locator('.head select');
    const options = await picker.locator('option').evaluateAll((els) => els.map((o) => ({ value: o.value, text: o.textContent })));
    check('usage: the picker lists the periods, newest first, the current one chosen and marked "now"',
      options.length >= 2 && options.map((o) => o.value).join() === [...options.map((o) => o.value)].sort().reverse().join()
        && (await picker.inputValue()) === h.current && options.find((o) => o.value === h.current)?.text.includes('now'),
      JSON.stringify(options));
    const current = await text(u.locator('.top .big'));
    const old = h.periods.find((p) => p.period === oldPeriod);
    await picker.selectOption(oldPeriod);
    await until('the old period', async () => (await text(u.locator('.top .big'))) !== current, 10_000).catch(() => {});
    check('usage: another period shows its own total', !!old && (await text(u.locator('.top .big'))) === usd(old.costUsd) && usd(old.costUsd) !== current,
      `${current} → ${await text(u.locator('.top .big'))} (${usd(old?.costUsd)})`);
    check('usage: and no "today" line, which belongs to the current period', (await u.locator('.top .figs', { hasText: 'today' }).count()) === 0);
    await u.locator('.month', { hasText: 'now' }).click(); // the months list chooses the period too: back to the current one
    await until('the current period', async () => (await text(u.locator('.top .big'))) === current, 10_000).catch(() => {});
    check('usage: a month in the list is a click away', (await text(u.locator('.top .big'))) === current && (await u.locator('.top .figs', { hasText: 'today' }).count()) === 1);
    await rpc('settings.set', { path: 'budget.resetDay', value: before.budget?.resetDay ?? null });
    await u.locator('.chat', { hasText: LANE }).click();
    await page.waitForFunction((id) => !!document.querySelector(`.topbar .tab.active[data-tab="${id}"]`), lane, { timeout: 5000 }).catch(() => {});
    check('usage: a chat in the ranking opens', (await page.locator(`.topbar .tab.active[data-tab="${lane}"]`).count()) === 1);

    // ---- the chat's cost in the composer: the chat's own calls and, with them, its subagent's
    const cost = page.locator('.composer .cost');
    await cost.waitFor({ timeout: 5000 }).catch(() => {});
    const own = await rpc('usage.session', { sessionId: lane });
    check('a chat shows what it cost, with its subagents', own.withSubagentsUsd > own.costUsd && (await text(cost)) === usd(Math.max(0, own.withSubagentsUsd - (own.reservedUsd ?? 0))), `${await text(cost)} | ${JSON.stringify(own)}`);
    check('the cost tooltip splits the chat from its subagents', ((await cost.getAttribute('title')) ?? '').includes(`This chat: ${usd(own.costUsd)}`) && ((await cost.getAttribute('title')) ?? '').includes(`with its subagents ${usd(own.withSubagentsUsd)}`), await cost.getAttribute('title'));

    // ---- the Work tab: this month against the budget, refreshed when the limits change while it is open
    const spent = async () => { const b = await rpc('budget.status'); return usd(b.settledUsd ?? b.spentUsd); };
    const monthly = 50;
    await rpc('settings.set', { path: 'budget.monthlyUsd', value: monthly });
    await openStripTab(page, 'right', 'Work');
    const wb = page.locator('.work .usage.budget');
    const wbShows = (want) => until(`the Work tab's budget to say ${want}`, async () => (await text(wb)).includes(want), 5000).catch(() => {});
    await wbShows(`${await spent()} / ${usd(monthly)}`);
    check('the Work tab shows this month against the budget', (await text(wb)).includes(`${await spent()} / ${usd(monthly)}`), await text(wb));
    await rpc('settings.set', { path: 'budget.monthlyUsd', value: 75 });
    await wbShows(' / $75');
    check('a monthly limit change refreshes the open Work tab', (await text(wb)).includes(`${await spent()} / $75`), await text(wb));
    await rpc('settings.set', { path: 'budget.dailyUsd', value: 5 });
    await until('the daily cap', async () => (await text(wb.locator('.umeta'))).includes('/ $5'), 5000).catch(() => {});
    check('a daily limit change refreshes the open Work tab', (await text(wb.locator('.umeta'))).includes('/ $5'), await text(wb));
    const doc = (await rpc('settings.get')).settings;
    await rpc('settings.replace', { settings: { ...doc, budget: { ...doc.budget, monthlyUsd: monthly, dailyUsd: null } } });
    await until('the replaced budget', async () => (await text(wb.locator('.uline'))).includes(` / ${usd(monthly)}`) && !(await text(wb.locator('.umeta'))).includes('/'), 5000).catch(() => {});
    check('a settings replacement refreshes the budget and removes the daily cap', (await text(wb.locator('.uline'))).includes(`${await spent()} / ${usd(monthly)}`) && !(await text(wb.locator('.umeta'))).includes('/'), await text(wb));

    // ---- the budget pill: there once the budget needs attention, and it opens the budget settings
    const pill = page.locator('.budget-pill');
    check('no budget pill below the warning level', (await pill.count()) === 0);
    await rpc('settings.set', { path: 'budget', value: { ...((await rpc('settings.get')).settings.budget ?? {}), monthlyUsd: 0.01, onLimit: 'ask' } });
    await pill.waitFor({ timeout: 5000 }).catch(() => {});
    check('a spent budget shows in the top bar', (await pill.count()) === 1 && ((await pill.getAttribute('class')) ?? '').includes('spent'));
    await shot(page, 'ui-usage-02-pill');
    await pill.click();
    await page.waitForSelector('.dialog', { timeout: 5000 }).catch(() => {});
    check('the pill opens the budget settings', (await text(page.locator('.nav button.active'))).includes('Agents & budget'), await text(page.locator('.nav button.active')));
    await page.keyboard.press('Escape');
    await page.locator('.dialog').waitFor({ state: 'detached', timeout: 3000 }).catch(() => {});

    // ---- a chat the budget stopped (budget.onLimit "ask") offers to let it go over
    const over = await chat(`Go over ${STAMP}`, CLAUDE);
    await send(over, 'Summarize the repo with the paid model. [s:say]');
    await openSession(page, `Go over ${STAMP}`);
    const go = page.locator('.notice button.link', { hasText: 'Let this chat go over' });
    await go.waitFor({ timeout: 5000 }).catch(() => {});
    check('a budget stop offers to let the chat go over', (await go.count()) === 1, await text(page.locator('.notice').last()));
    await shot(page, 'ui-usage-03-budget-stop');
    await go.click();
    await page.locator('.dialog button', { hasText: /^Go over$/ }).click();
    await waitIdle(over, 2, 30_000).catch(() => {});
    const allowed = (await rpc('sessions.get', { id: over })).meta?.budgetAllowedFrom;
    check('going over is recorded for the chat, and the offer goes', !!allowed && (await go.count()) === 0, String(allowed));
    await page.locator('.notice', { hasText: 'go over the budget' }).first().waitFor({ timeout: 5000 }).catch(() => {});
    check('the chat is told to continue, and does', (await page.locator('.notice', { hasText: 'The user let this chat go over the budget' }).count()) === 1
      && (await rpc('sessions.messages', { id: over })).messages.at(-1)?.role === 'assistant');

    await rpc('settings.set', { path: 'budget', value: { ...((await rpc('settings.get')).settings.budget ?? {}), monthlyUsd: 1000 } });
    await pill.waitFor({ state: 'detached', timeout: 5000 }).catch(() => {});
    check('under the warning level again, the pill goes', (await pill.count()) === 0);
  } finally {
    for (const id of sessions) await rpc('agent.abort', { sessionId: id }).catch(() => {});
    for (const id of sessions) await rpc('sessions.delete', { id }).catch(() => {});
    await rpc('projects.delete', { id: project.id }).catch(() => {});
    await rpc('settings.set', { path: 'budget', value: before.budget ?? null }).catch(() => {});
    for (const a of [PAID, FREE]) await rpc('settings.set', { path: `agents.${a}`, value: null }).catch(() => {});
  }
}, { keepsSettings: true });
