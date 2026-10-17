// The taskbar badge counts (web/src/lib/taskbar.js).
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { taskbarCounts } from '../src/lib/taskbar.js';

test('taskbarCounts: working chats, not subagents, not asking or idle ones', () => {
  const sessions = [
    { status: 'running' },
    { status: 'queued' },
    { status: 'waiting' },
    { status: 'asking' },
    { status: 'idle' },
    { status: 'running', kind: 'subagent' },
  ];
  assert.deepEqual(taskbarCounts(sessions, 2), { working: 3, waiting: 2 });
  assert.deepEqual(taskbarCounts([], 0), { working: 0, waiting: 0 });
});
