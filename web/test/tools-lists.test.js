// A switched-off list (a profile's toolsOff) as the profile dialog reads and rewrites it (web/src/lib/tools.js).
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { listedTools, unlistedTools } from '../src/lib/tools.js';

const registered = new Set(['bash', 'ssh', 'read']);

test('listedTools: a name, or a merged action name of a tool that has actions', () => {
  assert.deepEqual([...listedTools(['Bash', 'ssh_run', 'gone'], registered)].sort(), ['bash', 'ssh']);
});

test('unlistedTools: only the entries that name no registered tool are left to keep', () => {
  assert.deepEqual(unlistedTools(['bash', 'ssh_run', 'mcp_x_search', 'gone'], registered), ['mcp_x_search', 'gone']);
  assert.deepEqual(unlistedTools(undefined, registered), []);
});
