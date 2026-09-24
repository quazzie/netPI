// Markdown rendering: marked (GFM) → DOMPurify, memoized; code blocks get a header with a copy button.
// Syntax highlighting is lazy: highlight.js core + only the languages actually used are loaded, and
// blocks are highlighted when they scroll into view (never while a message is still streaming).
import { Marked } from 'marked';
import DOMPurify from 'dompurify';

const esc = (s) =>
  String(s).replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c]);

const COPY_SVG =
  '<svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect width="14" height="14" x="8" y="8" rx="2"/><path d="M4 16c-1.1 0-2-.9-2-2V4c0-1.1.9-2 2-2h10c1.1 0 2 .9 2 2"/></svg>';

/** A link target that names a local file (relative, absolute, file://) rather than a web address. */
export function isFileHref(href) {
  const h = String(href ?? '').trim();
  if (!h) return false;
  if (/^file:/i.test(h) || /^[a-zA-Z]:[\\/]/.test(h) || /^[a-zA-Z]:%5C/i.test(h)) return true;
  return !(/^[a-z][a-z0-9+.-]*:/i.test(h) || h.startsWith('#') || h.startsWith('//') || h.startsWith('?'));
}

const decode = (s) => {
  try {
    return decodeURIComponent(s);
  } catch {
    return s;
  }
};

const marked = new Marked({ gfm: true, breaks: false, async: false });
marked.use({
  renderer: {
    // links to files open with the OS (lib/openFile.js) instead of navigating the app
    link({ href, tokens }) {
      if (!isFileHref(href)) return false;
      const path = decode(href);
      return `<a href="#" class="file-link" data-path="${esc(path)}" title="Open ${esc(path)}">${this.parser.parseInline(tokens)}</a>`;
    },
    code({ text, lang }) {
      const l = (lang || '').trim().split(/\s+/)[0].toLowerCase();
      const cls = l ? ` class="language-${esc(l)}"` : '';
      const body = text.replace(/\n$/, '');
      return (
        `<div class="code-block"><div class="code-head"><span class="code-lang">${esc(l || 'text')}</span>` +
        `<button type="button" class="code-copy" data-copy="1">${COPY_SVG}<span>Copy</span></button></div>` +
        `<pre><code${cls}>${esc(body)}</code></pre></div>\n`
      );
    },
  },
});

DOMPurify.addHook('afterSanitizeAttributes', (node) => {
  if (node.tagName === 'A' && node.getAttribute('href')) {
    const href = node.getAttribute('href');
    if (/^https?:/i.test(href)) {
      node.setAttribute('target', '_blank');
      node.setAttribute('rel', 'noopener noreferrer');
    }
  }
});

const PURIFY = { ADD_ATTR: ['target'], FORBID_TAGS: ['style', 'form', 'iframe', 'object', 'embed'] };

const cache = new Map();
const CACHE_MAX = 600;

/** Render markdown to sanitized HTML. Results are memoized by source text unless cache=false. */
export function renderMarkdown(text, { cache: useCache = true } = {}) {
  if (!text) return '';
  if (useCache) {
    const hit = cache.get(text);
    if (hit !== undefined) {
      // refresh LRU position
      cache.delete(text);
      cache.set(text, hit);
      return hit;
    }
  }
  let html;
  try {
    html = DOMPurify.sanitize(marked.parse(text), PURIFY);
  } catch (e) {
    html = `<pre>${esc(text)}</pre>`;
  }
  if (useCache) {
    cache.set(text, html);
    if (cache.size > CACHE_MAX) cache.delete(cache.keys().next().value);
  }
  return html;
}

// ------------------------------------------------------------------------------------------ highlight

const LANGS = {
  javascript: () => import('highlight.js/lib/languages/javascript'),
  typescript: () => import('highlight.js/lib/languages/typescript'),
  json: () => import('highlight.js/lib/languages/json'),
  bash: () => import('highlight.js/lib/languages/bash'),
  csharp: () => import('highlight.js/lib/languages/csharp'),
  python: () => import('highlight.js/lib/languages/python'),
  xml: () => import('highlight.js/lib/languages/xml'),
  css: () => import('highlight.js/lib/languages/css'),
  diff: () => import('highlight.js/lib/languages/diff'),
  markdown: () => import('highlight.js/lib/languages/markdown'),
  yaml: () => import('highlight.js/lib/languages/yaml'),
  powershell: () => import('highlight.js/lib/languages/powershell'),
  sql: () => import('highlight.js/lib/languages/sql'),
  rust: () => import('highlight.js/lib/languages/rust'),
  go: () => import('highlight.js/lib/languages/go'),
  ini: () => import('highlight.js/lib/languages/ini'),
  dockerfile: () => import('highlight.js/lib/languages/dockerfile'),
  cpp: () => import('highlight.js/lib/languages/cpp'),
  java: () => import('highlight.js/lib/languages/java'),
};
const ALIASES = {
  js: 'javascript', jsx: 'javascript', mjs: 'javascript', cjs: 'javascript', javascript: 'javascript',
  ts: 'typescript', tsx: 'typescript', mts: 'typescript', typescript: 'typescript',
  json: 'json', jsonc: 'json', json5: 'json',
  bash: 'bash', sh: 'bash', shell: 'bash', zsh: 'bash', console: 'bash', shellsession: 'bash',
  cs: 'csharp', csharp: 'csharp', 'c#': 'csharp',
  py: 'python', python: 'python',
  html: 'xml', xml: 'xml', svelte: 'xml', vue: 'xml', svg: 'xml', xaml: 'xml', csproj: 'xml',
  css: 'css', scss: 'css', less: 'css',
  diff: 'diff', patch: 'diff',
  md: 'markdown', markdown: 'markdown',
  yaml: 'yaml', yml: 'yaml',
  ps: 'powershell', ps1: 'powershell', pwsh: 'powershell', powershell: 'powershell',
  sql: 'sql', rust: 'rust', rs: 'rust', go: 'go', golang: 'go',
  ini: 'ini', toml: 'ini', dockerfile: 'dockerfile', docker: 'dockerfile',
  c: 'cpp', h: 'cpp', cpp: 'cpp', 'c++': 'cpp', hpp: 'cpp', java: 'java',
};

let corePromise = null;
const loaded = new Map(); // canonical -> Promise

function getCore() {
  corePromise ??= import('highlight.js/lib/core').then((m) => m.default);
  return corePromise;
}

async function ensureLang(name) {
  const canonical = ALIASES[name];
  if (!canonical) return null;
  let p = loaded.get(canonical);
  if (!p) {
    p = Promise.all([getCore(), LANGS[canonical]()]).then(([hljs, mod]) => {
      if (!hljs.getLanguage(canonical)) hljs.registerLanguage(canonical, mod.default);
      return hljs;
    });
    loaded.set(canonical, p);
  }
  await p;
  return canonical;
}

const MAX_HL = 60_000;

export async function highlightCode(el) {
  if (el.dataset.hl) return;
  el.dataset.hl = '1';
  const m = /language-([\w#+-]+)/.exec(el.className);
  if (!m) return;
  const text = el.textContent;
  if (!text || text.length > MAX_HL) return;
  try {
    const lang = await ensureLang(m[1].toLowerCase());
    if (!lang || !el.isConnected) return;
    const hljs = await getCore();
    el.innerHTML = hljs.highlight(text, { language: lang, ignoreIllegals: true }).value;
    el.classList.add('hljs');
  } catch (e) {
    console.warn('highlight failed', e);
  }
}

/** Highlight a string (used by tool views). Returns escaped HTML. */
export async function highlightText(text, langName) {
  const lang = await ensureLang((langName || '').toLowerCase());
  if (!lang || !text || text.length > MAX_HL) return null;
  const hljs = await getCore();
  return hljs.highlight(text, { language: lang, ignoreIllegals: true }).value;
}

export function langFromPath(p) {
  const m = /\.([a-z0-9+#]+)$/i.exec(p || '');
  if (!m) return null;
  const ext = m[1].toLowerCase();
  return ALIASES[ext] ? ext : null;
}

let io = null;
function observer() {
  io ??= new IntersectionObserver(
    (entries) => {
      for (const e of entries) {
        if (!e.isIntersecting) continue;
        io.unobserve(e.target);
        highlightCode(e.target);
      }
    },
    { rootMargin: '400px 0px' },
  );
  return io;
}

/** Svelte action: highlight code blocks inside `node` as they become visible. Pass `false` to skip. */
export function highlight(node, enabled = true) {
  const scan = () => {
    if (!enabled) return;
    for (const el of node.querySelectorAll('pre code[class*="language-"]:not([data-hl])')) observer().observe(el);
  };
  scan();
  return {
    update(v) {
      enabled = v;
      scan();
    },
    destroy() {
      if (!io) return;
      for (const el of node.querySelectorAll('pre code')) io.unobserve(el);
    },
  };
}

// ------------------------------------------------------------------------------------------ clipboard

export async function copyText(text) {
  try {
    await navigator.clipboard.writeText(text);
    return true;
  } catch {
    try {
      const ta = document.createElement('textarea');
      ta.value = text;
      ta.style.cssText = 'position:fixed;left:-9999px;top:0';
      document.body.appendChild(ta);
      ta.select();
      const ok = document.execCommand('copy');
      ta.remove();
      return ok;
    } catch {
      return false;
    }
  }
}

let copyInstalled = false;
/** One delegated click handler for every rendered code block's copy button. */
export function installCodeCopy() {
  if (copyInstalled) return;
  copyInstalled = true;
  document.addEventListener('click', async (e) => {
    const btn = e.target.closest?.('.code-copy[data-copy]');
    if (!btn) return;
    const code = btn.closest('.code-block')?.querySelector('pre code');
    if (!code) return;
    if (await copyText(code.textContent)) {
      btn.classList.add('copied');
      const label = btn.querySelector('span');
      if (label) label.textContent = 'Copied';
      setTimeout(() => {
        btn.classList.remove('copied');
        if (label) label.textContent = 'Copy';
      }, 1200);
    }
  });
}
