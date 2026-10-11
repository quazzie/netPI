#!/usr/bin/env node
// UI test: Settings → Agents & budget → agent row → "Remove agent" → confirm. The confirmed removal must
// remove the agent — from the list AND from the settings document (settings.get), not just hide the row —
// and the page must actually dispatch settings.set { path: agents.<id>, value: null } and get an answer.
//
// The agent is seeded by the C# test (ui.remove-agent) before this script runs.
//
//   node tests/NetPI.E2E/ui/remove-agent.mjs --url http://127.0.0.1:7470 --token e2e-token --agent e2e-rm-abc123
// Prints one JSON line at the end: { ok, checks: [{name, ok, detail}], errors: [...] }.
import { run, arg, check, shot, openApp, BASE, TOKEN } from './lib.mjs';

const AGENT = arg('agent', 'e2e-rm');

await run('remove-agent', async ({ page }) => {
  // Every frame the page sends/receives on its WebSocket, parsed when JSON: the evidence for what the
  // remove flow actually put on the wire (and whether the host answered).
  const sent = [];
  const received = [];
  const record = (list, payload) => {
    try {
      const m = JSON.parse(String(payload));
      if (m.t === 'rpc') list.push({ id: m.id, m: m.m, p: m.p ?? null });
      else if (m.t === 'res') list.push({ id: m.id, m: m.m, res: m.r ?? null, e: m.e ?? null });
    } catch {}
  };
  page.on('websocket', (ws) => {
    ws.on('framesent', (f) => record(sent, f.payload));
    ws.on('framereceived', (f) => record(received, f.payload));
  });

  await openApp(page);

  // Settings → Agents & budget. .agents scopes to the settings list: the Work tab renders [data-agent] pool rows too.
  await page.locator('button[title="Settings (Ctrl+,)"]').click();
  await page.locator('.nav button', { hasText: 'Agents & budget' }).click();
  const row = page.locator(`.agents [data-agent="${AGENT}"]`);
  await row.first().waitFor({ timeout: 15_000 });
  check('agent listed in the settings', true);

  // open the agent's dialog
  await row.first().click();
  const dialog = page.locator('.dialog', { has: page.getByRole('button', { name: 'Remove agent' }) });
  await dialog.getByRole('button', { name: 'Remove agent' }).waitFor({ timeout: 5_000 });
  check('agent dialog open', true);

  // "Remove agent" → confirm
  await dialog.getByRole('button', { name: 'Remove agent' }).click();
  const confirm = page.locator('.dialog', { has: page.getByRole('button', { name: 'Cancel' }) });
  await confirm.getByRole('button', { name: 'Remove', exact: true }).click();
  await confirm.waitFor({ state: 'detached', timeout: 5_000 }).catch(() => {});

  // wait for the host's answer (it must not need 30 s to time out) and for the row to go, instead of a fixed pause:
  // the list follows the settings.changed event, which the host sends after it answered
  const answered = () => {
    const f = sent.find((x) => x.m === 'settings.set' && x.p?.path === `agents.${AGENT}`);
    return !!f && received.some((r) => r.id === f.id);
  };
  for (const end = Date.now() + 10_000; !answered() && Date.now() < end; ) await page.waitForTimeout(50);
  await row.first().waitFor({ state: 'detached', timeout: 5_000 }).catch(() => {});

  const setFrames = sent.filter((f) => f.m === 'settings.set' && f.p?.path === `agents.${AGENT}`);
  check(
    'the page sends settings.set { value: null } for the agent',
    setFrames.length === 1 && (setFrames[0].p.value ?? null) === null,
    `sent=${JSON.stringify(setFrames)}`
  );
  if (setFrames.length === 1) {
    const reply = received.find((r) => r.id === setFrames[0].id);
    check('the host answers the settings.set', reply !== undefined, reply ? JSON.stringify(reply) : 'no reply frame');
  }

  const rowGone = (await row.count()) === 0;
  check('the agent row is gone from the list', rowGone);

  // the real thing: the settings document no longer has the agent (a hidden row is not a removal)
  const res = await fetch(`${BASE}/api/rpc/settings.get`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', 'X-NetPI-Token': TOKEN },
    body: '{}',
  });
  const doc = (await res.json())?.settings ?? {};
  const keys = Object.keys(doc.agents ?? {});
  check('settings.get: the agent is removed from the document', !keys.includes(AGENT), `agents: ${keys.join(', ')}`);

  // a failure surfaces as a toast, not silence
  const toastText = await page.locator('.toast').allInnerTexts().catch(() => []);
  check('no error toast', !toastText.some((t) => /failed|error/i.test(t)), toastText.join(' | '));

  await shot(page, 'ui-remove-agent-01-after');
});
