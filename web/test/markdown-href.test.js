// Markdown link classification (isFileHref, web/src/lib/markdown.js): which hrefs name a local file the OS opens and
// which are web addresses that navigate. The image and link renderers (markdown.js:47, 53) turn file hrefs into the
// file links openFile.js opens; everything else navigates the app.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import vm from 'node:vm';

// the module's top level needs a window (DOMPurify), so evaluate it with fakes and take the pure function out
const source = fs
  .readFileSync(new URL('../src/lib/markdown.js', import.meta.url), 'utf8')
  .replace(/^import .*;\r?\n/gm, '')
  .replaceAll('export ', '');
const context = vm.createContext({ Marked: class { use() {} }, DOMPurify: { addHook() {} }, TextEncoder, console });
vm.runInContext(source + '\n;globalThis.__f = isFileHref;', context);
const isFileHref = context.__f;

test('isFileHref: web schemes and page-only hrefs are not files', () => {
  assert.equal(isFileHref('https://example.com/a'), false);
  assert.equal(isFileHref('http://example.com'), false);
  assert.equal(isFileHref('mailto:x@example.com'), false);
  assert.equal(isFileHref('data:image/png;base64,AAA='), false, 'a data: picture is not a file link');
  assert.equal(isFileHref('javascript:alert(1)'), false);
  assert.equal(isFileHref('#section'), false, 'an anchor stays in the page');
  assert.equal(isFileHref('?q=1'), false);
  assert.equal(isFileHref('//example.com/a'), false, 'protocol-relative is a web address');
  assert.equal(isFileHref(''), false, 'no href is no file');
});

test('isFileHref: Windows paths are files: absolute, percent-encoded and UNC', () => {
  assert.equal(isFileHref('C:\\Users\\me\\a.txt'), true);
  assert.equal(isFileHref('C:/Users/me/a.txt'), true);
  assert.equal(isFileHref('c:\\a.txt'), true, 'lowercase drive');
  assert.equal(isFileHref('C%3A%5CUsers%5Cme%5Ca.txt'), true, 'fully percent-encoded');
  assert.equal(isFileHref('C:%5CUsers%5Cme'), true, 'encoded separator after a raw drive');
  assert.equal(isFileHref('C:%5cUsers%5cme'), true, 'percent-encoding is case-insensitive');
  assert.equal(isFileHref('\\\\server\\share\\a.txt'), true, 'UNC');
});

test('isFileHref: file:// URLs are files', () => {
  assert.equal(isFileHref('file:///C:/Users/me/a.txt'), true);
  assert.equal(isFileHref('file:C:/a.txt'), true);
});

test('isFileHref: relative paths are files, fragments and queries included', () => {
  assert.equal(isFileHref('docs/a.md'), true);
  assert.equal(isFileHref('./a.txt'), true);
  assert.equal(isFileHref('../a.txt'), true);
  assert.equal(isFileHref('a.txt'), true);
  assert.equal(isFileHref('docs/a.md#frag'), true);
  assert.equal(isFileHref('docs/a.md?v=1'), true);
});
