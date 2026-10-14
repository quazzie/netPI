// The MCP server editor's form (plugins/NetPI.Mcp/ui/config.js): a configuration as fields and back, keys the form does
// not know carried through, and what the form refuses to save.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { configToForm, formToConfig, textToPairs, pairsToText } from '../../plugins/NetPI.Mcp/ui/config.js';

test('a stdio server round-trips, and an unknown key survives', () => {
  const cfg = {
    enabled: true, transport: 'stdio', command: 'npx', args: ['-y', 'server'], cwd: 'C:/x', env: { TOKEN: 'MY_TOKEN' },
    callTimeoutMs: 5000, tools: ['a', 'b'], readOnly: ['a'], futureKey: { keep: true },
  };
  const { config, errors } = formToConfig(configToForm(cfg), cfg);
  assert.deepEqual(errors, []);
  assert.deepEqual(config, cfg);
});

test('an http server round-trips with its headers', () => {
  const cfg = { enabled: false, transport: 'http', url: 'https://mcp.example/x', headerEnv: { Authorization: 'MCP_AUTH' } };
  const { config, errors } = formToConfig(configToForm(cfg), cfg);
  assert.deepEqual(errors, []);
  assert.deepEqual(config, cfg);
});

test('a configuration with a url and no command is an http one', () => {
  assert.equal(configToForm({ url: 'http://x' }).transport, 'http');
  assert.equal(configToForm({ url: 'http://x', command: 'y' }).transport, 'stdio');
  assert.equal(configToForm({}).enabled, true);
});

test('the form refuses what cannot be saved', () => {
  const stdio = { ...configToForm({}), transport: 'stdio', command: ' ', env: 'NOEQUALS', connectTimeoutMs: '50' };
  const e1 = formToConfig(stdio).errors;
  assert.ok(e1.some((e) => /command is required/.test(e)), e1.join(' | '));
  assert.ok(e1.some((e) => /Environment entries/.test(e)), e1.join(' | '));
  assert.ok(e1.some((e) => /Connect timeout/.test(e)), e1.join(' | '));
  const http = { ...configToForm({}), transport: 'http', url: 'ftp://x', headerEnv: 'A=' };
  const e2 = formToConfig(http).errors;
  assert.ok(e2.some((e) => /must start with http/.test(e)), e2.join(' | '));
  assert.ok(e2.some((e) => /Headers are/.test(e)), e2.join(' | '));
});

test('NAME=VALUE lines and back', () => {
  assert.deepEqual(textToPairs(' A = 1 \n\nB=2\nbad\n=x\ny='), { map: { A: '1', B: '2' }, bad: ['bad', '=x', 'y='] });
  assert.equal(pairsToText({ A: '1', B: '2' }), 'A=1\nB=2');
  assert.equal(pairsToText(null), '');
});
