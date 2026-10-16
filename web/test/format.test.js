// The chat's small formatters (web/src/lib/format.js, diff.js, turn.js): what a duration, a price, a token count, a path,
// an edit's diff and a turn's numbers read as.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { duration, usd, tokens, bytes, relPath, basename, truncate, recencyBucket, timeAgo, toMs, plural } from '../src/lib/format.js';
import { parseDiff, editsToDiff, normalizeEdits, editFiles } from '../src/lib/diff.js';
import { turnStats, cachedText, speedText } from '../src/lib/turn.js';

test('duration: never rounds up into the next unit', () => {
  assert.equal(duration(820), '820ms');
  assert.equal(duration(3_460), '3.4s');
  assert.equal(duration(59_600), '59s');
  assert.equal(duration(80_000), '1m 20s');
  assert.equal(duration(60_000 * 12 + 59_900), '12m 59s');
  assert.equal(duration(3_600_000 * 2 + 5 * 60_000), '2h 5m');
  assert.equal(duration(null), '');
  assert.equal(duration(Infinity), '');
});

test('usd, tokens and bytes', () => {
  assert.equal(usd(50), '$50');
  assert.equal(usd(12.4), '$12.40');
  assert.equal(usd(0.675), '$0.68');
  assert.equal(usd(0.0042), '$0.0042');
  assert.equal(usd(0), '$0');
  assert.equal(tokens(262_144), '262k');
  assert.equal(tokens(1_048_576), '1M');
  assert.equal(tokens(1234), '1.2k');
  assert.equal(tokens(2000), '2k');
  assert.equal(tokens(999), '999');
  assert.equal(bytes(512), '512 B');
  assert.equal(bytes(1536), '1.5 KB');
  assert.equal(bytes(5 * 1024 * 1024), '5.0 MB');
});

test('relPath and basename: shown relative to the project, with forward slashes', () => {
  assert.equal(relPath('C:\\repo\\src\\a.js', 'C:\\repo'), 'src/a.js');
  assert.equal(relPath('c:/Repo/src/a.js', 'C:/repo/'), 'src/a.js', 'the base is matched without case');
  assert.equal(relPath('C:/repo', 'C:/repo'), '.');
  assert.equal(relPath('C:/repository/a.js', 'C:/repo'), 'C:/repository/a.js', 'a prefix that is not a folder is no base');
  assert.equal(basename('C:\\repo\\a.js'), 'a.js');
  assert.equal(basename('/x/dir/'), 'dir');
  assert.equal(truncate('abcdef', 4), 'abc…');
  assert.equal(plural(1, 'chat'), '1 chat');
  assert.equal(plural(2, 'chat'), '2 chats');
});

test('recencyBucket and timeAgo', () => {
  const now = new Date(2026, 9, 14, 12, 0, 0);
  const at = (d, h = 12) => new Date(2026, 9, 14 - d, h).getTime();
  assert.equal(recencyBucket(at(0, 0), now), 'Today');
  assert.equal(recencyBucket(at(1), now), 'Yesterday');
  assert.equal(recencyBucket(at(5), now), 'Previous 7 days');
  assert.equal(recencyBucket(at(9), now), 'Earlier');
  assert.equal(timeAgo(now.getTime() - 10_000, now.getTime()), 'just now');
  assert.equal(timeAgo(now.getTime() - 5 * 60_000, now.getTime()), '5m ago');
  assert.equal(timeAgo(null), '');
  assert.equal(toMs('not a date'), 0);
});

test('parseDiff: rows with line numbers and the counts', () => {
  const { rows, added, removed } = parseDiff('--- a/x\n+++ b/x\n@@ -1,3 +1,3 @@\n keep\n-old\n+new\n tail\n');
  assert.deepEqual(rows.map((r) => r.t), ['file', 'file', 'hunk', 'ctx', 'del', 'add', 'ctx']);
  assert.equal(added, 1);
  assert.equal(removed, 1);
  assert.deepEqual(rows.find((r) => r.t === 'del'), { t: 'del', text: 'old', o: 2 });
  assert.deepEqual(rows.find((r) => r.t === 'add'), { t: 'add', text: 'new', n: 2 });
  assert.deepEqual(parseDiff(''), { rows: [], added: 0, removed: 0 });
});

test('parseDiff: a line in a hunk that looks like a header is content', () => {
  const { rows } = parseDiff('@@ -1,1 +1,1 @@\n---- not a header\n++++ neither\n');
  assert.deepEqual(rows.map((r) => r.t), ['hunk', 'del', 'add']);
});

test('the edit tool\'s arguments as a diff, in every spelling a model uses', () => {
  assert.deepEqual(normalizeEdits({ old_string: 'a', new_string: 'b' }), [{ oldText: 'a', newText: 'b', replaceAll: undefined }]);
  assert.deepEqual(normalizeEdits({ edits: '[{"oldText":"a","newText":"b","replace_all":true}]' }), [{ oldText: 'a', newText: 'b', replaceAll: true }]);
  assert.deepEqual(normalizeEdits(null), []);
  assert.equal(editsToDiff({ oldText: 'a\nb', newText: 'c' }), '@@ -1,2 +1,1 @@ edit 1\n-a\n-b\n+c');
  const files = editFiles({ files: [{ path: 'a.txt', oldText: 'x', newText: 'y' }, null] });
  assert.deepEqual(files.map((f) => f.path), ['a.txt']);
  assert.equal(editFiles({ oldText: 'x' }), null, 'the one-file form');
  assert.match(editsToDiff({ files: [{ path: 'a.txt', oldText: 'x', newText: 'y' }] }), /^--- a\/a\.txt\n\+\+\+ b\/a\.txt\n@@/);
});

test('turnStats: the prompt with its cache, and the speed after the first token', () => {
  const s = turnStats({ usage: { inputTokens: 100, cacheReadTokens: 900, outputTokens: 200 }, meta: { ttftMs: 500 }, durationMs: 2500 });
  assert.equal(s.prompt, 1000);
  assert.equal(s.cached, 900);
  assert.equal(cachedText(s), '90%');
  assert.equal(s.tps, 100);
  assert.equal(speedText(s.tps), '100');
  assert.equal(speedText(7.25), '7.3');
  assert.equal(turnStats({}), null);
  assert.equal(cachedText({ cachedPct: 0.999 }), '99%', 'a prompt not all reused never reads 100%');
  assert.equal(turnStats({ usage: { outputTokens: 5 }, meta: { ttftMs: 100 }, durationMs: 1000 }).tps, null, 'too few tokens for a speed');
});

test('firstLine: the first non-empty line, trimmed, cut with an ellipsis when asked', async () => {
  const { firstLine } = await import('../src/lib/format.js');
  assert.equal(firstLine('\n  \n  hello \nworld'), 'hello');
  assert.equal(firstLine('x'.repeat(10), 5), 'xxxx…');
  assert.equal(firstLine(null), '');
});
