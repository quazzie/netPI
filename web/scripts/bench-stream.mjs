#!/usr/bin/env node
// What one streaming answer costs the browser: the whole accumulated markdown re-parsed, re-sanitized and put back into
// the DOM every 100 ms (what web/src/components/chat/AssistantText.svelte did), against rendering each finished block once
// and only the block being written again (web/src/lib/live.js → splitLive).
//
//   node web/scripts/bench-stream.mjs [--chars 60000] [--ticks 600] [--json]
//
// It serves the repo's own web/src/lib/markdown.js (marked + DOMPurify with the hooks the UI installs) and live.js to a
// headless browser, streams a long answer through both strategies tick by tick and reports where the time goes. The tail
// of the distribution is what a user feels: a tick that costs more than the 100 ms before the next one is dropped text.
import fs from 'node:fs/promises';
import http from 'node:http';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { loadPlaywright, launchBrowser } from '../mock/pw.mjs';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const argv = process.argv.slice(2);
const argVal = (n, d) => {
  const i = argv.indexOf(n);
  return i === -1 ? d : argv[i + 1];
};
const CHARS = Number(argVal('--chars', 60000));
const TICKS = Number(argVal('--ticks', 600));

// -------------------------------------------------------------------------------------------------- the answer
const PARA = [
  'The store keeps one document per session and the index fields the tools filter on, so a query is a range scan over the keys it declared rather than a table it does not need. Everything else — the compaction thresholds, the retry backoff — stays in the plugin that owns it, because a change there does not need a migration here.',
  'A reload is a swap, not a restart: the new version starts while the old one still serves, so a chat in the middle of a turn never loses a tool. What a swap does need is a lease per session, because both versions write to the same store for as long as the old one is still answering.',
  'Once the scheduler owns the choice, the remaining question is what happens to a run that was told to wait and then found itself cancelled: the queue has to answer with the id it was given, or the waiting session stays on a promise nobody will ever settle.',
  'The audit found three places where a poll had no in-flight guard, which is the same bug as a mount without a cleanup: the second one starts while the first is still running, and the slower of the two answers first.',
  'What the measurement has to cover is not the average tick but the worst one, because the text the user reads is the text that arrived before the render that was late.',
];
const CODE = ['```csharp', 'public async Task<int> Refresh(CancellationToken ct)', '{', '    await using var lease = await _leases.AcquireAsync(sessionId, ct);', '    var snap = await _rpc.CallAsync("work.snapshot", null, ct);', '    // a slow answer must not start a second one', '    return snap?.Agents?.Count ?? 0;', '}', '```'];
const TABLE = ['| what | where | why |', '| --- | --- | --- |', '| the lease | the store | one writer per session |', '| the poll | the tab | one in flight, ever |'];
const LIST = '- a session that lost its lease\n- a poll without a guard\n- a mount without a cleanup\n- a promise nobody settles';

function build(chars) {
  const parts = ['# Why the swap got slower\n'];
  for (let i = 0; parts.join('\n\n').length < chars; i++) {
    if (i % 7 === 3) parts.push('## ' + ['The lease', 'The poll', 'The queue', 'The ledger'][i % 4]);
    else if (i % 7 === 5) parts.push(CODE.join('\n'));
    else if (i % 11 === 9) parts.push(TABLE.join('\n'));
    else if (i % 5 === 4) parts.push(LIST);
    else parts.push(PARA[i % PARA.length]);
  }
  return parts.join('\n\n');
}

// Documents where a split could change what is rendered: a blank line inside a code fence or an indented block, a list or a
// quote that carries on after one, a table, a link definition, and one answer that is a single paragraph.
const TRICKY = [
  'a paragraph\n\n```js\nconst a = 1;\n\nconst b = 2;\n```\n\nand the text after it\n',
  'a paragraph\n\n```js\nconst a = 1;\n\nconst b = 2;\n```\n',
  'a paragraph\n\n    an indented block\n\n    still the same block\n\nafter it\n',
  'a paragraph\n\n- one\n\n- two\n\nafter the list\n',
  'a paragraph\n\n1. one\n\n2. two\n\nafter the list\n',
  'a paragraph\n\n> a quote\n\n> still the quote\n\nafter it\n',
  'a paragraph\n\n| a | b |\n| --- | --- |\n| 1 | 2 |\n\nafter the table\n',
  'a paragraph\n\n    - indented list item\n\n  - which continues the list above\n\nafter it\n',
  'a paragraph\n\n[ref]: https://example.com\n\nafter the definition\n',
  'a paragraph with [ref] in it\n\n[ref]: https://example.com\n\nand the text that follows\n',
  'a heading\n\n---\n\nand the text after the rule\n',
  'just one long paragraph with no blank line in it at all, which is what a stream that has not reached a second block looks like\n',
  'a paragraph\n\n<script>alert(1)</script>\n\nafter the html\n',
];

// -------------------------------------------------------------------------------------------------- the page
// Served as-is, with the two bare imports pointed at the files they resolve to.
const FILES = {
  '/markdown.js': ['web/src/lib/markdown.js', (s) => s.replace("'marked'", "'/marked.mjs'").replace("'dompurify'", "'/purify.mjs'")],
  '/live.js': ['web/src/lib/live.js', null],
  '/marked.mjs': ['node_modules/marked/lib/marked.esm.js', null],
  '/purify.mjs': ['node_modules/dompurify/dist/purify.es.mjs', null],
};

const PAGE = `<!doctype html><meta charset="utf-8"><body style="font:16px/1.6 sans-serif">
<script type="module">
import { renderMarkdown } from '/markdown.js';
import { splitLive } from '/live.js';

const CARET = (html) => html.replace(/<\\/p>\\s*$/, '<span class="caret"></span></p>');
const tpl = document.createElement('template');
/** The nodes one {html} produces, as they sit in the DOM: siblings, no wrapper (this is what Svelte's does). */
const nodes = (html) => {
  tpl.innerHTML = html;
  return [...tpl.content.childNodes];
};

/**
 * The split must not change what the answer renders as: split a document at splitLive() and the two halves must render
 * exactly like the whole document, and the cut must never move backwards as the text grows.
 */
const signature = (node) => {
  let out = '';
  for (const n of node.childNodes) {
    if (n.nodeType === 3) {
      const t = n.textContent.replace(/\s+/g, ' ');
      if (t.trim()) out += t; // the whitespace between two blocks is not rendered
    } else {
      out += '<' + n.tagName + [...n.attributes].map((a) => ' ' + a.name).join('') + '>' + signature(n) + '</' + n.tagName + '>';
    }
  }
  return out;
};
const box = document.createElement('div');

window.check = (docs) => {
  const bad = [];
  for (const doc of docs) {
    box.innerHTML = renderMarkdown(doc, { cache: false });
    const whole = signature(box);
    const cut = splitLive(doc);
    box.innerHTML = renderMarkdown(doc.slice(0, cut), { cache: false }) + renderMarkdown(doc.slice(cut), { cache: false });
    if (signature(box) !== whole) bad.push([doc.slice(0, 30), 'cut ' + cut]);
    let last = 0;
    for (let i = 1; i < doc.length; i++) {
      const at = splitLive(doc.slice(0, i));
      if (at < last) bad.push(['moved back at ' + i, last + ' -> ' + at]);
      last = at;
    }
  }
  return bad;
};

window.run = (doc, ticks) => {
  const step = Math.max(1, Math.floor(doc.length / ticks));
  const pieces = [];
  for (let i = 0; i < doc.length; i += step) pieces.push(doc.slice(i, i + step));
  const out = { whole: [], live: [], blocks: 0 };

  // what AssistantText did: the whole answer, every tick, into one node
  const a = document.createElement('div');
  a.className = 'md';
  document.body.append(a);
  let text = '';
  for (const piece of pieces) {
    text += piece;
    const t0 = performance.now();
    a.innerHTML = CARET(renderMarkdown(text, { cache: false }));
    a.getBoundingClientRect();
    out.whole.push(performance.now() - t0);
  }
  const whole = a.innerHTML;

  // what it does now: a finished block is rendered once and left alone; only the block being written is replaced
  const b = document.createElement('div');
  b.className = 'md';
  document.body.append(b);
  let tail = [];
  let cut = 0;
  let blocks = 0;
  text = '';
  for (const piece of pieces) {
    text += piece;
    const t0 = performance.now();
    const at = splitLive(text);
    if (at > cut) {
      for (const n of nodes(renderMarkdown(text.slice(cut, at), { cache: false }))) b.insertBefore(n, tail[0] ?? null);
      cut = at;
      blocks++;
    }
    for (const n of tail) n.remove();
    tail = nodes(CARET(renderMarkdown(text.slice(cut), { cache: false })));
    for (const n of tail) b.append(n);
    b.getBoundingClientRect();
    out.live.push(performance.now() - t0);
  }
  out.blocks = blocks;
  out.same = b.innerHTML.replace(/<span class="caret"><\\/span>/g, '') === whole.replace(/<span class="caret"><\\/span>/g, '');
  a.remove();
  b.remove();
  return out;
};
</script>`;

// -------------------------------------------------------------------------------------------------- run
const server = http.createServer(async (req, res) => {
  const [file, rewrite] = FILES[new URL(req.url, 'http://x').pathname] ?? [];
  if (!file) {
    res.writeHead(200, { 'content-type': 'text/html' });
    res.end(PAGE);
    return;
  }
  try {
    const src = await fs.readFile(path.join(root, file), 'utf8');
    res.writeHead(200, { 'content-type': 'text/javascript' });
    res.end(rewrite ? rewrite(src) : src);
  } catch (e) {
    res.writeHead(500);
    res.end(String(e));
  }
});
await new Promise((r) => server.listen(0, '127.0.0.1', r));

const pw = await loadPlaywright();
const browser = await launchBrowser(pw);
const page = await browser.newPage();
page.on('pageerror', (e) => console.error('  pageerror:', e.message));
await page.goto(`http://127.0.0.1:${server.address().port}/`);
const doc = build(CHARS);
const bad = await page.evaluate((d) => window.check(d), TRICKY);
const res = await page.evaluate(([d, t]) => window.run(d, t), [doc, TICKS]);
await browser.close();
server.close();

const stat = (xs) => {
  const s = [...xs].sort((a, b) => a - b);
  return {
    total: s.reduce((a, b) => a + b, 0),
    median: s[s.length >> 1],
    p95: s[Math.floor(s.length * 0.95)],
    max: s[s.length - 1],
  };
};
const w = stat(res.whole);
const l = stat(res.live);
const ms = (v) => `${v.toFixed(2).padStart(7)} ms`;
console.log(`${doc.length} chars streamed in ${res.whole.length} ticks, ${res.blocks} finished blocks`);
console.log(`whole   total ${w.total.toFixed(0).padStart(6)} ms   median ${ms(w.median)}   p95 ${ms(w.p95)}   max ${ms(w.max)}`);
console.log(`live    total ${l.total.toFixed(0).padStart(6)} ms   median ${ms(l.median)}   p95 ${ms(l.p95)}   max ${ms(l.max)}`);
console.log(`live: ${(100 - (l.total / w.total) * 100).toFixed(0)}% less in total, ${(100 - (l.p95 / w.p95) * 100).toFixed(0)}% less at p95`);
console.log(`rendered html identical: ${res.same}`);
if (bad.length) {
  console.log(`split changes the rendering of ${bad.length} case(s):`);
  for (const [why, where] of bad) console.log(`  ${where}  ${JSON.stringify(why)}`);
  process.exitCode = 1;
} else {
  console.log(`split checked on ${TRICKY.length} documents (fences, lists, tables, quotes, indented code, one long paragraph): rendering unchanged, cut only moves forwards`);
}
if (argv.includes('--json')) console.log(JSON.stringify({ whole: w, live: l, blocks: res.blocks, same: res.same, bad }, null, 2));