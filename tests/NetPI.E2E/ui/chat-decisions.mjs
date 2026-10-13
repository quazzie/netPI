#!/usr/bin/env node
// What a chat asks the user, against a REAL netpi-server + MockLlm (test "ui.chat-decisions", moved from the mock suite's
// sections "ask_user: questions in the chat", "guardrails: a tool call waits for your OK", "fork: a new chat from a
// message", "goals" and "plan mode: the pill, the plan card and its decisions"). The model's side is MockLlm's [s:ask],
// [s:guard], [s:goal] and [s:planui]; the push the guardrail asks about goes to a bare repository of the test's own.
//
//   node tests/NetPI.E2E/ui/chat-decisions.mjs --url http://127.0.0.1:7470 --token e2e-token --mock <url> --work <dir>
import fs from 'node:fs';
import path from 'node:path';
import { execFileSync } from 'node:child_process';
import { run, check, shot, openApp, openSession, rpc, send, gates, newDir, STAMP } from './lib.mjs';

const git = (cwd, ...args) => execFileSync('git', args, { cwd, stdio: 'pipe', encoding: 'utf8' });
const origin = newDir(`dec-origin-${STAMP}.git`);
git(origin, 'init', '--bare', '-q', '-b', 'main');
const dir = newDir(`dec-${STAMP}`);
git(dir, 'init', '-q', '-b', 'main');
git(dir, 'config', 'user.name', 'e2e');
git(dir, 'config', 'user.email', 'e2e@example.invalid');
fs.writeFileSync(path.join(dir, 'README.md'), '# Decisions\n');
git(dir, 'add', '-A');
git(dir, 'commit', '-q', '-m', 'first');
git(dir, 'remote', 'add', 'origin', origin);
const project = await rpc('projects.create', { name: `dec-${STAMP}`, path: dir });
const TITLE = `Decisions chat ${STAMP}`;
const chat = await rpc('sessions.create', { title: TITLE, projectId: project.id });
await send(chat.id, 'After a reconnect, re-subscribe to the open chats. [s:echo]');

await run('chat-decisions', async ({ page }) => {
  const commandsBefore = (await rpc('settings.get')).settings.guardrails?.commands;
  try {
    await openApp(page);
    await openSession(page, TITLE);
    const ta = page.locator('.composer textarea');
    const idle = () => page.waitForFunction(() => !document.querySelector('.composer.running'), null, { timeout: 30_000 }).catch(() => {});
    const say = async (text) => {
      await ta.fill(text);
      await ta.press('Enter');
    };

    // ---- ask_user: questions in the chat
    await say('[s:ask n=2] fix the reconnect');
    const askCard = page.locator('.item[data-kind="ask"] .card').last();
    await askCard.waitFor({ timeout: 15_000 }).catch(() => {});
    const kinds = await page.locator('.content:visible .item').evaluateAll((els) => els.slice(-2).map((e) => e.dataset.kind));
    check('ask_user: the questions come after the message that explains them', kinds.join() === 'text,ask', kinds.join());
    check('ask_user: the message box answers', ((await ta.getAttribute('placeholder')) ?? '').startsWith('Answer the question'), await ta.getAttribute('placeholder'));
    check('ask_user: the chat tab shows that a question waits', (await page.locator('.tab.active .np-dot[data-status="asking"]').count()) === 1);
    await askCard.locator('.opt', { hasText: 'Rewrite the client' }).click();
    await askCard.locator('.opt', { hasText: 'unit' }).click();
    await shot(page, 'ui-decisions-01-ask');
    await askCard.locator('.foot button', { hasText: 'Send' }).click();
    const line = page.locator('.item[data-kind="ask"] button.line').last();
    await line.waitFor({ timeout: 5_000 }).catch(() => {});
    check('ask_user: answered, the card is one line: question → answer', ((await line.count()) ? await line.innerText() : '').includes('Rewrite the client'));
    await idle();
    await say('[s:ask] again');
    await page.locator('.item[data-kind="ask"] .card').last().waitFor({ timeout: 15_000 }).catch(() => {});
    await say('Neither: a heartbeat');
    await page.waitForFunction(() => !document.querySelector('.item[data-kind="ask"] .card'), null, { timeout: 5_000 }).catch(() => {});
    check("ask_user: typed in the message box, the answer is the user's own words", (await page.locator('.item[data-kind="ask"] button.line').last().innerText()).includes('Neither: a heartbeat'));
    await idle();

    // ---- guardrails: a tool call waits for your OK (a rule that asks before a push)
    const defaults = (await rpc('settings.schema')).flatMap((sec) => sec.settings ?? []).find((x) => x.key === 'guardrails.commands')?.default ?? [];
    await rpc('settings.set', { path: 'guardrails.commands', value: [...(commandsBefore ?? defaults), 'ask: ^git push'] });
    await say('[s:guard] push it');
    const bar = page.locator('.tool .approve').last();
    await bar.waitFor({ timeout: 15_000 }).catch(() => {});
    check('guardrails: the tool row asks for your OK', (await bar.count()) === 1 && (await bar.innerText()).includes('ask: ^git push'), await bar.innerText().catch(() => 'no approval row'));
    check('guardrails: the chat tab shows that something waits', (await page.locator('.tab.active .np-dot[data-status="asking"]').count()) === 1);
    await shot(page, 'ui-decisions-02-guardrail');
    await bar.locator('button', { hasText: /^Allow$/ }).click();
    await idle();
    check('guardrails: allowed, it ran', (await page.locator('.content:visible .item').last().innerText()).includes('Pushed'), await page.locator('.content:visible .item').last().innerText());
    // allowed for this chat: the next push runs without asking, its row says why
    await say('[s:guard] push it');
    const bar2 = page.locator('.tool .approve').last();
    await bar2.waitFor({ timeout: 15_000 }).catch(() => {});
    await bar2.locator('button', { hasText: 'Allow in this chat' }).click();
    await idle();
    const asksBefore = await page.locator('.tool .approve').count();
    await say('[s:guard] push it');
    await page.locator('.composer.running').waitFor({ timeout: 5_000 }).catch(() => {});
    await idle();
    check('guardrails: allowed in this chat, the next push does not ask', asksBefore === 0 && (await page.locator('.tool .approve').count()) === 0);
    check('guardrails: its row says it was allowed in this chat', (await page.locator('.tool .state', { hasText: 'allowed in this chat' }).count()) > 0);

    // ---- fork: a new chat from a message
    const answer = page.locator('.assistant', { has: page.locator('.foot') }).last();
    await answer.hover();
    await answer.locator('.foot button[title^="Fork"]').click();
    await page.waitForFunction(() => /\(fork/.test(document.querySelector('.tab.active')?.textContent ?? ''), null, { timeout: 10_000 }).catch(() => {});
    check('fork: after an answer, the fork opens in a tab of its own', /\(fork/.test(await page.locator('.tab.active').innerText()), await page.locator('.tab.active').innerText());
    const crumb = page.locator('.header .crumb');
    check('fork: its header leads back to the original', ((await crumb.count()) ? await crumb.innerText() : '').includes(TITLE));
    await crumb.click();
    const firstUser = page.locator('.user', { hasText: 'After a reconnect' }).first();
    await firstUser.waitFor({ timeout: 10_000 });
    await firstUser.hover();
    await firstUser.locator('button[title^="Fork"]').click();
    await page.waitForFunction(() => document.querySelector('.composer textarea')?.value.startsWith('After a reconnect'), null, { timeout: 10_000 }).catch(() => {});
    check("fork: before a user message, its text waits in the new chat's box", (await ta.inputValue()).startsWith('After a reconnect'), await ta.inputValue());
    await ta.fill('');

    // ---- goals: set with /goal, paused while a pass runs, resumed, achieved, dismissed
    const before = await page.locator('.topbar .tab').count();
    await page.keyboard.press('Control+t');
    await page.waitForFunction((n) => document.querySelectorAll('.topbar .tab').length > n, before, { timeout: 10_000 });
    await page.locator('.intro').waitFor({ timeout: 10_000 });
    const g = `goal-${STAMP}`;
    await say(`/goal Make the demo page render again [s:goal gate=${g}]`);
    await page.locator('.goal[data-status="active"]').waitFor({ timeout: 10_000 }).catch(() => {});
    check('/goal sets the goal and the strip shows it', (await page.locator('.goal .obj').innerText().catch(() => '')).includes('Make the demo page render again'));
    await gates.waiting(g);
    await page.locator('.goal .act[title^="Pause"]').click();
    await page.locator('.goal[data-status="paused"]').waitFor({ timeout: 5_000 }).catch(() => {});
    await gates.release(g);
    await idle();
    await page.waitForTimeout(500);
    check('pausing lets the current pass finish and starts no new one',
      (await page.locator('.notice', { hasText: 'automatic continuation' }).count()) === 0 && (await page.locator('.goal .reason').innerText().catch(() => '')).includes('Paused by the user'),
      await page.locator('.goal .reason').innerText().catch(() => ''));
    await shot(page, 'ui-decisions-03-goal-paused');
    await page.locator('.goal .act[title="Resume"]').click();
    await page.locator('.goal[data-status="complete"]').waitFor({ timeout: 30_000 }).catch(() => {});
    await idle();
    const goalNotices = await page.locator('.notice', { has: page.locator('.label', { hasText: /^Goal$/ }) }).count();
    check('goal notices: set, resumed, one automatic continuation', goalNotices === 3, String(goalNotices));
    check('the goal_update row shows complete', (await page.locator('.tool .np-badge', { hasText: 'complete' }).count()) >= 1);
    check('the strip says achieved, with the summary', (await page.locator('.goal .state').innerText().catch(() => '')) === 'Achieved'
      && (await page.locator('.goal .reason').innerText().catch(() => '')).includes('The page renders'), await page.locator('.goal').innerText().catch(() => 'no strip'));
    await shot(page, 'ui-decisions-04-goal-achieved');
    await page.locator('.goal .act[title="Dismiss"]').click();
    await page.locator('.goal').waitFor({ state: 'detached', timeout: 5_000 }).catch(() => {});
    check('dismissing removes the strip', (await page.locator('.goal').count()) === 0);

    // ---- schedules: an agent's add waits for the user's OK on the card (allow / discard)
    await say('[s:schedule]');
    const schedCard = page.locator('.item[data-kind="schedule"] .card').last();
    await schedCard.waitFor({ timeout: 15_000 }).catch(() => {});
    check('schedules: an agent add is a card of its own, with the proposal and the decision', (await schedCard.count()) === 1 && (await schedCard.innerText()).includes('Mock digest') && (await schedCard.innerText()).includes('every 60 min'),
      await schedCard.innerText().catch(() => 'no card'));
    check('schedules: the chat tab shows that a decision waits', (await page.locator('.tab.active .np-dot[data-status="asking"]').count()) === 1);
    await schedCard.locator('.foot button', { hasText: /^Allow$/ }).click();
    await idle();
    await page.waitForFunction(() => [...document.querySelectorAll('.item[data-kind="schedule"] .line')].at(-1)?.textContent.includes('allowed'), null, { timeout: 10_000 }).catch(() => {});
    check('schedules: allowed, the card is one line', (await page.locator('.item[data-kind="schedule"] .line').last().innerText().catch(() => '')).includes('allowed'),
      await page.locator('.item[data-kind="schedule"] .line').last().innerText().catch(() => 'no line'));
    let list = (await rpc('schedules.list', {}).catch(() => [])) ?? [];
    check('schedules: the allowed one is enabled', list.some((s) => s.name === 'Mock digest' && s.enabled), JSON.stringify(list));
    // the model's next turn proposes a second schedule (the scenario adds one after the allow): discard it
    const schedCard2 = page.locator('.item[data-kind="schedule"] .card').last();
    await schedCard2.waitFor({ timeout: 15_000 }).catch(() => {});
    check('schedules: the second add is a card of its own', (await schedCard2.count()) === 1 && (await schedCard2.innerText()).includes('Summarise the open chats, again'),
      await schedCard2.innerText().catch(() => 'no card'));
    await schedCard2.locator('.foot button', { hasText: 'Discard' }).click();
    await idle();
    check('schedules: discarded, the card is one line', (await page.locator('.item[data-kind="schedule"] .line').last().innerText().catch(() => '')).includes('discarded'),
      await page.locator('.item[data-kind="schedule"] .line').last().innerText().catch(() => 'no line'));
    list = (await rpc('schedules.list', {}).catch(() => [])) ?? [];
    check('schedules: the discarded one is gone, the allowed one stays', list.filter((s) => s.name === 'Mock digest').length === 1 && list.some((s) => s.name === 'Mock digest' && s.prompt === 'Summarise the open chats' && s.enabled), JSON.stringify(list));
    await shot(page, 'ui-decisions-07-schedule');

    // ---- plan mode: the pill, the plan card and its decisions (last: approving in a new chat archives this chat). The
    // chat is opened by its tab: its forks' rows carry its title too.
    await page.locator(`.topbar .tab[data-tab="${chat.id}"]`).click();
    await page.waitForFunction((id) => document.querySelector('.topbar .tab.active')?.dataset.tab === id, chat.id, { timeout: 5_000 }).catch(() => {});
    const pill = page.locator('.composer .bar button.pick[data-state]');
    const card = page.locator('.item[data-kind="plan"] .card').last();
    check('plan: the composer has a Plan pill, off', (await pill.getAttribute('data-state')) === 'off' && (await pill.innerText()).trim() === 'Plan');
    await pill.click();
    await page.waitForFunction(() => document.querySelector('.composer .bar button.pick[data-state]')?.dataset.state === 'planning', null, { timeout: 5_000 }).catch(() => {});
    check('plan: clicking it puts the chat in plan mode (Planning)', (await pill.getAttribute('data-state')) === 'planning' && (await pill.innerText()).trim() === 'Planning');
    await say('go [s:planui]');
    await card.waitFor({ timeout: 15_000 }).catch(() => {});
    check('plan: the plan is a card of its own in the chat, with its steps and files', (await card.locator('.steps li').count()) === 3 && (await card.locator('.files li').count()) === 2);
    await card.locator('.foot button', { hasText: 'Revise' }).waitFor({ timeout: 5_000 }).catch(() => {}); // the buttons come once the plugin says the plan waits
    check('plan: the card has the decisions', (await card.locator('.foot button', { hasText: 'Approve in new chat' }).count()) === 1 && (await card.locator('.foot button', { hasText: 'Revise' }).count()) === 1);
    check('plan: the pill says the plan is ready, and the tab says the chat waits for you', (await pill.getAttribute('data-state')) === 'awaiting' && (await page.locator('.tab.active .np-dot[data-status="asking"]').count()) === 1);
    await shot(page, 'ui-decisions-05-plan');
    // revise: the feedback goes back, the next revision is a new card and the first becomes one line
    await card.locator('.foot button', { hasText: 'Revise' }).click();
    await card.locator('textarea').fill('also log the reconnect');
    await card.locator('.foot button', { hasText: 'Send changes' }).click();
    await page.waitForFunction(() => document.querySelector('.item[data-kind="plan"] .card .rev')?.textContent.includes('revision 2'), null, { timeout: 15_000 }).catch(() => {});
    const second = page.locator('.item[data-kind="plan"] .card').last();
    check('plan: revise answers with a second revision that has the change', (await second.locator('.steps li').count()) === 4 && (await second.innerText()).includes('also log the reconnect'));
    check('plan: the first revision is one line: changes asked', (await page.locator('.item[data-kind="plan"] button.line').first().innerText().catch(() => '')).includes('changes asked: also log the reconnect'),
      await page.locator('.item[data-kind="plan"] button.line').first().innerText().catch(() => 'no line'));
    // save as idea stays on the card; approve ends plan mode
    await second.locator('.link', { hasText: 'Save as idea' }).click();
    await page.waitForTimeout(500);
    check('plan: save as idea does not decide', (await page.locator('.item[data-kind="plan"] .card').count()) === 1);
    await second.locator('.foot button', { hasText: /^Approve$/ }).click();
    await idle();
    await page.waitForFunction(() => [...document.querySelectorAll('.item[data-kind="plan"] button.line')].at(-1)?.textContent.includes('approved'), null, { timeout: 5_000 }).catch(() => {});
    const approved = page.locator('.item[data-kind="plan"] button.line').last();
    check('plan: approved, the card is one line', (await approved.innerText()).includes('approved'));
    check('plan: the pill says the plan was approved', (await pill.getAttribute('data-state')) === 'approved', await pill.getAttribute('data-state'));
    await approved.click();
    check('plan: the line opens to the plan and the idea it was saved as', /idea-[a-z0-9]+/.test(await page.locator('.item[data-kind="plan"] .opened').last().innerText().catch(() => '')),
      (await page.locator('.item[data-kind="plan"] .opened').last().innerText().catch(() => '')).slice(0, 160));
    // the agent offers plan mode: not now, then yes; cancel leaves the mode
    await say('[s:planui enter=1] a bigger change');
    const offer = page.locator('.item[data-kind="planenter"] .card').last();
    await offer.waitFor({ timeout: 15_000 }).catch(() => {});
    check('plan: the offer is a card with the reason and two answers', (await offer.locator('.foot button').count()) === 2 && (await offer.innerText()).includes('reconnect path'));
    await offer.locator('.foot button', { hasText: 'Not now' }).click();
    await idle();
    check('plan: declined, the offer is one line', (await page.locator('.item[data-kind="planenter"] .line').last().innerText().catch(() => '')).includes('not now'),
      await page.locator('.item[data-kind="planenter"] .line').last().innerText().catch(() => 'no line'));
    await say('[s:planui enter=1] again');
    await page.locator('.item[data-kind="planenter"] .card').last().waitFor({ timeout: 15_000 }).catch(() => {});
    await page.locator('.item[data-kind="planenter"] .card .foot button', { hasText: 'Enter plan mode' }).last().click();
    await page.waitForFunction(() => document.querySelector('.composer .bar button.pick[data-state]')?.dataset.state === 'awaiting', null, { timeout: 15_000 }).catch(() => {});
    check('plan: entering the mode lets the plan follow', (await pill.getAttribute('data-state')) === 'awaiting', await pill.getAttribute('data-state'));
    await page.locator('.item[data-kind="plan"] .card .link', { hasText: 'Cancel' }).last().click();
    await idle();
    await page.waitForFunction(() => document.querySelector('.composer .bar button.pick[data-state]')?.dataset.state === 'off', null, { timeout: 5_000 }).catch(() => {});
    check('plan: cancelled, the pill is off again', (await pill.getAttribute('data-state')) === 'off', await pill.getAttribute('data-state'));
    check('plan: the cancelled plan is one line', (await page.locator('.item[data-kind="plan"] button.line').last().innerText()).includes('cancelled'));
    // approve in a new chat: the new chat takes this tab's place and starts from the plan; this chat is archived
    await pill.click();
    await page.waitForFunction(() => document.querySelector('.composer .bar button.pick[data-state]')?.dataset.state === 'planning', null, { timeout: 5_000 }).catch(() => {});
    await say('once more [s:planui]');
    await card.waitFor({ timeout: 15_000 }).catch(() => {});
    await page.locator('.item[data-kind="plan"] .card .foot button', { hasText: 'Approve in new chat' }).last().click();
    await page.waitForFunction(() => [...document.querySelectorAll('.tab')].some((t) => t.textContent.includes('Plan: Re-subscribe')), null, { timeout: 15_000 }).catch(() => {});
    const tabs = await page.locator('.topbar .tab').evaluateAll((els) => els.map((e) => ({ id: e.dataset.tab, title: e.textContent.trim() })));
    check("plan: the new chat took the plan chat's tab", tabs.some((t) => t.title.includes('Plan: Re-subscribe')) && !tabs.some((t) => t.id === chat.id),
      tabs.map((t) => `${t.title}${t.id === chat.id ? ' (the plan chat)' : ''}`).join(' | '));
    await page.waitForFunction(() => [...document.querySelectorAll('.content')].some((c) => c.offsetParent && c.textContent.includes('Carry out this approved plan')), null, { timeout: 15_000 }).catch(() => {});
    const first = page.locator('.item[data-kind="user"]', { hasText: 'Carry out this approved plan' }).last();
    check('plan: the new chat starts from the plan', (await first.count()) === 1 && (await first.innerText()).includes('Re-subscribe to the open chats'));
    await shot(page, 'ui-decisions-06-plan-new-chat');
    await idle();
  } finally {
    await rpc('settings.set', { path: 'guardrails.commands', value: commandsBefore ?? null }).catch(() => {});
    const all = (await rpc('sessions.list', { includeArchived: true, includeSubagents: true, limit: 500 }).catch(() => [])) ?? [];
    for (const s of all) if (s.projectId === project.id || s.title?.includes(STAMP) || s.title?.includes('Make the demo page render again')) await rpc('sessions.delete', { id: s.id }).catch(() => {});
    const ideas = (await rpc('ideas.list', { projectId: project.id }).catch(() => null))?.ideas ?? [];
    for (const i of ideas) await rpc('ideas.delete', { id: i.id }).catch(() => {});
    await rpc('projects.delete', { id: project.id }).catch(() => {});
  }
}, { viewport: { width: 1600, height: 1000 }, freshTabs: true, keepsSettings: true });
