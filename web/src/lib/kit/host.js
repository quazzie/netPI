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
