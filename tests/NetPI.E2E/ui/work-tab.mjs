#!/usr/bin/env node
// UI test: the Work tab (right panel) on a real server, as a person watching their agents meets it. The test sets up its
// own agents (restored after): one on qwen (two instances, the model's slots) and one on gemma-4, which the mock lists as
// not loaded. Real runs fill the slots: a chat running a foreground command that waits for a file, and a chat whose
// background subagent waits at a MockLlm gate; a third run waits for an instance. Gates and the file let them go one at a
// time, so the rows can be checked to stay where they are. A background command gives the process output.
//
//   node tests/NetPI.E2E/ui/work-tab.mjs --url http://127.0.0.1:7470 --token e2e-token --mock http://127.0.0.1:7479 --work <dir>
// Prints one JSON line at the end: { ok, checks: [{name, ok, detail}], errors: [...] }.
import fs from 'node:fs';
import path from 'node:path';
import { run, check, shot, openApp, openStripTab, rpc, gates, newDir, until, waitIdle, STAMP } from './lib.mjs';

const QWEN = 'aiproxy/qwen3.8-27b';
const Q = `wq-${STAMP}`; // the agent the slots are on
const G = `wg-${STAMP}`; // an agent whose model is not loaded
const Q2 = `wq2-${STAMP}`; // a second agent on qwen, for a run that may go to either
const W = `surveyor-${STAMP}`; // the background subagent's gate
const GC = `check-${STAMP}`; // the gate of a run that waits for an instance (it is cancelled before it gets one)
const GB = `review-${STAMP}`; // the gate of the subagent's chat when it asks again and waits for an instance
const FLAG = '.work-release'; // the foreground command runs until this file is in the project folder
const INDEX = 'Index docs for semantic search';
const REFACTOR = 'Refactor provider retry policy';
const REVIEW = 'Review the backoff math';

await run('work-tab', async ({ page, socket }) => {
  const dir = newDir(`work-${STAMP}`);
  const project = await rpc('projects.create', { name: `work-${STAMP}`, path: dir });
  const sessions = [];
  const chat = async (title) => {
    const s = await rpc('sessions.create', { title, projectId: project.id, model: QWEN });
    sessions.push(s.id);
    return s.id;
  };
  const processes = async () => (await rpc('processes.list')) ?? [];
  const status = async (sessionId) => (await rpc('agent.get', { sessionId }))?.status;
  let bg = null;
  try {
    await rpc('settings.set', { path: `agents.${Q}`, value: { model: QWEN, use: 'the e2e Work tab test' } });
    await rpc('settings.set', { path: `agents.${G}`, value: { model: 'aiproxy/gemma-4', use: 'the e2e Work tab test: not loaded' } });

    // a background command that keeps printing (its run ends at once)
    const dev = await chat('Dev server');
    await rpc('agent.send', { sessionId: dev, text: '[s:bgproc]' });
    await waitIdle(dev, 1, 30_000);
    bg = await until('the background command', async () => (await processes()).find((p) => p.sessionId === dev && p.background && p.status === 'running'), 10_000);

    // the two slots: a chat in a foreground command, and a background subagent (its chat's turn ends while it works)
    const index = await chat(INDEX);
    await rpc('agent.send', { sessionId: index, text: `[s:fgwait file=${FLAG}]` });
    await until('the foreground command', async () => (await processes()).find((p) => p.sessionId === index && p.status === 'running'), 30_000);
    const refactor = await chat(REFACTOR);
    await rpc('agent.send', { sessionId: refactor, text: `[s:spawnbg agent=${Q} name=surveyor workergate=${W} delay=100]` });
    await gates.waiting(W, 30_000);
    await waitIdle(refactor, 1, 30_000);
    const surveyorSid = (await rpc('sessions.list', { includeSubagents: true, parentSessionId: refactor })).find((s) => s.kind === 'subagent')?.id;

    await openApp(page);
    await openStripTab(page, 'right', 'Work');
    const pool = page.locator(`.work .pool[data-agent="${Q}"]`);
    await pool.locator('.slot.busy').nth(1).waitFor({ timeout: 15_000 });
    const summary = async () => (await page.locator('.work .summary').innerText()).replace(/\s+/g, ' ');
    const rowNames = () => pool.locator('.slot').evaluateAll((els) => els.map((e) => (e.classList.contains('free') ? 'free' : e.querySelector('.name')?.textContent)));
    // where the agents, the rows and the section titles are: a job ending, or another taking its place, moves none of them
    const geometry = () => page.evaluate((q) => {
      const box = (e) => { const r = e.getBoundingClientRect(); return [Math.round(r.top), Math.round(r.height)]; };
      return {
        pools: [...document.querySelectorAll('.work .pool')].map(box),
        rows: [...document.querySelectorAll(`.work .pool[data-agent="${q}"] .slot`)].map(box),
        sections: [...document.querySelectorAll('.work .np-section-title')].map((e) => Math.round(e.getBoundingClientRect().top)),
      };
    }, Q);

    // ---- who holds the instances
    check('work: one row per instance, busy or free', (await pool.locator('.slot').count()) === 2 && (await pool.locator('.slot.busy').count()) === 2);
    const names = await rowNames();
    check('work: a chat on an instance shows its title, not "main"; a subagent its name', names.includes(INDEX) && names.includes('surveyor') && !names.includes('main'), names.join(' | '));
    check('work: a subagent says whose work it is', (await pool.locator('.slot.busy', { hasText: 'surveyor' }).innerText()).includes(`for ${REFACTOR}`));
    const sections = page.locator('.work .np-section-label').evaluateAll((els) => els.map((e) => e.getAttribute('title')));
    const labels = await sections;
    check('work: Background and Finished sections, and no Agents, Runs, Processes, Usage today, Model capacity, Physical owners or Idea checks',
      ['Background', 'Finished'].every((t) => labels.includes(t)) && ['Agents', 'Runs', 'Processes', 'Usage today', 'Model capacity', 'Physical owners', 'Idea checks'].every((t) => !labels.includes(t)), labels.join(', '));

    // a foreground command shows on the row of the chat that runs it; the Background section lists only what outlives a call
    const openSection = async (title) => {
      const sec = page.locator('.work .np-section', { has: page.locator('.np-section-title', { hasText: title }) });
      const toggle = sec.locator('.np-section-toggle');
      if ((await toggle.getAttribute('aria-expanded')) !== 'true') await toggle.click(); // the sections are folded until opened
      return sec;
    };
    const background = await openSection('Background');
    await background.locator('.proc').first().waitFor({ timeout: 5000 }).catch(() => {});
    const indexRow = pool.locator('.slot.busy', { hasText: INDEX });
    check('work: a foreground command shows on the row of the chat that runs it, not in the Background list',
      (await indexRow.innerText()).includes('$ echo "indexing docs/"') && (await background.locator('.proc', { hasText: 'indexing docs' }).count()) === 0
        && (await background.locator('.proc', { hasText: 'dev server' }).count()) === 1,
      `${await indexRow.innerText()} | ${(await background.locator('.proc').allInnerTexts()).join(' | ')}`);

    // a chat whose turn ended while its subagent works is not done: the Work tab and its row in the sessions list say so
    const waitingChat = page.locator('.work .waiting-chat');
    await waitingChat.first().waitFor({ timeout: 5000 }).catch(() => {});
    check('work: a chat whose subagents still work is listed as waiting for them',
      (await waitingChat.count()) === 1 && (await waitingChat.innerText()).includes(REFACTOR) && /waiting for 1 agent\b/.test(await waitingChat.innerText()),
      (await waitingChat.allInnerTexts()).join(' | '));
    const refactorRow = page.locator(`.srow[data-session="${refactor}"]`);
    check('sessions: its row says it waits for its subagent, with the waiting dot',
      /waiting for 1 agent\b/.test(await refactorRow.locator('.waiting').innerText().catch(() => '')) && (await refactorRow.locator('.np-dot').first().getAttribute('data-status').catch(() => '')) === 'waiting',
      `${await refactorRow.count()} rows`);
    await shot(page, 'ui-work-01-waiting-chat');

    // the shared clock ticks: a running elapsed time moves on its own
    const elapsed = () => pool.locator('.np-elapsed').allInnerTexts();
    const e0 = await elapsed();
    await page.waitForTimeout(2300);
    const e1 = await elapsed();
    check('work: a running elapsed time ticks', e0.length > 0 && e0.join('|') !== e1.join('|'), `${e0.join(' ')} → ${e1.join(' ')}`);

    // an agent whose model is not loaded is listed, says why, and has a switch like the others
    const gemma = page.locator(`.work .pool[data-agent="${G}"]`);
    check('work: an agent whose model is not loaded is listed as such', (await gemma.locator('.st').innerText()) === 'not loaded' && /isn't loaded/.test(await gemma.locator('.second').innerText()), await gemma.innerText());
    await gemma.locator('input.np-switch').click();
    await until('the agent off', async () => (await gemma.locator('.st').innerText()) === 'off', 5000).catch(() => {});
    check('work: the switch takes an agent off (agents.setEnabled)', (await rpc('settings.get')).settings.agents?.[G]?.disabled === true && (await gemma.locator('.st').innerText()) === 'off');
    await gemma.locator('input.np-switch').click();
    await until('the agent on', async () => !(await rpc('settings.get')).settings.agents?.[G]?.disabled, 5000).catch(() => {});
    check('work: and back on', !(await rpc('settings.get')).settings.agents?.[G]?.disabled);

    // a busy row opens its chat: the subagent's
    await pool.locator('.slot.busy', { hasText: 'surveyor' }).click();
    await page.waitForFunction((id) => !!document.querySelector(`.topbar .tab.active[data-tab="${id}"]`), surveyorSid, { timeout: 5000 }).catch(() => {});
    check('work: a busy row opens its session', !!surveyorSid && (await page.locator(`.topbar .tab.active[data-tab="${surveyorSid}"]`).count()) === 1, String(surveyorSid));

    // ---- a run that waits for an instance: another chat asks while both are taken
    const review = await chat(REVIEW);
    await rpc('agent.send', { sessionId: review, text: `Check the backoff math. [s:say gate=${GC}]` });
    const waitLine = pool.locator('.waiting');
    await waitLine.waitFor({ timeout: 10_000 }).catch(() => {});
    check('work: the summary counts working, waiting and free', /2 working/.test(await summary()) && /1 waiting/.test(await summary()) && /0 free/.test(await summary()), await summary());
    check('work: the waiting runs are one line under the agent', (await waitLine.count()) === 1 && /1 waiting/.test(await waitLine.innerText()));
    const heightBefore = Math.round((await page.locator('.work').boundingBox()).height);
    await waitLine.click();
    const waitRow = page.locator('.work .pop .row', { hasText: REVIEW });
    await waitRow.waitFor({ timeout: 3000 }).catch(() => {});
    check('work: the waiting line opens who waits in a floating list, and the tab does not grow', (await waitRow.count()) === 1 && Math.round((await page.locator('.work').boundingBox()).height) === heightBefore);
    check('work: the gap before the longest wait is there', (await waitLine.locator('.longest').evaluate((e) => parseFloat(getComputedStyle(e).marginLeft))) >= 4);
    await shot(page, 'ui-work-02-waiting');
    await waitRow.locator('button[aria-label^="Cancel this run"]').click();
    await waitLine.waitFor({ state: 'detached', timeout: 5000 }).catch(() => {});
    check('work: an x on a waiting run cancels it (agent.abort): it never starts, and no one waits',
      (await waitLine.count()) === 0 && /no one waiting/.test(await pool.innerText()) && (await pool.locator('.slot.busy').count()) === 2 && (await status(review)) !== 'queued',
      `${await status(review)} | ${await pool.innerText()}`);

    // ---- a run that may go to either of two agents waits in nobody's queue: one line, always there, so it moves nothing
    const unassigned = page.locator('.work .unassigned');
    check('work: the line for runs waiting for any agent is there when no one waits', /no one waiting for any agent/.test(await unassigned.innerText()));
    await rpc('settings.set', { path: `agents.${Q2}`, value: { model: QWEN } });
    await page.locator(`.work .pool[data-agent="${Q2}"]`).waitFor({ timeout: 10_000 });
    await page.waitForTimeout(300);
    const geo0 = await geometry();
    const any = await chat('Waiter for any agent');
    await rpc('agent.send', { sessionId: any, text: 'Any agent will do. [s:say]' });
    await until('the run waiting for any agent', async () => /1 waiting for any agent/.test(await unassigned.innerText()), 10_000).catch(() => {});
    const geo1 = await geometry();
    check('work: a run waiting for any agent is one line, counted as waiting, and nothing moves',
      /1 waiting for any agent/.test(await unassigned.innerText()) && /1 waiting/.test(await summary()) && JSON.stringify(geo0) === JSON.stringify(geo1),
      `${await unassigned.innerText()} | ${await summary()} | ${JSON.stringify([geo0, geo1])}`);
    await rpc('agent.abort', { sessionId: any });
    await rpc('settings.set', { path: `agents.${Q2}`, value: null });
    await page.locator(`.work .pool[data-agent="${Q2}"]`).waitFor({ state: 'detached', timeout: 10_000 }).catch(() => {});

    // ---- fixed rows: a job ending, or a waiting run taking its place, moves nothing in the tab. The subagent's chat asks
    // again: its run waits for an instance, and the chat is no longer one that only waits for its subagent.
    await rpc('agent.send', { sessionId: refactor, text: `And the jitter? [s:say gate=${GB}]` });
    await waitLine.waitFor({ timeout: 10_000 }).catch(() => {});
    await waitingChat.first().waitFor({ state: 'detached', timeout: 5000 }).catch(() => {});
    check('work: …and not while its own run waits for an instance', (await waitingChat.count()) === 0 && /1 waiting/.test(await waitLine.innerText()));
    await page.waitForTimeout(300);
    const g0 = await geometry();
    const at = await rowNames();
    const surveyorRow = at.indexOf('surveyor');
    const indexRowAt = at.indexOf(INDEX);
    await gates.release(W); // the subagent reports: the waiting run takes its instance
    await gates.waiting(GB, 15_000).catch(() => {});
    await until('the waiting run in the subagent\'s row', async () => (await rowNames())[surveyorRow] === REFACTOR, 10_000).catch(() => {});
    await page.waitForTimeout(300);
    const g1 = await geometry();
    check('work: a waiting run takes the instance that ended, in the same row, and nothing moves',
      surveyorRow >= 0 && JSON.stringify(g0) === JSON.stringify(g1) && (await rowNames())[surveyorRow] === REFACTOR, JSON.stringify([at, await rowNames(), g0, g1]));
    check('work: the waiting line stays, saying no one waits', /no one waiting/.test(await pool.innerText()) && (await waitLine.count()) === 0);
    fs.writeFileSync(path.join(dir, FLAG), 'done\n'); // the foreground command ends, and its chat's run with it
    await waitIdle(index, 1, 30_000);
    await until('a free row', async () => (await rowNames())[indexRowAt] === 'free', 10_000).catch(() => {});
    await page.waitForTimeout(300);
    const g2 = await geometry();
    check('work: an instance that ended turns free in place, as tall as a busy one, and nothing moves',
      indexRowAt >= 0 && JSON.stringify(g0) === JSON.stringify(g2) && (await rowNames())[indexRowAt] === 'free', JSON.stringify([await rowNames(), g0, g2]));
    check('work: the summary and the agent say what is free', /1 working/.test(await summary()) && /1 free/.test(await summary()) && (await pool.locator('.st').innerText()) === '1 free', `${await summary()} | ${await pool.locator('.st').innerText()}`);
    await shot(page, 'ui-work-03-free');
    await gates.release(GB);
    await until('the chat to finish', async () => ['idle', 'completed'].includes(await status(refactor)), 30_000).catch(() => {});

    // ---- what the agent has done is on the agent (usage.summary for the period)
    const used = async () => ((await rpc('work.snapshot')).usage?.models ?? []).filter((m) => m.agent === Q).reduce((n, m) => n + (m.calls ?? 0), 0);
    await until('the calls on the agent', async () => {
      const n = await used();
      return n > 0 && (await pool.locator('.use').innerText()).startsWith(`${n} calls`);
    }, 10_000).catch(() => {});
    const useText = await pool.locator('.use').innerText();
    check('work: what the agent has done is on the agent: calls and tokens', useText.startsWith(`${await used()} calls · `) && /↑ .+↓/.test(useText), useText);

    // ---- the background command: its output, live; a collapse while the first tail is in flight leaves nothing polling
    const proc = background.locator('.proc', { hasText: 'dev server' }).first();
    await proc.locator('.row').click();
    await proc.locator('.out').waitFor({ timeout: 5000 }).catch(() => {});
    const len0 = (await proc.locator('.out').innerText().catch(() => '')).length;
    await page.waitForFunction((n) => (document.querySelector('.work .proc .out')?.innerText ?? '').length > n, len0, { timeout: 8000 }).catch(() => {});
    const len1 = (await proc.locator('.out').innerText().catch(() => '')).length;
    check('work: a background command shows its output, and new output as it comes', len0 > 0 && len1 > len0, `${len0} → ${len1} chars`);
    await shot(page, 'ui-work-04-process');
    await proc.locator('.row').click();
    const tails = () => socket.sent('processes.output').length;
    const undelay = socket.delay((f) => f.t === 'res' && f.m === 'processes.output', 1000);
    try {
      const base = tails();
      await proc.locator('.row').click(); // open: the tail is asked for, its answer 1 s away
      await socket.waitSent('processes.output', 0, 3000).catch(() => {});
      await page.waitForTimeout(200);
      await proc.locator('.row').click(); // folded again before the answer
      await page.waitForTimeout(1500); // the answer lands
      const calls0 = tails();
      await page.waitForTimeout(3000); // a poll left behind would ask again in here
      const calls1 = tails();
      check('work: folding a process while its output loads leaves no polling behind', calls0 - base >= 1 && calls1 === calls0, `${base} → ${calls0} → ${calls1} processes.output calls`);
    } finally {
      undelay();
    }
    const kill = proc.locator('button[aria-label="Kill process tree"]');
    await proc.hover(); // the row's actions show on hover
    await kill.click();
    await kill.click(); // a second click confirms
    await until('the process killed', async () => (await processes()).find((p) => p.id === bg.id)?.status === 'killed', 10_000).catch(() => {});
    check('work: kill a background command (processes.kill)', (await processes()).find((p) => p.id === bg.id)?.status === 'killed', (await processes()).find((p) => p.id === bg.id)?.status);

    // ---- the agent's name opens Settings → Agents with its dialog on top
    await pool.locator('button.key').click();
    const dialog = page.locator('.dialog', { has: page.getByRole('button', { name: 'Remove agent' }) });
    const nameInput = dialog.locator('input[aria-label="Name"]');
    await nameInput.waitFor({ timeout: 10_000 }).catch(() => {});
    check('work: the agent\'s name opens its dialog in Settings → Agents', (await nameInput.inputValue().catch(() => '')) === Q, await nameInput.inputValue().catch(() => '?'));
    await dialog.getByRole('button', { name: 'Done' }).click();
    await page.keyboard.press('Escape');
  } finally {
    // nothing left running or waiting: the gates, the file, the background command, the chats, the agents
    await gates.release(W).catch(() => {});
    await gates.release(GB).catch(() => {});
    await gates.release(GC).catch(() => {});
    try { fs.writeFileSync(path.join(dir, FLAG), 'done\n'); } catch {}
    if (bg) await rpc('processes.kill', { id: bg.id }).catch(() => {});
    for (const id of sessions) await rpc('agent.abort', { sessionId: id }).catch(() => {});
    for (const id of sessions) await waitIdle(id, 0, 15_000).catch(() => {});
    for (const id of sessions) await rpc('sessions.delete', { id }).catch(() => {});
    await rpc('projects.delete', { id: project.id }).catch(() => {});
    for (const a of [Q, G, Q2]) await rpc('settings.set', { path: `agents.${a}`, value: null }).catch(() => {});
  }
}, { tap: true, keepsSettings: true });
