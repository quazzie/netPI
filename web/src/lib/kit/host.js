// Host services shared with plugin bundles. The host app sets globalThis.__netpiHost in main.js before
// anything mounts; plugin bundles (built with their own copy of the kit) find it at runtime, so they
// don't need to bundle marked/DOMPurify/highlight.js or the icon set.
const escapeHtml = (s) =>
  String(s ?? '').replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c]);

export function host() {
  return globalThis.__netpiHost ?? null;
}

export function iconSvg(name) {
  if (name && String(name).trimStart().startsWith('<')) return null;
  return host()?.icon?.(name) ?? '';
}

export function renderMarkdown(text) {
  const h = host();
  return h?.renderMarkdown ? h.renderMarkdown(text) : `<p style="white-space:pre-wrap">${escapeHtml(text)}</p>`;
}

/** Svelte action: lazy syntax highlighting of code blocks (no-op without the host). */
export function highlight(node, enabled) {
  const h = host();
  return h?.highlight ? h.highlight(node, enabled) : {};
}

/** The host's confirm dialog: confirm({ title, message, confirmLabel, danger }) → Promise<boolean>. */
export function confirm(opts = {}) {
  const h = host();
  if (h?.confirm) return h.confirm(opts);
  return Promise.resolve(window.confirm([opts.title, opts.message].filter(Boolean).join('\n\n')));
}

/** Copy text to the clipboard → Promise<boolean>. */
export function copyText(text) {
  const h = host();
  if (h?.copyText) return h.copyText(String(text ?? ''));
  return navigator.clipboard?.writeText(String(text ?? '')).then(
    () => true,
    () => false,
  ) ?? Promise.resolve(false);
}

/** Desktop shell (WebView2) integration. */
export const desktop = {
  get available() {
    return !!globalThis.chrome?.webview;
  },
  /** Show a file or folder in the OS file manager. */
  revealPath(path) {
    globalThis.chrome?.webview?.postMessage({ type: 'revealPath', path });
  },
  /** Open a URL in the default browser (links with target=_blank already do this). */
  openExternal(url) {
    if (globalThis.chrome?.webview) globalThis.chrome.webview.postMessage({ type: 'openExternal', url });
    else window.open(url, '_blank', 'noopener');
  },
};
