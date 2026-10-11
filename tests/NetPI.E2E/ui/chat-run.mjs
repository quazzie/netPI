#!/usr/bin/env node
// A chat run as a person watches it, against a REAL netpi-server + MockLlm (test "ui.chat-run", moved from the mock suite's
// sections "new session + agent run", "showcase session", "abort", "images", "fast steps: layout stability", "compaction
// banner" and the /compact check of "settings"). The model's side is MockLlm's [s:watchrun], [s:spawnbg], [s:multiedit],
// [s:empty], [s:slow], [s:tools] and [s:long] on the small-context model; what has to be seen while it happens
// waits at MockLlm gates.
//
//   node tests/NetPI.E2E/ui/chat-run.mjs --url http://127.0.0.1:7470 --token e2e-token --mock <url> --work <dir>
import fs from 'node:fs';
import path from 'node:path';
import { execFileSync } from 'node:child_process';
import { run, check, shot, openApp, openSession, rpc, send, waitIdle, gates, newDir, STAMP } from './lib.mjs';

const git = (cwd, ...args) => execFileSync('git', args, { cwd, stdio: 'pipe', encoding: 'utf8' });
const dir = newDir(`run-${STAMP}`);
const put = (rel, text) => {
  fs.mkdirSync(path.dirname(path.join(dir, rel)), { recursive: true });
  fs.writeFileSync(path.join(dir, rel), text);
};
put('notes.txt', 'alpha line\nbeta line\ngamma line\n');
put('fast.txt', 'v0\n');
put('web/src/lib/rpc.svelte.js', 'const RECONNECT_MS = 1000;\nexport const reconnectMs = RECONNECT_MS;\n');
put('docs/PROTOCOL.md', '# Protocol\n\n## Reconnect\n\nThe client connects again.\n');
put('web/src/lib/config.js', 'export const RECONNECT_MS = 1000;\n');
git(dir, 'init', '-q', '-b', 'main');
git(dir, 'config', 'user.name', 'e2e');
git(dir, 'config', 'user.email', 'e2e@example.invalid');
git(dir, 'config', 'core.autocrlf', 'false');
git(dir, 'add', '-A');
git(dir, 'commit', '-q', '-m', 'first');
const project = await rpc('projects.create', { name: `run-${STAMP}`, path: dir });
// the showcase chat: a nudge, a subagent's report, an edit of two files and a shell command that changed one
const SHOW = `Showcase chat ${STAMP}`;
const show = await rpc('sessions.create', { title: SHOW, projectId: project.id });
await send(show.id, 'Fix the streaming reconnect. [s:empty]');
// a background worker: its report comes back as a notice, which wakes the chat for one more run
await send(show.id, 'Map the websocket handlers. [s:spawnbg delay=100]', 60_000);
for (let i = 0; i < 100; i++) {
  const msgs = (await rpc('sessions.messages', { id: show.id })).messages ?? [];
  const agent = await rpc('agent.get', { sessionId: show.id });
  if (msgs.some((m) => m.meta?.kind === 'agent-result') && ['idle', 'completed'].includes(agent?.status)) break;
  await new Promise((r) => setTimeout(r, 200));
}
await send(show.id, 'Change the client and the protocol. [s:multiedit]');
// the compaction banner's chat, on the model with the small context window
const TINY = `Tiny chat ${STAMP}`;
const tiny = await rpc('sessions.create', { title: TINY, projectId: project.id, model: 'aiproxy/tiny-ctx' });
await send(tiny.id, 'hi [s:say]');

await run('chat-run', async ({ page }) => {
  try {
    await openApp(page);
    const ta = page.locator('.composer textarea');
    const ring = page.locator('.composer .ring');
    const idle = (ms = 60_000) => page.waitForFunction(() => !document.querySelector('.composer.running'), null, { timeout: ms }).catch(() => {});
    const say = async (text, key = 'Enter') => {
      await ta.fill(text);
      await ta.press(key);
    };
    const newTab = async () => {
      const n = await page.locator('.topbar .tab').count();
      await page.keyboard.press('Control+t');
      await page.waitForFunction((m) => document.querySelectorAll('.topbar .tab').length > m, n, { timeout: 10_000 });
      await page.locator('.intro').waitFor({ timeout: 10_000 });
    };
    const attachProject = async () => {
      await page.locator('.composer button[aria-label="Project"]').click();
      await page.locator('.popover .item', { hasText: project.name }).first().click();
      await page.waitForFunction((n) => document.querySelector('.composer button[aria-label="Project"]')?.innerText.includes(n), project.name, { timeout: 5_000 }).catch(() => {});
    };

    // ---- new session + agent run: what a run shows while it works, and after
    await newTab();
    await attachProject();
    const W = `worker-${STAMP}`;
    await say(`Start a background check first. [s:spawnbg delay=100 workergate=${W} name=bg-check]`); // its report waits for the worker's gate; a
    // worker of another name than the showcase chat's (each gets a worktree of the project named after it)
    // the run ends at once: wait for it on the server, as the composer may not have shown it running yet
    await page.waitForFunction(() => document.querySelector('.topbar .tab.active')?.dataset.tab?.startsWith('ses_'), null, { timeout: 10_000 });
    await waitIdle(await page.locator('.topbar .tab.active').getAttribute('data-tab'), 1);
    const G = `run-${STAMP}`;
    await say(`Why does the agent scheduler throw when a pool is missing? Make it fail with a clear message. [s:watchrun lines=40 gate=${G}]`);
    await gates.waiting(G);
    const sid = await page.locator('.topbar .tab.active').getAttribute('data-tab');
    check('the run status line is above the composer', (await page.locator('.dock .run-status').count()) === 1 && (await page.locator('.messages .working, .content .working').count()) === 0,
      await page.locator('.dock .run-status').innerText().catch(() => ''));
    check('the system prompt row appears after the first message is sent', (await page.locator('.content:visible .item[data-kind="prompt"]').count()) === 1);
    const refused = await rpc('compaction.run', { sessionId: sid }).then(() => null, (e) => e);
    check('a manual compaction is refused while the agent runs', refused?.code === 'busy', refused ? `${refused.code}: ${refused.message}` : 'it was not refused');
    await ring.click();
    await page.locator('.popover .cx').waitFor({ timeout: 5_000 });
    const fresh = await page.locator('.popover .cx .row', { hasText: 'Compactions' }).locator('b').innerText();
    check('a fresh chat has compacted nothing', fresh === '0', fresh);
    const compactBtn = page.locator('.popover .cx .foot button', { hasText: 'Compact now' });
    const busyTip = (await compactBtn.getAttribute('title')) ?? '';
    check('Compact now is disabled while the agent runs', (await compactBtn.isDisabled()) && /wait for it to finish/.test(busyTip), busyTip);
    await shot(page, 'ui-run-01-busy');
    await page.keyboard.press('Escape');
    await gates.release(G);
    await page.locator('.tool[data-status="ok"]').first().waitFor({ timeout: 15_000 });
    // the third model call drops halfway on its first attempt: a retry banner, and its words are thrown away
    await page.locator('.banner').first().waitFor({ timeout: 10_000 }).catch(() => {});
    check('retry banner (agent.notice)', (await page.locator('.banner').count()) > 0);
    check('stream.reset drops the text streamed before the retry', !(await page.locator('.content:visible').innerText()).includes('Now I will build the solution'));
    // the shell command's output arrives while it runs
    await page.locator('.tool[data-status="running"]', { hasText: 'Bash' }).waitFor({ timeout: 20_000 }).catch(() => {});
    await page.locator('.tool .tail').first().waitFor({ timeout: 5_000 }).catch(() => {});
    await gates.release(W); // the worker reports while the command runs (40 lines, 10 s)
    await shot(page, 'ui-run-02-live-bash');
    check('live tool output tail', (await page.locator('.tool .tail').count()) > 0);
    // steer and queue while it runs
    await say('Also make Release() return early for unknown pools.');
    await say('Afterwards, write a one-line changelog entry.', 'Alt+Enter');
    await page.locator('.queue .chip').first().waitFor({ timeout: 5_000 }).catch(() => {});
    const chips = await page.locator('.queue .chip').count();
    check('queue chips while running', chips >= 1, `${chips} chip(s)`);
    // a subagent's report that arrives while the agent works waits as an internal input: never a chip you could remove
    let queued = [];
    for (let i = 0; i < 60 && !queued.some((q) => String(q.source ?? '').startsWith('agent:')); i++) {
      await page.waitForTimeout(100);
      queued = (await rpc('agent.queue', { sessionId: sid }).catch(() => [])) ?? [];
    }
    const shown = await page.locator('.queue .chip').allInnerTexts();
    check('an internal queued input is not shown as a chip', queued.some((q) => String(q.source ?? '').startsWith('agent:')) && !shown.some((t) => /agent-result|Report from/.test(t)),
      `queued: ${queued.map((q) => q.source).join(', ')}; chips: ${shown.join(' | ')}`);
    await idle(90_000);
    await page.waitForTimeout(600);
    await shot(page, 'ui-run-03-finished');
    check('retry banner cleared after run', (await page.locator('.banner').count()) === 0);
    check('the text discarded by stream.reset is in no message', !(await page.locator('.content:visible').innerText()).includes('Now I will build the solution'));
    const pinned = await page.locator('.scroller').evaluate((el) => el.scrollHeight - el.scrollTop - el.clientHeight);
    check('stayed pinned to bottom during run', pinned < 60, `${Math.round(pinned)}px from bottom`);
    check('finished steps group collapsed', (await page.locator('.content:visible .group.collapsible').count()) > 0);
    const closed = page.locator('.content:visible .group.collapsible .head[aria-expanded="false"]');
    for (let n = 0; n < 10 && (await closed.count()); n++) await closed.first().click();
    for (const label of ['Edit', 'Bash']) {
      const row = page.locator('.content:visible .tool .line', { hasText: label }).last();
      if (await row.count()) await row.click();
    }
    await page.waitForTimeout(300);
    await shot(page, 'ui-run-04-expanded');
    check('diff view rendered', (await page.locator('.content:visible .diff .dl.add').count()) > 0);
    const turnLines = await page.locator('.content:visible .group .turn').allInnerTexts();
    check('model turns: time to the first token and cache reuse under each turn', turnLines.length > 0 && turnLines.every((t) => /first token \d/.test(t) && /\d+% cached/.test(t)), turnLines[0]);
    const answerInfo = await page.locator('.content:visible .assistant .foot .info').last().innerText().catch(() => '');
    check('the answer footer has its turn numbers too', /first token \d/.test(answerInfo) && /\d+% cached/.test(answerInfo), answerInfo);

    // ---- showcase session: what the model got, for the prompt and for every notice; edits of several files; a shell
    // command's changed files
    await openSession(page, SHOW);
    const first = page.locator('.content:visible .item').first();
    await first.waitFor({ timeout: 10_000 });
    check('the system prompt row comes first', (await first.getAttribute('data-kind')) === 'prompt', await first.getAttribute('data-kind'));
    await first.locator('.pill').click();
    const raw = await first.locator('.sent .raw').innerText().catch(() => '');
    check('it opens to the prompt as sent', raw.startsWith('You are a coding agent'), raw.slice(0, 60));
    const toolRows = first.locator('.sent .tool');
    check('with the tools sent with it', (await toolRows.count()) > 5, `${await toolRows.count()} tools`);
    await toolRows.first().click();
    check('a tool opens to its definition', (await first.locator('.tjson').innerText().catch(() => '')).includes('"parameters"'));
    await shot(page, 'ui-run-05-prompt');
    await first.locator('.pill').click();
    const nudge = page.locator('.content:visible .item[data-kind="notice"]', { hasText: 'Nudge' }).first();
    await nudge.locator('.pill').click();
    const sent = await nudge.locator('.sent .raw').innerText().catch(() => '');
    check('a notice opens to what was sent', sent.startsWith('<system-notice kind="nudge">') && sent.trim().endsWith('</system-notice>'), sent.slice(0, 60));
    const report = page.locator('.content:visible .item[data-kind="notice"]', { hasText: 'bg-worker finished' }).first();
    await report.locator('.pill').click();
    check('a report opens rendered', (await report.locator('.sent .md').count()) === 1);
    await report.locator('.sent .tb', { hasText: 'As sent' }).click();
    check('…and as sent on request', (await report.locator('.sent .raw').innerText().catch(() => '')).startsWith('<system-notice kind="agent-result">'));
    await shot(page, 'ui-run-06-notice');
    for (let i = 0; i < 10; i++) {
      const folded = page.locator('.content:visible .group.collapsible:not(.open) .head');
      if (!(await folded.count())) break;
      await folded.first().click();
      await page.waitForTimeout(100);
    }
    const multi = page.locator('.content:visible .tool', { hasText: '2 files' }).first();
    check('an edit of several files names them on its row', (await multi.count()) === 1 && (await multi.locator('.line').innerText()).includes('rpc.svelte.js'), await multi.locator('.line').innerText().catch(() => 'no row'));
    await multi.locator('.line').click();
    await page.waitForTimeout(150);
    const heads = await multi.locator('.dl.file').allInnerTexts();
    check('…and its diff has a header per file', heads.length === 2 && heads[0].includes('rpc.svelte.js') && heads[1].includes('PROTOCOL.md'), heads.join(' | '));
    const sed = page.locator('.content:visible .tool', { has: page.locator('.line', { hasText: 'sed -i' }) }).first();
    await sed.locator('.line').click();
    await page.waitForTimeout(150);
    check('a shell command lists the files it changed', (await sed.locator('.changes .cf').allInnerTexts()).some((x) => x.includes('config.js')), (await sed.locator('.changes .cf').allInnerTexts()).join(', '));
    await sed.locator('.changes button', { hasText: 'Diff' }).click().catch(() => {});
    await sed.locator('.diff .dl.add').first().waitFor({ timeout: 5_000 }).catch(() => {});
    check('…and shows its diff on request', (await sed.locator('.diff .dl.add').innerText().catch(() => '')).includes('RECONNECT_MS = 250'));
    await shot(page, 'ui-run-07-changes');
    // a server slash command: /compact adds a summary
    await say('/compact');
    await page.waitForTimeout(150);
    if ((await ta.inputValue()).startsWith('/compact')) await ta.press('Enter');
    const summaryRow = page.locator('.content:visible .notice .label', { hasText: 'Summary' });
    await summaryRow.first().waitFor({ timeout: 20_000 }).catch(() => {});
    check('/compact (server rpc command) adds a summary', (await summaryRow.count()) > 0, (await page.locator('.np-toast').allInnerTexts().catch(() => [])).join(' | '));

    // ---- abort: Esc stops a run (in the project: the cleanup takes its chats)
    await newTab();
    await attachProject();
    await say('Why does the agent scheduler throw when a pool is missing? [s:slow ms=8000]');
    await page.locator('.composer.running').waitFor({ timeout: 5_000 });
    await page.locator('.thinking.live').waitFor({ timeout: 5_000 }).catch(() => {});
    await ta.press('Escape');
    await idle(10_000);
    const aborted = (await page.locator('.content:visible .status[data-reason="aborted"]').count()) + (await page.locator('.content:visible .notice', { hasText: 'aborted' }).count());
    check('Esc aborts the run', aborted > 0);
    await shot(page, 'ui-run-08-aborted');

    // ---- images: an attached picture is part of the message
    const png = await page.evaluate(() => {
      const c = document.createElement('canvas');
      c.width = 48;
      c.height = 32;
      const g = c.getContext('2d');
      g.fillStyle = '#7c93ff';
      g.fillRect(0, 0, 48, 32);
      return c.toDataURL('image/png').split(',')[1];
    });
    await page.locator('.composer input[type=file]').setInputFiles({ name: 'screenshot.png', mimeType: 'image/png', buffer: Buffer.from(png, 'base64') });
    await page.locator('.composer .thumb').waitFor({ timeout: 5_000 });
    await say('Quick question about this screenshot — short answer please. [s:echo]');
    await page.locator('.content:visible .user .img img').first().waitFor({ timeout: 10_000 }).catch(() => {});
    check('user message shows the image', (await page.locator('.content:visible .user .img img').count()) > 0);
    await idle();

    // ---- fast steps: the chat never jumps while the agent works
    await page.setViewportSize({ width: 1280, height: 560 });
    await newTab();
    await attachProject();
    const runFast = async (text) => {
      await ta.fill(text);
      await page.evaluate(() => {
        const users = document.querySelectorAll('.item[data-kind="user"]').length;
        const rec = (window.__rec = { frames: [], on: true });
        const baseline = (el) => {
          const p = document.createElement('span');
          p.style.cssText = 'display:inline-block;width:0;height:0';
          el.appendChild(p);
          const y = p.getBoundingClientRect().bottom;
          p.remove();
          return y;
        };
        (function tick() {
          const all = document.querySelectorAll('.item[data-kind="user"]');
          const u = all.length > users ? all[all.length - 1] : null;
          const lbl = document.querySelector('.group .latest .lbl');
          const sum = document.querySelector('.group .latest .sum');
          const cnt = lbl?.closest('.head')?.querySelector('.count');
          const off = lbl && sum && cnt ? Math.max(Math.abs(baseline(sum) - baseline(lbl)), Math.abs(baseline(lbl) - baseline(cnt))) : null;
          rec.frames.push({ top: u ? u.getBoundingClientRect().top : null, busy: !!document.querySelector('.composer.running'), latest: document.querySelector('.group .latest')?.innerText ?? null, off });
          if (rec.on) requestAnimationFrame(tick);
        })();
      });
      await ta.press('Enter');
      await page.locator('.composer.running').waitFor({ timeout: 5_000 }).catch(() => {});
      await idle();
      await page.waitForTimeout(500);
      return page.evaluate(() => {
        const f = window.__rec.frames;
        window.__rec.on = false;
        const downs = { run: [], end: [] };
        let busySeen = false;
        for (let i = 1; i < f.length; i++) {
          const a = f[i - 1];
          const b = f[i];
          if (b.busy) busySeen = true;
          if (a.top == null || b.top == null || b.top - a.top <= 1) continue;
          (busySeen && !b.busy ? downs.end : downs.run).push(Math.round(b.top - a.top));
        }
        const offs = f.map((x) => x.off).filter((x) => x != null);
        return { frames: f.length, downs, latest: [...new Set(f.map((x) => x.latest).filter(Boolean))], offFrames: offs.length, maxOff: offs.length ? Math.max(...offs) : null };
      });
    };
    await runFast('Fill the chat first. [s:tools file=fast.txt old=v0 new=v1]');
    const done = await runFast('Again, measured. [s:tools file=fast.txt old=v1 new=v2]');
    check('fast steps never push the chat down while the agent works', done.frames > 30 && done.downs.run.length === 0, `${done.frames} frames; down: ${done.downs.run.join(', ') || 'none'}`);
    await page.keyboard.press('Control+,');
    await page.locator('.dialog').first().waitFor();
    await page.locator('.np-seg button', { hasText: 'Folded' }).click();
    await page.keyboard.press('Escape');
    await page.waitForTimeout(200);
    const folded = await runFast('Folded from the start. [s:tools file=fast.txt old=v2 new=v3]');
    check('folded steps: nothing moves down, during the run or when it ends', folded.frames > 30 && folded.downs.run.length + folded.downs.end.length === 0,
      `run: ${folded.downs.run.join(', ') || 'none'}; end: ${folded.downs.end.join(', ') || 'none'}`);
    check('folded steps: the line shows the latest step while the agent works', folded.latest.some((l) => /Bash|Read|Edit|Grep|Find|List|Write|Thinking/.test(l)), folded.latest.slice(0, 4).join(' | '));
    check('folded steps: the step, its command and "N steps" share one baseline', folded.offFrames > 0 && folded.maxOff < 0.5, `${folded.offFrames} frames, off by up to ${folded.maxOff?.toFixed(2)}px`);
    const wrapped = await page.evaluate(() =>
      [...document.querySelectorAll('.group.collapsible .head')].filter((h) => [...h.querySelectorAll('.count, .took')].some((e) => e.getClientRects().length > 1 || e.offsetHeight > 20)).length);
    check('folded steps: "N steps · time" stays on one line', wrapped === 0, `${wrapped} wrapped`);
    check('folded steps: every group of this run is one line', (await page.locator('.content:visible .group .steps').count()) === 0 && (await page.locator('.content:visible .item[data-kind="steps"]').count()) > 0);
    await shot(page, 'ui-run-09-folded');
    await page.keyboard.press('Control+,');
    await page.locator('.dialog').first().waitFor();
    await page.locator('.np-seg button', { hasText: 'Fold when done' }).click();
    await page.keyboard.press('Escape');
    await page.setViewportSize({ width: 1600, height: 1000 });

    // ---- compaction banner: an automatic compaction mid-run keeps its banner while the summary is written, and after it
    // until the model answers again (the summary and that answer wait at gates)
    await openSession(page, TINY);
    const S = `sum-${STAMP}`;
    const A = `after-${STAMP}`;
    await say(`Keep going. [s:long n=2 lines=60 sumgate=${S} aftergate=${A}]`);
    const banners = async () => (await page.locator('.banner .banner-text').allInnerTexts()).join(' | ');
    await gates.waiting(S, 30_000).catch(() => {});
    await page.locator('.banner', { hasText: 'Compacting context' }).waitFor({ timeout: 5_000 }).catch(() => {});
    check('compaction banner: kept while the summary is written', (await banners()).includes('Compacting context'), (await banners()) || 'no banner');
    await gates.release(S);
    await gates.waiting(A, 30_000).catch(() => {});
    await page.locator('.banner', { hasText: 'Context compacted' }).waitFor({ timeout: 10_000 }).catch(() => {});
    await page.waitForTimeout(500);
    check('compaction banner: kept until the model answers again', (await banners()).includes('Context compacted'), (await banners()) || 'no banner');
    await shot(page, 'ui-run-10-compaction-banner');
    await gates.release(A);
    await page.waitForFunction(() => !(document.querySelector('.banner')?.textContent ?? '').includes('Context compacted'), null, { timeout: 15_000 }).catch(() => {});
    check('compaction banner: gone once the model answers', !(await banners()).includes('Context compacted'), await banners());
    await idle();
  } finally {
    const all = (await rpc('sessions.list', { includeArchived: true, includeSubagents: true, limit: 500 }).catch(() => [])) ?? [];
    for (const s of all) if (s.projectId === project.id || s.title?.includes(STAMP)) await rpc('sessions.delete', { id: s.id }).catch(() => {});
    await rpc('projects.delete', { id: project.id }).catch(() => {});
  }
}, { viewport: { width: 1600, height: 1000 }, freshTabs: true });
