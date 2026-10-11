#!/usr/bin/env node
// UI test: tool calls as a person reads them, on a real server — a run with the web tools ([s:web]: a plan, a search
// against MockLlm's SearXNG page, a fetched page, a screenshot in headless Edge/Chrome, an image shown in the chat) and
// the answer's links: a file link asks before the operating system opens anything (files.open is answered by the test,
// never sent), a web link opens a new window, a remote image stays a link, raw HTML and a forged copy button stay text.
//
// The ssh tool's cards come from real calls too: the E2E runner's exe is the client (--ssh), running the commands in Git
// Bash in a folder that stands for the host. And a Claude CLI model whose CLI is a script: it prints the tools the CLI
// ran itself and waits for a file before it ends the call, so the rows can be seen done while the call still runs.
//
//   node tests/NetPI.E2E/ui/tool-cards.mjs --url http://127.0.0.1:7470 --token e2e-token --mock http://127.0.0.1:7479 --work <dir> --ssh <NetPI.E2E.exe>
// Prints one JSON line at the end: { ok, checks: [{name, ok, detail}], errors: [...] }.
import fs from 'node:fs';
import path from 'node:path';
import { run, arg, check, shot, openApp, openSession, rpc, send, gates, newDir, waitIdle, MOCK, STAMP } from './lib.mjs';

const CLAUDE = 'anthropic/claude-haiku-4-5'; // the screenshot needs a model that reads images
const SSH = arg('ssh', ''); // the E2E runner's exe: it stands in for ssh and scp (tests/NetPI.E2E/FakeSsh.cs)

await run('tool-cards', async ({ page, socket }) => {
  const dir = newDir(`tools-${STAMP}`);
  const project = await rpc('projects.create', { name: `tools-${STAMP}`, path: dir });
  const before = (await rpc('settings.get')).settings;
  const sessions = [];
  const think = `think-${STAMP}`;
  const planGate = `plan-${STAMP}`;
  // files.open hands the file to the operating system: answered here, never sent
  const noOpen = socket.intercept((f) => f.t === 'rpc' && f.m === 'files.open', (f) => ({ t: 'res', id: f.id, r: true }));
  try {
    await rpc('settings.set', { path: 'web.search.searxngUrl', value: `${MOCK}/web/sx/` });
    await openApp(page);

    // ---- the web tools: thinking opened while it streams, the plan above the composer, then every card
    const title = `Why the demo breaks ${STAMP}`;
    const web = await rpc('sessions.create', { title, projectId: project.id, model: CLAUDE });
    sessions.push(web.id);
    // the chat is open before its answer streams: a chat opened mid-answer gets the stream only from there on
    await send(web.id, 'The demo page shows no count. [s:say text="Let me look."]');
    await openSession(page, title);
    await rpc('agent.send', { sessionId: web.id, text: `Why does the demo page break? Check the Svelte docs. [s:web base=${MOCK} gate=${think} plangate=${planGate}]` });
    await gates.waiting(think);
    await page.waitForSelector('.thinking.live .line', { timeout: 10_000 }).catch(() => {});
    await page.locator('.thinking.live .line').click();
    await page.waitForSelector('.thinking.live .body', { timeout: 3000 }).catch(() => {});
    check('streaming thinking opens while it streams', (await page.locator('.thinking.live .body').count()) === 1);
    await shot(page, 'ui-tools-01-live-thinking');
    await gates.release(think);

    await gates.waiting(planGate, 20_000); // the plan is written; the next answer waits
    await page.waitForSelector('.dock .strip', { timeout: 10_000 }).catch(() => {});
    await page.waitForTimeout(400);
    const gap = await page.locator('.scroller').evaluate((el) => el.scrollHeight - el.scrollTop - el.clientHeight);
    check('the chat stays at the bottom when the plan strip appears', gap < 48, `${Math.round(gap)}px from the bottom`);
    const bar = (await page.locator('.dock .strip .bar').innerText().catch(() => '')).replace(/\s+/g, ' ');
    check('the plan shows above the composer while the agent works', /\b0\/3\b/.test(bar), bar);
    await page.locator('.dock .strip .bar').click();
    await page.waitForTimeout(250);
    check('the plan opens as a checklist', (await page.locator('.dock .strip .todo li').count()) === 3);
    await shot(page, 'ui-tools-02-plan');
    await gates.release(planGate);

    await waitIdle(web.id, 2, 90_000);
    await page.waitForFunction(() => !document.querySelector('.composer.running'), null, { timeout: 15_000 }).catch(() => {});
    await page.waitForTimeout(400);
    check('the plan hides once every item is done', (await page.locator('.dock .strip').count()) === 0);
    check('finished thinking of an opened stream stays open', (await page.locator('.thinking.open .body').count()) >= 1);
    const shown = page.locator('.item[data-kind="shown"]');
    check('show_image puts the image in the chat, outside the steps', (await shown.locator('img').count()) === 1 && (await shown.innerText()).includes('The demo as it renders now'));
    await shown.locator('button.frame').click();
    await page.waitForSelector('.lb', { timeout: 3000 }).catch(() => {});
    check('clicking the shown image opens it full size', (await page.locator('.lb').count()) === 1);
    await page.keyboard.press('Escape');
    await page.waitForTimeout(200);
    if (await page.locator('.lb').count()) await page.locator('.lb').click();
    check('a tools notice says which tools are new', (await page.locator('.notice', { hasText: 'Tools changed' }).count()) >= 1, (await page.locator('.notice').allInnerTexts()).join(' | '));

    // open the folded steps and the web cards
    const closed = page.locator('.group.collapsible .head[aria-expanded="false"]');
    for (let n = 0; n < 12 && (await closed.count()); n++) await closed.first().click();
    await page.waitForTimeout(150);
    for (const label of ['Web search', 'Fetch', 'Screenshot']) {
      const row = page.locator('.tool .line[aria-expanded="false"]', { has: page.locator('.label', { hasText: label }) }).last();
      if (await row.count()) await row.click();
    }
    const todoRow = page.locator('.tool .line[aria-expanded="false"]', { has: page.locator('.label', { hasText: 'Todo' }) }).first();
    if (await todoRow.count()) await todoRow.click();
    await page.waitForTimeout(300);
    check('web_search lists its results', (await page.locator('.tool .hits .hit').count()) === 3, String(await page.locator('.tool .hits .hit').count()));
    check('web_fetch shows the page title and text', (await page.locator('.tool .page .title', { hasText: '$state' }).count()) === 1 && (await page.locator('.tool .page .box').first().innerText().catch(() => '')).includes('reactive state'));
    check('the screenshot shows the image and the console error', (await page.locator('.tool img.shot').count()) === 1 && (await page.locator('.tool .box', { hasText: 'count is undefined' }).count()) === 1,
      `${await page.locator('.tool img.shot').count()} images; ${(await page.locator('.tool', { has: page.locator('.label', { hasText: 'Screenshot' }) }).allInnerTexts()).join(' | ').slice(0, 300)}`);
    check('todo_write shows a checklist', (await page.locator('.tool .todo li').count()) === 3);
    const badges = await page.locator('.tool .np-badge').allInnerTexts();
    check('badges: results, console errors, progress', ['3 results', '1 console error', '0/3'].every((b) => badges.includes(b)), badges.join(' | '));
    await page.locator('.tool', { has: page.locator('.label', { hasText: 'Screenshot' }) }).last().scrollIntoViewIfNeeded().catch(() => {});
    await shot(page, 'ui-tools-03-web-cards');

    // ---- links in the answer: a file opens with the operating system, only after the user says so (the path came from
    // the model, and whatever program it is associated with will run)
    const links = page.locator('.md a.file-link');
    check('file links in the answer are marked', (await links.count()) === 2, String(await links.count()));
    const url = page.url();
    const opens = () => socket.sent('files.open');
    const openedBefore = opens().length;
    await links.first().click();
    const ask = page.locator('[role="dialog"][aria-label="Open this file?"]');
    await ask.waitFor({ timeout: 5000 }).catch(() => {});
    check('clicking a file link asks before opening it', (await ask.count()) === 1, String(await ask.count()));
    check('nothing is opened while the question is open', opens().length === openedBefore);
    await ask.locator('button', { hasText: /^Open$/ }).click();
    await socket.waitSent('files.open', 0, 5000).catch(() => {});
    const asked = opens().at(-1)?.msg?.p ?? opens().at(-1)?.msg?.params ?? {};
    check('a confirmed file link asks the host to open it', String(asked.path ?? '').replace(/\\/g, '/').endsWith('web/src/App.svelte'), JSON.stringify(asked));
    check('the confirmed link opens it as a deliberate pick (user: true)', asked.user === true, JSON.stringify(asked));
    check('the app does not navigate away', page.url() === url && (await page.locator('.composer textarea').count()) === 1);
    check('web links still open in a new window', (await page.locator('.md a[target="_blank"]', { hasText: 'docs' }).count()) === 1);

    // an answer is text the model may have copied off a page: its raw HTML stays text and its images stay links
    check('a remote image in an answer is a link, not a request', (await page.locator('.md img[src^="http"]').count()) === 0 && (await page.locator('.md a.md-image', { hasText: 'pixel' }).count()) === 1);
    check('raw HTML in an answer is shown, not rendered', (await page.locator('.md a[style], .md [style]').count()) === 0 && (await page.locator('.md', { hasText: 'position:fixed' }).count()) >= 1);
    check('a forged copy button in an answer is text', (await page.locator('.md .code-copy').count()) === 0 && (await page.locator('.md', { hasText: 'code-copy' }).count()) >= 1);

    // ---- the ssh tool: the host is a folder (home, beside the config), the client the E2E runner
    const sshDir = newDir(`ssh-${STAMP}`);
    const host = path.join(sshDir, 'home');
    fs.mkdirSync(path.join(host, 'demo'), { recursive: true });
    fs.writeFileSync(path.join(sshDir, 'config'), 'Host nuc\n  HostName 127.0.0.1\n  User demo\n');
    fs.writeFileSync(path.join(host, 'demo', 'nginx.conf'), 'server {\n  listen 80;\n  server_name demo.local;\n}\n');
    fs.writeFileSync(path.join(host, 'demo', 'error.log'), '2026/10/11 12:00:00 [error] connect() failed\n');
    await rpc('settings.set', { path: 'ssh', value: { ...(before.ssh ?? {}), path: SSH, scpPath: SSH, config: path.join(sshDir, 'config'), reuseConnections: false } });
    const sshTitle = `Demo site on nuc ${STAMP}`;
    const remote = await rpc('sessions.create', { title: sshTitle, projectId: project.id, model: CLAUDE });
    sessions.push(remote.id);
    await send(remote.id, 'Make the demo site on nuc listen on 8080. [s:ssh]', 60_000);
    await openSession(page, sshTitle);
    await page.waitForTimeout(300);
    for (let n = 0; n < 12 && (await closed.count()); n++) await closed.first().click();
    await page.waitForTimeout(150);
    for (const label of [/^SSH$/, /^SSH read$/, /^SSH edit$/, /^SCP$/]) {
      const row = page.locator('.tool .line[aria-expanded="false"]', { has: page.locator('.label', { hasText: label }) }).last();
      if (await row.count()) await row.click();
    }
    await page.waitForTimeout(300);
    const cards = async () => (await page.locator('.tool').allInnerTexts()).join(' | ').slice(0, 600);
    check('ssh run shows the host as the prompt, and the output', (await page.locator('.tool .shell .prompt', { hasText: 'nuc$' }).count()) === 1
      && (await page.locator('.tool .shell .out').last().innerText().catch(() => '')).includes('nginx: active'), await cards());
    check('ssh read shows the remote file', (await page.locator('.tool .read .path', { hasText: 'nuc:demo/nginx.conf' }).count()) === 1
      && (await page.locator('.tool .read .code').last().innerText().catch(() => '')).includes('server_name demo.local;'), await cards());
    check('ssh edit shows the diff', (await page.locator('.tool .diff .dl.add', { hasText: 'listen 8080;' }).count()) === 1 && (await page.locator('.tool .diff .dl.del', { hasText: 'listen 80;' }).count()) === 1, await cards());
    const summaries = await page.locator('.tool .summary').allInnerTexts();
    check('the summaries name the host', summaries.some((x) => x.startsWith('nuc · grep -c listen')) && summaries.filter((x) => x === 'nuc:demo/nginx.conf').length === 2 && summaries.includes('nuc:demo/error.log → logs/nuc-error.log'), summaries.join(' | '));
    const sshBadges = await page.locator('.tool .np-badge, .tool .diffbadge').allInnerTexts();
    check('badges: the exit code and the size of the diff', sshBadges.includes('exit 0') && sshBadges.some((b) => b.replace(/\s+/g, ' ') === '+1 −1'), sshBadges.join(' | '));
    check('remote files get no "Open file" button', (await page.locator('.tool .openfile').count()) === 0);
    check('the edit and the copy happened: the host\'s file changed, its log is here',
      fs.readFileSync(path.join(host, 'demo', 'nginx.conf'), 'utf8').includes('listen 8080;') && fs.existsSync(path.join(dir, 'logs', 'nuc-error.log')));
    await page.locator('.tool', { has: page.locator('.label', { hasText: /^SSH edit$/ }) }).last().scrollIntoViewIfNeeded().catch(() => {});
    await shot(page, 'ui-tools-04-ssh');

    // ---- tools the provider ran itself (the Claude CLI): each row is done, with its result, while the model call still
    // runs; the tool messages are stored only at the call's end, after the assistant message, and change nothing on screen
    const cli = newDir(`cli-${STAMP}`);
    const ev = (e) => JSON.stringify({ type: 'stream_event', event: e });
    const toolUse = (index, id, name, input) => [
      ev({ type: 'content_block_start', index, content_block: { type: 'tool_use', id, name, input: {} } }),
      ev({ type: 'content_block_delta', index, delta: { type: 'input_json_delta', partial_json: JSON.stringify(input) } }),
      ev({ type: 'content_block_stop', index }),
    ];
    const result = (id, content) => JSON.stringify({ type: 'user', message: { role: 'user', content: [{ type: 'tool_result', tool_use_id: id, content, is_error: false }] }, parent_tool_use_id: null });
    const answer = 'The tree has one change: notes.txt.';
    fs.writeFileSync(path.join(cli, 'before.jsonl'), [
      JSON.stringify({ type: 'system', subtype: 'init', apiKeySource: 'none', model: 'claude-sonnet-5-5', tools: [] }),
      ev({ type: 'message_start', message: { usage: { input_tokens: 3, output_tokens: 1 } } }),
      ...toolUse(0, 'tu_1', 'Bash', { command: 'git status --short' }),
      ...toolUse(1, 'tu_2', 'Read', { file_path: 'notes.txt' }),
      ev({ type: 'message_delta', delta: { stop_reason: 'tool_use' }, usage: { input_tokens: 3, output_tokens: 20 } }),
      result('tu_1', ' M notes.txt'),
      result('tu_2', 'alpha line'),
      ev({ type: 'message_start', message: { usage: { input_tokens: 5, output_tokens: 1 } } }),
      ev({ type: 'content_block_start', index: 0, content_block: { type: 'text', text: '' } }),
      ev({ type: 'content_block_delta', index: 0, delta: { type: 'text_delta', text: answer } }),
      ev({ type: 'content_block_stop', index: 0 }),
      ev({ type: 'message_delta', delta: { stop_reason: 'end_turn' }, usage: { input_tokens: 5, output_tokens: 9 } }),
    ].join('\n') + '\n');
    fs.writeFileSync(path.join(cli, 'after.jsonl'), JSON.stringify({ type: 'result', subtype: 'success', is_error: false, result: answer, usage: { input_tokens: 8, output_tokens: 29 } }) + '\n');
    // the chat's first call (the message that lists it) gets a plain answer; the next one the tools, and the wait. The
    // provider's own `claude auth status` (once, when it is switched on) is answered apart.
    fs.writeFileSync(path.join(cli, 'hello.jsonl'), [
      JSON.stringify({ type: 'system', subtype: 'init', apiKeySource: 'none', model: 'claude-sonnet-5-5', tools: [] }),
      JSON.stringify({ type: 'result', subtype: 'success', is_error: false, result: 'Hello.', usage: { input_tokens: 3, output_tokens: 2 } }),
    ].join('\n') + '\n');
    const exe = path.join(cli, process.platform === 'win32' ? 'claude.cmd' : 'claude');
    fs.writeFileSync(exe, process.platform === 'win32'
      ? '@echo off\r\nif "%~1"=="auth" exit /b 0\r\nif exist "%~dp0first" goto main\r\necho.>"%~dp0first"\r\ntype "%~dp0hello.jsonl"\r\nexit /b 0\r\n:main\r\ntype "%~dp0before.jsonl"\r\n:wait\r\nif exist "%~dp0release" goto done\r\nping -n 2 127.0.0.1 >nul\r\ngoto wait\r\n:done\r\ntype "%~dp0after.jsonl"\r\n'
      : '#!/bin/sh\n[ "$1" = auth ] && exit 0\nd=$(dirname "$0")\nif [ ! -e "$d/first" ]; then : > "$d/first"; cat "$d/hello.jsonl"; exit 0; fi\ncat "$d/before.jsonl"\nwhile [ ! -e "$d/release" ]; do sleep 0.5; done\ncat "$d/after.jsonl"\n');
    if (process.platform !== 'win32') fs.chmodSync(exe, 0o755);
    await rpc('settings.set', { path: 'providers.claudeCli', value: { ...(before.providers?.claudeCli ?? {}), enabled: true, executable: exe } });
    const cliTitle = `Look at the tree ${STAMP}`;
    const local = await rpc('sessions.create', { title: cliTitle, projectId: project.id, model: 'claude-cli/claude-sonnet-5-5' });
    sessions.push(local.id);
    await send(local.id, 'Hello.', 60_000); // a first message, so the chat is listed and open before the call
    await openSession(page, cliTitle);
    await rpc('agent.send', { sessionId: local.id, text: 'Look at the tree.' });
    const bashRow = page.locator('.tool', { has: page.locator('.summary', { hasText: 'git status --short' }) });
    await page.waitForFunction(() => [...document.querySelectorAll('.tool')].filter((t) => t.dataset.status === 'ok').length >= 2, null, { timeout: 20_000 }).catch(() => {});
    const stored = async () => ((await rpc('sessions.messages', { id: local.id })).messages ?? []).map((m) => m.role);
    const roles0 = await stored();
    const midRoles = roles0.slice(roles0.lastIndexOf('user') + 1);
    const streaming = (await page.locator('.composer.running').count()) > 0;
    check('provider tools: both rows are done while the model call still runs', streaming && (await page.locator('.tool[data-status="ok"]').count()) >= 2 && !midRoles.includes('assistant') && !midRoles.includes('tool'),
      `running ${streaming}; stored after the question: ${midRoles.join(',')}`);
    await bashRow.locator('.line').click();
    const body = await bashRow.locator('.body').innerText().catch(() => '');
    check('provider tools: the result is in the row before anything is stored', body.includes('notes.txt'), JSON.stringify(body.slice(0, 120)));
    await page.waitForTimeout(300);
    const snap = () => page.evaluate(() => [...document.querySelectorAll('.tool')].map((t) => ({ s: t.dataset.status, top: Math.round(t.getBoundingClientRect().top), h: Math.round(t.getBoundingClientRect().height), body: t.querySelector('.body')?.innerText ?? '' })));
    const mid = await snap();
    fs.writeFileSync(path.join(cli, 'release'), ''); // the CLI ends the call
    await waitIdle(local.id, 2, 30_000);
    await page.waitForFunction(() => !document.querySelector('.composer.running'), null, { timeout: 15_000 }).catch(() => {});
    await page.waitForTimeout(400);
    const end = await snap();
    const endRoles = await stored();
    check('provider tools: the tool messages are stored after the assistant message', endRoles.slice(-3).join(',') === 'assistant,tool,tool', endRoles.join(','));
    check('provider tools: the stored messages change nothing on screen', JSON.stringify(mid) === JSON.stringify(end), `${JSON.stringify(mid)} → ${JSON.stringify(end)}`);
    await shot(page, 'ui-tools-05-provider-tools');
  } finally {
    noOpen();
    await gates.release(think).catch(() => {});
    await gates.release(planGate).catch(() => {});
    for (const id of sessions) await rpc('agent.abort', { sessionId: id }).catch(() => {});
    for (const id of sessions) await rpc('sessions.delete', { id }).catch(() => {});
    await rpc('projects.delete', { id: project.id }).catch(() => {});
    await rpc('settings.set', { path: 'web', value: before.web ?? null }).catch(() => {});
    await rpc('settings.set', { path: 'ssh', value: before.ssh ?? null }).catch(() => {});
    await rpc('settings.set', { path: 'providers.claudeCli', value: before.providers?.claudeCli ?? null }).catch(() => {});
  }
}, { tap: true, keepsSettings: true });
