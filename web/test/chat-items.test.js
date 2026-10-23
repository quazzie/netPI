// The files a turn wrote or edited are listed once it ends (createItemBuilder, web/src/lib/chatItems.js). An edit of several
// files has no top-level path (`files: [{ path, edits }]`): each file in it is listed.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createItemBuilder } from '../src/lib/chatItems.js';

const turn = (calls) => {
  const messages = [
    { id: 'u1', seq: 1, role: 'user', parts: [{ type: 'text', text: 'go' }], createdAt: 1000 },
    {
      id: 'a1',
      seq: 2,
      role: 'assistant',
      parts: calls.map(([id, name, args]) => ({ type: 'tool_call', id, name, arguments: JSON.stringify(args) })),
      createdAt: 2000,
    },
    {
      id: 't1',
      seq: 3,
      role: 'tool',
      parts: calls.map(([id]) => ({ type: 'tool_result', callId: id, isError: false, content: 'ok' })),
      createdAt: 3000,
    },
  ];
  return createItemBuilder()(messages, [], true).find((i) => i.kind === 'artifacts')?.files ?? [];
};

test('an edit of one file lists its path', () => {
  const files = turn([['c1', 'edit', { path: 'src/a.js', edits: [] }]]);
  assert.deepEqual(files.map((f) => [f.path, f.how]), [['src/a.js', 'edited']]);
});

test('an edit of several files lists each of them', () => {
  const files = turn([['c1', 'edit', { files: [{ path: 'src/a.js', edits: [] }, { path: 'src/b.js', edits: [] }] }]]);
  assert.deepEqual(files.map((f) => [f.path, f.how]), [['src/a.js', 'edited'], ['src/b.js', 'edited']]);
});

test('a file written and then edited is listed once, as written', () => {
  const files = turn([
    ['c1', 'write', { path: 'src/a.js', content: 'x' }],
    ['c2', 'edit', { files: [{ path: 'src\\a.js', edits: [] }] }],
  ]);
  assert.deepEqual(files.map((f) => f.how), ['written']);
});
