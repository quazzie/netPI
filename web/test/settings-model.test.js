// The settings dialog's model (web/src/lib/settingsModel.js, settingsSearch.js, uiPrefs.js): where a section goes, how
// it is laid out, where a target opens, what the search finds and in which order, and the keys nobody declares.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import {
  PAGES,
  pagesOf,
  navOf,
  resolveTarget,
  sectionLayout,
  sectionInline,
  sectionOff,
  sectionState,
  helpParts,
  unknownKeys,
  cidrProblem,
  deferredList,
} from '../src/lib/settingsModel.js';
import { buildIndex, search, groupResults } from '../src/lib/settingsSearch.js';
import { UI_PREFS, PREF_SECTIONS, PREF_DEFAULTS, prefsFor, prefChanged } from '../src/lib/uiPrefs.js';

const st = (key, extra = {}) => ({ key, type: 'int', label: key, ...extra });
const SCHEMA = [
  { id: 'defaults', title: 'Defaults', group: 'General', settings: [st('workspace.default', { type: 'folder', label: 'Default working folder' })] },
  { id: 'provider-x', title: 'X', group: 'Models', providerType: 'x', settings: [st('apiKey')] },
  { id: 'retry', title: 'Retries', group: 'Models', settings: [st('retry.maxAttempts', { label: 'Attempts', subgroup: 'Attempts' }), st('retry.stallTimeoutSeconds', { label: 'Silence between tokens', subgroup: 'Timeouts', keywords: ['stall', 'hang'] }), st('retry.baseDelayMs', { label: 'First delay', subgroup: 'Backoff', advanced: true })] },
  { id: 'budget', title: 'Budget', group: 'Agents', settings: [st('budget.monthlyUsd', { type: 'number', label: 'Monthly budget' })] },
  { id: 'webhooks', title: 'Webhooks', group: 'Automation', settings: [st('webhooks.enabled', { type: 'bool', label: 'Webhook receiver', default: false }), st('webhooks.port', { label: 'Port' })] },
  { id: 'shell', title: 'Shell', group: 'Tools', settings: [st('shell.timeoutSeconds', { label: 'Default timeout' }), st('shell.hideEnv', { type: 'list', label: 'Hidden environment variables', keywords: ['secrets'] })] },
  { id: 'mystery', title: 'Mystery', group: 'Nowhere', settings: [st('mystery.x')] },
  { id: 'core', title: 'Host', group: 'Advanced', settings: [st('server.port', { label: 'Port', applies: 'restart', subgroup: 'Server', keywords: ['network'] })] },
];

test('pagesOf: a section goes to the page of its group; an unknown group to Other; a provider kind to none', () => {
  const l = pagesOf(SCHEMA);
  const of = (id) => l.sections.get(id)?.page.id;
  assert.equal(of('defaults'), 'general');
  assert.equal(of('retry'), 'models');
  assert.equal(of('budget'), 'agents', 'the budget is declared on Agents, no special case');
  assert.equal(of('webhooks'), 'automation');
  assert.equal(of('mystery'), 'other');
  assert.equal(of('core'), 'advanced');
  assert.equal(l.sections.has('provider-x'), false, 'Models offers it under Add provider');
  const ids = l.pages.map((p) => p.id);
  assert.ok(!ids.includes('knowledge'), 'a schema page without sections is left out');
  for (const own of ['general', 'agents', 'profiles', 'plugins', 'json', 'about']) assert.ok(ids.includes(own), own);
  assert.deepEqual(pagesOf([]).pages.find((p) => p.id === 'other'), undefined, 'Other only with sections in it');
});

test('navOf: pages under their headings, in order', () => {
  const nav = navOf(pagesOf(SCHEMA).pages);
  assert.deepEqual(nav.map((g) => g.heading), ['', 'AI', 'Capabilities', 'System']);
  assert.equal(nav[0].pages[0].id, 'general');
  assert.equal(new Set(PAGES.map((p) => p.id)).size, PAGES.length, 'page ids are unique');
});

test('resolveTarget: a key names its section and page; a page id alone; unknown parts are dropped', () => {
  const l = pagesOf(SCHEMA);
  assert.deepEqual(resolveTarget({ key: 'retry.stallTimeoutSeconds' }, l), { page: 'models', section: 'retry', key: 'retry.stallTimeoutSeconds', q: null });
  assert.deepEqual(resolveTarget('agents', l), { page: 'agents', section: null, key: null, q: null });
  assert.deepEqual(resolveTarget({ section: 'webhooks' }, l), { page: 'automation', section: 'webhooks', key: null, q: null });
  assert.deepEqual(resolveTarget({ page: 'nope', section: 'gone' }, l), { page: null, section: null, key: null, q: null });
  assert.equal(resolveTarget({ key: 'theme' }, l, ['theme']).page, 'general', 'a UI preference is on General');
  assert.equal(resolveTarget({ q: 'port' }, l).q, 'port');
});

test('sectionLayout: headings in order of appearance, plain ones first; the advanced ones in a block of their own', () => {
  const retry = SCHEMA.find((s) => s.id === 'retry');
  let l = sectionLayout(retry, {});
  assert.deepEqual(l.groups.map((g) => g.title), ['Attempts', 'Timeouts']);
  assert.deepEqual(l.advanced.groups.map((g) => g.title), ['Backoff'], 'grouped under their own headings');
  assert.equal(l.advanced.count, 1);
  assert.equal(l.advanced.set, 0);
  l = sectionLayout(retry, { retry: { baseDelayMs: 500 } });
  assert.deepEqual(l.groups.map((g) => g.title), ['Attempts', 'Timeouts'], 'a set advanced setting stays in the block');
  assert.equal(l.advanced.set, 1, 'and the block says so (it starts open)');
  const mixed = { settings: [st('a', { subgroup: 'H' }), st('b')] };
  assert.deepEqual(sectionLayout(mixed, {}).groups.map((g) => g.title), [null, 'H']);
  assert.equal(sectionLayout(mixed, {}).advanced.count, 0);
});

test('sectionInline: small plain sections inline, headings or more than three settings make a row', () => {
  assert.equal(sectionInline(SCHEMA.find((s) => s.id === 'shell')), true);
  assert.equal(sectionInline(SCHEMA.find((s) => s.id === 'retry')), false, 'headings');
  assert.equal(sectionInline({ settings: [st('a'), st('b'), st('c'), st('d')] }), false);
  assert.equal(sectionInline({ settings: [st('a', { type: 'text' }), st('b', { type: 'text' })] }), false, 'a text area counts twice');
  assert.equal(sectionInline({ settings: [st('a'), st('b'), st('c'), st('d', { advanced: true })] }), true, 'advanced ones do not count');
});

test('sectionOff and sectionState: the …enabled switch, off by its default too', () => {
  const wh = SCHEMA.find((s) => s.id === 'webhooks');
  assert.equal(sectionOff(wh, {}), true, 'default false');
  assert.equal(sectionOff(wh, { webhooks: { enabled: true } }), false);
  assert.deepEqual(sectionState(wh, { webhooks: { port: 9, enabled: true } }), { changed: 2, off: false });
  assert.equal(sectionOff(SCHEMA.find((s) => s.id === 'shell'), {}), false, 'no switch: never off');
  const ideas = { settings: [st('ideas.tellAgentOnCommit', { type: 'bool' }), st('ideas.autoRefine.enabled', { type: 'bool', default: false })] };
  assert.equal(sectionOff(ideas, {}), false, "a part's switch (ideas.autoRefine.enabled) is not the section's");
});

test('helpParts: the first sentence, the rest behind More; short texts stay whole', () => {
  assert.deepEqual(helpParts('Short help.'), { first: 'Short help.', rest: '' });
  const p = helpParts('The first sentence is long enough. And then there is quite a lot more to read here.');
  assert.equal(p.first, 'The first sentence is long enough.');
  assert.equal(p.rest, 'And then there is quite a lot more to read here.');
  assert.equal(helpParts('e.g. http://localhost:5173 for npm run dev.').rest, '', 'no split inside "e.g."');
  assert.deepEqual(helpParts(null), { first: '', rest: '' });
});

test('unknownKeys: undeclared leaves, not under a declared key or a dynamic root', () => {
  const doc = {
    retry: { maxAttempts: 3, maxAttempt: 4 },
    shell: { hideEnv: ['A'] },
    agents: { qwen: { model: 'x' }, queueMax: 5 },
    providers: { instances: { a: { type: 'x' } } },
    profiles: { coder: { name: 'C' } },
    ui: { anything: 1 },
    old: { thing: { deep: true } },
    empty: {},
  };
  assert.deepEqual(unknownKeys(doc, SCHEMA).map((u) => u.key).sort(), ['agents.queueMax', 'empty', 'old.thing.deep', 'retry.maxAttempt'], 'an agent is an entry, agents.queueMax a leftover');
});

test('cidrProblem: addresses and ranges of both families', () => {
  for (const ok of ['127.0.0.1', '127.0.0.1/32', '192.168.1.0/24', '::1', '::1/128', 'fe80::/10', '0.0.0.0/0']) assert.equal(cidrProblem(ok), null, ok);
  for (const bad of ['', 'localhost', '256.1.1.1', '10.0.0.0/33', '::1/129', '1.2.3.4/x', '1.2.3.4/8/9']) assert.ok(cidrProblem(bad), bad);
});

test('deferredList: "*" with the declared tools as exceptions, so later tools are deferred like the default', () => {
  assert.deepEqual(deferredList(['read', 'web_fetch', 'bash', 'tool_search'], new Set(['web_fetch'])), ['*', '!bash', '!read', '!tool_search']);
  assert.deepEqual(deferredList(['a'], new Set(['a'])), ['*']);
});

test('search: label words first, keywords and keys find it too, every word must match', () => {
  const layout = pagesOf(SCHEMA);
  const idx = buildIndex(layout, { prefs: UI_PREFS, prefSections: PREF_SECTIONS });
  const top = (q, opts) => search(idx, q, opts)[0]?.id;
  assert.equal(top('stall'), 'retry.stallTimeoutSeconds', 'a keyword');
  assert.equal(top('silence'), 'retry.stallTimeoutSeconds', 'a label word');
  assert.equal(top('server.port'), 'server.port', 'a key');
  assert.equal(top('theme'), 'pref:theme', 'a UI preference');
  assert.equal(top('webhooks'), 'section:webhooks', 'a section by its title');
  assert.equal(top('automation'), 'page:automation', 'a page by its title');
  assert.deepEqual(search(idx, 'port network').map((e) => e.id), ['server.port'], 'both words');
  assert.deepEqual(search(idx, 'zzz'), []);
  const ports = search(idx, 'port').filter((e) => e.kind === 'setting').map((e) => e.id);
  assert.deepEqual(ports.sort(), ['server.port', 'webhooks.port']);
  assert.ok(search(idx, 'secrets').some((e) => e.id === 'shell.hideEnv'));
});

test('search with a filter: Changed lists the set settings and changed preferences, no query needed', () => {
  const idx = buildIndex(pagesOf(SCHEMA), { prefs: UI_PREFS, prefSections: PREF_SECTIONS });
  const doc = { shell: { timeoutSeconds: 30 } };
  const prefs = { ...PREF_DEFAULTS, theme: 'light' };
  const changed = (e) => (e.kind === 'setting' ? doc.shell?.[e.key.split('.')[1]] !== undefined && e.key.startsWith('shell.') : e.kind === 'pref' && prefChanged(e.pref, prefs));
  assert.deepEqual(search(idx, '', { filter: changed }).map((e) => e.id), ['shell.timeoutSeconds', 'pref:theme']);
  const restart = (e) => e.setting?.applies === 'restart';
  assert.deepEqual(search(idx, '', { filter: restart }).map((e) => e.id), ['server.port']);
});

test('groupResults: links apart, settings by section in the order of their best match', () => {
  const idx = buildIndex(pagesOf(SCHEMA), { items: [{ kind: 'item', id: 'agent:qwen', page: 'agents', label: 'qwen', sub: 'port' }] });
  const { links, groups } = groupResults(search(idx, 'port'));
  assert.ok(links.some((e) => e.id === 'agent:qwen'), 'an item is a link');
  assert.ok(groups.every((g) => g.entries.every((e) => e.kind === 'setting')));
  assert.equal(new Set(groups.map((g) => g.key)).size, groups.length);
});

test('uiPrefs: every preference has a section, choices have options with their default among them', () => {
  for (const p of UI_PREFS) {
    assert.ok(PREF_SECTIONS.some((s) => s.id === p.section), p.key);
    if (p.type === 'choice') assert.ok(p.options.some(([v]) => v === p.default), `${p.key} default is an option`);
  }
  assert.equal(PREF_DEFAULTS.theme, 'dark');
  assert.ok(!prefsFor(false).some((p) => p.key === 'notifications'), 'the notification switch is the desktop app’s');
  assert.ok(prefsFor(true).some((p) => p.key === 'notifications'));
  assert.equal(prefChanged(UI_PREFS.find((p) => p.key === 'theme'), { theme: 'dark' }), false);
});

test('pickers: options name the sources, the standalone values and fixed ones; values round-trip as array or text', async () => {
  const { pickSpec, pickValue, pickStore } = await import('../src/lib/settingsModel.js');
  assert.deepEqual(pickSpec({ options: ['=default', '=none', 'Read', 'Grep'] }), { sources: [], fixed: ['default', 'none', 'Read', 'Grep'], exclusive: ['default', 'none'], max: 0 });
  assert.deepEqual(pickSpec({ options: ['@tools', '@plugins', 'rpc:embed.models'], max: 1 }).sources, ['@tools', '@plugins', 'rpc:embed.models']);
  const text = { type: 'pickText' };
  assert.deepEqual(pickValue(text, 'a, b ,c'), ['a', 'b', 'c']);
  assert.equal(pickStore(text, ['a', 'b']), 'a, b');
  assert.equal(pickStore(text, []), null, 'none chosen: the default applies');
  assert.deepEqual(pickStore({ type: 'pick' }, ['x']), ['x']);
  assert.deepEqual(pickValue({ type: 'pick' }, ['x', 'y']), ['x', 'y']);
  assert.deepEqual(pickValue({ type: 'pick' }, undefined), []);
});
