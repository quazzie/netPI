// Where the finished blocks of a streaming answer end (splitLive, web/src/lib/live.js). A cut is only right where no open
// block crosses it, so each case is a text, the cut it must take (null: none yet) and why.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { splitLive } from '../src/lib/live.js';

const cases = [
  ['a paragraph, a blank line, the next paragraph begins (and is complete)', 'One.\n\nTwo.\n', 'One.\n\n'],
  ['the next paragraph is still being written: its line is not read yet', 'One.\n\nTwo is being wr', null],
  ['nothing is complete until a line ends', 'One paragraph that is still being wri', null],
  ['a list item keeps the list together', '1. a\n\n2. b\n\n3. c\n', null],
  ["an item's paragraph indented by its marker (three spaces) is the item's, not a new block", '1. **Step**\n\n   its paragraph\n\n2. next\n', null],
  ["a bullet item's paragraph indented two spaces too", '- a\n\n  more of a\n\n- b\n', null],
  ['a blockquote carries on over a blank line', '> a\n\n> b\n', null],
  ['a fence is not cut inside', 'Intro.\n\n```js\nlet a;\n\nlet b;\n', 'Intro.\n\n'],
  ['a closed fence and a paragraph after it', '```js\nlet a;\n```\n\nAfter.\n', '```js\nlet a;\n```\n\n'],
  ['an indented code block with a blank line inside', '    a\n\n    b\n', null],
  ['a heading, a paragraph, a third still being written', '# Title\n\nBody text.\n\nMore', '# Title\n\n'],
  ['a heading, a paragraph, a third complete', '# Title\n\nBody text.\n\nMore.\n', '# Title\n\nBody text.\n\n'],
];

for (const [what, text, want] of cases) {
  test(what, () => {
    const cut = splitLive(text);
    assert.equal(cut === 0 ? null : text.slice(0, cut), want);
  });
}

// the text grows a character at a time, as it streams: a cut taken once is still the cut (it never moves back), and the
// blocks it leaves behind are never changed by what follows
test('the cut never moves back as the text grows', () => {
  const answer = 'A.\n\nB.\n\n1. x\n\n   y\n\n2. z\n\nC is being written';
  let last = 0;
  for (let n = 1; n <= answer.length; n++) {
    const cut = splitLive(answer.slice(0, n));
    assert.ok(cut >= last, `${last} → ${cut} at ${n}`);
    last = cut;
  }
});
