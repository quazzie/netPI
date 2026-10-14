// The Ideas tab's word search and tags (plugins/NetPI.Ideas/ui/model.js), and the host's own copy of the tags helper
// (web/src/lib/ideas.js), which must read a tags box the same way.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { parseTags, matches, STATUSES, GROUP_ORDER, CLOSED_ORDER } from '../../plugins/NetPI.Ideas/ui/model.js';
import { parseTags as hostParseTags, IDEA_STATUSES } from '../src/lib/ideas.js';

test('parseTags: comma or space separated, # dropped, no duplicates (case-insensitive)', () => {
  assert.deepEqual(parseTags('ui, #docs  ui UI'), ['ui', 'docs']);
  assert.deepEqual(parseTags(''), []);
  assert.deepEqual(parseTags(null), []);
});

test('the host and the tab read tags and statuses alike', () => {
  for (const s of ['ui, #docs  ui', ' #a,#b ,, c ', 'X x']) assert.deepEqual(hostParseTags(s), parseTags(s), s);
  assert.deepEqual(IDEA_STATUSES, STATUSES);
});

test('every status has a group: the open ones in order, the closed ones behind', () => {
  assert.deepEqual([...GROUP_ORDER, ...CLOSED_ORDER].sort(), [...STATUSES].sort());
});

test('matches: every word somewhere in the idea', () => {
  const idea = { title: 'Faster builds', summary: 'cache the restore', tags: ['ci'], sections: [{ title: 'Plan', content: 'use a lock file' }] };
  assert.equal(matches(idea, ''), true);
  assert.equal(matches(idea, 'faster CACHE'), true);
  assert.equal(matches(idea, 'ci lock'), true, 'tags and sections count');
  assert.equal(matches(idea, 'faster rocket'), false, 'one missing word is no match');
  assert.equal(matches({ title: 'x' }, 'x'), true, 'an idea without tags or sections');
});
