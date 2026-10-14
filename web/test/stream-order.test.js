// withStream (web/src/lib/chatItems.js) lays the answer being streamed out like the finished message. A provider that
// runs its own loop (the Claude CLI) streams thinking, text, a tool call, more text, another tool call… in one model
// call; joined, the text read as one wall of paragraphs with every tool below it until the message replaced it
// (idea-xg7ayy). Each case is a stream's segments and the items it must become, in order.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { withStream } from '../src/lib/chatItems.js';
import { flushNow } from '../src/lib/frame.js';

const timing = (ended) => ({ thinkingStartedAt: 1000, thinkingEndedAt: ended ? 2000 : 0 });
const stream = (segments, ended = false) => ({
  active: true,
  ended,
  startedAt: 1000,
  segments,
  text: segments.filter((s) => s.kind === 'text').map((s) => s.text).join(''),
  thinking: segments.filter((s) => s.kind === 'thinking').map((s) => s.text).join(''),
  tools: segments.filter((s) => s.kind === 'tool'),
});
// an item as a short label: text "Looking…", steps [thinking, tool Read], ask
const shape = (items) =>
  items.map((i) =>
    i.kind === 'steps'
      ? `steps[${i.steps.map((s) => (s.kind === 'thinking' ? 'thinking' : `tool ${s.call.name}`)).join(', ')}]`
      : i.kind === 'text'
        ? `text${i.done ? '' : '*'} ${JSON.stringify(i.text)}`
        : i.kind,
  );

const cases = [
  [
    'one call of an ordinary model: thinking, then the answer',
    [{ kind: 'thinking', text: 'hmm', timing: timing(true) }, { kind: 'text', text: 'Answer.' }],
    ['steps[thinking]', 'text* "Answer."'],
  ],
  [
    'the Claude CLI: text, tool, text, tool, text, in the order they came (* marks the text still being written)',
    [
      { kind: 'text', text: 'I will look.' },
      { kind: 'tool', callId: 'c1', name: 'Read' },
      { kind: 'text', text: 'Found it. Fixing.' },
      { kind: 'tool', callId: 'c2', name: 'Edit' },
      { kind: 'text', text: 'Done.' },
    ],
    ['text "I will look."', 'steps[tool Read]', 'text "Found it. Fixing."', 'steps[tool Edit]', 'text* "Done."'],
  ],
  [
    'thinking between tools joins their steps group; a run of tools stays one group',
    [
      { kind: 'tool', callId: 'c1', name: 'Grep' },
      { kind: 'thinking', text: 'which one', timing: timing(true) },
      { kind: 'tool', callId: 'c2', name: 'Read' },
      { kind: 'text', text: 'So.' },
    ],
    ['steps[tool Grep, thinking, tool Read]', 'text* "So."'],
  ],
  [
    'text that is only whitespace between two tools adds no item',
    [{ kind: 'tool', callId: 'c1', name: 'Read' }, { kind: 'text', text: '\n\n' }, { kind: 'tool', callId: 'c2', name: 'Read' }],
    ['steps[tool Read, tool Read]'],
  ],
  [
    'ask_user is its card between the steps around it',
    [{ kind: 'tool', callId: 'c1', name: 'Read' }, { kind: 'tool', callId: 'c2', name: 'ask_user' }],
    ['steps[tool Read]', 'ask'],
  ],
];

for (const [what, segments, want] of cases) {
  test(what, () => assert.deepEqual(shape(withStream([], stream(segments))), want));
}

test('each thinking run carries its own timing and key', () => {
  const a = { kind: 'thinking', text: 'first', timing: timing(true) };
  const b = { kind: 'thinking', text: 'second', timing: timing(false) };
  const steps = withStream([], stream([a, { kind: 'tool', callId: 'c1', name: 'Read' }, b]))[0]?.steps ?? [];
  assert.equal(steps[0]?.stream, a.timing);
  assert.equal(steps[2]?.stream, b.timing);
  assert.notEqual(steps[0]?.key, steps[2]?.key);
});

test('the keys do not move while the last text grows', () => {
  const keys = (text) => withStream([], stream([{ kind: 'text', text: 'One.' }, { kind: 'tool', callId: 'c1', name: 'Read' }, { kind: 'text', text }]))
    .map((i) => i.key).join(',');
  assert.equal(keys('Two'), keys('Two more'));
});

test("the stream's first steps join the open group of the finished list", () => {
  const open = { kind: 'steps', key: 'g1', steps: [{ kind: 'tool', key: 'cX', call: { name: 'Bash' } }] };
  const items = withStream([open], stream([{ kind: 'tool', callId: 'c1', name: 'Read' }]));
  assert.equal(items.length, 1);
  assert.equal(items[0].steps.length, 2);
});

// StreamState.tool() pushes its segment and flushes at once (flushNow). The Claude CLI often calls a tool before any
// text: no delta has queued the frame flush then, and a flush that ran only when queued left the live tool row out of
// the chat until the run's first text, at its end.
test('flushNow runs the flush even when no frame was queued for it', () => {
  let ran = 0;
  flushNow(() => ran++);
  assert.equal(ran, 1);
});
