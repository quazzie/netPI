#!/usr/bin/env node
// The MCP tab against a REAL netpi-server (test "ui.mcp-tab", moved from the mock suite's section "plugin tab: MCP"). The
// servers are the E2E suite's MCP fixture in its "ui" mode (--fixture: NetPI.E2E.dll; four tools, one rejected) and a
// command that does not exist (a server that fails). Its narrow-panel and failed-save fixture page is ui.mcp-panel.
//
//   node tests/NetPI.E2E/ui/mcp-tab.mjs --url http://127.0.0.1:7470 --token e2e-token --fixture <NetPI.E2E.dll> --work <dir>
import { run, arg, check, shot, openApp, openStripTab, rpc, WORK, STAMP } from './lib.mjs';
import { configToForm, formToConfig, textToPairs, lines } from '../../../plugins/NetPI.Mcp/ui/config.js';

const FIXTURE = arg('fixture', '');
if (!FIXTURE) throw new Error('--fixture <NetPI.E2E.dll> is required');
const UI = `ui${STAMP}`;
const GONE = `gone${STAMP}`;

// the editor's model: a configuration to fields and back, with what the form does not know carried through
{
  const cfg = { enabled: true, transport: 'stdio', command: 'npx', args: ['-y', 'x'], cwd: 'C:\\w', env: { A: 'B' }, readOnly: ['status'], futureKey: { keep: 1 } };
  const back = formToConfig(configToForm(cfg), cfg);
  check('mcp editor: a configuration survives the form unchanged, unknown keys included', back.errors.length === 0 && JSON.stringify(back.config) === JSON.stringify({ futureKey: { keep: 1 }, enabled: true, transport: 'stdio', command: 'npx', args: ['-y', 'x'], cwd: 'C:\\w', env: { A: 'B' }, readOnly: ['status'] }), JSON.stringify(back));
  check('mcp editor: an http server shows as web and needs a url; a command server needs a command',
    configToForm({ url: 'http://x/y' }).transport === 'http' && formToConfig({ ...configToForm({}), transport: 'http' }, {}).errors.length === 1 && formToConfig(configToForm({}), {}).errors.length === 1);
  check('mcp editor: bad entries are named, numbers are checked, blank lists are left out',
    textToPairs('A=B\nnonsense\nC=').bad.length === 2 && formToConfig({ ...configToForm({ command: 'x' }), connectTimeoutMs: '12' }, {}).errors.length === 1
      && !('tools' in formToConfig(configToForm({ command: 'x' }), {}).config) && lines(' a \n\n b ').join() === 'a,b');
}

await run('mcp-tab', async ({ page }) => {
  await rpc('mcp.save', { id: UI, config: { command: 'dotnet', args: [FIXTURE, '--mcp-fixture', 'ui'], cwd: WORK, readOnly: ['status'] } });
  await rpc('mcp.save', { id: GONE, config: { command: `netpi-no-such-mcp-server-${STAMP}` } });
  try {
    await openApp(page);
    await openStripTab(page, 'right', 'MCP');
    const servers = page.locator('.mcp .server');
    const ui = servers.filter({ hasText: UI });
    const gone = servers.filter({ hasText: GONE });
    const text = async (loc) => (await loc.innerText().catch(() => '')).replace(/\s+/g, ' ');
    await page.waitForFunction(([a, b]) => {
      const rows = [...document.querySelectorAll('.mcp .server')].map((r) => r.textContent.replace(/\s+/g, ' '));
      return rows.some((t) => t.includes(a) && /connected/.test(t)) && rows.some((t) => t.includes(b) && /failed/.test(t));
    }, [UI, GONE], { timeout: 20_000 }).catch(() => {});
    check('mcp: the configured servers are listed with their state and tool count (mcp.list)', /connected · 3 tools/.test(await text(ui)), await text(ui));
    check('mcp: a failed server shows its error', /failed · 0 tools/.test(await text(gone)) && (await gone.locator('.err').count()) > 0, await text(gone));
    check('mcp: a tool the plugin rejected is named with the reason', /broken: Tool inputSchema must be a JSON object/.test(await text(ui)), await text(ui));
    check('mcp: every server starts collapsed, so no tool is listed', (await page.locator('.mcp details.tool').count()) === 0);
    await ui.locator('button.pick').click();
    await page.locator('.mcp details.tool').first().waitFor({ timeout: 5_000 }).catch(() => {});
    check('mcp: clicking a server lists its tools (mcp.tools)', (await page.locator('.mcp details.tool').count()) === 3, `${await page.locator('.mcp details.tool').count()} tools`);
    await ui.locator('button.pick').click();   // a second click closes it again
    await page.waitForFunction(() => document.querySelectorAll('.mcp details.tool').length === 0, null, { timeout: 5_000 });
    check('mcp: a second click on the open server collapses it', true);
    await ui.locator('button.pick').click();
    await page.locator('.mcp details.tool').first().waitFor({ timeout: 5_000 });
    const tool = (name) => page.locator('.mcp details.tool', { has: page.locator('summary', { hasText: new RegExp(`^\\s*${name}\\b`) }) });
    // a row is opened or closed, and the next step waits until it is (a click on a row that is still moving is lost)
    const toggle = async (name, open) => {
      await tool(name).locator('summary').click();
      await page.waitForFunction(([n, o]) => [...document.querySelectorAll('.mcp details.tool')]
        .some((d) => d.querySelector('summary')?.textContent.trim().startsWith(n) && d.open === o), [name, open], { timeout: 5_000 }).catch(() => {});
    };
    await toggle('act', true);
    check('mcp: a tool opens to its description, id, the three switches and its schema',
      (await tool('act').locator('.schema').isVisible()) && new RegExp(`mcp_${UI}_act_`).test(await text(tool('act'))) && (await tool('act').locator('.np-check input').count()) === 3,
      await text(tool('act')));
    await toggle('act', false);
    // read-only: the tool's own hint (lookup) or the server's readOnly list (status), and neither for act
    const readOnly = async (name) => {
      await toggle(name, true);
      const on = await tool(name).locator('.np-check', { hasText: 'read-only' }).locator('input').isChecked();
      await toggle(name, false);
      return on;
    };
    const ro = { lookup: await readOnly('lookup'), status: await readOnly('status'), act: await readOnly('act') };
    check('mcp: read-only follows the tool hint and the server list', ro.lookup && ro.status && !ro.act, JSON.stringify(ro));
    await page.locator('.mcp .np-search input').fill('stat');
    await page.waitForTimeout(200);
    check('mcp: the filter narrows the tools', (await page.locator('.mcp details.tool').count()) === 1 && (await tool('status').count()) === 1);
    await page.locator('.mcp .np-search input').fill('');
    await page.waitForTimeout(200);
    // switching a tool off writes the server's configuration (mcp.save with a tools list) and the list is read again
    await toggle('act', true);
    const expose = tool('act').locator('.np-check', { hasText: 'Expose tool' }).locator('input');
    await expose.click();
    let cfg = null;
    for (let i = 0; i < 50 && !Array.isArray(cfg?.tools); i++) {
      if (i) await page.waitForTimeout(100);
      cfg = (await rpc('mcp.list')).servers.find((s) => s.id === UI)?.config;
    }
    check('mcp: switching a tool off saves the server configuration (mcp.save) and the row follows',
      Array.isArray(cfg?.tools) && !cfg.tools.includes('act') && cfg.tools.includes('lookup') && !(await expose.isChecked()), JSON.stringify(cfg?.tools));
    // the editor is a dialog over the tab, with fields: Add server opens it, a command server is saved from them
    const dialog = page.locator('[role=dialog]');
    await page.locator('.mcp button', { hasText: 'Add server' }).click();
    await dialog.waitFor({ timeout: 5_000 });
    check('mcp: Add server opens a dialog with fields, not a JSON box', /Add MCP server/.test(await text(dialog)) && (await dialog.locator('input').count()) >= 4 && (await dialog.locator('textarea.json').count()) === 0, await text(dialog));
    await shot(page, 'ui-mcp-02-dialog');
    await dialog.locator('button', { hasText: 'Web (HTTP)' }).click();
    await dialog.locator('summary, button', { hasText: 'Limits and tool lists' }).click();
    check('mcp: the Web choice shows the url and headers instead of the command', (await dialog.locator('input[placeholder^="http"]').count()) === 1 && (await dialog.locator('input[placeholder^="npx"]').count()) === 0);
    await shot(page, 'ui-mcp-03-http');
    await dialog.locator('button', { hasText: 'Command (stdio)' }).click();
    await dialog.locator('button', { hasText: /^Save$/ }).click();
    await page.waitForFunction(() => /name must be/.test(document.querySelector('[role=dialog]')?.textContent ?? ''), null, { timeout: 5_000 });
    check('mcp: saving without a name says what is missing, and the dialog stays', (await dialog.count()) === 1);
    const NEW = `form${STAMP}`;
    await dialog.locator('input[placeholder="e.g. homeassistant"]').fill(NEW);
    await dialog.locator('button', { hasText: /^Save$/ }).click();
    await page.waitForFunction(() => /command is required/.test(document.querySelector('[role=dialog]')?.textContent ?? ''), null, { timeout: 5_000 });
    check('mcp: a command server without a command is refused in the dialog', true);
    await dialog.locator('input[placeholder^="npx"]').fill('dotnet');
    await dialog.locator('textarea[placeholder="one argument per line"]').fill(`${FIXTURE}\n--mcp-fixture\nui`);
    await dialog.locator('input[placeholder^="absolute path"]').fill(WORK);
    await dialog.locator('button', { hasText: /^Save$/ }).click();
    await dialog.waitFor({ state: 'detached', timeout: 10_000 });
    const saved = await rpc('mcp.config', { id: NEW });
    check('mcp: the fields are saved as the server configuration', saved?.config?.command === 'dotnet' && saved.config.args?.join('|') === `${FIXTURE}|--mcp-fixture|ui` && saved.config.cwd === WORK && saved.config.enabled === true, JSON.stringify(saved?.config));
    await rpc('mcp.remove', { id: NEW }).catch(() => {});
    // editing an existing server: the dialog is filled in, the id is fixed, JSON is one click away, Esc leaves
    await ui.locator('.head button[title^="Actions"]').click();
    await page.locator('[role=menuitem]', { hasText: 'Edit configuration' }).click();
    await dialog.waitFor({ timeout: 5_000 });
    check('mcp: editing shows the server filled in, with its name locked',
      (await dialog.locator('input[readonly]').inputValue()) === UI && (await dialog.locator('input[placeholder^="npx"]').inputValue()) === 'dotnet', await text(dialog));
    await dialog.locator('button', { hasText: 'Edit as JSON' }).click();
    check('mcp: Edit as JSON shows the whole configuration', /"command": "dotnet"/.test(await dialog.locator('textarea.json').inputValue()));
    await page.keyboard.press('Escape');
    await dialog.waitFor({ state: 'detached', timeout: 5_000 });
    check('mcp: Esc closes the dialog and the servers are still listed', (await ui.count()) === 1);
    await shot(page, 'ui-mcp-01-tab');
  } finally {
    await rpc('mcp.remove', { id: UI }).catch(() => {});
    await rpc('mcp.remove', { id: GONE }).catch(() => {});
  }
}, { viewport: { width: 1600, height: 1000 }, freshTabs: true, keepsSettings: true });
