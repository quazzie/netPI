#!/usr/bin/env node
// Journey: a person watches their agent's instances in the Work tab. An agent on qwen has two instances: two chats hold
// them (busy rows), a third waits for one (queued), and the person cancels it from the tab, so it never starts. When the
// first run ends its row is free and the other run stays in the row it had. Each run waits at a MockLlm gate, so the
// rows stay as they are until the journey lets them go. The agent is the journey's own and is removed again.
//
//   node tests/NetPI.E2E/ui/work.mjs --url http://127.0.0.1:7470 --token e2e-token --mock <url>
import { run, check, shot, expectEventually, openApp, showTab, rpc, gates, until, STAMP, MODEL, PROBE } from './lib.mjs';

const QWEN = 'aiproxy/qwen3.8-27b';
const AGENT = `wq-${STAMP}`;

await run('work', async ({ page }) => {
  const chats = [];
  const gate = (i) => `work-${STAMP}-${i}`;
  try {
    await rpc('settings.set', { path: `agents.${AGENT}`, value: { model: QWEN, use: 'the e2e Work tab journey' } });
    await until('the agent to be listed with its two instances', async () =>
      ((await rpc('agents.list')) ?? []).find((a) => a.key === AGENT)?.capacity === 2, 10_000);
    const start = async (i) => {
      const s = await rpc('sessions.create', { title: `Work ${i} ${STAMP}`, model: QWEN });
      await rpc('agents.use', { sessionId: s.id, agent: AGENT });
      chats.push(s.id);
      await rpc('agent.send', { sessionId: s.id, text: `Job ${i} [s:echo gate=${gate(i)}]` });
      return s.id;
    };
    const [one, two] = [await start(1), await start(2)];
    await gates.waiting(gate(1));
    await gates.waiting(gate(2));
    const three = await start(3);
    await until('the third run to wait for an instance', async () => (await rpc('agent.get', { sessionId: three }))?.status === 'queued', 10_000);

    await openApp(page);
    const tab = await showTab(page, 'netpi.work/work');
    const pool = tab.locator(`[data-agent="${AGENT}"]`);
    const rows = () => pool.getByTestId('slot').evaluateAll((els) => els.map((e) => (e.dataset.state === 'busy' ? e.dataset.session : 'free')));
    const before = await expectEventually('both instances are busy, one row each', rows,
      { pass: (r) => r.length === 2 && r.includes(one) && r.includes(two), timeout: 15_000, stop: true });
    await expectEventually('the third chat is shown queued for an instance', () => pool.getByTestId('queued').getAttribute('data-count', PROBE), { pass: (n) => n === '1' });
    await shot(page, 'ui-work-01-full');

    // cancel the queued run from the tab
    await pool.getByTestId('queued').click();
    const waiting = page.locator(`[role="menuitem"][data-session="${three}"]`);
    await expectEventually('the queued list names the waiting chat', () => waiting.count(), { pass: (n) => n === 1 });
    await shot(page, 'ui-work-02-queued');
    await waiting.getByTestId('cancel-run').click();
    await expectEventually('cancelled, it leaves the queue', () => pool.getByTestId('queued').count(), { pass: (n) => n === 0 });

    // the first run ends: its row is free, the other run keeps its row
    await gates.release(gate(1));
    await expectEventually('the first run\'s row is free and the other stays in its row', rows,
      { pass: (r) => r.length === 2 && r[before.indexOf(one)] === 'free' && r[before.indexOf(two)] === two, timeout: MODEL });
    await shot(page, 'ui-work-03-one-free');
    await gates.release(gate(2));
    await expectEventually('both instances are free again', rows, { pass: (r) => r.length === 2 && r.every((x) => x === 'free'), timeout: MODEL });
    // with both instances free, a run still waiting would have started by now
    const after = await rpc('agent.get', { sessionId: three });
    const answered = (await rpc('sessions.messages', { id: three })).messages.some((m) => m.role === 'assistant');
    check('the cancelled run never started, not even once an instance was free', !answered && after?.status !== 'queued' && after?.status !== 'running', `${after?.status}, answered: ${answered}`);
  } finally {
    for (let i = 1; i <= 3; i++) await gates.release(gate(i)).catch(() => {});
    for (const id of chats) await rpc('agent.abort', { sessionId: id }).catch(() => {});
    await rpc('settings.set', { path: `agents.${AGENT}`, value: null });
  }
}, { keepsSettings: true });
